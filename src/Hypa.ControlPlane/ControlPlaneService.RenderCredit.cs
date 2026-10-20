using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace Hypa.ControlPlane;

/// <summary>
/// Render credit. A connection that offered
/// <see cref="AttachEndpointProtocol.RenderCreditCapability"/> in its hello
/// gets a budget of unacked render bytes. A pane without the focus keeps its
/// latest frame deferred while the budget is spent, and the mux does not
/// encode it. When credit comes back, the mux encodes one delta from the last
/// admitted frame of the pane to that deferred frame. The deferred frame is
/// the latest capture, so the client gets the current state and skips the
/// frames between. The base is the last admitted frame, not the last acked
/// frame: the client applies every admitted frame first.
/// <para>
/// A large cells frame or JSON full snapshot goes in slices of whole rows,
/// each with its own charge, so an echo waits for one slice at most. The rest
/// of a started frame goes before any later frame of the same pane.
/// </para>
/// </summary>
public sealed partial class ControlPlaneService
{
    private RenderCreditLedger? _renderCredits;

    /// <summary>The credit limits for a connection that the next hello configures.</summary>
    internal RenderCreditOptions RenderCreditOptions { get; set; } = RenderCreditOptions.Default;

    private RenderCreditLedger RenderCredits =>
        LazyInitializer.EnsureInitialized(ref _renderCredits, () => new RenderCreditLedger(_time));

    /// <summary>The credit of a connection, or null when it has none. Diagnostic.</summary>
    internal RenderCreditState? RenderCreditStateFor(string connectionId) =>
        RenderCredits.For(connectionId)?.State;

    /// <summary>The connections that have render credit now. Diagnostic.</summary>
    internal IReadOnlyList<string> RenderCreditConnectionIds => RenderCredits.ConnectionIds;

    /// <summary>
    /// Turns credit on for <paramref name="connection"/> when its hello lists
    /// the capability. A hello without it turns credit off.
    /// </summary>
    private void ConfigureRenderCredit(IClientConnection connection, AttachEndpointHello hello)
    {
        var offered = hello.OptionalCapabilities is { } optional
            && optional.Contains(AttachEndpointProtocol.RenderCreditCapability, StringComparer.Ordinal);
        var connectionId = connection.ConnectionId;
        RenderCredits.Configure(
            connectionId,
            offered && connection.UsesWriterLanes,
            () => ScheduleRenderCreditRetry(connectionId),
            RenderCreditOptions);
    }

    /// <summary>
    /// True when a new frame of <paramref name="paneId"/> may go to
    /// <paramref name="connectionId"/> now. A connection without credit is
    /// never limited.
    /// </summary>
    private bool MayAdmitRender(string connectionId, string paneId) =>
        RenderCredits.For(connectionId)?.MayAdmit(paneId) ?? true;

    /// <summary>
    /// Charges one encoded frame when it fits in the credit of
    /// <paramref name="connectionId"/>. A frame that does not fit stays
    /// deferred, so the bytes in flight stay near the budget. A connection
    /// without credit always takes the frame.
    /// </summary>
    private bool TryChargeRenderCredit(string connectionId, string paneId, long generation, int bytes, int rowEnd = RenderRows.All) =>
        RenderCredits.For(connectionId)?.TryCharge(paneId, generation, bytes, rowEnd) ?? true;

    /// <summary>
    /// The writer did not take a charged frame. Take the charge back. A full
    /// writer runs the deferred frames again on its next drain.
    /// </summary>
    private void RefundRenderCredit(
        string connectionId,
        string paneId,
        long generation,
        int bytes,
        WriterLaneResult admitted,
        int rowEnd = RenderRows.All)
    {
        var credit = RenderCredits.For(connectionId);
        if (credit is null)
            return;
        credit.Refund(paneId, generation, bytes, rowEnd);
        if (admitted.IsFull)
            credit.NoteWriterFull();
    }

