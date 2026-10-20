using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The local attach heartbeat with a manual clock: no ping while lines
/// arrive, one ping after the quiet interval, and a stall only when that
/// ping fails. The maintenance work runs on its own timer.
/// </summary>
public sealed class LocalLinkHeartbeatTests
{
    private const string EventLine = "{\"event\":\"runtime.event\",\"params\":{\"type\":\"test.tick\"}}\n";

    [SkippableFact]
    public async Task No_ping_while_lines_arrive()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket peer");
        using var peer = LinkPeer.Listen();
        await using var client = peer.ConnectClient();
        peer.Respond(answerPings: true);
        var time = new ManualTimeProvider();
        var probe = ProbeRun.Start(client, time);
        await time.WaitForArmedTimerAsync();

        // 10 s of a busy link: one line in each tick.
        for (var i = 0; i < 40; i++)
        {
            var before = client.LinesReceived;
            peer.Send(EventLine);
            Until(() => client.LinesReceived > before, "the client did not read the line");
            await probe.TickAsync(time);
        }

        Assert.DoesNotContain(LocalLinkStep.Ping, probe.Steps);
        Assert.DoesNotContain("ping", peer.Methods());
        Assert.False(probe.Loop.IsCompleted);
        await probe.StopAsync();
    }

    [SkippableFact]
    public async Task One_ping_after_the_quiet_interval_and_a_stall_only_when_it_fails()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket peer");
        using var peer = LinkPeer.Listen();
        await using var client = peer.ConnectClient();
        peer.Respond(answerPings: false);
        var time = new ManualTimeProvider();
        var liveness = new LocalLinkLiveness(time);
        var probe = ProbeRun.Start(client, time, liveness);
        await time.WaitForArmedTimerAsync();

        // The first tick starts the quiet time. No ping before the interval.
        var quietTicks = (int)(liveness.QuietInterval / AttachSession.LivenessTickPeriod);
        for (var i = 0; i < quietTicks; i++)
            await probe.TickAsync(time);
        Assert.All(probe.Steps, step => Assert.Equal(LocalLinkStep.Wait, step));

        await probe.TickAsync(time);
        Assert.Equal(LocalLinkStep.Ping, probe.Steps[^1]);
        Assert.Single(probe.Steps, step => step == LocalLinkStep.Ping);
        Until(() => peer.Methods().Contains("ping"), "the ping did not reach the mux");

        // The ping has no reply and no line comes. The attempt ends only
        // when the ping timeout has passed.
        var timeoutTicks = (int)(liveness.PingTimeout / AttachSession.LivenessTickPeriod);
        for (var i = 0; i < timeoutTicks - 1; i++)
        {
            await probe.TickAsync(time);
            Assert.False(probe.Loop.IsCompleted, $"stall after {i + 1} ticks of the ping timeout");
        }

        time.Advance(AttachSession.LivenessTickPeriod);
        await Assert.ThrowsAsync<ControlPlaneClientTimeoutException>(
            () => probe.Loop.WaitAsync(TestWait.HangGuard));
        Assert.Single(peer.Methods(), method => method == "ping");
    }

    [SkippableFact]
    public async Task Held_ping_reply_is_not_a_stall_while_the_mux_still_sends_lines()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket peer");
        using var peer = LinkPeer.Listen();
        await using var client = peer.ConnectClient();
        peer.Respond(answerPings: false);
        var time = new ManualTimeProvider();
        var liveness = new LocalLinkLiveness(time);
        var probe = ProbeRun.Start(client, time, liveness);
        await time.WaitForArmedTimerAsync();

        while (!probe.Steps.Contains(LocalLinkStep.Ping))
            await probe.TickAsync(time);
        Until(() => peer.Methods().Contains("ping"), "the ping did not reach the mux");

        // The mux holds the ping reply for 4 s and sends lines all the time.
        for (var i = 0; i < 16; i++)
        {
            var before = client.LinesReceived;
            peer.Send(EventLine);
            Until(() => client.LinesReceived > before, "the client did not read the line");
            await probe.TickAsync(time);
        }

        Assert.False(probe.Loop.IsCompleted, "a held ping reply started a stall");
        Assert.DoesNotContain(LocalLinkStep.Expired, probe.Steps);
        Assert.Single(peer.Methods(), method => method == "ping");
        await probe.StopAsync();
    }

    [SkippableFact]
    public async Task Maintenance_timer_drains_renews_shows_the_notice_and_detaches_within_one_period()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket peer");
        using var peer = LinkPeer.Listen();
        await using var local = peer.ConnectClient();
        peer.Respond(answerPings: true);
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        live.ControlSub = "sub_local";
        live.RenderSub = "sub_local";
        live.LastPublishedVisiblePaneIds = ["p1", "p2"];
        live.SourceMuxSocketPath = "/run/hypa.sock";
        var notice = new SidebarUpdateNotice(
            SidebarUpdateNoticeKind.RestartRequired,
            MuxRestartCommands.SidebarAction,
            MuxUpdateNoticePolicy.RestartPrompt);
        var notices = new RecordingNotices(notice);
        live.UpdateNotices = notices;
        var before = local.LinesReceived;
        peer.Send(EventLine);
        Until(() => local.LinesReceived > before && local.HasPendingReliableEvents, "the event was not queued");

        var time = new ManualTimeProvider();
        using var gate = new SemaphoreSlim(1, 1);
        using var linked = new CancellationTokenSource();
        using var attempt = new CancellationTokenSource();
        var beat = AttachSession.ControlHeartbeatAsync(
            local, gate, live, tty: null, linked, time, attempt.Token);
        // The liveness timer and the maintenance timer.
        await time.WaitForArmedTimerAsync();
        await time.WaitForArmedTimerAsync();

        time.Advance(AttachSession.ControlHeartbeatPeriod);
        Until(() => peer.Methods().Contains("terminal.visible_set"), "the visible set was not renewed");
        Until(() => Equals(live.UpdateNotice, notice), "the update notice did not show");
        Until(() => !local.HasPendingReliableEvents, "the overlay drain did not run");
        Assert.True(notices.Pongs > 0);
        Assert.False(linked.IsCancellationRequested);

        // A detach request ends the attempt on the next maintenance tick,
        // with no gate wait and no ping.
        live.InputDetachRequested = true;
        time.Advance(AttachSession.ControlHeartbeatPeriod);
        Until(() => linked.IsCancellationRequested, "the detach did not end the attempt");
        Assert.True(AttachSession.ControlHeartbeatPeriod <= TimeSpan.FromMilliseconds(500));

        attempt.Cancel();
        await Drain(beat);
    }

    [SkippableFact]
    public async Task Maintenance_call_timeout_is_logged_and_is_not_a_stall()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix socket peer");
        using var peer = LinkPeer.Listen();
        await using var local = peer.ConnectClient(callTimeout: TimeSpan.FromMilliseconds(200));
        peer.Respond(answerPings: true, hold: "terminal.visible_set");
        var live = MouseTestGeom.ApplyLive(MouseTestGeom.ApplyPort(), MouseTestGeom.Split());
        var sink = new RecordingLog();
        live.ProcessLog = sink;
        live.ControlSub = "sub_local";
        live.RenderSub = "sub_local";
        live.LastPublishedVisiblePaneIds = ["p1"];
        using var gate = new SemaphoreSlim(1, 1);
        using var linked = new CancellationTokenSource();

        await AttachSession.MaintenanceTickAsync(
            local, gate, live, tty: null, linked, notice: null, CancellationToken.None);

        Assert.Contains(sink.Records, r => r.Action == AttachSession.MaintenanceLogStage);
        Assert.False(linked.IsCancellationRequested);
        Assert.Equal(1, gate.CurrentCount);
    }

    private static async Task Drain(Task task)
    {
        try { await task.WaitAsync(TestWait.HangGuard); }
        catch (OperationCanceledException) { }
    }

    // A poll on a test counter. The limit only stops a hung run.
    private static void Until(Func<bool> condition, string message) =>
        Assert.True(SpinWait.SpinUntil(condition, TestWait.HangGuard), message);

    private sealed class RecordingLog : IProcessLogSink
    {
        private readonly List<ProcessLogRecord> _records = [];

        public List<ProcessLogRecord> Records
        {
            get { lock (_records) return [.. _records]; }
        }

        public bool IsEnabled(ProcessLogLevel level) => true;

        public bool Disabled => false;

        public string? Path => null;

        public void Write(ProcessLogRecord record)
        {
            lock (_records)
                _records.Add(record);
        }
    }

    private sealed class RecordingNotices(SidebarUpdateNotice notice) : IMuxUpdateNoticeSource
    {
        private int _pongs;

        public int Pongs => Volatile.Read(ref _pongs);

        public SidebarUpdateNotice? Evaluate(string? pingJson)
        {
            Interlocked.Increment(ref _pongs);
            return notice;
        }
    }

    /// <summary>A running liveness loop and the steps that it decided.</summary>
    private sealed class ProbeRun
    {
        private readonly SemaphoreSlim _ticks = new(0);
        private readonly List<LocalLinkStep> _steps = [];
        private readonly CancellationTokenSource _stop = new();

        private ProbeRun()
        {
        }

        public Task Loop { get; private set; } = Task.CompletedTask;

        public List<LocalLinkStep> Steps
        {
            get { lock (_steps) return [.. _steps]; }
        }

        public static ProbeRun Start(ControlPlaneClient client, ManualTimeProvider time, LocalLinkLiveness? liveness = null)
        {
            var run = new ProbeRun();
            run.Loop = AttachSession.LocalLinkProbeLoopAsync(
                () => client,
                liveness ?? new LocalLinkLiveness(time),
                time,
                run._stop.Token,
                step =>
                {
                    lock (run._steps)
                        run._steps.Add(step);
                    run._ticks.Release();
                });
            return run;
        }

        /// <summary>Moves the clock one liveness tick and waits for that tick.</summary>
        public async Task TickAsync(ManualTimeProvider time)
        {
            time.Advance(AttachSession.LivenessTickPeriod);
            var done = await Task.WhenAny(_ticks.WaitAsync(TestWait.HangGuard), Loop);
            if (ReferenceEquals(done, Loop))
                await Loop;
        }

        public async Task StopAsync()
        {
            _stop.Cancel();
            await Drain(Loop);
        }
    }

    /// <summary>A Unix socket mux peer. It records each request and can hold the ping reply.</summary>
    private sealed class LinkPeer : IDisposable
    {
        private readonly Socket _listener;
        private readonly List<string> _methods = [];
        private NetworkStream? _stream;

        private LinkPeer(Socket listener, string path)
        {
            _listener = listener;
            Path = path;
        }

        private string Path { get; }

        internal static LinkPeer Listen()
        {
            var path = "/tmp/hypa-link-" + Guid.NewGuid().ToString("N")[..8] + ".sock";
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
            return new LinkPeer(listener, path);
        }

        internal ControlPlaneClient ConnectClient(TimeSpan? callTimeout = null)
        {
            var client = new ControlPlaneClient(
                Path, connectTimeout: TestWait.HangGuard, callTimeout: callTimeout ?? TestWait.HangGuard);
            var accept = _listener.AcceptAsync();
            client.ConnectAsync().GetAwaiter().GetResult();
            _stream = new NetworkStream(accept.GetAwaiter().GetResult(), ownsSocket: true);
            return client;
        }

        internal List<string> Methods()
        {
            lock (_methods)
                return [.. _methods];
        }

        internal void Respond(bool answerPings, string? hold = null)
        {
            var stream = _stream!;
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
                        var method = doc.RootElement.GetProperty("method").GetString()!;
                        lock (_methods)
                            _methods.Add(method);
                        if ((method == "ping" && !answerPings) || method == hold)
                            continue;
                        var result = method == "ping"
                            ? """{"ok":true,"protocol":1,"version":"1.0.3","install_present":false}"""
                            : "{}";
                        Write("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}\n");
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

        internal void Send(string line) => Write(line);

        private void Write(string text)
        {
            var stream = _stream!;
            lock (stream)
            {
                stream.Write(Encoding.UTF8.GetBytes(text));
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
