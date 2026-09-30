namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Optional export hooks for checkpoint tests. Production uses <see cref="NoOpCheckpointExportHooks"/>.
/// </summary>
public interface ICheckpointExportHooks
{
    Task DelayExportIoAsync(CancellationToken ct);

    void BeforeGitProbe();

    void AfterGitProbePathPublished(string path);

    void AfterSecretDenyCheck(string path);
}

/// <summary>Production no-op.</summary>
public sealed class NoOpCheckpointExportHooks : ICheckpointExportHooks
{
    public static readonly NoOpCheckpointExportHooks Instance = new();

    public Task DelayExportIoAsync(CancellationToken ct) => Task.CompletedTask;

    public void BeforeGitProbe()
    {
    }

    public void AfterGitProbePathPublished(string path)
    {
    }

    public void AfterSecretDenyCheck(string path)
    {
    }
}
