using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Continuity.Tests.Support;
using Xunit;

namespace Hypa.Continuity.Tests.Impairment;

/// <summary>
/// One run of the echo probe on the joined control stream:
/// <code>
/// fake mux Unix socket
///   -> LocalUnixBridge -> AcceptControlNdjsonPump -> FramedControlStream
///   -> OutboundFramedSession -> QuicBytePath | TcpTlsBytePath
/// </code>
/// The fake mux writes bulk render lines for a second pane with no pause.
/// The client sends one key at a time. The fake mux writes an echo line when
/// it reads the key. The client clock measures the key write to the echo read.
/// </summary>
internal static class EchoUnderBulkProbe
{
    public const string Quic = "quic";
    public const string Tcp = "tcp";

    private static readonly byte[] EchoMarker = "\"echo\":"u8.ToArray();

    public static async Task<EchoUnderBulkResult> RunAsync(
        string transport,
        ImpairmentProfile profile,
        X509Certificate2 certificate,
        EchoUnderBulkOptions options)
    {
        var quic = string.Equals(transport, Quic, StringComparison.Ordinal);
        if (quic)
            Skip.IfNot(QuicTransportRuntime.IsUsable(), "QUIC runtime is not usable on this host");
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_echob", "echob001");
        await using var stack = await AcceptStack.StartAsync(
            muxBoot,
            tlsCertificate: certificate,
            bindHost: IPAddress.Any.ToString(),
            trustGate: new LocalSelfHostedJoinTrustGate());
        if (quic)
            Skip.IfNot(stack.Accept.QuicListening, "the accept has no QUIC listener: " + stack.Accept.QuicStartupDetail);
        await using var proxy = profile.IsPassThrough ? null : ImpairmentProxy.Start(stack.Accept.Port, profile);
        var endpoint = stack.Accept.DialEndpoint with
        {
            Host = IPAddress.Loopback.ToString(),
            Port = proxy?.Port ?? stack.Accept.Port,
            QuicListening = quic,
        };
        IBytePath bytePath = quic ? new QuicBytePath(certificate) : new TcpTlsBytePath(certificate);
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            endpoint,
            clientBoot,
            tlsTrust: certificate,
            bytePath: bytePath);
        using var muxSocket = await stack.FakeMux.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var outcome = await clientTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(outcome.Ok, outcome.Detail);
        await using var session = outcome.Value!;
        await using var control = new FramedControlStream(session, ownsSession: false);
        await using var mux = new FakeMuxPeer(muxSocket, options.BulkLineBytes);
        using var stop = new CancellationTokenSource();
        var client = new EchoClient(control, options.EchoWait, async () =>
        {
            // The control stream hides why the session ended. The session
            // gives its close outcome to a receive after the end.
            var closed = await session.ReceiveAsync(CancellationToken.None);
            return closed.Ok ? "open" : $"{closed.Reason}: {closed.Detail}";
        });
        client.Describe = () =>
            $"client[{client.State}] mux[{mux.State}] proxy[{proxy?.Describe() ?? "none"}]";
        var reader = Task.Run(() => client.ReadLoopAsync(stop.Token));
        var muxReader = Task.Run(() => mux.EchoLoopAsync(stop.Token));
        try
        {
            var idle = await client.TypeAsync(options.IdleKeys, options.KeyInterval, firstSeq: 1);
            var bulk = mux.StartBulk(stop.Token);
            // The bulk runs for the warm-up of the model before the probe measures.
            await Pacing.GapAsync(options.BulkWarmUp);
            var bulkBytesBefore = client.BulkBytes;
            var started = Stopwatch.GetTimestamp();
            var underBulk = await client.TypeAsync(options.BulkKeys, options.KeyInterval, firstSeq: 1 + options.IdleKeys);
            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var bulkRate = (client.BulkBytes - bulkBytesBefore) / Math.Max(seconds, 1e-3);
            await stop.CancelAsync();
            await mux.StopAsync();
            return new EchoUnderBulkResult(
                transport,
                profile,
                idle,
                underBulk,
                options.IdleKeys + options.BulkKeys,
                client.EchoCount,
                bulkRate,
                proxy?.Counters);
        }
        finally
        {
            await stop.CancelAsync();
            await Poll.EndAsync(Task.WhenAll(reader, muxReader), "the probe read loops to stop");
        }
    }

    /// <summary>The fake mux end: one writer, so a line never splits another line.</summary>
    private sealed class FakeMuxPeer : IAsyncDisposable
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly byte[] _bulkLine;
        private Task? _bulk;
        private long _keysRead;
        private long _echoesWritten;
        private long _bulkLines;
        private volatile string _echoLoop = "running";
        private volatile string _bulkLoop = "not started";

        public string State =>
            $"keys_read={Interlocked.Read(ref _keysRead)} echoes_written={Interlocked.Read(ref _echoesWritten)} "
            + $"bulk_lines={Interlocked.Read(ref _bulkLines)} echo_loop={_echoLoop} bulk_loop={_bulkLoop}";

        public FakeMuxPeer(Socket socket, int bulkLineBytes)
        {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: false);
            _bulkLine = BuildBulkLine(bulkLineBytes);
        }

        public Task StartBulk(CancellationToken ct)
        {
            _bulkLoop = "running";
            _bulk = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await WriteAsync(_bulkLine, ct);
                        Interlocked.Increment(ref _bulkLines);
                    }

                    _bulkLoop = "stopped";
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    _bulkLoop = ex.GetType().Name + ": " + ex.Message;
                }
            });
            return _bulk;
        }

        public async Task StopAsync()
        {
            if (_bulk is not null)
                await Poll.EndAsync(_bulk, "the bulk loop to stop");
        }

        public async Task EchoLoopAsync(CancellationToken ct)
        {
            var lines = new LineSplitter();
            var buffer = new byte[64 * 1024];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await _stream.ReadAsync(buffer, ct);
                    if (n <= 0)
                    {
                        _echoLoop = "end of stream";
                        return;
                    }

                    foreach (var line in lines.Push(buffer.AsSpan(0, n)))
                    {
                        if (TryReadNumber(line, "\"seq\":"u8, out var seq))
                        {
                            Interlocked.Increment(ref _keysRead);
                            var echo = Encoding.UTF8.GetBytes(
                                "{\"jsonrpc\":\"2.0\",\"method\":\"event\",\"params\":{\"type\":\"terminal.render\","
                                + "\"payload\":{\"kind\":\"cells\",\"pane_id\":\"typing\",\"echo\":"
                                + seq.ToString(CultureInfo.InvariantCulture) + "}}}\n");
                            await WriteAsync(echo, ct);
                            Interlocked.Increment(ref _echoesWritten);
                        }
                    }
                }

                _echoLoop = "stopped";
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                _echoLoop = ex.GetType().Name + ": " + ex.Message;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await _stream.DisposeAsync();
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _write.Dispose();
        }

        private async Task WriteAsync(byte[] line, CancellationToken ct)
        {
            await _write.WaitAsync(ct);
            try
            {
                await _stream.WriteAsync(line, ct);
            }
            finally
            {
                _write.Release();
            }
        }

        /// <summary>A render line of the size of a full cells body of a busy pane.</summary>
        private static byte[] BuildBulkLine(int bytes)
        {
            const string head = "{\"jsonrpc\":\"2.0\",\"method\":\"event\",\"params\":{\"type\":\"terminal.render\","
                + "\"payload\":{\"kind\":\"cells\",\"pane_id\":\"bulk\",\"rows\":[{\"i\":0,\"t\":\"";
            const string tail = "\"}]}}}\n";
            var fill = Math.Max(16, bytes - head.Length - tail.Length);
            var random = new Random(4021);
            var raw = new byte[(fill * 3 / 4) + 3];
            random.NextBytes(raw);
            var text = Convert.ToBase64String(raw)[..fill];
            return Encoding.UTF8.GetBytes(head + text + tail);
        }
    }

    /// <summary>The client end: keys out, lines in, and the time of each echo.</summary>
    private sealed class EchoClient(Stream control, TimeSpan echoWait, Func<Task<string>> sessionEnd)
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, TaskCompletionSource<long>> _waiting = [];
        private long _bulkBytes;
        private int _echoes;
        private long _lastReadAt = Stopwatch.GetTimestamp();
        private volatile string _readLoop = "running";
        private volatile string? _sessionEnd;
        private bool _ended;

        /// <summary>The state of every part of the probe, for the report of a missing echo.</summary>
        public Func<string> Describe { get; set; } = () => "";

        public string State =>
            string.Create(
                CultureInfo.InvariantCulture,
                $"echoes={EchoCount} bulk_bytes={BulkBytes} last_read={Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReadAt)).TotalMilliseconds:0}ms_ago read_loop={_readLoop}");

        public long BulkBytes => Interlocked.Read(ref _bulkBytes);

        public int EchoCount => Volatile.Read(ref _echoes);

        public async Task ReadLoopAsync(CancellationToken ct)
        {
            var lines = new LineSplitter();
            var buffer = new byte[256 * 1024];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var n = await control.ReadAsync(buffer, ct);
                    if (n <= 0)
                    {
                        _sessionEnd = await sessionEnd();
                        _readLoop = "end of stream, session " + _sessionEnd;
                        return;
                    }

                    var at = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref _lastReadAt, at);
                    foreach (var line in lines.Push(buffer.AsSpan(0, n)))
                    {
                        if (!TryReadNumber(line, EchoMarker, out var seq))
                        {
                            Interlocked.Add(ref _bulkBytes, line.Length + 1);
                            continue;
                        }

                        TaskCompletionSource<long>? waiter;
                        lock (_gate)
                            _waiting.Remove(seq, out waiter);
                        Interlocked.Increment(ref _echoes);
                        waiter?.TrySetResult(at);
                    }
                }

                _readLoop = "stopped";
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                _readLoop = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                // No echo can come after the read loop ends. Fail each waiting
                // key now, so the probe reports the end at once.
                List<TaskCompletionSource<long>> waiting;
                lock (_gate)
                {
                    _ended = true;
                    waiting = [.. _waiting.Values];
                    _waiting.Clear();
                }

                foreach (var waiter in waiting)
                    waiter.TrySetException(new EchoStreamEndedException("the control stream read loop ended: " + _readLoop, _sessionEnd));
            }
        }

        /// <summary>Sends keys one at a time and returns the key-to-echo times in ms.</summary>
        public async Task<List<double>> TypeAsync(int keys, TimeSpan interval, long firstSeq)
        {
            var times = new List<double>(keys);
            for (var i = 0; i < keys; i++)
            {
                var seq = firstSeq + i;
                var waiter = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    if (_ended)
                        waiter.TrySetException(new EchoStreamEndedException("the control stream read loop ended: " + _readLoop, _sessionEnd));
                    else
                        _waiting[seq] = waiter;
                }

                var line = Encoding.UTF8.GetBytes(
                    "{\"jsonrpc\":\"2.0\",\"method\":\"pane.send_keys\",\"params\":{\"pane_id\":\"typing\","
                    + "\"encoding\":\"utf8\",\"data\":\"a\",\"seq\":" + seq.ToString(CultureInfo.InvariantCulture) + "}}\n");
                var sent = Stopwatch.GetTimestamp();
                await control.WriteAsync(line);
                await control.FlushAsync();
                long echoed;
                try
                {
                    echoed = await waiter.Task.WaitAsync(echoWait);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"no echo of key seq={seq} in {echoWait.TotalSeconds:0} s: {Describe()}"));
                }
                catch (EchoStreamEndedException ex)
                {
                    throw new EchoStreamEndedException(
                        string.Create(CultureInfo.InvariantCulture, $"no echo of key seq={seq}: {ex.Message}: {Describe()}"),
                        ex.SessionEnd);
                }

                times.Add(Stopwatch.GetElapsedTime(sent, echoed).TotalMilliseconds);
                await Pacing.GapAsync(interval);
            }

            return times;
        }
    }

    /// <summary>Splits a byte stream into NDJSON lines.</summary>
    private sealed class LineSplitter
    {
        private byte[] _pending = new byte[64 * 1024];
        private int _length;

        public List<byte[]> Push(ReadOnlySpan<byte> chunk)
        {
            var lines = new List<byte[]>();
            while (!chunk.IsEmpty)
            {
                var newline = chunk.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    Append(chunk);
                    break;
                }

                Append(chunk[..newline]);
                lines.Add(_pending.AsSpan(0, _length).ToArray());
                _length = 0;
                chunk = chunk[(newline + 1)..];
            }

            return lines;
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            if (_length + bytes.Length > _pending.Length)
                Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _length + bytes.Length));
            bytes.CopyTo(_pending.AsSpan(_length));
            _length += bytes.Length;
        }
    }

    private static bool TryReadNumber(ReadOnlySpan<byte> line, ReadOnlySpan<byte> marker, out long value)
    {
        value = 0;
        var at = line.IndexOf(marker);
        if (at < 0)
            return false;
        var digits = line[(at + marker.Length)..];
        var any = false;
        foreach (var b in digits)
        {
            if (b is < (byte)'0' or > (byte)'9')
                break;
            value = (value * 10) + (b - '0');
            any = true;
        }

        return any;
    }
}

