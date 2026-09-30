using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Application.Sidebar;

/// <summary>Validates and renders sidebar row tokens. No Cli types.</summary>
public static class SidebarTokenGrammar
{
    public const int MaxRows = 16;
    public const int MaxTokensPerRow = 16;

    public const string Working = "working";
    public const string Blocked = "blocked";
    public const string Idle = "idle";
    public const string Done = "done";
    public const string Unknown = "unknown";

    public const string AgentsId = "agents";
    public const string SpacesId = "spaces";
    public const string CubesId = "cubes";
    public const string CubesEmptyText = "No cubes";
    public const string CubesUnavailableText = "Cubes unavailable";

    public static IReadOnlyList<string> BuiltInSectionIds { get; } =
    [
        AgentsId,
        SpacesId,
        CubesId,
    ];

    public static IReadOnlyList<string> BuiltInTokens { get; } =
    [
        "state_icon",
        "state_text",
        "machine",
        "workspace",
        "tab",
        "pane",
        "agent",
        "terminal_title",
        "terminal_title_stripped",
        "branch",
        "git_status",
        "name",
        "kind",
        "reachability",
        "work_title",
    ];

    private static readonly HashSet<string> BuiltIn = new(BuiltInTokens, StringComparer.Ordinal);

    public static bool IsBuiltInSection(string? id) =>
        !string.IsNullOrEmpty(id)
        && (id == AgentsId || id == SpacesId || id == CubesId);

    public static bool IsBuiltIn(string token) =>
        !string.IsNullOrEmpty(token) && BuiltIn.Contains(token);

    public static bool IsCustom(string token) =>
        !string.IsNullOrEmpty(token) && token[0] == '$';

    public static bool IsKnown(string token) => IsBuiltIn(token) || IsCustom(token);

    public static string CanonicalState(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Unknown;
        return raw.Trim().ToLowerInvariant() switch
        {
            Working => Working,
            Blocked => Blocked,
            Idle => Idle,
            Done => Done,
            _ => Unknown,
        };
    }

    public static string StateText(string? raw) => CanonicalState(raw);

    public static string StateIcon(string? raw, StatusIndicatorStyle style)
    {
        var state = CanonicalState(raw);
        if (style is StatusIndicatorStyle.Symbols)
        {
            return state switch
            {
                Working => "*",
                Blocked => "!",
                Idle => "-",
                Done => "+",
                _ => "?",
            };
        }

        return state switch
        {
            Working => "●",
            Blocked => "◆",
            Idle => "○",
            Done => "●",
            _ => "·",
        };
    }

    public static int PriorityRank(string? raw) =>
        CanonicalState(raw) switch
        {
            Blocked => 0,
            Working => 1,
            Idle => 2,
            Done => 3,
            _ => 4,
        };

    public static AttachConfigError? ValidateRows(
        IReadOnlyList<IReadOnlyList<SidebarTokenSpec>>? rows,
        string key,
        int? line = null)
    {
        if (rows is null)
            return null;
        if (rows.Count > MaxRows)
        {
            return AttachConfigError.Value(
                key,
                $"{key} accepts at most {MaxRows} rows.",
                line);
        }

        foreach (var row in rows)
        {
            if (row is null)
                continue;
            if (row.Count > MaxTokensPerRow)
            {
                return AttachConfigError.Value(
                    key,
                    $"{key} accepts at most {MaxTokensPerRow} tokens per row.",
                    line);
            }

            foreach (var token in row)
            {
                var id = token.Id;
                if (string.IsNullOrEmpty(id) || IsKnown(id))
                {
                    if (token.Rules.Count > 16)
                    {
                        return AttachConfigError.Value(
                            key,
                            "sidebar tokens may contain at most 16 rules",
                            line);
                    }

                    if (token.Rules.Count > 0 && id is "state_icon" or "git_status")
                    {
                        return AttachConfigError.Value(
                            key,
                            "sidebar rules require a text-valued token",
                            line);
                    }

                    continue;
                }

                return AttachConfigError.Value(
                    key,
                    $"{key} token '{id}' is not a built-in token and is not a $custom token.",
                    line);
            }
        }

        return null;
    }

    public static string RenderRow(
        IReadOnlyList<string> tokens,
        SidebarTokenValues values,
        int maxCols = 64) =>
        RenderRow(ToSpecs(tokens), values, maxCols);

    public static string RenderRow(
        IReadOnlyList<SidebarTokenSpec> tokens,
        SidebarTokenValues values,
        int maxCols = 64)
    {
        if (tokens is null || tokens.Count == 0)
            return "";
        var rendered = FitVisibleTokens(TokensForRow(tokens, values), maxCols);
        if (rendered.Count == 0)
            return "";

        var joined = new System.Text.StringBuilder();
        for (var i = 0; i < rendered.Count; i++)
        {
            if (i > 0)
                joined.Append(Separator(rendered[i - 1].Id, rendered[i].Id));
            joined.Append(rendered[i].Text);
        }

        return SafeDisplayText.Clip(joined.ToString(), maxCols);
    }

