using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.WindowTitle;

namespace Hypa.Cli.Attach.Chrome;

/// <summary>Applies OSC 2 to the outer host title. Empty template skips OSC.</summary>
public static class WindowTitleApplier
{
    /// <summary>Clears OSC 2. 1049l does not restore the host title.</summary>
    public static string ClearSequence { get; } = Osc2("");

    public static string Osc2(string title)
    {
        var safe = SafeDisplayText.Clip(SafeDisplayText.Encode(title), WindowTitleExpander.MaxCols);
        return "\u001b]2;" + safe + "\u0007";
    }

    public static string? Resolve(
        string? overrideTitle,
        string? template,
        WindowTitleValues values)
    {
        if (!string.IsNullOrEmpty(overrideTitle))
            return SafeDisplayText.Clip(SafeDisplayText.Encode(overrideTitle), WindowTitleExpander.MaxCols);
        if (string.IsNullOrEmpty(template))
            return null;
        var expanded = WindowTitleExpander.Expand(template, values);
        return expanded.Length == 0 ? null : expanded;
    }

    public static string? Sequence(
        string? overrideTitle,
        string? template,
        WindowTitleValues values)
    {
        var title = Resolve(overrideTitle, template, values);
        return title is null ? null : Osc2(title);
    }
}
