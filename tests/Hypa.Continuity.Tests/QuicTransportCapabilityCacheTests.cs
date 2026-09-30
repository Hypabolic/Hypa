using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class QuicTransportCapabilityCacheTests
{
    [Fact]
    public void Probe_runs_once_per_cache_instance_and_keeps_a_failure()
    {
        var inner = new CountingProbe(supported: false);
        var cache = new CachingQuicTransportCapabilityProbe(inner);
        var first = cache.Probe();
        var second = cache.Probe();
        Assert.False(first.IsSupported);
        Assert.Same(first, second);
        Assert.Equal(1, inner.Calls);

        var again = new CachingQuicTransportCapabilityProbe(inner);
        var third = again.Probe();
        Assert.False(third.IsSupported);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Probe_runs_once_per_cache_instance_when_supported()
    {
        var inner = new CountingProbe(supported: true);
        var cache = new CachingQuicTransportCapabilityProbe(inner);
        var first = cache.Probe();
        _ = cache.Probe();
        Assert.True(first.IsSupported);
        Assert.Equal(1, inner.Calls);
    }

    private sealed class CountingProbe(bool supported) : IQuicTransportCapabilityProbe
    {
        public int Calls { get; private set; }

        public QuicTransportCapabilityReport Probe()
        {
            Calls++;
            return new QuicTransportCapabilityReport
            {
                RuntimeIdentifier = "test",
                IsSupported = supported,
                NativeLibraryFound = supported,
                NativeLibraryLocation = supported ? "test" : MsQuicNativeLocations.Absent,
                QuicProvider = BytePathProviders.Quic,
                FallbackProvider = BytePathProviders.Tcp,
                ZeroRttEnabled = false,
                Reason = supported ? null : ConnectivityReasons.QuicUnsupported,
            };
        }
    }
}
