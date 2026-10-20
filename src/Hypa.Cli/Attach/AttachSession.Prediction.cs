using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Latency;
using Hypa.Cli.Attach.Prediction;

namespace Hypa.Cli.Attach;

/// <summary>
/// The attach side of predictive local echo (ADR 0018). The predictor only
/// changes what the host shows. The predicted cells go into the one
/// host-size frame through the normal compose and the patch path, and are
/// encoded once with the rest of the frame.
/// </summary>
public sealed partial class AttachSession
{
    /// <summary>
    /// Records the predictions of a pane payload that the input sender
    /// accepted under <paramref name="mark"/>. A key that changes the shown
    /// predictions paints the host once. The paint sends no RPC.
    /// </summary>
    internal static void ObservePaneInput(
        UnixRawTerminal tty,
        AttachLiveState live,
        ReadOnlyMemory<byte> payload,
        long mark,
        bool paste,
        AssembledSnapshot? preSend = null,
        string? preSendPane = null)
    {
        ArgumentNullException.ThrowIfNull(tty);
        ArgumentNullException.ThrowIfNull(live);
        if (!live.Prediction.Predicts)
            return;
        var readAt = live.Latency.CurrentReadTimestamp;
        live.WithPaint(() =>
        {
            // The focused pane is read under the paint lock. If the sender
            // later sends these keys to another pane, OnSeqAssigned ends the
            // predictions.
            var paneId = live.PaneId;
            if (string.IsNullOrWhiteSpace(paneId))
                return;
            live.TryGetPaneFrame(paneId, out var frame);
            // The base of a prediction is the frame from before the send. A
            // new frame object, or a new focused pane, means that a frame came
            // during the send (it can hold this echo, and it can reuse the
            // cell storage of the old one). The base is then unknown: these
            // keys go as unpredicted keys.
            if (preSendPane is not null
                && (!string.Equals(preSendPane, paneId, StringComparison.Ordinal)
                    || !ReferenceEquals(frame, preSend)))
            {
                if (live.Prediction.ObserveUnknownBase(paneId, mark, paste))
                    PaintPrediction(tty, live);
                return;
            }

            if (!live.Prediction.Observe(
                    paneId,
                    payload.Span,
                    mark,
                    frame,
                    paste,
                    PredictionAllowed(live, paneId),
                    PaneRunsAgent(live, paneId)))
            {
                return;
            }

            if (PaintPrediction(tty, live) && readAt != 0 && live.ComposePrediction is not null)
                live.Latency.RecordHop(AttachLatencyHop.PredictedPaint, readAt);
        });
        ArmPredictionTimer(tty, live);
    }

