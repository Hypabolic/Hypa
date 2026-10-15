using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.UnitTests.Cli;

public sealed class ConnectionIdentityTests
{
    [Fact]
    public async Task BindStoresTheIdentityAndUnbindRemovesIt()
    {
        var registry = new AttachEndpointConnections();
        await using var client = NewClient("bind");

        registry.Bind(client, new ConnectionId("cube", 3));
        Assert.True(registry.TryGet(client, out var bound));
        Assert.Equal(new ConnectionId("cube", 3), bound);
        Assert.True(registry.IsBound(new ConnectionId("cube", 3)));

        registry.Unbind(client);
        Assert.False(registry.TryGet(client, out _));
        Assert.False(registry.IsBound(new ConnectionId("cube", 3)));
    }

    [Fact]
    public async Task RebindReplacesTheIdentityOfTheSameClient()
    {
        var registry = new AttachEndpointConnections();
        await using var client = NewClient("rebind");

        registry.Bind(client, new ConnectionId("cube", 3));
        registry.Bind(client, new ConnectionId("cube", 8));

        Assert.True(registry.TryGet(client, out var bound));
        Assert.Equal(new ConnectionId("cube", 8), bound);
        Assert.False(registry.IsBound(new ConnectionId("cube", 3)));
        Assert.True(registry.IsBound(new ConnectionId("cube", 8)));
    }

    [Fact]
    public async Task DisposedClientIsNotBound()
    {
        var registry = new AttachEndpointConnections();
        var client = NewClient("disposed");

        registry.Bind(client, new ConnectionId("cube", 5));
        Assert.True(registry.IsBound(new ConnectionId("cube", 5)));

        await client.DisposeAsync();

        Assert.False(registry.IsBound(new ConnectionId("cube", 5)));
        Assert.False(registry.TryGet(client, out _));
    }

    [Fact]
    public async Task SameIdSourceAndTargetResolveTheirOwnGenerations()
    {
        // The source and the target share the endpoint id "cube". The source is
        // available at generation 3. The target is the new connection at 8.
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        await using var source = NewClient("same-id-source");
        await using var target = NewClient("same-id-target");
        live.ConnectedPlacementId = "cube";
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                2,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            Lease("cube", 3),
            Lease("cube", 8),
            sourceAvailable: true);
        live.Connections.Bind(source, new ConnectionId("cube", 3));
        live.Connections.Bind(target, new ConnectionId("cube", 8));

        Assert.True(AttachEndpointActivationPump.TryResolveDrainedClient(
            live, source, out var sourceId, out var sourceGeneration));
        Assert.Equal("cube", sourceId);
        Assert.Equal(3UL, sourceGeneration);

