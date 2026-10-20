using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Settings;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The attach client has one control gate. A heartbeat or a lease renew can
/// hold it across a round trip. A plain key and an echo paint have no work
/// for the gate, so they must not wait for it. Work that the gate serializes
/// must still wait.
/// </summary>
/// <remarks>
/// The fast-path tests hold the gate for the whole check and assert that the
/// work completes while the gate is still held. That is deterministic. A
/// wall-clock bound would be flaky on a loaded host.
/// <para>
/// The gated tests need no clock either. A gated method runs on the caller
/// thread up to its first await, and that await is the gate. So when the
/// call returns an open task, the method waits for the gate, and it can do
/// nothing more until the test releases the gate.
/// </para>
/// </remarks>
public sealed class AttachControlGateFastPathTests
{
    // No chrome text contains this run of letters.
    private const string EchoMarker = "zqxj";

    [Fact]
    public async Task Key_chunk_without_theme_fields_does_not_wait_for_the_control_gate()
    {
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var port = new MouseRecordingPort();
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();

        var apply = AttachSession.ApplyDecodedHostThemeAsync(
            live, default, port, gate, tty: null, CancellationToken.None);

        await apply.WaitAsync(TestWait.HangGuard);
        Assert.Equal(0, gate.CurrentCount);
        Assert.Empty(port.Calls);
        gate.Release();
    }

    [Fact]
    public async Task Decode_with_only_reply_counters_does_not_wait_for_the_control_gate()
    {
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var port = new MouseRecordingPort();
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();
        var decode = new HostThemeDecode { OscDropped = true, ColorReports = 2, HostStatusReply = true };

        await AttachSession.ApplyDecodedHostThemeAsync(
                live, decode, port, gate, tty: null, CancellationToken.None)
            .WaitAsync(TestWait.HangGuard);

        Assert.Equal(0, gate.CurrentCount);
        Assert.Empty(port.Calls);
        gate.Release();
    }

    [Fact]
    public async Task Theme_chunk_waits_for_the_control_gate()
    {
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var port = new MouseRecordingPort();
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();
        var decode = new HostThemeDecode { Background = new HostRgb(4, 5, 6) };

        var apply = AttachSession.ApplyDecodedHostThemeAsync(
            live, decode, port, gate, tty: null, CancellationToken.None);

        Assert.False(apply.IsCompleted);
        Assert.Empty(port.Calls);

        gate.Release();
        await apply.WaitAsync(TestWait.HangGuard);
        var call = Assert.Single(port.Calls);
        Assert.Equal(ProtocolMethods.ClientHostThemeSet, call.Method);
        Assert.Equal(new HostRgb(4, 5, 6), live.HostTheme.Background);
    }

    [Fact]
    public async Task Palette_only_chunk_waits_for_the_control_gate()
    {
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var port = new MouseRecordingPort();
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();
        var decode = new HostThemeDecode { Palette = default(HostPalette).WithColor(3, new HostRgb(7, 8, 9)) };

        var apply = AttachSession.ApplyDecodedHostThemeAsync(
            live, decode, port, gate, tty: null, CancellationToken.None);

        Assert.False(apply.IsCompleted);

        gate.Release();
        await apply.WaitAsync(TestWait.HangGuard);
        Assert.Equal(ProtocolMethods.ClientHostThemeSet, Assert.Single(port.Calls).Method);
        Assert.Equal(new HostRgb(7, 8, 9), live.HostTheme.Palette[3]);
    }

    [Fact]
    public async Task Plain_key_reaches_the_input_sender_while_the_control_gate_is_held()
    {
        using var tty = new UnixRawTerminal(new MemoryStream(), 80, 24);
        using var linked = new CancellationTokenSource();
        using var attempt = new CancellationTokenSource();
        using var gate = new SemaphoreSlim(1, 1);
        var stdin = new ChannelAttachStdin();
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        var client = new ControlPlaneClient("/tmp/hypa-gate-fast-key.sock");
        await using var sender = new AttachInputSender(client, () => (live.PaneId, live.InputLease))
        {
            BlockNotifyForTests = true,
        };
        live.InputSender = sender;
        Assert.True(live.HasLivePane);
        Assert.False(AttachSession.BlocksPaneKeys(live));

        var input = new AttachSession().ReadInputForTests(
            tty, live, client, gate, linked, stdin, attempt.Token);
        try
        {
            await gate.WaitAsync();
            stdin.Push([0x20]);

            Assert.True(
                await sender.NotifyEnteredForTests.WaitAsync(TestWait.HangGuard),
                "the key did not reach the input sender while the control gate was held");
            Assert.Equal(0, gate.CurrentCount);
            Assert.False(input.IsCompleted);
        }
        finally
        {
            gate.Release();
            attempt.Cancel();
            sender.NotifyHoldForTests.Release();
            await Drain(input);
        }
    }

