using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>Runtime QUIC gate aligned with the C-69 capability probe.</summary>
internal static class QuicTransportRuntime
{
    public static bool IsUsable(IQuicTransportCapabilityProbe? probe = null)
    {
        var report = (probe ?? new QuicTransportCapabilityProbe()).Probe();
        return report.IsSupported;
    }

    public static QuicTransportCapabilityReport Probe(IQuicTransportCapabilityProbe? probe = null) =>
        (probe ?? new QuicTransportCapabilityProbe()).Probe();
}
