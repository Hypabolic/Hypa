using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Metadata;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal async Task<JsonElement> HandlePaneReportAgentAsync(
        PaneReportAgentParams p, CancellationToken ct)
    {
        var result = await PaneReportAgentAsync(p, ct).ConfigureAwait(false);
        return OkTyped(result, ProtocolJsonContext.Default.AgentAuthorityResult);
    }

    internal async Task<JsonElement> HandlePaneReportAgentSessionAsync(
        PaneReportAgentSessionParams p, CancellationToken ct)
    {
        var result = await PaneReportAgentSessionAsync(p, ct).ConfigureAwait(false);
        return OkTyped(result, ProtocolJsonContext.Default.AgentAuthorityResult);
    }

    internal async Task<JsonElement> HandlePaneReportMetadataAsync(
        PaneReportMetadataParams p, CancellationToken ct)
    {
        var result = await PaneReportMetadataAsync(p, ct).ConfigureAwait(false);
        return OkTyped(result, ProtocolJsonContext.Default.MetadataReportResult);
    }

    internal async Task<JsonElement> HandlePaneReleaseAgentAsync(
        PaneReleaseAgentParams p, CancellationToken ct)
    {
        var result = await PaneReleaseAgentAsync(p, ct).ConfigureAwait(false);
        return OkTyped(result, ProtocolJsonContext.Default.AgentAuthorityResult);
    }

    internal async Task<JsonElement> HandlePaneClearAgentAuthorityAsync(
        PaneClearAgentAuthorityParams p, CancellationToken ct)
    {
        var result = await PaneClearAgentAuthorityAsync(p, ct).ConfigureAwait(false);
        return OkTyped(result, ProtocolJsonContext.Default.AgentAuthorityResult);
    }

    private async Task<AgentAuthorityResult> PaneReportAgentAsync(
        PaneReportAgentParams p, CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.PaneReportAgent);
        var paneId = RequireField(p.PaneId, "pane_id");
        var source = RequireNormalizedSource(p.Source);
        var agent = RequireAgentName(p.Agent);
        var state = RequireAgentReportState(p.State);
        // Validate session fields before stale seq can no-op the report.
        var session = ParseNativeSessionRef(
            source, agent, p.AgentSessionId, p.AgentSessionPath, p.SessionStartSource, required: false);
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation(ProtocolMethods.PaneReportAgent);
            var pane = RequirePane(paneId);
            RejectIfReplacementInFlight(pane.Id.Value);
            if (pane.AgentAuthority is { } held
                && !string.Equals(held.Source, source, StringComparison.Ordinal))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    "source does not own pane agent authority");
            }

            if (IsStaleAuthoritySequence(pane, source, p.Sequence))
                return new AgentAuthorityResult { Ok = true };
            var sequences = AcceptAuthoritySequence(pane, source, p.Sequence);

            UpdatePaneEmittingStatus(pane.Id, current => current with
            {
                AgentStatus = state,
                AgentKind = agent,
                AgentMessage = EmptyToNull(p.Message),
                AgentSession = session ?? current.AgentSession,
                AgentAuthority = new PaneAgentAuthority
                {
                    Source = source,
                    Agent = agent,
                    State = state,
                    Message = EmptyToNull(p.Message),
                    Sequence = p.Sequence,
                    Session = session?.Value ?? current.AgentAuthority?.Session,
                    ReportedAt = _time.GetUtcNow(),
                },
                AgentAuthoritySequences = sequences,
            });
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        return new AgentAuthorityResult { Ok = true };
    }

    private async Task<AgentAuthorityResult> PaneReportAgentSessionAsync(
        PaneReportAgentSessionParams p, CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.PaneReportAgentSession);
        var paneId = RequireField(p.PaneId, "pane_id");
        var source = RequireNormalizedSource(p.Source);
        var agent = RequireAgentName(p.Agent);
        var session = RequireNativeSessionRef(source, agent, p);
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation(ProtocolMethods.PaneReportAgentSession);
            var pane = RequirePane(paneId);
            RejectIfReplacementInFlight(pane.Id.Value);
            if (IsStaleAuthoritySequence(pane, source, p.Sequence))
                return new AgentAuthorityResult { Ok = true };
            var sequences = AcceptAuthoritySequence(pane, source, p.Sequence);
            // A
            // custom hook may hold state while an official integration reports
            // a native session ref.

            _ = _state.UpdatePane(pane.Id, current => current with
            {
                AgentSession = session,
                AgentAuthority = current.AgentAuthority is { } authority
                    && string.Equals(authority.Source, source, StringComparison.Ordinal)
                    ? authority with { Session = session.Value }
                    : current.AgentAuthority,
                AgentAuthoritySequences = sequences,
            });
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        return new AgentAuthorityResult { Ok = true };
    }

    private async Task<MetadataReportResult> PaneReportMetadataAsync(
        PaneReportMetadataParams p, CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.PaneReportMetadata);
        var id = RequireField(p.PaneId, "pane_id");
        var source = RequireField(p.Source, "source");
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation(ProtocolMethods.PaneReportMetadata);
            if (_state.GetPane(new PaneId(id)) is null)
                throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
            RejectIfReplacementInFlight(id);
            await _metadataEmitGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EmitExpiredMetadataLockedAsync(ct).ConfigureAwait(false);
                if (_state.GetPane(new PaneId(id)) is null)
                    throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
                var result = _metadata.ApplyPatch("pane", id, source, p.Tokens, p.Sequence, p.TtlMs, _time.GetUtcNow());
                if (!result.Accepted)
                    throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, result.Error ?? "invalid metadata");
                if (result.Changed)
                {
                    if (_state.GetPane(new PaneId(id)) is null)
                        throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {id}");
                    await EmitLivePaneMetadataUpdatedAsync(id, result.Values, ct)
                        .ConfigureAwait(false);
                }

                return new MetadataReportResult { Ok = true };
            }
            finally
            {
                _metadataEmitGate.Release();
            }
        }
        finally
        {
            _bindingMutationGate.Release();
        }
    }

    private async Task<AgentAuthorityResult> PaneReleaseAgentAsync(
        PaneReleaseAgentParams p, CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.PaneReleaseAgent);
        var paneId = RequireField(p.PaneId, "pane_id");
        var source = RequireNormalizedSource(p.Source);
        var agent = RequireAgentName(p.Agent);
        var persist = false;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation(ProtocolMethods.PaneReleaseAgent);
            var pane = RequirePane(paneId);
            RejectIfReplacementInFlight(pane.Id.Value);
            var dropAuthority = pane.AgentAuthority is { } held
                && string.Equals(held.Source, source, StringComparison.Ordinal)
                && string.Equals(held.Agent, agent, StringComparison.Ordinal);
            var dropSession = pane.AgentSession is { } session
                && string.Equals(session.Source, source, StringComparison.Ordinal)
                && string.Equals(session.Agent, agent, StringComparison.Ordinal);
            if (pane.AgentAuthority is not null && !dropAuthority)
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    "source does not own pane agent authority");
            }

            if (IsStaleAuthoritySequence(pane, source, p.Sequence))
                return new AgentAuthorityResult { Ok = true };
            persist = ApplyReleaseOrClearMutation(pane, source, p.Sequence, dropAuthority, dropSession);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        if (persist)
            await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        return new AgentAuthorityResult { Ok = true };
    }

    private async Task<AgentAuthorityResult> PaneClearAgentAuthorityAsync(
        PaneClearAgentAuthorityParams p, CancellationToken ct)
    {
        EnsureNotFrozenForMutation(ProtocolMethods.PaneClearAgentAuthority);
        var paneId = RequireField(p.PaneId, "pane_id");
        // Admin-clear only when source is omitted. Empty/whitespace is a present
        // non-owner and must fail closed (InvalidParams), same as report/release.
        string? source = p.Source is null ? null : RequireNormalizedSource(p.Source);
        var persist = false;
        await _bindingMutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotFrozenForMutation(ProtocolMethods.PaneClearAgentAuthority);
            var pane = RequirePane(paneId);
            RejectIfReplacementInFlight(pane.Id.Value);
            // A source-omitted clear still validates seq against the current owner.
            if (source is not null
                && pane.AgentAuthority is { } held
                && !string.Equals(held.Source, source, StringComparison.Ordinal))
            {
                throw new ControlPlaneException(
                    ProtocolErrorCodes.InvalidState,
                    "source does not own pane agent authority");
            }

            var sequenceSource = source ?? pane.AgentAuthority?.Source;
            if (sequenceSource is not null && IsStaleAuthoritySequence(pane, sequenceSource, p.Sequence))
                return new AgentAuthorityResult { Ok = true };

            var dropAuthority = source is null
                ? pane.AgentAuthority is not null
                : pane.AgentAuthority is { } owned
                    && string.Equals(owned.Source, source, StringComparison.Ordinal);
            var dropSession = source is null
                ? pane.AgentSession is not null
                : pane.AgentSession is { } session
                    && string.Equals(session.Source, source, StringComparison.Ordinal);
            persist = ApplyReleaseOrClearMutation(
                pane, sequenceSource, p.Sequence, dropAuthority, dropSession);
        }
        finally
        {
            _bindingMutationGate.Release();
        }

        if (persist)
            await PersistGraphAsync(requireDurable: true).ConfigureAwait(false);
        return new AgentAuthorityResult { Ok = true };
    }

    /// <summary>
    /// Status and waits change only when <see cref="PaneAgentAuthority"/> ends.
    /// A matching session-only release/clear drops the native ref and accepts
    /// <c>seq</c>. Empty-holder is a no-op.
    /// </summary>
    private bool ApplyReleaseOrClearMutation(
        PaneState pane,
        string? sequenceSource,
        long? sequence,
        bool dropAuthority,
        bool dropSession)
    {
        if (!dropAuthority && !dropSession)
            return false;

        var sequences = sequenceSource is null
            ? pane.AgentAuthoritySequences
            : AcceptAuthoritySequence(pane, sequenceSource, sequence);
        if (dropAuthority)
        {
            _ = UpdatePaneEmittingStatus(pane.Id, current => current with
            {
                AgentAuthority = null,
                AgentSession = dropSession ? null : current.AgentSession,
                AgentAuthoritySequences = sequences,
                AgentStatus = AgentStatus.Unknown,
            });
            RefreshAgentPane(pane.Id.Value);
            return true;
        }

        _ = _state.UpdatePane(pane.Id, current => current with
        {
            AgentSession = null,
            AgentAuthoritySequences = sequences,
        });
        return true;
    }

    private PaneState RequirePane(string paneId) =>
        _state.GetPane(new PaneId(paneId))
        ?? throw new ControlPlaneException(ProtocolErrorCodes.NotFound, $"Pane not found: {paneId}");

    private static string RequireNormalizedSource(string? source)
    {
        var raw = RequireField(source, "source");
        if (!MetadataTokenNormalizer.TryNormalizeSource(raw, out var normalized))
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "source is invalid");
        return normalized;
    }

    private static string RequireAgentName(string? agent)
    {
        var trimmed = RequireField(agent, "agent").Trim();
        if (trimmed.Length == 0)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "agent is required");
        return trimmed;
    }

    private static AgentStatus RequireAgentReportState(string? state)
    {
        var raw = RequireField(state, "state").Trim().ToLowerInvariant();
        return raw switch
        {
            "working" => AgentStatus.Working,
            "blocked" => AgentStatus.Blocked,
            "idle" => AgentStatus.Idle,
            "unknown" => AgentStatus.Unknown,
            "done" => AgentStatus.Done,
            _ => throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "state must be working, blocked, idle, unknown, or done"),
        };
    }

    private static NativeAgentSessionRef RequireNativeSessionRef(
        string source,
        string agent,
        PaneReportAgentSessionParams p) =>
        ParseNativeSessionRef(
            source, agent, p.AgentSessionId, p.AgentSessionPath, p.SessionStartSource, required: true)!;

    private static NativeAgentSessionRef? ParseNativeSessionRef(
        string source,
        string agent,
        string? agentSessionId,
        string? agentSessionPath,
        string? sessionStartSource,
        bool required)
    {
        var id = EmptyToNull(agentSessionId);
        var path = EmptyToNull(agentSessionPath);
        if (id is not null && path is not null)
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "provide agent_session_id or agent_session_path");
        if (id is null && path is null)
        {
            if (required)
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "agent_session_id or agent_session_path is required");
            return null;
        }

        string kind;
        string value;
        if (id is not null)
        {
            if (id.Length > NativeAgentSessionRef.MaxIdLength)
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "agent_session_id is too long");
            if (NativeAgentSessionRef.ContainsControl(id))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "agent_session_id contains a control character");
            kind = NativeAgentSessionRef.KindId;
            value = id;
        }
        else
        {
            if (path!.Length > NativeAgentSessionRef.MaxPathLength)
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "agent_session_path is too long");
            if (NativeAgentSessionRef.ContainsControl(path))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "agent_session_path contains a control character");
            if (!Path.IsPathRooted(path))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "agent_session_path must be a fully qualified path");
            kind = NativeAgentSessionRef.KindPath;
            value = path;
        }

        var start = EmptyToNull(sessionStartSource);
        if (start is not null)
        {
            start = start.Trim().ToLowerInvariant();
            if (!NativeAgentSessionRef.IsSessionStartSource(start))
                throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "session_start_source is invalid");
        }

        return new NativeAgentSessionRef
        {
            Kind = kind,
            Value = value,
            Source = source,
            Agent = agent,
            SessionStartSource = start,
        };
    }

    private static bool HoldsSemanticAuthority(PaneState pane) => pane.HoldsSemanticAuthority;

    private static AgentStatus StatusAfterProcessDeath(PaneState pane) => pane.StatusAfterProcessDeath();

    private static IReadOnlyDictionary<string, long> EmptyAuthoritySequences() =>
        new Dictionary<string, long>(StringComparer.Ordinal);

    private void RejectIfReplacementInFlight(string paneId)
    {
        if (HasUncommittedReplacement(paneId))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidState,
                "occupant replacement in progress");
        }
    }

    private static bool IsStaleAuthoritySequence(PaneState pane, string source, long? sequence)
    {
        if (!pane.AgentAuthoritySequences.TryGetValue(source, out var previous))
            return false;
        if (sequence is not { } seq)
            return true;
        return seq <= previous;
    }

    private static IReadOnlyDictionary<string, long> AcceptAuthoritySequence(
        PaneState pane, string source, long? sequence)
    {
        if (sequence is not { } seq)
            return pane.AgentAuthoritySequences;
        if (!pane.AgentAuthoritySequences.ContainsKey(source)
            && pane.AgentAuthoritySequences.Count >= MetadataTokenLimits.MaxSequencedSources)
        {
            throw new ControlPlaneException(ProtocolErrorCodes.InvalidParams, "too many sequenced sources");
        }

        var next = new Dictionary<string, long>(pane.AgentAuthoritySequences, StringComparer.Ordinal)
        {
            [source] = seq,
        };
        return next;
    }

    private JsonNode? AgentSessionToJson(NativeAgentSessionRef? session)
    {
        if (session is null)
            return null;
        var obj = new JsonObject
        {
            ["kind"] = session.Kind,
            ["value"] = session.Value,
            ["source"] = session.Source,
            ["agent"] = session.Agent,
        };
        if (!string.IsNullOrWhiteSpace(session.SessionStartSource))
            obj["session_start_source"] = session.SessionStartSource;
        return obj;
    }

    /// <summary>
    /// Live-only Lifecycle emit for <c>pane.metadata_updated</c>. Never journals.
    /// Uses the render seq domain so journal seq is not torn.
    /// Caller holds <see cref="_metadataEmitGate"/>.
    /// </summary>
    private async Task EmitLivePaneMetadataUpdatedAsync(
        string paneId, IReadOnlyDictionary<string, string> tokens, CancellationToken ct)
    {
        if (_subscriptions is null)
            return;

        var payload = RuntimeEventPayloadJson.WritePaneMetadataUpdated(paneId, tokens);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PaneMetadataUpdated, payload);
        var seq = _paneRenderSeq.AddOrUpdate(
            ProtocolEventTypes.PaneMetadataUpdated, 1L, static (_, prev) => prev + 1);
        var rec = new RuntimeEventRecord
        {
            Seq = seq,
            Class = EventClass.Lifecycle,
            Reliability = EventReliability.Reliable,
            Type = ProtocolEventTypes.PaneMetadataUpdated,
            OccurredAt = _time.GetUtcNow(),
            PayloadJson = payload,
        };
        await _subscriptions.FanoutLiveAsync(rec, ct).ConfigureAwait(false);
    }
}
