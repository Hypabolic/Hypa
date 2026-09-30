using System.Text.Json;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Default checkpoint orchestration: store records and materialize export manifests.
/// </summary>
public sealed class DefaultCheckpointService : ICheckpointService
{
    private readonly ICheckpointStore _store;
    private readonly ICheckpointArtifactBuilder _builder;

    public DefaultCheckpointService(ICheckpointStore store, ICheckpointArtifactBuilder builder)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public Task<RuntimeResult<CheckpointRecord>> SavePreparedAsync(
        CheckpointRecord record, CancellationToken ct = default) =>
        _store.SaveAsync(record, ct);

    public Task<RuntimeResult<CheckpointRecord?>> GetAsync(
        string checkpointId, CancellationToken ct = default) =>
        _store.GetAsync(checkpointId, ct);

    public Task<RuntimeResult<IReadOnlyList<CheckpointRecord>>> ListBySessionAsync(
        string sessionId, CancellationToken ct = default) =>
        _store.ListBySessionAsync(sessionId, ct);

    public bool TryPinProjectRoot(
        string? projectRoot,
        out ulong device,
        out ulong inode,
        out bool wasSymlink) =>
        _builder.TryPinProjectRoot(projectRoot, out device, out inode, out wasSymlink);

    public async Task<RuntimeResult<CheckpointExportMaterial>> BuildExportAsync(
        CheckpointRecord prepared,
        SessionState session,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(session);

        var dir = _store.GetCheckpointDirectory(prepared.CheckpointId);
        var build = await _builder.BuildAsync(new CheckpointArtifactBuildRequest
        {
            Record = prepared,
            Session = session,
            CheckpointDirectory = dir,
        }, ct).ConfigureAwait(false);

        if (!build.IsOk)
            return RuntimeResult<CheckpointExportMaterial>.Fail(build.Error);

        var artifacts = build.Value.Artifacts;
        var warnings = build.Value.Warnings.ToList();
        if (prepared.Warnings is { Count: > 0 })
            warnings.InsertRange(0, prepared.Warnings);

        var transferIncomplete = build.Value.TransferIncomplete || prepared.TransferIncomplete;

        var manifest = new CheckpointManifestDto
        {
            CheckpointId = prepared.CheckpointId,
            RuntimeSessionId = prepared.SessionId,
            BarrierSeq = prepared.BarrierSeq,
            CreatedAt = prepared.CreatedAt.UtcDateTime.ToString("O"),
            ProtocolMajor = ProtocolVersion.Major,
            ProtocolMinor = ProtocolVersion.Minor,
            PersistenceSchema = RuntimePersistenceSchema.Version,
            Placement = prepared.Placement,
            PlacementGeneration = prepared.PlacementGeneration,
            Binding = ToBindingDto(session.Binding),
            Limits = new CheckpointLimitsDto
            {
                MaxWorkspaceBytes = CheckpointBudgets.MaxWorkspaceBytes,
                MaxFileBytes = CheckpointBudgets.MaxFileBytes,
            },
            Excludes = CheckpointSecretFilePolicy.ManifestExcludes,
            Journal = new CheckpointJournalDto
            {
                BarrierSeq = prepared.BarrierSeq,
                NextSeqAtPrepare = prepared.NextSeqAtPrepare,
                ReplayComplete = prepared.ReplayComplete,
            },
            SessionFingerprint = prepared.SessionFingerprint,
            Artifacts = artifacts.Select(a => new CheckpointArtifactEntryDto
            {
                Path = a.RelativePath,
                Sha256 = a.Sha256,
                ByteCount = a.ByteCount,
                Kind = a.Kind,
            }).ToList(),
            TransferIncomplete = transferIncomplete,
            Warnings = warnings.Count > 0 ? warnings : null,
        };

        // Source-gen serialize for AOT-safe snake_case wire.
        var manifestJson = JsonSerializer.Serialize(
            manifest, ProtocolJsonContext.Default.CheckpointManifestDto);

        var written = await _store.WriteManifestAsync(
            prepared.CheckpointId, manifestJson, ct).ConfigureAwait(false);
        if (!written.IsOk)
            return RuntimeResult<CheckpointExportMaterial>.Fail(written.Error);

        var staged = prepared with
        {
            ManifestRelativePath = written.Value.ManifestRelativePath,
            ManifestSha256 = written.Value.ManifestSha256,
            ByteCount = written.Value.ByteCount + build.Value.TotalArtifactBytes,
            TransferIncomplete = transferIncomplete,
            Warnings = warnings,
        };

        return RuntimeResult<CheckpointExportMaterial>.Ok(new CheckpointExportMaterial
        {
            Record = staged,
            ManifestRelativePath = written.Value.ManifestRelativePath,
            ManifestSha256 = written.Value.ManifestSha256,
            ByteCount = staged.ByteCount ?? written.Value.ByteCount,
            TransferIncomplete = transferIncomplete,
        });
    }

