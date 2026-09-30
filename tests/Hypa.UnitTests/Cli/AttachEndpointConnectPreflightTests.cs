using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachEndpointConnectPreflightTests
{
    [Fact]
    public void Source_hello_stamps_a_provisional_lease()
    {
        var geometry = new AttachGeometry
        {
            Columns = 80,
            Rows = 24,
            CellWidthPx = 8,
            CellHeightPx = 16,
            GeometryRevision = 1,
        };
        var target = Welcome("dest-boot");
        var source = Welcome("source-boot");
        var request = AttachEndpointConnectPreflight.BuildBeginRequest(
            "attach-client",
            geometry,
            target,
            targetGeneration: 7,
            source,
            sourceGeneration: 3,
            sourceAvailable: true);

        Assert.NotNull(request.Source);
        Assert.True(request.SourceAvailable);
        Assert.Equal("source", request.Source!.EndpointId);
        Assert.Equal(3UL, request.Source.ConnectionGeneration);
        Assert.Equal("source-boot", request.Source.BootId);
        Assert.Null(AttachEndpointPreflight.ValidateBegin(request, target, source));
    }

    [Fact]
    public void Source_available_without_lease_fails_preflight()
    {
        var geometry = new AttachGeometry
        {
            Columns = 80,
            Rows = 24,
            CellWidthPx = 8,
            CellHeightPx = 16,
            GeometryRevision = 1,
        };
        var target = Welcome("dest-boot");
        var request = new EndpointActivationBeginRequest
        {
            ClientId = "attach-client",
            Geometry = geometry,
            Target = new EndpointActivationIntent { EndpointId = "pending" },
            Source = null,
            SourceAvailable = true,
            HostFocused = true,
            Epoch = 1,
            TargetLease = new EndpointActivationLease
            {
                EndpointId = "pending",
                ConnectionGeneration = 7,
                BootId = "dest-boot",
                MinimumProjectionRevision = 0,
                ClientId = "attach-client",
                LeaseId = string.Empty,
            },
        };

        var error = AttachEndpointPreflight.ValidateBegin(request, target, Welcome("source-boot"));
        Assert.NotNull(error);
        Assert.Equal("source endpoint lease is missing", error!.Message);
    }

    private static AttachEndpointWelcome Welcome(string bootId) =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            BootId = bootId,
            MuxIdentity = ProtocolVersion.Name,
            SnapshotCodec = AttachEndpointProtocol.SnapshotCodec,
            SurfaceCodec = AttachEndpointProtocol.SurfaceCodec,
            InputCodec = AttachEndpointProtocol.InputCodec,
            Methods = [.. AttachEndpointProtocol.Methods],
            Capabilities = [.. AttachEndpointProtocol.RequiredCapabilities],
        };
}
