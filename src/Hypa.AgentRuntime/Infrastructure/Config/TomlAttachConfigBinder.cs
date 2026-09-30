using System.Globalization;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.Theme;
using static Hypa.AgentRuntime.Infrastructure.Config.TomlSubsetParser;

namespace Hypa.AgentRuntime.Infrastructure.Config;

/// <summary>Binds attach TOML through the subset parser. No ToModel. No reflection.</summary>
public static class TomlAttachConfigBinder
{
    private static readonly HashSet<string> KnownTables = new(StringComparer.Ordinal)
    {
        "theme",
        "theme.custom",
        "terminal",
        "keys",
        "keys.indexed",
        "ui",
        "ui.chrome",
        "ui.sidebar",
        "ui.sidebar.agents",
        "ui.sidebar.agents.rows_by_agent",
        "ui.sidebar.spaces",
        "ui.sidebar.cubes",
        "ui.toast",
        "ui.toast.hypa",
        "ui.toast.clipboard",
        "ui.toast.sources",
        "ui.sound",
        "ui.sound.agents",
        "session",
        "worktrees",
        "remote",
        "advanced",
        "experimental",
        "update",
    };

    private static readonly HashSet<string> KnownArrayTables = new(StringComparer.Ordinal)
    {
        "keys.command",
        "ui.sidebar.section",
    };

    private static readonly HashSet<string> SoundAgentNames = new(StringComparer.Ordinal)
    {
        "pi", "claude", "codex", "gemini", "cursor", "devin", "agy", "cline",
        "open_code", "github_copilot", "kimi", "kiro", "droid", "amp", "grok",
        "hermes", "kilo", "qodercli", "maki",
    };

    private static readonly HashSet<string> CommandTypes = new(StringComparer.Ordinal)
    {
        "shell", "pane", "popup", "plugin_action",
    };

    private static readonly HashSet<string> CjkCursorShapes = new(StringComparer.Ordinal)
    {
        "block", "steady_block", "underline", "steady_underline", "bar", "steady_bar",
    };

    public static AttachConfigResult<AttachClientConfig> Bind(string text)
    {
        var parsed = Parse(text);
        if (!parsed.IsOk)
            return AttachConfigResult<AttachClientConfig>.Fail(parsed.Errors);

        var errors = new List<AttachConfigError>();
        foreach (var (table, line) in parsed.Value.TableHeaders)
        {
            if (KnownArrayTables.Contains(table) || KnownTables.Contains(table))
                continue;
            errors.Add(DescribeUnknownSection(table, line));
        }

        var state = new BindState();
        foreach (var assignment in parsed.Value.Assignments)
            Apply(state, assignment, errors);

        var commands = new List<AttachKeyCommandConfig>();
        var sections = new List<AttachSidebarSectionOverride>();
        foreach (var table in parsed.Value.ArrayTables)
        {
            if (!KnownArrayTables.Contains(table.Path))
            {
                errors.Add(DescribeUnknownSection(table.Path, table.Line));
                continue;
            }

            if (table.Path == "keys.command")
                commands.Add(BindCommand(table, errors));
            else if (table.Path == "ui.sidebar.section")
                sections.Add(BindSidebarSection(table, errors));
        }

        if (commands.Count > 0)
            state.Keys = state.Keys with { Commands = commands };
        if (sections.Count > 0)
            state.SidebarSections = sections;

        ValidateThemes(state, errors);
        ValidateSession(state, errors);
        ValidateKeys(state, errors);
        ValidateSidebar(state, errors);

        if (errors.Count > 0)
            return AttachConfigResult<AttachClientConfig>.Fail(errors);

        return AttachConfigResult<AttachClientConfig>.Ok(state.Build());
    }

    private static void Apply(BindState state, TomlAssignment assignment, List<AttachConfigError> errors)
    {
        var key = assignment.Path;
        var value = assignment.Value;
        var line = assignment.Line;

        if (value is TomlInlineTableValue inline)
        {
            if (inline.Fields.Count == 0)
            {
                if (!KnownTables.Contains(key))
                    errors.Add(AttachConfigError.Unknown(key, line));
                return;
            }

            foreach (var field in inline.Fields)
            {
                var nestedPath = string.IsNullOrEmpty(key) ? field.Path : key + "." + field.Path;
                Apply(state, new TomlAssignment(nestedPath, field.Value, field.Line), errors);
            }

            return;
        }

        if (key is "update.channel" or "update.version_check" or "update")
        {
            errors.Add(AttachConfigError.Value(
                key,
                $"'{key}' is out of product. Hypa does not ship update.channel or update.version_check.",
                line));
            return;
        }

        if (key.StartsWith("ui.toast.herdr", StringComparison.Ordinal))
        {
            errors.Add(AttachConfigError.Value(
                key,
                "Unknown key 'ui.toast.herdr'. Use ui.toast.hypa.position.",
                line));
            return;
        }

        if (key.StartsWith("theme.custom.", StringComparison.Ordinal))
        {
            var token = key["theme.custom.".Length..];
            if (!Hypa.AgentRuntime.Domain.Theme.ThemePalette.IsCustomToken(token))
            {
                errors.Add(AttachConfigError.Unknown(key, line));
                return;
            }

            if (!TryString(value, key, errors, out var color))
                return;
            if (!Hypa.AgentRuntime.Domain.Theme.ThemeColorParser.TryParse(color, out _))
            {
                errors.Add(AttachConfigError.Value(
                    key,
                    $"theme.custom.{token} is not a valid color.",
                    line));
                return;
            }

            var custom = new Dictionary<string, string>(state.Theme.Custom, StringComparer.Ordinal)
            {
                [token] = color,
            };
            state.Theme = state.Theme with { Custom = custom };
            return;
        }

        if (key.StartsWith("ui.sound.agents.", StringComparison.Ordinal))
        {
            var agent = key["ui.sound.agents.".Length..];
            if (!SoundAgentNames.Contains(agent))
            {
                errors.Add(AttachConfigError.Unknown(key, line));
                return;
            }

            if (!TryEnum<SoundAgentMode>(value, key, errors, ParseSoundAgent, "default / on / off", out var mode))
                return;
            var agents = new Dictionary<string, SoundAgentMode>(state.Sound.Agents, StringComparer.Ordinal)
            {
                [agent] = mode,
            };
            state.Sound = state.Sound with { Agents = agents };
            return;
        }

        if (key.StartsWith("ui.toast.sources.", StringComparison.Ordinal))
        {
            var source = key["ui.toast.sources.".Length..];
            if (string.IsNullOrWhiteSpace(source))
            {
                errors.Add(AttachConfigError.Unknown(key, line));
                return;
            }

            if (!TryEnum<ToastSourceMode>(value, key, errors, ParseToastSourceMode, "on / off", out var mode))
                return;
            var sources = new Dictionary<string, ToastSourceMode>(state.Toast.Sources, StringComparer.Ordinal)
            {
                [source] = mode,
            };
            state.Toast = state.Toast with { Sources = sources };
            return;
        }

        if (key.StartsWith("ui.sidebar.agents.rows_by_agent.", StringComparison.Ordinal))
        {
            var agent = key["ui.sidebar.agents.rows_by_agent.".Length..];
            if (!TryTokenRows(value, key, errors, out var rows))
                return;
            var map = new Dictionary<string, IReadOnlyList<IReadOnlyList<SidebarTokenSpec>>>(
                state.SidebarAgents.RowsByAgent,
                StringComparer.Ordinal)
            {
                [agent] = rows,
            };
            state.SidebarAgents = state.SidebarAgents with { RowsByAgent = map };
            return;
        }

        if (key.Equals("ui.sidebar.spaces.rows_by_agent", StringComparison.Ordinal)
            || key.StartsWith("ui.sidebar.spaces.rows_by_agent.", StringComparison.Ordinal)
            || key.Equals("ui.sidebar.cubes.rows_by_agent", StringComparison.Ordinal)
            || key.StartsWith("ui.sidebar.cubes.rows_by_agent.", StringComparison.Ordinal))
        {
            errors.Add(AttachConfigError.Unknown(key, line));
            return;
        }

        switch (key)
        {
            case "onboarding":
                if (TryBool(value, key, errors, out var onboarding))
                    state.Onboarding = onboarding;
                return;
            case "theme.name":
                if (TryString(value, key, errors, out var themeName))
                {
                    state.Theme = state.Theme with { Name = themeName };
                    state.ThemeNameLine = line;
                }

                return;
            case "theme.auto_switch":
                if (TryBool(value, key, errors, out var autoSwitch))
                    state.Theme = state.Theme with { AutoSwitch = autoSwitch };
                return;
            case "theme.dark_name":
                if (TryString(value, key, errors, out var dark))
                {
                    state.Theme = state.Theme with { DarkName = dark };
                    state.ThemeDarkNameLine = line;
                }

                return;
            case "theme.light_name":
                if (TryString(value, key, errors, out var light))
                {
                    state.Theme = state.Theme with { LightName = light };
                    state.ThemeLightNameLine = line;
                }

                return;
            case "terminal.default_shell":
                if (TryString(value, key, errors, out var shell))
                    state.Terminal = state.Terminal with { DefaultShell = shell };
                return;
            case "terminal.shell_mode":
                if (TryEnum<TerminalShellMode>(value, key, errors, ParseShellMode, "auto / login / non_login", out var mode))
                    state.Terminal = state.Terminal with { ShellMode = mode };
                return;
            case "terminal.new_cwd":
                if (TryString(value, key, errors, out var cwd))
                    state.Terminal = state.Terminal with { NewCwd = ParseNewCwd(cwd) };
                return;
            case "session.name":
                if (TryString(value, key, errors, out var sessionName))
                {
                    state.Session = state.Session with { Name = sessionName };
                    state.SessionNameLine = line;
                }

                return;
            case "session.resume_agents_on_restore":
                if (TryBool(value, key, errors, out var resume))
                    state.Session = state.Session with { ResumeAgentsOnRestore = resume };
                return;
            case "advanced.scrollback_limit_bytes":
                if (TryInt(value, key, errors, min: 1, out var bytes))
                    state.Advanced = state.Advanced with { ScrollbackLimitBytes = bytes };
                return;
            case "worktrees.directory":
                if (TryString(value, key, errors, out var dir))
                    state.Worktrees = state.Worktrees with { Directory = dir };
                return;
            case "remote.manage_ssh_config":
                if (TryBool(value, key, errors, out var ssh))
                    state.Remote = state.Remote with { ManageSshConfig = ssh };
                return;
            case "update.manifest_check":
                if (TryBool(value, key, errors, out var manifestCheck))
                    state.Update = state.Update with { ManifestCheck = manifestCheck };
                return;
            case "experimental.allow_nested":
                if (TryBool(value, key, errors, out var nested))
                    state.Experimental = state.Experimental with { AllowNested = nested };
                return;
            case "experimental.kitty_graphics":
                if (TryBool(value, key, errors, out var kitty))
                    state.Experimental = state.Experimental with { KittyGraphics = kitty };
                return;
            case "experimental.pane_history":
                if (TryBool(value, key, errors, out var history))
                    state.Experimental = state.Experimental with { PaneHistory = history };
                return;
            case "experimental.reveal_hidden_cursor_for_cjk_ime":
                if (TryBool(value, key, errors, out var reveal))
                    state.Experimental = state.Experimental with { RevealHiddenCursorForCjkIme = reveal };
                return;
            case "experimental.cjk_ime_agents":
                if (TryStringList(value, key, errors, out var agents))
                    state.Experimental = state.Experimental with { CjkImeAgents = agents };
                return;
            case "experimental.cjk_ime_cursor_shape":
                if (TryString(value, key, errors, out var shape))
                {
                    if (!CjkCursorShapes.Contains(shape))
                    {
                        errors.Add(AttachConfigError.Value(
                            key,
                            "experimental.cjk_ime_cursor_shape must be block / steady_block / underline / steady_underline / bar / steady_bar.",
                            line));
                        return;
                    }

                    state.Experimental = state.Experimental with { CjkImeCursorShape = shape };
                }

                return;
            case "experimental.switch_ascii_input_source_in_prefix":
                if (TryBool(value, key, errors, out var ascii))
                    state.Experimental = state.Experimental with { SwitchAsciiInputSourceInPrefix = ascii };
                return;
        }

        if (TryApplyKey(state, key, value, errors))
            return;
        if (TryApplyUi(state, key, value, errors))
            return;

        errors.Add(AttachConfigError.Unknown(key, line));
    }