    /// <summary>
    /// Puts the slices of one JSON full on the ordered lane of
    /// <paramref name="sub"/>. Without credit, every slice goes as one
    /// batch. With credit, the first slices that fit in the budget go now,
    /// with one charge, and <paramref name="rest"/> holds the slices that
    /// wait for credit. The client acks each slice, so the bytes in front of
    /// an echo stay near one slice. <paramref name="rest"/> is also the whole
    /// batch when the writer refused it, and null when nothing waits.
    /// </summary>
    private WriterLaneResult EnqueueJsonFullSlices(
        EventSubscription sub,
        string paneId,
        DeferredJsonFull batch,
        out DeferredJsonFull? rest)
    {
        var credit = RenderCredits.For(sub.ConnectionId);
        if (credit is null)
        {
            var all = sub.Sink.EnqueueOrderedRenderBatch(batch.Lines, batch.EmitBinding);
            rest = all.IsFull ? batch : null;
            return all;
        }

        var sizes = new RenderLine[batch.Lines.Count];
        for (var i = 0; i < sizes.Length; i++)
        {
            var rowEnd = batch.RowEnds is { } ends && i < ends.Count ? ends[i] : RenderRows.All;
            sizes[i] = new RenderLine(batch.Lines[i].Length, rowEnd);
        }

        // A pane without the focus puts one slice in front of the writer at a
        // time. The ordered lane takes one barrier, so a focused echo then
        // waits for one slice at most, not for a budget of other panes.
        var focused = string.Equals(credit.FocusedPaneId, paneId, StringComparison.Ordinal);
        IReadOnlyList<RenderLine> chargeable = focused || sizes.Length <= 1 ? sizes : sizes[..1];
        var (count, _) = credit.TryChargeLines(paneId, batch.Generation, chargeable);
        if (count == 0)
        {
            rest = batch;
            return WriterLaneResult.Full;
        }

        // The one-slice cap held back slices that credit would allow. They go
        // on the next writer drain, not on the next ack, so bulk output keeps
        // its rate. The flag is set before the enqueue, so the drain of this
        // slice always sees it. The drain retry tries the focused pane first.
        var capped = count < sizes.Length && count == chargeable.Count && chargeable.Count < sizes.Length;
        if (capped)
            credit.NoteWriterFull();

        var lines = count == sizes.Length ? batch.Lines : batch.Lines.Take(count).ToArray();
        var admitted = sub.Sink.EnqueueOrderedRenderBatch(lines, batch.EmitBinding);
        if (!admitted.IsOk)
        {
            credit.RefundLines(paneId, batch.Generation, chargeable, count);
            if (admitted.IsFull)
                credit.NoteWriterFull();
            rest = admitted.IsFull ? batch : null;
            return admitted;
        }

        rest = count == sizes.Length
            ? null
            : batch with
            {
                Lines = batch.Lines.Skip(count).ToArray(),
                RowEnds = batch.RowEnds?.Skip(count).ToArray(),
                Started = true,
            };
        return admitted;
    }

    /// <summary>
    /// The credit slices of one cells payload, or null when the payload goes
    /// as one line: the connection has no credit, or the line is not larger
    /// than one slice. A slice holds whole rows. A row larger than the slice
    /// size goes alone, under the line cap of the connection.
    /// </summary>
    private IReadOnlyList<AttachCellsPackedSlice>? CreditSlicesFor(
        EventSubscription sub,
        TerminalRenderCellsPayload payload,
        int lineBytes,
        int lineCap)
    {
        var sliceBytes = RenderCreditOptions.SliceBytes;
        if (sliceBytes < 256 || lineBytes <= sliceBytes || !HasRenderCredit(sub.ConnectionId))
            return null;
        var slices = AttachCellsLinePacker.SliceToFitPacked(
            payload, sub.SubscriptionId, lineCap, skipCompleteFitCheck: true, partTargetBytes: sliceBytes);
        return slices.Count > 1 ? slices : null;
    }

    /// <summary>
    /// Puts the slices of one cells frame on the ordered lane of a connection
    /// with credit. The first slices that fit go now. The frame is then the
    /// delta base of the pane, and the rest waits for credit as a started
    /// batch, so it goes before any later frame of the pane. Full means that
    /// no slice went and the caller defers the frame.
    /// </summary>
    private WriterLaneResult AdmitCellsSlicesWithCredit(
        EventSubscription sub,
        string paneId,
        TerminalRenderCellsPayload payload,
        byte[] payloadUtf8,
        IReadOnlyList<ReadOnlyMemory<byte>> lines,
        IReadOnlyList<AttachCellsPackedSlice> slices,
        string frameIdentity,
        AttachSurfaceEmitBinding? emitBinding)
    {
        var rowEnds = new int[slices.Count];
        for (var i = 0; i < rowEnds.Length; i++)
            rowEnds[i] = RenderRows.CellsRowEnd(slices[i].Payload);
        var batch = new DeferredJsonFull(
            lines,
            frameIdentity,
            0,
            emitBinding,
            payload.Generation,
            Started: false,
            CellsRest: true,
            RowEnds: rowEnds);
        var admitted = EnqueueJsonFullSlices(sub, paneId, batch, out var rest);
        if (ReferenceEquals(rest, batch))
            return admitted.IsOk ? WriterLaneResult.Full : admitted;
        if (!admitted.IsOk)
            return admitted;
        sub.NoteAdmittedPayloadUtf8(paneId, payloadUtf8);
        if (rest is not null)
            DeferJsonFullRest(sub, paneId, rest);
        return admitted;
    }

