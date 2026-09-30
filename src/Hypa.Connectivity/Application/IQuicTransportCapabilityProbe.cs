using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>Reports whether System.Net.Quic is usable on this host and RID.</summary>
public interface IQuicTransportCapabilityProbe
{
    QuicTransportCapabilityReport Probe();
}