    private static void ApplyZoomAlias(
        BindState state,
        string key,
        AttachBindingSpec spec,
        int? line,
        List<AttachConfigError> errors)
    {
        var fromFullscreen = key == "keys.fullscreen";
        if (fromFullscreen && state.ZoomSeen)
        {
            errors.Add(AttachConfigError.Value(
                "keys.fullscreen",
                "keys.fullscreen is an alias of keys.zoom; do not set both.",
                line));
            return;
        }

        if (!fromFullscreen && state.FullscreenSeen)
        {
            errors.Add(AttachConfigError.Value(
                "keys.zoom",
                "keys.fullscreen is an alias of keys.zoom; do not set both.",
                line));
            return;
        }

        if (fromFullscreen)
        {
            state.FullscreenSeen = true;
            state.Keys = state.Keys with { Zoom = spec, Fullscreen = spec };
            return;
        }

        state.ZoomSeen = true;
        state.Keys = state.Keys with { Zoom = spec };
    }

    private static bool TryApplyKey(BindState state, string key, TomlValue value, List<AttachConfigError> errors)
    {
        if (!key.StartsWith("keys.", StringComparison.Ordinal))
            return false;

        if (key is "keys.indexed.tabs" or "keys.indexed.workspaces" or "keys.indexed.agents")
        {
            if (!TryBinding(value, key, errors, out var raw))
                return true;
            var normalized = new List<string>(raw.Specs.Count);
            foreach (var item in raw.Specs)
                normalized.Add(AttachKeySpec.NormalizeIndexed(item));
            var bound = AttachBindingSpec.From(normalized);
            state.Keys = key switch
            {
                "keys.indexed.tabs" => state.Keys with { IndexedTabs = bound },
                "keys.indexed.workspaces" => state.Keys with { IndexedWorkspaces = bound },
                _ => state.Keys with { IndexedAgents = bound },
            };
            return true;
        }

        if (!TryBinding(value, key, errors, out var spec))
            return key.StartsWith("keys.", StringComparison.Ordinal);

        if (key == "keys.prefix")
            state.PrefixLine = value.Line;

        if (key is "keys.zoom" or "keys.fullscreen")
        {
            ApplyZoomAlias(state, key, spec, value.Line, errors);
            return true;
        }

        state.Keys = key switch
        {
            "keys.prefix" => state.Keys with { Prefix = spec },
            "keys.help" => state.Keys with { Help = spec },
            "keys.settings" => state.Keys with { Settings = spec },
            "keys.new_workspace" => state.Keys with { NewWorkspace = spec },
            "keys.new_worktree" => state.Keys with { NewWorktree = spec },
            "keys.open_worktree" => state.Keys with { OpenWorktree = spec },
            "keys.remove_worktree" => state.Keys with { RemoveWorktree = spec },
            "keys.rename_workspace" => state.Keys with { RenameWorkspace = spec },
            "keys.close_workspace" => state.Keys with { CloseWorkspace = spec },
            "keys.workspace_picker" => state.Keys with { WorkspacePicker = spec },
            "keys.goto" => state.Keys with { Goto = spec },
            "keys.navigate" => state.Keys with { Navigate = spec },
            "keys.navigate_workspace_up" => state.Keys with { NavigateWorkspaceUp = spec },
            "keys.navigate_workspace_down" => state.Keys with { NavigateWorkspaceDown = spec },
            "keys.navigate_pane_left" => state.Keys with { NavigatePaneLeft = spec },
            "keys.navigate_pane_down" => state.Keys with { NavigatePaneDown = spec },
            "keys.navigate_pane_up" => state.Keys with { NavigatePaneUp = spec },
            "keys.navigate_pane_right" => state.Keys with { NavigatePaneRight = spec },
            "keys.detach" => state.Keys with { Detach = spec },
            "keys.reload_config" => state.Keys with { ReloadConfig = spec },
            "keys.open_notification_target" => state.Keys with { OpenNotificationTarget = spec },
            "keys.previous_workspace" => state.Keys with { PreviousWorkspace = spec },
            "keys.next_workspace" => state.Keys with { NextWorkspace = spec },
            "keys.previous_agent" => state.Keys with { PreviousAgent = spec },
            "keys.next_agent" => state.Keys with { NextAgent = spec },
            "keys.focus_agent" => state.Keys with { FocusAgent = spec },
            "keys.remote_image_paste" => state.Keys with { RemoteImagePaste = spec },
            "keys.new_tab" => state.Keys with { NewTab = spec },
            "keys.rename_tab" => state.Keys with { RenameTab = spec },
            "keys.previous_tab" => state.Keys with { PreviousTab = spec },
            "keys.next_tab" => state.Keys with { NextTab = spec },
            "keys.move_tab_previous" => state.Keys with { MoveTabPrevious = spec },
            "keys.move_tab_next" => state.Keys with { MoveTabNext = spec },
            "keys.switch_tab" => state.Keys with { SwitchTab = spec },
            "keys.switch_workspace" => state.Keys with { SwitchWorkspace = spec },
            "keys.close_tab" => state.Keys with { CloseTab = spec },
            "keys.rename_pane" => state.Keys with { RenamePane = spec },
            "keys.edit_scrollback" => state.Keys with { EditScrollback = spec },
            "keys.annotate_handoff" => state.Keys with { AnnotateHandoff = spec },
            "keys.copy_mode" => state.Keys with { CopyMode = spec },
            "keys.focus_pane_left" => state.Keys with { FocusPaneLeft = spec },
            "keys.focus_pane_down" => state.Keys with { FocusPaneDown = spec },
            "keys.focus_pane_up" => state.Keys with { FocusPaneUp = spec },
            "keys.focus_pane_right" => state.Keys with { FocusPaneRight = spec },
            "keys.swap_pane_left" => state.Keys with { SwapPaneLeft = spec },
            "keys.swap_pane_down" => state.Keys with { SwapPaneDown = spec },
            "keys.swap_pane_up" => state.Keys with { SwapPaneUp = spec },
            "keys.swap_pane_right" => state.Keys with { SwapPaneRight = spec },
            "keys.cycle_pane_next" => state.Keys with { CyclePaneNext = spec },
            "keys.cycle_pane_previous" => state.Keys with { CyclePanePrevious = spec },
            "keys.last_pane" => state.Keys with { LastPane = spec },
            "keys.split_vertical" => state.Keys with { SplitVertical = spec },
            "keys.split_horizontal" => state.Keys with { SplitHorizontal = spec },
            "keys.close_pane" => state.Keys with { ClosePane = spec },
            "keys.resize_mode" => state.Keys with { ResizeMode = spec },
            "keys.resize_pane_left" => state.Keys with { ResizePaneLeft = spec },
            "keys.resize_pane_down" => state.Keys with { ResizePaneDown = spec },
            "keys.resize_pane_up" => state.Keys with { ResizePaneUp = spec },
            "keys.resize_pane_right" => state.Keys with { ResizePaneRight = spec },
            "keys.toggle_sidebar" => state.Keys with { ToggleSidebar = spec },
            _ => state.Keys,
        };

        return key switch
        {
            "keys.prefix" or "keys.help" or "keys.settings" or "keys.new_workspace" or "keys.new_worktree"
                or "keys.open_worktree" or "keys.remove_worktree" or "keys.rename_workspace"
                or "keys.close_workspace" or "keys.workspace_picker" or "keys.goto"
                or "keys.navigate"
                or "keys.navigate_workspace_up" or "keys.navigate_workspace_down"
                or "keys.navigate_pane_left" or "keys.navigate_pane_down" or "keys.navigate_pane_up"
                or "keys.navigate_pane_right" or "keys.detach" or "keys.reload_config"
                or "keys.open_notification_target" or "keys.previous_workspace" or "keys.next_workspace"
                or "keys.previous_agent" or "keys.next_agent" or "keys.focus_agent"
                or "keys.remote_image_paste" or "keys.new_tab" or "keys.rename_tab"
                or "keys.previous_tab" or "keys.next_tab" or "keys.move_tab_previous"
                or "keys.move_tab_next" or "keys.switch_tab" or "keys.switch_workspace"
                or "keys.close_tab" or "keys.rename_pane" or "keys.edit_scrollback" or "keys.annotate_handoff" or "keys.copy_mode"
                or "keys.focus_pane_left" or "keys.focus_pane_down" or "keys.focus_pane_up"
                or "keys.focus_pane_right" or "keys.swap_pane_left" or "keys.swap_pane_down"
                or "keys.swap_pane_up" or "keys.swap_pane_right" or "keys.cycle_pane_next"
                or "keys.cycle_pane_previous" or "keys.last_pane" or "keys.split_vertical"
                or "keys.split_horizontal" or "keys.close_pane" or "keys.zoom" or "keys.fullscreen"
                or "keys.resize_mode" or "keys.resize_pane_left" or "keys.resize_pane_down"
                or "keys.resize_pane_up" or "keys.resize_pane_right" or "keys.toggle_sidebar" => true,
            _ => false,
        };
    }

