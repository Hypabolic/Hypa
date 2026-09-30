using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachSurfaceInterestTests
{
    [Fact]
    public void Active_true_advances_projection_floor_and_clears_snapshot()
    {
        var registry = new AttachSurfaceInterestPublication("boot_a", "mux_a");
        registry.ApplyHello(new AttachEndpointHelloApplyRequest
        {
            ConnectionId = "conn_1",
            EndpointId = "plc_a",
            Hello = SampleHello("cli_a"),
        });

        var first = registry.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
        {
            ConnectionId = "conn_1",
            ClientId = "cli_a",
            Active = true,
            GeometryRevision = 1,
        });
        Assert.True(first.IsOk);
        Assert.Equal(1UL, first.Value.ProjectionRevision);

        var second = registry.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
        {
            ConnectionId = "conn_1",
            ClientId = "cli_a",
            Active = true,
            GeometryRevision = 1,
        });
        Assert.True(second.IsOk);
        Assert.Equal(2UL, second.Value.ProjectionRevision);
        Assert.Null(registry.GetSnapshot("conn_1")!.CachedSnapshotRevision);
    }

    [Fact]
    public void Stale_same_boot_surface_rejected_by_projection_floor()
    {
        var registry = new AttachSurfaceInterestPublication("boot_a", "mux_a");
        registry.ApplyHello(new AttachEndpointHelloApplyRequest
        {
            ConnectionId = "conn_1",
            EndpointId = "plc_a",
            Hello = SampleHello("cli_a"),
        });
        var on = registry.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
        {
            ConnectionId = "conn_1",
            ClientId = "cli_a",
            Active = true,
            GeometryRevision = 1,
        });
        Assert.True(on.IsOk);

        var stale = registry.AdmitSurface(new AttachActivationAdmissionRequest
        {
            ConnectionId = "conn_1",
            ProjectionRevision = 0,
            SurfaceRevision = 0,
            GeometryRevision = 1,
            BootId = "boot_a",
            LeaseId = on.Value.LeaseId,
        });
        Assert.False(stale.IsOk);
        Assert.Equal(AttachEndpointErrorCodes.StaleSurface, stale.Error.Code);
    }

    [Fact]
    public void Inactive_connection_rejects_pane_mutation()
    {
        var registry = new AttachSurfaceInterestPublication("boot_a", "mux_a");
        registry.ApplyHello(new AttachEndpointHelloApplyRequest
        {
            ConnectionId = "conn_1",
            EndpointId = "plc_a",
            Hello = SampleHello("cli_a"),
        });

        var admit = registry.AdmitPaneMutation("conn_1");
        Assert.False(admit.IsOk);
        Assert.Equal(AttachEndpointErrorCodes.SurfaceInactive, admit.Error.Code);
    }

    [Fact]
    public void ValidateIngress_rejects_stale_connection_generation()
    {
        var registry = new AttachSurfaceInterestPublication("boot_a", "mux_a");
        registry.ApplyHello(new AttachEndpointHelloApplyRequest
        {
            ConnectionId = "conn_1",
            EndpointId = "plc_a",
            Hello = SampleHello("cli_a"),
        });
        registry.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
        {
            ConnectionId = "conn_1",
            ClientId = "cli_a",
            Active = true,
            GeometryRevision = 1,
        });

        var snapshot = registry.GetSnapshot("conn_1");
        Assert.NotNull(snapshot);
        var ingress = registry.ValidateIngress("conn_1", "cli_a", snapshot!.ConnectionGeneration + 1);
        Assert.False(ingress.IsOk);
    }

    [Fact]
    public void SessionProjectionRevision_tracks_active_attach_surfaces()
    {
        var registry = new AttachSurfaceInterestPublication("boot_a", "mux_a");
        Assert.Equal(0UL, registry.SessionProjectionRevision());

        registry.ApplyHello(new AttachEndpointHelloApplyRequest
        {
            ConnectionId = "conn_1",
            EndpointId = "plc_a",
            Hello = SampleHello("cli_a"),
        });
        registry.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
        {
            ConnectionId = "conn_1",
            ClientId = "cli_a",
            Active = true,
            GeometryRevision = 1,
        });

        Assert.Equal(1UL, registry.SessionProjectionRevision());
    }

    [Fact]
    public void AdmitPaneMutation_rejects_superseded_connection_generation()
    {
        var registry = new AttachSurfaceInterestPublication("boot_a", "mux_a");
        registry.ApplyHello(new AttachEndpointHelloApplyRequest
        {
            ConnectionId = "conn_old",
            EndpointId = "plc_a",
            Hello = SampleHello("cli_a"),
        });
        registry.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
        {
            ConnectionId = "conn_old",
            ClientId = "cli_a",
            Active = true,
            GeometryRevision = 1,
        });

        registry.ApplyHello(new AttachEndpointHelloApplyRequest
        {
            ConnectionId = "conn_new",
            EndpointId = "plc_a",
            Hello = SampleHello("cli_a"),
        });

        var stale = registry.AdmitPaneMutation("conn_old");
        Assert.False(stale.IsOk);
        Assert.Equal(AttachEndpointErrorCodes.StaleSurface, stale.Error.Code);
    }

    [Fact]
    public void Deactivation_releases_surface_ownership()
    {
        var registry = new AttachSurfaceInterestPublication("boot_a", "mux_a");
        registry.ApplyHello(new AttachEndpointHelloApplyRequest
        {
            ConnectionId = "conn_1",
            EndpointId = "plc_a",
            Hello = SampleHello("cli_a"),
        });
        registry.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
        {
            ConnectionId = "conn_1",
            ClientId = "cli_a",
            Active = true,
            GeometryRevision = 1,
        });
        registry.ApplySurfaceInterest(new AttachSurfaceInterestApplyRequest
        {
            ConnectionId = "conn_1",
            ClientId = "cli_a",
            Active = false,
            GeometryRevision = 1,
        });

        Assert.False(registry.IsSurfaceActive("conn_1"));
        Assert.False(registry.AdmitPaneMutation("conn_1").IsOk);
    }

    private static AttachEndpointHello SampleHello(string clientId) =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = 1,
            ProtocolMinor = 1,
            ClientId = clientId,
            Geometry = new AttachGeometry
            {
                Columns = 80,
                Rows = 24,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            },
            SurfaceActive = false,
            SnapshotCodecs = [AttachEndpointProtocol.SnapshotCodec],
            SurfaceCodecs = [AttachEndpointProtocol.SurfaceCodec],
            InputCodecs = [AttachEndpointProtocol.InputCodec],
            RequiredCapabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
        };
}
