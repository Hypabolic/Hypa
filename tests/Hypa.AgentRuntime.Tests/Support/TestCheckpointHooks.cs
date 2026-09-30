using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Tests.Support;

internal sealed class TestWorkspaceWalkHooks : IWorkspaceWalkHooks
{
    public bool ThrowOnList { get; set; }

    public Action<string>? AfterWalkRootLstatHandler { get; set; }

    public Action<string>? AfterPinClassifyHandler { get; set; }

    public void AfterWalkRootLstat(string path) => AfterWalkRootLstatHandler?.Invoke(path);

    public void AfterPinClassify(string path) => AfterPinClassifyHandler?.Invoke(path);

    public void Reset()
    {
        ThrowOnList = false;
        AfterWalkRootLstatHandler = null;
        AfterPinClassifyHandler = null;
    }
}

internal sealed class TestCheckpointExportHooks : ICheckpointExportHooks
{
    public Func<CancellationToken, Task>? ExportIoDelay { get; set; }

    public Action? BeforeGitProbeHandler { get; set; }

    public Action<string>? AfterGitProbePathPublishedHandler { get; set; }

    public Action<string>? AfterSecretDenyCheckHandler { get; set; }

    public Task DelayExportIoAsync(CancellationToken ct) =>
        ExportIoDelay is null ? Task.CompletedTask : ExportIoDelay(ct);

    public void BeforeGitProbe() => BeforeGitProbeHandler?.Invoke();

    public void AfterGitProbePathPublished(string path) =>
        AfterGitProbePathPublishedHandler?.Invoke(path);

    public void AfterSecretDenyCheck(string path) =>
        AfterSecretDenyCheckHandler?.Invoke(path);

    public void Reset()
    {
        ExportIoDelay = null;
        BeforeGitProbeHandler = null;
        AfterGitProbePathPublishedHandler = null;
        AfterSecretDenyCheckHandler = null;
    }
}

internal sealed class TestGitProbeHooks : IGitProbeHooks
{
    public Action<string>? BeforeProcessStartHandler { get; set; }

    public void BeforeProcessStart(string workingDirectory) =>
        BeforeProcessStartHandler?.Invoke(workingDirectory);

    public void Reset() => BeforeProcessStartHandler = null;
}
