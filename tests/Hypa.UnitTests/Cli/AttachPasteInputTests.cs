using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// Large pastes decode into one key event per byte. Input must arrive whole
/// and in order without tripping item-count limits on either side.
/// </summary>
public sealed class AttachPasteInputTests
{
    [SkippableFact]
    public async Task Paste_enqueued_per_key_is_delivered_whole_and_in_order()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var peer = SendKeysPeer.Listen();
        await using var client = new ControlPlaneClient(
            peer.Path, connectTimeout: TimeSpan.FromSeconds(2), callTimeout: TimeSpan.FromSeconds(5));
        var accept = peer.AcceptAsync();
        await client.ConnectAsync();
        await accept;

        await using var sender = new AttachInputSender(client, () => ("pane_a", "lease_a"));
        var paste = Paste(512 * 1024);
        foreach (var b in paste)
            Assert.True(await sender.EnqueueAsync(new[] { b }, CancellationToken.None));

        Assert.True(
            SpinWait.SpinUntil(() => peer.ReceivedBytes >= paste.Length, TimeSpan.FromSeconds(10)),
            $"received {peer.ReceivedBytes} of {paste.Length}");
        Assert.Equal(paste, peer.Received());
        Assert.Equal(0, sender.RejectedBatches);
        Assert.False(sender.IsFaulted);
        Assert.True(peer.LongestLine < UnixSocketServerOptions.DefaultMaxLineBytes);
    }

    [SkippableFact]
    public async Task Paste_enqueued_per_stdin_read_uses_few_notifications()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");

        await using var peer = SendKeysPeer.Listen();
        await using var client = new ControlPlaneClient(
            peer.Path, connectTimeout: TimeSpan.FromSeconds(2), callTimeout: TimeSpan.FromSeconds(5));
        var accept = peer.AcceptAsync();
        await client.ConnectAsync();
        await accept;

        await using var sender = new AttachInputSender(client, () => ("pane_a", "lease_a"));
        var paste = Paste(4 * 1024 * 1024);
        for (var offset = 0; offset < paste.Length; offset += 4096)
            Assert.True(await sender.EnqueueAsync(paste.AsMemory(offset, 4096), CancellationToken.None));

        Assert.True(
            SpinWait.SpinUntil(() => peer.ReceivedBytes >= paste.Length, TimeSpan.FromSeconds(20)),
            $"received {peer.ReceivedBytes} of {paste.Length}");
        Assert.Equal(paste, peer.Received());
        Assert.True(peer.Notifications <= paste.Length / 4096, $"{peer.Notifications} notifications");
        Assert.True(peer.LongestLine < UnixSocketServerOptions.DefaultMaxLineBytes);
    }

    [Fact]
    public void Adjacent_pane_byte_events_coalesce_in_order()
    {
        var events = new List<KeyEngineEvent>
        {
            new(KeyEngineEventKind.SendPaneBytes, Bytes: [0x61]),
            new(KeyEngineEventKind.SendPaneBytes, Bytes: [0x62]),
            new(KeyEngineEventKind.SendPaneBytes, Bytes: [0x63], TargetId: "popup"),
            new(KeyEngineEventKind.SendPaneBytes, Bytes: [0x64], TargetId: "popup"),
            new(KeyEngineEventKind.Dispatch, Action: KeyActionId.Detach),
            new(KeyEngineEventKind.SendPaneBytes, Bytes: [0x65]),
            new(KeyEngineEventKind.SendPaneBytes, Bytes: [0x66]),
        };

        var merged = AttachSession.CoalescePaneBytes(events);

        Assert.Equal(4, merged.Count);
        Assert.Equal("ab"u8.ToArray(), merged[0].Bytes);
        Assert.Null(merged[0].TargetId);
        Assert.Equal("cd"u8.ToArray(), merged[1].Bytes);
        Assert.Equal("popup", merged[1].TargetId);
        Assert.Equal(KeyEngineEventKind.Dispatch, merged[2].Kind);
        Assert.Equal("ef"u8.ToArray(), merged[3].Bytes);
    }

    [Fact]
    public async Task EnqueueAsync_waits_for_space_instead_of_dropping()
    {
        var client = new ControlPlaneClient("/tmp/hypa-paste-wait.sock");
        await using var sender = new AttachInputSender(
            client, () => ("pane_a", "lease_a"), maxQueuedBytes: 16)
        {
            BlockNotifyForTests = true,
        };

        Assert.True(await sender.EnqueueAsync(new byte[] { 0x61 }, CancellationToken.None));
        await sender.NotifyEnteredForTests.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(sender.TryEnqueue(new byte[32]));
        var pending = sender.EnqueueAsync(new byte[40], CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);
        Assert.Equal(16, sender.QueuedBytes);

        await sender.DisposeAsync();
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task EnqueueAsync_honours_cancellation_while_waiting()
    {
        var client = new ControlPlaneClient("/tmp/hypa-paste-cancel.sock");
        await using var sender = new AttachInputSender(
            client, () => ("pane_a", "lease_a"), maxQueuedBytes: 8)
        {
            BlockNotifyForTests = true,
        };
        Assert.True(await sender.EnqueueAsync(new byte[] { 0x61 }, CancellationToken.None));
        await sender.NotifyEnteredForTests.WaitAsync(TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.EnqueueAsync(new byte[64], cts.Token).AsTask());
        sender.NotifyHoldForTests.Release();
    }

    [Fact]
    public async Task Pane_input_actor_admits_many_small_writes_while_pty_is_blocked()
    {
        var runtime = new GatedRuntime();
        await using var actor = new PaneInputActor(runtime, occupantGeneration: 1);
        var paste = Paste(64 * 1024);

        // Far more admissions than the old 1024-item cap, while the PTY
        // write is stalled.
        foreach (var b in paste)
            Assert.True(actor.TryAdmit(new[] { b }));

        runtime.Open();
        Assert.True(
            SpinWait.SpinUntil(() => runtime.WrittenBytes >= paste.Length, TimeSpan.FromSeconds(10)),
            $"written {runtime.WrittenBytes} of {paste.Length}");
        Assert.Equal(paste, runtime.Written());
        Assert.True(runtime.Writes < paste.Length / 64, $"{runtime.Writes} PTY writes");
        Assert.Equal(0, actor.UndeliverableBytes);
    }

    [Fact]
    public async Task Pane_input_actor_rejects_beyond_byte_budget()
    {
        var runtime = new GatedRuntime();
        await using var actor = new PaneInputActor(runtime, occupantGeneration: 1, maxQueuedBytes: 8);
        Assert.True(actor.TryAdmit(new byte[8]));
        await Task.Delay(20);
        Assert.False(actor.TryAdmit(new byte[9]));
        runtime.Open();
    }

    private static byte[] Paste(int length)
    {
        const string text =
            "hypa attach: Socket parent '/home/user/.config/hypa/runtime/default' is writable by "
            + "group or others. The socket must live in a private directory (mode 0700).\n";
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)text[i % text.Length];
        return bytes;
    }

    private sealed class GatedRuntime : IPaneRuntime
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly MemoryStream _written = new();
        private int _writes;

        public PaneId Id => new("pane_a");
        public bool IsAlive => true;
        public int? ExitCode => null;
        public int? Pid => null;
        public int Writes => Volatile.Read(ref _writes);

        public long WrittenBytes
        {
            get
            {
                lock (_gate)
                    return _written.Length;
            }
        }

        public void Open() => _open.TrySetResult();

        public byte[] Written()
        {
            lock (_gate)
                return _written.ToArray();
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            await _open.Task.WaitAsync(ct);
            lock (_gate)
                _written.Write(data.Span);
            Interlocked.Increment(ref _writes);
        }

        public ValueTask WriteTextAsync(string text, CancellationToken ct) =>
            WriteAsync(Encoding.UTF8.GetBytes(text), ct);

        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => "";
        public string ReadDetectionText() => "";

        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited { add { } remove { } }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Unix socket peer that reassembles <c>pane.send_keys</c> payloads.</summary>
    private sealed class SendKeysPeer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly object _gate = new();
        private readonly MemoryStream _received = new();
        private NetworkStream? _stream;
        private Task? _reader;
        private int _notifications;
        private int _longestLine;

        private SendKeysPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        internal string Path { get; }

        internal int Notifications => Volatile.Read(ref _notifications);

        internal int LongestLine => Volatile.Read(ref _longestLine);

        internal long ReceivedBytes
        {
            get
            {
                lock (_gate)
                    return _received.Length;
            }
        }

        internal static SendKeysPeer Listen()
        {
            var path = "/tmp/hypa-paste-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            if (File.Exists(path))
                File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(1);
                return new SendKeysPeer(listener, path);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        internal async Task AcceptAsync()
        {
            var accepted = await _listener.AcceptAsync();
            _stream = new NetworkStream(accepted, ownsSocket: true);
            _reader = ReadAsync();
        }

        internal byte[] Received()
        {
            lock (_gate)
                return _received.ToArray();
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
                catch (Exception)
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

        private async Task ReadAsync()
        {
            var stream = _stream ?? throw new InvalidOperationException("peer is not connected");
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.GetProperty("method").GetString() != ProtocolMethods.PaneSendKeys)
                        continue;
                    var data = Convert.FromBase64String(
                        root.GetProperty("params").GetProperty("data").GetString()!);
                    lock (_gate)
                        _received.Write(data);
                    Interlocked.Increment(ref _notifications);
                    if (line.Length > _longestLine)
                        Volatile.Write(ref _longestLine, line.Length);
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
