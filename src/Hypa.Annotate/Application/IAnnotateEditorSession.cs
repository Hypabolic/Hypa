using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Lite editor I/O for the annotate popup pane.
/// </summary>
public interface IAnnotateEditorSession
{
    AnnotateEditorSessionResult Run(PendingAnnotation pending);
}

public enum AnnotateEditorOutcome
{
    Saved,
    Cancelled,
}

public sealed record AnnotateEditorSessionResult
{
    public required AnnotateEditorOutcome Outcome { get; init; }

    public string? Comment { get; init; }
}
