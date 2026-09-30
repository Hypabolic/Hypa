using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// <c>last</c> dispatch: resolve Pi path-kind transcript, write pending, open review popup.
/// </summary>
public static class AnnotateLastService
{
    public static async Task<Result<AnnotateLastCompletion, string>> RunAsync(
        AnnotateLastRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = AgentSessionContextParser.Parse(request.ContextJson);
        if (session is null)
            return Result<AnnotateLastCompletion, string>.Fail(AnnotateLastMessages.NoAgentSession);

        if (!string.Equals(session.Kind, AgentSessionContextParser.KindPath, StringComparison.Ordinal))
            return Result<AnnotateLastCompletion, string>.Fail(AnnotateLastMessages.UnsupportedAgent);

        if (!string.Equals(session.Agent, "pi", StringComparison.Ordinal))
            return Result<AnnotateLastCompletion, string>.Fail(AnnotateLastMessages.UnsupportedAgent);

        var message = PiTranscriptReader.ReadLastVisibleAssistantMessage(session.Value);
        if (!message.IsOk)
            return Result<AnnotateLastCompletion, string>.Fail(message.Error);

        var pending = new PendingLastReview
        {
            AgentMessage = message.Value,
            TargetPaneId = request.TargetPaneId,
            Context = CaptureContextParser.Parse(request.ContextJson),
            CapturedAt = request.NowIso(),
        };

        var pendingPath = PendingLastReviewFiles.WritePending(request.StateDirectory, pending);
        if (!pendingPath.IsOk)
            return Result<AnnotateLastCompletion, string>.Fail(pendingPath.Error);

        var opened = await request.PaneOpener.OpenLastReviewPopupAsync(
            new PluginPaneOpenRequest
            {
                PluginRoot = request.PluginRoot,
                TargetPaneId = request.TargetPaneId,
                PendingFilePath = pendingPath.Value,
            },
            cancellationToken).ConfigureAwait(false);

        if (!opened.IsOk)
        {
            _ = PendingLastReviewFiles.RemovePending(pendingPath.Value);
            return Result<AnnotateLastCompletion, string>.Fail(opened.Error);
        }

        return Result<AnnotateLastCompletion, string>.Ok(AnnotateLastCompletion.ReviewOpened);
    }
}

public enum AnnotateLastCompletion
{
    ReviewOpened,
}

public sealed record AnnotateLastRequest
{
    public required string StateDirectory { get; init; }

    public required string PluginRoot { get; init; }

    public required string TargetPaneId { get; init; }

    public string? ContextJson { get; init; }

    public required IPluginPaneOpener PaneOpener { get; init; }

    public Func<string> NowIso { get; init; } = AnnotateTimestamps.NowIso;
}
