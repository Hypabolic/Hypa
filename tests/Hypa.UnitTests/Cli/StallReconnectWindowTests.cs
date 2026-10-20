using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// While a stall reconnect runs, the detach key still ends the attach and
/// the action socket still answers <c>latency.report</c>.
/// </summary>
public sealed class StallReconnectWindowTests
{
    [SkippableFact]
    public async Task Detach_key_ends_a_reconnect_that_never_finishes_and_latency_report_answers()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix action socket");
        var dir = ShortDir();
        var live = Live();
        await using var stdin = new ScriptedStdin();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        // This reconnect never ends and does not look at its token.
        var never = new TaskCompletionSource<AttachSession.StallReconnectResult>();
        try
        {
            var window = StallReconnectWindow.RunAsync(
                token =>
                {
                    started.TrySetResult(token);
                    return never.Task;
                },
                _ => stdin,
                live.Table,
                token => AttachSession.TryStartReconnectingActionHost(dir, live, token),
                _ => { },
                CancellationToken.None);
            var reconnectToken = await started.Task.WaitAsync(TestWait.HangGuard);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var stdout = new StringWriter();
            var exit = await AttachSemanticClient.ExecuteAsync(
                dir,
                attachClientId: null,
                new AttachSemanticRequest { Action = AttachSemanticActions.LatencyReport },
                stdout,
                cts.Token);
            Assert.Equal(AttachSemanticClient.ExitOk, exit);
            using (var doc = JsonDocument.Parse(stdout.ToString()))
            {
                Assert.Equal(AttachSemanticOutcomes.Applied, doc.RootElement.GetProperty("outcome").GetString());
                Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("latency").ValueKind);
            }

            stdout = new StringWriter();
            _ = await AttachSemanticClient.ExecuteAsync(
                dir,
                attachClientId: null,
                new AttachSemanticRequest { Action = AttachSemanticActions.TabFocus, TabId = "tab_two" },
                stdout,
                cts.Token);
            using (var doc = JsonDocument.Parse(stdout.ToString()))
                Assert.Equal(AttachSemanticOutcomes.Reconnecting, doc.RootElement.GetProperty("outcome").GetString());

            // Keys that are not the detach key do not end the window.
            stdin.Push("ls"u8.ToArray());
            Assert.False(window.IsCompleted);

            // The detach key: the prefix, then q.
            var clock = Stopwatch.StartNew();
            stdin.Push([0x02, (byte)'q']);
            var result = await window.WaitAsync(TestWait.HangGuard);
            clock.Stop();

