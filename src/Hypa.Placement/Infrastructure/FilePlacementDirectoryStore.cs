using System.Security.Cryptography;
using System.Text.Json;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;

namespace Hypa.Placement.Infrastructure;

/// <summary>
/// JSON file store for the Placement directory. Source-generated serialization only.
/// Cross-process mutations serialize through an exclusive lock file.
/// </summary>
public sealed class FilePlacementDirectoryStore : IPlacementDirectoryStore
{
    public const int SchemaOne = 1;
    public const int SchemaTwo = 2;

    private readonly string _directory;
    private readonly string _path;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FilePlacementDirectoryStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        PlacementCatalogPathSecurity.EnsurePrivateDirectory(_directory);
        _path = Path.Combine(_directory, "directory.json");
        _lockPath = Path.Combine(_directory, "directory.lock");
    }

    public string FilePath => _path;

    public PlacementDirectoryChangeStamp ReadChangeStamp()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_path);
        }
        catch (FileNotFoundException)
        {
            return default;
        }
        catch (DirectoryNotFoundException)
        {
            return default;
        }

        return new PlacementDirectoryChangeStamp
        {
            Exists = true,
            Length = bytes.Length,
            ContentSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        };
    }

    public async ValueTask<PlacementDirectorySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var _ = await AcquireExclusiveLockAsync(cancellationToken).ConfigureAwait(false);
            return await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        PlacementDirectorySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var _ = await AcquireExclusiveLockAsync(cancellationToken).ConfigureAwait(false);
            var current = await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
            if (current.Revision != snapshot.Revision)
            {
                throw new InvalidDataException("placement directory revision conflict");
            }

            await SaveInternalAsync(
                    snapshot with { Revision = current.Revision + 1 },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<T> MutateAsync<T>(
        Func<PlacementDirectorySnapshot, (PlacementDirectorySnapshot Next, T Result)> mutator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var _ = await AcquireExclusiveLockAsync(cancellationToken).ConfigureAwait(false);
            var current = await LoadInternalAsync(cancellationToken).ConfigureAwait(false);
            var (next, result) = mutator(current);
            if (next.Revision != current.Revision)
            {
                throw new InvalidDataException("placement directory revision conflict");
            }

            await SaveInternalAsync(
                    next with { Revision = current.Revision + 1 },
                    cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<PlacementDirectorySnapshot> LoadInternalAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new PlacementDirectorySnapshot();

        PlacementCatalogPathSecurity.EnsurePrivateFile(_path);
        var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        RequireKnownSchemaArrays(json);
        var document = JsonSerializer.Deserialize(json, PlacementJsonContext.Default.PlacementDirectoryDocument);
        if (document is null)
            throw new InvalidDataException("placement directory JSON is empty");
        if (document.Schema is not (SchemaOne or SchemaTwo))
            throw new InvalidDataException("placement directory schema is unsupported");
        return ToSnapshot(document);
    }

    private async ValueTask SaveInternalAsync(
        PlacementDirectorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var schema = ResolveSchema(snapshot);
        var document = ToDocument(snapshot with { SchemaVersion = schema }, schema);
        var json = JsonSerializer.Serialize(document, PlacementJsonContext.Default.PlacementDirectoryDocument);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        PlacementCatalogPathSecurity.EnsurePrivateFile(temp);
        File.Move(temp, _path, overwrite: true);
        PlacementCatalogPathSecurity.EnsurePrivateFile(_path);
    }

    private static int ResolveSchema(PlacementDirectorySnapshot snapshot)
    {
        if (snapshot.SchemaVersion >= SchemaTwo)
            return SchemaTwo;
        if (snapshot.Placements.Any(p => p.Ssh is not null || p.Quic is not null))
            return SchemaTwo;
        return SchemaOne;
    }

    private async ValueTask<IAsyncDisposable> AcquireExclusiveLockAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 50;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                PlacementCatalogPathSecurity.EnsurePrivateFile(_lockPath);
                return new LockHandle(stream);
            }
            catch (IOException) when (attempt + 1 < maxAttempts)
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("placement directory lock is busy");
    }

    private static void RequireKnownSchemaArrays(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("placement directory JSON is empty");
        RequireArray(root, "placements");
        RequireArray(root, "placement_grants");
        RequireArray(root, "work_grants");
    }

    private static void RequireArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("placement directory arrays are missing");
    }

    private static PlacementDirectorySnapshot ToSnapshot(PlacementDirectoryDocument document)
    {
        if (document.Placements is null || document.PlacementGrants is null || document.WorkGrants is null)
            throw new InvalidDataException("placement directory arrays are missing");

        var placements = new List<PlacementRecord>(document.Placements.Count);
        foreach (var row in document.Placements)
        {
            if (row is null)
                throw new InvalidDataException("placement row is missing");
            if (!PlacementId.TryParse(row.PlacementId, out var id))
                throw new InvalidDataException("placement id is invalid");
            if (!DirectoryIdentity.TryParse(row.OwnerId, out var owner))
                throw new InvalidDataException("owner id is invalid");
            if (!MuxIdentity.TryParse(row.MuxIdentity, out var mux))
                throw new InvalidDataException("mux identity is invalid");
            if (string.IsNullOrWhiteSpace(row.DisplayName))
                throw new InvalidDataException("display name is invalid");
            if (!DateTimeOffset.TryParse(row.LastSeen, out var lastSeen))
                throw new InvalidDataException("last seen is invalid");
            if (row.Kind is not (PlacementDirectoryKind.Local or PlacementDirectoryKind.Peer or PlacementDirectoryKind.Cube))
                throw new InvalidDataException("kind is invalid");
            if (!PlacementReachabilityRules.IsAllowedForKind(row.Kind, row.Reachability))
                throw new InvalidDataException("reachability is invalid");
            if (row.Ssh is not null && row.Quic is not null)
                throw new InvalidDataException("peer row cannot use more than one provider");
            if (!SshPlacementProfileMapper.TryFromDto(row.Ssh, out var ssh, out var sshError))
                throw new InvalidDataException(sshError ?? PlacementReasons.ProfileInvalid);
            if (!SshPlacementProfileMapper.ValidateRecord(id, row.DisplayName, ssh, out var sshMismatch))
                throw new InvalidDataException(sshMismatch ?? PlacementReasons.ProfileInvalid);
            if (!QuicPlacementProfileMapper.TryFromDto(row.Quic, out var quic, out var quicError))
                throw new InvalidDataException(quicError ?? PlacementReasons.ProfileInvalid);
            if (!QuicPlacementProfileMapper.ValidateRecord(id, row.DisplayName, quic, out var quicMismatch))
                throw new InvalidDataException(quicMismatch ?? PlacementReasons.ProfileInvalid);

            placements.Add(new PlacementRecord
            {
                Id = id,
                Owner = owner,
                DisplayName = row.DisplayName,
                Kind = row.Kind,
                MuxIdentity = mux,
                Reachability = row.Reachability,
                ActiveWorkId = string.IsNullOrWhiteSpace(row.ActiveWorkId) ? null : row.ActiveWorkId,
                LastSeen = lastSeen,
                Ssh = ssh,
                Quic = quic,
            });
        }

        var grants = new List<PlacementAccessGrant>(document.PlacementGrants.Count);
        foreach (var grant in document.PlacementGrants)
        {
            if (grant is null)
                throw new InvalidDataException("placement grant is missing");
            if (!PlacementId.TryParse(grant.PlacementId, out var id))
                throw new InvalidDataException("grant placement id is invalid");
            if (!DirectoryIdentity.TryParse(grant.GranteeId, out var grantee))
                throw new InvalidDataException("grantee id is invalid");
            grants.Add(new PlacementAccessGrant { PlacementId = id, Grantee = grantee });
        }

        var workGrants = new List<WorkAccessGrant>(document.WorkGrants.Count);
        foreach (var grant in document.WorkGrants)
        {
            if (grant is null)
                throw new InvalidDataException("work grant is missing");
            if (!DirectoryIdentity.TryParse(grant.Identity, out var identity))
                throw new InvalidDataException("work grant identity is invalid");
            if (string.IsNullOrWhiteSpace(grant.WorkId))
                throw new InvalidDataException("work grant work id is invalid");
            workGrants.Add(new WorkAccessGrant { Identity = identity, WorkId = grant.WorkId.Trim() });
        }

        return new PlacementDirectorySnapshot
        {
            SchemaVersion = document.Schema,
            Revision = document.Revision,
            Placements = placements,
            PlacementGrants = grants,
            WorkGrants = workGrants,
        };
    }

    private static PlacementDirectoryDocument ToDocument(PlacementDirectorySnapshot snapshot, int schema) =>
        new()
        {
            Schema = schema,
            Revision = snapshot.Revision,
            Placements = snapshot.Placements.Select(p => new PlacementRecordDto
            {
                PlacementId = p.Id.Value,
                OwnerId = p.Owner.Value,
                DisplayName = p.DisplayName,
                Kind = p.Kind,
                MuxIdentity = p.MuxIdentity.Value,
                Reachability = p.Reachability,
                ActiveWorkId = p.ActiveWorkId,
                LastSeen = p.LastSeen.UtcDateTime.ToString("O"),
                Ssh = SshPlacementProfileMapper.ToDto(p.Ssh),
                Quic = QuicPlacementProfileMapper.ToDto(p.Quic),
            }).ToList(),
            PlacementGrants = snapshot.PlacementGrants.Select(g => new PlacementGrantDto
            {
                PlacementId = g.PlacementId.Value,
                GranteeId = g.Grantee.Value,
            }).ToList(),
            WorkGrants = snapshot.WorkGrants.Select(g => new WorkAccessGrantDto
            {
                Identity = g.Identity.Value,
                WorkId = g.WorkId,
            }).ToList(),
        };

    private sealed class LockHandle(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
