using System.Text.Json;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>Map the versioned join envelope through source-generated JSON.</summary>
public static class JoinBootstrapCodec
{
    public static JoinBootstrapDocument ToDocument(JoinBootstrap bootstrap) =>
        new()
        {
            ProtocolVersion = bootstrap.ProtocolVersion.ToString(),
            Role = bootstrap.Role,
            PlacementId = bootstrap.PlacementId.Value,
            JoinNonce = bootstrap.Nonce.Value,
            StreamClass = bootstrap.StreamClass,
            Capability = ToDocument(bootstrap.Capability),
            Audience = bootstrap.Audience,
            TenantScope = bootstrap.TenantScope,
            EphPublicKey = bootstrap.EphPublicKey,
            EphPublicMac = bootstrap.EphPublicMac,
            DeviceSignature = bootstrap.DeviceSignature,
        };

    public static JoinCapabilityDocument ToDocument(JoinCapability capability) =>
        new()
        {
            OperatorId = capability.OperatorId.Value,
            PlacementId = capability.PlacementId.Value,
            DeviceId = capability.DeviceId.Value,
            Role = capability.Role,
            ExpiresAt = ConnectivityTimestamp.FormatUtc(capability.ExpiresAt),
            Nonce = capability.Nonce.Value,
        };

    public static ConnectivityOutcome<JoinCapability> FromCapabilityDocument(
        JoinCapabilityDocument? document)
    {
        if (document is null)
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability is required");
        }

        if (!PlacementId.TryParse(document.PlacementId, out var placementId))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability placement id is invalid");
        }

        OperatorIdentity operatorId;
        if (string.IsNullOrWhiteSpace(document.OperatorId))
            operatorId = OperatorIdentity.LocalSelfHosted;
        else if (!OperatorIdentity.TryParse(document.OperatorId, out operatorId))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability operator id is invalid");
        }

        if (!DeviceId.TryParse(document.DeviceId, out var deviceId))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "device id is invalid");
        }

        if (!JoinNonce.TryParse(document.Nonce, out var capabilityNonce))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability nonce is invalid");
        }

        if (!ConnectivityTimestamp.TryParse(document.ExpiresAt, out var expiresAt))
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability expiry is invalid");
        }

        if (document.Role is not JoinRole capabilityRole)
        {
            return ConnectivityOutcome<JoinCapability>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability role is required");
        }

        return ConnectivityOutcome<JoinCapability>.Success(new JoinCapability
        {
            OperatorId = operatorId,
            PlacementId = placementId,
            DeviceId = deviceId,
            Role = capabilityRole,
            ExpiresAt = expiresAt,
            Nonce = capabilityNonce,
        });
    }

    public static string Write(JoinBootstrap bootstrap) =>
        JsonSerializer.Serialize(ToDocument(bootstrap), ConnectivityJsonContext.Default.JoinBootstrapDocument);

    public static string WriteResult(JoinResultDocument document) =>
        JsonSerializer.Serialize(document, ConnectivityJsonContext.Default.JoinResultDocument);

    public static ConnectivityOutcome<JoinBootstrap> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "bootstrap json is required");
        }

        JoinBootstrapDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, ConnectivityJsonContext.Default.JoinBootstrapDocument);
        }
        catch (JsonException)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "bootstrap json is invalid");
        }

        if (document is null)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "bootstrap json is required");
        }

        return FromDocument(document);
    }

    public static ConnectivityOutcome<JoinBootstrap> FromDocument(JoinBootstrapDocument document)
    {
        if (!ConnectivityProtocolVersion.TryParse(document.ProtocolVersion, out var version))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "protocol version is invalid");
        }

        if (!JoinNonce.TryParse(document.JoinNonce, out var nonce))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "join nonce is invalid");
        }

        var capabilityDocument = document.Capability;
        if (capabilityDocument is null)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability is required");
        }

        if (!PlacementId.TryParse(document.PlacementId, out var placementId))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "placement id is invalid");
        }

        if (!PlacementId.TryParse(capabilityDocument.PlacementId, out var capabilityPlacementId))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability placement id is invalid");
        }

        if (!OperatorIdentity.TryParse(capabilityDocument.OperatorId, out var operatorId))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability operator id is invalid");
        }

        if (!DeviceId.TryParse(capabilityDocument.DeviceId, out var deviceId))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "device id is invalid");
        }

        if (!JoinNonce.TryParse(capabilityDocument.Nonce, out var capabilityNonce))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability nonce is invalid");
        }

        if (!ConnectivityTimestamp.TryParse(capabilityDocument.ExpiresAt, out var expiresAt))
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability expiry is invalid");
        }

        if (document.Role is not JoinRole role)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "role is required");
        }

        if (document.StreamClass is not StreamClass streamClass)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "stream class is required");
        }

        if (capabilityDocument.Role is not JoinRole capabilityRole)
        {
            return ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "capability role is required");
        }

        var bootstrap = new JoinBootstrap
        {
            ProtocolVersion = version,
            Role = role,
            PlacementId = placementId,
            Nonce = nonce,
            StreamClass = streamClass,
            Capability = new JoinCapability
            {
                OperatorId = operatorId,
                PlacementId = capabilityPlacementId,
                DeviceId = deviceId,
                Role = capabilityRole,
                ExpiresAt = expiresAt,
                Nonce = capabilityNonce,
            },
            Audience = document.Audience,
            TenantScope = document.TenantScope,
            EphPublicKey = document.EphPublicKey,
            EphPublicMac = document.EphPublicMac,
            DeviceSignature = document.DeviceSignature,
        };
        return JoinBootstrapRules.Validate(bootstrap);
    }

    public static bool TryPeekJoinNonce(string? json, out JoinNonce nonce)
    {
        nonce = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.NameEquals("join_nonce"))
                    continue;
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
                return JoinNonce.TryParse(value, out nonce);
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static ConnectivityOutcome<JoinResultDocument> ReadResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ConnectivityOutcome<JoinResultDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "join result json is required");
        }

        try
        {
            var document = JsonSerializer.Deserialize(json, ConnectivityJsonContext.Default.JoinResultDocument);
            if (document is null)
            {
                return ConnectivityOutcome<JoinResultDocument>.Failure(
                    ConnectivityReasons.BootstrapInvalid,
                    "join result json is required");
            }

            return ConnectivityOutcome<JoinResultDocument>.Success(document);
        }
        catch (JsonException)
        {
            return ConnectivityOutcome<JoinResultDocument>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "join result json is invalid");
        }
    }
}