    private static bool TryApplyUi(BindState state, string key, TomlValue value, List<AttachConfigError> errors)
    {
        switch (key)
        {
            case "ui.sidebar_width":
                if (TryInt(value, key, errors, min: 1, out var w))
                    state.Ui = state.Ui with { SidebarWidth = (int)w };
                return true;
            case "ui.sidebar_min_width":
                if (TryInt(value, key, errors, min: 1, out var minW))
                {
                    state.Ui = state.Ui with { SidebarMinWidth = (int)minW };
                    state.SidebarMinWidthSeen = true;
                }

                return true;
            case "ui.sidebar_max_width":
                if (TryInt(value, key, errors, min: 1, out var maxW))
                {
                    state.Ui = state.Ui with { SidebarMaxWidth = (int)maxW };
                    state.SidebarMaxWidthSeen = true;
                }

                return true;
            case "ui.sidebar_start_collapsed":
                if (TryBool(value, key, errors, out var collapsed))
                    state.Ui = state.Ui with { SidebarStartCollapsed = collapsed };
                return true;
            case "ui.sidebar_collapsed_mode":
                if (TryEnum<SidebarCollapsedMode>(value, key, errors, ParseCollapsed, "compact / hidden", out var colMode))
                    state.Ui = state.Ui with { SidebarCollapsedMode = colMode };
                return true;
            case "ui.mobile_width_threshold":
                if (TryInt(value, key, errors, min: 1, out var mobile))
                    state.Ui = state.Ui with { MobileWidthThreshold = (int)mobile };
                return true;
            case "ui.mouse_capture":
                if (TryBool(value, key, errors, out var mouse))
                    state.Ui = state.Ui with { MouseCapture = mouse };
                return true;
            case "ui.copy_on_select":
                if (TryBool(value, key, errors, out var copy))
                    state.Ui = state.Ui with { CopyOnSelect = copy };
                return true;
            case "ui.host_cursor":
                if (TryEnum<HostCursorMode>(value, key, errors, ParseHostCursor, "auto / native / drawn", out var cursor))
                    state.Ui = state.Ui with { HostCursor = cursor };
                return true;
            case "ui.right_click_passthrough_modifier":
                if (TryString(value, key, errors, out var modifier))
                    state.Ui = state.Ui with { RightClickPassthroughModifier = modifier };
                return true;
            case "ui.redraw_on_focus_gained":
                if (TryBool(value, key, errors, out var redraw))
                    state.Ui = state.Ui with { RedrawOnFocusGained = redraw };
                return true;
            case "ui.mouse_scroll_lines":
                if (TryInt(value, key, errors, min: 1, out var scroll))
                    state.Ui = state.Ui with { MouseScrollLines = (int)scroll };
                return true;
            case "ui.confirm_close":
                if (TryBool(value, key, errors, out var confirm))
                    state.Ui = state.Ui with { ConfirmClose = confirm };
                return true;
            case "ui.prompt_new_tab_name":
                if (TryBool(value, key, errors, out var promptTab))
                    state.Ui = state.Ui with { PromptNewTabName = promptTab };
                return true;
            case "ui.prompt_new_workspace_name":
                if (TryBool(value, key, errors, out var promptWs))
                    state.Ui = state.Ui with { PromptNewWorkspaceName = promptWs };
                return true;
            case "ui.pane_borders":
                if (TryBool(value, key, errors, out var borders))
                    state.Ui = state.Ui with { PaneBorders = borders };
                return true;
            case "ui.pane_outer_borders":
                if (TryBool(value, key, errors, out var outer))
                    state.Ui = state.Ui with { PaneOuterBorders = outer };
                return true;
            case "ui.pane_scrollbars":
                if (TryBool(value, key, errors, out var bars))
                    state.Ui = state.Ui with { PaneScrollbars = bars };
                return true;
            case "ui.pane_gaps":
                if (TryBool(value, key, errors, out var gaps))
                    state.Ui = state.Ui with { PaneGaps = gaps };
                return true;
            case "ui.chrome.glyphs":
                if (TryEnum<ChromeGlyphPreset>(value, key, errors, ParseGlyphs, "unicode / ascii / double", out var glyphs))
                    state.Ui = state.Ui with { Glyphs = ChromeGlyphSet.For(glyphs) };
                return true;
            case "ui.show_agent_labels_on_pane_borders":
                if (TryBool(value, key, errors, out var labels))
                    state.Ui = state.Ui with { ShowAgentLabelsOnPaneBorders = labels };
                return true;
            case "ui.startup_splash":
                if (TryBool(value, key, errors, out var splash))
                    state.Ui = state.Ui with { StartupSplash = splash };
                return true;
            case "ui.hide_tab_bar_when_single_tab":
                if (TryBool(value, key, errors, out var hide))
                    state.Ui = state.Ui with { HideTabBarWhenSingleTab = hide };
                return true;
            case "ui.tab_bar_position":
                if (TryEnum<TabBarPosition>(value, key, errors, ParseTabBar, "top / bottom", out var pos))
                    state.Ui = state.Ui with { TabBarPosition = pos };
                return true;
            case "ui.tab_bar_right":
                if (TryTabBarRight(value, key, errors, out var right))
                    state.Ui = state.Ui with { TabBarRight = right };
                return true;
            case "ui.tab_bar_right_separator":
                if (TryString(value, key, errors, out var sep))
                    state.Ui = state.Ui with { TabBarRightSeparator = sep };
                return true;
            case "ui.window_title":
                if (TryString(value, key, errors, out var title))
                    state.Ui = state.Ui with { WindowTitle = title };
                return true;
            case "ui.agent_panel_sort":
                if (TryEnum<AgentPanelSort>(value, key, errors, ParseAgentSort, "spaces / priority", out var sort))
                    state.Ui = state.Ui with { AgentPanelSort = sort };
                return true;
            case "ui.status_indicators":
                if (TryEnum<StatusIndicatorStyle>(value, key, errors, ParseStatus, "dots / symbols", out var indicators))
                    state.Ui = state.Ui with { StatusIndicators = indicators };
                return true;
            case "ui.accent":
                if (TryString(value, key, errors, out var accent))
                {
                    if (!IsLegacyCyanAccent(accent)
                        && !Hypa.AgentRuntime.Domain.Theme.ThemeColorParser.TryParse(accent, out _))
                    {
                        errors.Add(AttachConfigError.Value(
                            key,
                            $"ui.accent '{accent}' is not a valid color.",
                            value.Line));
                        return true;
                    }

                    state.Ui = state.Ui with { Accent = accent };
                }

                return true;
            case "ui.sidebar.agents.row_gap":
                if (TryInt(value, key, errors, min: 0, out var aGap))
                    state.SidebarAgents = state.SidebarAgents with { RowGap = (int)aGap };
                return true;
            case "ui.sidebar.agents.rows":
                if (TryTokenRows(value, key, errors, out var aRows))
                    state.SidebarAgents = state.SidebarAgents with { Rows = aRows };
                return true;
            case "ui.sidebar.spaces.row_gap":
                if (TryInt(value, key, errors, min: 0, out var sGap))
                    state.SidebarSpaces = state.SidebarSpaces with { RowGap = (int)sGap };
                return true;
            case "ui.sidebar.spaces.rows":
                if (TryTokenRows(value, key, errors, out var sRows))
                    state.SidebarSpaces = state.SidebarSpaces with { Rows = sRows };
                return true;
            case "ui.sidebar.cubes.row_gap":
                if (TryInt(value, key, errors, min: 0, out var cGap))
                    state.SidebarCubes = state.SidebarCubes with { RowGap = (int)cGap };
                return true;
            case "ui.sidebar.cubes.rows":
                if (TryTokenRows(value, key, errors, out var cRows))
                    state.SidebarCubes = state.SidebarCubes with { Rows = cRows };
                return true;
            case "ui.toast.enabled":
                if (TryBool(value, key, errors, out var toastOn))
                    state.Toast = state.Toast with { Enabled = toastOn };
                return true;
            case "ui.toast.delivery":
                if (value is TomlStringValue { Value: "herdr" })
                {
                    errors.Add(AttachConfigError.Value(
                        key,
                        "ui.toast.delivery 'herdr' is not valid. Use off / hypa / terminal / system.",
                        value.Line));
                    return true;
                }

                if (TryEnum<ToastDelivery>(value, key, errors, ParseDelivery, "off / hypa / terminal / system", out var delivery))
                    state.Toast = state.Toast with { Delivery = delivery };
                return true;
            case "ui.toast.delay_seconds":
                if (TryInt(value, key, errors, min: 0, out var delay))
                    state.Toast = state.Toast with { DelaySeconds = (int)delay };
                return true;
            case "ui.toast.hypa.position":
                if (TryEnum<ToastPosition>(value, key, errors, ParseToastCorner, "top-left / top-right / bottom-left / bottom-right", out var hypaPos))
                    state.Toast = state.Toast with { HypaPosition = hypaPos };
                return true;
            case "ui.toast.clipboard.enabled":
                if (TryBool(value, key, errors, out var clipOn))
                    state.Toast = state.Toast with { ClipboardEnabled = clipOn };
                return true;
            case "ui.toast.clipboard.position":
                if (TryEnum<ToastPosition>(value, key, errors, ParseToastPosition, "top-left / top-center / top-right / bottom-left / bottom-center / bottom-right", out var clipPos))
                    state.Toast = state.Toast with { ClipboardPosition = clipPos };
                return true;
            case "ui.sound.enabled":
                if (TryBool(value, key, errors, out var soundOn))
                    state.Sound = state.Sound with { Enabled = soundOn };
                return true;
            case "ui.sound.path":
                if (TryString(value, key, errors, out var soundPath))
                    state.Sound = state.Sound with { Path = soundPath };
                return true;
            case "ui.sound.done_path":
                if (TryString(value, key, errors, out var donePath))
                    state.Sound = state.Sound with { DonePath = donePath };
                return true;
            case "ui.sound.request_path":
                if (TryString(value, key, errors, out var reqPath))
                    state.Sound = state.Sound with { RequestPath = reqPath };
                return true;
            default:
                return false;
        }
    }

