using System.Diagnostics;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Sidebar;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlacementId = Hypa.Placement.Domain.PlacementId;

namespace Hypa.UnitTests.Mux;

public sealed class CubesConnectProcessCacheTests
{
    [Fact]
    public async Task Remote_connect_loads_the_directory_once_and_does_not_resolve_home()
    {
        var homeCalls = 0;
        var armed = false;
        var paths = ProcessOperatorPaths.Resolve(() =>
        {
            homeCalls++;
            if (armed)
                throw new InvalidOperationException("login home resolved during connect");
            return Path.Combine(Path.GetTempPath(), "hypa-operator-home");
        });
        armed = true;

        var store = new CountingPlacementStore(QuicRecord());
        var opens = 0;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            directoryFactory: () =>
            {
                opens++;
                _ = paths.PlacementDirectory;
                return new PlacementDirectoryService(store);
            },
            directMaterial: new StubDirectMaterial(),
            directConnect: (_, _) =>
                Task.FromResult<IFramedSession?>(ChannelFramedSession.Pair("plc_quic01").Client));
        var clock = new CubesConnectStageClock(
            Stopwatch.StartNew(),
            NullProcessLogSink.Instance,
            "plc_quic01",
            sessionId: null,
            attachClientId: null);
        var staged = (ICubesConnectStageResolver)resolver;
        var endpoint = await staged.ResolveAsync(Destination(), clock, CancellationToken.None);
        Assert.NotNull(endpoint);
        Assert.Equal(1, opens);
        Assert.Equal(1, store.Loads);
        Assert.Equal(1, homeCalls);

        var directoryOpens = 0;
        var authorized = await AttachSession.AuthorizePlacementConnectAsync(
            "plc_quic01",
            CancellationToken.None,
            clock,
            openDirectory: () =>
            {
                directoryOpens++;
                throw new InvalidOperationException("directory opened during authorize");
            });
        Assert.True(authorized);
        Assert.Equal(0, directoryOpens);
        Assert.Equal(1, store.Loads);
        Assert.Equal(3, store.Stamps);
        Assert.Equal(1, homeCalls);

