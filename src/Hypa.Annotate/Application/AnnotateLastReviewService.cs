using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Review popup save and cancel. Save sends review text with <c>plugin.pane.send_text</c>.
/// </summary>
public static class AnnotateLastReviewService
{
    public static async Task<Result<int, string>> RunAsync(
        AnnotateLastReviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pendingPath = request.PendingPath;
        if (string.IsNullOrWhiteSpace(pendingPath))
            return Result<int, string>.Fail("Missing pending last review");

        var loaded = PendingLastReviewFiles.ReadPending(pendingPath);
        if (!loaded.IsOk)
            return Result<int, string>.Fail(loaded.Error);

        _ = PendingLastReviewFiles.RemovePending(pendingPath);

        var session = request.ReviewSession.Run(loaded.Value);
        if (session.Outcome == AnnotateLastReviewOutcome.Failed)
            return Result<int, string>.Ok(1);
        if (session.Outcome == AnnotateLastReviewOutcome.Cancelled)
            return Result<int, string>.Ok(0);

        var comment = AnnotationParser.JavascriptTrim(session.Comment ?? string.Empty);
        if (comment.Length == 0)
            return Result<int, string>.Fail("Write a review before sending.");

        var sent = await request.PaneSender.SendTextAsync(
            new PluginPaneSendTextRequest
            {
                TargetPaneId = loaded.Value.TargetPaneId,
                Text = comment,
            },
            cancellationToken).ConfigureAwait(false);

        return sent.IsOk
            ? Result<int, string>.Ok(0)
            : Result<int, string>.Fail(sent.Error);
    }
}

public sealed record AnnotateLastReviewRequest
{
    public string? PendingPath { get; init; }

    public required IAnnotateLastReviewSession ReviewSession { get; init; }

    public required IPluginPaneSender PaneSender { get; init; }
}
