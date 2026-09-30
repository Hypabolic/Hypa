using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

public sealed partial class ControlPlaneService
{
    internal const int NotificationTitleMax = 80;
    internal const int NotificationBodyMax = 200;
    internal const double NotificationCoreRatePerSecond = 4;
    internal const double NotificationCoreBurst = 8;

    internal bool NotificationsSuppressed { get; set; }

    internal Task<JsonElement> HandleNotificationShowAsync(
        NotificationShowParams p, IClientConnection? connection, CancellationToken ct) =>
        NotificationShowAsync(p, connection, ct);

    private async Task<JsonElement> NotificationShowAsync(
        NotificationShowParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        _ = connection;
        var source = string.IsNullOrWhiteSpace(p.Source)
            ? NotificationSources.Core
            : p.Source.Trim();
        RejectPluginPresentationParams(p);

        var title = NormalizeNoticeText(p.Title, NotificationTitleMax);
        if (string.IsNullOrEmpty(title))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "title is required");
        }

        var body = string.IsNullOrWhiteSpace(p.Body)
            ? null
            : NormalizeNoticeText(p.Body, NotificationBodyMax);
        if (body is { Length: 0 })
            body = null;

        var sound = string.IsNullOrWhiteSpace(p.Sound)
            ? NotificationSounds.None
            : p.Sound.Trim().ToLowerInvariant();
        if (sound is not (NotificationSounds.None or NotificationSounds.Done or NotificationSounds.Request))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "sound must be none, done, or request");
        }

        var paneId = string.IsNullOrWhiteSpace(p.PaneId) ? null : p.PaneId.Trim();
        var reason = ResolveNotificationReason(source, paneId);
        if (reason != NotificationShowReasons.Shown)
        {
            return OkTyped(
                new NotificationShowResult { Reason = reason, Source = source },
                ProtocolJsonContext.Default.NotificationShowResult);
        }

        MarkNotificationShown(source, paneId);
        var payload = RuntimeEventPayloadJson.WriteNotificationShown(
            title, body, source, sound, paneId, NotificationShowReasons.Shown);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.NotificationShown, payload);
        await EmitReliableAsync(
                EventClass.Control,
                ProtocolEventTypes.NotificationShown,
                payload,
                ct)
            .ConfigureAwait(false);

        return OkTyped(
            new NotificationShowResult { Reason = NotificationShowReasons.Shown, Source = source },
            ProtocolJsonContext.Default.NotificationShowResult);
    }

    internal string ResolveNotificationReason(string source, string? paneId)
    {
        if (NotificationsSuppressed || !AttachConfig.Ui.Toast.Enabled || IsSourceMuted(source))
            return NotificationShowReasons.Disabled;
        if (IsShuttingDown || SessionLifecycle.IsFrozen(_state.Snapshot().LifecycleState))
            return NotificationShowReasons.Busy;
        if (!HasForegroundClient())
            return NotificationShowReasons.NoForegroundClient;
        if (IsNotificationBusy(source, paneId))
            return NotificationShowReasons.Busy;
        if (!TryTakeNotificationToken(source))
            return NotificationShowReasons.RateLimited;
        return NotificationShowReasons.Shown;
    }

    internal static bool IsPluginSource(string source) =>
        source.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase)
        || source.StartsWith("plugin.", StringComparison.OrdinalIgnoreCase);

    internal bool IsSourceMuted(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;
        return AttachConfig.Ui.Toast.Sources.TryGetValue(source.Trim(), out var mode)
            && mode is ToastSourceMode.Off;
    }

    private static void RejectPluginPresentationParams(NotificationShowParams p)
    {
        if (!string.IsNullOrWhiteSpace(p.Position)
            || !string.IsNullOrWhiteSpace(p.Colour)
            || !string.IsNullOrWhiteSpace(p.Color)
            || !string.IsNullOrWhiteSpace(p.Glyph))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "position, colour, and glyph are host-owned");
        }
    }

    /// <summary>
    /// Skill model: idle is seen done. A transition into Done is unseen unless
    /// the occupant was already reviewed (Done or Idle).
    /// </summary>
    internal static bool SeenAfterStatus(PaneState pane, AgentStatus nextStatus)
    {
        if (nextStatus == AgentStatus.Idle)
            return true;
        if (nextStatus != AgentStatus.Done)
            return pane.Seen;
        if (pane.AgentStatus is AgentStatus.Done or AgentStatus.Idle)
            return pane.Seen;
        return false;
    }

    internal static string NormalizeNoticeText(string? text, int maxChars)
    {
        var encoded = SafeDisplayText.Encode(text?.Trim());
        if (encoded.Length <= maxChars)
            return encoded;
        return encoded[..maxChars];
    }

    internal bool HasForegroundClient()
    {
        foreach (var att in _attachments.ListAll())
        {
            if (att.Mode is AttachmentModes.Observe or AttachmentModes.Control)
                return true;
        }

        return false;
    }

    private bool TryTakeNotificationToken(string source)
    {
        var bucket = _notificationBuckets.GetOrAdd(source, _ => new NotificationTokenBucket());
        return bucket.TryTake(_time);
    }

    private bool IsNotificationBusy(string source, string? paneId)
    {
        var key = source + "\0" + (paneId ?? "");
        if (!_notificationBusyUntil.TryGetValue(key, out var until))
            return false;
        return until > _time.GetUtcNow();
    }

    private void MarkNotificationShown(string source, string? paneId)
    {
        var delay = Math.Max(0, AttachConfig.Ui.Toast.DelaySeconds);
        var key = source + "\0" + (paneId ?? "");
        _notificationBusyUntil[key] = _time.GetUtcNow().AddSeconds(delay);
    }

    private Task<bool> EmitReliableAsync(
        EventClass cls,
        string type,
        string payload,
        CancellationToken ct,
        Func<bool>? stillValid = null,
        Action<RuntimeEventRecord>? onPublished = null) =>
        PublishReliableAsync(
            cls,
            type,
            payload,
            ct,
            stillValid,
            notifyPlugin: true,
            publishWithoutJournal: true,
            onPublished: onPublished,
            awaitDelivery: true);

    /// <summary>
    /// Allocate a reliable sequence, publish it live, then commit the disk write.
    /// Publish does not wait for fsync or a SQLite floor write.
    /// A disk failure consumes the sequence and raises the retention floor.
    /// The identity check runs before publish.
    /// </summary>
    private async Task<bool> PublishReliableAsync(
        EventClass cls,
        string type,
        string payload,
        CancellationToken ct,
        Func<bool>? stillValid = null,
        bool notifyPlugin = false,
        string? pluginPayload = null,
        bool publishWithoutJournal = false,
        IClientConnection? sequenceConnection = null,
        Action<RuntimeEventRecord>? onPublished = null,
        bool awaitDelivery = false)
    {
        if (_journal is null)
        {
            if (!publishWithoutJournal || _subscriptions is null)
                return false;
            if (!await IdentityStillValidAfterDelayAsync(stillValid).ConfigureAwait(false))
                return false;
            var liveOnly = new RuntimeEventRecord
            {
                Seq = 0,
                Class = cls,
                Reliability = EventReliability.Reliable,
                Type = type,
                PayloadJson = payload,
                OccurredAt = _time.GetUtcNow(),
            };
            if (sequenceConnection is ClientConnection liveCtx)
                liveCtx.LastEmittedSeq = liveOnly.Seq;
            await _subscriptions.FanoutLiveAsync(liveOnly, ct).ConfigureAwait(false);
            if (notifyPlugin)
                NotifyPluginEvent(type, pluginPayload ?? payload);
            onPublished?.Invoke(liveOnly);
            return true;
        }

        await _emitGate.WaitAsync(ct).ConfigureAwait(false);
        RuntimeEventRecord? reserved = null;
        try
        {
            // Recheck occupant identity after the emit gate so a replacement
            // that lands during WaitAsync cannot allocate or publish.
            if (!await IdentityStillValidAfterDelayAsync(stillValid).ConfigureAwait(false))
                return false;

            reserved = _journal.ReserveReliable(cls, type, payload, _time.GetUtcNow());
            if (reserved is null)
                return false;

            if (sequenceConnection is ClientConnection ctx)
                ctx.LastEmittedSeq = reserved.Seq;
            SyncJournalFlagsToAppState();
            _subscriptions?.PostLive(reserved);
            try
            {
                if (notifyPlugin)
                    NotifyPluginEvent(type, pluginPayload ?? payload);
                onPublished?.Invoke(reserved);
            }
            catch
            {
                // The record is live: its commit must still be read.
                _ = _journal.CommitReservedAsync(reserved, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _emitGate.Release();
        }

        // Only the path that awaited FanoutLiveAsync before this change waits for
        // delivery. Other publishes stay queue-only, so a slow client cannot
        // block their RPC.
        if (awaitDelivery && _subscriptions is not null)
            await _subscriptions.WhenQueuedDeliveredAsync(ct).ConfigureAwait(false);
        // Not the RPC token: the commit result must always be read, and the
        // event is already live, so a cancel here would report a false failure.
        await _journal.CommitReservedAsync(reserved!, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> IdentityStillValidAfterDelayAsync(Func<bool>? stillValid)
    {
        if (stillValid is null)
            return true;
        if (!stillValid())
            return false;

        var delay = DelayAfterReliableEmitIdentityCheckAsync;
        if (delay is not null)
            await delay().ConfigureAwait(false);

        return stillValid();
    }

    private async Task EmitAgentStatusChangedAsync(PaneState pane, AgentStatus previous, CancellationToken ct)
    {
        // One-shot waiters consume this same emission (report, detection, process
        // exit). Publish before the first await so fire-and-forget callers notify
        // waiters on the mutation thread.
        // events_after reads the hub sequence; Hypa fans the emit into the waiter.
        PublishAgentStatusWaitEvent(pane);

        // Fire-and-forget callers capture PaneState at mutation time. Recheck
        // occupant identity before journal/fanout so a replacement cannot toast.
        var delay = DelayAgentStatusEmitAsync;
        if (delay is not null)
            await delay(pane).ConfigureAwait(false);

        if (!OccupantGenerationIsCurrent(pane))
            return;

        var payload = RuntimeEventPayloadJson.WritePaneAgentStatusChanged(
            pane.Id.Value,
            pane.TabId.Value,
            pane.WorkspaceId.Value,
            pane.OccupantGeneration,
            pane.AgentStatus.ToString().ToLowerInvariant(),
            previous.ToString().ToLowerInvariant(),
            pane.AgentKind,
            pane.AgentMessage,
            pane.Seen);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PaneAgentStatusChanged, payload);
        if (!OccupantGenerationIsCurrent(pane))
            return;

        var afterCheck = DelayAfterAgentStatusEmitCheckAsync;
        if (afterCheck is not null)
            await afterCheck(pane).ConfigureAwait(false);

        await EmitReliableAsync(
                EventClass.Lifecycle,
                ProtocolEventTypes.PaneAgentStatusChanged,
                payload,
                ct,
                stillValid: () => OccupantGenerationIsCurrent(pane),
                onPublished: _ => AttachProcessLog.AgentStatusEmitted(
                    _processLog,
                    _state.SessionId.Value,
                    pane.Id.Value,
                    pane.AgentKind,
                    pane.AgentStatus.ToString().ToLowerInvariant(),
                    previous.ToString().ToLowerInvariant()))
            .ConfigureAwait(false);
    }

    private bool OccupantGenerationIsCurrent(PaneState pane)
    {
        var current = _state.GetPane(pane.Id);
        return current is not null && current.OccupantGeneration == pane.OccupantGeneration;
    }

    internal bool TryApplyAgentStatus(
        string paneId,
        int? expectedGeneration,
        AgentStatus status,
        string? kind,
        string? message)
    {
        var applied = false;
        var pane = UpdatePaneEmittingStatus(new PaneId(paneId), p =>
        {
            if (expectedGeneration is { } gen && p.OccupantGeneration != gen)
                return p;
            if (HoldsSemanticAuthority(p))
                return p;

            var nextKind = kind ?? p.AgentKind;
            var nextMessage = message;
            if (status == p.AgentStatus
                && string.Equals(nextKind, p.AgentKind, StringComparison.Ordinal)
                && string.Equals(nextMessage, p.AgentMessage, StringComparison.Ordinal))
            {
                return p;
            }

            applied = true;
            return p with
            {
                AgentStatus = status,
                AgentKind = nextKind,
                AgentMessage = nextMessage,
            };
        });
        return applied && pane is not null;
    }

    /// <summary>
    /// Graph write that emits <c>pane.agent_status_changed</c> when status fields
    /// change. Always applies <see cref="SeenAfterStatus"/> on that transition
    /// (done is unseen; idle is seen done).
    /// </summary>
    private PaneState? UpdatePaneEmittingStatus(PaneId id, Func<PaneState, PaneState> mutate)
    {
        AgentStatus? previous = null;
        var pane = _state.UpdatePane(id, p =>
        {
            var next = mutate(p);
            if (next.AgentStatus == p.AgentStatus
                && string.Equals(next.AgentKind, p.AgentKind, StringComparison.Ordinal)
                && string.Equals(next.AgentMessage, p.AgentMessage, StringComparison.Ordinal))
            {
                return next;
            }

            previous = p.AgentStatus;
            return next with
            {
                Seen = SeenAfterStatus(p, next.AgentStatus),
                UpdatedAt = _time.GetUtcNow(),
            };
        });

        if (previous is not null && pane is not null)
            _ = EmitAgentStatusChangedAsync(pane, previous.Value, CancellationToken.None);

        return pane;
    }

    internal async Task EmitLayoutUpdatedAsync(string? tabId, string? paneId, CancellationToken ct)
    {
        JsonObject snapshot;
        try
        {
            snapshot = ExportLayout(tabId, paneId);
        }
        catch (ControlPlaneException)
        {
            return;
        }

        var payload = snapshot.ToJsonString();
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.LayoutUpdated, payload);
        await EmitReliableAsync(
                EventClass.Lifecycle,
                ProtocolEventTypes.LayoutUpdated,
                payload,
                ct)
            .ConfigureAwait(false);
    }

    internal void MaybeEmitScrollChanged(IPaneRuntime runtime, bool originOnly = false)
    {
        if (originOnly)
        {
            EmitOriginOnlyScrollChanged(runtime);
            return;
        }

        int offset;
        int maxOffset;
        if (runtime is IPaneVtSnapshot snap && snap.TryGetScrollMetrics(out offset, out maxOffset))
        {
            // engine metrics
        }
        else
        {
            var pane = _state.GetPane(runtime.Id);
            var recent = runtime.ReadRecentText(10_000);
            var lines = CountTextLines(recent);
            maxOffset = Math.Max(0, lines - (pane?.Rows ?? 40));
            offset = 0;
        }

        var next = (offset, maxOffset);
        var hasPrevious = _lastScroll.TryGetValue(runtime.Id.Value, out var previous);
        if (hasPrevious && previous == next)
            return;

        _lastScroll[runtime.Id.Value] = next;

        var payload = RuntimeEventPayloadJson.WritePaneScrollChanged(runtime.Id.Value, offset, maxOffset);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PaneScrollChanged, payload);
        _ = EmitReliableAsync(
            EventClass.Lifecycle,
            ProtocolEventTypes.PaneScrollChanged,
            payload,
            CancellationToken.None);
    }

    /// <summary>
    /// PTY callback path. Reads the stored origin only. Does not measure
    /// scrollback or copy recent text.
    /// </summary>
    private void EmitOriginOnlyScrollChanged(IPaneRuntime runtime)
    {
        if (runtime is not IPaneVtSnapshot originSnap)
            return;
        if (!originSnap.TryGetScrollOrigin(out var origin))
            return;

        if (!_lastScroll.TryGetValue(runtime.Id.Value, out var previousOrigin))
        {
            _lastScroll[runtime.Id.Value] = (origin, 0);
            return;
        }

        if (previousOrigin.Offset == origin)
            return;

        // Incomplete pair. Coalesced Full paint publishes offset and max
        // together. Do not journal origin above the last known max.
        if (origin > previousOrigin.MaxOffset)
            return;

        _lastScroll[runtime.Id.Value] = (origin, previousOrigin.MaxOffset);
        var payload = RuntimeEventPayloadJson.WritePaneScrollChanged(
            runtime.Id.Value,
            origin,
            previousOrigin.MaxOffset);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PaneScrollChanged, payload);
        _ = EmitReliableAsync(
            EventClass.Lifecycle,
            ProtocolEventTypes.PaneScrollChanged,
            payload,
            CancellationToken.None);
    }

    /// <summary>
    /// Production scroll-origin commit. Sets the VT origin then emits
    // / <c>pane.scroll_changed</c> when metrics change.
    /// <c>src/server/headless.rs:401-406</c> HostScroll returns without a
    /// snapshot. Origin paint is coalesced 16 ms latest-wins.
    /// </summary>
    internal bool TryApplyPaneScroll(string paneId, int offset)
    {
        if (!TryGetRuntime(paneId, out var runtime) || runtime is null)
            return false;
        if (runtime is not IPaneVtSnapshot snap || !snap.TryGetScrollOrigin(out _))
            return false;
        if (!snap.TrySetScrollOrigin(offset))
            return false;
        MaybeEmitScrollChanged(runtime, originOnly: false);
        _renderCoalescer.RequestOriginPaint(paneId);
        return true;
    }

    /// <summary>
    /// Coalesced Full paint. Publish complete (offset, max_offset) while
    // / parked.
    /// frame. Do not use originOnly on this path.
    /// </summary>
    private void MaybeEmitParkedScrollMetrics(IPaneVtSnapshot snapshot)
    {
        if (snapshot is not IPaneRuntime runtime)
            return;
        if (!snapshot.TryGetScrollMetrics(out _, out _))
            return;
        MaybeEmitScrollChanged(runtime, originOnly: false);
    }

    internal bool TryPeekOriginPaint(string paneId, out bool forceFull) =>
        _renderCoalescer.TryPeekPending(paneId, out forceFull);

    /// <summary>
    /// Test hook: pending coalescer paint for one pane. A pane with no live
    /// observer schedules no flush, so no entry stays pending.
    /// </summary>
    internal bool TryPeekCoalescerPendingForTest(string paneId, out long feedGeneration) =>
        _renderCoalescer.TryPeekPending(paneId, out _, out feedGeneration);

    private JsonElement PaneScroll(PaneScrollParams p)
    {
        var id = RequireField(p.PaneId, "pane_id");
        if (p.Offset is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "offset is required");
        }

        if (!TryGetRuntime(id, out var runtime) || runtime is null)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.NotFound,
                $"Pane not found: {id}");
        }

        if (runtime is not IPaneVtSnapshot)
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "pane has no scroll origin");
        }

        if (!TryApplyPaneScroll(id, p.Offset.Value)
            || runtime is not IPaneVtSnapshot snap
            || !snap.TryGetScrollMetrics(out var offset, out var maxOffset))
        {
            throw new ControlPlaneException(
                ProtocolErrorCodes.InvalidParams,
                "pane has no scroll origin");
        }

        return OkTyped(
            new PaneScrollResult
            {
                PaneId = id,
                Offset = offset,
                MaxOffset = maxOffset,
            },
            ProtocolJsonContext.Default.PaneScrollResult);
    }

    internal void ForceScrollForTests(string paneId, int offset, int maxOffset)
    {
        if (TryApplyPaneScroll(paneId, offset))
            return;

        var next = (offset, maxOffset);
        if (_lastScroll.TryGetValue(paneId, out var previous) && previous == next)
            return;
        _lastScroll[paneId] = next;
        var payload = RuntimeEventPayloadJson.WritePaneScrollChanged(paneId, offset, maxOffset);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PaneScrollChanged, payload);
        _ = EmitReliableAsync(
            EventClass.Lifecycle,
            ProtocolEventTypes.PaneScrollChanged,
            payload,
            CancellationToken.None);
    }

    internal async Task EmitOutputMatchedAsync(string paneId, bool matched, CancellationToken ct)
    {
        var payload = RuntimeEventPayloadJson.WritePaneOutputMatched(paneId, matched);
        payload = _redactor.RedactJsonPayload(ProtocolEventTypes.PaneOutputMatched, payload);
        await EmitReliableAsync(
                EventClass.Lifecycle,
                ProtocolEventTypes.PaneOutputMatched,
                payload,
                ct)
            .ConfigureAwait(false);
    }

    private static int CountTextLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var lines = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
                lines++;
        }

        return lines;
    }

    private sealed class NotificationTokenBucket
    {
        private readonly object _gate = new();
        private double _tokens = NotificationCoreBurst;
        private DateTimeOffset _last = DateTimeOffset.UnixEpoch;

        public bool TryTake(TimeProvider time)
        {
            lock (_gate)
            {
                var now = time.GetUtcNow();
                if (_last != DateTimeOffset.UnixEpoch)
                {
                    var elapsed = (now - _last).TotalSeconds;
                    if (elapsed > 0)
                    {
                        _tokens = Math.Min(
                            NotificationCoreBurst,
                            _tokens + (elapsed * NotificationCoreRatePerSecond));
                    }
                }

                _last = now;
                if (_tokens < 1)
                    return false;
                _tokens -= 1;
                return true;
            }
        }
    }
}
