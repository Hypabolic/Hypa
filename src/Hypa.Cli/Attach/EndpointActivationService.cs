using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Cli.Attach;

/// Resolve-only helper. Activation lives in PendingEndpointActivation.
internal sealed class EndpointActivationService
{
    internal Result<(EndpointActivationBeginRequest Request, EndpointActivationLease Target, EndpointActivationLease? Source), EndpointActivationPreflightError>
        ResolveLeases(
            string clientId,
            AttachGeometry geometry,
            EndpointActivationIntent target,
            AttachEndpointConnectPreflightResult preflight,
            string targetEndpointId,
            string? sourceEndpointId,
            bool sourceAvailable)
    {
        var targetLease = new EndpointActivationLease
        {
            EndpointId = targetEndpointId,
            ConnectionGeneration = preflight.TargetConnectionGeneration,
            BootId = preflight.TargetWelcome.BootId,
            MinimumProjectionRevision = 0,
            ClientId = clientId,
            LeaseId = string.Empty,
        };
        EndpointActivationLease? sourceLease = null;
        if (sourceAvailable && sourceEndpointId is not null)
        {
            sourceLease = new EndpointActivationLease
            {
                EndpointId = sourceEndpointId,
                ConnectionGeneration = preflight.SourceConnectionGeneration,
                BootId = preflight.SourceWelcome?.BootId ?? string.Empty,
                MinimumProjectionRevision = 0,
                ClientId = clientId,
                LeaseId = "source",
            };
        }

        var request = new EndpointActivationBeginRequest
        {
            ClientId = clientId,
            Geometry = geometry,
            Target = target,
            Source = sourceLease,
            SourceAvailable = sourceAvailable && sourceLease is not null,
            HostFocused = true,
            Epoch = 1,
            TargetLease = targetLease,
        };

        var error = AttachEndpointPreflight.ValidateBegin(
            request,
            preflight.TargetWelcome,
            preflight.SourceWelcome);
        if (error is not null)
            return Result<(EndpointActivationBeginRequest, EndpointActivationLease, EndpointActivationLease?), EndpointActivationPreflightError>.Fail(error);

        return Result<(EndpointActivationBeginRequest, EndpointActivationLease, EndpointActivationLease?), EndpointActivationPreflightError>.Ok(
            (request, targetLease, sourceLease));
    }
}
