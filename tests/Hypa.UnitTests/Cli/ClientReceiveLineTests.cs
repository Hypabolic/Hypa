using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.ControlPlane;
using Hypa.ControlPlane.Unix;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The receive step of <see cref="ControlPlaneClient"/>: each received line
/// is read as UTF-8 in a buffer from a pool, one pass over the bytes selects
/// its queue, and the event is parsed one time when the render loop takes it.
/// Every buffer goes back to the pool.
/// </summary>
public sealed class ClientReceiveLineTests
{
    private const string PaneId = "p1";
    private readonly ITestOutputHelper _output;

    public ClientReceiveLineTests(ITestOutputHelper output) => _output = output;

    private enum Queue
    {
        Reliable,
        Ordered,
        LatestRender,
    }

    [SkippableFact]
    public async Task Each_line_type_goes_to_its_queue_and_decodes_as_the_old_path_did()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket pair");
        await RunEachLineTypeAsync(reorder: false);
    }

    [SkippableFact]
    public async Task Lines_with_the_properties_in_another_order_go_to_the_same_queues()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket pair");
        await RunEachLineTypeAsync(reorder: true);
    }

    private async Task RunEachLineTypeAsync(bool reorder)
    {
        var pool = new CountingPool();
        using var pair = SocketPair.Open();
        await using var client = pair.Client(pool);
        var admitted = 0;
        client.SetEventAdmitted(() => Interlocked.Increment(ref admitted));
        var shellChunks = new List<string>();
        client.SetShellChunkAdmit(root =>
        {
            var id = root.GetProperty("id").GetString()!;
            if (!id.StartsWith("shell-", StringComparison.Ordinal))
                return false;
            lock (shellChunks)
                shellChunks.Add(root.GetProperty("data").GetString()!);
            return true;
        });

        var cases = new (string Line, Queue Queue)[]
        {
            (ReliableLine(ProtocolEventTypes.LayoutUpdated, """{"tab_id":"t1"}"""), Queue.Reliable),
            (ReliableLine(
                ProtocolEventTypes.PaneScrollChanged,
                RuntimeEventPayloadJson.WritePaneScrollChanged(PaneId, 3, 90)), Queue.Reliable),
            (CellsLine(Cells(generation: 1, baseGeneration: 0, full: true)), Queue.Ordered),
            (CellsLine(Cells(generation: 2, baseGeneration: 1)), Queue.Ordered),
            // No params.lane: the payload selects the ordered lane.
            (WithoutLane(CellsLine(Cells(generation: 3, baseGeneration: 0, reanchor: true))), Queue.Ordered),
        };

        foreach (var (line, queue) in cases)
        {
            var sent = reorder ? Reorder(line) : line;
            var before = Volatile.Read(ref admitted);
            pair.Send(sent);
            Until(() => Volatile.Read(ref admitted) > before, "the client did not admit the event");
            AssertQueuedIn(client, queue);
            Assert.True(client.TryTakeQueuedEvent(out var taken));
            AssertSameAsOldPath(sent, taken);
        }

        // A latest-wins cells line: base 0, not full, not reanchor. With and
        // without params.lane.
        foreach (var line in new[]
                 {
                     CellsLine(Cells(generation: 4, baseGeneration: 0)),
                     WithoutLane(CellsLine(Cells(generation: 5, baseGeneration: 0))),
                 })
        {
            var sent = reorder ? Reorder(line) : line;
            var before = Volatile.Read(ref admitted);
            pair.Send(sent);
            Until(() => Volatile.Read(ref admitted) > before, "the client did not admit the render event");
            AssertQueuedIn(client, Queue.LatestRender);
            Assert.True(client.TryTakeQueuedEvent(out var taken));
            AssertSameAsOldPath(sent, taken);
        }

        // An RPC reply goes to the waiting call.
        var call = client.CallAsync("test.echo");
        var request = pair.ReadRequest();
        var reply = "{\"jsonrpc\":\"2.0\",\"id\":" + request.GetProperty("id").GetRawText()
            + ",\"result\":{\"value\":\"echo\",\"n\":7}}\n";
        pair.Send(reorder ? Reorder(reply) : reply);
        var result = await call.WaitAsync(TestWait.HangGuard);
        Assert.Equal("echo", result.GetProperty("value").GetString());
        Assert.Equal(7, result.GetProperty("n").GetInt32());

        // A shell chunk has an id and no pending call. The shell admit takes
        // it, so it does not queue.
        var chunk = """{"jsonrpc":"2.0","id":"shell-9","final_chunk":false,"data":"chunk-a"}""" + "\n";
        pair.Send(reorder ? Reorder(chunk) : chunk);
        Until(() => { lock (shellChunks) return shellChunks.Count == 1; }, "the shell admit did not get the chunk");
        Assert.Equal("chunk-a", shellChunks[0]);

        // A reply that no call and no shell lane takes stays for the render loop.
        var stray = """{"jsonrpc":"2.0","id":"77","result":{"late":true}}""" + "\n";
        if (reorder)
            stray = Reorder(stray);
        pair.Send(stray);
        Until(() => client.ReliableQueueCount == 1, "the stray reply did not queue");
        Assert.True(client.TryTakeQueuedEvent(out var strayEvent));
        AssertSameAsOldPath(stray, strayEvent);

        Assert.Equal(0, client.ReliableQueueCount);
        Assert.Equal(0, client.OrderedQueueCount);
        Assert.False(client.HasLatestRenderEvent);
        Assert.Equal(pool.Rented, pool.Returned);
        Assert.Empty(pool.Faults);
    }

    [SkippableFact]
    public async Task Latest_render_slot_keeps_the_last_of_three_events_and_returns_the_two_replaced_buffers()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket pair");
        var pool = new CountingPool();
        using var pair = SocketPair.Open();
        await using var client = pair.Client(pool);
        var admitted = 0;
        client.SetEventAdmitted(() => Interlocked.Increment(ref admitted));

        pair.Send(
            CellsLine(Cells(generation: 1, baseGeneration: 0, text: "one"))
            + CellsLine(Cells(generation: 2, baseGeneration: 0, text: "two"))
            + CellsLine(Cells(generation: 3, baseGeneration: 0, text: "three")));
        Until(() => Volatile.Read(ref admitted) == 3, "the client did not admit three events");

        Assert.True(client.HasLatestRenderEvent);
        Assert.Equal(0, client.OrderedQueueCount);
        Assert.Equal(3, pool.Rented);
        Assert.Equal(2, pool.Returned);
        Assert.True(client.ReanchorRequired);

        var taken = await client.ReadEventAsync().WaitAsync(TestWait.HangGuard);
        var cells = Payload(taken).Deserialize(ProtocolJsonContext.Default.TerminalRenderCellsPayload)!;
        Assert.Equal(3, cells.Generation);
        Assert.Equal("three", cells.Rows![0].T);
        Assert.False(client.HasLatestRenderEvent);
        Assert.Equal(3, pool.Returned);
        Assert.Empty(pool.Faults);
    }

    [SkippableFact]
    public async Task Closed_connection_with_events_in_each_queue_returns_every_buffer()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket pair");
        var pool = new CountingPool();
        using var pair = SocketPair.Open();
        var client = pair.Client(pool);
        var admitted = 0;
        client.SetEventAdmitted(() => Interlocked.Increment(ref admitted));

        pair.Send(
            ReliableLine(ProtocolEventTypes.LayoutUpdated, """{"tab_id":"t1"}""")
            + ReliableLine(ProtocolEventTypes.LayoutUpdated, """{"tab_id":"t2"}""")
            + CellsLine(Cells(generation: 1, baseGeneration: 0, full: true))
            + CellsLine(Cells(generation: 2, baseGeneration: 1))
            + CellsLine(Cells(generation: 3, baseGeneration: 0)));
        Until(() => Volatile.Read(ref admitted) == 5, "the client did not admit five events");
        Assert.Equal(2, client.ReliableQueueCount);
        Assert.Equal(2, client.OrderedQueueCount);
        Assert.True(client.HasLatestRenderEvent);
        Assert.Equal(5, pool.Outstanding);

        // The peer closes. The queued events stay readable until the client
        // is disposed, so the buffers stay out.
        pair.ClosePeer();
        await Assert.ThrowsAsync<IOException>(() => client.WatchDisconnectAsync().WaitAsync(TestWait.HangGuard));
        Assert.Equal(5, pool.Outstanding);

        await client.DisposeAsync();
        Assert.Equal(0, pool.Outstanding);
        Assert.Equal(5, pool.Returned);
        Assert.Empty(pool.Faults);

        // The reliable events stay readable after the dispose. They hold
        // their own copy and no pool buffer.
        var reliable = client.DrainPendingEvents();
        Assert.Equal(2, reliable.Count);
        Assert.Equal("t1", Payload(reliable[0]).GetProperty("tab_id").GetString());
        Assert.Equal("t2", Payload(reliable[1]).GetProperty("tab_id").GetString());
        Assert.Equal(0, client.OrderedQueueCount);
        Assert.False(client.HasLatestRenderEvent);
        Assert.Empty(pool.Faults);
    }

    [SkippableFact]
    public async Task Events_taken_after_the_peer_closed_return_their_buffers()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket pair");
        var pool = new CountingPool();
        using var pair = SocketPair.Open();
        await using var client = pair.Client(pool);
        var admitted = 0;
        client.SetEventAdmitted(() => Interlocked.Increment(ref admitted));

        pair.Send(
            ReliableLine(ProtocolEventTypes.LayoutUpdated, """{"tab_id":"t1"}""")
            + CellsLine(Cells(generation: 1, baseGeneration: 0, full: true))
            + CellsLine(Cells(generation: 2, baseGeneration: 0)));
        Until(() => Volatile.Read(ref admitted) == 3, "the client did not admit three events");
        pair.ClosePeer();
        await Assert.ThrowsAsync<IOException>(() => client.WatchDisconnectAsync().WaitAsync(TestWait.HangGuard));

        Assert.Equal(3, client.DrainPendingAllEvents().Count);
        await Assert.ThrowsAsync<IOException>(() => client.ReadEventAsync().WaitAsync(TestWait.HangGuard));
        Assert.Equal(0, pool.Outstanding);
        Assert.Empty(pool.Faults);
    }

    [SkippableFact]
    public async Task Line_longer_than_the_limit_fails_the_connection_closed()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket pair");
        var pool = new CountingPool();
        using var pair = SocketPair.Open();
        await using var client = pair.Client(pool);
        var call = client.CallAsync("test.wait");
        _ = pair.ReadRequest();

        var huge = "{\"jsonrpc\":\"2.0\",\"event\":\"x\",\"params\":{\"pad\":\""
            + new string('a', UnixSocketServerOptions.DefaultMaxLineBytes) + "\"}}\n";
        pair.Send(huge);

        await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(TestWait.HangGuard));
        await Assert.ThrowsAsync<IOException>(() => client.WatchDisconnectAsync().WaitAsync(TestWait.HangGuard));
        Assert.True(client.ReanchorRequired);
        Assert.Equal(0, client.ReliableQueueCount);
        Assert.Equal(0, pool.Rented);
        Assert.Empty(pool.Faults);
    }

    [SkippableTheory]
    [InlineData("""{"event":"x","params":{"type":"layout.updated"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""{"event":"x"} {"event":"y"}""")]
    public async Task Line_that_is_not_one_json_object_fails_the_connection_closed_and_returns_its_buffer(string line)
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket pair");
        var pool = new CountingPool();
        using var pair = SocketPair.Open();
        await using var client = pair.Client(pool);

        pair.Send(line + "\n");

        await Assert.ThrowsAsync<IOException>(() => client.WatchDisconnectAsync().WaitAsync(TestWait.HangGuard));
        Assert.True(client.ReanchorRequired);
        Assert.Equal(1, pool.Rented);
        Assert.Equal(0, pool.Outstanding);
        Assert.Empty(pool.Faults);
    }

    [Theory]
    [InlineData("""{"event":"e","params":{"lane":"ordered","payload":{"kind":"cells"}}}""", WriterLaneNames.Ordered)]
    [InlineData("""{"event":"e","params":{"lane":"render","reliability":"reliable"}}""", WriterLaneNames.Render)]
    [InlineData("""{"event":"e","params":{"lane":"control","type":"terminal.render"}}""", WriterLaneNames.Control)]
    // An unknown lane falls through to the other fields.
    [InlineData("""{"event":"e","params":{"lane":"bulk","type":"terminal.render","payload":{"kind":"cells","base_generation":0}}}""", WriterLaneNames.Render)]
    [InlineData("""{"event":"e","params":{"lane":"","type":"terminal.render","payload":{"kind":"cells","base_generation":4}}}""", WriterLaneNames.Ordered)]
    [InlineData("""{"event":"e","params":{"reliability":"output","type":"terminal.render"}}""", WriterLaneNames.Control)]
    [InlineData("""{"event":"e","params":{"reliability":"render","payload":{"kind":"cells","full":true}}}""", WriterLaneNames.Ordered)]
    [InlineData("""{"event":"e","params":{"reliability":"render","payload":{"kind":"cells","full":false,"reanchor":false}}}""", WriterLaneNames.Render)]
    [InlineData("""{"event":"e","params":{"reliability":"render","payload":{"kind":"cells","base_generation":1.5}}}""", WriterLaneNames.Render)]
    // A render line without a payload object stays ordered.
    [InlineData("""{"event":"e","params":{"type":"terminal.render"}}""", WriterLaneNames.Ordered)]
    [InlineData("""{"event":"e","params":{"type":"terminal.render","payload":"x"}}""", WriterLaneNames.Ordered)]
    [InlineData("""{"event":"e","params":{"type":"terminal.render","payload":{"kind":"blit"}}}""", WriterLaneNames.Render)]
    [InlineData("""{"event":"e","params":{"type":"terminal.render","payload":{"kind":"blit","reanchor":true}}}""", WriterLaneNames.Ordered)]
    // A snapshot slice is latest-wins only when it is a complete patch.
    [InlineData("""{"event":"e","params":{"type":"terminal.render","payload":{"kind":"snapshot","patch":true}}}""", WriterLaneNames.Render)]
    [InlineData("""{"event":"e","params":{"type":"terminal.render","payload":{"kind":"snapshot","patch":true,"complete":false}}}""", WriterLaneNames.Ordered)]
    [InlineData("""{"event":"e","params":{"type":"terminal.render","payload":{"kind":"snapshot"}}}""", WriterLaneNames.Ordered)]
    [InlineData("""{"event":"e","params":{"type":"terminal.render","payload":{"kind":"ansi"}}}""", WriterLaneNames.Ordered)]
    [InlineData("""{"event":"e","params":{"type":"layout.updated","payload":{"kind":"cells"}}}""", WriterLaneNames.Control)]
    [InlineData("""{"event":"e","params":"x"}""", WriterLaneNames.Control)]
    [InlineData("""{"event":"e"}""", WriterLaneNames.Control)]
    // A nested field with a known name does not change the result.
    [InlineData("""{"event":"e","params":{"payload":{"lane":"render","params":{"lane":"render"}},"type":"layout.updated"}}""", WriterLaneNames.Control)]
    // Escaped property names and values match.
    [InlineData("""{"event":"e","params":{"type":"terminal.render","payload":{"kind":"cells","base_generation":0}}}""", WriterLaneNames.Render)]
    public void Fields_select_the_lane_from_the_line_bytes(string line, string lane)
    {
        Assert.Equal(lane, ReceiveLineFields.Read(Encoding.UTF8.GetBytes(line)).Lane);
        Assert.Equal(lane, ReceiveLineFields.Read(Encoding.UTF8.GetBytes(Reorder(line))).Lane);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":"12","result":{"id":"inner"}}""", "12")]
    [InlineData("""{"result":{"id":5},"id":42}""", "42")]
    [InlineData("""{"id":-3.50,"result":{}}""", "-3.50")]
    [InlineData("""{"id":null,"result":{}}""", null)]
    [InlineData("""{"id":{"a":1},"result":{}}""", null)]
    [InlineData("""{"result":{"id":"nested"}}""", null)]
    public void Fields_read_only_the_top_level_id(string line, string? id)
    {
        var fields = ReceiveLineFields.Read(Encoding.UTF8.GetBytes(line));
        Assert.Equal(id, fields.Id);
        Assert.False(fields.HasEvent);
    }

    [Fact]
    public void One_200x50_cells_event_allocates_less_from_the_read_to_the_typed_object()
    {
        var payload = Cells200x50();
        var line = AttachCellsLinePacker.FormatLineUtf8(payload, "sub_local");
        const int lines = 24;
        var stream = new MemoryStream();
        for (var i = 0; i < lines; i++)
        {
            stream.Write(line);
            stream.WriteByte((byte)'\n');
        }

        var oldReader = new BoundedNdjsonLineReader(new MemoryStream(stream.ToArray()), UnixSocketServerOptions.DefaultMaxLineBytes);
        var newReader = new BoundedNdjsonLineReader(new MemoryStream(stream.ToArray()), UnixSocketServerOptions.DefaultMaxLineBytes);
        var pool = ArrayPool<byte>.Create(UnixSocketServerOptions.DefaultMaxLineBytes, 4);

        // Before: the reader made a string, the read loop made bytes from
        // it and parsed them to select the queue, and the take parsed them
        // again and cloned the root.
        TerminalRenderCellsPayload? OldPath()
        {
            var read = oldReader.ReadLineAsync().GetAwaiter().GetResult();
            var utf8 = Encoding.UTF8.GetBytes(read.Line!);
            using (var classify = JsonDocument.Parse(utf8))
                _ = classify.RootElement.TryGetProperty("event", out _);
            JsonElement root;
            using (var doc = JsonDocument.Parse(utf8))
                root = doc.RootElement.Clone();
            return Payload(root).Deserialize(ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        }

        // After: pooled bytes, one field pass, one parse at the take.
        TerminalRenderCellsPayload? NewPath()
        {
            var read = newReader.ReadLineUtf8Async(pool).GetAwaiter().GetResult();
            var rented = read.Line!;
            var fields = ReceiveLineFields.Read(rented.Span);
            var ev = ClientEvent.FromLine(rented, fields, rented.Length, receivedAt: 1);
            var root = ev.Materialize();
            return Payload(root).Deserialize(ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        }

        var expected = JsonSerializer.Serialize(payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        Assert.Equal(expected, JsonSerializer.Serialize(OldPath(), ProtocolJsonContext.Default.TerminalRenderCellsPayload));
        Assert.Equal(expected, JsonSerializer.Serialize(NewPath(), ProtocolJsonContext.Default.TerminalRenderCellsPayload));

        var oldBytes = MinAllocated(() => OldPath(), lines - 1);
        var newBytes = MinAllocated(() => NewPath(), lines - 1);
        _output.WriteLine($"line bytes {line.Length}; read to typed object: old {oldBytes} B, new {newBytes} B");
        Assert.True(newBytes < oldBytes, $"new path allocated {newBytes} B, old path {oldBytes} B");
    }

    private static long MinAllocated(Action action, int runs)
    {
        var min = long.MaxValue;
        for (var i = 0; i < runs; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return min;
    }

    private static void AssertQueuedIn(ControlPlaneClient client, Queue queue)
    {
        Assert.Equal(queue == Queue.Reliable ? 1 : 0, client.ReliableQueueCount);
        Assert.Equal(queue == Queue.Ordered ? 1 : 0, client.OrderedQueueCount);
        Assert.Equal(queue == Queue.LatestRender, client.HasLatestRenderEvent);
    }

    /// <summary>
    /// The old path: a string line, its bytes, one parse and a clone. The
    /// event and its typed cells body must be equal.
    /// </summary>
    private static void AssertSameAsOldPath(string line, JsonElement taken)
    {
        JsonElement old;
        using (var doc = JsonDocument.Parse(Encoding.UTF8.GetBytes(line.TrimEnd('\n'))))
            old = doc.RootElement.Clone();
        Assert.Equal(old.GetRawText(), taken.GetRawText());
        if (!old.TryGetProperty("params", out var p)
            || !p.TryGetProperty("payload", out var payload)
            || !payload.TryGetProperty("kind", out var kind)
            || kind.GetString() != TerminalRenderCellsPayload.KindCells)
        {
            return;
        }

        var info = ProtocolJsonContext.Default.TerminalRenderCellsPayload;
        Assert.Equal(
            JsonSerializer.Serialize(payload.Deserialize(info), info),
            JsonSerializer.Serialize(Payload(taken).Deserialize(info), info));
    }

    private static JsonElement Payload(JsonElement ev) => ev.GetProperty("params").GetProperty("payload");

    private static TerminalRenderCellsPayload Cells(
        long generation,
        long baseGeneration,
        bool full = false,
        bool reanchor = false,
        string? text = null) =>
        new()
        {
            PaneId = PaneId,
            Kind = TerminalRenderCellsPayload.KindCells,
            Full = full,
            Reanchor = reanchor,
            GridCols = 20,
            GridRows = 5,
            Generation = generation,
            BaseGeneration = baseGeneration,
            Rows =
            [
                new TerminalRenderCellRow
                {
                    I = 0,
                    T = text ?? "gen " + generation,
                    S = [new TerminalRenderStyleRun { C = 0, N = 3, Fg = 0xff0000, Bold = true }],
                },
            ],
            Cursor = new TerminalRenderCursorPayload { Col = 3, Row = 0, Visible = true },
        };

    private static TerminalRenderCellsPayload Cells200x50()
    {
        var rows = new TerminalRenderCellRow[50];
        for (var i = 0; i < rows.Length; i++)
        {
            var text = new StringBuilder(200);
            for (var c = 0; c < 200; c++)
                text.Append((char)('a' + ((i + c) % 26)));
            rows[i] = new TerminalRenderCellRow
            {
                I = i,
                T = text.ToString(),
                S =
                [
                    new TerminalRenderStyleRun { C = 0, N = 12, Fg = 0x00ff00 },
                    new TerminalRenderStyleRun { C = 40, N = 30, Bg = 0x202020, Bold = true },
                    new TerminalRenderStyleRun { C = 120, N = 8, Fg = 0xff8800, Underline = true },
                ],
            };
        }

        return new TerminalRenderCellsPayload
        {
            PaneId = PaneId,
            Kind = TerminalRenderCellsPayload.KindCells,
            Full = true,
            GridCols = 200,
            GridRows = 50,
            Generation = 9,
            Rows = rows,
            Cursor = new TerminalRenderCursorPayload { Col = 10, Row = 49, Visible = true },
        };
    }

    private static string CellsLine(TerminalRenderCellsPayload payload) =>
        Encoding.UTF8.GetString(AttachCellsLinePacker.FormatLineUtf8(payload, "sub_local")) + "\n";

    private static long _seq;

    private static string ReliableLine(string type, string payload)
    {
        var record = new RuntimeEventRecord
        {
            Seq = Interlocked.Increment(ref _seq),
            Class = EventClass.Lifecycle,
            Reliability = EventReliability.Reliable,
            Type = type,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = payload,
        };
        return Encoding.UTF8.GetString(EventSubscriptionHub.FormatRuntimeEventUtf8(record, "sub_local")) + "\n";
    }

    private static string WithoutLane(string line)
    {
        var node = JsonNode.Parse(line)!.AsObject();
        var parms = node["params"]!.AsObject();
        Assert.True(parms.Remove("lane"), "the line had no lane");
        return node.ToJsonString() + "\n";
    }

    /// <summary>The same JSON with the properties of each object in reverse order.</summary>
    private static string Reorder(string line)
    {
        var trailing = line.EndsWith('\n') ? "\n" : "";
        return Reverse(JsonNode.Parse(line)!)!.ToJsonString() + trailing;
    }

    private static JsonNode? Reverse(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Reverse().Select(p => KeyValuePair.Create(p.Key, Reverse(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Reverse).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };

    // A poll on a test counter. The limit only stops a hung run.
    private static void Until(Func<bool> condition, string message) =>
        Assert.True(SpinWait.SpinUntil(condition, TestWait.HangGuard), message);

    /// <summary>
    /// An array pool that counts the buffers. A return of a buffer that is
    /// not out, or a second return of one buffer, is a fault.
    /// </summary>
    private sealed class CountingPool : ArrayPool<byte>
    {
        private readonly object _gate = new();
        private readonly HashSet<byte[]> _out = new(ReferenceEqualityComparer.Instance);
        private readonly List<string> _faults = [];
        private int _rented;
        private int _returned;

        public int Rented { get { lock (_gate) return _rented; } }

        public int Returned { get { lock (_gate) return _returned; } }

        public int Outstanding { get { lock (_gate) return _out.Count; } }

        public IReadOnlyList<string> Faults { get { lock (_gate) return [.. _faults]; } }

        public override byte[] Rent(int minimumLength)
        {
            var buffer = new byte[minimumLength];
            lock (_gate)
            {
                _out.Add(buffer);
                _rented++;
            }

            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            lock (_gate)
            {
                if (!_out.Remove(array))
                {
                    _faults.Add("returned a buffer that was not out");
                    return;
                }

                _returned++;
            }
        }
    }

    /// <summary>A connected Unix socket pair. The far end plays the mux.</summary>
    private sealed class SocketPair : IDisposable
    {
        private readonly string _path;
        private readonly NetworkStream _client;
        private readonly NetworkStream _peer;
        private readonly StreamReader _requests;

        private SocketPair(string path, NetworkStream client, NetworkStream peer)
        {
            _path = path;
            _client = client;
            _peer = peer;
            _requests = new StreamReader(peer, leaveOpen: true);
        }

        public static SocketPair Open()
        {
            // A short path: the Unix socket path limit is about 100 bytes.
            var path = "/tmp/hypa-recv-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            var peer = listener.Accept();
            return new SocketPair(path, new NetworkStream(client, ownsSocket: true), new NetworkStream(peer, ownsSocket: true));
        }

        public ControlPlaneClient Client(ArrayPool<byte> pool)
        {
            var client = ControlPlaneClient.FromConnectedStream(_client, pool, callTimeout: TestWait.HangGuard);
            client.ConnectAsync().GetAwaiter().GetResult();
            return client;
        }

        public void Send(string lines)
        {
            _peer.Write(Encoding.UTF8.GetBytes(lines));
            _peer.Flush();
        }

        public JsonElement ReadRequest()
        {
            var line = _requests.ReadLine() ?? throw new IOException("no request");
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }

        public void ClosePeer() => _peer.Socket.Shutdown(SocketShutdown.Send);

        public void Dispose()
        {
            _requests.Dispose();
            _peer.Dispose();
            _client.Dispose();
            try { File.Delete(_path); }
            catch (IOException) { }
        }
    }
}
