using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
// / Annotation-manager copy transition.
/// </summary>
public static class AnnotateManagerCopy
{
    public static AnnotateManagerCopyOutcome CopyAnnotations(
        IReadOnlyList<Annotation> annotations,
        Func<string, Result<bool, string>> writeClipboard)
    {
        if (annotations.Count == 0)
        {
            return new AnnotateManagerCopyOutcome
            {
                Close = false,
                Message = "Nothing to copy.",
            };
        }

        var markdown = AnnotationMarkdown.Format(annotations);
        var copied = writeClipboard(markdown);
        if (!copied.IsOk)
        {
            return new AnnotateManagerCopyOutcome
            {
                Close = false,
                Message = copied.Error,
            };
        }

        return new AnnotateManagerCopyOutcome { Close = true };
    }
}

public sealed record AnnotateManagerCopyOutcome
{
    public bool Close { get; init; }

    public string? Message { get; init; }
}