    [SkippableFact]
    public async Task Cells_echo_paints_while_the_control_gate_is_held()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        using var peer = EventPeer.Listen();
        await using var reader = await peer.ConnectAsync();
        var live = MouseTestGeom.ApplyLive(new MouseRecordingPort(), MouseTestGeom.Split());
        Assert.Null(live.PendingActivation);
        Assert.False(AttachSession.HasPendingObserveWork(live));
        var output = new CountingStream();
        using var tty = new UnixRawTerminal(output, 80, 24);
        var box = Assert.Single(live.Chrome!.Panes, pane => pane.PaneId == "p1").Content;
        var full = new TerminalRenderCellsPayload
        {
            Kind = TerminalRenderCellsPayload.KindCells,
            PaneId = "p1",
            Full = true,
            GridCols = box.Cols,
            GridRows = box.Rows,
            Generation = 1,
            Rows = [new TerminalRenderCellRow { I = 0, T = "$ " }],
            Cursor = new TerminalRenderCursorPayload { Col = 2, Row = 0, Visible = true, Shape = 2 },
        };
        Assert.True(AttachSession.TryWriteCells(tty, live, full, reanchorBeforePaint: true));

        using var gate = new SemaphoreSlim(1, 1);
        using var stop = new CancellationTokenSource();
        var loop = new AttachSession().ReadRenderAsync(
            reader, reader, gate, new SnapshotAssembler(), tty, live, stop, stop.Token);
        try
        {
            await gate.WaitAsync();
            var before = output.BytesWritten;
            var echo = full with
            {
                Full = false,
                BaseGeneration = 1,
                Generation = 2,
                Rows = [new TerminalRenderCellRow { I = 0, T = "$ " + EchoMarker }],
                Cursor = new TerminalRenderCursorPayload { Col = 2 + EchoMarker.Length, Row = 0, Visible = true, Shape = 2 },
            };
            Assert.DoesNotContain(EchoMarker, Encoding.UTF8.GetString(output.Snapshot(0)), StringComparison.Ordinal);
            peer.Send(RenderEventLine(echo, seq: 1));

            Assert.True(
                SpinWait.SpinUntil(
                    () => Encoding.UTF8.GetString(output.Snapshot(before)).Contains(EchoMarker, StringComparison.Ordinal)
                        || loop.IsCompleted,
                    TestWait.HangGuard),
                "the echo was not painted while the control gate was held");
            Assert.False(loop.IsCompleted);
            Assert.Equal(0, gate.CurrentCount);
        }
        finally
        {
            gate.Release();
            stop.Cancel();
            await Drain(loop);
        }
    }

    [Fact]
    public async Task Pending_observe_work_still_takes_the_control_gate()
    {
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.RenderSub = "sub_1";
        live.PendingObservePaneId = "p2";
        Assert.True(AttachSession.HasPendingObserveWork(live));
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();

        var observe = AttachSession.ObservePendingUnderGateAsync(
            port, live, gate, tty: null, CancellationToken.None);

        Assert.False(observe.IsCompleted);
        Assert.DoesNotContain(port.Calls, call => call.Method == ProtocolMethods.TerminalObserve);

        gate.Release();
        await observe.WaitAsync(TestWait.HangGuard);
        Assert.Contains(
            port.Calls,
            call => call.Method == ProtocolMethods.TerminalObserve
                && call.Params?["pane_id"]?.GetValue<string>() == "p2");
        Assert.Null(live.PendingObservePaneId);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Held_pending_observe_does_not_take_the_control_gate()
    {
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.RenderSub = "sub_1";
        live.PendingObservePaneId = "p2";
        Interlocked.Exchange(ref live.HoldPendingObserve, 1);
        Assert.False(AttachSession.HasPendingObserveWork(live));
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();

        await AttachSession.ObservePendingUnderGateAsync(
                port, live, gate, tty: null, CancellationToken.None)
            .WaitAsync(TestWait.HangGuard);

        Assert.Equal(0, gate.CurrentCount);
        Assert.Empty(port.Calls);
        Assert.Equal("p2", live.PendingObservePaneId);
        gate.Release();
    }

    [Fact]
    public async Task Pending_chrome_refresh_still_takes_the_control_gate()
    {
        var port = MouseTestGeom.ApplyPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.PendingChromeRefresh = true;
        Assert.True(AttachSession.HasPendingObserveWork(live));
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();

        var refresh = AttachSession.ObservePendingUnderGateAsync(
            port, live, gate, tty: null, CancellationToken.None);

        Assert.False(refresh.IsCompleted);
        Assert.True(live.PendingChromeRefresh);
        Assert.Empty(port.Calls);

        gate.Release();
        await refresh.WaitAsync(TestWait.HangGuard);
        Assert.False(live.PendingChromeRefresh);
        Assert.NotEmpty(port.Calls);
    }

    [Fact]
    public async Task Integrations_settings_page_still_takes_the_control_gate()
    {
        var port = new MouseRecordingPort();
        var live = MouseTestGeom.ApplyLive(port, MouseTestGeom.Split());
        live.Engine.EnterSettings(SettingsPageRegistry.IntegrationsId);
        Assert.True(AttachSession.HasPendingObserveWork(live));
        using var gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();

        var observe = AttachSession.ObservePendingUnderGateAsync(
            port, live, gate, tty: null, CancellationToken.None);

        Assert.False(observe.IsCompleted);

        gate.Release();
        await observe.WaitAsync(TestWait.HangGuard);
        Assert.Equal(1, gate.CurrentCount);
    }

    private static string RenderEventLine(TerminalRenderCellsPayload payload, long seq)
    {
        var body = JsonSerializer.Serialize(payload, ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        var line = new JsonObject
        {
            ["event"] = "runtime.event",
            ["params"] = new JsonObject
            {
                ["type"] = ProtocolEventTypes.TerminalRender,
                ["payload"] = JsonNode.Parse(body),
                ["seq"] = seq,
            },
        };
        return line.ToJsonString() + "\n";
    }

    private static async Task Drain(Task task)
    {
        try
        {
            await task.WaitAsync(TestWait.HangGuard).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // An unconnected control client ends the loop on its first RPC.
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A stdin source that returns pushed chunks and times out when idle.</summary>
    private sealed class ChannelAttachStdin : IAttachStdinSource
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();

        public bool ThreadAlive => false;

        public void Push(byte[] chunk) => Assert.True(_chunks.Writer.TryWrite(chunk));

        public async ValueTask<AttachStdinRead> ReadAsync(
            byte[] buffer,
            int timeoutMs,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeoutMs > 0)
                timeout.CancelAfter(timeoutMs);
            try
            {
                var chunk = await _chunks.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                chunk.CopyTo(buffer, 0);
                return AttachStdinRead.Bytes(chunk.Length);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return AttachStdinRead.Timeout;
            }
        }

        public ValueTask DisposeAsync()
        {
            _chunks.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A host TTY sink that other threads can read while the loop writes.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly object _gate = new();
        private readonly MemoryStream _inner = new();

        public long BytesWritten
        {
            get
            {
                lock (_gate)
                    return _inner.Length;
            }
        }

        public byte[] Snapshot(long from)
        {
            lock (_gate)
                return _inner.ToArray()[(int)from..];
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
                _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_gate)
                _inner.Write(buffer);
        }
    }

    /// <summary>A Unix-socket mux stand-in that pushes event lines to one client.</summary>
    private sealed class EventPeer : IDisposable
    {
        private readonly Socket _listener;
        private NetworkStream? _stream;

        private EventPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        private string Path { get; }

        internal static EventPeer Listen()
        {
            var path = "/tmp/hypa-gate-fast-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            return new EventPeer(listener, path);
        }

        internal async Task<ControlPlaneClient> ConnectAsync()
        {
            var client = new ControlPlaneClient(
                Path, connectTimeout: TestWait.HangGuard, callTimeout: TestWait.HangGuard);
            var accept = _listener.AcceptAsync();
            await client.ConnectAsync();
            _stream = new NetworkStream(await accept, ownsSocket: true);
            return client;
        }

        internal void Send(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            _stream!.Write(bytes);
            _stream.Flush();
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _listener.Dispose();
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}
