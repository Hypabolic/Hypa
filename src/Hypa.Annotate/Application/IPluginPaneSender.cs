using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Send text to a pane through <c>plugin.pane.send_text</c>.
/// </summary>
public interface IPluginPaneSender
{
    Task<Result<Unit, string>> SendTextAsync(
        PluginPaneSendTextRequest request,
        CancellationToken cancellationToken);
}

public sealed record PluginPaneSendTextRequest
{
    public required string TargetPaneId { get; init; }

    /// <summary>Review text without a trailing newline. The host appends one.</summary>
    public required string Text { get; init; }
}
