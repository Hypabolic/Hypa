using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using PlacementId = Hypa.Placement.Domain.PlacementId;
using Xunit;

namespace Hypa.UnitTests.Mux;

public sealed class CubesQuicConnectResolverTests
{
    [Fact]
    public async Task Quic_placement_uses_direct_material_not_rendezvous_join()
    {
        var rendezvousCalled = false;
        var directCalled = false;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            joinMaterial: new ThrowIfCalledJoinMaterial(() => rendezvousCalled = true),
            directMaterial: new StubDirectMaterial(),
            connect: (_, _) =>
            {
                rendezvousCalled = true;
                return Task.FromResult<IFramedSession?>(null);
            },
            directConnect: (_, _) =>
            {
                directCalled = true;
                return Task.FromResult<IFramedSession?>(ChannelFramedSession.Pair("plc_quic01").Client);
            });
        var destination = new SidebarCubeItem
        {
            Id = "plc_quic01",
            Name = "Edge",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
            ProviderSuffix = "QUIC · Reachable",
            ConnectEnabled = true,
        };
        var directory = new MapPlacementDirectory(new PlacementRecord
        {
            Id = PlacementId.Parse("plc_quic01"),
            Owner = DirectoryIdentity.Parse("local:test"),
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
        });
        var endpoint = await resolver.WithDirectory(directory).ResolveAsync(destination);
        Assert.True(directCalled);
        Assert.False(rendezvousCalled);
        Assert.IsType<ConnectivityAttachEndpoint>(endpoint);
    }

    [Fact]
    public async Task Disabled_quic_profile_blocks_connect()
    {
        var directCalled = false;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            directConnect: (_, _) =>
            {
                directCalled = true;
                return Task.FromResult<IFramedSession?>(null);
            });
        var destination = new SidebarCubeItem
        {
            Id = "plc_quic01",
            Name = "Edge",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Unreachable,
            ProviderSuffix = "QUIC · Disabled",
            ConnectEnabled = false,
        };
        var directory = new MapPlacementDirectory(new PlacementRecord
        {
            Id = PlacementId.Parse("plc_quic01"),
            Owner = DirectoryIdentity.Parse("local:test"),
            DisplayName = "Edge",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Unreachable,
            LastSeen = DateTimeOffset.UtcNow,
            Quic = new QuicPlacementProfile
            {
                Id = "plc_quic01",
                Label = "Edge",
                Target = "192.168.1.10",
                Session = "agents",
                Enabled = false,
            },
        });
        var endpoint = await resolver.WithDirectory(directory).ResolveAsync(destination);
        Assert.False(directCalled);
        Assert.Null(endpoint);
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
            return new ValueTask<CubesConnectDirectMaterial?>(new CubesConnectDirectMaterial
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

    private sealed class ThrowIfCalledJoinMaterial(Action onCall) : ICubesConnectJoinMaterialSource
    {
        public ValueTask<CubesConnectJoinMaterial?> GetAsync(
            PlacementRecord placement,
            CancellationToken cancellationToken = default)
        {
            onCall();
            throw new InvalidOperationException("rendezvous join must not run for QUIC peer");
        }
    }

    private sealed class MapPlacementDirectory(PlacementRecord record) : IPlacementDirectory
    {
        public ValueTask<PlacementOutcome<PlacementRecord>> GetAsync(
            PlacementId placementId,
            CancellationToken cancellationToken = default) =>
            new(PlacementOutcome<PlacementRecord>.Success(record));

        public ValueTask<PlacementOutcome<PlacementRecord>> GetForConnectAsync(
            DirectoryIdentity requester,
            PlacementId placementId,
            CancellationToken cancellationToken = default) =>
            new(PlacementOutcome<PlacementRecord>.Success(record));

        public ValueTask<PlacementOutcome<PlacementRecord>> RegisterAsync(
            PlacementRegistration request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<PlacementRecord>> SetReachabilityAsync(
            DirectoryIdentity actor,
            PlacementId placementId,
            PlacementReachability reachability,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<PlacementRecord>> SetActiveWorkAsync(
            DirectoryIdentity actor,
            PlacementId placementId,
            string? workId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome> GrantListAccessAsync(
            DirectoryIdentity actor,
            PlacementId placementId,
            DirectoryIdentity grantee,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome> GrantWorkAccessAsync(
            DirectoryIdentity actor,
            DirectoryIdentity identity,
            string workId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<PlacementRecord>> RemoveOwnedAsync(
            DirectoryIdentity owner,
            PlacementId placementId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<PlacementOutcome<IReadOnlyList<PlacementRow>>> ListAsync(
            DirectoryIdentity requester,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