            Assert.True(result.Detached);
            Assert.Null(result.ExitReason);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"detach took {clock.Elapsed}");
            Assert.True(reconnectToken.IsCancellationRequested);
            Assert.False(never.Task.IsCompleted);
            Assert.True(stdin.Disposed);
        }
        finally
        {
            TryDeleteDir(dir);
        }
    }

    [Fact]
    public async Task Reconnect_that_paints_after_the_detach_key_writes_nothing_to_the_host()
    {
        var live = Live();
        var capture = new MemoryStream();
        var tty = new UnixRawTerminal(capture, 80, 24);
        await using var stdin = new ScriptedStdin();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var painted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = StallReconnectWindow.RunAsync(
            async _ =>
            {
                started.SetResult();
                // This reconnect does not look at its token. It paints after
                // the user left, as a late snapshot apply could.
                await resume.Task.ConfigureAwait(false);
                painted.SetResult(tty.WriteHostEncoded("late frame"u8));
                return new AttachSession.StallReconnectResult(false, null, null, "late");
            },
            _ => stdin,
            live.Table,
            _ => null,
            _ => { },
            CancellationToken.None,
            onAbandon: tty.CloseHostOutput);
        await started.Task.WaitAsync(TestWait.HangGuard);
        tty.WriteBytes("before"u8);

        stdin.Push([0x02, (byte)'q']);
        var result = await window.WaitAsync(TestWait.HangGuard);
        Assert.True(result.Detached);
        Assert.True(tty.HostOutputClosed);

        resume.SetResult();
        Assert.False(await painted.Task.WaitAsync(TestWait.HangGuard));
        Assert.Equal("before", System.Text.Encoding.UTF8.GetString(capture.ToArray()));
    }

    [Fact]
    public async Task Reconnect_that_ends_first_returns_its_result()
    {
        var live = Live();
        await using var stdin = new ScriptedStdin();
        var done = new TaskCompletionSource<AttachSession.StallReconnectResult>();
        var window = StallReconnectWindow.RunAsync(
            _ => done.Task,
            _ => stdin,
            live.Table,
            _ => null,
            _ => { },
            CancellationToken.None);
        stdin.Push("abc"u8.ToArray());
        done.SetResult(new AttachSession.StallReconnectResult(false, null, null, "reconnect failed"));

        var result = await window.WaitAsync(TestWait.HangGuard);

        Assert.False(result.Detached);
        Assert.Equal("reconnect failed", result.Reconnect?.Detail);
        Assert.True(stdin.Disposed);
    }

    [Fact]
    public async Task Closed_stdin_ends_the_window()
    {
        var live = Live();
        await using var stdin = new ScriptedStdin();
        var never = new TaskCompletionSource<AttachSession.StallReconnectResult>();
        var window = StallReconnectWindow.RunAsync(
            _ => never.Task,
            _ => stdin,
            live.Table,
            _ => null,
            _ => { },
            CancellationToken.None);
        stdin.End();

        var result = await window.WaitAsync(TestWait.HangGuard);

        Assert.True(result.Detached);
        Assert.Equal(StallReconnectWindow.StdinClosed, result.ExitReason);
    }

    [Fact]
    public void Stall_record_names_the_reason_the_loop_and_its_fault()
    {
        var sink = new ListProcessLog();
        var live = Live(sink);
        var render = new TaskCompletionSource().Task;
        var beat = Task.FromException(new ControlPlaneClientTimeoutException("control ping timed out"));

        AttachSession.RecordAttachStall(
            live, "control ping timed out", beat, render, render, render, beat, render);

        var record = Assert.Single(sink.Records);
        Assert.Equal(AttachSession.StallLogStage, record.Action);
        Assert.Contains("control ping timed out", record.Err, StringComparison.Ordinal);
        Assert.Contains("loop=heartbeat faulted", record.Err, StringComparison.Ordinal);
        Assert.Contains(nameof(ControlPlaneClientTimeoutException), record.Err, StringComparison.Ordinal);
    }

    private static AttachLiveState Live(ListProcessLog? sink = null)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new MouseRecordingPort(), "ws", "tab_one", "p1", "lease-r"),
            ProcessLog = sink ?? new ListProcessLog(),
            ChromeEnabled = false,
            PaneId = "p1",
            TabId = "tab_one",
            WorkspaceId = "ws",
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            AttachClientId = "attach-stall",
        };
    }

    private static string ShortDir()
    {
        var dir = Path.Combine("/tmp", "hsw" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class ListProcessLog : IProcessLogSink
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

    /// <summary>A stdin source that yields the bytes a test pushes.</summary>
    private sealed class ScriptedStdin : IAttachStdinSource
    {
        private readonly Channel<byte[]?> _reads = Channel.CreateUnbounded<byte[]?>();
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public bool ThreadAlive => !Disposed;

        public void Push(byte[] bytes) => _reads.Writer.TryWrite(bytes);

        public void End() => _reads.Writer.TryWrite(null);

        public async ValueTask<AttachStdinRead> ReadAsync(byte[] buffer, int timeoutMs, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                var item = await _reads.Reader.ReadAsync(timeout.Token);
                if (item is null)
                    return AttachStdinRead.End;
                item.CopyTo(buffer, 0);
                return AttachStdinRead.Bytes(item.Length);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return AttachStdinRead.Timeout;
            }
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
