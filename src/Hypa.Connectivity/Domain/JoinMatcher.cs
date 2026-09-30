namespace Hypa.Connectivity.Domain;

/// <summary>
/// Bind two outbound bootstrap legs. Anonymous streams never join.
/// </summary>
public static class JoinMatcher
{
    public static ConnectivityOutcome Admit(
        JoinBootstrap? bootstrap,
        RendezvousRelayIdentity relay,
        DateTimeOffset utcNow,
        bool enforceSelfHostedLocalOperator = true,
        bool enforceCapabilityExpiry = true)
    {
        var validated = JoinBootstrapRules.Validate(bootstrap);
        if (!validated.Ok || validated.Value is null)
        {
            return ConnectivityOutcome.Failure(
                validated.Reason ?? ConnectivityReasons.BootstrapInvalid,
                validated.Detail ?? "bootstrap is required");
        }

        var value = validated.Value;
        if (!value.ProtocolVersion.Equals(relay.ProtocolVersion))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "protocol version does not match relay");
        }

        if (!string.Equals(value.Audience, relay.Audience, StringComparison.Ordinal))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "audience does not match relay");
        }

        if (!string.Equals(value.TenantScope, relay.TenantScope, StringComparison.Ordinal))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "tenant scope does not match relay");
        }

        var capability = value.Capability;
        if (capability.PlacementId != value.PlacementId)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "capability placement id does not match bootstrap");
        }

        if (capability.Role != value.Role)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "capability role does not match bootstrap");
        }

        if (!string.Equals(capability.Nonce.Value, value.Nonce.Value, StringComparison.Ordinal))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "capability nonce does not match bootstrap");
        }

        if (enforceSelfHostedLocalOperator
            && relay.Deployment is RendezvousDeployment.SelfHosted
            && !capability.OperatorId.IsLocalSelfHosted)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinDenied,
                "capability operator must be local-operator identity");
        }

        if (enforceCapabilityExpiry && capability.ExpiresAt <= utcNow)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.JoinExpired,
                "join capability expired");
        }

        return ConnectivityOutcome.Success();
    }

    public static ConnectivityOutcome<JoinMatch> Bind(
        JoinBootstrap first,
        JoinBootstrap second,
        RendezvousRelayIdentity relay,
        DateTimeOffset utcNow)
    {
        var firstAdmit = Admit(first, relay, utcNow);
        if (!firstAdmit.Ok)
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                firstAdmit.Reason ?? ConnectivityReasons.JoinDenied,
                firstAdmit.Detail ?? "first bootstrap denied");
        }

        var secondAdmit = Admit(second, relay, utcNow);
        if (!secondAdmit.Ok)
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                secondAdmit.Reason ?? ConnectivityReasons.JoinDenied,
                secondAdmit.Detail ?? "second bootstrap denied");
        }

        if (!JoinRoleRules.AreComplementary(first.Role, second.Role))
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                ConnectivityReasons.JoinDenied,
                "roles must be mux and client");
        }

        if (first.PlacementId != second.PlacementId)
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                ConnectivityReasons.JoinDenied,
                "placement id does not match");
        }

        if (first.Capability.OperatorId != second.Capability.OperatorId)
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                ConnectivityReasons.JoinDenied,
                "operator does not match");
        }

        if (!string.Equals(first.Nonce.Value, second.Nonce.Value, StringComparison.Ordinal))
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                ConnectivityReasons.JoinDenied,
                "join nonce does not match");
        }

        if (first.StreamClass != second.StreamClass)
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                ConnectivityReasons.JoinDenied,
                "stream class does not match");
        }

        if (!first.ProtocolVersion.Equals(second.ProtocolVersion))
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                ConnectivityReasons.JoinDenied,
                "protocol version does not match");
        }

        if (!string.Equals(first.Audience, second.Audience, StringComparison.Ordinal)
            || !string.Equals(first.TenantScope, second.TenantScope, StringComparison.Ordinal))
        {
            return ConnectivityOutcome<JoinMatch>.Failure(
                ConnectivityReasons.JoinDenied,
                "audience or tenant scope does not match");
        }

        return ConnectivityOutcome<JoinMatch>.Success(new JoinMatch
        {
            First = ToBinding(first, second.EphPublicKey, second.EphPublicMac),
            Second = ToBinding(second, first.EphPublicKey, first.EphPublicMac),
        });
    }

    public static JoinBinding ToBinding(
        JoinBootstrap bootstrap,
        string peerEphPublicKey = "",
        string peerEphPublicMac = "") =>
        new()
        {
            PlacementId = bootstrap.PlacementId,
            StreamClass = bootstrap.StreamClass,
            Role = bootstrap.Role,
            PeerEphPublicKey = peerEphPublicKey,
            PeerEphPublicMac = peerEphPublicMac,
        };
}