    public static string Separator(string previousId, string currentId)
    {
        if (previousId == "state_icon" || currentId == "git_status")
            return " ";
        return " · ";
    }

    public static IReadOnlyList<SidebarRowToken> TokensForRow(
        IReadOnlyList<string> tokens,
        SidebarTokenValues values) =>
        TokensForRow(ToSpecs(tokens), values);

    public static IReadOnlyList<SidebarRowToken> TokensForRow(
        IReadOnlyList<SidebarTokenSpec> tokens,
        SidebarTokenValues values)
    {
        if (tokens is null || tokens.Count == 0)
            return [];
        var parts = new List<SidebarRowToken>(tokens.Count);
        foreach (var token in tokens)
        {
            var text = Resolve(token.Id, values);
            if (text.Length == 0)
                continue;
            parts.Add(new SidebarRowToken(token.Id, text, token.StyleForValue(text)));
        }

        return parts;
    }

    private static IReadOnlyList<SidebarTokenSpec> ToSpecs(IReadOnlyList<string>? tokens)
    {
        if (tokens is null || tokens.Count == 0)
            return [];
        var specs = new SidebarTokenSpec[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
            specs[i] = tokens[i];
        return specs;
    }

    /// <summary>
    /// Fixed tokens (state_icon, git_status) stay. Flexible tokens need one
    /// column to stay, then share leftover width. Never drops the indent
    /// prefix; that is not a token.
    /// </summary>
    public static IReadOnlyList<SidebarRowToken> FitVisibleTokens(
        IReadOnlyList<SidebarRowToken> tokens,
        int maxWidth)
    {
        if (tokens is null || tokens.Count == 0)
            return [];
        if (maxWidth < 1)
            return [];

        var fixedW = new int[tokens.Count];
        var flexW = new int[tokens.Count];
        for (var i = 0; i < tokens.Count; i++)
        {
            var width = SafeDisplayText.Width(tokens[i].Text);
            if (IsFlexibleToken(tokens[i].Id))
                flexW[i] = width;
            else
                fixedW[i] = width;
        }

        var active = new bool[tokens.Count];
        Array.Fill(active, true);
        if (MinimumWidth(tokens, fixedW, flexW, active) > maxWidth)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (flexW[i] > 0)
                    active[i] = false;
            }

            for (var i = tokens.Count - 1; i >= 0; i--)
            {
                if (flexW[i] == 0)
                    continue;
                active[i] = true;
                if (MinimumWidth(tokens, fixedW, flexW, active) > maxWidth)
                    active[i] = false;
            }
        }

