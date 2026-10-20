using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Latency;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The input sender puts a <c>seq</c> on <c>pane.send_keys</c> only when the
/// welcome of its connection listed <c>hypa.attach.input_ack.v2</c>.
/// </summary>
public sealed class AttachInputSeqTests
{
    [SkippableFact]
    public async Task A_peer_with_input_ack_gets_a_monotonic_seq_on_each_batch()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var peer = ParamsPeer.Listen();
        await using var client = await peer.ConnectAsync();
        client.NotePeerCapabilities(
            [.. AttachEndpointProtocol.RequiredCapabilities, AttachEndpointProtocol.InputAckCapability]);
        var meter = new AttachLatencyMeter();
        await using var sender = new AttachInputSender(client, () => ("pane_a", "lease_a"), latency: meter);

        foreach (var key in "abc")
        {
            var bytes = new[] { (byte)key };
            meter.NoteStdinRead(bytes, meter.Now());
            Assert.True(await sender.EnqueueAsync(bytes, CancellationToken.None, meter.StampKeys(bytes)));
            await sender.FlushForTests();
        }

        var sent = await peer.WaitForAsync(3);
        Assert.Equal(new long[] { 1, 2, 3 }, sent.Select(p => p.GetProperty("seq").GetInt64()));
        var report = meter.Report();
        Assert.False(report.AckUnavailable);
        Assert.Equal(3, report.KeysSent);
        Assert.Equal(3, report.OpenKeys);
        Assert.Equal(3, report.Hops["input-wire"].Count);
    }

    [SkippableFact]
    public async Task A_peer_without_input_ack_gets_no_seq_and_the_meter_reports_it()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var peer = ParamsPeer.Listen();
        await using var client = await peer.ConnectAsync();
        client.NotePeerCapabilities([.. AttachEndpointProtocol.RequiredCapabilities]);
        var meter = new AttachLatencyMeter();
        await using var sender = new AttachInputSender(client, () => ("pane_a", "lease_a"), latency: meter);

        var bytes = "x"u8.ToArray();
        meter.NoteStdinRead(bytes, meter.Now());
        Assert.True(await sender.EnqueueAsync(bytes, CancellationToken.None, meter.StampKeys(bytes)));
        await sender.FlushForTests();

        var sent = Assert.Single(await peer.WaitForAsync(1));
        Assert.False(sent.TryGetProperty("seq", out _), sent.GetRawText());
        Assert.Equal("x", Encoding.UTF8.GetString(Convert.FromBase64String(sent.GetProperty("data").GetString()!)));
        var report = meter.Report();
        Assert.True(report.AckUnavailable);
        Assert.Equal(1, report.KeysRead);
        Assert.Equal(1, report.KeysSent);
        Assert.Equal(0, report.OpenKeys);
    }

    [SkippableFact]
    public async Task A_peer_that_lists_only_the_first_input_ack_version_gets_no_seq()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var peer = ParamsPeer.Listen();
        await using var client = await peer.ConnectAsync();
        // A mux of the first version sends no ack-only body, and a client of
        // the first version does not know it. Neither side uses the seq.
        client.NotePeerCapabilities([.. AttachEndpointProtocol.RequiredCapabilities, "hypa.attach.input_ack.v1"]);
        var meter = new AttachLatencyMeter();
        await using var sender = new AttachInputSender(client, () => ("pane_a", "lease_a"), latency: meter);

        var bytes = "x"u8.ToArray();
        meter.NoteStdinRead(bytes, meter.Now());
        Assert.True(await sender.EnqueueAsync(bytes, CancellationToken.None, meter.StampKeys(bytes)));
        await sender.FlushForTests();

        var sent = Assert.Single(await peer.WaitForAsync(1));
        Assert.False(sent.TryGetProperty("seq", out _), sent.GetRawText());
        Assert.True(meter.Report().AckUnavailable);
    }

    [SkippableFact]
    public async Task A_buffer_split_into_batches_opens_each_key_under_the_seq_of_its_last_byte()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var peer = ParamsPeer.Listen();
        await using var client = await peer.ConnectAsync();
        client.NotePeerCapabilities([AttachEndpointProtocol.InputAckCapability]);
        var meter = new AttachLatencyMeter();
        await using var sender = new AttachInputSender(client, () => ("pane_a", "lease_a"), latency: meter);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.HoldRoundForTests = hold.Task;

        // 40 000 bytes end in the first 64 KiB batch. The next 40 000 bytes
        // end in the second batch.
        var first = new byte[40_000];
        var second = new byte[40_000];
        Array.Fill(first, (byte)'a');
        Array.Fill(second, (byte)'b');
        Assert.True(sender.TryEnqueue(first, new AttachKeyStamp(meter.Now(), 3)));
        Assert.True(sender.TryEnqueue(second, new AttachKeyStamp(meter.Now(), 5)));
        hold.TrySetResult();
        await sender.FlushForTests();

        var sent = await peer.WaitForAsync(2);
        Assert.Equal(new long[] { 1, 2 }, sent.Select(p => p.GetProperty("seq").GetInt64()));
        Assert.Equal(8, meter.Report().OpenKeys);
        meter.NoteInputAck("pane_a", 1);
        Assert.Equal(5, meter.Report().OpenKeys);
        meter.NoteInputAck("pane_a", 2);
        Assert.Equal(0, meter.Report().OpenKeys);
    }

    [SkippableFact]
    public async Task Keys_admitted_in_slices_open_under_the_seq_of_the_last_slice()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var peer = ParamsPeer.Listen();
        await using var client = await peer.ConnectAsync();
        client.NotePeerCapabilities([AttachEndpointProtocol.InputAckCapability]);
        var meter = new AttachLatencyMeter();
        await using var sender = new AttachInputSender(
            client, () => ("pane_a", "lease_a"), maxQueuedBytes: 4, latency: meter);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.HoldRoundForTests = hold.Task;

        // Eight bytes and a four-byte queue: the first slice goes out alone.
        var enqueue = sender.EnqueueAsync("abcdefgh"u8.ToArray(), CancellationToken.None, new AttachKeyStamp(meter.Now(), 8));
        Assert.False(enqueue.IsCompleted);
        hold.TrySetResult();
        Assert.True(await enqueue.AsTask().WaitAsync(TestWait.HangGuard));
        await sender.FlushForTests();

        var sent = await peer.WaitForAsync(2);
        Assert.Equal(new long[] { 1, 2 }, sent.Select(p => p.GetProperty("seq").GetInt64()));
        meter.NoteInputAck("pane_a", 1);
        Assert.Equal(8, meter.Report().OpenKeys);
        meter.NoteInputAck("pane_a", 2);
        Assert.Equal(0, meter.Report().OpenKeys);
    }

    [SkippableFact]
    public async Task A_retarget_drops_the_keys_that_wait_for_an_ack()
    {
        Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Unix domain sockets only.");
        await using var peer = ParamsPeer.Listen();
        await using var client = await peer.ConnectAsync();
        client.NotePeerCapabilities([AttachEndpointProtocol.InputAckCapability]);
        var meter = new AttachLatencyMeter();
        await using var sender = new AttachInputSender(client, () => ("pane_a", "lease_a"), latency: meter);

        var bytes = "y"u8.ToArray();
        meter.NoteStdinRead(bytes, meter.Now());
        Assert.True(await sender.EnqueueAsync(bytes, CancellationToken.None, meter.StampKeys(bytes)));
        await sender.FlushForTests();
        await peer.WaitForAsync(1);
        Assert.Equal(1, meter.Report().OpenKeys);

        sender.Retarget(client, installTarget: null);
        var report = meter.Report();
        Assert.Equal(0, report.OpenKeys);
        Assert.Equal(1, report.DroppedKeys);
    }

    /// <summary>A Unix socket peer that keeps the params of each <c>pane.send_keys</c>.</summary>
    private sealed class ParamsPeer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly object _gate = new();
        private readonly List<JsonElement> _params = [];
        private NetworkStream? _stream;
        private Task? _reader;

        private ParamsPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        private string Path { get; }

        public static ParamsPeer Listen()
        {
            var path = "/tmp/hypa-seq-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(1);
                return new ParamsPeer(listener, path);
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        public async Task<ControlPlaneClient> ConnectAsync()
        {
            var client = new ControlPlaneClient(
                Path, connectTimeout: TestWait.HangGuard, callTimeout: TestWait.HangGuard);
            var accept = _listener.AcceptAsync();
            await client.ConnectAsync();
            _stream = new NetworkStream(await accept, ownsSocket: true);
            _reader = ReadAsync();
            return client;
        }

        public Task<JsonElement[]> WaitForAsync(int count) =>
            Poll.UntilAsync(
                () =>
                {
                    lock (_gate)
                        return _params.Count >= count ? _params.ToArray() : null;
                },
                $"the peer to get {count} pane.send_keys");

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
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.GetProperty("method").GetString() != ProtocolMethods.PaneSendKeys)
                        continue;
                    lock (_gate)
                        _params.Add(doc.RootElement.GetProperty("params").Clone());
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
