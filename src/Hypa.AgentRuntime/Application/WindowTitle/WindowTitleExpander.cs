namespace Hypa.AgentRuntime.Application.WindowTitle;

public sealed record WindowTitleValues
{
    public string Hostname { get; init; } = "";

    public string Workspace { get; init; } = "";

    public string Tab { get; init; } = "";

    public string Pane { get; init; } = "";

    public string TerminalTitle { get; init; } = "";
}

/// <summary>
/// Expands <c>ui.window_title</c> tokens. Strips controls and caps length.
/// Never emits OSC from token values.
/// </summary>
public static class WindowTitleExpander
{
    public const int MaxCols = 200;

    public const string DefaultTemplate = "{hostname}: {workspace}";

    public static string Expand(string? template, WindowTitleValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (template is null || template.Length == 0)
            return "";

        var hostname = Safe(values.Hostname);
        var workspace = Safe(values.Workspace);
        var tab = Safe(values.Tab);
        var pane = Safe(values.Pane);
        var title = Safe(values.TerminalTitle);
        var expanded = template
            .Replace("{hostname}", hostname, StringComparison.Ordinal)
            .Replace("{workspace}", workspace, StringComparison.Ordinal)
            .Replace("{tab}", tab, StringComparison.Ordinal)
            .Replace("{pane}", pane, StringComparison.Ordinal)
            .Replace("{terminal_title}", title, StringComparison.Ordinal);
        return SafeDisplayText.Clip(SafeDisplayText.Encode(expanded), MaxCols);
    }

    private static string Safe(string? value) => SafeDisplayText.Encode(value);
}
