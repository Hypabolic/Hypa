using System.Text.Json;
using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

/// <summary>
// / SSH-backed peer Placement mutations.
/// </summary>
public sealed class SshPlacementCatalogService : ISshPlacementCatalog
{
    private const string AddStoreFailedDetail =
        "Remote session is ready. Placement was not saved. Retry add-ssh.";
    private const string EnableStoreFailedDetail =
        "Remote session is ready. Placement remains disabled. Retry enable.";
    private const string RenameStoreFailedDetail =
        "Placement rename was not saved. Retry rename.";
    private const string RemoveStoreFailedDetail =
        "Placement remove was not saved. Retry remove.";
    private const string DisableStoreFailedDetail =
        "Placement disable was not saved. Retry disable.";
    private const string EnableCatalogChangedDetail =
        "Placement catalog changed during preparation. Retry enable.";

    private readonly IPlacementDirectoryStore _store;
    private readonly ISshPlacementPreparation _preparation;
    private readonly TimeProvider _clock;

    public SshPlacementCatalogService(
        IPlacementDirectoryStore store,
        ISshPlacementPreparation preparation,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> AddAsync(
        SshPlacementAddRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!PeerProfile.TryCreate(
                PeerProfile.NewId(),
                request.Label,
                request.Target,
                request.Session,
                enabled: true,
                PeerProviders.Ssh,
                out var peer,
                out var profileError))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ProfileInvalid,
                profileError ?? "Invalid SSH Placement fields. Check the label, target, and session.");
        }

        var placementId = PlacementId.New();
        peer = peer with { Id = placementId.Value };
        if (!SshPlacementProfile.TryFromPeerProfile(peer, out var sshProfile, out var sshError))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ProfileInvalid,
                sshError ?? "Invalid SSH Placement fields. Check the label, target, and session.");
        }

        var prepared = await _preparation.PrepareAsync(peer, cancellationToken).ConfigureAwait(false);
        if (!prepared.Ok)
        {
            var label = sshProfile.Label;
            return PlacementOutcome<PlacementRecord>.Failure(
                prepared.Reason ?? PlacementReasons.PreparationFailed,
                prepared.Detail ?? $"Remote preparation failed for {label}. Placement was not saved.");
        }

        var muxIdentity = prepared.Value;
        var now = _clock.GetUtcNow();
        var record = new PlacementRecord
        {
            Id = placementId,
            Owner = request.Owner,
            DisplayName = sshProfile.Label,
            Kind = PlacementDirectoryKind.Peer,
            MuxIdentity = muxIdentity,
            Reachability = PlacementReachability.Unreachable,
            LastSeen = now,
            Ssh = sshProfile,
        };

        try
        {
            var saved = await _store.MutateAsync(
                    snapshot =>
                    {
                        var placements = snapshot.Placements.ToList();
                        placements.Add(record);
                        var schema = snapshot.SchemaVersion >= 2
                            || placements.Any(p => p.Ssh is not null || p.Quic is not null)
                            ? 2
                            : snapshot.SchemaVersion;
                        return (
                            snapshot with
                            {
                                Placements = placements,
                                SchemaVersion = schema,
                            },
                            record);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return PlacementOutcome<PlacementRecord>.Success(saved);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.StoreFailed,
                AddStoreFailedDetail);
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
                        var index = FindOwnedSshIndex(snapshot.Placements, owner, placementId);
                        if (index < 0)
                            return (snapshot, ProviderMismatchOrMissing(snapshot.Placements, placementId, owner));

                        var current = snapshot.Placements[index];
                        var ssh = current.Ssh!;
                        if (!PeerProfile.TryCreate(
                                ssh.Id,
                                label,
                                ssh.Target,
                                ssh.Session,
                                ssh.Enabled,
                                PeerProviders.Ssh,
                                out var peer,
                                out var profileError))
                        {
                            return (
                                snapshot,
                                PlacementOutcome<PlacementRecord>.Failure(
                                    PlacementReasons.ProfileInvalid,
                                    profileError ?? "Invalid SSH Placement fields. Check the label, target, and session."));
                        }

                        if (!SshPlacementProfile.TryFromPeerProfile(peer, out var renamed, out var sshError))
                        {
                            return (
                                snapshot,
                                PlacementOutcome<PlacementRecord>.Failure(
                                    PlacementReasons.ProfileInvalid,
                                    sshError ?? "Invalid SSH Placement fields. Check the label, target, and session."));
                        }

                        var placements = snapshot.Placements.ToList();
                        placements[index] = current with
                        {
                            DisplayName = renamed.Label,
                            Ssh = renamed with { Attention = ssh.Attention },
                            LastSeen = _clock.GetUtcNow(),
                        };
                        return (
                            snapshot with { Placements = placements },
                            PlacementOutcome<PlacementRecord>.Success(placements[index]));
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
                        var index = FindOwnedSshIndex(snapshot.Placements, owner, placementId);
                        if (index < 0)
                            return (snapshot, ProviderMismatchOrMissing(snapshot.Placements, placementId, owner));

                        var removed = snapshot.Placements[index];
                        var placements = snapshot.Placements.ToList();
                        placements.RemoveAt(index);
                        var grants = snapshot.PlacementGrants
                            .Where(g => g.PlacementId != placementId)
                            .ToList();
                        return (
                            snapshot with { Placements = placements, PlacementGrants = grants },
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
        var loaded = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok || loaded.Value is null)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                loaded.Reason ?? PlacementReasons.DirectoryInvalid,
                loaded.Detail ?? "directory load failed");
        }

        var snapshot = loaded.Value;
        var index = FindOwnedSshIndex(snapshot.Placements, owner, placementId);
        if (index < 0)
            return ProviderMismatchOrMissing(snapshot.Placements, placementId, owner);

        var current = snapshot.Placements[index];
        var ssh = current.Ssh!;
        if (ssh.Enabled)
            return PlacementOutcome<PlacementRecord>.Success(current);

        var expectedRevision = snapshot.Revision;
        var peer = ssh.ToPeerProfile() with { Enabled = true };
        var prepared = await _preparation.PrepareAsync(peer, cancellationToken).ConfigureAwait(false);
        if (!prepared.Ok)
        {
            await TryPersistFailedEnableAttentionAsync(
                    owner,
                    placementId,
                    expectedRevision,
                    prepared,
                    cancellationToken)
                .ConfigureAwait(false);
            var label = ssh.Label;
            return PlacementOutcome<PlacementRecord>.Failure(
                prepared.Reason ?? PlacementReasons.PreparationFailed,
                prepared.Detail ?? $"Remote preparation failed for {label}. Placement remains disabled.");
        }

        try
        {
            return await _store.MutateAsync(
                    latest =>
                    {
                        var latestIndex = FindOwnedSshIndex(latest.Placements, owner, placementId);
                        if (latestIndex < 0)
                        {
                            return (
                                latest,
                                PlacementOutcome<PlacementRecord>.Failure(
                                    PlacementReasons.PlacementNotFound,
                                    "placement is not in the directory"));
                        }

                        var latestRow = latest.Placements[latestIndex];
                        if (latestRow.Ssh is null)
                        {
                            return (
                                latest,
                                PlacementOutcome<PlacementRecord>.Failure(
                                    PlacementReasons.ProviderMismatch,
                                    "This Placement does not use SSH."));
                        }

                        if (latestRow.Ssh.Enabled)
                            return (latest, PlacementOutcome<PlacementRecord>.Success(latestRow));

                        if (latest.Revision != expectedRevision)
                        {
                            return (
                                latest,
                                PlacementOutcome<PlacementRecord>.Failure(
                                    PlacementReasons.StoreFailed,
                                    EnableCatalogChangedDetail));
                        }

                        var enabledProfile = latestRow.Ssh with { Enabled = true, Attention = null };
                        var placements = latest.Placements.ToList();
                        placements[latestIndex] = latestRow with
                        {
                            MuxIdentity = prepared.Value,
                            Ssh = enabledProfile,
                            LastSeen = _clock.GetUtcNow(),
                        };
                        return (
                            latest with { Placements = placements },
                            PlacementOutcome<PlacementRecord>.Success(placements[latestIndex]));
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.StoreFailed,
                EnableStoreFailedDetail);
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
                        var index = FindOwnedSshIndex(snapshot.Placements, owner, placementId);
                        if (index < 0)
                            return (snapshot, ProviderMismatchOrMissing(snapshot.Placements, placementId, owner));

                        var current = snapshot.Placements[index];
                        var disabled = current.Ssh! with { Enabled = false, Attention = null };
                        var placements = snapshot.Placements.ToList();
                        placements[index] = current with
                        {
                            Ssh = disabled,
                            LastSeen = _clock.GetUtcNow(),
                        };
                        return (
                            snapshot with { Placements = placements },
                            PlacementOutcome<PlacementRecord>.Success(placements[index]));
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

    private async ValueTask TryPersistFailedEnableAttentionAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        int expectedRevision,
        PlacementOutcome<MuxIdentity> prepared,
        CancellationToken cancellationToken)
    {
        var attention = MapFailedEnableAttention(prepared.Reason);
        try
        {
            await _store.MutateAsync(
                    snapshot =>
                    {
                        if (snapshot.Revision != expectedRevision)
                            return (snapshot, false);

                        var index = FindOwnedSshIndex(snapshot.Placements, owner, placementId);
                        if (index < 0)
                            return (snapshot, false);

                        var current = snapshot.Placements[index];
                        if (current.Ssh is null)
                            return (snapshot, false);

                        if (current.Ssh.Enabled)
                            return (snapshot, false);

                        var updated = current.Ssh with
                        {
                            Enabled = false,
                            Attention = attention,
                        };
                        var placements = snapshot.Placements.ToList();
                        placements[index] = current with
                        {
                            Ssh = updated,
                            LastSeen = _clock.GetUtcNow(),
                        };
                        return (snapshot with { Placements = placements }, true);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (InvalidDataException)
        {
        }
        catch (JsonException)
        {
        }
    }

    private static string? MapFailedEnableAttention(string? reason) =>
        reason switch
        {
            RemoteMuxReasons.ApprovalRequired => SshPlacementAttentionStates.ApprovalRequired,
            RemoteMuxReasons.AuthenticationFailed => SshPlacementAttentionStates.PreparationRequired,
            RemoteMuxReasons.RestartRequired => SshPlacementAttentionStates.PreparationRequired,
            PlacementReasons.PreparationFailed => SshPlacementAttentionStates.PreparationRequired,
            _ => SshPlacementAttentionStates.PreparationRequired,
        };

    private static int FindOwnedSshIndex(
        IReadOnlyList<PlacementRecord> placements,
        DirectoryIdentity owner,
        PlacementId placementId)
    {
        for (var i = 0; i < placements.Count; i++)
        {
            var row = placements[i];
            if (row.Id == placementId && row.Owner == owner && row.Ssh is not null)
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
                "only the owner can mutate SSH profiles");
        }

        return PlacementOutcome<PlacementRecord>.Failure(
            PlacementReasons.ProviderMismatch,
            "This Placement does not use SSH.");
    }

    private async ValueTask<PlacementOutcome<PlacementDirectorySnapshot>> TryLoadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            return PlacementOutcome<PlacementDirectorySnapshot>.Success(snapshot);
        }
        catch (JsonException ex)
        {
            return PlacementOutcome<PlacementDirectorySnapshot>.Failure(
                PlacementReasons.DirectoryInvalid,
                ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return PlacementOutcome<PlacementDirectorySnapshot>.Failure(
                PlacementReasons.DirectoryInvalid,
                ex.Message);
        }
    }
}
