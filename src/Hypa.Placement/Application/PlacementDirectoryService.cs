using System.Text.Json;
using Hypa.Placement.Domain;

namespace Hypa.Placement.Application;

/// <summary>
/// Placement directory use cases. Authorization uses owner and grants.
/// Display name is not authorization.
/// </summary>
public sealed class PlacementDirectoryService : IPlacementDirectory, IPlacementDirectoryChangeStamp
{
    private readonly IPlacementDirectoryStore _store;
    private readonly TimeProvider _clock;

    public PlacementDirectoryService(IPlacementDirectoryStore store, TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
    }

    public PlacementDirectoryChangeStamp ReadChangeStamp() => _store.ReadChangeStamp();

    public async ValueTask<PlacementOutcome<PlacementRecord>> RegisterAsync(
        PlacementRegistration request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.DisplayNameInvalid,
                "display name is required");
        }

        if (request.Kind is not (PlacementDirectoryKind.Local or PlacementDirectoryKind.Peer or PlacementDirectoryKind.Cube))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.KindInvalid,
                "kind must be local, peer, or cube");
        }

        if (!MuxIdentity.TryParse(request.MuxIdentity.Value, out var mux))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.MuxIdentityInvalid,
                "mux identity must be an opaque mux_ value");
        }

        var loaded = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok || loaded.Value is null)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                loaded.Reason ?? PlacementReasons.DirectoryInvalid,
                loaded.Detail ?? "directory load failed");
        }

        var snapshot = loaded.Value;
        var now = _clock.GetUtcNow();
        var placements = snapshot.Placements.ToList();
        var existing = placements.Find(p =>
            p.Owner == request.Owner && p.MuxIdentity == mux);
        if (existing is not null)
        {
            var updated = existing with
            {
                Id = request.Id ?? existing.Id,
                DisplayName = request.DisplayName.Trim(),
                LastSeen = now,
                Ssh = existing.Ssh,
                Quic = existing.Quic,
            };
            placements[placements.IndexOf(existing)] = updated;
            await _store.SaveAsync(
                    snapshot with { Placements = placements },
                    cancellationToken)
                .ConfigureAwait(false);
            return PlacementOutcome<PlacementRecord>.Success(updated);
        }

        if (request.Id is { } requested
            && placements.Exists(p => p.Id == requested && p.Owner == request.Owner))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.Internal,
                "placement id is already in use");
        }

        var created = new PlacementRecord
        {
            Id = request.Id ?? PlacementId.New(),
            Owner = request.Owner,
            DisplayName = request.DisplayName.Trim(),
            Kind = request.Kind,
            MuxIdentity = mux,
            Reachability = request.Kind == PlacementDirectoryKind.Local
                ? PlacementReachability.Local
                : PlacementReachability.Unreachable,
            LastSeen = now,
        };
        placements.Add(created);
        await _store.SaveAsync(
                snapshot with { Placements = placements },
                cancellationToken)
            .ConfigureAwait(false);
        return PlacementOutcome<PlacementRecord>.Success(created);
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> SetReachabilityAsync(
        DirectoryIdentity actor,
        PlacementId placementId,
        PlacementReachability reachability,
        CancellationToken cancellationToken = default)
    {
        if (!PlacementReachabilityRules.IsDefined(reachability))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ReachabilityInvalid,
                "reachability must be local, reachable, unreachable, or asleep");
        }

        var loaded = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok || loaded.Value is null)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                loaded.Reason ?? PlacementReasons.DirectoryInvalid,
                loaded.Detail ?? "directory load failed");
        }

        var snapshot = loaded.Value;
        var placements = snapshot.Placements.ToList();
        var index = placements.FindIndex(p => p.Id == placementId);
        if (index < 0)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.PlacementNotFound,
                "placement is not in the directory");
        }

        var current = placements[index];
        if (current.Owner != actor)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.Unauthorized,
                "only the owner can change reachability");
        }

        if (!PlacementReachabilityRules.IsAllowedForKind(current.Kind, reachability))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.ReachabilityInvalid,
                "local reachability is only valid for kind local");
        }

        var updated = current with
        {
            Reachability = reachability,
            LastSeen = _clock.GetUtcNow(),
            Ssh = current.Ssh,
            Quic = current.Quic,
        };
        placements[index] = updated;
        await _store.SaveAsync(
                snapshot with { Placements = placements },
                cancellationToken)
            .ConfigureAwait(false);
        return PlacementOutcome<PlacementRecord>.Success(updated);
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> SetActiveWorkAsync(
        DirectoryIdentity actor,
        PlacementId placementId,
        string? workId,
        CancellationToken cancellationToken = default)
    {
        if (workId is not null && string.IsNullOrWhiteSpace(workId))
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.WorkIdInvalid,
                "work id is empty");
        }

        var loaded = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok || loaded.Value is null)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                loaded.Reason ?? PlacementReasons.DirectoryInvalid,
                loaded.Detail ?? "directory load failed");
        }

        var snapshot = loaded.Value;
        var placements = snapshot.Placements.ToList();
        var index = placements.FindIndex(p => p.Id == placementId);
        if (index < 0)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.PlacementNotFound,
                "placement is not in the directory");
        }

        if (placements[index].Owner != actor)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.Unauthorized,
                "only the owner can set active work");
        }

        var trimmed = workId?.Trim();
        var updated = placements[index] with
        {
            ActiveWorkId = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed,
            LastSeen = _clock.GetUtcNow(),
            Ssh = placements[index].Ssh,
            Quic = placements[index].Quic,
        };
        placements[index] = updated;
        await _store.SaveAsync(
                snapshot with { Placements = placements },
                cancellationToken)
            .ConfigureAwait(false);
        return PlacementOutcome<PlacementRecord>.Success(updated);
    }

    public async ValueTask<PlacementOutcome> GrantListAccessAsync(
        DirectoryIdentity actor,
        PlacementId placementId,
        DirectoryIdentity grantee,
        CancellationToken cancellationToken = default)
    {
        var loaded = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok || loaded.Value is null)
        {
            return PlacementOutcome.Failure(
                loaded.Reason ?? PlacementReasons.DirectoryInvalid,
                loaded.Detail ?? "directory load failed");
        }

        var snapshot = loaded.Value;
        var placement = snapshot.Placements.FirstOrDefault(p => p.Id == placementId);
        if (placement is null)
            return PlacementOutcome.Failure(PlacementReasons.PlacementNotFound, "placement is not in the directory");
        if (placement.Owner != actor)
            return PlacementOutcome.Failure(PlacementReasons.Unauthorized, "only the owner can grant list access");
        if (grantee == placement.Owner)
            return PlacementOutcome.Success();

        var grants = snapshot.PlacementGrants.ToList();
        if (grants.Exists(g => g.PlacementId == placementId && g.Grantee == grantee))
            return PlacementOutcome.Success();

        grants.Add(new PlacementAccessGrant { PlacementId = placementId, Grantee = grantee });
        await _store.SaveAsync(
                snapshot with { PlacementGrants = grants },
                cancellationToken)
            .ConfigureAwait(false);
        return PlacementOutcome.Success();
    }

    public async ValueTask<PlacementOutcome> GrantWorkAccessAsync(
        DirectoryIdentity actor,
        DirectoryIdentity identity,
        string workId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workId))
            return PlacementOutcome.Failure(PlacementReasons.WorkIdInvalid, "work id is required");

        var loaded = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok || loaded.Value is null)
        {
            return PlacementOutcome.Failure(
                loaded.Reason ?? PlacementReasons.DirectoryInvalid,
                loaded.Detail ?? "directory load failed");
        }

        var snapshot = loaded.Value;
        var trimmed = workId.Trim();
        if (!snapshot.Placements.Any(p => p.Owner == actor && p.ActiveWorkId == trimmed))
        {
            return PlacementOutcome.Failure(
                PlacementReasons.Unauthorized,
                "only the owner can grant work access");
        }

        var grants = snapshot.WorkGrants.ToList();
        if (grants.Exists(g => g.Identity == identity && g.WorkId == trimmed))
            return PlacementOutcome.Success();

        grants.Add(new WorkAccessGrant { Identity = identity, WorkId = trimmed });
        await _store.SaveAsync(
                snapshot with { WorkGrants = grants },
                cancellationToken)
            .ConfigureAwait(false);
        return PlacementOutcome.Success();
    }

    public async ValueTask<PlacementOutcome<IReadOnlyList<PlacementRow>>> ListAsync(
        DirectoryIdentity requester,
        CancellationToken cancellationToken = default)
    {
        var loaded = await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        if (!loaded.Ok || loaded.Value is null)
        {
            return PlacementOutcome<IReadOnlyList<PlacementRow>>.Failure(
                loaded.Reason ?? PlacementReasons.DirectoryInvalid,
                loaded.Detail ?? "directory load failed");
        }

        var snapshot = loaded.Value;
        var granted = snapshot.PlacementGrants
            .Where(g => g.Grantee == requester)
            .Select(g => g.PlacementId)
            .ToHashSet();
        var workAccess = snapshot.WorkGrants
            .Where(g => g.Identity == requester)
            .Select(g => g.WorkId)
            .ToHashSet(StringComparer.Ordinal);

        IReadOnlyList<PlacementRow> rows = snapshot.Placements
            .Where(p => p.Owner == requester || granted.Contains(p.Id))
            .Select(p => ToRow(p, workAccess, requester))
            .ToList();
        return PlacementOutcome<IReadOnlyList<PlacementRow>>.Success(rows);
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> GetAsync(
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

        var record = loaded.Value.Placements.FirstOrDefault(p => p.Id == placementId);
        if (record is null)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.PlacementNotFound,
                "placement is not in the directory");
        }

        return PlacementOutcome<PlacementRecord>.Success(record);
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> GetForConnectAsync(
        DirectoryIdentity requester,
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
        var record = snapshot.Placements.FirstOrDefault(p => p.Id == placementId);
        if (record is null)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.PlacementNotFound,
                "placement is not in the directory");
        }

        if (record.Ssh is not null || record.Quic is not null)
        {
            if (record.Owner != requester)
            {
                return PlacementOutcome<PlacementRecord>.Failure(
                    PlacementReasons.Unauthorized,
                    "only the owner can connect to provider-backed placements");
            }

            return PlacementOutcome<PlacementRecord>.Success(record);
        }

        var granted = snapshot.PlacementGrants.Any(
            g => g.PlacementId == placementId && g.Grantee == requester);
        if (record.Owner != requester && !granted)
        {
            return PlacementOutcome<PlacementRecord>.Failure(
                PlacementReasons.Unauthorized,
                "directory access is required to connect");
        }

        return PlacementOutcome<PlacementRecord>.Success(record);
    }

    public async ValueTask<PlacementOutcome<PlacementRecord>> RemoveOwnedAsync(
        DirectoryIdentity owner,
        PlacementId placementId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _store.MutateAsync(
                    snapshot =>
                    {
                        var index = -1;
                        for (var i = 0; i < snapshot.Placements.Count; i++)
                        {
                            var row = snapshot.Placements[i];
                            if (row.Id == placementId && row.Owner == owner)
                            {
                                index = i;
                                break;
                            }
                        }

                        if (index < 0)
                        {
                            var found = snapshot.Placements.FirstOrDefault(p => p.Id == placementId);
                            if (found is null)
                            {
                                return (
                                    snapshot,
                                    PlacementOutcome<PlacementRecord>.Failure(
                                        PlacementReasons.PlacementNotFound,
                                        "placement is not in the directory"));
                            }

                            return (
                                snapshot,
                                PlacementOutcome<PlacementRecord>.Failure(
                                    PlacementReasons.Unauthorized,
                                    "only the owner can remove this Placement"));
                        }

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
                "Placement remove was not saved. Retry remove.");
        }
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

    private static PlacementRow ToRow(
        PlacementRecord record,
        HashSet<string> workAccess,
        DirectoryIdentity requester)
    {
        var workId = record.ActiveWorkId;
        if (workId is not null && !workAccess.Contains(workId))
            workId = null;

        return new PlacementRow
        {
            Id = record.Id,
            DisplayName = record.DisplayName,
            Kind = record.Kind,
            MuxIdentity = record.MuxIdentity,
            Reachability = record.Reachability,
            ActiveWorkId = workId,
            LastSeen = record.LastSeen,
            Ssh = record.Owner == requester ? record.Ssh : null,
            Quic = record.Owner == requester ? record.Quic : null,
        };
    }
}