internal sealed record EchoUnderBulkOptions
{
    public int IdleKeys { get; init; } = 20;

    public int BulkKeys { get; init; } = 60;

    public TimeSpan KeyInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    public TimeSpan BulkWarmUp { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest wait for the echo of one key.</summary>
    public TimeSpan EchoWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The size of one bulk render line, near a full 120x40 cells body.</summary>
    public int BulkLineBytes { get; init; } = 8 * 1024;
}

internal sealed record EchoUnderBulkResult(
    string Transport,
    ImpairmentProfile Profile,
    IReadOnlyList<double> IdleMs,
    IReadOnlyList<double> BulkMs,
    int KeysSent,
    int Echoes,
    double BulkBytesPerSecond,
    ImpairmentCounters? Proxy)
{
    public LatencyStats Idle => LatencyStats.From([.. IdleMs]);

    public LatencyStats UnderBulk => LatencyStats.From([.. BulkMs]);

    public string Summary
    {
        get
        {
            var proxy = Proxy is { } p
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $" proxy_packets={p.Packets} lost={p.Lost} queue_drops={p.QueueDrops} tcp_stalls={p.Stalls}")
                : "";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"echo-under-bulk transport={Transport} link=[{Profile.Describe()}] keys={KeysSent} echoes={Echoes} idle[{Idle}] bulk[{UnderBulk}] bulk_rate={BulkBytesPerSecond / 1_000_000:0.00}MB/s{proxy}");
        }
    }
}

/// <summary>
/// The client control stream ended while a key waited for its echo. No echo
/// can come after that. <see cref="SessionEnd"/> is the close outcome of the
/// framed session, when the session closed.
/// </summary>
internal sealed class EchoStreamEndedException(string message, string? sessionEnd)
    : IOException(message)
{
    public string? SessionEnd { get; } = sessionEnd;

    /// <summary>True when the framed session reset because its incoming control queue was full.</summary>
    public bool IsControlQueueReset =>
        SessionEnd is not null
        && SessionEnd.StartsWith(ConnectivityReasons.StreamReset + ":", StringComparison.Ordinal)
        && SessionEnd.Contains("control reserved capacity is exhausted", StringComparison.Ordinal);
}
