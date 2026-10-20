using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Hypa.Cli.Attach.Latency;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Infrastructure;
using Hypa.Continuity.Tests.Impairment;
using Hypa.Continuity.Tests.Support;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.Continuity.Tests.RemoteTyping;

/// <summary>One cell of the remote typing matrix.</summary>
internal sealed record RemoteTypingCell(string Transport, string Load, ImpairmentProfile Profile)
{
    public const string Idle = "idle";
    public const string Bulk = "bulk";

    public string Id =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Transport}-rtt{Profile.Rtt.TotalMilliseconds:0}-loss{Profile.LossRate * 100:0.#}-{Load}")
        + (Profile.BandwidthBytesPerSecond > 0
            ? string.Create(CultureInfo.InvariantCulture, $"-cap{Profile.BandwidthBytesPerSecond * 8 / 1_000_000}")
            : "");
}

internal sealed record RemoteTypingOptions
{
    /// <summary>The time between two key writes, as in the live baseline.</summary>
    public TimeSpan KeyInterval { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>The wait after the first frame of a typing pane, for its startup paint.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>The longest wait for the open keys after the last key write.</summary>
    public TimeSpan CloseWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The longest wait for the host frame to equal the oracle.</summary>
    public TimeSpan ConvergeWait { get; init; } = TimeSpan.FromSeconds(3);

    public IReadOnlyList<string> Fixtures { get; init; } = TypingScript.AllFixtures;

    /// <summary>The longest wait for the first frame of a typing pane.</summary>
    public TimeSpan FirstFrameWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// True when a typing pane that gets no first frame is a result, not a
    /// test failure. The bandwidth cap cells use it: a cap below the render
    /// rate can starve the pane, and that is the measurement.
    /// </summary>
    public bool ReportOnly { get; init; }

    /// <summary>
    /// The fixtures from <c>HYPA_REMOTE_TYPING_FIXTURES</c> (a comma list),
    /// or all fixtures of the typing conformance harness.
    /// </summary>
    public static RemoteTypingOptions FromEnvironment()
    {
        var list = Environment.GetEnvironmentVariable("HYPA_REMOTE_TYPING_FIXTURES");
        if (string.IsNullOrWhiteSpace(list))
            return new RemoteTypingOptions();
        return new RemoteTypingOptions
        {
            Fixtures = [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        };
    }
}

/// <summary>
/// One key of a fixture: the time from the client stdin read to the host write
/// of its echo. <see cref="KeyCount"/> is the meter count of keys in the read
/// (more than one for a burst of text). <see cref="Seq"/> is the seq of the
/// batch that carried it, when the key closed.
/// </summary>
internal sealed record RemoteTypingKey(
    string Fixture,
    int Index,
    string Label,
    int KeyCount,
    long? Seq,
    double? KeyToPaintMs);

internal sealed record RemoteTypingFixtureResult(string Fixture, int Keys, int Closed, string? Divergence);

internal sealed record RemoteTypingCellResult(
    RemoteTypingCell Cell,
    IReadOnlyList<double> IdlePingMs,
    IReadOnlyList<double> LoadPingMs,
    IReadOnlyList<RemoteTypingKey> Keys,
    IReadOnlyList<RemoteTypingFixtureResult> Fixtures,
    AttachLatencyReport Meter,
    long Reanchors,
    long RejectedFrames,
    long CellsBodies,
    ImpairmentCounters Up,
    ImpairmentCounters Down,
    long BulkAppBytes,
    double Seconds)
{
    /// <summary>Proxy bytes to the client during the typing windows of the fixtures.</summary>
    public long TypingWindowDownBytes { get; init; }

    /// <summary>The sum of the typing windows, in seconds.</summary>
    public double TypingWindowSeconds { get; init; }

    /// <summary>The render rate to the client while keys were typed: bulk and echo render.</summary>
    public double RenderBytesPerSecond => TypingWindowDownBytes / Math.Max(TypingWindowSeconds, 1e-3);

    /// <summary>Keys whose bytes the scripted pane read did not equal the recorded input.</summary>
    public long InputMismatches { get; init; }

    /// <summary>The fault that stopped the client event loop, or null.</summary>
    public string? ClientFault { get; init; }

    /// <summary>True when the connection used render credit.</summary>
    public bool RenderCredit { get; init; }

    /// <summary>The <c>render.ack</c> notifications that the client wrote.</summary>
    public long RenderAcks { get; init; }

    /// <summary>
    /// Percentiles over keys, as the meter counts them: a read with several
    /// keys gives one sample for each key.
    /// </summary>
    public LatencyStats KeyToPaint =>
        LatencyStats.From([.. Keys
            .Where(k => k.KeyToPaintMs is not null)
            .SelectMany(k => Enumerable.Repeat(k.KeyToPaintMs!.Value, Math.Max(1, k.KeyCount)))]);

    public int OpenKeys => Keys.Count(k => k.KeyToPaintMs is null);

    public IEnumerable<RemoteTypingFixtureResult> NonConverged => Fixtures.Where(f => f.Divergence is not null);

    public string Summary =>
        string.Join(
            ' ',
            string.Create(CultureInfo.InvariantCulture, $"remote-typing {Cell.Id} link=[{Cell.Profile.Describe()}]"),
            string.Create(CultureInfo.InvariantCulture, $"keys={Keys.Count} open={OpenKeys} key_to_paint[{KeyToPaint}]"),
            string.Create(CultureInfo.InvariantCulture, $"ping_idle[{LatencyStats.From([.. IdlePingMs])}]"),
            string.Create(CultureInfo.InvariantCulture, $"ping_load[{LatencyStats.From([.. LoadPingMs])}]"),
            string.Create(CultureInfo.InvariantCulture, $"stale={Meter.StaleKey} reanchors={Reanchors} rejected={RejectedFrames} render_credit={RenderCredit} render_acks={RenderAcks}"),
            string.Create(CultureInfo.InvariantCulture, $"render_rate={RenderBytesPerSecond / 1_000_000:0.000}MB/s bulk_app_rate={BulkAppBytes / Math.Max(Seconds, 1e-3) / 1_000_000:0.00}MB/s"),
            string.Create(CultureInfo.InvariantCulture, $"lost={Up.Lost + Down.Lost} queue_drops={Up.QueueDrops + Down.QueueDrops} tcp_stalls={Up.Stalls + Down.Stalls}"),
            string.Create(CultureInfo.InvariantCulture, $"non_converged={NonConverged.Count()} input_mismatches={InputMismatches}"));
}

/// <summary>
/// Runs one cell: a live mux in this process, the real accept stack, the
/// impairment proxy between the client and the accept, and the attach
/// client pieces. The typing fixtures run one after another, each in its
/// own pane. With the bulk load, a second visible pane writes with no pause
/// during the whole cell.
/// </summary>
internal static class RemoteTypingBench
{
    public static async Task<RemoteTypingCellResult> RunAsync(
        RemoteTypingCell cell,
        X509Certificate2 certificate,
        string ghosttyLibrary,
        RemoteTypingOptions options)
    {
        var quic = string.Equals(cell.Transport, EchoUnderBulkProbe.Quic, StringComparison.Ordinal);
        if (quic)
        {
            // The skip text starts with a fixed reason that the CI gate reads.
            // The probe detail shows which check failed. The probe can stop
            // before it looks at the library, so the text also has a library
            // check of its own.
            var report = QuicTransportRuntime.Probe();
            Skip.IfNot(
                report.IsSupported,
                "QUIC runtime is not usable on this host: "
                + (report.Detail ?? report.Reason ?? "no detail")
                + "; " + AppLocalMsQuicState());
        }
        var started = Stopwatch.GetTimestamp();
        await using var mux = await RemoteTypingMux.StartAsync(ghosttyLibrary);
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_rtyping", "rtyp0001");
        await using var stack = await AcceptStack.StartForMuxSocketAsync(
            mux.SocketPath,
            muxBoot,
            certificate,
            bindHost: IPAddress.Any.ToString(),
            trustGate: new LocalSelfHostedJoinTrustGate());
        if (quic)
            Skip.IfNot(stack.Accept.QuicListening, "the accept has no QUIC listener: " + stack.Accept.QuicStartupDetail);
        await using var proxy = ImpairmentProxy.Start(stack.Accept.Port, cell.Profile);
        var endpoint = stack.Accept.DialEndpoint with
        {
            Host = IPAddress.Loopback.ToString(),
            Port = proxy.Port,
            QuicListening = quic,
        };
        IBytePath bytePath = quic ? new QuicBytePath(certificate) : new TcpTlsBytePath(certificate);
        var connected = await OutboundFramedSession.ConnectDirectAsync(
                endpoint,
                clientBoot,
                tlsTrust: certificate,
                bytePath: bytePath)
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(connected.Ok, connected.Detail);
        await using var session = connected.Value!;
        await using var client = await RemoteTypingClient.ConnectAsync(session);

        var idlePing = await client.PingAsync(10);
        string? bulkPane = null;
        string? previousTyping = null;

        var keys = new List<RemoteTypingKey>();
        var fixtures = new List<RemoteTypingFixtureResult>();
        var loadPing = new List<double>();
        long typingDownBytes = 0;
        var typingSeconds = 0.0;
        foreach (var name in options.Fixtures)
        {
            var script = TypingScript.Load(name);
            // Each fixture gets a new pane in the same tab. The bulk pane, when
            // the cell has one, stays and writes during every fixture.
            var typingPane = await mux.CreatePaneAsync(
                ScriptedPaneFactory.TypingPrefix + name,
                splitFrom: previousTyping ?? bulkPane);
            if (previousTyping is not null)
                await mux.ClosePaneAsync(previousTyping);
            previousTyping = typingPane;
            if (cell.Load == RemoteTypingCell.Bulk && bulkPane is null)
                bulkPane = await mux.CreatePaneAsync(ScriptedPaneFactory.BulkCommand, splitFrom: typingPane);
            await client.ShowAsync(
                bulkPane is null ? [typingPane] : [typingPane, bulkPane],
                script.Cols,
                script.Rows);
            if (!await client.WaitUntilAsync(() => client.HasFrame(typingPane), options.FirstFrameWait))
            {
                var what = $"no first frame of {name} ({typingPane}) in {options.FirstFrameWait.TotalSeconds:0} s: {client.Describe()}";
                Assert.True(options.ReportOnly, what);
                fixtures.Add(new RemoteTypingFixtureResult(name, 0, 0, what));
                continue;
            }

            // The settle gap of the model: the pane is quiet before the keys.
            await Pacing.GapAsync(options.Settle);
            if (bulkPane is not null && loadPing.Count == 0)
                loadPing.AddRange(await client.PingAsync(10));

            var sent = new List<(TypingScriptKey Key, long ReadAt)>();
            var downBefore = proxy.Down.Bytes;
            var start = Stopwatch.GetTimestamp();
            var index = 0;
            foreach (var key in script.TypedKeys)
            {
                var due = options.KeyInterval * index++;
                var wait = due - Stopwatch.GetElapsedTime(start);
                if (wait > TimeSpan.Zero)
                    await Pacing.GapAsync(wait);
                sent.Add((key, await client.SendKeyAsync(key.Input)));
            }

            await client.WaitUntilAsync(() => sent.All(s => client.Closed(s.ReadAt) is not null), options.CloseWait);

            typingDownBytes += proxy.Down.Bytes - downBefore;
            typingSeconds += Stopwatch.GetElapsedTime(start).TotalSeconds;

            var divergence = await WaitForConvergenceAsync(client, typingPane, script, ghosttyLibrary, options.ConvergeWait);
            var closed = 0;
            for (var i = 0; i < sent.Count; i++)
            {
                var done = client.Closed(sent[i].ReadAt);
                if (done is not null)
                    closed++;
                keys.Add(new RemoteTypingKey(
                    name,
                    i,
                    sent[i].Key.Label,
                    AttachLatencyMeter.CountKeys(sent[i].Key.Input),
                    done?.Seq,
                    done is { } d ? d.KeyToPaintUs / 1000.0 : null));
            }

            fixtures.Add(new RemoteTypingFixtureResult(name, sent.Count, closed, divergence));
        }

        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        return new RemoteTypingCellResult(
            cell,
            idlePing,
            loadPing,
            keys,
            fixtures,
            client.Meter.Report(),
            client.Reanchors,
            client.RejectedFrames,
            client.CellsBodies,
            proxy.Up,
            proxy.Down,
            mux.Panes.BulkBytesWritten,
            seconds)
        {
            InputMismatches = mux.Panes.InputMismatches,
            TypingWindowDownBytes = typingDownBytes,
            TypingWindowSeconds = typingSeconds,
            ClientFault = client.LoopFault,
            RenderCredit = client.RenderCredit,
            RenderAcks = client.RenderAcksSent,
        };
    }

    /// <summary>
    /// Waits until the host bytes of the typing pane, read by a fresh Ghostty,
    /// equal an oracle Ghostty that read every recorded byte. Returns null when
    /// they are equal, or the first difference at the deadline.
    /// </summary>
    private static async Task<string?> WaitForConvergenceAsync(
        RemoteTypingClient client,
        string paneId,
        TypingScript script,
        string ghosttyLibrary,
        TimeSpan wait)
    {
        using var oracle = new GhosttyVtEngine(script.Cols, script.Rows, libraryPathOverride: ghosttyLibrary);
        foreach (var key in script.Keys)
        {
            foreach (var chunk in key.Chunks)
                oracle.Feed(chunk.Bytes);
        }

        // The host bytes change only when the client handles an event, so
        // each change of the client compares the screens again. The host
        // bytes only grow: the same length is the same screen.
        string? diff = null;
        var compared = -1;
        await client.WaitUntilAsync(
            () =>
            {
                var bytes = client.HostBytes(paneId);
                if (bytes.Length == compared)
                    return diff is null;
                compared = bytes.Length;
                using var host = new GhosttyVtEngine(script.Cols, script.Rows, libraryPathOverride: ghosttyLibrary);
                host.Feed(bytes);
                diff = Diff(oracle, host);
                return diff is null;
            },
            wait);
        return diff;
    }

    /// <summary>
    /// The first difference between the oracle viewport and the host pane:
    /// a cell (text, width, style), or the cursor. Null when they are equal.
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
                    return $"row {row}: host row has {gotRow.Length} cells, want {wantRow.Length}";
                var w = wantRow[col];
                var g = gotRow[col];
                var wantText = string.IsNullOrEmpty(w.Text) ? " " : w.Text;
                var gotText = string.IsNullOrEmpty(g.Text) ? " " : g.Text;
                if (!string.Equals(wantText, gotText, StringComparison.Ordinal)
                    || w.Width != g.Width
                    || w.IsContinuation != g.IsContinuation
                    || w.Style != g.Style)
                {
                    return $"cell {col},{row}: want '{RowText(wantRow)}' got '{RowText(gotRow)}'";
                }
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
        return null;
    }

    private static string RowText(VtCellSnapshot[] row)
    {
        var text = new StringBuilder(row.Length);
        foreach (var cell in row)
        {
            if (!cell.IsContinuation)
                text.Append(string.IsNullOrEmpty(cell.Text) ? " " : cell.Text);
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Writes the per-key times and the summary of one cell as JSON. The
    /// writer is <see cref="Utf8JsonWriter"/>, so no reflection runs.
    /// </summary>
    public static string WriteJson(RemoteTypingCellResult result, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, result.Cell.Id + ".json");
        using var file = File.Create(path);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteStartObject("cell");
        json.WriteString("id", result.Cell.Id);
        json.WriteString("transport", result.Cell.Transport);
        json.WriteString("load", result.Cell.Load);
        json.WriteNumber("rtt_ms", result.Cell.Profile.Rtt.TotalMilliseconds);
        json.WriteNumber("jitter_ms", result.Cell.Profile.Jitter.TotalMilliseconds);
        json.WriteNumber("loss_percent", result.Cell.Profile.LossRate * 100);
        json.WriteNumber("cap_bytes_per_s", result.Cell.Profile.BandwidthBytesPerSecond);
        json.WriteNumber("queue_limit_bytes", result.Cell.Profile.QueueLimitBytes);
        json.WriteNumber("seed", result.Cell.Profile.Seed);
        json.WriteEndObject();
        WriteStats(json, "key_to_paint_ms", result.KeyToPaint);
        WriteStats(json, "ping_idle_ms", LatencyStats.From([.. result.IdlePingMs]));
        WriteStats(json, "ping_load_ms", LatencyStats.From([.. result.LoadPingMs]));
        json.WriteNumber("keys", result.Keys.Count);
        json.WriteNumber("open_keys", result.OpenKeys);
        json.WriteNumber("reanchors", result.Reanchors);
        json.WriteBoolean("render_credit", result.RenderCredit);
        json.WriteNumber("render_acks", result.RenderAcks);
        json.WriteNumber("rejected_frames", result.RejectedFrames);
        json.WriteNumber("cells_bodies", result.CellsBodies);
        json.WriteNumber("seconds", result.Seconds);
        json.WriteNumber("bulk_app_bytes", result.BulkAppBytes);
        json.WriteNumber("render_bytes_per_s", Math.Round(result.RenderBytesPerSecond));
        json.WriteNumber("input_mismatches", result.InputMismatches);
        json.WriteStartObject("meter");
        json.WriteNumber("keys_read", result.Meter.KeysRead);
        json.WriteNumber("keys_sent", result.Meter.KeysSent);
        json.WriteNumber("keys_acked", result.Meter.KeysAcked);
        json.WriteNumber("open_keys", result.Meter.OpenKeys);
        json.WriteNumber("stale_key", result.Meter.StaleKey);
        json.WriteNumber("dropped_keys", result.Meter.DroppedKeys);
        WriteHistogram(json, "key_to_paint", result.Meter.KeyToPaint);
        json.WriteStartObject("hops");
        foreach (var (name, hop) in result.Meter.Hops.OrderBy(h => h.Key, StringComparer.Ordinal))
            WriteHistogram(json, name, hop);
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteStartObject("proxy");
        WriteCounters(json, "up", result.Up);
        WriteCounters(json, "down", result.Down);
        json.WriteEndObject();
        json.WriteStartArray("fixtures");
        foreach (var fixture in result.Fixtures)
        {
            json.WriteStartObject();
            json.WriteString("name", fixture.Fixture);
            json.WriteNumber("keys", fixture.Keys);
            json.WriteNumber("closed", fixture.Closed);
            json.WriteBoolean("converged", fixture.Divergence is null);
            if (fixture.Divergence is not null)
                json.WriteString("divergence", fixture.Divergence);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteStartArray("per_key");
        foreach (var key in result.Keys)
        {
            json.WriteStartObject();
            json.WriteString("fixture", key.Fixture);
            json.WriteNumber("index", key.Index);
            json.WriteString("key", key.Label);
            json.WriteNumber("key_count", key.KeyCount);
            if (key.Seq is { } seq)
                json.WriteNumber("seq", seq);
            else
                json.WriteNull("seq");
            if (key.KeyToPaintMs is { } ms)
                json.WriteNumber("key_to_paint_ms", Math.Round(ms, 3));
            else
                json.WriteNull("key_to_paint_ms");
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        return path;
    }

    /// <summary>
    /// The JSON directory: <c>HYPA_REMOTE_TYPING_RESULTS</c>, or
    /// <c>TestResults/remote-typing</c> at the repository root.
    /// </summary>
    public static string ResultsDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("HYPA_REMOTE_TYPING_RESULTS");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(TypingScript.FindRepoRoot(), "TestResults", "remote-typing")
            : configured;
    }

    private static void WriteStats(Utf8JsonWriter json, string name, LatencyStats stats)
    {
        json.WriteStartObject(name);
        json.WriteNumber("count", stats.Count);
        json.WriteNumber("p50", Math.Round(stats.P50, 3));
        json.WriteNumber("p95", Math.Round(stats.P95, 3));
        json.WriteNumber("p99", Math.Round(stats.P99, 3));
        json.WriteNumber("max", Math.Round(stats.Max, 3));
        json.WriteEndObject();
    }

    /// <summary>
    /// The app-local MsQuic library beside the tests: "library present,
    /// digest ok", or the first fault. It does not use the probe, so it shows
    /// a missing or changed library also when the probe stops before that.
    /// </summary>
    private static string AppLocalMsQuicState()
    {
        var name = OperatingSystem.IsMacOS() ? "libmsquic.dylib" : "libmsquic.so.2";
        var library = Path.Combine(AppContext.BaseDirectory, name);
        var sidecar = library + ".sha256";
        if (!File.Exists(library))
            return "library missing: " + name;
        if (!File.Exists(sidecar))
            return "library digest sidecar missing: " + name;
        var expected = File.ReadAllText(sidecar).Trim();
        using var stream = File.OpenRead(library);
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
            ? "library present, digest ok"
            : "library digest does not match: " + name;
    }

    private static void WriteHistogram(Utf8JsonWriter json, string name, LatencyHistogramReport histogram)
    {
        json.WriteStartObject(name);
        json.WriteNumber("count", histogram.Count);
        json.WriteNumber("p50_us", histogram.P50Us);
        json.WriteNumber("p95_us", histogram.P95Us);
        json.WriteNumber("p99_us", histogram.P99Us);
        json.WriteNumber("max_us", histogram.MaxUs);
        json.WriteEndObject();
    }

    private static void WriteCounters(Utf8JsonWriter json, string name, ImpairmentCounters counters)
    {
        json.WriteStartObject(name);
        json.WriteNumber("packets", counters.Packets);
        json.WriteNumber("bytes", counters.Bytes);
        json.WriteNumber("lost", counters.Lost);
        json.WriteNumber("queue_drops", counters.QueueDrops);
        json.WriteNumber("tcp_stalls", counters.Stalls);
        json.WriteEndObject();
    }
}
