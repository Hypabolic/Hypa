using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Latency;
using Hypa.Cli.Attach.Prediction;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The typing fixtures with predictive echo on and a simulated round trip
/// of 80 ms. The mux replays each fixture as in the main harness. The client
/// sees each key 40 ms before the mux writes it and each cells body 40 ms
/// after the mux posts it. The ack of a body is the newest key that the mux
/// wrote before the post.
/// </summary>
public sealed partial class TypingConformanceTests
{
    private static readonly TimeSpan OneWay = TimeSpan.FromMilliseconds(40);

    /// <summary>One confirm: the round trip, the judge delay and two render ticks.</summary>
    private static readonly TimeSpan OneConfirm = (OneWay * 2) + EchoPredictor.JudgeDelay + TimeSpan.FromMilliseconds(32);

    /// <summary>
    /// The fixtures on the main screen. The recorded Claude Code 2.1.301
    /// starts the alternate screen (<c>ESC[?1049h</c>). ADR 0018 turns
    /// prediction off there for a pane with no detected agent (see the
    /// full-screen test) and keeps it on for a pane with a detected agent
    /// (see the agent test).
    /// </summary>
    private static readonly string[] PredictedFixtureNames =
    [
        "zsh-f",
        "zsh-p10k",
        "bash-norc",
        "sync-echo-tui",
    ];

    /// <summary>
    /// Fixtures in which no shown prediction may be wrong. zsh-p10k is not in
    /// the list: its echo of the 40-byte burst takes more than the judge
    /// delay, so one judge deletes the epoch while the line redraws. Its
    /// wrong glyphs still go within one confirm.
    /// </summary>
    private static readonly string[] ExactFixtureNames = ["zsh-f", "bash-norc", "sync-echo-tui"];