        endpoint = await staged.ResolveAsync(Destination(), clock, CancellationToken.None);
        Assert.NotNull(endpoint);
        Assert.Equal(2, opens);
        Assert.Equal(2, store.Loads);
        Assert.Equal(5, store.Stamps);
        Assert.Equal(1, homeCalls);
    }

    [Fact]
    public async Task Disabled_placement_between_read_and_accept_fails_closed()
    {
        var store = new CountingPlacementStore(QuicRecord());
        var clock = Clock();
        var endpoint = await Resolve(store, clock, (_, _) =>
        {
            store.DisableQuic();
            return Task.FromResult<IFramedSession?>(ChannelFramedSession.Pair("plc_quic01").Client);
        });
        Assert.Null(endpoint);
        Assert.Equal(2, store.Loads);

        var directoryOpens = 0;
        var authorized = await AttachSession.AuthorizePlacementConnectAsync(
            "plc_quic01",
            CancellationToken.None,
            clock,
            openDirectory: () =>
            {
                directoryOpens++;
                throw new InvalidOperationException("directory opened during authorize");
            });
        Assert.False(authorized);
        Assert.Equal(0, directoryOpens);
        Assert.Equal(3, store.Loads);
    }

    [Fact]
    public async Task Retargeted_profile_between_read_and_accept_fails_closed()
    {
        var store = new CountingPlacementStore(QuicRecord());
        var clock = Clock();
        var endpoint = await Resolve(store, clock, (_, _) =>
        {
            store.RetargetQuic("127.0.0.1:9443");
            return Task.FromResult<IFramedSession?>(ChannelFramedSession.Pair("plc_quic01").Client);
        });
        Assert.Null(endpoint);
        Assert.Equal(2, store.Loads);
    }

    [Fact]
    public async Task Relabeled_profile_between_read_and_accept_still_connects()
    {
        var store = new CountingPlacementStore(QuicRecord());
        var clock = Clock();
        var endpoint = await Resolve(store, clock, (_, _) =>
        {
            store.RelabelQuic("renamed cube");
            return Task.FromResult<IFramedSession?>(ChannelFramedSession.Pair("plc_quic01").Client);
        });
        Assert.NotNull(endpoint);
        Assert.Equal(2, store.Loads);
    }

    [Fact]
    public async Task Removed_placement_between_read_and_accept_fails_closed()
    {
        var store = new CountingPlacementStore(QuicRecord());
        var clock = Clock();
        var endpoint = await Resolve(store, clock, (_, _) =>
        {
            store.RemovePlacement();
            return Task.FromResult<IFramedSession?>(ChannelFramedSession.Pair("plc_quic01").Client);
        });
        Assert.Null(endpoint);
        Assert.Equal(2, store.Loads);

        var directoryOpens = 0;
        var authorized = await AttachSession.AuthorizePlacementConnectAsync(
            "plc_quic01",
            CancellationToken.None,
            clock,
            openDirectory: () =>
            {
                directoryOpens++;
                throw new InvalidOperationException("directory opened during authorize");
            });
        Assert.False(authorized);
        Assert.Equal(0, directoryOpens);
        Assert.Equal(3, store.Loads);
    }

    [Fact]
    public void Attach_composition_resolves_login_home_once()
    {
        var calls = 0;
        var home = Path.Combine(Path.GetTempPath(), "hypa-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var services = AttachHostEntry.CreateServices(() =>
            {
                calls++;
                return home;
            });
            using var provider = services.BuildServiceProvider();
            _ = provider.GetRequiredService<ISidebarCubeCatalogSource>();
            var probe = provider.GetRequiredService<IQuicTransportCapabilityProbe>();
            Assert.Same(probe, provider.GetRequiredService<IQuicTransportCapabilityProbe>());
            _ = provider.GetRequiredService<ICubesConnectEndpointResolver>();
            _ = provider.GetRequiredService<ICubesConnectDirectMaterialSource>();
            Assert.Equal(1, calls);
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); }
            catch (IOException) { }
        }
    }

    private static SidebarCubeItem Destination() =>
        new()
        {
            Id = "plc_quic01",
            Name = "Edge",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
            ProviderSuffix = "QUIC · Reachable",
            ConnectEnabled = true,
        };

    [Fact]
    public void Plain_placement_recheck_follows_route_and_reachability()
    {
        var plain = QuicRecord() with { Quic = null, Kind = PlacementDirectoryKind.Peer };

        Assert.True(ConnectPlacementRecheck.SameEnabledProfile(plain, plain with { DisplayName = "Renamed" }));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            plain, plain with { MuxIdentity = MuxIdentity.Parse("mux_other") }));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            plain, plain with { Reachability = PlacementReachability.Asleep }));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            plain, plain with { Reachability = PlacementReachability.Unreachable }));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(plain, QuicRecord()));
        Assert.False(ConnectPlacementRecheck.SameEnabledProfile(
            plain, plain with { Quic = QuicRecord().Quic! with { Enabled = false } }));
    }

    private static PlacementRecord QuicRecord() =>
        new()
        {
            Id = PlacementId.Parse("plc_quic01"),
            Owner = DirectoryIdentity.Parse(Environment.UserName),
            DisplayName = "Edge",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Reachable,
            LastSeen = DateTimeOffset.UtcNow,
            Quic = new QuicPlacementProfile
            {
                Id = "plc_quic01",
                Label = "Edge",
                Target = "192.168.1.10",
                Session = "agents",
                Enabled = true,
            },
        };

    private static CubesConnectStageClock Clock() =>
        new(
            Stopwatch.StartNew(),
            NullProcessLogSink.Instance,
            "plc_quic01",
            sessionId: null,
            attachClientId: null);

    private static Task<IAttachEndpoint?> Resolve(
        CountingPlacementStore store,
        CubesConnectStageClock clock,
        Func<CubesConnectDirectMaterial, CancellationToken, Task<IFramedSession?>> directConnect)
    {
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: new PlacementDirectoryService(store),
            directMaterial: new StubDirectMaterial(),
            directConnect: directConnect);
        return ((ICubesConnectStageResolver)resolver).ResolveAsync(Destination(), clock, CancellationToken.None).AsTask();
    }

    private sealed class CountingPlacementStore(PlacementRecord record) : IPlacementDirectoryStore
    {
        private PlacementRecord? _record = record;
        private PlacementDirectoryChangeStamp _stamp = new()
        {
            Exists = true,
            Length = 1,
            ContentSha256 = "0",
        };

        public int Loads { get; private set; }

        public int Stamps { get; private set; }

        public void DisableQuic()
        {
            if (_record?.Quic is { } quic)
                _record = _record with { Quic = quic with { Enabled = false } };
            Bump();
        }

        public void RetargetQuic(string target)
        {
            if (_record?.Quic is { } quic)
                _record = _record with { Quic = quic with { Target = target } };
            Bump();
        }

        public void RelabelQuic(string label)
        {
            if (_record?.Quic is { } quic)
                _record = _record with { Quic = quic with { Label = label } };
            Bump();
        }

        public void RemovePlacement()
        {
            _record = null;
            Bump();
        }

        public PlacementDirectoryChangeStamp ReadChangeStamp()
        {
            Stamps++;
            return _stamp;
        }

        public ValueTask<PlacementDirectorySnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            Loads++;
            IReadOnlyList<PlacementRecord> placements = _record is null ? [] : [_record];
            return ValueTask.FromResult(new PlacementDirectorySnapshot
            {
                Placements = placements,
            });
        }

        public ValueTask SaveAsync(
            PlacementDirectorySnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private void Bump() =>
            _stamp = _stamp with
            {
                ContentSha256 = (int.Parse(_stamp.ContentSha256!, System.Globalization.CultureInfo.InvariantCulture) + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

        public ValueTask<T> MutateAsync<T>(
            Func<PlacementDirectorySnapshot, (PlacementDirectorySnapshot Next, T Result)> mutator,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubDirectMaterial : ICubesConnectDirectMaterialSource
    {
        public ValueTask<CubesConnectDirectMaterial?> GetAsync(
            PlacementRecord placement,
            CancellationToken cancellationToken = default)
        {
            Assert.True(JoinNonce.TryParse("nonce001", out var nonce));
            Assert.True(DeviceId.TryParse("dev_cli001", out var deviceId));
            Assert.True(Hypa.Connectivity.Domain.PlacementId.TryParse("plc_quic01", out var joinPlacement));
            var now = DateTimeOffset.UtcNow;
            var issued = new DeveloperJoinCapabilityIssuer().Issue(
                joinPlacement,
                deviceId,
                JoinRole.Client,
                nonce,
                now.AddMinutes(1),
                now);
            Assert.True(issued.Ok, issued.Detail);
            return ValueTask.FromResult<CubesConnectDirectMaterial?>(new CubesConnectDirectMaterial
            {
                Endpoint = new BytePathEndpoint
                {
                    Host = "192.168.1.10",
                    Port = 443,
                    Tls = true,
                    TlsServerName = "192.168.1.10",
                },
                Bootstrap = new JoinBootstrap
                {
                    ProtocolVersion = ConnectivityProtocolVersion.V0,
                    Role = JoinRole.Client,
                    PlacementId = joinPlacement,
                    Nonce = nonce,
                    StreamClass = StreamClass.Control,
                    Capability = issued.Value!,
                    Audience = RendezvousAudiences.Rendezvous,
                    TenantScope = RendezvousTenants.Local,
                },
            });
        }
    }
}
