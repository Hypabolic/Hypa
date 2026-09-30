using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// One QUIC capability result for the life of this instance.
/// A failed report is kept. A second instance probes again.
/// </summary>
public sealed class CachingQuicTransportCapabilityProbe : IQuicTransportCapabilityProbe
{
    private readonly IQuicTransportCapabilityProbe _inner;
    private readonly object _gate = new();
    private QuicTransportCapabilityReport? _report;

    public CachingQuicTransportCapabilityProbe(IQuicTransportCapabilityProbe inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public QuicTransportCapabilityReport Probe()
    {
        lock (_gate)
        {
            _report ??= _inner.Probe();
            return _report;
        }
    }
}
