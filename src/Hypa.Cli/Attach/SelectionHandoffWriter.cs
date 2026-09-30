using Hypa.Cli.Attach.Copy;
using Hypa.Cli.Attach.Mouse;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Cli.Attach;

/// <summary>
/// Writes attach selection to the annotate handoff file.
/// </summary>
public static class SelectionHandoffWriter
{
    public static Result<Unit, string> WriteCurrentSelection(
        CopyModeSession copy,
        MouseSelection mouse,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(mouse);
        return SelectionHandoffFiles.Write(ExtractSelection(copy, mouse), filePath);
    }

    internal static string ExtractSelection(
        CopyModeSession copy,
        MouseSelection mouse,
        string? prefilledCopyText = null)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(mouse);

        if (!string.IsNullOrEmpty(prefilledCopyText)
            && !SelectionHandoffFiles.IsBlank(prefilledCopyText))
        {
            return prefilledCopyText;
        }

        var fromCopy = copy.ExtractSelection();
        if (!SelectionHandoffFiles.IsBlank(fromCopy))
            return fromCopy;

        return mouse.Extract();
    }
}