        Assert.True(AttachEndpointActivationPump.TryResolveDrainedClient(
            live, target, out var targetId, out var targetGeneration));
        Assert.Equal("cube", targetId);
        Assert.Equal(8UL, targetGeneration);
    }

    [Fact]
    public async Task EventFromBoundClientIsAdmittedWithItsOwnConnectionId()
    {
        // The reader is bound to generation 9. The pending target lease is 8.
        // The event must carry 9, not the looked-up target generation 8.
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        await using var reader = NewClient("admit-reader");
        live.ConnectedPlacementId = "remote";
        live.ControlSlot = new AttachControlSlot { Client = reader };
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ActivatingTarget(
                "on",
                5,
                null,
                null,
                true,
                new EndpointActivationEvidence()),
            Lease("local", 1),
            Lease("remote", 8),
            sourceAvailable: false);
        live.Connections.Bind(reader, new ConnectionId("remote", 9));

        using var doc = JsonDocument.Parse(
            $$"""
            {
              "event":"{{AttachEndpointProtocol.ProjectionSnapshotEvent}}",
              "params":{
                "boot_id":"remote-boot",
                "revision":2,
                "projection_revision":2,
                "surface_revision":2,
                "columns":80,
                "rows":24,
                "focused":true,
                "pane_id":"pane-a"
              }
            }
            """);

        Assert.True(AttachEndpointActivationPump.TryForwardReadLoopActivationEvent(
            live, reader, doc.RootElement, tty: null));
        Assert.True(live.PendingActivation!.Phase is ActivationPhase.ActivatingTarget target
            && target.Evidence.SnapshotConnectionGeneration is null);
    }

    [Fact]
    public async Task HealthFailureFromClientReplacedByRedialIsStale()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "cube";
        live.TransportEnvelope.StampServerGeneration(1);
        await using var oldClient = NewClient("redial-old");
        await using var newClient = NewClient("redial-new");
        live.Connections.Bind(oldClient, new ConnectionId("cube", 1));
        live.ControlSlot = new AttachControlSlot { Client = oldClient };

        // The redial unbinds the old client and binds the new one at generation 2.
        live.Connections.Unbind(oldClient);
        live.TransportEnvelope.StampServerGeneration(2);
        live.Connections.Bind(newClient, new ConnectionId("cube", 2));
        live.ControlSlot = new AttachControlSlot { Client = newClient };

        AttachSession.RecordEndpointTransportFailure(
            live,
            "cube",
            1,
            "health timed out",
            oldClient);

        Assert.Empty(live.EndpointFailures);
        Assert.False(newClient.IsDisposed);
        Assert.Same(newClient, live.ControlSlot!.Client);
        Assert.True(live.Connections.TryGet(newClient, out var bound));
        Assert.Equal(new ConnectionId("cube", 2), bound);
    }

    [Fact]
    public async Task UnboundReaderFallsBackAndLogsTheResolvedIdentity()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var sink = new CapturingProcessLogSink();
        live.ProcessLog = sink;
        live.ConnectedPlacementId = "remote";
        live.TransportEnvelope.StampServerGeneration(4);
        await using var reader = NewClient("unbound-reader");

        Assert.True(AttachEndpointActivationPump.TryResolveDrainedClient(
            live, reader, out var endpointId, out var generation));
        Assert.Equal("remote", endpointId);
        Assert.Equal(4UL, generation);
        Assert.Contains(sink.Records, record =>
            record.Action == "connection_identity_unbound"
            && record.Err == "remote#4");
    }

    [Fact]
    public void LocalLossAppliesTheBoundIdentityWhenTheEnvelopeGenerationDiffers()
    {
        // Local hello stamps the source envelope. The transport envelope holds
        // another generation. The loss report names the bound Local client.
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "local";
        live.TransportEnvelope.StampServerGeneration(7);
        var local = NewClient("local-loss");
        live.Connections.Bind(local, new ConnectionId("local", 5));
        live.ControlSlot = new AttachControlSlot { Client = local };

        AttachSession.RecordEndpointTransportFailure(
            live,
            "local",
            7,
            "connection was lost",
            local);

        var failure = Assert.Single(live.EndpointFailures);
        Assert.Equal("local", failure.EndpointId);
        Assert.Equal(5UL, failure.Generation);
        Assert.True(local.IsDisposed);
    }

    [Fact]
    public async Task BoundClientFailureWithGenerationZeroApplies()
    {
        // Generation 0 means unspecified. It applies with the bound identity.
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "cube";
        await using var client = NewClient("zero-gen");
        live.Connections.Bind(client, new ConnectionId("cube", 2));
        live.ControlSlot = new AttachControlSlot { Client = client };

        AttachSession.RecordEndpointTransportFailure(live, "cube", 0, "connection was lost");

        var failure = Assert.Single(live.EndpointFailures);
        Assert.Equal("cube", failure.EndpointId);
        Assert.Equal(2UL, failure.Generation);
    }

    [Fact]
    public async Task BoundClientFailureWithAnotherGenerationIsStale()
    {
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ConnectedPlacementId = "cube";
        await using var client = NewClient("stale-gen");
        live.Connections.Bind(client, new ConnectionId("cube", 2));
        live.ControlSlot = new AttachControlSlot { Client = client };

        AttachSession.RecordEndpointTransportFailure(live, "cube", 1, "connection was lost");

        Assert.Empty(live.EndpointFailures);
        Assert.False(client.IsDisposed);
    }

    private static ControlPlaneClient NewClient(string name) =>
        new("/tmp/hypa-connection-identity-" + name + "-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

    private static EndpointActivationLease Lease(string endpointId, ulong generation) =>
        new()
        {
            EndpointId = endpointId,
            ConnectionGeneration = generation,
            BootId = endpointId + "-boot",
            MinimumProjectionRevision = 0,
            ClientId = "attach-client",
            LeaseId = "lease-" + endpointId,
        };
}
