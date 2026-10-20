using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The attach receive rules of the client queue: the focused echo first,
/// a byte limit for the unpainted deltas of a pane without the focus, and
/// one queued scroll event for each pane.
/// </summary>
public sealed class AttachReceiveQueueTests
{
    private const string Focused = "p1";
    private const string Busy = "p2";

    [SkippableFact]
    public async Task Two_seconds_of_unfocused_deltas_stay_under_the_limit_and_the_echo_goes_first()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket peer");
        using var peer = UnixPeer.Listen();
        await using var client = peer.ConnectClient();
        peer.Respond();
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.RenderSub = "sub_local";
        AttachSession.ApplyAttachReceivePolicy(live, client);
        var limit = client.AttachReceivePolicy!.UnfocusedPaneDeltaBytes;
        Assert.True(limit > 0, "a local connection has the limit");

        // 2 s of a busy pane at 60 frames each second, as the mux sends them,
        // plus its scroll events at 10 each second. The render loop takes
        // nothing during this time.
        var lines = new StringBuilder();
        var sent = 0;
        lines.Append(Line(Cells(Busy, generation: 1, baseGeneration: 0, full: true)));
        sent++;
        for (var frame = 1; frame <= 120; frame++)
        {
            lines.Append(Line(Cells(Busy, generation: frame + 1, baseGeneration: frame)));
            sent++;
            if (frame % 6 == 0)
            {
                lines.Append(ScrollLine(Busy, offset: 0, maxOffset: 1000 + frame));
                sent++;
            }
        }

        // The echo of a key in the focused pane comes last.
        lines.Append(Line(Cells(Focused, generation: 8, baseGeneration: 7, text: "echo")));
        sent++;
        peer.Send(lines.ToString());
        Until(() => client.LinesReceived >= sent, "the client did not read every line");

        Assert.True(client.PaneQueuedDeltaBytes(Busy) <= limit,
            $"unpainted deltas {client.PaneQueuedDeltaBytes(Busy)} passed the limit {limit}");
        Assert.True(client.OrderedQueuedBytes <= limit + 64 * 1024,
            $"the ordered lane holds {client.OrderedQueuedBytes} bytes");
        Assert.True(client.DroppedPaneDeltas > 0);

        // The drop itself starts one reanchor request for the busy pane, sent
        // as terminal.observe. The render loop read no event.
        Until(
            () => peer.Requests().Any(r => r.Method == ProtocolMethods.TerminalObserve),
            "the drop did not ask for a reanchor");
        await AttachSession.StartPaneReanchors(client, live, CancellationToken.None);
        // The peer answers in order. Its reply to this call comes after any
        // request that the client wrote before it.
        await client.CallAsync("test.barrier");
        var observes = peer.Requests().Where(r => r.Method == ProtocolMethods.TerminalObserve).ToList();
        var observe = Assert.Single(observes);
        Assert.Equal(Busy, observe.PaneId);

