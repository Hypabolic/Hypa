using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Persistence;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Replays recorded typing through the mux and the attach client. After each
/// key, the pane region of the host terminal must equal an oracle VT that read
/// the same bytes directly. The fixtures come from
/// <c>tools/TypingRecorder/record_typing.py</c>.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed partial class TypingConformanceTests(ITestOutputHelper output)
{
    private const string FixtureDirectory = "tests/Hypa.AgentRuntime.Tests/Fixtures/typing";

    private static readonly string[] FixtureNames =
    [
        "claude-code",
        "zsh-f",
        "zsh-p10k",
        "bash-norc",
        "vim-insert",
        "sync-echo-tui",
    ];

    private static readonly int[] SplitSeeds = [11, 23];

    /// <summary>A generated stream: a full shell pane, then each Enter prints lines and the pane scrolls.</summary>
    private const string ScrollOutputFixture = "scroll-output";

    /// <summary>
    /// A generated stream: a full shell pane, then keys that clear the
    /// screen and draw a short page, and keys that print more lines than the
    /// pane has rows.
    /// </summary>
    private const string ClearRedrawFixture = "clear-redraw";

    private readonly string? _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var name in FixtureNames)
            data.Add(name);
        return data;
    }

    public static TheoryData<string, int> SplitFixtures()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in FixtureNames)
        {
            foreach (var seed in SplitSeeds)
                data.Add(name, seed);
        }

        return data;
    }

    [SkippableTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Host_frame_matches_oracle_after_each_key(string fixture)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var result = await ReplayAsync(TypingFixture.Load(fixture), seed: null);
        Report(fixture, "recorded", result);
        Assert.True(result.NonConvergedKeys == 0, result.FirstDivergence);
        Assert.True(result.TornFrames == 0, $"{result.TornFrames} cells bodies posted inside an open DEC 2026 block");
        AssertDirtyRowPathRan(result);
        // A key whose echo fits in the echo budget converges at its last
        // read. A longer burst is rate capped by design.
        Assert.True(
            result.LateKeysWithinEchoBudget.Count == 0,
            string.Join("; ", result.LateKeysWithinEchoBudget));
    }

    [SkippableTheory]
    [MemberData(nameof(SplitFixtures))]
    public async Task Host_frame_matches_oracle_with_split_reads(string fixture, int seed)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var result = await ReplayAsync(TypingFixture.Load(fixture), seed);
        Report(fixture, "split seed " + seed, result);
        Assert.True(result.NonConvergedKeys == 0, result.FirstDivergence);
        Assert.True(result.TornFrames == 0, $"{result.TornFrames} cells bodies posted inside an open DEC 2026 block");
        AssertDirtyRowPathRan(result);
    }

    /// <summary>
    /// The attach client offers row shift in each hello. The same fixtures,
    /// recorded and split, through a connection that negotiated it: a delta
    /// can move the kept rows before its rows apply.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(RowShiftCases))]
    public async Task Host_frame_matches_oracle_with_row_shift(string fixture, int seed)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var loaded = fixture == ScrollOutputFixture ? TypingFixture.ScrollOutput() : TypingFixture.Load(fixture);
        var result = await ReplayAsync(loaded, seed == 0 ? null : seed, rowShift: true);
        Report(fixture, "row shift, " + (seed == 0 ? "recorded" : "split seed " + seed), result);
        output.WriteLine($"bodies with a shift {result.ShiftedBodies}");
        Assert.True(result.NonConvergedKeys == 0, result.FirstDivergence);
        Assert.True(result.TornFrames == 0, $"{result.TornFrames} cells bodies posted inside an open DEC 2026 block");
        if (fixture == ScrollOutputFixture)
            Assert.True(result.ShiftedBodies > 0, "no body of the scrolling output had a shift");
    }

    /// <summary>
    /// A clear of a full pane makes the mux send a full body with no
    /// reanchor, because it is smaller than the delta. The client paints that
    /// body through the patch path with all rows of the pane. The host must
    /// still equal the oracle after each key.
    /// </summary>
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(23)]
    public async Task Host_frame_matches_oracle_when_full_bodies_without_reanchor_paint_through_the_patch_path(int seed)
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        var result = await ReplayAsync(TypingFixture.ClearRedraw(), seed == 0 ? null : seed, rowShift: true);
        Report(ClearRedrawFixture, "row shift, " + (seed == 0 ? "recorded" : "split seed " + seed), result);
        output.WriteLine(
            $"full bodies without reanchor {result.FullBodiesWithoutReanchor}, reanchor bodies {result.ReanchorBodies}, "
            + $"client patches {result.ClientPatches}, client full_frame fallbacks {result.ClientFullFrameFallbacks}");
        Assert.True(result.NonConvergedKeys == 0, result.FirstDivergence);
        Assert.True(result.TornFrames == 0, $"{result.TornFrames} cells bodies posted inside an open DEC 2026 block");
        Assert.True(result.FullBodiesWithoutReanchor > 0, "no body of the stream was a full body without reanchor");
        Assert.True(result.ClientPatches > 0, "no paint took the patch path");
        // Only a reanchor body, or the first body of the pane, is a full
        // compose for its body kind.
        Assert.True(
            result.ClientFullFrameFallbacks <= result.ReanchorBodies,
            $"{result.ClientFullFrameFallbacks} full_frame fallbacks for {result.ReanchorBodies} reanchor bodies");
    }

    /// <summary>
    /// The recorded fixtures do not scroll. The scroll output stream does, so
    /// it runs with each split seed too.
    /// </summary>
    public static TheoryData<string, int> RowShiftCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in FixtureNames)
            data.Add(name, 0);
        data.Add(ScrollOutputFixture, 0);
        foreach (var seed in SplitSeeds)
            data.Add(ScrollOutputFixture, seed);
        return data;
    }

    /// <summary>
    /// The live paints of the replay must use the dirty-row stamp, and no
    /// check frame may find a stale row.
    /// </summary>
    private static void AssertDirtyRowPathRan(ReplayResult result)
    {
        Assert.True(result.DirtyRowStamps > 0, "no live paint used the dirty-row stamp");
        Assert.True(result.VerifyMismatchRows == 0, $"{result.VerifyMismatchRows} stale rows found by check frames");
    }

    private void Report(string fixture, string mode, ReplayResult result)
    {
        output.WriteLine(
            "fixture | mode | keys | non_converged_keys | torn_frames | extra_frames_per_key | "
            + "recorded_ms_to_converge (max) | ms_after_last_read (max) | late_keys | ms_after_last_read (sum)");
        output.WriteLine(
            $"{fixture} | {mode} | {result.Keys} | {result.NonConvergedKeys} | {result.TornFrames} | "
            + $"{result.ExtraFramesPerKey:0.00} | {result.MaxMsToConverge:0.0} | {result.MaxMsAfterLastRead:0.0} | "
            + $"{result.LateKeys.Count} | {result.SumMsAfterLastRead:0.0}");
        output.WriteLine($"dirty-row stamps {result.DirtyRowStamps}, check-frame stale rows {result.VerifyMismatchRows}");
        foreach (var line in result.Divergences.Take(8))
            output.WriteLine("  " + line);
        foreach (var line in result.LateKeys.Take(8))
            output.WriteLine("  late: " + line);
    }

    /// <summary>
    /// Replay one fixture on virtual time. A key that the recorder wrote gives
    /// the mux the input admission signal. Before each read, the clock moves by
    /// the recorded gap, clamped to 0-20 ms, so the real coalescer decides when
    /// to paint. With a seed, each read is split at seeded byte offsets with a
    /// seeded 0-8 ms gap between the parts. At the end of each key the clock
    /// moves past two render ticks in 1 ms steps, so a pending paint posts.
    /// Then the host pane region must equal the oracle.
    /// </summary>
    private async Task<ReplayResult> ReplayAsync(TypingFixture fixture, int? seed, bool rowShift = false)
    {
        await using var server = await ServerSession.OpenAsync(_lib, fixture.Cols, fixture.Rows, rowShift);
        using var oracle = new GhosttyVtEngine(fixture.Cols, fixture.Rows, libraryPathOverride: _lib);
        using var host = new GhosttyVtEngine(fixture.Cols, fixture.Rows, libraryPathOverride: _lib);
        using var client = new ClientSide(server.PaneId, fixture.Cols, fixture.Rows);
        var random = seed is { } s ? new Random(s) : null;
        var result = new ReplayResult();

        // Apply the observe frame and the resize frame before the first byte.
        await server.SettleAsync(TimeSpan.FromMilliseconds(100));
        client.Apply(server.TakeCells(), host);

        foreach (var key in fixture.Keys)
        {
            var keyStart = server.Now;
            var lastReadAt = keyStart;
            var reads = 0;
            DateTimeOffset? convergedAt = null;
            var framesForKey = 0;
            await server.SettleAsync(TimeSpan.FromMilliseconds(100));
            client.Apply(server.TakeCells(), host);
            keyStart = server.Now;
            lastReadAt = keyStart;
            // The recorder wrote the key here. Give the mux the signal that
            // input admission gives it. Do not write to the /bin/cat pane: it
            // echoes the key and the oracle would not see that echo.
            if (key.HasInput)
                server.Cp.MarkInputAdmittedForTests(server.PaneId);
            var previousMs = 0.0;
            foreach (var chunk in key.Chunks)
            {
                var gap = TimeSpan.FromMilliseconds(Math.Clamp(chunk.Ms - previousMs, 0, 20));
                previousMs = chunk.Ms;
                var first = true;
                foreach (var part in Split(chunk.Bytes, random))
                {
                    if (!first)
                        gap = TimeSpan.FromMilliseconds(random!.Next(0, 9));
                    first = false;
                    await server.SettleAsync(gap);
                    framesForKey += Apply(server, client, host, oracle, result);
                    oracle.Feed(part);
                    server.Runtime.PublishFedOutputForTests(part);
                    await server.DeliveredAsync();
                    lastReadAt = server.Now;
                    reads++;
                    framesForKey += Apply(server, client, host, oracle, result);
                    TrackConvergence(oracle, host, server.Now, ref convergedAt);
                }
            }

            // Two render ticks in 1 ms steps: a paint that the coalescer
            // holds now posts, and the step shows when it posted.
            for (var step = 0; step < 40; step++)
            {
                await server.SettleAsync(TimeSpan.FromMilliseconds(1));
                framesForKey += Apply(server, client, host, oracle, result);
                TrackConvergence(oracle, host, server.Now, ref convergedAt);
            }

            var diff = Diff(oracle, host);
            if (key.IsStartup)
            {
                if (diff is not null)
                {
                    result.NonConvergedKeys++;
                    result.Divergences.Add($"startup: {diff}");
                }

                continue;
            }

            result.Keys++;
            if (framesForKey > 1)
                result.ExtraFrames += framesForKey - 1;
            if (diff is not null)
            {
                result.NonConvergedKeys++;
                result.Divergences.Add($"key #{result.Keys} {key.Label}: {diff}");
            }
            else if (convergedAt is { } at)
            {
                result.MaxMsToConverge = Math.Max(result.MaxMsToConverge, (at - keyStart).TotalMilliseconds);
                var afterLastRead = Math.Max(0, (at - lastReadAt).TotalMilliseconds);
                result.MaxMsAfterLastRead = Math.Max(result.MaxMsAfterLastRead, afterLastRead);
                result.SumMsAfterLastRead += afterLastRead;
                if (afterLastRead > 0)
                {
                    var late = $"key #{result.Keys} {key.Label}: {afterLastRead:0.0} ms after the last read ({reads} reads)";
                    result.LateKeys.Add(late);
                    if (reads <= PaneRenderCoalescer.EchoFlushCap)
                        result.LateKeysWithinEchoBudget.Add(late);
                }
            }
        }

        var counters = Assert.IsType<LiveStampCounters>(server.Runtime.LiveStampMeter);
        result.DirtyRowStamps = counters.DirtyRowStamps;
        result.VerifyMismatchRows = counters.VerifyMismatchRows;
        result.FullBodiesWithoutReanchor = client.FullBodiesWithoutReanchor;
        result.ReanchorBodies = client.ReanchorBodies;
        result.ClientPatches = client.Paint.Patches;
        result.ClientFullFrameFallbacks = client.Paint.Fallbacks(HostPatchFallback.FullFrame);
        return result;
    }

    private static int Apply(
        ServerSession server,
        ClientSide client,
        GhosttyVtEngine host,
        GhosttyVtEngine oracle,
        ReplayResult result)
    {
        var bodies = server.TakeCells();
        foreach (var body in bodies)
        {
            if (body.Sync && oracle.IsSynchronizedOutputActive)
                result.TornFrames++;
            if (body.Shift != 0)
                result.ShiftedBodies++;
        }

        client.Apply(bodies, host);
        return bodies.Count;
    }

    /// <summary>The virtual time after which the host stays equal to the oracle.</summary>
    private static void TrackConvergence(
        GhosttyVtEngine oracle,
        GhosttyVtEngine host,
        DateTimeOffset now,
        ref DateTimeOffset? convergedAt)
    {
        if (Diff(oracle, host) is not null)
            convergedAt = null;
        else
            convergedAt ??= now;
    }

    /// <summary>
    /// Split one recorded read at seeded random byte offsets. A null random
    /// keeps the read whole.
    /// </summary>
    private static IEnumerable<byte[]> Split(byte[] bytes, Random? random)
    {
        if (random is null || bytes.Length < 2)
        {
            yield return bytes;
            yield break;
        }

        var start = 0;
        while (start < bytes.Length)
        {
            var take = Math.Min(bytes.Length - start, 1 + random.Next(Math.Max(1, bytes.Length / 2)));
            yield return bytes[start..(start + take)];
            start += take;
        }
    }

    /// <summary>
    /// The first difference between the oracle viewport and the host pane
    /// region: a cell (text, width, continuation, style), or the cursor
    /// column, row, visibility or shape. Null when they are equal.
    /// </summary>
    private static string? Diff(GhosttyVtEngine oracle, GhosttyVtEngine host)
    {
        var want = oracle.CaptureSnapshot();
        var got = host.CaptureSnapshot();
        for (var row = 0; row < want.Cells.Length; row++)
        {
            var wantRow = want.Cells[row];
            var gotRow = row < got.Cells.Length ? got.Cells[row] : [];
            for (var col = 0; col < wantRow.Length; col++)
            {
                if (col >= gotRow.Length)
                    return $"row {row}: host row has {gotRow.Length} cells, want {wantRow.Length} ('{RowText(wantRow)}')";
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

    private static string? CellDiff(VtCellSnapshot want, VtCellSnapshot got)
    {
        var wantText = string.IsNullOrEmpty(want.Text) ? " " : want.Text;
        var gotText = string.IsNullOrEmpty(got.Text) ? " " : got.Text;
        if (!string.Equals(wantText, gotText, StringComparison.Ordinal))
            return $"text '{wantText}' vs '{gotText}'";
        if (want.Width != got.Width || want.IsContinuation != got.IsContinuation)
            return $"width {want.Width}/{want.IsContinuation} vs {got.Width}/{got.IsContinuation}";
        if (want.Style != got.Style)
            return $"style {want.Style} vs {got.Style}";
        return null;
    }

    private static string RowText(VtCellSnapshot[] row)
    {
        var text = new StringBuilder(row.Length);
        foreach (var cell in row)
        {
            if (cell.IsContinuation)
                continue;
            text.Append(string.IsNullOrEmpty(cell.Text) ? " " : cell.Text);
        }

        return text.ToString().TrimEnd();
    }

    private sealed class ReplayResult
    {
        public int Keys { get; set; }

        public int NonConvergedKeys { get; set; }

        public int TornFrames { get; set; }

        public int ShiftedBodies { get; set; }

        public int FullBodiesWithoutReanchor { get; set; }

        public int ReanchorBodies { get; set; }

        public long ClientPatches { get; set; }

        public long ClientFullFrameFallbacks { get; set; }

        public int Frames { get; set; }

        public int ExtraFrames { get; set; }

        public double MaxMsToConverge { get; set; }

        /// <summary>
        /// The largest virtual time from the last read of a key to the time
        /// after which the host stays equal to the oracle. The coalescer adds
        /// this delay. It is 0 when the paint of the last read posts at once.
        /// </summary>
        public double MaxMsAfterLastRead { get; set; }

        public double SumMsAfterLastRead { get; set; }

        public long DirtyRowStamps { get; set; }

        public long VerifyMismatchRows { get; set; }

        public List<string> Divergences { get; } = [];

        public List<string> LateKeys { get; } = [];

        public List<string> LateKeysWithinEchoBudget { get; } = [];

        public double ExtraFramesPerKey => Keys == 0 ? 0 : (double)ExtraFrames / Keys;

        public string FirstDivergence => Divergences.FirstOrDefault() ?? "";
    }

    private sealed record TypingChunk(byte[] Bytes, double Ms);

    private sealed record TypingKey(string Label, IReadOnlyList<TypingChunk> Chunks, bool HasInput)
    {
        /// <summary>The bytes that the recorder wrote for this key. Empty for the startup.</summary>
        public byte[] Input { get; init; } = [];

        public bool IsStartup => Label == "STARTUP";
    }

    private sealed record TypingFixture(int Cols, int Rows, IReadOnlyList<TypingKey> Keys)
    {
        /// <summary>
        /// 120x40. The startup fills the pane. Then each key is an Enter or a
        /// typed glyph. An Enter prints one to three lines in one or two
        /// reads and a new prompt, so the pane scrolls by one to four rows.
        /// </summary>
        public static TypingFixture ScrollOutput()
        {
            var keys = new List<TypingKey>();
            var startup = new StringBuilder();
            for (var i = 1; i <= 40; i++)
            {
                if (i > 1)
                    startup.Append("\r\n");
                startup.Append("history line ").Append(i).Append(" of the scroll output stream");
            }

            startup.Append("\r\n$ ");
            keys.Add(new TypingKey("STARTUP", [Chunk(startup.ToString(), 1)], HasInput: false));
            var next = 1;
            for (var key = 0; key < 30; key++)
            {
                if (key % 3 == 2)
                {
                    keys.Add(new TypingKey("x", [Chunk("x", 0.1)], HasInput: true) { Input = "x"u8.ToArray() });
                    continue;
                }

                var lines = 1 + (key % 3) + (key % 2);
                var chunks = new List<TypingChunk>();
                var first = new StringBuilder("\r\n");
                var second = new StringBuilder();
                for (var line = 0; line < lines; line++)
                {
                    var target = line == 0 || lines == 1 ? first : second;
                    target.Append("output ").Append(next++).Append(" \u001b[32mok\u001b[0m\r\n");
                }

                second.Append("$ ");
                chunks.Add(Chunk(first.ToString(), 0.2));
                chunks.Add(Chunk(second.ToString(), 1.5));
                keys.Add(new TypingKey("ENTER", chunks, HasInput: true) { Input = "\r"u8.ToArray() });
            }

            return new TypingFixture(120, 40, keys);
        }

        /// <summary>
        /// 120x40. The startup fills the pane. Then each key is a clear, a
        /// fill or a typed glyph. A clear moves to the top left, erases the
        /// screen and draws a page of two to four short lines and a prompt.
        /// A fill redraws each row in place with random text, long and short
        /// in turn, so every row of the pane changes and each long row
        /// becomes short.
        /// </summary>
        public static TypingFixture ClearRedraw()
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            var random = new Random(5);
            var keys = new List<TypingKey>();
            var startup = new StringBuilder();
            for (var i = 1; i <= 40; i++)
            {
                if (i > 1)
                    startup.Append("\r\n");
                startup.Append("history line ").Append(i).Append(" of the clear and redraw stream");
            }

            startup.Append("\r\n$ ");
            keys.Add(new TypingKey("STARTUP", [Chunk(startup.ToString(), 1)], HasInput: false));
            var page = 0;
            var fills = 0;
            for (var key = 0; key < 24; key++)
            {
                if (key % 4 == 3)
                {
                    keys.Add(new TypingKey("x", [Chunk("x", 0.1)], HasInput: true) { Input = "x"u8.ToArray() });
                    continue;
                }

                var text = new StringBuilder();
                if (key % 4 == 0)
                {
                    page++;
                    text.Append("\r\n\u001b[H\u001b[2J");
                    var lines = 2 + (page % 3);
                    for (var line = 0; line < lines; line++)
                        text.Append("page ").Append(page).Append(" row ").Append(line).Append(" \u001b[33mstatus\u001b[0m\r\n");
                }
                else
                {
                    // Redraw each row in place. A long row becomes short and
                    // a short row becomes long at each fill.
                    fills++;
                    text.Append("\u001b[H");
                    for (var row = 0; row < 39; row++)
                    {
                        text.Append("\u001b[2K");
                        var length = (row + fills) % 2 == 0 ? 100 : 10;
                        for (var c = 0; c < length; c++)
                            text.Append(alphabet[random.Next(alphabet.Length)]);
                        text.Append("\r\n");
                    }

                    text.Append("\u001b[2K");
                }

                text.Append("$ ");
                keys.Add(new TypingKey("ENTER", [Chunk(text.ToString(), 0.3)], HasInput: true) { Input = "\r"u8.ToArray() });
            }

            return new TypingFixture(120, 40, keys);
        }

        private static TypingChunk Chunk(string text, double ms) => new(Encoding.UTF8.GetBytes(text), ms);

        public static TypingFixture Load(string name)
        {
            var path = Path.Combine(VtRecordingReplay.FindRepoRoot(), FixtureDirectory, name + ".jsonl");
            var cols = 120;
            var rows = 40;
            var keys = new List<TypingKey>();
            List<TypingChunk>? current = null;
            string? label = null;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("meta", out var meta))
                {
                    cols = meta.GetProperty("cols").GetInt32();
                    rows = meta.GetProperty("rows").GetInt32();
                    continue;
                }

                var key = root.GetProperty("key").GetString()!;
                // A line with "input" starts a new key write. Later lines with
                // no input are more reads for the same key.
                var hasInput = root.TryGetProperty("input", out var input);
                if (current is null || hasInput || key != label)
                {
                    current = [];
                    label = key;
                    keys.Add(new TypingKey(key, current, hasInput)
                    {
                        Input = hasInput && input.ValueKind == JsonValueKind.String
                            ? Convert.FromBase64String(input.GetString()!)
                            : [],
                    });
                }

                var bytes = Convert.FromBase64String(root.GetProperty("chunk").GetString()!);
                if (bytes.Length > 0)
                    current.Add(new TypingChunk(bytes, Math.Clamp(root.GetProperty("t_ms").GetDouble(), 0, 1000)));
            }

            return new TypingFixture(cols, rows, keys);
        }
    }

    /// <summary>
    /// The attach client side: a chrome-off live state that composes the
    /// focused pane at the host origin, over an in-memory TTY.
    /// </summary>
    private sealed class ClientSide : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly UnixRawTerminal _tty;
        private readonly AttachLiveState _live;
        private long _read;

        public ClientSide(string paneId, int cols, int rows)
        {
            _tty = new UnixRawTerminal(_stream, cols, rows);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            _live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = null!,
                PaneId = paneId,
            };
        }

        public void Apply(IReadOnlyList<TerminalRenderCellsPayload> bodies, GhosttyVtEngine host)
        {
            foreach (var body in bodies)
            {
                if (body.Reanchor)
                    ReanchorBodies++;
                else if (body.Full)
                    FullBodiesWithoutReanchor++;
                AttachSession.TryWriteCells(_tty, _live, body, reanchorBeforePaint: true);
            }
            var length = _stream.Length;
            if (length > _read)
            {
                host.Feed(_stream.GetBuffer().AsSpan((int)_read, (int)(length - _read)));
                _read = length;
            }
        }

        /// <summary>The applied bodies with <c>full</c> true and <c>reanchor</c> false.</summary>
        public int FullBodiesWithoutReanchor { get; private set; }

        /// <summary>The applied bodies with <c>reanchor</c> true.</summary>
        public int ReanchorBodies { get; private set; }

        /// <summary>The paint counters of the attach client.</summary>
        public HostPaintCounters Paint => _live.Latency.Paint;

        public void Dispose() => _tty.Dispose();
    }

    /// <summary>
    /// The mux side: a live <see cref="ControlPlaneService"/>, a /bin/cat pane,
    /// and one live observer. The clock never moves, so a paint runs only in
    /// <see cref="PaintAsync"/>.
    /// </summary>
    private sealed class ServerSession : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly RuntimeStatePaths _paths;
        private readonly CellsSink _sink = new("typing-conformance");
        private int _mark;

        private readonly ManualTimeProvider _time;

        private ServerSession(string dir, RuntimeStatePaths paths, ControlPlaneService cp, ManualTimeProvider time)
        {
            _dir = dir;
            _paths = paths;
            _time = time;
            Cp = cp;
        }

        public ControlPlaneService Cp { get; }

        public PaneRuntime Runtime { get; private set; } = null!;

        public string PaneId { get; private set; } = "";

        /// <summary>
        /// With <paramref name="rowShift"/>, the observer connection first
        /// sends an attach hello that offers row shift and turns its surface on.
        /// </summary>
        public static async Task<ServerSession> OpenAsync(string? lib, int cols, int rows, bool rowShift = false)
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-typing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var paths = new RuntimeStatePaths { StateDirectory = dir };
            try
            {
                return await OpenInAsync(dir, paths, lib, cols, rows, rowShift);
            }
            catch
            {
                SqliteTestCleanup.ReleaseAndDelete(dir, paths.DatabasePath);
                throw;
            }
        }

        private static async Task<ServerSession> OpenInAsync(
            string dir,
            RuntimeStatePaths paths,
            string? lib,
            int cols,
            int rows,
            bool rowShift)
        {
            Assert.True((await new SqliteRuntimeSchemaMigrator(paths).MigrateAsync()).IsOk);
            var state = new AppState(SessionId.New("typing"));
            state.UpdateSession(s => s with { Name = "typing", LifecycleState = SessionLifecycle.Ready });
            state.CreateWorkspace(dir, label: "default");
            var store = new SqliteRuntimeSessionStore(paths);
            Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
            var journal = new FileRuntimeEventJournal(paths, new SqliteJournalManifestStore(paths), state.SessionId.Value);
            Assert.True((await journal.RecoverAsync()).IsOk);
            var factory = new PaneRuntimeFactory(
                new PaneIntelligencePipeline(),
                ptyFactory: TestPaneFactories.ProcessIo(),
                vtEngineFactory: new VtEngineFactory(new VtProviderSelection { LibraryPathOverride = lib }));
            var time = new ManualTimeProvider();
            var cp = new ControlPlaneService(
                state,
                factory,
                new PaneIntelligencePipeline(),
                new HeuristicAgentDetector(),
                store: store,
                journal: journal,
                subscriptions: new EventSubscriptionHub(),
                timeProvider: time);
            var session = new ServerSession(dir, paths, cp, time);
            try
            {
                if (rowShift)
                    await session.HelloAsync(cols, rows);
                var sub = await cp.DispatchAsync(
                    ProtocolMethods.EventsSubscribe,
                    Json(new JsonObject
                    {
                        ["from_seq"] = 0,
                        ["types"] = new JsonArray("output", "render"),
                        ["live"] = true,
                    }),
                    session._sink,
                    CancellationToken.None);
                var subId = sub.GetProperty("subscription_id").GetString()!;
                await cp.CompleteEventsSubscribeAsync(subId, [], session._sink, CancellationToken.None);
                var create = await cp.DispatchAsync(
                    ProtocolMethods.PaneCreate,
                    Json(new JsonObject { ["command"] = "/bin/cat", ["cwd"] = dir }),
                    CancellationToken.None);
                session.PaneId = create.GetProperty("pane_id").GetString()!;
                session.Runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneId));
                await session.Runtime.ResizeAsync(cols, rows, CancellationToken.None);
                await cp.DispatchAsync(
                    ProtocolMethods.TerminalVisibleSet,
                    Json(new JsonObject
                    {
                        ["subscription_id"] = subId,
                        ["pane_ids"] = new JsonArray(session.PaneId),
                    }),
                    session._sink,
                    CancellationToken.None);
                await cp.DispatchAsync(
                    ProtocolMethods.TerminalObserve,
                    Json(new JsonObject { ["pane_id"] = session.PaneId, ["subscription_id"] = subId }),
                    session._sink,
                    CancellationToken.None);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        public DateTimeOffset Now => _time.GetUtcNow();

        private async Task HelloAsync(int cols, int rows)
        {
            var clientId = _sink.ConnectionId;
            var hello = new AttachEndpointHello
            {
                EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
                ProtocolMajor = (uint)ProtocolVersion.Major,
                ProtocolMinor = (uint)ProtocolVersion.Minor,
                ClientId = clientId,
                Geometry = new AttachGeometry
                {
                    Columns = (ushort)cols,
                    Rows = (ushort)rows,
                    CellWidthPx = 8,
                    CellHeightPx = 16,
                    GeometryRevision = 1,
                },
                SurfaceActive = false,
                SnapshotCodecs = [AttachEndpointProtocol.SnapshotCodec],
                SurfaceCodecs = [AttachEndpointProtocol.SurfaceCodec],
                InputCodecs = [AttachEndpointProtocol.InputCodec],
                RequiredCapabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
                OptionalCapabilities = [AttachEndpointProtocol.RowShiftCapability],
            };
            await Cp.HandleAttachHelloAsync(hello, _sink, CancellationToken.None);
            await Cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "on",
                    ClientId = clientId,
                    Active = true,
                    GeometryRevision = 1,
                },
                _sink,
                CancellationToken.None);
            Assert.True(Cp.RowShiftOn(_sink.ConnectionId));
        }

        /// <summary>
        /// Move the clock. A coalescer tick that falls inside the gap runs and
        /// its paint is delivered before this returns.
        /// </summary>
        public async Task SettleAsync(TimeSpan gap)
        {
            // A zero move still runs a timer that is due now.
            _time.Advance(gap > TimeSpan.Zero ? gap : TimeSpan.Zero);
            await Poll.GuardAsync(Cp.CoalescerTickFlushedForTestsAsync(), "coalescer tick");
            await DeliveredAsync();
        }

        /// <summary>Wait until the output emit and any paint it started are delivered.</summary>
        public async Task DeliveredAsync()
        {
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
            await Poll.GuardAsync(Cp.CoalescerTickFlushedForTestsAsync(), "coalescer tick");
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
        }

        /// <summary>The cells bodies posted since the last call, in order.</summary>
        public List<TerminalRenderCellsPayload> TakeCells()
        {
            var lines = _sink.Lines;
            var bodies = new List<TerminalRenderCellsPayload>();
            for (; _mark < lines.Count; _mark++)
            {
                using var doc = JsonDocument.Parse(lines[_mark]);
                if (!doc.RootElement.TryGetProperty("params", out var parms)
                    || !parms.TryGetProperty("type", out var type)
                    || type.GetString() != ProtocolEventTypes.TerminalRender
                    || !parms.TryGetProperty("payload", out var payload)
                    || !payload.TryGetProperty("kind", out var kind)
                    || kind.GetString() != TerminalRenderCellsPayload.KindCells)
                    continue;
                bodies.Add(payload.Deserialize(ProtocolJsonContext.Default.TerminalRenderCellsPayload)!);
            }

            return bodies;
        }

        public async ValueTask DisposeAsync()
        {
            await Cp.ShutdownAsync(CancellationToken.None);
            SqliteTestCleanup.ReleaseAndDelete(_dir, _paths.DatabasePath);
        }

        private static JsonElement Json(JsonObject obj) =>
            JsonDocument.Parse(obj.ToJsonString()).RootElement;
    }

    private sealed class CellsSink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId { get; } = id;

        public bool UsesWriterLanes => true;

        public int MaxLineBytes { get; set; } = 4 * 1_048_576;

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            Add(jsonLine);
            return Task.CompletedTask;
        }

        public WriterLaneResult TryEnqueueRender(string jsonLine) => Add(jsonLine);

        public WriterLaneResult TryEnqueueRender(byte[] utf8Json) => Add(Encoding.UTF8.GetString(utf8Json));

        public WriterLaneResult EnqueueOrderedRender(string jsonLine) => Add(jsonLine);

        public WriterLaneResult EnqueueOrderedRender(byte[] utf8Json) => Add(Encoding.UTF8.GetString(utf8Json));

        private WriterLaneResult Add(string json)
        {
            lock (_gate)
                _lines.Add(json);
            return WriterLaneResult.Ok;
        }
    }
}
