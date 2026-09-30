using Hypa.Continuity.Application;

namespace Hypa.Continuity.Harnesses.Fake;

/// <summary>
/// Fake dest start path. Optionally wraps a mux starter, then records dest start evidence.
/// </summary>
public sealed class FakeDestOccupantStarter : IDestOccupantStarter
{
    private readonly IDestOccupantStarter? _inner;
    private readonly FakeHarnessAdapter _adapter;

    public FakeDestOccupantStarter(IDestOccupantStarter? inner = null)
        : this(new FakeHarnessAdapter(), inner)
    {
    }

    public FakeDestOccupantStarter(FakeHarnessAdapter adapter, IDestOccupantStarter? inner = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _inner = inner;
    }

    public async ValueTask<DestOccupantStartResult> StartResumeAsync(
        DestOccupantStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_inner is not null)
        {
            var inner = await _inner.StartResumeAsync(request, cancellationToken).ConfigureAwait(false);
            if (!inner.Ok)
                return inner;
        }

        var started = _adapter.RunDestStart(request.CubeHome);
        if (!started.Ok)
        {
            return DestOccupantStartResult.Fail(
                started.Detail ?? "fake dest start failed");
        }

        return DestOccupantStartResult.Pass(
            request.PaneId,
            request.HarnessId,
            alive: true,
            liveness: new FakeDestOccupantLiveness());
    }
}

/// <summary>Test double that reports dest occupant liveness.</summary>
public sealed class FakeDestOccupantLiveness : IDestOccupantLiveness
{
    public bool Alive { get; set; } = true;

    public ValueTask<bool> IsAliveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Alive);
    }
}
