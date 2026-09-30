using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
// / Copy saved annotations as Markdown.
/// <c>copy_context</c>.
/// </summary>
public static class AnnotateCopyContext
{
    public const string EmptyTitle = "No annotations";

    public const string EmptyBody = "There is nothing to copy yet.";

    public const string CopiedTitle = "Annotations copied";

    public static Result<CopyContextOutcome, string> Run(
        string stateDirectory,
        IClipboardWriter clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);

        var loaded = AnnotationStore.LoadAnnotations(stateDirectory);
        if (!loaded.IsOk)
            return Result<CopyContextOutcome, string>.Fail(loaded.Error);

        var annotations = AnnotationStore.NewestFirst(loaded.Value);
        if (annotations.Count == 0)
            return Result<CopyContextOutcome, string>.Ok(CopyContextOutcome.Empty);

        var markdown = AnnotationMarkdown.Format(annotations);
        var copied = clipboard.TryWrite(markdown);
        if (!copied.IsOk)
            return Result<CopyContextOutcome, string>.Fail(copied.Error);

        return Result<CopyContextOutcome, string>.Ok(new CopyContextOutcome
        {
            Kind = CopyContextKind.Copied,
            Count = annotations.Count,
        });
    }

    public static string CopiedBody(int count)
    {
        var noun = count == 1 ? "annotation" : "annotations";
        return $"{count} {noun} copied as Markdown.";
    }
}

public enum CopyContextKind
{
    Empty,
    Copied,
}

public sealed record CopyContextOutcome
{
    public CopyContextKind Kind { get; init; }

    public int Count { get; init; }

    public static CopyContextOutcome Empty { get; } = new() { Kind = CopyContextKind.Empty };
}
