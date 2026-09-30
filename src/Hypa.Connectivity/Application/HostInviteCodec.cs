using System.Text.Json;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>Map host invite transfer and redeem documents through source-generated JSON.</summary>
public static class HostInviteCodec
{
    public static string Encode(HostInvite invite)
    {
        ArgumentNullException.ThrowIfNull(invite);
        var json = JsonSerializer.Serialize(
            ToDocument(invite),
            ConnectivityJsonContext.Default.HostInviteDocument);
        return HostInviteFormat.Wrap(json);
    }

    public static ConnectivityOutcome<HostInvite> Decode(string? value)
    {
        if (!HostInviteFormat.TryUnwrap(value, out var bytes, out var unwrapError))
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                ConnectivityReasons.InviteInvalid,
                unwrapError ?? "invite is invalid");
        }

        HostInviteDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(bytes, ConnectivityJsonContext.Default.HostInviteDocument);
        }
        catch (JsonException)
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                ConnectivityReasons.InviteInvalid,
                "invite is invalid");
        }

        if (document is null
            || !string.Equals(document.Type, HostInviteFormat.TypeName, StringComparison.Ordinal)
            || document.Schema != HostInviteFormat.Schema)
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                ConnectivityReasons.InviteInvalid,
                "invite is invalid");
        }

        if (!InviteId.TryParse(document.InviteId, out var inviteId))
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                ConnectivityReasons.InviteInvalid,
                "invite id is invalid");
        }

        if (!TryReadHosts(document, out var host, out var hosts, out var hostError))
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                ConnectivityReasons.InviteInvalid,
                hostError ?? "invite host is invalid");
        }

        if (!HostInviteFormat.TryNormalizeFingerprint(document.CertificateSha256, out var fingerprint, out var pinError))
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                ConnectivityReasons.InviteInvalid,
                pinError ?? "certificate fingerprint is invalid");
        }

        if (!HostInviteFormat.TryNormalizeSecret(document.Secret, out var secret, out var secretError))
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                ConnectivityReasons.InviteInvalid,
                secretError ?? "invite secret is invalid");
        }

        if (!ConnectivityTimestamp.TryParse(document.ExpiresAt, out var expiresAt))
        {
            return ConnectivityOutcome<HostInvite>.Failure(
                ConnectivityReasons.InviteInvalid,
                "invite expiry is invalid");
        }

        return ConnectivityOutcome<HostInvite>.Success(new HostInvite
        {
            InviteId = inviteId,
            Secret = secret,
            Host = host,
            Hosts = hosts,
            Port = document.Port,
            CertificateSha256 = fingerprint,
            ExpiresAt = expiresAt,
            Session = string.IsNullOrWhiteSpace(document.Session) ? null : document.Session.Trim(),
            Label = string.IsNullOrWhiteSpace(document.Label) ? null : document.Label.Trim(),
            TransferValue = HostInviteFormat.Wrap(
                System.Text.Encoding.UTF8.GetString(bytes)),
        });
    }

    public static bool LooksLikeRedeem(string? json) =>
        LooksLikeType(json, HostInviteRedeemCodec.TypeName);

    public static bool LooksLikeRevoke(string? json) =>
        LooksLikeType(json, HostInviteRevokeCodec.TypeName);

    internal static bool LooksLikeType(string? json, string typeName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (!parsed.RootElement.TryGetProperty("type", out var type)
                && !parsed.RootElement.TryGetProperty("Type", out type))
            {
                return false;
            }

            return type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), typeName, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadHosts(
        HostInviteDocument document,
        out string host,
        out IReadOnlyList<string> hosts,
        out string? error)
    {
        host = "";
        hosts = [];
        error = null;
        if (document.Hosts is { Count: > 0 })
        {
            if (document.Hosts.Count > HostInviteReach.MaxHosts)
            {
                error = "invite lists too many hosts";
                return false;
            }

            var list = new List<string>();
            foreach (var candidate in document.Hosts)
            {
                if (!HostInviteReach.TryValidate(candidate, document.Port, out var normalized, out error))
                    return false;
                if (!list.Exists(existing =>
                        string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(normalized);
                }
            }

            if (!string.IsNullOrWhiteSpace(document.Host))
            {
                if (!HostInviteReach.TryValidate(document.Host, document.Port, out var listed, out error))
                    return false;
                if (!string.Equals(listed, list[0], StringComparison.OrdinalIgnoreCase))
                {
                    error = "invite host is invalid";
                    return false;
                }
            }

            host = list[0];
            hosts = list;
            return true;
        }

        if (!HostInviteReach.TryValidate(document.Host, document.Port, out host, out error))
            return false;
        hosts = [host];
        return true;
    }

    private static HostInviteDocument ToDocument(HostInvite invite)
    {
        var hosts = invite.Hosts is { Count: > 0 }
            ? invite.Hosts.ToList()
            : new List<string> { invite.Host };
        return new HostInviteDocument
        {
            Type = HostInviteFormat.TypeName,
            Schema = HostInviteFormat.Schema,
            InviteId = invite.InviteId.Value,
            Host = hosts[0],
            Hosts = hosts,
            Port = invite.Port,
            CertificateSha256 = invite.CertificateSha256,
            Secret = invite.Secret,
            ExpiresAt = ConnectivityTimestamp.FormatUtc(invite.ExpiresAt),
            Session = invite.Session,
            Label = invite.Label,
        };
    }
}

