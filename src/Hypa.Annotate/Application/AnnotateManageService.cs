using Hypa.Annotate.Domain;

namespace Hypa.Annotate.Application;

/// <summary>
// / Open the manager popup pane.
/// </summary>
public static class AnnotateManageService
{
    public static async Task<Result<Unit, string>> RunAsync(
        AnnotateManageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await request.PaneOpener.OpenManagerPopupAsync(
            new PluginManagerPaneOpenRequest
            {
                PluginRoot = request.PluginRoot,
                TargetPaneId = request.TargetPaneId,
            },
            cancellationToken).ConfigureAwait(false);
    }
}

public sealed record AnnotateManageRequest
{
    public required string PluginRoot { get; init; }

    public required string TargetPaneId { get; init; }

    public required IPluginPaneOpener PaneOpener { get; init; }
}