    /// <summary>
    /// The JSON full slice size for a paint of <paramref name="paneId"/>:
    /// the credit slice size when a live observer of the pane has render
    /// credit, else zero (no extra slices).
    /// </summary>
    private int JsonFullSliceBytes(string paneId)
    {
        if (_subscriptions is null)
            return 0;
        foreach (var sub in _subscriptions.ListLiveObservers(paneId))
        {
            if (RenderCredits.For(sub.ConnectionId) is not null)
                return RenderCreditOptions.SliceBytes;
        }

        return 0;
    }

    /// <summary>
    /// Input to <paramref name="paneId"/> was admitted. Opens the echo window
    /// of the pane, and gives the echo the focused render budget of the
    /// connection that sent the input. Each input path calls this one step.
    /// </summary>
    private void MarkPaneInputAdmitted(IClientConnection? connection, string paneId)
    {
        _renderCoalescer.MarkInputAdmitted(paneId);
        if (connection is not null)
            NoteRenderCreditInput(connection.ConnectionId, paneId);
    }

    /// <summary>
    /// The connection sent input to <paramref name="paneId"/>: it takes the
    /// focused budget. The mux reads the input of a connection in order, so
    /// the latest input always names the focused pane. No ack moves it.
    /// </summary>
    private void NoteRenderCreditInput(string connectionId, string paneId)
    {
        if (RenderCredits.For(connectionId)?.NoteFocus(paneId) == true)
            ScheduleRenderCreditRetry(connectionId);
    }

    private bool HasRenderCredit(string connectionId) => RenderCredits.For(connectionId) is not null;

    /// <summary>Test seam: charges bytes on the credit of a connection as one frame.</summary>
    internal void ChargeRenderCreditForTests(string connectionId, string paneId, long generation, int bytes) =>
        RenderCredits.For(connectionId)?.Charge(paneId, generation, bytes);

    /// <summary>
    /// Stores the rest of a slice batch, then makes sure a retry runs after
    /// the store. A writer drain or an ack that ran before the store found
    /// nothing to retry. While the writer-full flag is still set, the drain
    /// that takes it comes after this store. When the flag is clear, the drain
    /// already ran, so this schedules the retry. This never loops: a retry
    /// that the full writer refuses sets the flag again first, and a pane with
    /// no credit left has bytes in flight, so their ack or the ack timeout
    /// runs the retry.
    /// </summary>
    private void DeferJsonFullRest(EventSubscription sub, string paneId, DeferredJsonFull rest)
    {
        sub.DeferJsonFull(paneId, rest);
        var credit = RenderCredits.For(sub.ConnectionId);
        if (credit is not null && !credit.WriterFullPending && credit.MayAdmit(paneId))
            ScheduleRenderCreditRetry(sub.ConnectionId);
    }

    /// <summary>True when a writer drain must run the deferred frames of a connection with credit.</summary>
    private bool TakeRenderWriterFull(string connectionId) =>
        RenderCredits.For(connectionId)?.TakeWriterFull() ?? false;

    /// <summary>
    /// Applies one <c>render.ack</c> of <paramref name="connection"/>. The
    /// Unix socket read loop calls this at once, so a slow request on the same
    /// connection never holds the credit back.
    /// </summary>
    internal void ApplyRenderAck(IClientConnection connection, JsonElement? parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (RenderCredits.For(connection.ConnectionId) is null
            || parameters is not { ValueKind: JsonValueKind.Object } element)
        {
            return;
        }

        RenderAckParams? ack;
        try
        {
            ack = element.Deserialize(ProtocolJsonContext.Default.RenderAckParams);
        }
        catch (JsonException)
        {
            return;
        }

        if (ack is not null)
            ApplyRenderAck(connection.ConnectionId, ack);
    }

    private void ApplyRenderAck(string connectionId, RenderAckParams ack)
    {
        var credit = RenderCredits.For(connectionId);
        if (credit is null)
            return;
        var entries = new List<RenderAck>(ack.Acks?.Count ?? 0);
        if (ack.Acks is { } acks)
        {
            foreach (var entry in acks)
            {
                if (entry is { PaneId.Length: > 0, Generation: > 0 })
                    entries.Add(new RenderAck(entry.PaneId, entry.Generation, entry.RowEnd ?? RenderRows.All));
            }
        }

        if (credit.Ack(entries))
            ScheduleRenderCreditRetry(connectionId);
    }

