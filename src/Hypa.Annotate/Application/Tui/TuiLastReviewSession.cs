using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application.Tui;

/// <summary>
// / Last-review popup. Send only after Ctrl+S.
/// </summary>
public sealed class TuiLastReviewSession : IAnnotateLastReviewSession
{
    public AnnotateLastReviewSessionResult Run(PendingLastReview pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var app = new AnnotateEditorApp(new PendingAnnotation
        {
            SelectedText = pending.AgentMessage,
            CapturedAt = pending.CapturedAt,
            Context = pending.Context,
        })
        {
            SelectionTitle = "Agent message",
            CommentTitle = "Review",
            Hint = "Ctrl+S send  ·  Esc cancel  ·  Enter new line",
            EmptyCommentMessage = "Write a review before sending.",
        };
        var code = PluginTuiLoop.RunEditor(app, stateDirectory: "", saveToStore: false);
        if (code != 0)
        {
            return new AnnotateLastReviewSessionResult
            {
                Outcome = AnnotateLastReviewOutcome.Failed,
            };
        }

        if (app.Quit && !app.Accepted)
        {
            return new AnnotateLastReviewSessionResult
            {
                Outcome = AnnotateLastReviewOutcome.Cancelled,
            };
        }

        if (!app.Accepted || !app.TryAcceptComment(out var comment))
        {
            return new AnnotateLastReviewSessionResult
            {
                Outcome = AnnotateLastReviewOutcome.Cancelled,
            };
        }

        return new AnnotateLastReviewSessionResult
        {
            Outcome = AnnotateLastReviewOutcome.Sent,
            Comment = comment,
        };
    }
}