    /// <summary>The predictor counters for <c>latency.report</c>, by snake_case name.</summary>
    internal static Dictionary<string, long> PredictionCounters(EchoPredictorStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var counters = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["made"] = stats.Made,
            ["unpredicted"] = stats.Unpredicted,
            ["barrier_skips"] = stats.BarrierSkips,
            ["shown"] = stats.PredictedShown,
            ["mispredicted_shown"] = stats.MispredictedShown,
            ["confirmed_glyph"] = stats.ConfirmedByGlyph,
            ["confirmed_cursor"] = stats.ConfirmedByCursor,
            ["retired"] = stats.Retired,
            ["mismatches"] = stats.Mismatches,
            ["expired"] = stats.Expired,
            ["resets"] = stats.Resets,
            ["pending"] = stats.Pending,
            ["srtt_us"] = stats.Srtt is { } srtt ? (long)srtt.TotalMicroseconds : -1,
        };
        foreach (var (reason, count) in stats.ResetCalls)
            counters["reset." + System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(reason.ToString())] = count;
        return counters;
    }

    /// <summary>
    /// Keys went to the focused pane on a path that sends no seq. They can
    /// never confirm, so they end every epoch.
    /// </summary>
    internal static void NoteUnsequencedPaneInput(UnixRawTerminal tty, AttachLiveState live)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Prediction.NoteUnsequencedInput())
            live.WithPaint(() => PaintPrediction(tty, live));
    }

    /// <summary>
    /// The predictions shown by the compose in progress. Null when the
    /// focused pane is not the predicted pane, or when an off condition holds.
    /// </summary>
    private static PredictionView? ComposePredictionView(AttachLiveState live, AssembledSnapshot? generationFrame)
    {
        if (!live.Prediction.Predicts || live.Prediction.PaneId is not { } paneId)
            return null;
        var focused = live.Chrome is { } chrome ? chrome.FocusedPaneId ?? live.PaneId : live.PaneId;
        if (!string.Equals(paneId, focused, StringComparison.Ordinal))
        {
            live.Prediction.Reset(PredictionResetReason.FocusChange);
            return null;
        }

        // An off condition deletes the epochs, so they do not come back when
        // the layer closes.
        if (!PredictionAllowed(live, paneId))
        {
            live.Prediction.Reset(PredictionResetReason.NotAllowed);
            return null;
        }

        return live.Prediction.View(
            paneId,
            ResolveComposeFrame(live, paneId, generationFrame),
            PaneRunsAgent(live, paneId));
    }

    /// <summary>
    /// True when the pane runs a detected agent (the pane <c>agent</c>
    /// field). The alternate screen then does not turn prediction off.
    /// </summary>
    internal static bool PaneRunsAgent(AttachLiveState live, string paneId) =>
        DetectedAgentOf(live, paneId) is not null;

    /// <summary>
    /// The agent field of <paramref name="paneId"/> changed. When the pane had
    /// a detected agent and now has none, the predictions of that pane go,
    /// and one patch paints the pane cells back. The alternate screen of the
    /// program that stays no longer allows prediction.
    /// </summary>
    internal static void NotePredictionAgentChanged(
        UnixRawTerminal? tty,
        AttachLiveState live,
        string paneId,
        bool hadAgent)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!hadAgent
            || PaneRunsAgent(live, paneId)
            || !string.Equals(live.Prediction.PaneId, paneId, StringComparison.Ordinal))
        {
            return;
        }

        if (live.Prediction.Reset(PredictionResetReason.AgentEnded) && tty is not null)
            live.WithPaint(() => PaintPrediction(tty, live));
    }

    /// <summary>
    /// False while a layer owns the screen or the pane does not show its live
    /// rows: the modal overlay, the history overlay, a mode other than
    /// terminal, a dialog, a popup with the caret, the splash, or scrollback.
    /// </summary>
    private static bool PredictionAllowed(AttachLiveState live, string paneId) =>
        !live.Overlay.OwnsModal
        && !OverlayIsVisiblePane(live, paneId)
        && !live.History.ShowsOverlay
        && LayoutPaintMode(live) is AttachClientMode.Terminal
        && !live.Worktrees.IsOpen
        && !live.CubesPairing.IsOpen
        && !PopupOwnsHostCaret(live)
        && live.Splash is not { IsPlaying: true }
        && !live.PaneIsScrolledBack(paneId);

    /// <summary>
    /// Stamps the predicted cells of <paramref name="paneId"/> into the pane
    /// box, after the pane cells and under the same occlusion. A later layer
    /// (selection, toast, popup, overlay) stamps over them.
    /// </summary>
    private static void StampPrediction(
        AttachLiveState live,
        string? paneId,
        CellRect box,
        CellRect? occlude,
        int? onlyRow = null)
    {
        if (live.ComposePrediction is not { } view
            || !string.Equals(view.PaneId, paneId, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var cell in view.Cells)
        {
            if (onlyRow is { } row && cell.Row != row)
                continue;
            if ((uint)cell.Col >= (uint)box.Cols || (uint)cell.Row >= (uint)box.Rows)
                continue;
            var hostCol = box.Col + cell.Col;
            var hostRow = box.Row + cell.Row;
            if (occlude is { } occ && occ.Contains(hostCol, hostRow))
                continue;
            live.Host.Stamp(hostCol, hostRow, cell.Cell);
        }
    }

    /// <summary>
    /// Adds the predicted pane to a patch: the rows that show predictions now
    /// and the rows that showed predictions at the last accepted paint, so
    /// a deleted prediction is restamped from the pane frame.
    /// </summary>
    private static void AddPredictionRows(AttachLiveState live, HostPatchRequest patch)
    {
        var view = live.ComposePrediction;
        var painted = live.PaintedPrediction;
        var paneId = view?.PaneId ?? painted?.PaneId;
        if (paneId is null)
            return;
        var hasCells = view is { Cells.Count: > 0 };
        var hasPainted = painted is { Rows.Length: > 0 } shownBefore
            && string.Equals(shownBefore.PaneId, paneId, StringComparison.Ordinal);
        if (!hasCells && !hasPainted)
            return;
        if (!live.TryGetPaneFrame(paneId, out var stored) || stored is null)
            return;
        var pane = patch.For(paneId, stored);
        var frame = pane.Frame ??= stored;
        if (hasCells)
        {
            foreach (var row in view!.Rows)
            {
                if ((uint)row < (uint)frame.Rows)
                    pane.Rows.Add(row);
            }
        }

        if (hasPainted)
        {
            foreach (var row in painted!.Value.Rows)
            {
                if ((uint)row < (uint)frame.Rows)
                    pane.Rows.Add(row);
            }
        }
    }

    /// <summary>
    /// After an accepted host paint: the predicted rows that the host shows
    /// now, and the predictions that it showed for the first time.
    /// </summary>
    private static void CommitPredictionPaint(AttachLiveState live, bool overlayOnly)
    {
        if (overlayOnly)
            return;
        var view = live.ComposePrediction;
        live.PaintedPrediction = view is { Cells.Count: > 0 }
            ? (view.PaneId, view.Rows.ToArray())
            : null;
        if (view is not null)
            live.Prediction.NoteShown(view);
    }

    /// <summary>
    /// A captured compose stamps without a write. Keep every predicted row
    /// as painted, so the next patch restamps it.
    /// </summary>
    private static void MergePredictionRows(AttachLiveState live)
    {
        var view = live.ComposePrediction;
        if (view is not { Cells.Count: > 0 })
            return;
        var rows = new HashSet<int>(view.Rows);
        if (live.PaintedPrediction is { } prior && string.Equals(prior.PaneId, view.PaneId, StringComparison.Ordinal))
            rows.UnionWith(prior.Rows);
        live.PaintedPrediction = (view.PaneId, rows.ToArray());
    }

    /// <summary>
    /// One host paint for a change of the shown predictions: a patch of the
    /// predicted pane against its stored frame. A fallback reason makes it a
    /// full compose. Caller holds the paint lock or takes it here.
    /// </summary>
    private static bool PaintPrediction(UnixRawTerminal tty, AttachLiveState live)
    {
        var paneId = live.Prediction.PaneId;
        if (paneId is null || !live.TryGetPaneFrame(paneId, out var stored) || stored is null)
            return false;
        var patch = new HostPatchRequest();
        patch.For(paneId, stored).Frame = stored;
        return WriteHostComposed(tty, live, patch: patch);
    }

    /// <summary>
    /// Arms the one prediction timer for the next timed rule: the judge
    /// 50 ms after an ack, the ack expiry, or the 250 ms show rule.
    /// </summary>
    private static void ArmPredictionTimer(UnixRawTerminal tty, AttachLiveState live)
    {
        if (!live.Prediction.Predicts)
            return;
        var wake = live.Prediction.NextWake();
        lock (live.PredictionTimerGate)
        {
            if (wake is null)
            {
                live.PredictionTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }

            live.PredictionTimer ??= live.Time.CreateTimer(
                _ => PredictionTick(tty, live),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            live.PredictionTimer.Change(wake.Value, Timeout.InfiniteTimeSpan);
        }
    }

    private static void PredictionTick(UnixRawTerminal tty, AttachLiveState live)
    {
        try
        {
            if (live.Prediction.PaneId is not { } paneId)
                return;
            // The paint lock only: the timer reads no key engine state, so a
            // key feed does not wait for this paint.
            live.WithFramePaint(() =>
            {
                live.TryGetPaneFrame(paneId, out var frame);
                if (live.Prediction.Tick(paneId, frame))
                    PaintPrediction(tty, live);
            });
            ArmPredictionTimer(tty, live);
        }
        catch (Exception)
        {
            // A timer paint after the host TTY closed has nothing to repair.
        }
    }
}
