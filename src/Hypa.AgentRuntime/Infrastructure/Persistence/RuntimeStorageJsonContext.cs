using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Application.Metadata;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Worktrees;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// AOT-safe source-generated JSON for binding_json columns and checkpoint sidecars
/// (snake_case wire names).
/// </summary>
[JsonSerializable(typeof(AtomicBindingStorageDto))]
[JsonSerializable(typeof(CheckpointStorageDto))]
[JsonSerializable(typeof(CheckpointMetadataArtifactDto))]
[JsonSerializable(typeof(CheckpointWorkspaceArtifactDto))]
[JsonSerializable(typeof(CheckpointPaneArtifactDto))]
[JsonSerializable(typeof(CheckpointGitMetaArtifactDto))]
[JsonSerializable(typeof(List<CheckpointWorkspaceArtifactDto>))]
[JsonSerializable(typeof(List<CheckpointPaneArtifactDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PaneAuthorityStorageDto))]
[JsonSerializable(typeof(PaneAgentAuthorityStorageDto))]
[JsonSerializable(typeof(NativeAgentSessionStorageDto))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(WorktreeSpaceMembershipStorageDto))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class RuntimeStorageJsonContext : JsonSerializerContext;

/// <summary>On-disk / SQLite-adjacent checkpoint sidecar DTO.</summary>
public sealed record CheckpointStorageDto
{
    [JsonPropertyName("checkpoint_id")]
    public required string CheckpointId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("barrier_seq")]
    public long BarrierSeq { get; init; }

    [JsonPropertyName("next_seq_at_prepare")]
    public long NextSeqAtPrepare { get; init; }

    [JsonPropertyName("session_fingerprint")]
    public required string SessionFingerprint { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("include_scrollback")]
    public bool IncludeScrollback { get; init; }

    [JsonPropertyName("include_workspace_files")]
    public bool IncludeWorkspaceFiles { get; init; }

    [JsonPropertyName("created_at")]
    public required string CreatedAt { get; init; }

    [JsonPropertyName("exported_at")]
    public string? ExportedAt { get; init; }

    [JsonPropertyName("manifest_relative_path")]
    public string? ManifestRelativePath { get; init; }

    [JsonPropertyName("manifest_sha256")]
    public string? ManifestSha256 { get; init; }

    [JsonPropertyName("byte_count")]
    public long? ByteCount { get; init; }

    [JsonPropertyName("transfer_incomplete")]
    public bool TransferIncomplete { get; init; }

    [JsonPropertyName("warnings")]
    public List<string>? Warnings { get; init; }

    [JsonPropertyName("placement")]
    public string? Placement { get; init; }

    [JsonPropertyName("placement_generation")]
    public int PlacementGeneration { get; init; }

    [JsonPropertyName("replay_complete")]
    public bool ReplayComplete { get; init; } = true;

    [JsonPropertyName("project_root_device")]
    public ulong? ProjectRootDevice { get; init; }

    [JsonPropertyName("project_root_inode")]
    public ulong? ProjectRootInode { get; init; }

    [JsonPropertyName("project_root_was_symlink")]
    public bool? ProjectRootWasSymlink { get; init; }

    public static CheckpointStorageDto FromDomain(CheckpointRecord r) => new()
    {
        CheckpointId = r.CheckpointId,
        SessionId = r.SessionId,
        State = r.State,
        BarrierSeq = r.BarrierSeq,
        NextSeqAtPrepare = r.NextSeqAtPrepare,
        SessionFingerprint = r.SessionFingerprint,
        Reason = r.Reason,
        IncludeScrollback = r.IncludeScrollback,
        IncludeWorkspaceFiles = r.IncludeWorkspaceFiles,
        CreatedAt = r.CreatedAt.UtcDateTime.ToString("O"),
        ExportedAt = r.ExportedAt?.UtcDateTime.ToString("O"),
        ManifestRelativePath = r.ManifestRelativePath,
        ManifestSha256 = r.ManifestSha256,
        ByteCount = r.ByteCount,
        TransferIncomplete = r.TransferIncomplete,
        Warnings = r.Warnings is { Count: > 0 } ? r.Warnings.ToList() : null,
        Placement = r.Placement,
        PlacementGeneration = r.PlacementGeneration,
        ReplayComplete = r.ReplayComplete,
        ProjectRootDevice = r.ProjectRootDevice,
        ProjectRootInode = r.ProjectRootInode,
        ProjectRootWasSymlink = r.ProjectRootWasSymlink,
    };

    public CheckpointRecord ToDomain() => new()
    {
        CheckpointId = CheckpointId,
        SessionId = SessionId,
        State = State,
        BarrierSeq = BarrierSeq,
        NextSeqAtPrepare = NextSeqAtPrepare,
        SessionFingerprint = SessionFingerprint,
        Reason = Reason,
        IncludeScrollback = IncludeScrollback,
        IncludeWorkspaceFiles = IncludeWorkspaceFiles,
        CreatedAt = DurableTimestampParser.ParseOrSentinel(CreatedAt),
        ExportedAt = ExportedAt is not null && DateTimeOffset.TryParse(ExportedAt, out var e)
            ? e.ToUniversalTime()
            : null,
        ManifestRelativePath = ManifestRelativePath,
        ManifestSha256 = ManifestSha256,
        ByteCount = ByteCount,
        TransferIncomplete = TransferIncomplete,
        Warnings = Warnings ?? [],
        Placement = Placement ?? "local",
        PlacementGeneration = PlacementGeneration,
        ReplayComplete = ReplayComplete,
        ProjectRootDevice = ProjectRootDevice,
        ProjectRootInode = ProjectRootInode,
        ProjectRootWasSymlink = ProjectRootWasSymlink,
    };
}