    private static AttachKeyCommandConfig BindCommand(TomlArrayTable table, List<AttachConfigError> errors)
    {
        var cmd = new AttachKeyCommandConfig();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in table.Fields)
        {
            var local = field.Path.StartsWith("keys.command.", StringComparison.Ordinal)
                ? field.Path["keys.command.".Length..]
                : field.Path;
            seen.Add(local);
            switch (local)
            {
                case "key":
                    if (TryBinding(field.Value, "keys.command.key", errors, out var key))
                        cmd = cmd with { Key = key };
                    break;
                case "type":
                    if (TryString(field.Value, "keys.command.type", errors, out var type))
                    {
                        if (!CommandTypes.Contains(type))
                        {
                            errors.Add(AttachConfigError.Value(
                                "keys.command.type",
                                "keys.command.type must be shell / pane / popup / plugin_action.",
                                field.Line));
                        }
                        else
                        {
                            cmd = cmd with { Type = type };
                        }
                    }

                    break;
                case "command":
                    if (TryString(field.Value, "keys.command.command", errors, out var command))
                        cmd = cmd with { Command = command };
                    break;
                case "description":
                    if (TryString(field.Value, "keys.command.description", errors, out var desc))
                        cmd = cmd with { Description = desc };
                    break;
                case "width":
                    if (TryPopupSize(field.Value, "keys.command.width", errors, out var width))
                        cmd = cmd with { Width = width };
                    break;
                case "height":
                    if (TryPopupSize(field.Value, "keys.command.height", errors, out var height))
                        cmd = cmd with { Height = height };
                    break;
                default:
                    errors.Add(AttachConfigError.Unknown("keys.command." + local, field.Line));
                    break;
            }
        }

