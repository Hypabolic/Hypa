using System.Text.Json;
using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

/// <summary>
/// QUIC-backed peer Placement mutations. No remote preparation or secrets in catalog data.
/// </summary>
public sealed class QuicPlacementCatalogService : IQuicPlacementCatalog
{
    private const string RenameStoreFailedDetail = "Placement rename was not saved. Retry rename.";
    private const string RemoveStoreFailedDetail = "Placement remove was not saved. Retry remove.";
    private const string DisableStoreFailedDetail = "Placement disable was not saved. Retry disable.";

    private readonly IPlacementDirectoryStore _store;
    private readonly TimeProvider _clock;

    public QuicPlacementCatalogService(
        IPlacementDirectoryStore store,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> AddAsync(
        QuicPlacementAddRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PeerProfile.TryCreate(
                PeerProfile.NewId(),
                request.Label,
                request.Target,
                request.Session,
                enabled: true,
                PeerProviders.Quic,
                out var peer,
                out var profileError))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ProfileInvalid,
                profileError ?? "Invalid QUIC Placement fields. Check the label, target, and session.");
        }

        var placementId = PlacementId.New();
        peer = peer with { Id = placementId.Value };
        if (!QuicPlacementProfile.TryFromPeerProfile(peer, out var quicProfile, out var quicError))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ProfileInvalid,
                quicError ?? "Invalid QUIC Placement fields. Check the label, target, and session.");
        }

        if (!CertificateSha256Rules.TryNormalizeOptional(
                request.CertificateSha256,
                out var certificateSha256,
                out var pinError))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ProfileInvalid,
                pinError ?? "certificate fingerprint is invalid");
        }

        quicProfile = quicProfile with
        {
            CertificateSha256 = certificateSha256,
            EnrolledDeviceId = string.IsNullOrWhiteSpace(request.EnrolledDeviceId)
                ? null
                : request.EnrolledDeviceId.Trim(),
        };

        if (!MuxIdentity.TryFromSessionName(quicProfile.Session, out var muxIdentity))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.MuxIdentityInvalid,
                "session name does not produce a valid mux identity");
        }

        var now = _clock.GetUtcNow();
        var enrolled = !string.IsNullOrWhiteSpace(quicProfile.EnrolledDeviceId);
        var record = new PlacementRecord
        {
            Id = placementId,
            Owner = request.Owner,
            DisplayName = quicProfile.Label,
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = muxIdentity,
            Reachability = enrolled
                ? PlacementReachability.Reachable
                : PlacementReachability.Unreachable,
            LastSeen = now,
            Quic = quicProfile,
        };

        try
        {
            return await _store.MutateAsync(
                    snapshot =>
                    {
                        var existingIndex = FindOwnedDestinationIndex(
                            snapshot.Placements,
                            request.Owner,
                            quicProfile.Target,
                            quicProfile.Session);
                        var placements = snapshot.Placements.ToList();
                        if (existingIndex >= 0)
                        {
                            // Same owner, target, and session: refresh the row in place.
                            var current = placements[existingIndex];
                            var currentQuic = current.Quic!;
                            var keptDevice = quicProfile.EnrolledDeviceId ?? currentQuic.EnrolledDeviceId;
                            var keptPin = quicProfile.CertificateSha256 ?? currentQuic.CertificateSha256;
                            var refreshedEnrolled = !string.IsNullOrWhiteSpace(keptDevice);
                            var updated = current with
                            {
                                DisplayName = quicProfile.Label,
                                LastSeen = now,
                                Reachability = refreshedEnrolled
                                    ? PlacementReachability.Reachable
                                    : current.Reachability,
                                Quic = quicProfile with
                                {
                                    Id = currentQuic.Id,
                                    Enabled = true,
                                    EnrolledDeviceId = keptDevice,
                                    CertificateSha256 = keptPin,
                                },
                            };
                            placements[existingIndex] = updated;
                            return (
                                snapshot with { Placements = placements },
                                PlacementOutcome<PlacementRecord>.Success(updated));
                        }

                        placements.Add(record);
                        return (
                            snapshot with
                            {
                                Placements = placements,
                                SchemaVersion = ResolveSchema(snapshot, placements),
                            },
                            PlacementOutcome<PlacementRecord>.Success(record));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.StoreFailed,
                "Placement was not saved. Retry add-quic.");
        }
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> EnrollDeviceAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ProfileInvalid,
                "device id is required");
        }

        try
        {
            return await _store.MutateAsync(
                    snapshot =>
                    {
                        var index = FindOwnedQuicIndex(snapshot.Placements, owner, placementId);
                        if (index < 0)
                            return (snapshot, ProviderMismatchOrMissing(snapshot.Placements, placementId, owner));

                        var current = snapshot.Placements[index];
                        if (!string.IsNullOrWhiteSpace(current.Quic!.EnrolledDeviceId))
                            return (snapshot, PlacementOutcome<PlacementRecord>.Success(current));

                        var updated = current with
                        {
                            Quic = current.Quic with { EnrolledDeviceId = deviceId.Trim() },
                            Reachability = PlacementReachability.Reachable,
                            LastSeen = _clock.GetUtcNow(),
                        };
                        var placements = snapshot.Placements.ToList();
                        placements[index] = updated;
                        return (
                            snapshot with { Placements = placements },
                            PlacementOutcome<PlacementRecord>.Success(updated));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.StoreFailed,
                "Placement device was not saved. Run add-quic --device DEVICE_ID.");
        }
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> RenameAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        string label,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _store.MutateAsync(
                    snapshot =>
                    {
                        var index = FindOwnedQuicIndex(snapshot.Placements, owner, placementId);
                        if (index < 0)
                            return (snapshot, ProviderMismatchOrMissing(snapshot.Placements, placementId, owner));

                        var current = snapshot.Placements[index];
                        var quic = current.Quic!;
                        if (!PeerProfile.TryCreate(
                                quic.Id,
                                label,
                                quic.Target,
                                quic.Session,
                                quic.Enabled,
                                PeerProviders.Quic,
                                out var peer,
                                out var profileError))
                        {
                            return (
                                snapshot,
                                PlacementOutcome<PlacementRecord>.Failure(
                                    PlacementReasons.ProfileInvalid,
                                    profileError ?? "Invalid QUIC Placement fields. Check the label, target, and session."));
                        }

                        if (!QuicPlacementProfile.TryFromPeerProfile(peer, out var renamed, out var quicError))
                        {
                            return (
                                snapshot,
                                PlacementOutcome<PlacementRecord>.Failure(
                                    PlacementReasons.ProfileInvalid,
                                    quicError ?? PlacementReasons.ProfileInvalid));
                        }

                        renamed = renamed with
                        {
                            CertificateSha256 = quic.CertificateSha256,
                            EnrolledDeviceId = quic.EnrolledDeviceId,
                        };

                        var updated = current with
                        {
                            DisplayName = renamed.Label,
                            Quic = renamed,
                            LastSeen = _clock.GetUtcNow(),
                        };
                        var placements = snapshot.Placements.ToList();
                        placements[index] = updated;
                        return (
                            snapshot with { Placements = placements },
                            PlacementOutcome<PlacementRecord>.Success(updated));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.StoreFailed,
                RenameStoreFailedDetail);
        }
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> RemoveAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _store.MutateAsync(
                    snapshot =>
                    {
                        var index = FindOwnedQuicIndex(snapshot.Placements, owner, placementId);
                        if (index < 0)
                            return (snapshot, ProviderMismatchOrMissing(snapshot.Placements, placementId, owner));

                        var removed = snapshot.Placements[index];
                        var placements = snapshot.Placements.ToList();
                        placements.RemoveAt(index);
                        var grants = snapshot.PlacementGrants
                            .Where(g => g.PlacementId != placementId)
                            .ToList();
                        return (
                            snapshot with
                            {
                                Placements = placements,
                                PlacementGrants = grants,
                                SchemaVersion = ResolveSchema(snapshot, placements),
                            },
                            PlacementOutcome<PlacementRecord>.Success(removed));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.StoreFailed,
                RemoveStoreFailedDetail);
        }
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> EnableAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _store.MutateAsync(
                    snapshot =>
                    {
                        var index = FindOwnedQuicIndex(snapshot.Placements, owner, placementId);
                        if (index < 0)
                            return (snapshot, ProviderMismatchOrMissing(snapshot.Placements, placementId, owner));

                        var current = snapshot.Placements[index];
                        var enabledProfile = current.Quic! with { Enabled = true };
                        var updated = current with
                        {
                            Quic = enabledProfile,
                            LastSeen = _clock.GetUtcNow(),
                        };
                        var placements = snapshot.Placements.ToList();
                        placements[index] = updated;
                        return (
                            snapshot with { Placements = placements },
                            PlacementOutcome<PlacementRecord>.Success(updated));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.StoreFailed,
                "Placement enable was not saved. Retry enable.");
        }
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> DisableAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _store.MutateAsync(
                    snapshot =>
                    {
                        var index = FindOwnedQuicIndex(snapshot.Placements, owner, placementId);
                        if (index < 0)
                            return (snapshot, ProviderMismatchOrMissing(snapshot.Placements, placementId, owner));

                        var current = snapshot.Placements[index];
                        var updated = current with
                        {
                            Quic = current.Quic! with { Enabled = false },
                            LastSeen = _clock.GetUtcNow(),
                        };
                        var placements = snapshot.Placements.ToList();
                        placements[index] = updated;
                        return (
                            snapshot with { Placements = placements },
                            PlacementOutcome<PlacementRecord>.Success(updated));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.StoreFailed,
                DisableStoreFailedDetail);
        }
    }

    private static int ResolveSchema(
        PlacementDirectorySnapshot snapshot,
        IReadOnlyList<PlacementRecord> placements)
    {
        if (snapshot.SchemaVersion >= 2)
            return 2;
        if (placements.Any(p => p.Ssh is not null || p.Quic is not null))
            return 2;
        return snapshot.SchemaVersion;
    }

    private static int FindOwnedDestinationIndex(
        IReadOnlyList<PlacementRecord> placements,
        DirectoryIdentity owner,
        string target,
        string session)
    {
        for (var i = 0; i < placements.Count; i++)
        {
            var row = placements[i];
            if (row.Owner == owner
                && row.Quic is { } quic
                && SameTarget(quic.Target, target)
                && string.Equals(quic.Session.Trim(), session.Trim(), StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Two targets are one destination when the host (case-insensitive, or the same
    /// IP address) and the effective port are equal. The parser is the one the dial
    /// uses, so a missing port is the default dial port. The stored text is not changed.
    /// A target that does not parse is compared as trimmed text.
    /// </summary>
    internal static bool SameTarget(string left, string right)
    {
        if (!QuicReachTarget.TryParseHostPort(left, out var leftHost, out var leftPort, out _)
            || !QuicReachTarget.TryParseHostPort(right, out var rightHost, out var rightPort, out _))
        {
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (leftPort != rightPort)
            return false;
        if (System.Net.IPAddress.TryParse(leftHost, out var leftIp)
            && System.Net.IPAddress.TryParse(rightHost, out var rightIp))
        {
            return leftIp.Equals(rightIp);
        }

        return string.Equals(leftHost, rightHost, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindOwnedQuicIndex(
        IReadOnlyList<PlacementRecord> placements,
        DirectoryIdentity owner,
        PlacementId placementId)
    {
        for (var i = 0; i < placements.Count; i++)
        {
            var row = placements[i];
            if (row.Id == placementId && row.Owner == owner && row.Quic is not null)
                return i;
        }

        return -1;
    }

    private static PlacementOutcome<PlacementRecord> ProviderMismatchOrMissing(
        IReadOnlyList<PlacementRecord> placements,
        PlacementId placementId,
        DirectoryIdentity owner)
    {
        var row = placements.FirstOrDefault(p => p.Id == placementId);
        if (row is null)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.PlacementNotFound,
                "placement is not in the directory");
        }

        if (row.Owner != owner)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.Unauthorized,
                "only the owner can mutate QUIC profiles");
        }

        return PlacementOutcome<PlacementRecord>.Failure(
            PlacementReasons.ProviderMismatch,
            "This Placement does not use QUIC.");
    }
}