        Assert.True(client.TryTakeQueuedEvent(out var first));
        Assert.Equal(Focused, PaneOf(first));
        Assert.Equal("cells", Payload(first).GetProperty("kind").GetString());
    }

    [SkippableFact]
    public async Task Failed_reanchor_request_keeps_the_drop_and_asks_again()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket peer");
        using var peer = UnixPeer.Listen();
        await using var client = peer.ConnectClient();
        peer.Respond(failFirstObserves: 1);
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.RenderSub = "sub_local";
        client.SetAttachReceivePolicy(new AttachReceivePolicy { FocusedPane = () => Focused });
        var generation = 1;
        Admit(client, Line(Cells(Busy, generation: 1, baseGeneration: 0, full: true)));
        while (client.DroppedPaneDeltas == 0)
        {
            Admit(client, Line(Cells(Busy, generation: generation + 1, baseGeneration: generation)));
            generation++;
        }

        var time = new ManualTimeProvider();
        var requests = AttachSession.StartPaneReanchors(client, live, CancellationToken.None, time);
        Until(() => peer.Requests().Count(r => r.Method == ProtocolMethods.TerminalObserve) == 1, "no first request");

        // The mux refused the request. Deltas that chain on dropped frames still drop.
        await time.WaitForArmedTimerAsync();
        var dropped = client.DroppedPaneDeltas;
        Admit(client, Line(Cells(Busy, generation: generation + 1, baseGeneration: generation)));
        Assert.Equal(dropped + 1, client.DroppedPaneDeltas);
        Assert.Equal(0, client.OrderedQueueCount);

        time.Advance(AttachSession.PaneReanchorRetryDelay);
        await requests.WaitAsync(TestWait.HangGuard);
        Assert.Equal(2, peer.Requests().Count(r => r.Method == ProtocolMethods.TerminalObserve));
    }

    [Fact]
    public void Dropped_pane_takes_no_delta_until_its_full_frame_and_its_chain_stays_whole()
    {
        var client = NewClient();
        var limit = client.AttachReceivePolicy!.UnfocusedPaneDeltaBytes;
        Admit(client, Line(Cells(Busy, generation: 1, baseGeneration: 0, full: true)));
        var generation = 1;
        while (client.DroppedPaneDeltas == 0)
        {
            Admit(client, Line(Cells(Busy, generation: generation + 1, baseGeneration: generation)));
            generation++;
            Assert.True(client.PaneQueuedDeltaBytes(Busy) <= limit);
        }

        Assert.Equal(new[] { Busy }, client.TakePaneReanchorRequests());
        Assert.Equal(0, client.PaneQueuedDeltaBytes(Busy));

        // Deltas that chain on frames the client dropped do not queue.
        for (var i = 0; i < 5; i++)
        {
            Admit(client, Line(Cells(Busy, generation: generation + 1, baseGeneration: generation)));
            generation++;
        }

        Assert.Equal(0, client.OrderedQueueCount);

        // The reanchor frame comes, and the deltas after it chain on it.
        Admit(client, Line(Cells(Busy, generation: 500, baseGeneration: 0, full: true, reanchor: true)));
        Admit(client, Line(Cells(Busy, generation: 501, baseGeneration: 500)));
        Admit(client, Line(Cells(Busy, generation: 502, baseGeneration: 501)));

        var applied = 0L;
        var taken = 0;
        while (client.TryTakeQueuedEvent(out var ev))
        {
            var payload = Payload(ev);
            var full = payload.TryGetProperty("full", out var f) && f.GetBoolean();
            var baseGeneration = payload.TryGetProperty("base_generation", out var b) ? b.GetInt64() : 0;
            Assert.True(full || baseGeneration == applied,
                $"delta on {baseGeneration} after {applied} breaks the chain");
            applied = payload.GetProperty("generation").GetInt64();
            taken++;
        }

        Assert.Equal(3, taken);
        Assert.Equal(502, applied);
        Assert.Empty(client.TakePaneReanchorRequests());
    }

    [Fact]
    public void Full_frame_of_an_unfocused_pane_replaces_its_queued_frames_without_a_reanchor()
    {
        var client = NewClient();
        Admit(client, Line(Cells(Busy, generation: 1, baseGeneration: 0, full: true)));
        Admit(client, Line(Cells(Busy, generation: 2, baseGeneration: 1)));
        Admit(client, Line(Cells(Busy, generation: 3, baseGeneration: 2)));
        Admit(client, Line(Cells(Busy, generation: 9, baseGeneration: 0, full: true)));

        Assert.Equal(1, client.OrderedQueueCount);
        Assert.Empty(client.TakePaneReanchorRequests());
        Assert.True(client.TryTakeQueuedEvent(out var ev));
        Assert.Equal(9, Payload(ev).GetProperty("generation").GetInt64());
    }

    [Fact]
    public void Full_frame_of_an_unfocused_pane_frees_its_bytes_before_the_budget_check()
    {
        var frame = Encoding.UTF8.GetByteCount(Line(Cells(Busy, generation: 2, baseGeneration: 1)));
        var client = new ControlPlaneClient(
            "/tmp/hypa-receive-budget.sock",
            connectTimeout: null,
            callTimeout: null,
            maxOrderedBytes: frame * 3 + frame / 2);
        // No delta limit: only the byte budget of the lane applies.
        client.SetAttachReceivePolicy(new AttachReceivePolicy
        {
            FocusedPane = () => Focused,
            UnfocusedPaneDeltaBytes = 0,
        });
        Admit(client, Line(Cells(Busy, generation: 1, baseGeneration: 0, full: true)));
        Admit(client, Line(Cells(Busy, generation: 2, baseGeneration: 1)));
        Admit(client, Line(Cells(Busy, generation: 3, baseGeneration: 2)));

        // The queue holds three frames. A fourth does not fit. The full frame replaces them,
        // so it fits, and the connection stays open.
        Admit(client, Line(Cells(Busy, generation: 9, baseGeneration: 0, full: true)));
        Admit(client, Line(Cells(Busy, generation: 10, baseGeneration: 9)));

        Assert.Equal(2, client.OrderedQueueCount);
        Assert.False(client.IsTransportClosed);
    }

    [Fact]
    public void Focused_echo_passes_other_pane_frames_and_scrolls_but_waits_for_an_earlier_layout_event()
    {
        var client = NewClient();
        Admit(client, Line(Cells(Busy, generation: 2, baseGeneration: 1)));
        Admit(client, ScrollLine(Busy, 0, 100));
        Admit(client, Line(Cells(Focused, generation: 5, baseGeneration: 4, text: "a")));

        Assert.True(client.TryTakeQueuedEvent(out var first));
        Assert.Equal(Focused, PaneOf(first));

        Admit(client, LayoutLine());
        Admit(client, Line(Cells(Focused, generation: 6, baseGeneration: 5, text: "b")));
        // A layout event that came later does not delay the echo.
        Admit(client, LayoutLine());

        Assert.True(client.TryTakeQueuedEvent(out var second));
        Assert.Equal(ProtocolEventTypes.PaneScrollChanged, TypeOf(second));
        Assert.True(client.TryTakeQueuedEvent(out var third));
        Assert.Equal(ProtocolEventTypes.LayoutUpdated, TypeOf(third));
        Assert.True(client.TryTakeQueuedEvent(out var fourth));
        Assert.Equal(Focused, PaneOf(fourth));
        Assert.Equal(6, Payload(fourth).GetProperty("generation").GetInt64());
    }

    [Fact]
    public void Status_event_of_another_pane_does_not_delay_the_echo()
    {
        var client = NewClient();
        Admit(client, ReliableLine(
            ProtocolEventTypes.PaneAgentStatusChanged,
            "{\"pane_id\":\"" + Busy + "\",\"status\":\"working\"}"));
        Admit(client, Line(Cells(Focused, generation: 5, baseGeneration: 4, text: "a")));

        Assert.True(client.TryTakeQueuedEvent(out var first));
        Assert.Equal(Focused, PaneOf(first));

        // A status event of the focused pane itself stays in order.
        Admit(client, ReliableLine(
            ProtocolEventTypes.PaneAgentStatusChanged,
            "{\"pane_id\":\"" + Focused + "\",\"status\":\"idle\"}"));
        Admit(client, Line(Cells(Focused, generation: 6, baseGeneration: 5, text: "b")));
        Assert.True(client.TryTakeQueuedEvent(out var second));
        Assert.Equal(ProtocolEventTypes.PaneAgentStatusChanged, TypeOf(second));
    }

    [Fact]
    public void Policy_set_after_frames_were_queued_keeps_the_chain_of_the_focused_pane()
    {
        var client = new ControlPlaneClient("/tmp/hypa-receive-late.sock");
        Admit(client, Line(Cells(Focused, generation: 1, baseGeneration: 0, full: true)));
        Admit(client, ScrollLine(Busy, 0, 10));
        Admit(client, ScrollLine(Busy, 0, 11));
        client.SetAttachReceivePolicy(new AttachReceivePolicy { FocusedPane = () => Focused });
        Admit(client, Line(Cells(Focused, generation: 2, baseGeneration: 1, text: "a")));

        var generations = new List<long>();
        var scrolls = 0;
        while (client.TryTakeQueuedEvent(out var ev))
        {
            if (TypeOf(ev) == ProtocolEventTypes.PaneScrollChanged)
                scrolls++;
            else
                generations.Add(Payload(ev).GetProperty("generation").GetInt64());
        }

        Assert.Equal(new long[] { 1, 2 }, generations);
        Assert.Equal(1, scrolls);
    }

    [Fact]
    public void Policy_set_after_a_backlog_drops_the_unfocused_pane_deltas_and_asks_once_for_a_reanchor()
    {
        var client = new ControlPlaneClient("/tmp/hypa-receive-backlog.sock");
        var requests = 0;
        Admit(client, Line(Cells(Focused, generation: 1, baseGeneration: 0, full: true)));
        Admit(client, Line(Cells(Busy, generation: 1, baseGeneration: 0, full: true)));
        var generation = 1;
        // More than the default limit of the unfocused pane, queued before
        // the attach set its rules.
        while (generation < 60)
        {
            Admit(client, Line(Cells(Busy, generation: generation + 1, baseGeneration: generation)));
            generation++;
        }

        client.SetAttachReceivePolicy(new AttachReceivePolicy
        {
            FocusedPane = () => Focused,
            ReanchorRequested = () => Interlocked.Increment(ref requests),
        });

        Assert.Equal(1, requests);
        Assert.Equal(0, client.PaneQueuedDeltaBytes(Busy));
        Assert.Equal(new[] { Busy }, client.TakePaneReanchorRequests());

        // A delta that chains on a dropped frame does not queue.
        Admit(client, Line(Cells(Busy, generation: generation + 1, baseGeneration: generation)));
        Admit(client, Line(Cells(Focused, generation: 2, baseGeneration: 1, text: "a")));

        var focused = new List<long>();
        var busy = 0;
        while (client.TryTakeQueuedEvent(out var ev))
        {
            if (PaneOf(ev) == Focused)
                focused.Add(Payload(ev).GetProperty("generation").GetInt64());
            else
                busy++;
        }

        Assert.Equal(new long[] { 1, 2 }, focused);
        Assert.Equal(0, busy);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void One_large_delta_on_an_empty_lane_queues_and_a_second_starts_the_drop()
    {
        var client = new ControlPlaneClient("/tmp/hypa-receive-large.sock");
        var delta = Encoding.UTF8.GetByteCount(Line(Cells(Busy, generation: 2, baseGeneration: 1)));
        client.SetAttachReceivePolicy(new AttachReceivePolicy
        {
            FocusedPane = () => Focused,
            // A limit below the size of one delta, as a large slice after a
            // reanchor can be.
            UnfocusedPaneDeltaBytes = delta / 2,
        });
        Admit(client, Line(Cells(Busy, generation: 1, baseGeneration: 0, full: true)));
        Admit(client, Line(Cells(Busy, generation: 2, baseGeneration: 1)));

        Assert.Equal(2, client.OrderedQueueCount);
        Assert.Empty(client.TakePaneReanchorRequests());

        Admit(client, Line(Cells(Busy, generation: 3, baseGeneration: 2)));

        Assert.Equal(new[] { Busy }, client.TakePaneReanchorRequests());
        Assert.Equal(0, client.PaneQueuedDeltaBytes(Busy));
    }

    [Fact]
    public void Burst_of_scroll_events_collapses_to_the_latest_value_of_each_pane()
    {
        var client = NewClient();
        for (var i = 1; i <= 50; i++)
        {
            Admit(client, ScrollLine(Busy, 0, 1000 + i));
            Admit(client, ScrollLine(Focused, i, 200));
            if (i == 25)
                Admit(client, LayoutLine());
        }

        Admit(client, LayoutLine());

        var taken = new List<JsonElement>();
        while (client.TryTakeQueuedEvent(out var ev))
            taken.Add(ev);

        Assert.Equal(98, client.CoalescedScrollEvents);
        Assert.Equal(
            new string?[]
            {
                ProtocolEventTypes.LayoutUpdated,
                ProtocolEventTypes.PaneScrollChanged,
                ProtocolEventTypes.PaneScrollChanged,
                ProtocolEventTypes.LayoutUpdated,
            },
            taken.Select(TypeOf).ToArray());
        Assert.Equal(1050, Payload(taken[1]).GetProperty("max_offset").GetInt32());
        Assert.Equal(Busy, PaneOf(taken[1]));
        Assert.Equal(50, Payload(taken[2]).GetProperty("offset").GetInt32());
        Assert.Equal(Focused, PaneOf(taken[2]));
    }

    [Fact]
    public void Client_without_the_attach_policy_keeps_every_scroll_event()
    {
        var client = new ControlPlaneClient("/tmp/hypa-receive-plain.sock");
        for (var i = 1; i <= 10; i++)
            Admit(client, ScrollLine(Busy, 0, 1000 + i));

        Assert.Equal(10, client.DrainPendingEvents().Count);
        Assert.Equal(0, client.CoalescedScrollEvents);
    }

    private static ControlPlaneClient NewClient()
    {
        var client = new ControlPlaneClient("/tmp/hypa-receive-policy.sock");
        client.SetAttachReceivePolicy(new AttachReceivePolicy { FocusedPane = () => Focused });
        return client;
    }

    private static void Admit(ControlPlaneClient client, string line)
    {
        var utf8 = Encoding.UTF8.GetBytes(line.TrimEnd('\n'));
        Assert.True(client.AdmitEventUtf8(utf8, utf8.Length));
    }

    private static TerminalRenderCellsPayload Cells(
        string paneId,
        long generation,
        long baseGeneration,
        bool full = false,
        bool reanchor = false,
        string? text = null)
    {
        // A busy pane changes about 40 rows of 100 cells in each frame.
        var rows = new List<TerminalRenderCellRow>();
        var count = text is null ? 40 : 1;
        for (var i = 0; i < count; i++)
        {
            rows.Add(new TerminalRenderCellRow
            {
                I = i,
                T = text ?? new string((char)('a' + (int)((generation + i) % 26)), 100),
            });
        }

        return new TerminalRenderCellsPayload
        {
            PaneId = paneId,
            Kind = TerminalRenderCellsPayload.KindCells,
            Full = full,
            Reanchor = reanchor,
            GridCols = 100,
            GridRows = 50,
            Generation = generation,
            BaseGeneration = baseGeneration,
            Rows = rows,
        };
    }

    private static string Line(TerminalRenderCellsPayload payload) =>
        Encoding.UTF8.GetString(AttachCellsLinePacker.FormatLineUtf8(payload, "sub_local")) + "\n";

    private static long _seq;

    private static string ScrollLine(string paneId, int offset, int maxOffset) =>
        ReliableLine(
            ProtocolEventTypes.PaneScrollChanged,
            RuntimeEventPayloadJson.WritePaneScrollChanged(paneId, offset, maxOffset));

    private static string LayoutLine() =>
        ReliableLine(ProtocolEventTypes.LayoutUpdated, """{"tab_id":"t1"}""");

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

    private static JsonElement Payload(JsonElement ev) => ev.GetProperty("params").GetProperty("payload");

    private static string? PaneOf(JsonElement ev) =>
        Payload(ev).TryGetProperty("pane_id", out var pane) ? pane.GetString() : null;

    private static string? TypeOf(JsonElement ev) => ev.GetProperty("params").GetProperty("type").GetString();

    // A poll on a test counter. The limit only stops a hung run.
    private static void Until(Func<bool> condition, string message) =>
        Assert.True(SpinWait.SpinUntil(condition, TestWait.HangGuard), message);

    private sealed record PeerRequest(string Method, string? PaneId);

    /// <summary>A Unix socket peer that answers each request and records it.</summary>
    private sealed class UnixPeer : IDisposable
    {
        private readonly Socket _listener;
        private readonly List<PeerRequest> _requests = [];
        private NetworkStream? _stream;

        private UnixPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        private string Path { get; }

        internal static UnixPeer Listen()
        {
            var path = "/tmp/hypa-receive-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            return new UnixPeer(listener, path);
        }

        internal ControlPlaneClient ConnectClient()
        {
            var client = new ControlPlaneClient(Path, connectTimeout: TestWait.HangGuard, callTimeout: TestWait.HangGuard);
            var accept = _listener.AcceptAsync();
            client.ConnectAsync().GetAwaiter().GetResult();
            _stream = new NetworkStream(accept.GetAwaiter().GetResult(), ownsSocket: true);
            return client;
        }

        internal List<PeerRequest> Requests()
        {
            lock (_requests)
                return [.. _requests];
        }

        internal void Respond(int failFirstObserves = 0)
        {
            var stream = _stream!;
            var failures = failFirstObserves;
            _ = Task.Run(async () =>
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                try
                {
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (!doc.RootElement.TryGetProperty("id", out var id))
                            continue;
                        string? pane = null;
                        if (doc.RootElement.TryGetProperty("params", out var p)
                            && p.ValueKind == JsonValueKind.Object
                            && p.TryGetProperty("pane_id", out var paneEl))
                        {
                            pane = paneEl.GetString();
                        }

                        var method = doc.RootElement.GetProperty("method").GetString()!;
                        lock (_requests)
                            _requests.Add(new PeerRequest(method, pane));
                        var reply = "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":{}}\n";
                        if (method == ProtocolMethods.TerminalObserve && failures-- > 0)
                        {
                            reply = "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText()
                                + ",\"error\":{\"code\":-32005,\"message\":\"busy\"}}\n";
                        }
                        lock (stream)
                            stream.Write(Encoding.UTF8.GetBytes(reply));
                    }
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            });
        }

        internal void Send(string lines)
        {
            var stream = _stream!;
            lock (stream)
            {
                stream.Write(Encoding.UTF8.GetBytes(lines));
                stream.Flush();
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _listener.Dispose();
            try { File.Delete(Path); }
            catch (IOException) { }
        }
    }
}
