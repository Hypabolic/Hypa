using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Negotiate attach endpoint hello against Hypa compatibility constants.
/// and returns welcome or incompatible.
/// </summary>
public static class AttachEndpointNegotiator
{
    public const string FixtureBootId = "boot_1";

    public static AttachEndpointHello CompatibleHello() =>
        new()
        {
            EndpointGeneration = ProtocolAttachEndpoint.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            ClientId = "client_1",
            Geometry = new AttachGeometry
            {
                Columns = 120,
                Rows = 40,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            },
            SurfaceActive = false,
            SnapshotCodecs = [ProtocolAttachEndpoint.SnapshotCodec],
            SurfaceCodecs = [ProtocolAttachEndpoint.SurfaceCodec],
            InputCodecs = [ProtocolAttachEndpoint.InputCodec],
            RequiredCapabilities = [.. ProtocolAttachEndpoint.RequiredCapabilities],
        };

    public static AttachEndpointWelcome Negotiate(
        AttachEndpointHello hello,
        AttachEndpointIdentity? identity = null,
        string bootId = FixtureBootId)
    {
        if (TryReject(hello, identity, out var error))
            return Incompatible(bootId, error);

        return new AttachEndpointWelcome
        {
            EndpointGeneration = ProtocolAttachEndpoint.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            BootId = bootId,
            MuxIdentity = ProtocolVersion.Name,
            SnapshotCodec = ProtocolAttachEndpoint.SnapshotCodec,
            SurfaceCodec = ProtocolAttachEndpoint.SurfaceCodec,
            InputCodec = ProtocolAttachEndpoint.InputCodec,
            Methods = [.. ProtocolAttachEndpoint.Methods],
            Capabilities = [.. ProtocolAttachEndpoint.KnownCapabilities],
        };
    }

    public static bool TryNegotiate(
        AttachEndpointHello hello,
        out AttachEndpointWelcome welcome,
        AttachEndpointIdentity? identity = null,
        string bootId = FixtureBootId)
    {
        welcome = Negotiate(hello, identity, bootId);
        return welcome.Error is null;
    }

    private static bool TryReject(
        AttachEndpointHello hello,
        AttachEndpointIdentity? identity,
        out AttachEndpointError error)
    {
        if (identity is not null
            && ProtocolAttachEndpoint.ConnectionGenerationSubstitutesEndpoint(
                hello.EndpointGeneration,
                identity.ConnectionGeneration))
        {
            error = Fail("connection generation cannot substitute for endpoint generation");
            return true;
        }

        if (hello.EndpointGeneration != ProtocolAttachEndpoint.EndpointGeneration)
        {
            error = Fail("endpoint generation does not match");
            return true;
        }

        if (hello.ProtocolMajor != (uint)ProtocolVersion.Major)
        {
            error = Fail("protocol major does not match");
            return true;
        }

        if (string.IsNullOrWhiteSpace(hello.ClientId))
        {
            error = Fail("client_id is required");
            return true;
        }

        if (hello.Geometry is null || hello.Geometry.Columns == 0 || hello.Geometry.Rows == 0)
        {
            error = Fail("geometry is required");
            return true;
        }

        if (!Contains(hello.SnapshotCodecs, ProtocolAttachEndpoint.SnapshotCodec)
            || !Contains(hello.SurfaceCodecs, ProtocolAttachEndpoint.SurfaceCodec)
            || !Contains(hello.InputCodecs, ProtocolAttachEndpoint.InputCodec))
        {
            error = Fail("required codecs are missing");
            return true;
        }

        var required = hello.RequiredCapabilities ?? [];
        foreach (var capability in ProtocolAttachEndpoint.RequiredCapabilities)
        {
            if (!required.Contains(capability, StringComparer.Ordinal))
            {
                error = Fail("required capabilities are missing");
                return true;
            }
        }

        foreach (var capability in required)
        {
            if (string.IsNullOrWhiteSpace(capability)
                || !ProtocolAttachEndpoint.KnownCapabilities.Contains(capability, StringComparer.Ordinal))
            {
                error = Fail("unknown required capability");
                return true;
            }
        }

        error = Fail("");
        return false;
    }

    private static bool Contains(IReadOnlyList<string>? values, string required)
    {
        if (values is null)
            return false;
        foreach (var value in values)
        {
            if (string.Equals(value, required, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static AttachEndpointError Fail(string message) =>
        new()
        {
            Code = ProtocolAttachEndpoint.IncompatibleCode,
            Message = message,
        };

    private static AttachEndpointWelcome Incompatible(string bootId, AttachEndpointError error) =>
        new()
        {
            EndpointGeneration = ProtocolAttachEndpoint.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            BootId = bootId,
            MuxIdentity = ProtocolVersion.Name,
            SnapshotCodec = ProtocolAttachEndpoint.SnapshotCodec,
            SurfaceCodec = ProtocolAttachEndpoint.SurfaceCodec,
            InputCodec = ProtocolAttachEndpoint.InputCodec,
            Methods = [],
            Capabilities = [],
            Error = error,
        };
}
