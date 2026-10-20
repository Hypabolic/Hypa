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
/// A live <see cref="ControlPlaneService"/> with one <c>/bin/cat</c> pane and
/// two attach connections. One connection offers
/// <see cref="AttachEndpointProtocol.RowShiftCapability"/> and one does not.
/// Each connection applies its bodies through the attach host paint into a
/// host Ghostty. The host must equal an oracle Ghostty that read the pane
/// bytes directly.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed class RowShiftLiveTests(ITestOutputHelper output)
{
    private readonly string? _lib = GhosttyTestRequire.TryResolveNativeLibraryPath();

    [SkippableFact]
    public async Task Scrolling_output_reaches_both_peers_and_only_the_peer_with_the_capability_gets_a_shift()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(("shift-client", true), ("plain-client", false));
        var (shift, plain) = (session.Clients[0], session.Clients[1]);
        Assert.True(session.Cp.RowShiftOn(shift.Sink.ConnectionId));
        Assert.False(session.Cp.RowShiftOn(plain.Sink.ConnectionId));
        Assert.Contains(AttachEndpointProtocol.RowShiftCapability, shift.Welcome.Capabilities);

        var (cols, rows) = session.GridSize(shift);
        using var oracle = new GhosttyVtEngine(cols, rows, libraryPathOverride: _lib);
        using var shiftView = new ClientSide(session.PaneId, cols, rows, _lib);
        using var plainView = new ClientSide(session.PaneId, cols, rows, _lib);
        shiftView.Apply(session.TakeCells(shift));
        plainView.Apply(session.TakeCells(plain));

        // Fill the pane, then scroll it by one, two and three lines per paint.
        await session.FeedAsync(oracle, Lines(1, rows));
        await session.ConvergeAsync(shift, shiftView, oracle, "shift peer, filled");
        await session.ConvergeAsync(plain, plainView, oracle, "plain peer, filled");
        var next = rows + 1;
        long shiftBytes = 0;
        long plainBytes = 0;
        long shiftLineBytes = 0;
        long plainLineBytes = 0;
        long shiftRowBytes = 0;
        long plainRowBytes = 0;
        var shiftedBodies = 0;
        for (var paint = 0; paint < 30; paint++)
        {
            var count = 1 + (paint % 3);
            await session.FeedAsync(oracle, "\r\n" + Lines(next, next + count - 1));
            next += count;
            var shiftCells = await session.ConvergeAsync(shift, shiftView, oracle, "shift peer, paint " + paint);
            var plainCells = await session.ConvergeAsync(plain, plainView, oracle, "plain peer, paint " + paint);
            shiftBytes += shiftCells.Sum(body => (long)body.PayloadBytes);
            plainBytes += plainCells.Sum(body => (long)body.PayloadBytes);
            shiftLineBytes += shiftCells.Sum(body => (long)body.LineBytes);
            plainLineBytes += plainCells.Sum(body => (long)body.LineBytes);
            shiftRowBytes += shiftCells.Sum(body => (long)body.RowBytes);
            plainRowBytes += plainCells.Sum(body => (long)body.RowBytes);
            shiftedBodies += shiftCells.Count(body => body.Payload.Shift != 0);
            Assert.All(plainCells, body => Assert.Equal(0, body.Payload.Shift));
        }

        output.WriteLine(
            $"30 scroll paints at {cols}x{rows}: rows bytes plain peer {plainRowBytes} B, shift peer {shiftRowBytes} B; "
            + $"payload bytes plain peer {plainBytes} B, shift peer {shiftBytes} B; "
            + $"line bytes plain peer {plainLineBytes} B, shift peer {shiftLineBytes} B; shifted bodies {shiftedBodies}");
        Assert.Equal(30, shiftedBodies);
        // The fields that each attach body has (boot id, revisions, cursor)
        // are the same for both peers. The rows are what the shift changes.
        Assert.True(
            shiftRowBytes * 4 <= plainRowBytes,
            $"the shift peer got {shiftRowBytes} B of rows, the plain peer {plainRowBytes} B");
        Assert.True(shiftBytes < plainBytes);
        // The peer without the capability never sees the field on the wire.
        Assert.All(plain.Sink.Lines, line => Assert.DoesNotContain("\"shift\"", line, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_cleared_full_pane_sends_a_full_frame_without_reanchor_and_a_chain_break_still_reanchors()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        await using var session = await OpenAsync(("shift-client", true));
        var client = session.Clients[0];
        var (cols, rows) = session.GridSize(client);
        using var oracle = new GhosttyVtEngine(cols, rows, libraryPathOverride: _lib);
        using var view = new ClientSide(session.PaneId, cols, rows, _lib);
        view.Apply(session.TakeCells(client));
        var full = new StringBuilder();
        for (var row = 1; row <= rows; row++)
        {
            if (row > 1)
                full.Append("\r\n");
            full.Append(row.ToString("D3")).Append(' ').Append(new string((char)('a' + (row % 26)), cols - 5));
        }

        await session.FeedAsync(oracle, full.ToString());
        await session.ConvergeAsync(client, view, oracle, "filled");

        await session.FeedAsync(oracle, "\u001b[2J\u001b[H$ ");
        var cleared = Assert.Single(await session.NextCellsAsync(client));
        output.WriteLine($"clear at {cols}x{rows}: payload {cleared.PayloadBytes} B, full {cleared.Payload.Full}");
        Assert.True(cleared.Payload.Full);
        Assert.False(cleared.Payload.Reanchor);
        view.Apply([cleared]);
        Assert.Null(Diff(oracle, view.Host, "after the clear"));

        // A delta after it chains on the full frame.
        await session.FeedAsync(oracle, "ls");
        var delta = Assert.Single(await session.NextCellsAsync(client));
        Assert.False(delta.Payload.Full);
        Assert.Equal(cleared.Payload.Generation, delta.Payload.BaseGeneration);
        Assert.All(view.Apply([delta]), Assert.True);
        Assert.Null(Diff(oracle, view.Host, "after the delta"));

        // A chain break: the client asks again with terminal.observe and
        // replace false. The next body is a full reanchor frame.
        await session.ObserveAgainAsync(client);
        await session.FeedAsync(oracle, " -l");
        var after = await session.NextCellsAsync(client);
        Assert.NotEmpty(after);
        Assert.True(after[0].Payload.Full, after[0].Line);
        Assert.True(after[0].Payload.Reanchor, after[0].Line);
        view.Apply(after);
        Assert.Null(Diff(oracle, view.Host, "after the reanchor"));
    }

    [SkippableFact]
    public async Task A_client_that_offers_row_shift_renders_the_plain_deltas_of_a_mux_without_it()
    {
        GhosttyTestRequire.RequireNativeLibrary(_lib);
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        // The hello of the real client goes to a mux whose welcome is the
        // welcome of a mux from before row shift.
        await using var mux = OldMux.Listen();
        await using var connection = await mux.ConnectAsync();
        var hello = AttachEndpointPreflight.BuildHello(
            "new-client",
            new AttachGeometry { Columns = 80, Rows = 24, CellWidthPx = 8, CellHeightPx = 16, GeometryRevision = 1 },
            surfaceActive: true);
        Assert.Contains(AttachEndpointProtocol.RowShiftCapability, hello.OptionalCapabilities!);
        var (welcome, _) = await new AttachEndpointRpcClient(connection).HelloAsync(hello);
        Assert.Null(welcome.Error);
        Assert.DoesNotContain(AttachEndpointProtocol.RowShiftCapability, welcome.Capabilities);

        // That mux encodes without a shift. The client paints each body.
        const int cols = 80;
        const int rows = 24;
        using var vt = new GhosttyVtEngine(cols, rows, libraryPathOverride: _lib);
        await using var pane = new PaneRuntime(
            new PaneSpawnOptions
            {
                Id = Hypa.AgentRuntime.Domain.PaneId.New(),
                Cwd = Path.GetTempPath(),
                Command = "/bin/true",
                Args = [],
                Cols = cols,
                Rows = rows,
            },
            spawnProcess: static (_, _) => throw new InvalidOperationException("not started"),
            vt: vt);
        using var oracle = new GhosttyVtEngine(cols, rows, libraryPathOverride: _lib);
        using var view = new ClientSide("p1", cols, rows, _lib);
        var encoder = new VtCellsEncoder();
        VtFrame? baseline = null;
        var generation = 0L;
        var next = 1;
        for (var paint = 0; paint < 20; paint++)
        {
            var count = paint == 0 ? rows : 1 + (paint % 3);
            var text = (paint == 0 ? "" : "\r\n") + Lines(next, next + count - 1);
            next += count;
            pane.FeedVtForTests(text);
            oracle.Feed(Encoding.UTF8.GetBytes(text));
            Assert.True(pane.TryCaptureLivePaintFrame(out var frame, out _, out _));
            pane.CommitPostedPaint();
            var previous = generation;
            generation++;
            var body = encoder.Encode(frame!, baseline, "p1", generation, previous, frame!.OccupantGeneration);
            Assert.NotNull(body);
            Assert.Equal(0, body.Value.Payload.Shift);
            var line = Encoding.UTF8.GetString(body.Value.PayloadUtf8);
            Assert.All(view.Apply([new Body(body.Value.Payload, line, line.Length, 0)]), Assert.True);
            Assert.Null(Diff(oracle, view.Host, "paint " + paint));
            baseline = frame;
        }
    }

    private static string Lines(int first, int last)
    {
        var text = new StringBuilder();
        for (var i = first; i <= last; i++)
        {
            if (i > first)
                text.Append("\r\n");
            text.Append("output line ").Append(i).Append(" of the live scroll");
        }

        return text.ToString();
    }

    /// <summary>The first difference between the oracle and a host, or null.</summary>
    private static string? Diff(GhosttyVtEngine oracle, GhosttyVtEngine host, string step)
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
                    return $"{step}: row {row} has {gotRow.Length} cells";
                var w = wantRow[col];
                var g = gotRow[col];
                var wText = string.IsNullOrEmpty(w.Text) ? " " : w.Text;
                var gText = string.IsNullOrEmpty(g.Text) ? " " : g.Text;
                if (wText != gText || w.Width != g.Width || w.IsContinuation != g.IsContinuation || w.Style != g.Style)
                    return $"{step}: cell {col},{row} want '{wText}' got '{gText}'";
            }
        }

        if (want.Cursor is { } wc && got.Cursor is { } gc && (wc.Col != gc.Col || wc.Row != gc.Row))
            return $"{step}: cursor want {wc.Col},{wc.Row} got {gc.Col},{gc.Row}";
        return null;
    }

    private static AttachEndpointHello Hello(string clientId, bool offerRowShift) =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = (uint)ProtocolVersion.Major,
            ProtocolMinor = (uint)ProtocolVersion.Minor,
            ClientId = clientId,
            Geometry = new AttachGeometry
            {
                Columns = 80,
                Rows = 24,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            },
            SurfaceActive = false,
            SnapshotCodecs = [AttachEndpointProtocol.SnapshotCodec],
            SurfaceCodecs = [AttachEndpointProtocol.SurfaceCodec],
            InputCodecs = [AttachEndpointProtocol.InputCodec],
            RequiredCapabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
            OptionalCapabilities = offerRowShift ? [AttachEndpointProtocol.RowShiftCapability] : null,
        };

    private static JsonElement Json(JsonObject obj) =>
        JsonDocument.Parse(obj.ToJsonString()).RootElement;

    private async Task<Session> OpenAsync(params (string ClientId, bool OfferRowShift)[] clients)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-row-shift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new RuntimeStatePaths { StateDirectory = dir };
        var migrator = new SqliteRuntimeSchemaMigrator(paths);
        Assert.True((await migrator.MigrateAsync()).IsOk);
        var state = new AppState(SessionId.New("row-shift"));
        state.UpdateSession(s => s with { Name = "row-shift", LifecycleState = SessionLifecycle.Ready });
        state.CreateWorkspace(dir, label: "default");
        var store = new SqliteRuntimeSessionStore(paths);
        Assert.True((await store.SaveAsync(state.Snapshot())).IsOk);
        var manifests = new SqliteJournalManifestStore(paths);
        var journal = new FileRuntimeEventJournal(paths, manifests, state.SessionId.Value);
        Assert.True((await journal.RecoverAsync()).IsOk);
        var factory = new PaneRuntimeFactory(
            new PaneIntelligencePipeline(),
            ptyFactory: TestPaneFactories.ProcessIo(),
            vtEngineFactory: new VtEngineFactory(new VtProviderSelection { LibraryPathOverride = _lib }));
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            store: store,
            journal: journal,
            subscriptions: new EventSubscriptionHub());
        var session = new Session(dir, paths, cp);
        try
        {
            var create = await cp.DispatchAsync(
                ProtocolMethods.PaneCreate,
                Json(new JsonObject { ["command"] = "/bin/cat", ["cwd"] = dir }),
                CancellationToken.None);
            session.PaneId = create.GetProperty("pane_id").GetString()!;
            session.Runtime = Assert.IsType<PaneRuntime>(cp.PeekRuntime(session.PaneId));
            foreach (var (clientId, offer) in clients)
            {
                var sink = new LaneSink(clientId);
                var welcome = await cp.HandleAttachHelloAsync(Hello(clientId, offer), sink, CancellationToken.None);
                var client = new Client(clientId, sink)
                {
                    Welcome = welcome.Deserialize(ProtocolJsonContext.Default.AttachEndpointWelcome)!,
                };
                session.Clients.Add(client);
                await cp.HandleAttachSurfaceInterestAsync(
                    new AttachSurfaceInterestRequest
                    {
                        RequestId = "on",
                        ClientId = clientId,
                        Active = true,
                        GeometryRevision = 1,
                    },
                    sink,
                    CancellationToken.None);
                await session.ObserveAsync(client);
            }

            await session.PaintAsync();
            foreach (var client in session.Clients)
            {
                await Poll.UntilAsync(
                    async () =>
                    {
                        if (session.PeekCells(client).Count > 0)
                            return true;
                        await session.PaintAsync();
                        return false;
                    },
                    "a first frame for " + client.ClientId);
            }

            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private sealed record Client(string ClientId, LaneSink Sink)
    {
        public AttachEndpointWelcome Welcome { get; init; } = null!;

        public string SubscriptionId { get; set; } = "";

        public int Mark { get; set; }
    }

    private sealed record Body(TerminalRenderCellsPayload Payload, string Line, int PayloadBytes, int RowBytes)
    {
        public int LineBytes => Encoding.UTF8.GetByteCount(Line);
    }

    /// <summary>The attach client side for one pane: a chrome-off live state, an in-memory TTY and a host Ghostty.</summary>
    private sealed class ClientSide : IDisposable
    {
        private readonly MemoryStream _stream = new();
        private readonly UnixRawTerminal _tty;
        private readonly AttachLiveState _live;
        private long _read;

        public ClientSide(string paneId, int cols, int rows, string? lib)
        {
            _tty = new UnixRawTerminal(_stream, cols, rows);
            Host = new GhosttyVtEngine(cols, rows, libraryPathOverride: lib);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            _live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = null!,
                PaneId = paneId,
            };
        }

        public GhosttyVtEngine Host { get; }

        public List<bool> Apply(IEnumerable<Body> bodies)
        {
            var applied = new List<bool>();
            foreach (var body in bodies)
                applied.Add(AttachSession.TryWriteCells(_tty, _live, body.Payload, reanchorBeforePaint: true));
            var length = _stream.Length;
            if (length > _read)
            {
                Host.Feed(_stream.GetBuffer().AsSpan((int)_read, (int)(length - _read)));
                _read = length;
            }

            return applied;
        }

        public void Dispose()
        {
            _tty.Dispose();
            Host.Dispose();
        }
    }

    private sealed class Session(string dir, RuntimeStatePaths paths, ControlPlaneService cp) : IAsyncDisposable
    {
        public ControlPlaneService Cp { get; } = cp;

        public List<Client> Clients { get; } = [];

        public string PaneId { get; set; } = "";

        public PaneRuntime Runtime { get; set; } = null!;

        public async Task PaintAsync()
        {
            await Poll.GuardAsync(Cp.FlushCoalescedPaintForTestsAsync(PaneId), "paint flush");
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
        }

        /// <summary>Feeds the pane and the oracle, then paints.</summary>
        public async Task FeedAsync(GhosttyVtEngine oracle, string text)
        {
            oracle.Feed(Encoding.UTF8.GetBytes(text));
            Runtime.PublishFedOutputForTests(text);
            await Cp.WaitOutputDeliveredForTestsAsync(PaneId);
            await PaintAsync();
        }

        /// <summary>
        /// Applies the bodies of <paramref name="client"/> until its host
        /// equals the oracle. A paint of one observer can post after the paint
        /// call returns, so the check waits for it. Returns the bodies.
        /// </summary>
        public async Task<List<Body>> ConvergeAsync(Client client, ClientSide view, GhosttyVtEngine oracle, string step)
        {
            var all = new List<Body>();
            string? diff = null;
            await Poll.UntilAsync(
                async () =>
                {
                    var bodies = TakeCells(client);
                    Assert.All(view.Apply(bodies), Assert.True);
                    all.AddRange(bodies);
                    diff = Diff(oracle, view.Host, step);
                    if (diff is null)
                        return true;
                    await PaintAsync();
                    return false;
                },
                () => diff ?? step);
            return all;
        }

        /// <summary>
        /// The cells bodies since the last call. A paint of one observer can
        /// post after the paint call returns, so this waits for at least one.
        /// </summary>
        public async Task<List<Body>> NextCellsAsync(Client client)
        {
            var bodies = new List<Body>();
            await Poll.UntilAsync(
                async () =>
                {
                    bodies = TakeCells(client);
                    if (bodies.Count > 0)
                        return true;
                    await PaintAsync();
                    return false;
                },
                "a cells body for " + client.ClientId);
            return bodies;
        }

        public (int Cols, int Rows) GridSize(Client client)
        {
            var first = PeekCells(client)[0].Payload;
            return (first.GridCols, first.GridRows);
        }

        public List<Body> PeekCells(Client client) => CellsFrom(client, client.Mark);

        /// <summary>The cells bodies of the pane since the last call, in order.</summary>
        public List<Body> TakeCells(Client client)
        {
            var count = client.Sink.Count;
            var bodies = CellsFrom(client, client.Mark, count);
            client.Mark = count;
            return bodies;
        }

        private List<Body> CellsFrom(Client client, int mark, int end = int.MaxValue)
        {
            var list = new List<Body>();
            var lines = client.Sink.Lines;
            for (var i = mark; i < lines.Count && i < end; i++)
            {
                using var doc = JsonDocument.Parse(lines[i]);
                if (!doc.RootElement.TryGetProperty("params", out var parms)
                    || !parms.TryGetProperty("type", out var type)
                    || type.GetString() != ProtocolEventTypes.TerminalRender
                    || !parms.TryGetProperty("payload", out var payload)
                    || payload.ValueKind != JsonValueKind.Object
                    || !payload.TryGetProperty("kind", out var kind)
                    || kind.GetString() != TerminalRenderCellsPayload.KindCells
                    || !payload.TryGetProperty("pane_id", out var pane)
                    || pane.GetString() != PaneId)
                {
                    continue;
                }

                list.Add(new Body(
                    payload.Deserialize(ProtocolJsonContext.Default.TerminalRenderCellsPayload)!,
                    lines[i],
                    Encoding.UTF8.GetByteCount(payload.GetRawText()),
                    payload.TryGetProperty("rows", out var rowsJson) ? Encoding.UTF8.GetByteCount(rowsJson.GetRawText()) : 0));
            }

            return list;
        }

        public async Task ObserveAsync(Client client)
        {
            var sub = await Cp.DispatchAsync(
                ProtocolMethods.EventsSubscribe,
                Json(new JsonObject
                {
                    ["from_seq"] = 0,
                    ["types"] = new JsonArray("output", "render"),
                    ["live"] = true,
                }),
                client.Sink,
                CancellationToken.None);
            client.SubscriptionId = sub.GetProperty("subscription_id").GetString()!;
            await Cp.CompleteEventsSubscribeAsync(client.SubscriptionId, [], client.Sink, CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Json(new JsonObject
                {
                    ["subscription_id"] = client.SubscriptionId,
                    ["pane_ids"] = new JsonArray(PaneId),
                }),
                client.Sink,
                CancellationToken.None);
            await Cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["subscription_id"] = client.SubscriptionId,
                }),
                client.Sink,
                CancellationToken.None);
        }

        /// <summary>The call that the attach client makes after a dropped delta.</summary>
        public Task ObserveAgainAsync(Client client) =>
            Cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Json(new JsonObject
                {
                    ["pane_id"] = PaneId,
                    ["subscription_id"] = client.SubscriptionId,
                    ["replace"] = false,
                }),
                client.Sink,
                CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Cp.ShutdownAsync(CancellationToken.None);
            SqliteTestCleanup.ReleaseAndDelete(dir, paths.DatabasePath);
        }
    }

    /// <summary>
    /// A Unix-socket stand-in for a mux from before row shift. It answers
    /// <c>hypa.attach.hello.v1</c> with the welcome capabilities of that mux.
    /// </summary>
    private sealed class OldMux : IAsyncDisposable
    {
        private readonly System.Net.Sockets.Socket _listener;
        private System.Net.Sockets.NetworkStream? _stream;
        private Task? _reader;

        private OldMux(System.Net.Sockets.Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        private string Path { get; }

        public static OldMux Listen()
        {
            var path = "/tmp/hypa-old-mux-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Unspecified);
            listener.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            return new OldMux(listener, path);
        }

        public async Task<ControlPlaneClient> ConnectAsync()
        {
            var client = new ControlPlaneClient(Path, connectTimeout: Poll.HangGuard, callTimeout: Poll.HangGuard);
            var accept = _listener.AcceptAsync();
            await client.ConnectAsync();
            _stream = new System.Net.Sockets.NetworkStream(await accept, ownsSocket: true);
            _reader = ReadAsync(_stream);
            return client;
        }

        private static async Task ReadAsync(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.GetProperty("method").GetString() != ProtocolMethods.AttachHello
                        || !root.TryGetProperty("id", out var id))
                    {
                        continue;
                    }

                    var welcome = new AttachEndpointWelcome
                    {
                        EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
                        ProtocolMajor = (uint)ProtocolVersion.Major,
                        ProtocolMinor = (uint)ProtocolVersion.Minor,
                        BootId = "boot_old",
                        MuxIdentity = "old-mux",
                        SnapshotCodec = AttachEndpointProtocol.SnapshotCodec,
                        SurfaceCodec = AttachEndpointProtocol.SurfaceCodec,
                        InputCodec = AttachEndpointProtocol.InputCodec,
                        Methods = [.. AttachEndpointProtocol.Methods],
                        Capabilities =
                        [
                            .. AttachEndpointProtocol.RequiredCapabilities,
                            AttachEndpointProtocol.InputAckCapability,
                            AttachEndpointProtocol.RenderCreditCapability,
                            AttachEndpointProtocol.EchoModeCapability,
                        ],
                        ConnectionGeneration = 1,
                    };
                    var reply = new JsonObject
                    {
                        ["id"] = id.GetString(),
                        ["result"] = JsonSerializer.SerializeToNode(welcome, ProtocolJsonContext.Default.AttachEndpointWelcome),
                    };
                    var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n");
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stream?.Dispose();
            _listener.Dispose();
            if (_reader is not null)
            {
                try
                {
                    await _reader.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
                {
                }
            }

            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class LaneSink(string id) : IClientConnection
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public string ConnectionId { get; } = id;

        public bool UsesWriterLanes => true;

        public int MaxLineBytes => 1_048_576;

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                    return _lines.Count;
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
