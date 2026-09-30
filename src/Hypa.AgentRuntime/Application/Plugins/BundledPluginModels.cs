namespace Hypa.AgentRuntime.Application.Plugins;

public interface IBundledPluginService
{
    PluginResult<BundledPluginStageResult> StageAnnotate(string executablePath);

    PluginResult<BundledPluginRemoveResult> RemoveAnnotate();

    BundledPluginDiskStatus InspectAnnotate();

    PluginResult<BundledPluginStageResult> StageSlots();

    PluginResult<BundledPluginRemoveResult> RemoveSlots();

    BundledPluginDiskStatus InspectSlots();
}

public sealed record BundledPluginStageResult
{
    public required string PluginId { get; init; }

    public required string PluginRoot { get; init; }

    public required string ManifestPath { get; init; }

    public required string Command0 { get; init; }
}

public sealed record BundledPluginRemoveResult
{
    public required string PluginId { get; init; }

    public required string PluginRoot { get; init; }

    public required bool Removed { get; init; }
}

public sealed record BundledPluginDiskStatus
{
    public required string PluginId { get; init; }

    public required string PluginRoot { get; init; }

    public required string ManifestPath { get; init; }

    public required bool Staged { get; init; }
}
