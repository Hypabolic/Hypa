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
                        var existingIndex = FindOwnedSessionIndex(
                            snapshot.Placements,
                            request.Owner,
                            muxIdentity);
                        var placements = snapshot.Placements.ToList();
                        if (existingIndex >= 0)
                        {
                            var current = placements[existingIndex];
                            if (current.Quic is null)
                            {
                                return (
                                    snapshot,
                                    PlacementOutcome<PlacementRecord>.Failure(
                                        PlacementReasons.MuxIdentityInvalid,
                                        "a Placement with this session already exists for the owner"));
                            }

                            var updated = current with
                            {
                                DisplayName = quicProfile.Label,
                                LastSeen = now,
                                Reachability = enrolled
                                    ? PlacementReachability.Reachable
                                    : string.Equals(
                                        current.Quic.Target,
                                        quicProfile.Target,
                                        StringComparison.Ordinal)
                                        ? current.Reachability
                                        : PlacementReachability.Unreachable,
                                Quic = quicProfile with { Id = current.Quic.Id, Enabled = true },
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

    private static int FindOwnedSessionIndex(
        IReadOnlyList<PlacementRecord> placements,
        DirectoryIdentity owner,
        MuxIdentity muxIdentity)
    {
        for (var i = 0; i < placements.Count; i++)
        {
            var row = placements[i];
            if (row.Owner == owner && row.MuxIdentity == muxIdentity)
                return i;
        }

        return -1;
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