/// <summary>First-line redeem document. Join bootstrap stays unchanged.</summary>
public static class HostInviteRedeemCodec
{
    public const string TypeName = "host_invite_redeem";
    public const int Schema = 1;

    public static string Write(HostInviteRedeemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(
            new HostInviteRedeemDocument
            {
                Type = TypeName,
                Schema = Schema,
                InviteId = request.InviteId.Value,
                Secret = request.Secret,
                DeviceId = request.DeviceId.Value,
                DevicePublicKey = request.DevicePublicKeySpkiBase64,
                IssuedAt = request.IssuedAt.ToUnixTimeSeconds(),
                Signature = request.Signature,
            },
            ConnectivityJsonContext.Default.HostInviteRedeemDocument);
    }

    public static ConnectivityOutcome<HostInviteRedeemRequest> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ConnectivityOutcome<HostInviteRedeemRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "redeem json is required");
        }

        HostInviteRedeemDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, ConnectivityJsonContext.Default.HostInviteRedeemDocument);
        }
        catch (JsonException)
        {
            return ConnectivityOutcome<HostInviteRedeemRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "redeem json is invalid");
        }

        if (document is null
            || !string.Equals(document.Type, TypeName, StringComparison.Ordinal)
            || document.Schema != Schema)
        {
            return ConnectivityOutcome<HostInviteRedeemRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "redeem document is invalid");
        }

        if (!InviteId.TryParse(document.InviteId, out var inviteId))
        {
            return ConnectivityOutcome<HostInviteRedeemRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "invite id is invalid");
        }

        if (!HostInviteFormat.TryNormalizeSecret(document.Secret, out var secret, out var secretError))
        {
            return ConnectivityOutcome<HostInviteRedeemRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                secretError ?? "invite secret is invalid");
        }

        if (!DeviceId.TryParse(document.DeviceId, out var deviceId))
        {
            return ConnectivityOutcome<HostInviteRedeemRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "device id is invalid");
        }

        if (string.IsNullOrWhiteSpace(document.DevicePublicKey))
        {
            return ConnectivityOutcome<HostInviteRedeemRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "device public key is invalid");
        }

        DateTimeOffset issuedAt;
        try
        {
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(document.IssuedAt);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ConnectivityOutcome<HostInviteRedeemRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "redeem issued_at is invalid");
        }

        return ConnectivityOutcome<HostInviteRedeemRequest>.Success(new HostInviteRedeemRequest
        {
            InviteId = inviteId,
            Secret = secret,
            DeviceId = deviceId,
            DevicePublicKeySpkiBase64 = document.DevicePublicKey.Trim(),
            IssuedAt = issuedAt,
            Signature = document.Signature ?? "",
        });
    }

    public static bool TryPeekInviteId(string? json, out InviteId inviteId)
    {
        inviteId = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (!parsed.RootElement.TryGetProperty("invite_id", out var raw)
                && !parsed.RootElement.TryGetProperty("InviteId", out raw))
            {
                return false;
            }

            return raw.ValueKind == JsonValueKind.String
                && InviteId.TryParse(raw.GetString(), out inviteId);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>First-line revoke document. Join bootstrap stays unchanged.</summary>
public static class HostInviteRevokeCodec
{
    public const string TypeName = "host_invite_revoke";
    public const int Schema = 1;

    public static string Write(HostInviteRevokeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.Serialize(
            new HostInviteRevokeDocument
            {
                Type = TypeName,
                Schema = Schema,
                DeviceId = request.DeviceId.Value,
                DevicePublicKey = request.DevicePublicKeySpkiBase64,
                IssuedAt = request.IssuedAt.ToUnixTimeSeconds(),
                Signature = request.Signature,
            },
            ConnectivityJsonContext.Default.HostInviteRevokeDocument);
    }

    public static ConnectivityOutcome<HostInviteRevokeRequest> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ConnectivityOutcome<HostInviteRevokeRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "revoke json is required");
        }

        HostInviteRevokeDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, ConnectivityJsonContext.Default.HostInviteRevokeDocument);
        }
        catch (JsonException)
        {
            return ConnectivityOutcome<HostInviteRevokeRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "revoke json is invalid");
        }

        if (document is null
            || !string.Equals(document.Type, TypeName, StringComparison.Ordinal)
            || document.Schema != Schema)
        {
            return ConnectivityOutcome<HostInviteRevokeRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "revoke document is invalid");
        }

        if (!DeviceId.TryParse(document.DeviceId, out var deviceId))
        {
            return ConnectivityOutcome<HostInviteRevokeRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "device id is invalid");
        }

        if (string.IsNullOrWhiteSpace(document.DevicePublicKey))
        {
            return ConnectivityOutcome<HostInviteRevokeRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "device public key is invalid");
        }

        DateTimeOffset issuedAt;
        try
        {
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(document.IssuedAt);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ConnectivityOutcome<HostInviteRevokeRequest>.Failure(
                ConnectivityReasons.InviteInvalid,
                "revoke issued_at is invalid");
        }

        return ConnectivityOutcome<HostInviteRevokeRequest>.Success(new HostInviteRevokeRequest
        {
            DeviceId = deviceId,
            DevicePublicKeySpkiBase64 = document.DevicePublicKey.Trim(),
            IssuedAt = issuedAt,
            Signature = document.Signature ?? "",
        });
    }
}