/// <summary>
/// Storage DTO matching BindingDto / live binding object field names.
/// Treated as opaque — no Atomic ontology validation in.
/// </summary>
public sealed record AtomicBindingStorageDto
{
    [JsonPropertyName("agent_session_id")]
    public string? AgentSessionId { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("step_id")]
    public string? StepId { get; init; }

    [JsonPropertyName("memory_id")]
    public string? MemoryId { get; init; }

    [JsonPropertyName("project_root")]
    public string? ProjectRoot { get; init; }

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; init; }

    public static AtomicBindingStorageDto? FromDomain(AtomicBinding? binding)
    {
        if (binding is null)
            return null;
        if (binding.AgentSessionId is null &&
            binding.RunId is null &&
            binding.StepId is null &&
            binding.MemoryId is null &&
            binding.ProjectRoot is null &&
            binding.TenantId is null)
            return null;

        return new AtomicBindingStorageDto
        {
            AgentSessionId = binding.AgentSessionId,
            RunId = binding.RunId,
            StepId = binding.StepId,
            MemoryId = binding.MemoryId,
            ProjectRoot = binding.ProjectRoot,
            TenantId = binding.TenantId,
        };
    }

    public AtomicBinding ToDomain() => new()
    {
        AgentSessionId = AgentSessionId,
        RunId = RunId,
        StepId = StepId,
        MemoryId = MemoryId,
        ProjectRoot = ProjectRoot,
        TenantId = TenantId,
    };
}

/// <summary>Additive panes.agent_authority_json payload. Schema version stays 1.</summary>
public sealed record PaneAuthorityStorageDto
{
    [JsonPropertyName("authority")]
    public PaneAgentAuthorityStorageDto? Authority { get; init; }

    [JsonPropertyName("agent_session")]
    public NativeAgentSessionStorageDto? AgentSession { get; init; }

    [JsonPropertyName("sequences")]
    public Dictionary<string, long>? Sequences { get; init; }

    public static PaneAuthorityStorageDto? FromDomain(PaneState pane)
    {
        var sequences = pane.AgentAuthoritySequences.Count > 0
            ? new Dictionary<string, long>(pane.AgentAuthoritySequences, StringComparer.Ordinal)
            : null;
        if (pane.AgentAuthority is null && pane.AgentSession is null && sequences is null)
            return null;
        return new PaneAuthorityStorageDto
        {
            Authority = PaneAgentAuthorityStorageDto.FromDomain(pane.AgentAuthority),
            AgentSession = NativeAgentSessionStorageDto.FromDomain(pane.AgentSession),
            Sequences = sequences,
        };
    }

    public PaneAuthorityLoad ToDomain() => new(
        Authority?.ToDomain(),
        AgentSession?.ToDomain(),
        MapSequences(Sequences));

    private static IReadOnlyDictionary<string, long> MapSequences(Dictionary<string, long>? sequences)
    {
        if (sequences is null || sequences.Count == 0)
            return new Dictionary<string, long>(StringComparer.Ordinal);
        if (sequences.Count > MetadataTokenLimits.MaxSequencedSources)
            throw new JsonException("agent_authority_json sequences exceed cap");

        var mapped = new Dictionary<string, long>(sequences.Count, StringComparer.Ordinal);
        foreach (var pair in sequences)
        {
            if (!MetadataTokenNormalizer.TryNormalizeSource(pair.Key, out var source)
                || !mapped.TryAdd(source, pair.Value))
            {
                throw new JsonException("agent_authority_json sequence source is invalid");
            }
        }

        return mapped;
    }
}

/// <summary>Validated pane authority payload after fail-closed JSON load.</summary>
public sealed record PaneAuthorityLoad(
    PaneAgentAuthority? Authority,
    NativeAgentSessionRef? AgentSession,
    IReadOnlyDictionary<string, long> Sequences);