    /// <summary>
    /// The registry route of <c>render.ack</c>. The Unix socket server takes
    /// the notification before the dispatch queue, so this route serves other
    /// transports and tests.
    /// </summary>
    internal Task<JsonElement> HandleRenderAckAsync(
        RenderAckParams p,
        IClientConnection? connection,
        CancellationToken ct)
    {
        _ = ct;
        ArgumentNullException.ThrowIfNull(p);
        if (connection is not null)
            ApplyRenderAck(connection.ConnectionId, p);
        return Task.FromResult(Ok(new System.Text.Json.Nodes.JsonObject { ["ok"] = true }));
    }

    /// <summary>
    /// Runs the deferred frames of <paramref name="connectionId"/> again. One
    /// run is in flight at a time for a connection. A request during a run
    /// starts one more run after it, so no deferred frame waits for a later
    /// ack.
    /// </summary>
    private void ScheduleRenderCreditRetry(string connectionId)
    {
        if (_subscriptions is null || string.IsNullOrEmpty(connectionId))
            return;
        var retry = _renderCreditRetries.GetOrAdd(connectionId, static _ => new RenderCreditRetry());
        if (Interlocked.Exchange(ref retry.Requested, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunRenderCreditRetryAsync(connectionId, retry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Render credit retry failed for {ConnectionId}", connectionId);
            }
        });
    }

    /// <summary>Orders <paramref name="panes"/> with <paramref name="focused"/> first.</summary>
    internal static IEnumerable<string> FocusedFirst(IEnumerable<string> panes, string? focused)
    {
        var list = panes as IReadOnlyCollection<string> ?? panes.ToList();
        if (focused is not null && list.Contains(focused, StringComparer.Ordinal))
            yield return focused;
        foreach (var paneId in list)
        {
            if (!string.Equals(paneId, focused, StringComparison.Ordinal))
                yield return paneId;
        }
    }

    private async Task RunRenderCreditRetryAsync(string connectionId, RenderCreditRetry retry)
    {
        await retry.Run.WaitAsync().ConfigureAwait(false);
        try
        {
            while (Interlocked.Exchange(ref retry.Requested, 0) != 0)
            {
                if (_subscriptions is null)
                    return;
                var focused = RenderCredits.For(connectionId)?.FocusedPaneId;
                foreach (var sub in _subscriptions.ListForConnection(connectionId))
                {
                    // The focused pane goes first, so its echo takes the
                    // writer before the next slice of a busy pane.
                    foreach (var paneId in FocusedFirst(sub.DeferredRenderPanes(), focused))
                        await RetryDeferredPaneAsync(sub, paneId).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            retry.Run.Release();
        }
    }

    /// <summary>
    /// Admits the deferred JSON full and the deferred frame of one pane under
    /// the emit gate of the pane. Without the gate, a live paint could admit
    /// a frame at the same time: the client would reject a second delta on
    /// the same base, or paint an older snapshot after a newer frame.
    /// <para>
    /// The newer of the two goes. A JSON full that the client has no slice of
    /// is stale when a newer cells frame waits or went. The rest of a started
    /// JSON full always goes before the next cells frame of the pane, so the
    /// client completes that batch.
    /// </para>
    /// </summary>
    private async Task RetryDeferredPaneAsync(EventSubscription sub, string paneId)
    {
        if (sub.IsClosed)
            return;
        var gate = GetPaneEmitGate(paneId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = sub.TryTakeDeferredJsonFull(paneId);
            var hasFrame = sub.TryTakeDeferredFrame(paneId, out var frame, out var emitBinding, out var inputAck);
            if (json is { Started: false }
                && ((hasFrame && frame.Generation > json.Generation)
                    || sub.AdmittedGeneration(paneId) > json.Generation))
            {
                json = null;
            }

            if (json is not null)
            {
                // The snapshot holds a newer state than this frame.
                if (hasFrame && frame.Generation < json.Generation)
                    hasFrame = false;
                TryAdmitDeferredJsonFull(sub, paneId, json);
            }

            if (!hasFrame)
                return;
            // A live paint admitted a newer frame while this one waited.
            if (sub.AdmittedGeneration(paneId) >= frame.Generation && sub.ReanchorGeneration(paneId) == 0)
                return;
            TryAdmitDeferredVtFrame(sub, paneId, frame, emitBinding, inputAck);
        }
        finally
        {
            gate.Release();
        }
    }

    private void ForgetRenderCredit(string connectionId)
    {
        RenderCredits.Forget(connectionId);
        _renderCreditRetries.TryRemove(connectionId, out _);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RenderCreditRetry> _renderCreditRetries =
        new(StringComparer.Ordinal);

    private sealed class RenderCreditRetry
    {
        public int Requested;

        public SemaphoreSlim Run { get; } = new(1, 1);
    }
}
