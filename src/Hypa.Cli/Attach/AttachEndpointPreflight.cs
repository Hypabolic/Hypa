using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.Cli.Attach;

internal static class AttachEndpointPreflight
{
    public static EndpointActivationPreflightError? ValidateBegin(
        EndpointActivationBeginRequest request,
        AttachEndpointWelcome? targetWelcome,
        AttachEndpointWelcome? sourceWelcome)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Geometry.Columns < 1 || request.Geometry.Rows < 1)
        {
            return new EndpointActivationPreflightError("activation resize did not include a valid surface geometry");
        }

        if (targetWelcome is null)
        {
            return new EndpointActivationPreflightError("target endpoint welcome is missing");
        }

        if (targetWelcome.Error is not null)
        {
            return new EndpointActivationPreflightError(targetWelcome.Error.Message)
            {
                Code = targetWelcome.Error.Code,
            };
        }

        if (!ValidateWelcome(targetWelcome, out var targetError))
            return targetError;

        if (request.SourceAvailable)
        {
            if (request.Source is null)
            {
                return new EndpointActivationPreflightError("source endpoint lease is missing");
            }

            if (sourceWelcome is not null && sourceWelcome.Error is not null)
            {
                return new EndpointActivationPreflightError(sourceWelcome.Error.Message)
                {
                    Code = sourceWelcome.Error.Code,
                };
            }

            if (sourceWelcome is not null && !ValidateWelcome(sourceWelcome, out var sourceError))
                return sourceError;
        }

        return null;
    }

    public static AttachEndpointHello BuildHello(
        string clientId,
        AttachGeometry geometry,
        bool surfaceActive = false) =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            ClientId = clientId,
            Geometry = geometry,
            SurfaceActive = surfaceActive,
            SnapshotCodecs = [AttachEndpointProtocol.SnapshotCodec],
            SurfaceCodecs = [AttachEndpointProtocol.SurfaceCodec],
            InputCodecs = [AttachEndpointProtocol.InputCodec],
            RequiredCapabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
        };

    private static bool ValidateWelcome(
        AttachEndpointWelcome welcome,
        out EndpointActivationPreflightError? error)
    {
        if (welcome.EndpointGeneration != AttachEndpointProtocol.EndpointGeneration)
        {
            error = new EndpointActivationPreflightError("endpoint generation is incompatible");
            return false;
        }

        if (welcome.ProtocolMajor != (uint)ProtocolVersion.Major)
        {
            error = new EndpointActivationPreflightError("protocol major is incompatible");
            return false;
        }

        foreach (var required in AttachEndpointProtocol.RequiredCapabilities)
        {
            if (!welcome.Capabilities.Contains(required, StringComparer.Ordinal))
            {
                error = new EndpointActivationPreflightError(
                    $"endpoint capability {required} is missing");
                return false;
            }
        }

        foreach (var required in AttachEndpointProtocol.Methods)
        {
            if (!welcome.Methods.Contains(required, StringComparer.Ordinal))
            {
                error = new EndpointActivationPreflightError(
                    $"endpoint method {required} is missing");
                return false;
            }
        }

        if (!string.Equals(welcome.SnapshotCodec, AttachEndpointProtocol.SnapshotCodec, StringComparison.Ordinal)
            || !string.Equals(welcome.SurfaceCodec, AttachEndpointProtocol.SurfaceCodec, StringComparison.Ordinal)
            || !string.Equals(welcome.InputCodec, AttachEndpointProtocol.InputCodec, StringComparison.Ordinal))
        {
            error = new EndpointActivationPreflightError("endpoint codec negotiation failed");
            return false;
        }

        error = null;
        return true;
    }
}
