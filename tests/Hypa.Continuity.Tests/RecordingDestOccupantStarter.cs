using Hypa.Continuity.Application;
using Hypa.Continuity.Harnesses.Fake;

namespace Hypa.Continuity.Tests;

/// <summary>Test double for dest occupant start.</summary>
public sealed class RecordingDestOccupantStarter : IDestOccupantStarter
{
    public DestOccupantStartRequest? LastRequest { get; private set; }
    public bool FailNext { get; set; }
    public string FailMessage { get; set; } = "injected failure";

    public ValueTask<DestOccupantStartResult> StartResumeAsync(
        DestOccupantStartRequest request,
        CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        if (FailNext)
            return ValueTask.FromResult(DestOccupantStartResult.Fail(FailMessage));

        var started = new FakeHarnessAdapter().RunDestStart(request.CubeHome);
        if (!started.Ok)
            return ValueTask.FromResult(DestOccupantStartResult.Fail(started.Detail ?? FailMessage));

        return ValueTask.FromResult(DestOccupantStartResult.Pass(
            request.PaneId,
            request.HarnessId,
            alive: true,
            liveness: new FakeDestOccupantLiveness()));
    }
}
