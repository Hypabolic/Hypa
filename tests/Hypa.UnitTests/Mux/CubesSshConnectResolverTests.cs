using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Xunit;

namespace Hypa.UnitTests.Mux;

public sealed class CubesSshConnectResolverTests
{
    [Fact]
    public async Task Ssh_placement_uses_remote_mux_instead_of_connectivity_join()
    {
        var opened = false;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            remoteMux: new StubRemoteMux(() => opened = true));
        var destination = new SidebarCubeItem
        {
            Id = "plc_ssh001",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var directory = new MapPlacementDirectory(new PlacementRecord
        {
            Id = PlacementId.Parse("plc_ssh001"),
            Owner = DirectoryIdentity.Parse("local:test"),
            DisplayName = "Build",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Reachable,
            LastSeen = DateTimeOffset.UtcNow,
            Ssh = new SshPlacementProfile
            {
                Id = "plc_ssh001",
                Label = "Build",
                Target = "user@dev",
                Session = "agents",
                Enabled = true,
            },
        });
        var resolved = resolver.WithDirectory(directory);
        var endpoint = await resolved.ResolveAsync(destination);
        Assert.True(opened);
        Assert.IsType<SshAttachEndpoint>(endpoint);
    }

    [Fact]
    public async Task Disabled_ssh_profile_does_not_open_remote_mux()
    {
        var opened = false;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            remoteMux: new StubRemoteMux(() => opened = true));
        var destination = new SidebarCubeItem
        {
            Id = "plc_ssh001",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Unreachable,
            ConnectEnabled = false,
        };
        var directory = new MapPlacementDirectory(new PlacementRecord
        {
            Id = PlacementId.Parse("plc_ssh001"),
            Owner = DirectoryIdentity.Parse("local:test"),
            DisplayName = "Build",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Unreachable,
            LastSeen = DateTimeOffset.UtcNow,
            Ssh = new SshPlacementProfile
            {
                Id = "plc_ssh001",
                Label = "Build",
                Target = "user@dev",
                Session = "agents",
                Enabled = false,
            },
        });
        var resolved = resolver.WithDirectory(directory);
        var endpoint = await resolved.ResolveAsync(destination);
        Assert.False(opened);
        Assert.Null(endpoint);
    }

    private sealed class StubRemoteMux(Action onOpen) : IRemoteMuxPath
    {
        public ValueTask<RemoteMuxOutcome> OpenAsync(
            RemoteMuxOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            onOpen();
            return new ValueTask<RemoteMuxOutcome>(RemoteMuxOutcome.Success(new RemoteMuxPath
            {
                LocalSocketPath = Path.Combine(Path.GetTempPath(), "hypa-ssh-" + Guid.NewGuid().ToString("N") + ".sock"),
                Session = "agents",
                Target = "user@dev",
                Generation = 1,
            }));
        }
    }

    [Fact]
    public async Task Unreachable_ssh_profile_still_opens_remote_mux_for_retry()
    {
        var opened = false;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            remoteMux: new StubRemoteMux(() => opened = true));
        var destination = new SidebarCubeItem
        {
            Id = "plc_ssh001",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Unreachable,
            ProviderSuffix = "SSH · Unreachable",
            ConnectEnabled = true,
        };
        var directory = new MapPlacementDirectory(new PlacementRecord
        {
            Id = PlacementId.Parse("plc_ssh001"),
            Owner = DirectoryIdentity.Parse("local:test"),
            DisplayName = "Build",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Unreachable,
            LastSeen = DateTimeOffset.UtcNow,
            Ssh = new SshPlacementProfile
            {
                Id = "plc_ssh001",
                Label = "Build",
                Target = "user@dev",
                Session = "agents",
                Enabled = true,
            },
        });
        var resolved = resolver.WithDirectory(directory);
        var endpoint = await resolved.ResolveAsync(destination);
        Assert.True(opened);
        Assert.IsType<SshAttachEndpoint>(endpoint);
    }

    [Fact]
    public async Task Approval_required_ssh_profile_blocks_connect()
    {
        var opened = false;
        var resolver = new DirectoryCubesConnectEndpointResolver(
            directory: null,
            remoteMux: new StubRemoteMux(() => opened = true));
        var destination = new SidebarCubeItem
        {
            Id = "plc_ssh001",
            Name = "Build",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Unreachable,
            ProviderSuffix = "SSH · Approval required",
            ConnectEnabled = false,
        };
        var directory = new MapPlacementDirectory(new PlacementRecord
        {
            Id = PlacementId.Parse("plc_ssh001"),
            Owner = DirectoryIdentity.Parse("local:test"),
            DisplayName = "Build",
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = MuxIdentity.Parse("mux_agents"),
            Reachability = PlacementReachability.Unreachable,
            LastSeen = DateTimeOffset.UtcNow,
            Ssh = new SshPlacementProfile
            {
                Id = "plc_ssh001",
                Label = "Build",
                Target = "user@dev",
                Session = "agents",
                Enabled = false,
                Attention = SshPlacementAttentionStates.ApprovalRequired,
            },
        });
        var resolved = resolver.WithDirectory(directory);
        var endpoint = await resolved.ResolveAsync(destination);
        Assert.False(opened);
        Assert.Null(endpoint);
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
