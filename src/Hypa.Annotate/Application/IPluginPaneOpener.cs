using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
/// Open a plugin popup pane through the mux control plane.
/// </summary>
public interface IPluginPaneOpener
{
    Task<Result<Unit, string>> OpenEditorPopupAsync(PluginPaneOpenRequest request, CancellationToken cancellationToken);

    Task<Result<Unit, string>> OpenLastReviewPopupAsync(
        PluginPaneOpenRequest request,
        CancellationToken cancellationToken);

    Task<Result<Unit, string>> OpenManagerPopupAsync(
        PluginManagerPaneOpenRequest request,
        CancellationToken cancellationToken);
}

public readonly record struct Unit;

public sealed record PluginPaneOpenRequest
{
    public required string PluginRoot { get; init; }

    public required string TargetPaneId { get; init; }

    public required string PendingFilePath { get; init; }
}

public sealed record PluginManagerPaneOpenRequest
{
    public required string PluginRoot { get; init; }

    public required string TargetPaneId { get; init; }
}