    public static TheoryData<string> PredictedFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var name in PredictedFixtureNames)
            data.Add(name);
        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(PredictedFixtures))]
    public async Task With_prediction_at_80_ms_the_host_frame_matches_the_oracle_after_the_acks(string fixture)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var timeline = await RecordServerAsync(TypingFixture.Load(fixture));
        var result = ReplayPredicted(timeline);
        ReportPredicted(fixture, result);
        Assert.True(result.NonConvergedKeys == 0, result.Divergences.FirstOrDefault());
        Assert.True(
            result.MaxWrongGlyphMs <= OneConfirm.TotalMilliseconds,
            $"a wrong glyph stayed {result.MaxWrongGlyphMs:0} ms: {result.WrongGlyphs.FirstOrDefault()}");
        Assert.True(result.Stats.PredictedShown > 0, "no prediction was shown");
        if (ExactFixtureNames.Contains(fixture))
            Assert.Equal(0, result.Stats.MispredictedShown);
    }

    /// <summary>
    /// The echo bytes of Claude Code with the alternate-screen switch taken
    /// out of the stream that the mux and the oracle read. This proves the
    /// confirm rule for a space on the real echo. The recorded stream, with
    /// the switch, shows no prediction.
    /// </summary>
    [SkippableFact]
    public async Task Claude_code_echo_on_the_main_screen_shows_predicted_spaces_that_confirm_by_the_cursor()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var timeline = await RecordServerAsync(WithoutAlternateScreen(TypingFixture.Load("claude-code")));
        var result = ReplayPredicted(timeline);
        ReportPredicted("claude-code main screen", result);
        Assert.True(result.NonConvergedKeys == 0, result.Divergences.FirstOrDefault());
        Assert.True(
            result.MaxWrongGlyphMs <= OneConfirm.TotalMilliseconds,
            $"a wrong glyph stayed {result.MaxWrongGlyphMs:0} ms: {result.WrongGlyphs.FirstOrDefault()}");
        var spaces = timeline.Keys.Where(key => key.Label == "SPACE").ToList();
        Assert.NotEmpty(spaces);
        foreach (var space in spaces)
        {
            Assert.True(
                result.CaretMovedBeforeEcho.Contains(space.Index),
                $"space key #{space.Index}: the caret did not move before the echo");
            Assert.True(
                result.ConfirmedByCursor.Contains(space.Index),
                $"space key #{space.Index}: no confirm by the cursor");
        }
    }

    /// <summary>
    /// The recorded Claude Code stream, with its alternate-screen switch, on
    /// a pane whose agent field names Claude Code. The alternate screen then
    /// does not turn prediction off.
    /// </summary>
    [SkippableFact]
    public async Task Claude_code_on_the_alternate_screen_of_a_detected_agent_pane_shows_predictions_that_converge()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var timeline = await RecordServerAsync(TypingFixture.Load("claude-code"));
        var result = ReplayPredicted(timeline, agent: "claude");
        ReportPredicted("claude-code agent pane", result);
        Assert.True(result.NonConvergedKeys == 0, result.Divergences.FirstOrDefault());
        Assert.True(
            result.MaxWrongGlyphMs <= OneConfirm.TotalMilliseconds,
            $"a wrong glyph stayed {result.MaxWrongGlyphMs:0} ms: {result.WrongGlyphs.FirstOrDefault()}");
        Assert.True(result.Stats.PredictedShown > 0, "no prediction was shown");
        Assert.True(result.PredictedPaints > 0, "no predicted paint");
    }

    [SkippableTheory]
    [InlineData("vim-insert")]
    [InlineData("less")]
    [InlineData("claude-code")]
    public async Task A_full_screen_program_shows_no_predicted_cell(string fixture)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var timeline = await RecordServerAsync(TypingFixture.Load(fixture));
        var result = ReplayPredicted(timeline);
        ReportPredicted(fixture, result);
        Assert.Equal(0, result.Stats.PredictedShown);
        Assert.Equal(0, result.PredictedPaints);
    }

    /// <summary>The fixture with <c>ESC[?1049h</c> and <c>ESC[?1049l</c> removed from each read.</summary>
    private static TypingFixture WithoutAlternateScreen(TypingFixture fixture)
    {
        static byte[] Strip(byte[] bytes)
        {
            var text = System.Text.Encoding.Latin1.GetString(bytes);
            text = text.Replace("\u001b[?1049h", "", StringComparison.Ordinal)
                .Replace("\u001b[?1049l", "", StringComparison.Ordinal);
            return System.Text.Encoding.Latin1.GetBytes(text);
        }

        var keys = fixture.Keys
            .Select(key => key with { Chunks = key.Chunks.Select(chunk => chunk with { Bytes = Strip(chunk.Bytes) }).ToList() })
            .ToList();
        return fixture with { Keys = keys };
    }

    private void ReportPredicted(string fixture, PredictedReplay result)
    {
        output.WriteLine(
            $"{fixture} | rtt 80 ms | keys {result.Keys} | non_converged {result.NonConvergedKeys} | "
            + $"shown {result.Stats.PredictedShown} | mispredicted {result.Stats.MispredictedShown} | "
            + $"glyph confirms {result.Stats.ConfirmedByGlyph} | cursor confirms {result.Stats.ConfirmedByCursor} | "
            + $"retired {result.Stats.Retired} | mismatches {result.Stats.Mismatches} | expired {result.Stats.Expired} | "
            + $"max wrong glyph {result.MaxWrongGlyphMs:0} ms | predicted paints {result.PredictedPaints}");
        foreach (var line in result.Divergences.Take(8))
            output.WriteLine("  " + line);
        foreach (var line in result.WrongGlyphs.Take(8))
            output.WriteLine("  wrong: " + line);
        foreach (var line in result.Notes)
            output.WriteLine("  note: " + line);
    }

    /// <summary>
    /// Replays the fixture on the mux as <see cref="ReplayAsync"/> does, and
    /// records each cells body with its post time, each key with the time
    /// the mux wrote it, and the oracle after each key.
    /// </summary>
    private async Task<ServerTimeline> RecordServerAsync(TypingFixture fixture)
    {
        await using var server = await ServerSession.OpenAsync(_lib, fixture.Cols, fixture.Rows);
        using var oracle = new GhosttyVtEngine(fixture.Cols, fixture.Rows, libraryPathOverride: _lib);
        var timeline = new ServerTimeline(fixture.Cols, fixture.Rows, server.PaneId, server.Now);

        void Collect()
        {
            foreach (var body in server.TakeCells())
                timeline.Bodies.Add((server.Now, body));
        }

        await server.SettleAsync(TimeSpan.FromMilliseconds(100));
        Collect();
        var index = 0;
        foreach (var key in fixture.Keys)
        {
            await server.SettleAsync(TimeSpan.FromMilliseconds(100));
            Collect();
            if (key.HasInput)
            {
                server.Cp.MarkInputAdmittedForTests(server.PaneId);
                timeline.Keys.Add(new TimelineKey(++index, key.Label, key.Input, server.Now));
            }

            var previousMs = 0.0;
            foreach (var chunk in key.Chunks)
            {
                var gap = TimeSpan.FromMilliseconds(Math.Clamp(chunk.Ms - previousMs, 0, 20));
                previousMs = chunk.Ms;
                await server.SettleAsync(gap);
                Collect();
                oracle.Feed(chunk.Bytes);
                server.Runtime.PublishFedOutputForTests(chunk.Bytes);
                await server.DeliveredAsync();
                Collect();
            }

            for (var step = 0; step < 40; step++)
            {
                await server.SettleAsync(TimeSpan.FromMilliseconds(1));
                Collect();
            }

            timeline.OracleAfter[index] = oracle.CaptureSnapshot();
        }

        return timeline;
    }

    /// <summary>
    /// Runs the client side of a recorded timeline on its own virtual clock,
    /// with the predictor of the attach session under the adaptive policy.
    /// <paramref name="agent"/> is the agent field of the pane, or null.
    /// </summary>
    private PredictedReplay ReplayPredicted(ServerTimeline timeline, string? agent = null)
    {
        var clock = new VirtualClock(timeline.Start);
        using var client = new PredictingClient(timeline.PaneId, timeline.Cols, timeline.Rows, clock);
        if (agent is not null)
            AttachSession.PatchSidebarAgentStatus(client.Live, timeline.PaneId, null, "working", agent, agentSpecified: true);
        using var host = new GhosttyVtEngine(timeline.Cols, timeline.Rows, libraryPathOverride: _lib);
        var result = new PredictedReplay();

        // The client events in time order: each key 40 ms before the mux
        // wrote it, each body 40 ms after its post.
        var events = new List<(DateTimeOffset At, int Order, TimelineKey? Key, TerminalRenderCellsPayload? Body)>();
        var order = 0;
        foreach (var key in timeline.Keys)
            events.Add((key.WrittenAt - OneWay, order++, key, null));
        foreach (var (postedAt, body) in timeline.Bodies)
        {
            long? ack = null;
            foreach (var key in timeline.Keys)
            {
                if (key.WrittenAt <= postedAt)
                    ack = key.Index;
            }

            events.Add((postedAt + OneWay, order++, null, ack is null ? body : body with { InputAck = ack }));
        }

        events.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Order.CompareTo(b.Order));
        var wrongSince = new Dictionary<(int Col, int Row), DateTimeOffset>();
        var observed = 0;
        var acked = 0;
        foreach (var ev in events)
        {
            if (ev.Key is { } key)
            {
                // The quiet time before the next key: every body of the
                // previous key arrived. The host must equal its oracle.
                var tickBefore = client.Live.Prediction.Stats();
                clock.AdvanceTo(ev.At);
                NoteDeletion(client, tickBefore, $"timer before key #{key.Index}", result);
                client.Drain(host);
                if (observed > 0)
                    CheckConverged(timeline, host, observed, result);
                TrackWrongGlyphs(timeline, host, clock.GetUtcNow(), observed, acked, wrongSince, result);

                var before = client.Live.Prediction.Stats();
                client.Type(key);
                client.Drain(host);
                observed = key.Index;
                result.Keys++;
                if (timeline.OracleAfter.TryGetValue(key.Index, out var want)
                    && want.Cursor is { } wantCursor
                    && host.CaptureSnapshot().Cursor is { } gotCursor
                    && gotCursor.Col == wantCursor.Col
                    && gotCursor.Row == wantCursor.Row
                    && client.Live.Prediction.Stats().PredictedShown > before.PredictedShown)
                {
                    result.CaretMovedBeforeEcho.Add(key.Index);
                }

                result.KeyStats[key.Index] = before;
                continue;
            }

            var timerBefore = client.Live.Prediction.Stats();
            clock.AdvanceTo(ev.At);
            NoteDeletion(client, timerBefore, $"timer after key #{observed}", result);
            var statsBefore = client.Live.Prediction.Stats();
            var cursorBefore = statsBefore.ConfirmedByCursor;
            client.Apply(ev.Body!);
            client.Drain(host);
            NoteDeletion(client, statsBefore, $"body ack {ev.Body!.InputAck} after key #{observed}", result);
            if (ev.Body!.InputAck is { } bodyAck)
                acked = (int)Math.Max(acked, bodyAck);
            if (client.Live.Prediction.Stats().ConfirmedByCursor > cursorBefore && ev.Body.InputAck is { } confirmedAck)
                result.ConfirmedByCursor.Add((int)confirmedAck);
            TrackWrongGlyphs(timeline, host, clock.GetUtcNow(), observed, acked, wrongSince, result);
        }

        // After the last ack: the judge timer runs, then the host must
        // equal the oracle of the last key.
        clock.AdvanceTo(clock.GetUtcNow() + TimeSpan.FromMilliseconds(200));
        client.Drain(host);
        TrackWrongGlyphs(timeline, host, clock.GetUtcNow(), observed, acked, wrongSince, result);
        if (observed > 0)
            CheckConverged(timeline, host, observed, result);
        foreach (var since in wrongSince.Values)
            result.MaxWrongGlyphMs = Math.Max(result.MaxWrongGlyphMs, (clock.GetUtcNow() - since).TotalMilliseconds);
        result.Stats = client.Live.Prediction.Stats();
        result.PredictedPaints = client.Live.Latency.Report().Hops["predicted-paint"].Count;
        return result;
    }

    /// <summary>Notes a mismatch or an expiry, with the row of the pane cursor.</summary>
    private static void NoteDeletion(PredictingClient client, EchoPredictorStats before, string when, PredictedReplay result)
    {
        var after = client.Live.Prediction.Stats();
        if (after.Mismatches == before.Mismatches && after.Expired == before.Expired)
            return;
        client.Live.TryGetPaneFrame(client.Live.PaneId!, out var frame);
        var row = frame is null ? "" : VtCellUnpacker.RowText(frame, frame.Cursor.Row).TrimEnd();
        result.Notes.Add(
            $"{when}: mismatches {before.Mismatches}->{after.Mismatches} expired {before.Expired}->{after.Expired} "
            + $"mispredicted {before.MispredictedShown}->{after.MispredictedShown} cursor {frame?.Cursor.Col},{frame?.Cursor.Row} row '{row}'");
    }

    private static void CheckConverged(ServerTimeline timeline, GhosttyVtEngine host, int key, PredictedReplay result)
    {
        if (!timeline.OracleAfter.TryGetValue(key, out var want))
            return;
        if (DiffSnapshot(want, host.CaptureSnapshot()) is { } diff)
        {
            result.NonConvergedKeys++;
            result.Divergences.Add($"key #{key} {timeline.Keys[key - 1].Label}: {diff}");
        }
    }

    /// <summary>
    /// A host glyph is wrong when no real state between the newest acked key
    /// and the newest observed key has it: not the oracle after any of those
    /// keys. A stale echo is not wrong; a wrong prediction is. Each wrong
    /// glyph must go within one confirm.
    /// </summary>
    private static void TrackWrongGlyphs(
        ServerTimeline timeline,
        GhosttyVtEngine host,
        DateTimeOffset now,
        int observed,
        int acked,
        Dictionary<(int Col, int Row), DateTimeOffset> wrongSince,
        PredictedReplay result)
    {
        if (observed == 0)
            return;
        var got = host.CaptureSnapshot();
        var states = new List<VtStructuredSnapshot>();
        for (var k = Math.Max(0, Math.Min(acked, observed) - 1); k <= observed; k++)
        {
            if (timeline.OracleAfter.TryGetValue(k, out var state))
                states.Add(state);
        }

        var wrongNow = new HashSet<(int, int)>();
        for (var row = 0; row < got.Cells.Length; row++)
        {
            for (var col = 0; col < got.Cells[row].Length; col++)
            {
                var text = Glyph(got.Cells[row][col]);
                var real = false;
                foreach (var state in states)
                {
                    if (row < state.Cells.Length && col < state.Cells[row].Length && Glyph(state.Cells[row][col]) == text)
                    {
                        real = true;
                        break;
                    }
                }

                if (!real)
                    wrongNow.Add((col, row));
            }
        }

        foreach (var cell in wrongNow)
        {
            if (!wrongSince.ContainsKey(cell))
            {
                wrongSince[cell] = now;
                result.WrongGlyphs.Add($"cell {cell.Item1},{cell.Item2} '{Glyph(got.Cells[cell.Item2][cell.Item1])}' after key #{observed}");
            }
        }

        foreach (var cell in wrongSince.Keys.ToList())
        {
            if (wrongNow.Contains(cell))
                continue;
            result.MaxWrongGlyphMs = Math.Max(result.MaxWrongGlyphMs, (now - wrongSince[cell]).TotalMilliseconds);
            wrongSince.Remove(cell);
        }
    }

    private static string Glyph(VtCellSnapshot cell) => string.IsNullOrEmpty(cell.Text) ? " " : cell.Text;

    /// <summary>The diff of <see cref="Diff"/> on two snapshots.</summary>
    private static string? DiffSnapshot(VtStructuredSnapshot want, VtStructuredSnapshot got)
    {
        for (var row = 0; row < want.Cells.Length; row++)
        {
            var wantRow = want.Cells[row];
            var gotRow = row < got.Cells.Length ? got.Cells[row] : [];
            for (var col = 0; col < wantRow.Length; col++)
            {
                if (col >= gotRow.Length)
                    return $"row {row}: host row has {gotRow.Length} cells";
                if (CellDiff(wantRow[col], gotRow[col]) is { } cell)
                    return $"cell {col},{row} {cell}: want '{RowText(wantRow)}' got '{RowText(gotRow)}'";
            }
        }

        var wc = want.Cursor;
        var gc = got.Cursor;
        if (wc is null || gc is null)
            return wc is null && gc is null ? null : "cursor presence";
        if (wc.Col != gc.Col || wc.Row != gc.Row)
            return $"cursor want {wc.Col},{wc.Row} got {gc.Col},{gc.Row}";
        if (wc.Visible != gc.Visible)
            return $"cursor visible want {wc.Visible} got {gc.Visible}";
        if (wc.Visible && wc.Shape != gc.Shape)
            return $"cursor shape want {wc.Shape} got {gc.Shape}";
        return null;
    }

    private sealed record TimelineKey(int Index, string Label, byte[] Input, DateTimeOffset WrittenAt);

    private sealed class ServerTimeline(int cols, int rows, string paneId, DateTimeOffset start)
    {
        public int Cols { get; } = cols;

        public int Rows { get; } = rows;

        public string PaneId { get; } = paneId;

        public DateTimeOffset Start { get; } = start;

        public List<(DateTimeOffset PostedAt, TerminalRenderCellsPayload Body)> Bodies { get; } = [];

        public List<TimelineKey> Keys { get; } = [];

        public Dictionary<int, VtStructuredSnapshot> OracleAfter { get; } = [];
    }

    private sealed class PredictedReplay
    {
        public int Keys { get; set; }

        public int NonConvergedKeys { get; set; }

        public double MaxWrongGlyphMs { get; set; }

        public long PredictedPaints { get; set; }

        public EchoPredictorStats Stats { get; set; } = new();

        public List<string> Divergences { get; } = [];

        public List<string> WrongGlyphs { get; } = [];

        public HashSet<int> CaretMovedBeforeEcho { get; } = [];

        public List<string> Notes { get; } = [];

        public HashSet<int> ConfirmedByCursor { get; } = [];

        public Dictionary<int, EchoPredictorStats> KeyStats { get; } = [];
    }

    /// <summary>
    /// The attach client with the predictor of the attach session: a
    /// chrome-off live state over an in-memory TTY. A key goes through the
    /// same steps as the dispatch site and the input sender.
    /// </summary>
    private sealed class PredictingClient : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly UnixRawTerminal _tty;
        private long _read;

        public PredictingClient(string paneId, int cols, int rows, TimeProvider clock)
        {
            _tty = new UnixRawTerminal(_stream, cols, rows);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            var meter = new AttachLatencyMeter(clock);
            var predictor = new EchoPredictor(clock, EchoDisplayPolicies.For(PredictiveEchoMode.Adaptive), meter);
            meter.SeqObserver = predictor;
            Live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = null!,
                PaneId = paneId,
                Latency = meter,
                Prediction = predictor,
            };
            Live.Time = clock;
        }

        public AttachLiveState Live { get; }

        public void Type(TimelineKey key)
        {
            var meter = Live.Latency;
            meter.NoteStdinRead(key.Input, meter.Now());
            var mark = Live.Prediction.ReserveMark();
            AttachSession.ObservePaneInput(_tty, Live, key.Input, mark, paste: false);
            meter.NoteSent(Live.PaneId!, key.Index, [meter.StampKeys(key.Input) with { Mark = mark }]);
        }

        public void Apply(TerminalRenderCellsPayload body) =>
            AttachSession.TryWriteCells(_tty, Live, body, reanchorBeforePaint: true);

        public void Drain(GhosttyVtEngine host)
        {
            var length = _stream.Length;
            if (length > _read)
            {
                host.Feed(_stream.GetBuffer().AsSpan((int)_read, (int)(length - _read)));
                _read = length;
            }
        }

        public void Dispose() => _tty.Dispose();
    }

    /// <summary>
    /// A clock for the client side. The monotonic clock moves with the wall
    /// clock, and a timer runs on the thread that moves the clock.
    /// </summary>
    private sealed class VirtualClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<VirtualTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _now;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new VirtualTimer(this, callback, state);
            lock (_gate)
                _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void AdvanceTo(DateTimeOffset target)
        {
            while (true)
            {
                VirtualTimer? next = null;
                lock (_gate)
                {
                    foreach (var timer in _timers)
                    {
                        if (timer.Due is { } due && due <= target && (next?.Due is not { } best || due < best))
                            next = timer;
                    }

                    if (next is null)
                    {
                        if (target > _now)
                            _now = target;
                        return;
                    }

                    if (next.Due!.Value > _now)
                        _now = next.Due.Value;
                    next.Due = null;
                }

                next.Fire();
            }
        }

        private sealed class VirtualTimer(VirtualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan
                        ? null
                        : clock._now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (clock._gate)
                    Due = null;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
