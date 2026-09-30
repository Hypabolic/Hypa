using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class AttachConfigRejectTests
{
    [Fact]
    public void Unknown_key_fails_with_key_and_line()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            not_a_real_key = true
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e => e.Key == "ui.not_a_real_key" && e.Line is > 0);
    }

    [Fact]
    public void Toast_herdr_position_fails_closed_with_hypa_name()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui.toast.herdr]
            position = "bottom-right"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Message.Contains("ui.toast.hypa.position", StringComparison.Ordinal));
    }

    [Fact]
    public void Delivery_herdr_fails_closed_with_hypa_names()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui.toast]
            delivery = "herdr"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Message.Contains("hypa", StringComparison.Ordinal)
            && e.Key == "ui.toast.delivery");
    }

    [Fact]
    public void Bad_enum_and_type_and_toml_fail_closed()
    {
        var badEnum = TomlAttachConfigBinder.Bind("""
            [terminal]
            shell_mode = "login-shell"
            """);
        Assert.False(badEnum.IsOk);
        Assert.Contains(badEnum.Errors, e => e.Key == "terminal.shell_mode");

        var badType = TomlAttachConfigBinder.Bind("""
            [ui]
            mouse_capture = "nope"
            """);
        Assert.False(badType.IsOk);
        Assert.Contains(badType.Errors, e => e.Code == AttachConfigError.WrongType);

        var badToml = TomlAttachConfigBinder.Bind("prefix = ");
        Assert.False(badToml.IsOk);
        Assert.Contains(badToml.Errors, e => e.Code == AttachConfigError.InvalidToml);
    }

    [Fact]
    public void Update_channel_and_version_check_fail_closed()
    {
        var channel = TomlAttachConfigBinder.Bind("""
            [update]
            channel = "stable"
            """);
        Assert.False(channel.IsOk);
        Assert.Contains(channel.Errors, e =>
            e.Key == "update.channel"
            && e.Message.Contains("out of product", StringComparison.Ordinal));

        var check = TomlAttachConfigBinder.Bind("update.version_check = true");
        Assert.False(check.IsOk);
        Assert.Contains(check.Errors, e =>
            e.Key == "update.version_check"
            && e.Message.Contains("out of product", StringComparison.Ordinal));

        var manifest = TomlAttachConfigBinder.Bind("update.manifest_check = true");
        Assert.True(manifest.IsOk, manifest.IsOk ? "" : manifest.Error.ToString());
        Assert.True(manifest.Value.Update.ManifestCheck);
    }

    [Fact]
    public void Unknown_section_fails_with_line()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [not_a_section]
            foo = true
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "not_a_section"
            && e.Code == AttachConfigError.UnknownSection
            && e.Line == 1);
    }

    [Fact]
    public void Invalid_session_name_fails()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [session]
            name = "../etc"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "session.name"
            && e.Line == 2);
    }

    [Fact]
    public void Invalid_theme_name_fails()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [theme]
            name = "not-a-theme"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "theme.name"
            && e.Line == 2);
    }

    [Fact]
    public void Invalid_theme_custom_color_fails_with_line()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [theme.custom]
            accent = "not-a-color"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Code == AttachConfigError.InvalidValue
            && e.Key == "theme.custom.accent"
            && e.Line == 2);
    }

    [Fact]
    public void Whitespace_theme_custom_color_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [theme.custom]
            accent = "   "
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Code == AttachConfigError.InvalidValue
            && e.Key == "theme.custom.accent"
            && e.Line == 2);
        Assert.DoesNotContain(result.Errors, e => e.Code == AttachConfigError.UnknownKey);
    }

    [Fact]
    public void Unknown_theme_custom_token_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [theme.custom]
            active_row_bg = "#ff00aa"
            selection_bg = "#00ffaa"
            not_a_token = "#112233"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Code == AttachConfigError.UnknownKey
            && e.Key == "theme.custom.not_a_token"
            && e.Line == 4);
        Assert.DoesNotContain(result.Errors, e =>
            e.Key is "theme.custom.active_row_bg" or "theme.custom.selection_bg");
    }

    [Fact]
    public void Whitespace_unknown_theme_custom_token_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [theme.custom]
            not_a_token = "   "
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Code == AttachConfigError.UnknownKey
            && e.Key == "theme.custom.not_a_token"
            && e.Line == 2);
    }

    [Fact]
    public void Invalid_ui_accent_fails_with_line()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            accent = "not-a-color"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Code == AttachConfigError.InvalidValue
            && e.Key == "ui.accent"
            && e.Line == 2);
    }

    [Fact]
    public void Cyan_and_empty_ui_accent_bind()
    {
        var cyan = TomlAttachConfigBinder.Bind("""
            [ui]
            accent = "cyan"
            """);
        Assert.True(cyan.IsOk, cyan.IsOk ? "" : cyan.Error.ToString());
        Assert.Equal("cyan", cyan.Value.Ui.Accent);

        var empty = TomlAttachConfigBinder.Bind("""
            [ui]
            accent = ""
            """);
        Assert.True(empty.IsOk, empty.IsOk ? "" : empty.Error.ToString());
        Assert.Equal("", empty.Value.Ui.Accent);

        var hex = TomlAttachConfigBinder.Bind("""
            [ui]
            accent = "#112233"
            """);
        Assert.True(hex.IsOk, hex.IsOk ? "" : hex.Error.ToString());
        Assert.Equal("#112233", hex.Value.Ui.Accent);
    }

    [Fact]
    public void Theme_name_aliases_fail_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [theme]
            name = "catppuccin-mocha"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e => e.Key == "theme.name" && e.Line == 2);
    }

    [Fact]
    public void Invalid_theme_dark_and_light_names_include_line()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [theme]
            dark_name = "nope"
            light_name = "also-nope"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "theme.dark_name"
            && e.Line == 2);
        Assert.Contains(result.Errors, e =>
            e.Key == "theme.light_name"
            && e.Line == 3);
    }

    [Fact]
    public void Spaces_rows_by_agent_table_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui.sidebar.spaces.rows_by_agent]
            default = [["name"]]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key is "ui.sidebar.spaces.rows_by_agent" or "ui.sidebar.spaces.rows_by_agent.default"
            && e.Line is > 0);
    }

    [Fact]
    public void Spaces_rows_by_agent_inline_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui.sidebar.spaces]
            rows_by_agent = { default = [["name"]] }
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.sidebar.spaces.rows_by_agent.default"
            && e.Line is > 0);
    }

    [Fact]
    public void Invalid_chord_fails()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [keys]
            prefix = "ctrl+"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e => e.Key == "keys.prefix");
    }

    [Fact]
    public void Invalid_chord_in_array_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [keys]
            detach = ["prefix+q", "ctrl+"]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e => e.Key == "keys.detach");
    }

    [Fact]
    public void Invalid_ctrl_alt_array_chord_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [keys]
            focus_pane_left = ["prefix+h", "ctrl+alt+"]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e => e.Key == "keys.focus_pane_left");
    }

    [Fact]
    public void Comma_inside_single_string_is_not_a_multi_bind_delimiter()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [keys]
            detach = "prefix+q,ctrl+d"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e => e.Key == "keys.detach");
    }

    [Theory]
    [InlineData("switch_tab", "ctrl+1..99")]
    [InlineData("switch_workspace", "9..1")]
    [InlineData("focus_agent", "1..8")]
    public void Malformed_indexed_range_fails_closed(string key, string spec)
    {
        var result = TomlAttachConfigBinder.Bind($"""
            [keys]
            {key} = "{spec}"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "keys." + key
            && e.Message.Contains("invalid_range", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unset")]
    public void Empty_or_unset_prefix_fails_with_line_and_key(string prefix)
    {
        var result = TomlAttachConfigBinder.Bind($"""
            [keys]
            prefix = "{prefix}"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "keys.prefix"
            && e.Line is > 0
            && e.Message.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Repeated_ui_key_fails_closed_with_second_line()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            mouse_capture = false
            mouse_capture = true
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Code == AttachConfigError.InvalidToml
            && e.Line == 3
            && e.Message.Contains("ui.mouse_capture", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_assignment_path_fails_closed_with_second_line()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [keys]
            prefix = "ctrl+b"
            prefix = "ctrl+a"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Code == AttachConfigError.InvalidToml
            && e.Line == 3
            && e.Message.Contains("keys.prefix", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_table_name_fails_closed_with_second_line()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [session]
            name = "one"
            [session]
            name = "two"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Code == AttachConfigError.InvalidToml
            && e.Line == 3
            && e.Message.Contains("session", StringComparison.Ordinal));
    }

    [Fact]
    public void Dotted_then_table_same_path_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            keys.prefix = "ctrl+b"
            [keys]
            prefix = "ctrl+a"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Code == AttachConfigError.InvalidToml
            && e.Line is > 0);
    }

    [Fact]
    public void Table_then_value_collision_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [keys]
            prefix = "ctrl+b"
            [keys]
            detach = "prefix+d"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Code == AttachConfigError.InvalidToml
            && e.Line == 3
            && e.Message.Contains("keys", StringComparison.Ordinal));
    }

    [Fact]
    public void Tab_bar_right_unknown_type_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            tab_bar_right = [{ type = "clock" }]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.tab_bar_right"
            && e.Line is > 0
            && e.Message.Contains("type", StringComparison.Ordinal));
    }

    [Fact]
    public void Tab_bar_right_resource_type_requires_resource_field()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            tab_bar_right = [{ type = "resource" }]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.tab_bar_right"
            && e.Line is > 0
            && e.Code == AttachConfigError.InvalidValue
            && e.Message.Contains("resource", StringComparison.Ordinal));
        Assert.Contains(Enum.GetNames<TabBarRightKind>(), n =>
            n.Equals("Resource", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tab_bar_right_resource_shorthand_requires_inline_table()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            tab_bar_right = ["resource"]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.tab_bar_right"
            && e.Line is > 0
            && e.Code == AttachConfigError.InvalidValue
            && e.Message.Contains("resource", StringComparison.Ordinal));
    }

    [Fact]
    public void Tab_bar_right_resource_bind_parses()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            tab_bar_right = [{ type = "resource", resource = "plugin:atomic.workbench/queue", format = "$attention_count waiting", max_items = 1 }]
            """);
        Assert.True(result.IsOk, string.Join("; ", result.Errors.Select(e => e.Message)));
        var entry = Assert.Single(result.Value!.Ui.TabBarRight);
        Assert.Equal(TabBarRightKind.Resource, entry.Kind);
        Assert.Equal("plugin:atomic.workbench/queue", entry.Resource);
        Assert.Equal("$attention_count waiting", entry.Format);
        Assert.Equal(1, entry.MaxItems);
    }

    [Fact]
    public void Invalid_host_cursor_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            host_cursor = "blink"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.host_cursor"
            && e.Line is > 0
            && e.Code == AttachConfigError.InvalidValue
            && e.Message.Contains("auto", StringComparison.Ordinal)
            && e.Message.Contains("native", StringComparison.Ordinal)
            && e.Message.Contains("drawn", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_status_indicators_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            status_indicators = "icons"
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.status_indicators"
            && e.Line is > 0
            && e.Code == AttachConfigError.InvalidValue
            && e.Message.Contains("dots", StringComparison.Ordinal)
            && e.Message.Contains("symbols", StringComparison.Ordinal));
    }

    [Fact]
    public void Tab_bar_right_unknown_field_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            tab_bar_right = [{ type = "zoom", color = "red" }]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.tab_bar_right.color"
            && e.Line is > 0);
    }

    [Fact]
    public void Tab_bar_right_invalid_datetime_format_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            tab_bar_right = [{ type = "datetime", format = "Q" }]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.tab_bar_right"
            && e.Line is > 0
            && e.Message.Contains("format", StringComparison.Ordinal));
    }

    [Fact]
    public void Tab_bar_right_unknown_strftime_token_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            tab_bar_right = [{ type = "datetime", format = "%Q" }]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.tab_bar_right"
            && e.Line is > 0
            && e.Message.Contains("format", StringComparison.Ordinal));
    }

    [Fact]
    public void Tab_bar_right_dotnet_datetime_format_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            tab_bar_right = [{ type = "datetime", format = "HH:mm" }]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.tab_bar_right"
            && e.Line is > 0
            && e.Message.Contains("format", StringComparison.Ordinal));
    }

    [Fact]
    public void Tab_bar_right_text_string_shorthand_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            tab_bar_right = ["text"]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.tab_bar_right"
            && e.Line is > 0
            && e.Message.Contains("inline table", StringComparison.Ordinal));
    }

    [Fact]
    public void Sidebar_section_resource_unknown_and_duplicate_fail_closed()
    {
        var builtIn = TomlAttachConfigBinder.Bind("""
            [[ui.sidebar.section]]
            id = "agents"
            resource = "plugin:atomic.workbench/queue"
            """);
        Assert.Contains(builtIn.Errors, e => e.Code == AttachConfigError.SidebarSectionBuiltInResource);

        var missingResource = TomlAttachConfigBinder.Bind("""
            [[ui.sidebar.section]]
            id = "chrome"
            """);
        Assert.Contains(
            missingResource.Errors,
            e => e.Code == AttachConfigError.SidebarSectionPluginRequiresResource);

        var duplicate = TomlAttachConfigBinder.Bind("""
            [[ui.sidebar.section]]
            id = "atomic"
            resource = "plugin:atomic.workbench/queue"
            [[ui.sidebar.section]]
            id = "atomic"
            resource = "plugin:atomic.workbench/queue"
            """);
        Assert.Contains(duplicate.Errors, e => e.Code == AttachConfigError.SidebarSectionDuplicateId);
    }

    [Fact]
    public void Sidebar_min_greater_than_max_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui]
            sidebar_min_width = 30
            sidebar_max_width = 18
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e => e.Key == "ui.sidebar_min_width");
    }

    [Fact]
    public void Unknown_rows_by_agent_sibling_table_fails_closed()
    {
        var result = TomlAttachConfigBinder.Bind("""
            [ui.sidebar.agents.not_rows]
            claude = [["state_icon"]]
            """);
        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e =>
            e.Key == "ui.sidebar.agents.not_rows"
            && e.Code == AttachConfigError.UnknownSection);
    }

    [Fact]
    public void Hypa_config_path_missing_fails()
    {
        var loader = new FileAttachConfigLoader(
            new MapEnv(new Dictionary<string, string>
            {
                ["HYPA_CONFIG_PATH"] = "/tmp/h35-missing-config.toml",
            }, home: "/tmp/h35-home"),
            new MapFiles());
        var loaded = loader.Load();
        Assert.False(loaded.IsOk);
        Assert.Equal(AttachConfigError.MissingFile, loaded.Error.Code);
        Assert.Equal("/tmp/h35-missing-config.toml", loader.ResolvePath());
    }

    [Fact]
    public void Hypa_config_path_wins_over_xdg_and_home()
    {
        var files = new MapFiles();
        files.WriteAllText("/explicit/config.toml", """
            [session]
            name = "from-path"
            """);
        var loader = new FileAttachConfigLoader(
            new MapEnv(new Dictionary<string, string>
            {
                ["HYPA_CONFIG_PATH"] = "/explicit/config.toml",
                ["XDG_CONFIG_HOME"] = "/xdg",
            }, home: "/home/me"),
            files);
        Assert.Equal("/explicit/config.toml", loader.ResolvePath());
        var loaded = loader.Load();
        Assert.True(loaded.IsOk, loaded.IsOk ? "" : loaded.Error.ToString());
        Assert.Equal("from-path", loaded.Value.Session.Name);
    }

    [Fact]
    public void Xdg_config_home_resolves_hypa_config_toml()
    {
        var files = new MapFiles();
        files.WriteAllText("/xdg/hypa/config.toml", """
            [session]
            name = "from-xdg"
            """);
        var loader = new FileAttachConfigLoader(
            new MapEnv(new Dictionary<string, string>
            {
                ["XDG_CONFIG_HOME"] = "/xdg",
            }, home: "/home/me"),
            files);
        Assert.Equal("/xdg/hypa/config.toml", loader.ResolvePath());
        var loaded = loader.Load();
        Assert.True(loaded.IsOk, loaded.IsOk ? "" : loaded.Error.ToString());
        Assert.Equal("from-xdg", loaded.Value.Session.Name);
    }

    [Fact]
    public void Home_config_resolves_hypa_config_toml()
    {
        var files = new MapFiles();
        files.WriteAllText("/home/me/.config/hypa/config.toml", """
            [session]
            name = "from-home"
            """);
        var loader = new FileAttachConfigLoader(
            new MapEnv([], home: "/home/me"),
            files);
        Assert.Equal("/home/me/.config/hypa/config.toml", loader.ResolvePath());
        var loaded = loader.Load();
        Assert.True(loaded.IsOk, loaded.IsOk ? "" : loaded.Error.ToString());
        Assert.Equal("from-home", loaded.Value.Session.Name);
    }

    [Fact]
    public void Windows_appdata_resolves_hypa_config_toml()
    {
        var files = new MapFiles();
        var loader = new FileAttachConfigLoader(
            new MapEnv(
                [],
                home: @"C:\Users\me",
                appData: @"C:\Users\me\AppData\Roaming",
                isWindows: true),
            files);
        var path = loader.ResolvePath();
        Assert.Equal(Path.Combine(@"C:\Users\me\AppData\Roaming", "hypa", "config.toml"), path);
        files.WriteAllText(path, """
            [session]
            name = "from-appdata"
            """);
        var loaded = loader.Load();
        Assert.True(loaded.IsOk, loaded.IsOk ? "" : loaded.Error.ToString());
        Assert.Equal("from-appdata", loaded.Value.Session.Name);
    }

    [Fact]
    public void Missing_default_file_uses_built_ins()
    {
        var loader = new FileAttachConfigLoader(
            new MapEnv([], home: "/tmp/h35-missing-home"),
            new MapFiles());
        var loaded = loader.Load();
        Assert.True(loaded.IsOk, loaded.IsOk ? "" : loaded.Error.ToString());
        Assert.Equal(AttachClientConfig.Default.Session.Name, loaded.Value.Session.Name);
        Assert.Equal(AttachClientConfig.Default.Keys.Prefix.Specs, loaded.Value.Keys.Prefix.Specs);
        Assert.True(loaded.Value.Ui.MouseCapture);
    }

    [Fact]
    public void Relative_hypa_config_path_resolves_against_client_cwd()
    {
        var root = Path.Combine(Path.GetTempPath(), "h35-rel-" + Guid.NewGuid().ToString("N"));
        var clientCwd = Path.Combine(root, "client");
        Directory.CreateDirectory(clientCwd);
        var configPath = Path.Combine(clientCwd, "config.toml");
        File.WriteAllText(configPath, """
            [session]
            name = "from-client"
            """);

        var previousCwd = Directory.GetCurrentDirectory();
        var previousConfig = Environment.GetEnvironmentVariable(FileAttachConfigLoader.ConfigPathVariable);
        try
        {
            Directory.SetCurrentDirectory(clientCwd);
            Environment.SetEnvironmentVariable(FileAttachConfigLoader.ConfigPathVariable, "./config.toml");

            var loader = new FileAttachConfigLoader();
            Assert.Equal(Path.GetFullPath("./config.toml"), loader.ResolvePath());
            var loaded = loader.Load();
            Assert.True(loaded.IsOk, loaded.IsOk ? "" : loaded.Error.ToString());
            Assert.Equal("from-client", loaded.Value.Session.Name);
        }
        finally
        {
            try { Directory.SetCurrentDirectory(previousCwd); } catch { /* restore */ }
            Environment.SetEnvironmentVariable(FileAttachConfigLoader.ConfigPathVariable, previousConfig);
            try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public void Relative_xdg_config_home_resolves_against_client_cwd()
    {
        var root = Path.Combine(Path.GetTempPath(), "h35-xdg-" + Guid.NewGuid().ToString("N"));
        var clientCwd = Path.Combine(root, "client");
        var xdg = Path.Combine(clientCwd, "xdg");
        Directory.CreateDirectory(Path.Combine(xdg, "hypa"));
        File.WriteAllText(Path.Combine(xdg, "hypa", "config.toml"), """
            [session]
            name = "from-xdg-rel"
            """);

        var previousCwd = Directory.GetCurrentDirectory();
        var previousConfig = Environment.GetEnvironmentVariable(FileAttachConfigLoader.ConfigPathVariable);
        var previousXdg = Environment.GetEnvironmentVariable(FileAttachConfigLoader.XdgConfigHomeVariable);
        try
        {
            Directory.SetCurrentDirectory(clientCwd);
            Environment.SetEnvironmentVariable(FileAttachConfigLoader.ConfigPathVariable, null);
            Environment.SetEnvironmentVariable(FileAttachConfigLoader.XdgConfigHomeVariable, "./xdg");

            var loader = new FileAttachConfigLoader();
            Assert.Equal(Path.GetFullPath("./xdg/hypa/config.toml"), loader.ResolvePath());
            var loaded = loader.Load();
            Assert.True(loaded.IsOk, loaded.IsOk ? "" : loaded.Error.ToString());
            Assert.Equal("from-xdg-rel", loaded.Value.Session.Name);
        }
        finally
        {
            try { Directory.SetCurrentDirectory(previousCwd); } catch { /* restore */ }
            Environment.SetEnvironmentVariable(FileAttachConfigLoader.ConfigPathVariable, previousConfig);
            Environment.SetEnvironmentVariable(FileAttachConfigLoader.XdgConfigHomeVariable, previousXdg);
            try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ }
        }
    }

    [Fact]
    public void Relative_hypa_config_path_host_loads_client_file_after_cwd_change()
    {
        var root = Path.Combine(Path.GetTempPath(), "h35-split-" + Guid.NewGuid().ToString("N"));
        var clientCwd = Path.Combine(root, "client");
        var hostCwd = Path.Combine(root, "host");
        Directory.CreateDirectory(clientCwd);
        Directory.CreateDirectory(hostCwd);
        File.WriteAllText(Path.Combine(clientCwd, "config.toml"), """
            [session]
            name = "from-client"
            """);
        File.WriteAllText(Path.Combine(hostCwd, "config.toml"), """
            [session]
            name = "from-cwd"
            """);

        var previousCwd = Directory.GetCurrentDirectory();
        var previousConfig = Environment.GetEnvironmentVariable(FileAttachConfigLoader.ConfigPathVariable);
        try
        {
            Directory.SetCurrentDirectory(clientCwd);
            Environment.SetEnvironmentVariable(FileAttachConfigLoader.ConfigPathVariable, "./config.toml");

            var client = new FileAttachConfigLoader();
            var clientLoaded = client.Load();
            Assert.True(clientLoaded.IsOk, clientLoaded.IsOk ? "" : clientLoaded.Error.ToString());
            Assert.Equal("from-client", clientLoaded.Value.Session.Name);

            var childEnv = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [FileAttachConfigLoader.ConfigPathVariable] = "./config.toml",
            };
            FileAttachConfigLoader.PinResolvedPaths(childEnv);
            var pinned = childEnv[FileAttachConfigLoader.ConfigPathVariable];
            Assert.Equal(Path.GetFullPath("./config.toml"), pinned);

            Directory.SetCurrentDirectory(hostCwd);
            Environment.SetEnvironmentVariable(FileAttachConfigLoader.ConfigPathVariable, pinned);

            var host = new FileAttachConfigLoader();
            var hostLoaded = host.Load();
            Assert.True(hostLoaded.IsOk, hostLoaded.IsOk ? "" : hostLoaded.Error.ToString());
            Assert.Equal("from-client", hostLoaded.Value.Session.Name);
            Assert.Equal(pinned, host.ResolvePath());
        }
        finally
        {
            try { Directory.SetCurrentDirectory(previousCwd); } catch { /* restore */ }
            Environment.SetEnvironmentVariable(FileAttachConfigLoader.ConfigPathVariable, previousConfig);
            try { Directory.Delete(root, recursive: true); } catch { /* cleanup */ }
        }
    }

    private sealed class MapEnv(
        Dictionary<string, string> vars,
        string home,
        string? appData = null,
        bool isWindows = false) : IAttachConfigEnvironment
    {
        public string? GetVariable(string name) =>
            vars.TryGetValue(name, out var value) ? value : null;

        public string UserHome { get; } = home;
        public string? AppData { get; } = appData;
        public bool IsWindows { get; } = isWindows;
        public bool IsMacOs => false;
    }

    private sealed class MapFiles : IAttachConfigFiles
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public bool FileExists(string path) => _files.ContainsKey(path);

        public string ReadAllText(string path) => _files[path];

        public void WriteAllText(string path, string contents) => _files[path] = contents;

        public void CopyFile(string source, string destination) => _files[destination] = _files[source];
    }
}
