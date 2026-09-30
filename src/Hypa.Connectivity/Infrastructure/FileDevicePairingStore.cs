using System.Text.Json;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// JSON pairing store. Path is pairing.json. Private keys are not stored here.
/// </summary>
public sealed class FileDevicePairingStore : IDevicePairingStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileDevicePairingStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _path = DevicePairingStatePaths.StoreFile(directory);
    }

    public string FilePath => _path;

    public async ValueTask<DevicePairingSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return new DevicePairingSnapshot();

            var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize(json, ConnectivityJsonContext.Default.DevicePairingDocument);
            if (document is null)
                throw new InvalidDataException("pairing JSON is empty");
            if (document.Schema is not (1 or 2))
                throw new InvalidDataException("pairing schema is not 1 or 2");
            RequireSchemaArrays(json, document.Schema);
            return ToSnapshot(document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        DevicePairingSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = ToDocument(snapshot);
            var json = JsonSerializer.Serialize(document, ConnectivityJsonContext.Default.DevicePairingDocument);
            var temp = _path + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
            RestrictFile(_path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void RequireSchemaArrays(string json, int schema)
    {
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("pairing JSON is empty");
        RequireArray(root, "devices");
        RequireArray(root, "pairing_challenges");
        RequireArray(root, "join_capabilities");
        if (schema >= 2)
            RequireArray(root, "host_invites");
    }

    private static void RequireArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("pairing arrays are missing");
    }

    private static DevicePairingSnapshot ToSnapshot(DevicePairingDocument document)
    {
        if (document.Devices is null
            || document.PairingChallenges is null
            || document.JoinCapabilities is null)
        {
            throw new InvalidDataException("pairing arrays are missing");
        }

        if (!OperatorIdentity.TryParse(document.OperatorId, out var operatorId) || !operatorId.IsLocalSelfHosted)
            throw new InvalidDataException("pairing operator id is invalid");

        var devices = new List<DeviceRecord>(document.Devices.Count);
        foreach (var row in document.Devices)
        {
            if (row is null)
                throw new InvalidDataException("device row is missing");
            if (!DeviceId.TryParse(row.DeviceId, out var id))
                throw new InvalidDataException("device id is invalid");
            if (!DeviceTrustStatusRules.IsDefined(row.Status))
                throw new InvalidDataException("device status is invalid");
            if (string.IsNullOrWhiteSpace(row.PublicKeySpkiBase64))
                throw new InvalidDataException("device public key is invalid");
            if (!DateTimeOffset.TryParse(row.CreatedAt, out var createdAt))
                throw new InvalidDataException("device created_at is invalid");

            DateTimeOffset? approvedAt = null;
            if (!string.IsNullOrWhiteSpace(row.ApprovedAt))
            {
                if (!DateTimeOffset.TryParse(row.ApprovedAt, out var parsedApproved))
                    throw new InvalidDataException("device approved_at is invalid");
                approvedAt = parsedApproved;
            }

            DeviceId? approverDevice = null;
            if (!string.IsNullOrWhiteSpace(row.ApproverDeviceId))
            {
                if (!DeviceId.TryParse(row.ApproverDeviceId, out var parsedApprover))
                    throw new InvalidDataException("approver device id is invalid");
                approverDevice = parsedApprover;
            }

            devices.Add(new DeviceRecord
            {
                Id = id,
                PublicKeySpkiBase64 = row.PublicKeySpkiBase64,
                Status = row.Status,
                CreatedAt = createdAt,
                ApprovedAt = approvedAt,
                ApprovedBy = row.ApprovedBy,
                ApproverDeviceId = approverDevice,
            });
        }

        var challenges = new List<PairingChallenge>(document.PairingChallenges.Count);
        foreach (var row in document.PairingChallenges)
        {
            if (row is null)
                throw new InvalidDataException("pairing challenge is missing");
            if (string.IsNullOrWhiteSpace(row.CodeSha256))
                throw new InvalidDataException("pairing code hash is invalid");
            if (!DeviceId.TryParse(row.DeviceId, out var id))
                throw new InvalidDataException("challenge device id is invalid");
            if (!DateTimeOffset.TryParse(row.ExpiresAt, out var expiresAt))
                throw new InvalidDataException("challenge expiry is invalid");
            challenges.Add(new PairingChallenge
            {
                CodeSha256 = row.CodeSha256,
                DeviceId = id,
                ExpiresAt = expiresAt,
                Consumed = row.Consumed,
            });
        }

        var capabilities = new List<IssuedJoinCapability>(document.JoinCapabilities.Count);
        foreach (var row in document.JoinCapabilities)
        {
            if (row is null)
                throw new InvalidDataException("join capability is missing");
            if (!OperatorIdentity.TryParse(row.OperatorId, out var capabilityOperator)
                || !capabilityOperator.IsLocalSelfHosted)
            {
                throw new InvalidDataException("capability operator id is invalid");
            }

            if (!PlacementId.TryParse(row.PlacementId, out var placementId))
                throw new InvalidDataException("capability placement id is invalid");
            if (!DeviceId.TryParse(row.DeviceId, out var id))
                throw new InvalidDataException("capability device id is invalid");
            if (!JoinRoleRules.IsDefined(row.Role))
                throw new InvalidDataException("capability role is invalid");
            if (!JoinNonce.TryParse(row.Nonce, out var nonce))
                throw new InvalidDataException("capability nonce is invalid");
            if (!ConnectivityTimestamp.TryParse(row.ExpiresAt, out var expiresAt))
                throw new InvalidDataException("capability expiry is invalid");
            capabilities.Add(new IssuedJoinCapability
            {
                OperatorId = capabilityOperator,
                PlacementId = placementId,
                DeviceId = id,
                Role = row.Role,
                ExpiresAt = expiresAt,
                Nonce = nonce,
                Spent = row.Spent,
            });
        }

        var invites = new List<HostInviteRecord>();
        if (document.HostInvites is not null)
        {
            foreach (var row in document.HostInvites)
            {
                if (row is null)
                    throw new InvalidDataException("host invite is missing");
                if (!InviteId.TryParse(row.InviteId, out var inviteId))
                    throw new InvalidDataException("invite id is invalid");
                if (string.IsNullOrWhiteSpace(row.SecretSha256))
                    throw new InvalidDataException("invite secret hash is invalid");
                if (!DateTimeOffset.TryParse(row.ExpiresAt, out var expiresAt))
                    throw new InvalidDataException("invite expiry is invalid");
                DeviceId? redeemedBy = null;
                if (!string.IsNullOrWhiteSpace(row.RedeemedBy))
                {
                    if (!DeviceId.TryParse(row.RedeemedBy, out var parsedRedeemed))
                        throw new InvalidDataException("invite redeemed_by is invalid");
                    redeemedBy = parsedRedeemed;
                }

                invites.Add(new HostInviteRecord
                {
                    Id = inviteId,
                    SecretSha256 = row.SecretSha256,
                    ExpiresAt = expiresAt,
                    Consumed = row.Consumed,
                    RedeemedBy = redeemedBy,
                    FailedAttempts = row.FailedAttempts,
                });
            }
        }

        return new DevicePairingSnapshot
        {
            OperatorId = operatorId,
            Devices = devices,
            PairingChallenges = challenges,
            JoinCapabilities = capabilities,
            HostInvites = invites,
        };
    }

    private static DevicePairingDocument ToDocument(DevicePairingSnapshot snapshot) =>
        new()
        {
            Schema = 2,
            OperatorId = snapshot.OperatorId.Value,
            Devices = snapshot.Devices.Select(d => new DeviceRecordDocument
            {
                DeviceId = d.Id.Value,
                PublicKeySpkiBase64 = d.PublicKeySpkiBase64,
                Status = d.Status,
                CreatedAt = d.CreatedAt.ToUniversalTime().ToString("O"),
                ApprovedAt = d.ApprovedAt?.ToUniversalTime().ToString("O"),
                ApprovedBy = d.ApprovedBy,
                ApproverDeviceId = d.ApproverDeviceId?.Value,
            }).ToList(),
            PairingChallenges = snapshot.PairingChallenges.Select(c => new PairingChallengeDocument
            {
                CodeSha256 = c.CodeSha256,
                DeviceId = c.DeviceId.Value,
                ExpiresAt = c.ExpiresAt.ToUniversalTime().ToString("O"),
                Consumed = c.Consumed,
            }).ToList(),
            JoinCapabilities = snapshot.JoinCapabilities.Select(c => new IssuedJoinCapabilityDocument
            {
                OperatorId = c.OperatorId.Value,
                PlacementId = c.PlacementId.Value,
                DeviceId = c.DeviceId.Value,
                Role = c.Role,
                ExpiresAt = ConnectivityTimestamp.FormatUtc(c.ExpiresAt),
                Nonce = c.Nonce.Value,
                Spent = c.Spent,
            }).ToList(),
            HostInvites = snapshot.HostInvites.Select(i => new HostInviteRecordDocument
            {
                InviteId = i.Id.Value,
                SecretSha256 = i.SecretSha256,
                ExpiresAt = i.ExpiresAt.ToUniversalTime().ToString("O"),
                Consumed = i.Consumed,
                RedeemedBy = i.RedeemedBy?.Value,
                FailedAttempts = i.FailedAttempts,
            }).ToList(),
        };

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