public sealed record PaneAgentAuthorityStorageDto
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("seq")]
    public long? Sequence { get; init; }

    [JsonPropertyName("session")]
    public string? Session { get; init; }

    [JsonPropertyName("reported_at")]
    public required string ReportedAt { get; init; }

    public static PaneAgentAuthorityStorageDto? FromDomain(PaneAgentAuthority? authority)
    {
        if (authority is null)
            return null;
        return new PaneAgentAuthorityStorageDto
        {
            Source = authority.Source,
            Agent = authority.Agent,
            State = authority.State.ToString().ToLowerInvariant(),
            Message = authority.Message,
            Sequence = authority.Sequence,
            Session = authority.Session,
            ReportedAt = authority.ReportedAt.UtcDateTime.ToString("O"),
        };
    }

    public PaneAgentAuthority ToDomain()
    {
        if (!MetadataTokenNormalizer.TryNormalizeSource(Source, out var source))
            throw new JsonException("agent_authority_json source is invalid");
        var agent = RequirePersistedAgent(Agent);
        return new PaneAgentAuthority
        {
            Source = source,
            Agent = agent,
            State = ParseAgentStatus(State),
            Message = Message,
            Sequence = Sequence,
            Session = Session,
            ReportedAt = DurableTimestampParser.ParseOrSentinel(ReportedAt),
        };
    }

    private static AgentStatus ParseAgentStatus(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "working" => AgentStatus.Working,
            "blocked" => AgentStatus.Blocked,
            "idle" => AgentStatus.Idle,
            "unknown" => AgentStatus.Unknown,
            "done" => AgentStatus.Done,
            _ => throw new JsonException("agent_authority_json state is invalid"),
        };

    internal static string RequirePersistedAgent(string? raw)
    {
        var agent = raw?.Trim() ?? string.Empty;
        if (agent.Length == 0)
            throw new JsonException("agent_authority_json agent is invalid");
        return agent;
    }
}

public sealed record NativeAgentSessionStorageDto
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("agent")]
    public required string Agent { get; init; }

    [JsonPropertyName("session_start_source")]
    public string? SessionStartSource { get; init; }

    public static NativeAgentSessionStorageDto? FromDomain(NativeAgentSessionRef? session)
    {
        if (session is null)
            return null;
        return new NativeAgentSessionStorageDto
        {
            Kind = session.Kind,
            Value = session.Value,
            Source = session.Source,
            Agent = session.Agent,
            SessionStartSource = session.SessionStartSource,
        };
    }

    public NativeAgentSessionRef ToDomain()
    {
        if (!MetadataTokenNormalizer.TryNormalizeSource(Source, out var source))
            throw new JsonException("agent_authority_json session source is invalid");
        var agent = PaneAgentAuthorityStorageDto.RequirePersistedAgent(Agent);
        var kind = Kind?.Trim() ?? string.Empty;
        var value = Value?.Trim() ?? string.Empty;
        if (kind == NativeAgentSessionRef.KindId)
        {
            if (value.Length is 0 or > NativeAgentSessionRef.MaxIdLength)
                throw new JsonException("agent_authority_json session id is invalid");
        }
        else if (kind == NativeAgentSessionRef.KindPath)
        {
            if (value.Length is 0 or > NativeAgentSessionRef.MaxPathLength)
                throw new JsonException("agent_authority_json session path is invalid");
        }
        else
        {
            throw new JsonException("agent_authority_json session kind is invalid");
        }

        var start = string.IsNullOrWhiteSpace(SessionStartSource)
            ? null
            : SessionStartSource.Trim().ToLowerInvariant();
        if (start is not null && !NativeAgentSessionRef.IsSessionStartSource(start))
            throw new JsonException("agent_authority_json session_start_source is invalid");

        return new NativeAgentSessionRef
        {
            Kind = kind,
            Value = value,
            Source = source,
            Agent = agent,
            SessionStartSource = start,
        };
    }
}

/// <summary>Additive workspaces.worktree_json payload. Schema version stays 1.</summary>
public sealed record WorktreeSpaceMembershipStorageDto
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("repo_root")]
    public required string RepoRoot { get; init; }

    [JsonPropertyName("checkout_path")]
    public required string CheckoutPath { get; init; }

    [JsonPropertyName("is_linked_worktree")]
    public bool IsLinkedWorktree { get; init; }

    public static WorktreeSpaceMembershipStorageDto? FromDomain(WorktreeSpaceMembership? membership)
    {
        if (membership is null)
            return null;
        return new WorktreeSpaceMembershipStorageDto
        {
            Key = membership.Key,
            Label = membership.Label,
            RepoRoot = membership.RepoRoot,
            CheckoutPath = membership.CheckoutPath,
            IsLinkedWorktree = membership.IsLinkedWorktree,
        };
    }

    public WorktreeSpaceMembership ToDomain() => new()
    {
        Key = Key,
        Label = Label,
        RepoRoot = RepoRoot,
        CheckoutPath = CheckoutPath,
        IsLinkedWorktree = IsLinkedWorktree,
    };
}