    public async Task<RuntimeResult<CheckpointRecord>> CommitExportedAsync(
        CheckpointExportMaterial material,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(material);

        var loaded = await _store.GetAsync(material.Record.CheckpointId, ct).ConfigureAwait(false);
        if (!loaded.IsOk)
            return RuntimeResult<CheckpointRecord>.Fail(loaded.Error);

        var current = loaded.Value;
        if (current is null)
        {
            return RuntimeResult<CheckpointRecord>.Fail(
                RuntimePersistenceError.Io("checkpoint disappeared before export commit"));
        }

        if (string.Equals(current.State, CheckpointStates.Exported, StringComparison.Ordinal))
            return RuntimeResult<CheckpointRecord>.Ok(current);

        if (!CheckpointStates.CanTransition(current.State, CheckpointStates.Exported))
        {
            return RuntimeResult<CheckpointRecord>.Fail(
                RuntimePersistenceError.Conflict(
                    $"illegal checkpoint transition {current.State} -> {CheckpointStates.Exported}"));
        }

        var exported = current with
        {
            State = CheckpointStates.Exported,
            ExportedAt = DateTimeOffset.UtcNow,
            ManifestRelativePath = material.ManifestRelativePath,
            ManifestSha256 = material.ManifestSha256,
            ByteCount = material.ByteCount,
            TransferIncomplete = material.TransferIncomplete,
            Warnings = material.Record.Warnings,
        };

        return await _store.SaveAsync(exported, ct).ConfigureAwait(false);
    }

    public async Task<RuntimeResult<CheckpointExportMaterial>> ExportAsync(
        CheckpointRecord prepared,
        SessionState session,
        CancellationToken ct = default)
    {
        var built = await BuildExportAsync(prepared, session, ct).ConfigureAwait(false);
        if (!built.IsOk)
            return built;

        var committed = await CommitExportedAsync(built.Value, ct).ConfigureAwait(false);
        if (!committed.IsOk)
            return RuntimeResult<CheckpointExportMaterial>.Fail(committed.Error);

        return RuntimeResult<CheckpointExportMaterial>.Ok(built.Value with
        {
            Record = committed.Value,
        });
    }

    private static BindingDto? ToBindingDto(AtomicBinding? b)
    {
        if (b is null)
            return null;
        if (b.AgentSessionId is null && b.RunId is null && b.StepId is null &&
            b.MemoryId is null && b.ProjectRoot is null && b.TenantId is null)
            return null;

        return new BindingDto
        {
            AgentSessionId = b.AgentSessionId,
            RunId = b.RunId,
            StepId = b.StepId,
            MemoryId = b.MemoryId,
            ProjectRoot = b.ProjectRoot,
            TenantId = b.TenantId,
        };
    }
}

/// <summary>Default workspace walk excludes (design §10.4 floor).</summary>
public static class DefaultCheckpointExcludes
{
    public static IReadOnlyList<string> Patterns { get; } =
    [
        "node_modules",
        "bin",
        "obj",
        ".git",
        ".hypa",
        "build",
        "dist",
        ".next",
        "target",
        "__pycache__",
        ".cache",
    ];

    public static bool IsExcluded(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            foreach (var exclude in Patterns)
            {
                if (string.Equals(part, exclude, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