        var visible = new List<int>(tokens.Count);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (active[i])
                visible.Add(i);
        }

        if (visible.Count == 0)
            return [];

        var separatorWidth = 0;
        for (var i = 1; i < visible.Count; i++)
        {
            separatorWidth += SafeDisplayText.Width(
                Separator(tokens[visible[i - 1]].Id, tokens[visible[i]].Id));
        }

        var fixedWidth = 0;
        foreach (var i in visible)
            fixedWidth += fixedW[i];

        var budgets = new int[tokens.Count];
        var minimum = 0;
        foreach (var i in visible)
        {
            if (flexW[i] > 0)
            {
                budgets[i] = 1;
                minimum++;
            }
        }

        var remaining = maxWidth - separatorWidth - fixedWidth - minimum;
        if (remaining < 0)
            remaining = 0;
        while (remaining > 0)
        {
            var grew = false;
            foreach (var i in visible)
            {
                if (budgets[i] > 0 && budgets[i] < flexW[i])
                {
                    budgets[i]++;
                    remaining--;
                    grew = true;
                    if (remaining == 0)
                        break;
                }
            }

            if (!grew)
                break;
        }

        var kept = new List<SidebarRowToken>(visible.Count);
        foreach (var i in visible)
        {
            var token = tokens[i];
            if (flexW[i] > 0)
            {
                // Width 1
                // of a wide grapheme is "…", not an omitted token.
                token = token with { Text = SafeDisplayText.TruncateEnd(token.Text, budgets[i]) };
            }

            kept.Add(token);
        }

        return kept;
    }

    private static bool IsFlexibleToken(string id) =>
        id is not "state_icon" and not "git_status";

    private static int MinimumWidth(
        IReadOnlyList<SidebarRowToken> tokens,
        int[] fixedW,
        int[] flexW,
        bool[] active)
    {
        var total = 0;
        string? previousId = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!active[i])
                continue;
            var content = fixedW[i] + (flexW[i] > 0 ? 1 : 0);
            if (content <= 0 && previousId is null)
                continue;
            if (previousId is not null)
                total += SafeDisplayText.Width(Separator(previousId, tokens[i].Id));
            total += content;
            previousId = tokens[i].Id;
        }

        return total;
    }

    public static string Resolve(string token, SidebarTokenValues values)
    {
        if (string.IsNullOrEmpty(token))
            return "";
        if (IsCustom(token))
        {
            var name = token[1..];
            if (values.Custom is not null && values.Custom.TryGetValue(name, out var custom))
                return SafeDisplayText.Encode(custom);
            return "";
        }

        return token switch
        {
            "state_icon" => values.StateIcon,
            "state_text" => values.StateText,
            "machine" => SafeDisplayText.Encode(values.Machine),
            "workspace" => SafeDisplayText.Encode(values.Workspace),
            "tab" => SafeDisplayText.Encode(values.Tab),
            "pane" => SafeDisplayText.Encode(values.Pane),
            "agent" => SafeDisplayText.Encode(values.Agent),
            "terminal_title" => SafeDisplayText.Encode(values.TerminalTitle),
            "terminal_title_stripped" => SafeDisplayText.Encode(values.TerminalTitleStripped),
            "branch" => SafeDisplayText.Encode(values.Branch),
            "git_status" => SafeDisplayText.Encode(values.GitStatus),
            "name" => SafeDisplayText.Encode(values.Name),
            "kind" => SafeDisplayText.Encode(values.Kind),
            "reachability" => SafeDisplayText.Encode(values.Reachability),
            "work_title" => SafeDisplayText.Encode(values.WorkTitle),
            _ => "",
        };
    }

    public static IReadOnlyList<SidebarPaneItem> SortAgents(
        IReadOnlyList<SidebarPaneItem> panes,
        IReadOnlyList<SidebarWorkspaceItem> workspaces,
        IReadOnlyList<SidebarTabItem> tabs,
        AgentPanelSort sort)
    {
        var workspaceOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < workspaces.Count; i++)
            workspaceOrder[workspaces[i].Id] = workspaces[i].Order != 0 ? workspaces[i].Order : i;

        var tabOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tab in tabs)
            tabOrder[tab.Id] = tab.Ordinal;

        IEnumerable<SidebarPaneItem> occupants = panes.Where(p => !IsOrdinaryShell(p));
        if (sort is AgentPanelSort.Priority)
        {
            return occupants
                .OrderBy(p => PriorityRank(p.State))
                .ThenBy(p => workspaceOrder.GetValueOrDefault(p.WorkspaceId, int.MaxValue))
                .ThenBy(p => tabOrder.GetValueOrDefault(p.TabId, int.MaxValue))
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .ToArray();
        }

        return occupants
            .OrderBy(p => workspaceOrder.GetValueOrDefault(p.WorkspaceId, int.MaxValue))
            .ThenBy(p => tabOrder.GetValueOrDefault(p.TabId, int.MaxValue))
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// has no agent name and no agent kind. Hypa stores kind on
    /// <see cref="SidebarPaneItem.Agent"/>. An empty kind or the detector
    /// idle-promote <c>shell</c> is an ordinary shell. Unknown real kinds
    /// stay visible.
    /// </summary>
    public static bool IsOrdinaryShell(SidebarPaneItem pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return IsOrdinaryShellKind(pane.Agent);
    }

    public static bool IsOrdinaryShellKind(string? agent)
    {
        if (string.IsNullOrWhiteSpace(agent))
            return true;
        return string.Equals(agent.Trim(), "shell", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<IReadOnlyList<SidebarTokenSpec>> RowsForAgent(
        AttachSidebarSectionConfig agents,
        string? canonicalId)
    {
        if (!string.IsNullOrWhiteSpace(canonicalId)
            && agents.RowsByAgent.TryGetValue(canonicalId, out var overrideRows)
            && overrideRows.Count > 0)
        {
            return overrideRows;
        }

        return agents.Rows;
    }

    public static string CubeKindText(SidebarCubeKind kind) => kind switch
    {
        SidebarCubeKind.Local => "local",
        SidebarCubeKind.Peer => "peer",
        SidebarCubeKind.Cube => "cube",
        _ => "",
    };

    public static string CubeReachabilityText(SidebarCubeReachability reachability) => reachability switch
    {
        SidebarCubeReachability.Local => "local",
        SidebarCubeReachability.Reachable => "reachable",
        SidebarCubeReachability.Unreachable => "unreachable",
        SidebarCubeReachability.Asleep => "asleep",
        _ => "",
    };
}

public sealed record SidebarTokenValues
{
    public string StateIcon { get; init; } = "";
    public string StateText { get; init; } = "";
    public string Machine { get; init; } = "";
    public string Workspace { get; init; } = "";
    public string Tab { get; init; } = "";
    public string Pane { get; init; } = "";
    public string Agent { get; init; } = "";
    public string TerminalTitle { get; init; } = "";
    public string TerminalTitleStripped { get; init; } = "";
    public string Branch { get; init; } = "";
    public string GitStatus { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Reachability { get; init; } = "";
    public string WorkTitle { get; init; } = "";
    public IReadOnlyDictionary<string, string>? Custom { get; init; }
}
