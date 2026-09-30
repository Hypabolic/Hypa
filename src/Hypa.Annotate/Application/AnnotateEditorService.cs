using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
// / Editor save and cancel.
/// </summary>
public static class AnnotateEditorService
{
    public static Result<int, string> Run(AnnotateEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pendingPath = request.PendingPath;
        if (string.IsNullOrWhiteSpace(pendingPath))
            return Result<int, string>.Fail("Missing pending annotation");

        var loaded = PendingAnnotationFiles.ReadPending(pendingPath);
        if (!loaded.IsOk)
            return Result<int, string>.Fail(loaded.Error);

        _ = PendingAnnotationFiles.RemovePending(pendingPath);

        var session = request.EditorSession.Run(loaded.Value);
        if (session.Outcome == AnnotateEditorOutcome.Cancelled)
            return Result<int, string>.Ok(0);

        var comment = AnnotationParser.JavascriptTrim(session.Comment ?? string.Empty);
        if (comment.Length == 0)
            return Result<int, string>.Fail("Write a comment before saving.");

        var annotation = new Annotation
        {
            SelectedText = loaded.Value.SelectedText,
            CapturedAt = loaded.Value.CapturedAt,
            Context = loaded.Value.Context,
            Id = request.NewId(),
            Comment = comment,
            CreatedAt = request.NowIso(),
        };

        var saved = AnnotationStore.AppendAnnotation(request.StateDirectory, annotation);
        return saved.IsOk
            ? Result<int, string>.Ok(0)
            : Result<int, string>.Fail(saved.Error);
    }
}

public sealed record AnnotateEditorRequest
{
    public required string StateDirectory { get; init; }

    public string? PendingPath { get; init; }

    public required IAnnotateEditorSession EditorSession { get; init; }

    public Func<string> NewId { get; init; } = () => Guid.NewGuid().ToString();

    public Func<string> NowIso { get; init; } = AnnotateTimestamps.NowIso;
}
