using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Lite review I/O for the last-message popup pane.
/// </summary>
public interface IAnnotateLastReviewSession
{
    AnnotateLastReviewSessionResult Run(PendingLastReview pending);
}

public enum AnnotateLastReviewOutcome
{
    Sent,
    Cancelled,
    Failed,
}

public sealed record AnnotateLastReviewSessionResult
{
    public required AnnotateLastReviewOutcome Outcome { get; init; }

    public string? Comment { get; init; }
}