        if (!seen.Contains("key"))
            errors.Add(AttachConfigError.Value("keys.command.key", "[[keys.command]] requires key.", table.Line));
        if (!seen.Contains("command"))
            errors.Add(AttachConfigError.Value("keys.command.command", "[[keys.command]] requires command.", table.Line));
        return cmd;
    }

    private static AttachSidebarSectionOverride BindSidebarSection(
        TomlArrayTable table,
        List<AttachConfigError> errors)
    {
        var section = new AttachSidebarSectionOverride();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in table.Fields)
        {
            var local = field.Path.StartsWith("ui.sidebar.section.", StringComparison.Ordinal)
                ? field.Path["ui.sidebar.section.".Length..]
                : field.Path;
            seen.Add(local);
            switch (local)
            {
                case "id":
                    if (TryString(field.Value, "ui.sidebar.section.id", errors, out var id))
                        section = section with { Id = id };
                    break;
                case "title":
                    if (TryString(field.Value, "ui.sidebar.section.title", errors, out var title))
                        section = section with { Title = title };
                    break;
                case "sort":
                    if (TryString(field.Value, "ui.sidebar.section.sort", errors, out var sort))
                    {
                        section = sort.Trim().ToLowerInvariant() switch
                        {
                            "attention" => section with { Sort = AttachSidebarSectionSort.Attention },
                            "order" => section with { Sort = AttachSidebarSectionSort.Order },
                            _ => section,
                        };
                        if (sort.Trim().ToLowerInvariant() is not ("order" or "attention"))
                        {
                            errors.Add(AttachConfigError.Value(
                                "ui.sidebar.section.sort",
                                "ui.sidebar.section.sort must be 'order' or 'attention'.",
                                field.Line));
                        }
                    }

                    break;
                case "row_gap":
                    if (TryInt(field.Value, "ui.sidebar.section.row_gap", errors, min: 0, out var rowGap))
                        section = section with { RowGap = (int)rowGap };
                    break;
                case "order":
                    if (TryInt(field.Value, "ui.sidebar.section.order", errors, min: 0, out var order))
                        section = section with { Order = (int)order };
                    break;
                case "collapsed":
                    if (TryBool(field.Value, "ui.sidebar.section.collapsed", errors, out var collapsed))
                        section = section with { Collapsed = collapsed };
                    break;
                case "rows":
                    if (TryTokenRowsOrFlat(field.Value, "ui.sidebar.section.rows", errors, out var rows))
                    {
                        var tokenError = SidebarTokenGrammar.ValidateRows(
                            rows,
                            "ui.sidebar.section.rows",
                            field.Line);
                        if (tokenError is not null)
                            errors.Add(tokenError);
                        section = section with { Rows = rows, RowsSpecified = true };
                    }

                    break;
                case "resource":
                    if (TryString(field.Value, "ui.sidebar.section.resource", errors, out var resource))
                        section = section with { Resource = resource };
                    break;
                default:
                    errors.Add(AttachConfigError.Unknown("ui.sidebar.section." + local, field.Line));
                    break;
            }
        }

        if (!seen.Contains("id"))
        {
            errors.Add(new AttachConfigError(
                AttachConfigError.SidebarSectionUnknownId,
                "[[ui.sidebar.section]] requires id.",
                "ui.sidebar.section.id",
                table.Line));
        }
        else if (SidebarTokenGrammar.IsBuiltInSection(section.Id))
        {
            if (!string.IsNullOrWhiteSpace(section.Resource))
            {
                errors.Add(new AttachConfigError(
                    AttachConfigError.SidebarSectionBuiltInResource,
                    $"[[ui.sidebar.section]] id '{section.Id}' is built-in. Built-in sections cannot bind resource.",
                    "ui.sidebar.section.resource",
                    table.Line));
            }
        }
        else if (string.IsNullOrWhiteSpace(section.Resource))
        {
            errors.Add(new AttachConfigError(
                AttachConfigError.SidebarSectionPluginRequiresResource,
                $"[[ui.sidebar.section]] id '{section.Id}' requires resource for a plugin section.",
                "ui.sidebar.section.resource",
                table.Line));
        }
        else if (!PluginResourceId.TryParse(section.Resource, out _))
        {
            errors.Add(new AttachConfigError(
                AttachConfigError.SidebarSectionResourceInvalid,
                $"[[ui.sidebar.section]] resource '{section.Resource}' is not a valid plugin resource id.",
                "ui.sidebar.section.resource",
                table.Line));
        }

        return section;
    }

    private static bool IsLegacyCyanAccent(string? accent) =>
        string.IsNullOrWhiteSpace(accent)
        || string.Equals(accent, "cyan", StringComparison.Ordinal);

    private static void ValidateThemes(BindState state, List<AttachConfigError> errors)
    {
        if (!AttachThemeNames.IsAllowed(state.Theme.Name))
        {
            errors.Add(AttachConfigError.Value(
                "theme.name",
                $"theme.name '{state.Theme.Name}' is not a built-in theme.",
                state.ThemeNameLine));
        }

        if (!AttachThemeNames.IsAllowed(state.Theme.DarkName))
        {
            errors.Add(AttachConfigError.Value(
                "theme.dark_name",
                $"theme.dark_name '{state.Theme.DarkName}' is not a built-in theme.",
                state.ThemeDarkNameLine));
        }

        if (!AttachThemeNames.IsAllowed(state.Theme.LightName))
        {
            errors.Add(AttachConfigError.Value(
                "theme.light_name",
                $"theme.light_name '{state.Theme.LightName}' is not a built-in theme.",
                state.ThemeLightNameLine));
        }
    }

    private static void ValidateSession(BindState state, List<AttachConfigError> errors)
    {
        if (!SessionId.IsValidName(state.Session.Name))
        {
            errors.Add(AttachConfigError.Value(
                "session.name",
                "session.name must be 1–64 characters of [A-Za-z0-9._-] only (no path separators).",
                state.SessionNameLine));
        }
    }

    private static void ValidateKeys(BindState state, List<AttachConfigError> errors)
    {
        void Check(string key, AttachBindingSpec spec, bool prefixSlot, int? line = null)
        {
            if (!AttachKeySpec.TryValidateBinding(spec, prefixSlot, out var error))
            {
                errors.Add(AttachConfigError.Value(
                    key,
                    prefixSlot && spec.IsUnset
                        ? "keys.prefix is required."
                        : $"Invalid keybinding for {key}: {error}.",
                    line));
            }
        }

        Check("keys.prefix", state.Keys.Prefix, prefixSlot: true, state.PrefixLine);
        Check("keys.help", state.Keys.Help, prefixSlot: false);
        Check("keys.settings", state.Keys.Settings, prefixSlot: false);
        Check("keys.new_workspace", state.Keys.NewWorkspace, prefixSlot: false);
        Check("keys.new_worktree", state.Keys.NewWorktree, prefixSlot: false);
        Check("keys.open_worktree", state.Keys.OpenWorktree, prefixSlot: false);
        Check("keys.remove_worktree", state.Keys.RemoveWorktree, prefixSlot: false);
        Check("keys.rename_workspace", state.Keys.RenameWorkspace, prefixSlot: false);
        Check("keys.close_workspace", state.Keys.CloseWorkspace, prefixSlot: false);
        Check("keys.workspace_picker", state.Keys.WorkspacePicker, prefixSlot: false);
        Check("keys.goto", state.Keys.Goto, prefixSlot: false);
        Check("keys.navigate", state.Keys.Navigate, prefixSlot: false);
        Check("keys.navigate_workspace_up", state.Keys.NavigateWorkspaceUp, prefixSlot: false);
        Check("keys.navigate_workspace_down", state.Keys.NavigateWorkspaceDown, prefixSlot: false);
        Check("keys.navigate_pane_left", state.Keys.NavigatePaneLeft, prefixSlot: false);
        Check("keys.navigate_pane_down", state.Keys.NavigatePaneDown, prefixSlot: false);
        Check("keys.navigate_pane_up", state.Keys.NavigatePaneUp, prefixSlot: false);
        Check("keys.navigate_pane_right", state.Keys.NavigatePaneRight, prefixSlot: false);
        Check("keys.detach", state.Keys.Detach, prefixSlot: false);
        Check("keys.reload_config", state.Keys.ReloadConfig, prefixSlot: false);
        Check("keys.open_notification_target", state.Keys.OpenNotificationTarget, prefixSlot: false);
        Check("keys.previous_workspace", state.Keys.PreviousWorkspace, prefixSlot: false);
        Check("keys.next_workspace", state.Keys.NextWorkspace, prefixSlot: false);
        Check("keys.previous_agent", state.Keys.PreviousAgent, prefixSlot: false);
        Check("keys.next_agent", state.Keys.NextAgent, prefixSlot: false);
        Check("keys.focus_agent", state.Keys.FocusAgent, prefixSlot: false);
        Check("keys.remote_image_paste", state.Keys.RemoteImagePaste, prefixSlot: false);
        Check("keys.new_tab", state.Keys.NewTab, prefixSlot: false);
        Check("keys.rename_tab", state.Keys.RenameTab, prefixSlot: false);
        Check("keys.previous_tab", state.Keys.PreviousTab, prefixSlot: false);
        Check("keys.next_tab", state.Keys.NextTab, prefixSlot: false);
        Check("keys.move_tab_previous", state.Keys.MoveTabPrevious, prefixSlot: false);
        Check("keys.move_tab_next", state.Keys.MoveTabNext, prefixSlot: false);
        Check("keys.switch_tab", state.Keys.SwitchTab, prefixSlot: false);
        Check("keys.switch_workspace", state.Keys.SwitchWorkspace, prefixSlot: false);
        Check("keys.close_tab", state.Keys.CloseTab, prefixSlot: false);
        Check("keys.rename_pane", state.Keys.RenamePane, prefixSlot: false);
        Check("keys.edit_scrollback", state.Keys.EditScrollback, prefixSlot: false);
        Check("keys.annotate_handoff", state.Keys.AnnotateHandoff, prefixSlot: false);
        Check("keys.copy_mode", state.Keys.CopyMode, prefixSlot: false);
        Check("keys.focus_pane_left", state.Keys.FocusPaneLeft, prefixSlot: false);
        Check("keys.focus_pane_down", state.Keys.FocusPaneDown, prefixSlot: false);
        Check("keys.focus_pane_up", state.Keys.FocusPaneUp, prefixSlot: false);
        Check("keys.focus_pane_right", state.Keys.FocusPaneRight, prefixSlot: false);
        Check("keys.swap_pane_left", state.Keys.SwapPaneLeft, prefixSlot: false);
        Check("keys.swap_pane_down", state.Keys.SwapPaneDown, prefixSlot: false);
        Check("keys.swap_pane_up", state.Keys.SwapPaneUp, prefixSlot: false);
        Check("keys.swap_pane_right", state.Keys.SwapPaneRight, prefixSlot: false);
        Check("keys.cycle_pane_next", state.Keys.CyclePaneNext, prefixSlot: false);
        Check("keys.cycle_pane_previous", state.Keys.CyclePanePrevious, prefixSlot: false);
        Check("keys.last_pane", state.Keys.LastPane, prefixSlot: false);
        Check("keys.split_vertical", state.Keys.SplitVertical, prefixSlot: false);
        Check("keys.split_horizontal", state.Keys.SplitHorizontal, prefixSlot: false);
        Check("keys.close_pane", state.Keys.ClosePane, prefixSlot: false);
        Check("keys.zoom", state.Keys.Zoom, prefixSlot: false);
        Check("keys.fullscreen", state.Keys.Fullscreen, prefixSlot: false);
        Check("keys.resize_mode", state.Keys.ResizeMode, prefixSlot: false);
        Check("keys.resize_pane_left", state.Keys.ResizePaneLeft, prefixSlot: false);
        Check("keys.resize_pane_down", state.Keys.ResizePaneDown, prefixSlot: false);
        Check("keys.resize_pane_up", state.Keys.ResizePaneUp, prefixSlot: false);
        Check("keys.resize_pane_right", state.Keys.ResizePaneRight, prefixSlot: false);
        Check("keys.toggle_sidebar", state.Keys.ToggleSidebar, prefixSlot: false);
        Check("keys.indexed.tabs", state.Keys.IndexedTabs, prefixSlot: false);
        Check("keys.indexed.workspaces", state.Keys.IndexedWorkspaces, prefixSlot: false);
        Check("keys.indexed.agents", state.Keys.IndexedAgents, prefixSlot: false);

        foreach (var command in state.Keys.Commands)
            Check("keys.command.key", command.Key, prefixSlot: false);
    }

    private static void ValidateSidebar(BindState state, List<AttachConfigError> errors)
    {
        if (state.SidebarMinWidthSeen
            && state.SidebarMaxWidthSeen
            && state.Ui.SidebarMinWidth > state.Ui.SidebarMaxWidth)
        {
            errors.Add(AttachConfigError.Value(
                "ui.sidebar_min_width",
                "ui.sidebar_min_width must be less than or equal to ui.sidebar_max_width.",
                line: null));
        }

        var tokenError = SidebarTokenGrammar.ValidateRows(state.SidebarAgents.Rows, "ui.sidebar.agents.rows");
        if (tokenError is not null)
            errors.Add(tokenError);
        foreach (var pair in state.SidebarAgents.RowsByAgent)
        {
            var byAgent = SidebarTokenGrammar.ValidateRows(
                pair.Value,
                "ui.sidebar.agents.rows_by_agent." + pair.Key);
            if (byAgent is not null)
                errors.Add(byAgent);
        }

        var spacesError = SidebarTokenGrammar.ValidateRows(state.SidebarSpaces.Rows, "ui.sidebar.spaces.rows");
        if (spacesError is not null)
            errors.Add(spacesError);

        var cubesError = SidebarTokenGrammar.ValidateRows(state.SidebarCubes.Rows, "ui.sidebar.cubes.rows");
        if (cubesError is not null)
            errors.Add(cubesError);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in state.SidebarSections)
        {
            if (string.IsNullOrEmpty(section.Id))
                continue;
            if (!seen.Add(section.Id))
            {
                errors.Add(new AttachConfigError(
                    AttachConfigError.SidebarSectionDuplicateId,
                    $"[[ui.sidebar.section]] id '{section.Id}' is listed more than once.",
                    "ui.sidebar.section.id",
                    null));
            }

            if (section.RowsSpecified)
            {
                var rowsError = SidebarTokenGrammar.ValidateRows(
                    section.Rows,
                    "ui.sidebar.section.rows");
                if (rowsError is not null)
                    errors.Add(rowsError);
            }
        }
    }

    private static AttachConfigError DescribeUnknownSection(string name, int? line)
    {
        if (name is "update.channel" or "update.version_check")
        {
            return AttachConfigError.Value(
                name,
                $"'{name}' is out of product. Hypa does not ship update.channel or update.version_check.",
                line);
        }

        if (name.StartsWith("ui.toast.herdr", StringComparison.Ordinal))
        {
            return AttachConfigError.Value(
                name,
                "Unknown section 'ui.toast.herdr'. Use ui.toast.hypa.position.",
                line);
        }

        return AttachConfigError.Section(name, line);
    }

    private static bool TryString(TomlValue value, string key, List<AttachConfigError> errors, out string result)
    {
        if (value is TomlStringValue s)
        {
            result = s.Value;
            return true;
        }

        errors.Add(AttachConfigError.Type(key, "a string", value.Line));
        result = "";
        return false;
    }

    private static bool TryBool(TomlValue value, string key, List<AttachConfigError> errors, out bool result)
    {
        if (value is TomlBoolValue b)
        {
            result = b.Value;
            return true;
        }

        errors.Add(AttachConfigError.Type(key, "a boolean", value.Line));
        result = false;
        return false;
    }

    private static bool TryInt(TomlValue value, string key, List<AttachConfigError> errors, long min, out long result)
    {
        if (value is TomlIntValue n)
        {
            if (n.Value < min)
            {
                errors.Add(AttachConfigError.Value(key, $"{key} must be >= {min}.", value.Line));
                result = n.Value;
                return false;
            }

            result = n.Value;
            return true;
        }

        errors.Add(AttachConfigError.Type(key, "an integer", value.Line));
        result = 0;
        return false;
    }

    private static bool TryPopupSize(
        TomlValue value,
        string key,
        List<AttachConfigError> errors,
        out AttachPopupSize size)
    {
        size = AttachPopupSize.Cells(1);
        if (value is TomlIntValue n)
        {
            if (n.Value < 1 || n.Value > int.MaxValue)
            {
                errors.Add(AttachConfigError.Value(key, $"{key} must be >= 1.", value.Line));
                return false;
            }

            size = AttachPopupSize.Cells((int)n.Value);
            return true;
        }

        if (value is TomlStringValue s)
        {
            var raw = s.Value.Trim();
            if (raw.EndsWith('%'))
            {
                var body = raw[..^1].Trim();
                if (!int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
                    || percent < 1
                    || percent > 100)
                {
                    errors.Add(AttachConfigError.Value(key, $"{key} percent must be 1-100.", value.Line));
                    return false;
                }

                size = AttachPopupSize.Percent(percent);
                return true;
            }

            errors.Add(AttachConfigError.Value(
                key,
                $"{key} must be cells or a percent string.",
                value.Line));
            return false;
        }

        errors.Add(AttachConfigError.Type(key, "an integer or percent string", value.Line));
        return false;
    }

    private static bool TryBinding(
        TomlValue value,
        string key,
        List<AttachConfigError> errors,
        out AttachBindingSpec spec)
    {
        if (value is TomlStringValue s)
        {
            spec = AttachBindingSpec.From(s.Value);
            return true;
        }

        if (value is TomlArrayValue arr)
        {
            var parts = new List<string>(arr.Items.Count);
            foreach (var item in arr.Items)
            {
                if (item is not TomlStringValue itemStr)
                {
                    errors.Add(AttachConfigError.Type(key, "a string or array of strings", value.Line));
                    spec = AttachBindingSpec.Unset;
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(itemStr.Value))
                    parts.Add(itemStr.Value.Trim());
            }

            spec = AttachBindingSpec.From(parts);
            return true;
        }

        errors.Add(AttachConfigError.Type(key, "a keybinding string", value.Line));
        spec = AttachBindingSpec.Unset;
        return false;
    }

    private static bool TryTabBarRight(
        TomlValue value,
        string key,
        List<AttachConfigError> errors,
        out IReadOnlyList<AttachTabBarRightEntry> result)
    {
        result = [];
        if (value is not TomlArrayValue arr)
        {
            errors.Add(AttachConfigError.Type(key, "an array of tab bar entries", value.Line));
            return false;
        }

        var list = new List<AttachTabBarRightEntry>(arr.Items.Count);
        foreach (var item in arr.Items)
        {
            if (!TryTabBarRightItem(item, key, errors, out var entry))
                return false;
            list.Add(entry);
        }

        result = list;
        return true;
    }

    private static bool TryTabBarRightItem(
        TomlValue item,
        string key,
        List<AttachConfigError> errors,
        out AttachTabBarRightEntry entry)
    {
        if (item is TomlStringValue s)
            return TryTabBarRightShorthand(s.Value, key, s.Line, errors, out entry);

        if (item is TomlInlineTableValue table)
            return TryTabBarRightTable(table, key, errors, out entry);

        errors.Add(AttachConfigError.Type(key, "a string or inline table", item.Line));
        entry = AttachTabBarRightEntry.Zoom;
        return false;
    }

    private static bool TryTabBarRightShorthand(
        string raw,
        string key,
        int line,
        List<AttachConfigError> errors,
        out AttachTabBarRightEntry entry)
    {
        switch (raw)
        {
            case "zoom":
                entry = AttachTabBarRightEntry.Zoom;
                return true;
            case "hostname":
                entry = AttachTabBarRightEntry.Hostname;
                return true;
            case "datetime":
                entry = AttachTabBarRightEntry.DateTime;
                return true;
            case "text":
                errors.Add(AttachConfigError.Value(
                    key,
                    "ui.tab_bar_right text entries require an inline table with text.",
                    line));
                entry = AttachTabBarRightEntry.Zoom;
                return false;
            case "command":
                errors.Add(AttachConfigError.Value(
                    key,
                    "ui.tab_bar_right command entries require an inline table with command.",
                    line));
                entry = AttachTabBarRightEntry.Zoom;
                return false;
            case "resource":
                errors.Add(AttachConfigError.Value(
                    key,
                    "ui.tab_bar_right resource entries require an inline table with resource.",
                    line));
                entry = AttachTabBarRightEntry.Zoom;
                return false;
            default:
                errors.Add(AttachConfigError.Value(
                    key,
                    "ui.tab_bar_right items must be zoom / hostname / datetime / text / command / resource.",
                    line));
                entry = AttachTabBarRightEntry.Zoom;
                return false;
        }
    }

    private static bool TryTabBarRightTable(
        TomlInlineTableValue table,
        string key,
        List<AttachConfigError> errors,
        out AttachTabBarRightEntry entry)
    {
        entry = AttachTabBarRightEntry.Zoom;
        string? type = null;
        int? typeLine = null;
        string? format = null;
        string? text = null;
        string? command = null;
        string? resource = null;
        int? interval = null;
        int? timeout = null;
        int? maxItems = null;

        foreach (var field in table.Fields)
        {
            switch (field.Path)
            {
                case "type":
                    if (!TryString(field.Value, key, errors, out type))
                        return false;
                    typeLine = field.Line;
                    break;
                case "format":
                    if (!TryString(field.Value, key, errors, out format))
                        return false;
                    break;
                case "text":
                    if (!TryString(field.Value, key, errors, out text))
                        return false;
                    break;
                case "command":
                    if (!TryString(field.Value, key, errors, out command))
                        return false;
                    break;
                case "resource":
                    if (!TryString(field.Value, key, errors, out resource))
                        return false;
                    break;
                case "interval_seconds":
                    if (!TryInt(field.Value, key, errors, min: 1, out var intervalValue))
                        return false;
                    interval = (int)intervalValue;
                    break;
                case "timeout_seconds":
                    if (!TryInt(field.Value, key, errors, min: 1, out var timeoutValue))
                        return false;
                    timeout = (int)timeoutValue;
                    break;
                case "max_items":
                    if (!TryInt(field.Value, key, errors, min: 1, out var maxItemsValue))
                        return false;
                    maxItems = (int)maxItemsValue;
                    break;
                default:
                    errors.Add(AttachConfigError.Unknown(key + "." + field.Path, field.Line));
                    return false;
            }
        }

        if (type is null)
        {
            errors.Add(AttachConfigError.Value(
                key,
                "ui.tab_bar_right entries require type.",
                table.Line));
            return false;
        }

        var line = typeLine ?? table.Line;
        switch (type)
        {
            case "zoom":
                if (!TabBarRightAllowsOnly(key, line, errors, "zoom", format, text, command, interval, timeout))
                    return false;
                entry = AttachTabBarRightEntry.Zoom;
                return true;
            case "hostname":
                if (!TabBarRightAllowsOnly(key, line, errors, "hostname", format, text, command, interval, timeout))
                    return false;
                entry = AttachTabBarRightEntry.Hostname;
                return true;
            case "datetime":
                if (text is not null || command is not null || interval is not null || timeout is not null)
                {
                    errors.Add(AttachConfigError.Value(
                        key,
                        "ui.tab_bar_right datetime entries accept only format.",
                        line));
                    return false;
                }

                if (format is { Length: > 0 } && !IsValidDateTimeFormat(format))
                {
                    errors.Add(AttachConfigError.Value(
                        key,
                        "ui.tab_bar_right datetime format is not valid.",
                        line));
                    return false;
                }

                entry = AttachTabBarRightEntry.ForDateTime(format);
                return true;
            case "text":
                if (text is null)
                {
                    errors.Add(AttachConfigError.Value(
                        key,
                        "ui.tab_bar_right text entries require text.",
                        line));
                    return false;
                }

                if (format is not null || command is not null || interval is not null || timeout is not null)
                {
                    errors.Add(AttachConfigError.Value(
                        key,
                        "ui.tab_bar_right text entries accept only text.",
                        line));
                    return false;
                }

                entry = AttachTabBarRightEntry.ForText(text);
                return true;
            case "command":
                if (string.IsNullOrEmpty(command))
                {
                    errors.Add(AttachConfigError.Value(
                        key,
                        "ui.tab_bar_right command entries require command.",
                        line));
                    return false;
                }

                if (format is not null || text is not null)
                {
                    errors.Add(AttachConfigError.Value(
                        key,
                        "ui.tab_bar_right command entries accept only command, interval_seconds, and timeout_seconds.",
                        line));
                    return false;
                }

                entry = AttachTabBarRightEntry.ForCommand(command, interval, timeout);
                return true;
            case "resource":
                if (string.IsNullOrWhiteSpace(resource))
                {
                    errors.Add(AttachConfigError.Value(
                        key,
                        "ui.tab_bar_right resource entries require resource.",
                        line));
                    return false;
                }

                if (text is not null || command is not null || interval is not null || timeout is not null)
                {
                    errors.Add(AttachConfigError.Value(
                        key,
                        "ui.tab_bar_right resource entries accept only resource, format, and max_items.",
                        line));
                    return false;
                }

                if (!resource.StartsWith("plugin:", StringComparison.Ordinal))
                {
                    errors.Add(AttachConfigError.Value(
                        key,
                        "ui.tab_bar_right resource must start with plugin:.",
                        line));
                    return false;
                }

                entry = AttachTabBarRightEntry.ForResource(resource, format, maxItems);
                return true;
            default:
                errors.Add(AttachConfigError.Value(
                    key,
                    "ui.tab_bar_right type must be zoom / hostname / datetime / text / command / resource.",
                    line));
                return false;
        }
    }

    private static bool TabBarRightAllowsOnly(
        string key,
        int line,
        List<AttachConfigError> errors,
        string type,
        string? format,
        string? text,
        string? command,
        int? interval,
        int? timeout)
    {
        if (format is null && text is null && command is null && interval is null && timeout is null)
            return true;

        errors.Add(AttachConfigError.Value(
            key,
            $"ui.tab_bar_right {type} entries do not accept extra fields.",
            line));
        return false;
    }

    private static bool IsValidDateTimeFormat(string format) =>
        StrftimeClock.IsValidFormat(format);

    private static bool TryStringList(TomlValue value, string key, List<AttachConfigError> errors, out IReadOnlyList<string> result)
    {
        if (value is TomlArrayValue arr)
        {
            var list = new List<string>(arr.Items.Count);
            foreach (var item in arr.Items)
            {
                if (item is not TomlStringValue s)
                {
                    errors.Add(AttachConfigError.Type(key, "an array of strings", value.Line));
                    result = [];
                    return false;
                }

                list.Add(s.Value);
            }

            result = list;
            return true;
        }

        errors.Add(AttachConfigError.Type(key, "an array of strings", value.Line));
        result = [];
        return false;
    }

    private static bool TryTokenRows(
        TomlValue value,
        string key,
        List<AttachConfigError> errors,
        out IReadOnlyList<IReadOnlyList<SidebarTokenSpec>> rows) =>
        TryTokenRowsOrFlat(value, key, errors, out rows, allowFlat: false);

    private static bool TryTokenRowsOrFlat(
        TomlValue value,
        string key,
        List<AttachConfigError> errors,
        out IReadOnlyList<IReadOnlyList<SidebarTokenSpec>> rows,
        bool allowFlat = true)
    {
        rows = [];
        if (value is not TomlArrayValue arr)
        {
            errors.Add(AttachConfigError.Type(key, "a list of token rows", value.Line));
            return false;
        }

        if (arr.Items.Count == 0)
        {
            rows = [];
            return true;
        }

        var allPlainOrStyled = arr.Items.All(IsTokenItem);
        var allArrays = arr.Items.All(i => i is TomlArrayValue);
        if (allowFlat && allPlainOrStyled && !allArrays)
        {
            var flat = new List<SidebarTokenSpec>(arr.Items.Count);
            foreach (var item in arr.Items)
            {
                if (!TryTokenSpec(item, key, errors, out var spec))
                    return false;
                flat.Add(spec);
            }

            rows = [flat];
            return true;
        }

        if (!allArrays)
        {
            errors.Add(AttachConfigError.Type(key, "a list of token rows", value.Line));
            return false;
        }

        var list = new List<IReadOnlyList<SidebarTokenSpec>>(arr.Items.Count);
        foreach (var item in arr.Items)
        {
            var inner = (TomlArrayValue)item;
            var row = new List<SidebarTokenSpec>(inner.Items.Count);
            foreach (var token in inner.Items)
            {
                if (!TryTokenSpec(token, key, errors, out var spec))
                    return false;
                row.Add(spec);
            }

            list.Add(row);
        }

        rows = list;
        return true;
    }

    private static bool IsTokenItem(TomlValue value) =>
        value is TomlStringValue or TomlInlineTableValue;

    /// <summary>
    /// Unknown colour and unknown fields fail closed.
    /// </summary>
    private static bool TryTokenSpec(
        TomlValue value,
        string key,
        List<AttachConfigError> errors,
        out SidebarTokenSpec spec)
    {
        spec = new SidebarTokenSpec();
        if (value is TomlStringValue s)
        {
            spec = s.Value;
            return true;
        }

        if (value is not TomlInlineTableValue table)
        {
            errors.Add(AttachConfigError.Type(key, "a token name or inline token table", value.Line));
            return false;
        }

        string? id = null;
        ThemeColor? fg = null;
        bool? bold = null;
        bool? dim = null;
        IReadOnlyList<SidebarTokenRule> rules = [];
        foreach (var field in table.Fields)
        {
            switch (field.Path)
            {
                case "token":
                    if (!TryString(field.Value, key, errors, out id))
                        return false;
                    break;
                case "fg":
                    if (!TryString(field.Value, key, errors, out var fgText))
                        return false;
                    if (!SidebarTokenRule.TryParseFg(fgText, out var parsedFg))
                    {
                        errors.Add(AttachConfigError.Value(
                            key,
                            "sidebar token fg must be #RGB or #RRGGBB",
                            field.Line));
                        return false;
                    }

                    fg = parsedFg;
                    break;
                case "bold":
                    if (!TryBool(field.Value, key, errors, out var boldValue))
                        return false;
                    bold = boldValue;
                    break;
                case "dim":
                    if (!TryBool(field.Value, key, errors, out var dimValue))
                        return false;
                    dim = dimValue;
                    break;
                case "rules":
                    if (!TryTokenRules(field.Value, key, errors, out rules))
                        return false;
                    break;
                default:
                    errors.Add(AttachConfigError.Unknown(key + "." + field.Path, field.Line));
                    return false;
            }
        }

        if (string.IsNullOrEmpty(id))
        {
            errors.Add(AttachConfigError.Value(
                key,
                $"{key} styled tokens require token.",
                table.Line));
            return false;
        }

        spec = new SidebarTokenSpec
        {
            Id = id,
            Style = new SidebarTokenStyle { Fg = fg, Bold = bold, Dim = dim },
            Rules = rules,
        };
        return true;
    }

    private static bool TryTokenRules(
        TomlValue value,
        string key,
        List<AttachConfigError> errors,
        out IReadOnlyList<SidebarTokenRule> rules)
    {
        rules = [];
        if (value is not TomlArrayValue arr)
        {
            errors.Add(AttachConfigError.Type(key, "an array of token rules", value.Line));
            return false;
        }

        if (arr.Items.Count > 16)
        {
            errors.Add(AttachConfigError.Value(
                key,
                "sidebar tokens may contain at most 16 rules",
                value.Line));
            return false;
        }

        var list = new List<SidebarTokenRule>(arr.Items.Count);
        foreach (var item in arr.Items)
        {
            if (!TryTokenRule(item, key, errors, out var rule))
                return false;
            list.Add(rule);
        }

        rules = list;
        return true;
    }

    private static bool TryTokenRule(
        TomlValue value,
        string key,
        List<AttachConfigError> errors,
        out SidebarTokenRule rule)
    {
        rule = new SidebarTokenRule { Kind = SidebarTokenRuleKind.Equals };
        if (value is not TomlInlineTableValue table)
        {
            errors.Add(AttachConfigError.Type(key, "an inline token rule table", value.Line));
            return false;
        }

        string? equals = null;
        string? contains = null;
        string? startsWith = null;
        double? gt = null;
        double? lt = null;
        bool? ignoreCase = null;
        ThemeColor? fg = null;
        bool? bold = null;
        bool? dim = null;
        foreach (var field in table.Fields)
        {
            switch (field.Path)
            {
                case "equals":
                    if (!TryString(field.Value, key, errors, out equals))
                        return false;
                    break;
                case "contains":
                    if (!TryString(field.Value, key, errors, out contains))
                        return false;
                    break;
                case "starts_with":
                    if (!TryString(field.Value, key, errors, out startsWith))
                        return false;
                    break;
                case "gt":
                    if (!TryRuleNumber(field.Value, key, errors, out var gtValue))
                        return false;
                    gt = gtValue;
                    break;
                case "lt":
                    if (!TryRuleNumber(field.Value, key, errors, out var ltValue))
                        return false;
                    lt = ltValue;
                    break;
                case "ignore_case":
                    if (!TryBool(field.Value, key, errors, out var ignore))
                        return false;
                    ignoreCase = ignore;
                    break;
                case "fg":
                    if (!TryString(field.Value, key, errors, out var fgText))
                        return false;
                    if (!SidebarTokenRule.TryParseFg(fgText, out var parsedFg))
                    {
                        errors.Add(AttachConfigError.Value(
                            key,
                            "sidebar token fg must be #RGB or #RRGGBB",
                            field.Line));
                        return false;
                    }

                    fg = parsedFg;
                    break;
                case "bold":
                    if (!TryBool(field.Value, key, errors, out var boldValue))
                        return false;
                    bold = boldValue;
                    break;
                case "dim":
                    if (!TryBool(field.Value, key, errors, out var dimValue))
                        return false;
                    dim = dimValue;
                    break;
                default:
                    errors.Add(AttachConfigError.Unknown(key + "." + field.Path, field.Line));
                    return false;
            }
        }

        var conditionCount = (equals is not null ? 1 : 0)
            + (contains is not null ? 1 : 0)
            + (startsWith is not null ? 1 : 0)
            + (gt is not null ? 1 : 0)
            + (lt is not null ? 1 : 0);
        if (conditionCount != 1)
        {
            errors.Add(AttachConfigError.Value(
                key,
                "sidebar rule requires exactly one of equals, contains, starts_with, gt, lt",
                table.Line));
            return false;
        }

        if ((gt is not null || lt is not null) && ignoreCase is not null)
        {
            errors.Add(AttachConfigError.Value(
                key,
                "ignore_case applies only to sidebar text conditions",
                table.Line));
            return false;
        }

        SidebarTokenRuleKind kind;
        string text;
        double threshold;
        if (equals is not null)
        {
            kind = SidebarTokenRuleKind.Equals;
            text = equals;
            threshold = 0;
        }
        else if (contains is not null)
        {
            kind = SidebarTokenRuleKind.Contains;
            text = contains;
            threshold = 0;
        }
        else if (startsWith is not null)
        {
            kind = SidebarTokenRuleKind.StartsWith;
            text = startsWith;
            threshold = 0;
        }
        else if (gt is not null)
        {
            kind = SidebarTokenRuleKind.GreaterThan;
            text = "";
            threshold = gt.Value;
        }
        else
        {
            kind = SidebarTokenRuleKind.LessThan;
            text = "";
            threshold = lt!.Value;
        }

        rule = new SidebarTokenRule
        {
            Kind = kind,
            Text = text,
            Threshold = threshold,
            IgnoreCase = ignoreCase ?? false,
            Style = new SidebarTokenStyle { Fg = fg, Bold = bold, Dim = dim },
        };
        return true;
    }

    private static bool TryRuleNumber(
        TomlValue value,
        string key,
        List<AttachConfigError> errors,
        out double result)
    {
        if (value is TomlIntValue n)
        {
            result = n.Value;
            return true;
        }

        if (value is TomlFloatValue f)
        {
            if (!double.IsFinite(f.Value))
            {
                errors.Add(AttachConfigError.Value(
                    key,
                    "sidebar numeric rule threshold must be finite",
                    value.Line));
                result = 0;
                return false;
            }

            result = f.Value;
            return true;
        }

        errors.Add(AttachConfigError.Type(key, "a number", value.Line));
        result = 0;
        return false;
    }

    private static bool TryEnum<T>(
        TomlValue value,
        string key,
        List<AttachConfigError> errors,
        TryParseEnum<T> parse,
        string allowed,
        out T result)
        where T : struct
    {
        result = default;
        if (!TryString(value, key, errors, out var raw))
            return false;
        if (parse(raw, out result))
            return true;
        errors.Add(AttachConfigError.Value(key, $"{key} must be {allowed}.", value.Line));
        return false;
    }

    private delegate bool TryParseEnum<T>(string raw, out T value) where T : struct;

    private static bool ParseShellMode(string raw, out TerminalShellMode value)
    {
        value = raw switch
        {
            "auto" => TerminalShellMode.Auto,
            "login" => TerminalShellMode.Login,
            "non_login" => TerminalShellMode.NonLogin,
            _ => (TerminalShellMode)(-1),
        };
        return value != (TerminalShellMode)(-1);
    }

    private static TerminalNewCwdSpec ParseNewCwd(string raw) =>
        raw switch
        {
            "follow" => TerminalNewCwdSpec.Follow,
            "home" => TerminalNewCwdSpec.Home,
            "current" => TerminalNewCwdSpec.Current,
            _ => TerminalNewCwdSpec.ForPath(raw),
        };

    private static bool ParseGlyphs(string raw, out ChromeGlyphPreset value)
    {
        value = raw switch
        {
            "unicode" => ChromeGlyphPreset.Unicode,
            "ascii" => ChromeGlyphPreset.Ascii,
            "double" => ChromeGlyphPreset.Double,
            _ => (ChromeGlyphPreset)(-1),
        };
        return value != (ChromeGlyphPreset)(-1);
    }

    private static bool ParseCollapsed(string raw, out SidebarCollapsedMode value)
    {
        value = raw switch
        {
            "compact" => SidebarCollapsedMode.Compact,
            "hidden" => SidebarCollapsedMode.Hidden,
            _ => (SidebarCollapsedMode)(-1),
        };
        return value != (SidebarCollapsedMode)(-1);
    }

    private static bool ParseHostCursor(string raw, out HostCursorMode value)
    {
        value = raw switch
        {
            "auto" => HostCursorMode.Auto,
            "native" => HostCursorMode.Native,
            "drawn" => HostCursorMode.Drawn,
            _ => (HostCursorMode)(-1),
        };
        return value != (HostCursorMode)(-1);
    }

    private static bool ParseTabBar(string raw, out TabBarPosition value)
    {
        value = raw switch
        {
            "top" => TabBarPosition.Top,
            "bottom" => TabBarPosition.Bottom,
            _ => (TabBarPosition)(-1),
        };
        return value != (TabBarPosition)(-1);
    }

    private static bool ParseAgentSort(string raw, out AgentPanelSort value)
    {
        value = raw switch
        {
            "spaces" => AgentPanelSort.Spaces,
            "priority" => AgentPanelSort.Priority,
            _ => (AgentPanelSort)(-1),
        };
        return value != (AgentPanelSort)(-1);
    }

    private static bool ParseStatus(string raw, out StatusIndicatorStyle value)
    {
        value = raw switch
        {
            "dots" => StatusIndicatorStyle.Dots,
            "symbols" => StatusIndicatorStyle.Symbols,
            _ => (StatusIndicatorStyle)(-1),
        };
        return value != (StatusIndicatorStyle)(-1);
    }

    private static bool ParseDelivery(string raw, out ToastDelivery value)
    {
        value = raw switch
        {
            "off" => ToastDelivery.Off,
            "hypa" => ToastDelivery.Hypa,
            "terminal" => ToastDelivery.Terminal,
            "system" => ToastDelivery.System,
            _ => (ToastDelivery)(-1),
        };
        return value != (ToastDelivery)(-1);
    }

    private static bool ParseToastCorner(string raw, out ToastPosition value)
    {
        value = raw switch
        {
            "top-left" => ToastPosition.TopLeft,
            "top-right" => ToastPosition.TopRight,
            "bottom-left" => ToastPosition.BottomLeft,
            "bottom-right" => ToastPosition.BottomRight,
            _ => (ToastPosition)(-1),
        };
        return value != (ToastPosition)(-1);
    }

    private static bool ParseToastPosition(string raw, out ToastPosition value)
    {
        value = raw switch
        {
            "top-left" => ToastPosition.TopLeft,
            "top-center" => ToastPosition.TopCenter,
            "top-right" => ToastPosition.TopRight,
            "bottom-left" => ToastPosition.BottomLeft,
            "bottom-center" => ToastPosition.BottomCenter,
            "bottom-right" => ToastPosition.BottomRight,
            _ => (ToastPosition)(-1),
        };
        return value != (ToastPosition)(-1);
    }

    private static bool ParseSoundAgent(string raw, out SoundAgentMode value)
    {
        value = raw switch
        {
            "default" => SoundAgentMode.Default,
            "on" => SoundAgentMode.On,
            "off" => SoundAgentMode.Off,
            _ => (SoundAgentMode)(-1),
        };
        return value != (SoundAgentMode)(-1);
    }

    private static bool ParseToastSourceMode(string raw, out ToastSourceMode value)
    {
        value = raw switch
        {
            "on" => ToastSourceMode.On,
            "off" => ToastSourceMode.Off,
            _ => (ToastSourceMode)(-1),
        };
        return value != (ToastSourceMode)(-1);
    }

    private sealed class BindState
    {
        public bool? Onboarding;
        public AttachThemeConfig Theme = AttachThemeConfig.Default;
        public AttachTerminalConfig Terminal = AttachTerminalConfig.Default;
        public AttachKeysConfig Keys = AttachKeysConfig.Default;
        public AttachUiConfig Ui = AttachUiConfig.Default;
        public AttachSidebarSectionConfig SidebarAgents = AttachSidebarSectionConfig.AgentsDefault;
        public AttachSidebarSectionConfig SidebarSpaces = AttachSidebarSectionConfig.SpacesDefault;
        public AttachSidebarSectionConfig SidebarCubes = AttachSidebarSectionConfig.CubesDefault;
        public IReadOnlyList<AttachSidebarSectionOverride> SidebarSections = [];
        public bool SidebarMinWidthSeen;
        public bool SidebarMaxWidthSeen;
        public AttachToastConfig Toast = AttachToastConfig.Default;
        public AttachSoundConfig Sound = AttachSoundConfig.Default;
        public AttachSessionConfig Session = AttachSessionConfig.Default;
        public AttachAdvancedConfig Advanced = AttachAdvancedConfig.Default;
        public AttachExperimentalConfig Experimental = AttachExperimentalConfig.Default;
        public AttachWorktreesConfig Worktrees = AttachWorktreesConfig.Default;
        public AttachRemoteConfig Remote = AttachRemoteConfig.Default;
        public AttachUpdateConfig Update = AttachUpdateConfig.Default;
        public int? PrefixLine;
        public int? ThemeNameLine;
        public int? ThemeDarkNameLine;
        public int? ThemeLightNameLine;
        public int? SessionNameLine;
        public bool ZoomSeen;
        public bool FullscreenSeen;

        public AttachClientConfig Build() => new()
        {
            Onboarding = Onboarding,
            Theme = Theme,
            Terminal = Terminal,
            Keys = Keys,
            Ui = Ui with
            {
                Sidebar = new AttachSidebarConfig
                {
                    Agents = SidebarAgents,
                    Spaces = SidebarSpaces,
                    Cubes = SidebarCubes,
                    Sections = SidebarSections,
                },
                Toast = Toast,
                Sound = Sound,
            },
            Session = Session,
            Advanced = Advanced,
            Experimental = Experimental,
            Worktrees = Worktrees,
            Remote = Remote,
            Update = Update,
        };
    }
}
