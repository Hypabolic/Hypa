using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.Placement.Application;
using Xunit;

namespace Hypa.UnitTests.Mux;

public sealed class CubesConnectRetargetServiceTests
{
    [Fact]
    public async Task ConnectAsync_retires_ssh_endpoint_on_dest_connect_timeout()
    {
        var registry = new SshPlacementEndpointRegistry();
        var endpoint = new SshAttachEndpoint(new RemoteMuxPath
        {
            LocalSocketPath = $"/tmp/hypa-connect-timeout-{Guid.NewGuid():N}.sock",
            Session = "agents",
            Target = "user@dev",
            Generation = 3,
        });
        registry.RegisterConnect("plc_ssh", endpoint);
        var resolver = new MapCubesConnectEndpointResolver((_, _) =>
            ValueTask.FromResult<IAttachEndpoint?>(endpoint));
        var service = new CubesConnectRetargetService(resolver);
        var destination = new SidebarCubeItem
        {
            Id = "plc_ssh",
            Name = "Remote",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };

        var outcome = await service.ConnectAsync(
            new CubesConnectRequest
            {
                Destination = destination,
                SshAttempt = new CubesConnectSshAttempt { Generation = 3 },
                RetireSshPlacement = (id, generation, ssh) =>
                    registry.RetireIfActive(id, generation, ssh),
            },
            CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(CubesConnectReasons.DestConnectFailed, outcome.Reason);
        Assert.False(registry.IsConnectActive("plc_ssh", 3, endpoint));
    }

    [Fact]
    public async Task ConnectAsync_maps_dest_cancel_to_connect_failure()
    {
        var service = new CubesConnectRetargetService(
            new MapCubesConnectEndpointResolver((_, _) =>
                throw new OperationCanceledException()));
        var destination = new SidebarCubeItem
        {
            Id = "plc_docker",
            Name = "docker",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
            ProviderSuffix = "QUIC · Reachable",
            ConnectEnabled = true,
        };

        var outcome = await service.ConnectAsync(
            new CubesConnectRequest { Destination = destination },
            CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(CubesConnectReasons.DestConnectFailed, outcome.Reason);
        Assert.Equal("destination connect timed out", outcome.Detail);
    }

    [Fact]
    public async Task ConnectAsync_reports_join_failure_when_catalog_target_has_no_live_endpoint()
    {
        var service = new CubesConnectRetargetService(
            new MapCubesConnectEndpointResolver((_, _) =>
                ValueTask.FromResult<IAttachEndpoint?>(null)));
        var destination = new SidebarCubeItem
        {
            Id = "plc_docker",
            Name = "docker",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
            ProviderSuffix = "QUIC · Reachable",
            ConnectEnabled = true,
        };

        var outcome = await service.ConnectAsync(
            new CubesConnectRequest { Destination = destination },
            CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(CubesConnectReasons.DestConnectFailed, outcome.Reason);
        Assert.Equal("destination did not accept the join", outcome.Detail);
    }

    [Fact]
    public async Task ConnectAsync_reports_missing_endpoint_when_catalog_has_no_join_target()
    {
        var service = new CubesConnectRetargetService(
            new MapCubesConnectEndpointResolver((_, _) =>
                ValueTask.FromResult<IAttachEndpoint?>(null)));
        var destination = new SidebarCubeItem
        {
            Id = "plc_unknown",
            Name = "unknown",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Unreachable,
        };

        var outcome = await service.ConnectAsync(
            new CubesConnectRequest { Destination = destination },
            CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Equal(CubesConnectReasons.DestUnreachable, outcome.Reason);
        Assert.Equal("destination has no attach endpoint", outcome.Detail);
    }

    [Fact]
    public void Dest_mux_claim_without_takeover_keeps_existing_holder()
    {
        var destLeases = new InMemoryLeaseRegistry();
        var held = destLeases.Claim(
            "pane_d",
            LeaseScopes.Input,
            "conn_4",
            takeover: false,
            reason: null,
            ttlMs: 30_000);
        Assert.Equal(LeaseOutcomes.Granted, held.Outcome);
        Assert.Equal("conn_4", held.Lease?.HolderId);

        var denied = destLeases.Claim(
            "pane_d",
            LeaseScopes.Input,
            "conn_5",
            takeover: false,
            reason: null,
            ttlMs: 30_000);
        Assert.Equal(LeaseOutcomes.Denied, denied.Outcome);
        Assert.Equal("conn_4", destLeases.GetActive("pane_d", LeaseScopes.Input)?.HolderId);

        var sameHolder = destLeases.Claim(
            "pane_d",
            LeaseScopes.Input,
            "conn_4",
            takeover: false,
            reason: null,
            ttlMs: 30_000);
        Assert.Equal(LeaseOutcomes.AlreadyHeld, sameHolder.Outcome);
        Assert.Equal("conn_4", destLeases.GetActive("pane_d", LeaseScopes.Input)?.HolderId);
    }

    [Fact]
    public void Dest_commit_gate_is_subscribe_not_leases()
    {
        var missing = new CubesConnectRetargetOutcome
        {
            Ok = true,
            Action = CubesConnectActions.Retargeted,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
            DestInputLease = null,
            DestResizeLease = null,
        };
        Assert.False(AttachSession.HasRequiredDestSubscribe(missing));
        var ready = missing with { DestSubscribeId = "sub_dest" };
        Assert.True(AttachSession.HasRequiredDestSubscribe(ready));
        Assert.Null(ready.DestResizeLease);
    }
}
