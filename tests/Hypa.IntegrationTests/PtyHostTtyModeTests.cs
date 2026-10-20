using System.Text;
using Hypa.Terminal.Pty;
using Xunit;

namespace Hypa.IntegrationTests;

/// <summary>
/// The native <c>hypa-pty-host</c> helper reports the termios ECHO and ICANON
/// state of the pane PTY in TtyMode frames. The helper reads the state through
/// the master. These tests drive the real helper on Linux and on macOS, and
/// read its frames in order. Like <see cref="PtyHostNativeTests"/>, they need
/// the helper binary and never skip.
/// <para>
/// Each child script waits for a start file before it changes the mode. The
/// helper sends its first TtyMode frame right after Spawned, so without that
/// wait the child could change the mode before the first frame.
/// </para>
/// </summary>
public sealed class PtyHostTtyModeTests
{
    private static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Echo_off_in_child_reports_echo_false_on_next_mode_frame()
    {
        using var files = new SyncFiles();
        await using var host = await HelperSession.SpawnAsync(
            $"while [ ! -e '{files.Start}' ]; do sleep 0.02; done; "
            + "stty -echo; printf MARK-OFF; read x; stty echo; printf MARK-ON; read y");

        Assert.True(
            host.Supervisor.HelperProtocolMinor >= PtyHostFrameCodec.TtyModeMinor,
            $"helper minor {host.Supervisor.HelperProtocolMinor}: the helper is too old. helper={host.Helper}");
        var initial = await host.NextModeAsync();
        Assert.True(initial.Echo, "a new PTY starts with echo on");
        Assert.True(initial.Canonical, "a new PTY starts in canonical mode");

        files.Release();
        // The child turns echo off, then prints the marker. The helper reads
        // the marker, finds the change, and sends the mode first.
        var off = await host.ReadUntilOutputAsync("MARK-OFF");
        var offMode = Assert.Single(off.Modes);
        Assert.False(offMode.Echo, "the first mode frame after the marker reports echo=false");
        Assert.True(offMode.Canonical, "stty -echo keeps canonical mode");
        Assert.True(
            off.ModeBeforeOutput("MARK-OFF"),
            "the helper sends the mode change before the Output frame of the same read");

        // The key is not echoed. stty echo then prints the next marker.
        await host.WriteInputAsync("x\n");
        var on = await host.ReadUntilOutputAsync("MARK-ON");
        var onMode = Assert.Single(on.Modes);
        Assert.True(onMode.Echo, "after stty echo the next mode frame reports echo=true");
        Assert.DoesNotContain("x", on.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Raw_mode_with_echo_off_reports_echo_and_icanon_off()
    {
        using var files = new SyncFiles();
        await using var host = await HelperSession.SpawnAsync(
            $"while [ ! -e '{files.Start}' ]; do sleep 0.02; done; "
            + "stty raw -echo; printf MARK-RAW; read x");

        var initial = await host.NextModeAsync();
        Assert.True(initial.Echo && initial.Canonical);

        files.Release();
        var raw = await host.ReadUntilOutputAsync("MARK-RAW");
        var mode = Assert.Single(raw.Modes);
        Assert.False(mode.Echo);
        Assert.False(mode.Canonical, "a line editor or raw-mode program reports icanon off");
    }

    [Fact]
    public async Task Silent_echo_off_reports_echo_false_at_the_next_input_write()
    {
        using var files = new SyncFiles();
        await using var host = await HelperSession.SpawnAsync(
            $"while [ ! -e '{files.Start}' ]; do sleep 0.02; done; "
            + $"stty -echo; : > '{files.Done}'; read x; read y");

        var initial = await host.NextModeAsync();
        Assert.True(initial.Echo);

        files.Release();
        await files.WaitDoneAsync();
        // The child printed nothing. No output read can find the change, so
        // the helper sent no mode frame yet.
        Assert.Empty(host.ReceivedModes());

        await host.WriteInputAsync("secret\n");
        var mode = await host.NextModeAsync();
        Assert.False(mode.Echo, "the input write finds the silent stty -echo");
        Assert.True(mode.Canonical);
        Assert.DoesNotContain("secret", host.ReceivedOutput(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adopting_helper_sends_the_current_mode_before_its_first_output()
    {
        using var files = new SyncFiles();
        var helper = PtyHostNativeTests.ResolveHelper();
        var hostOptions = new PtyHostOptions { HelperPath = helper };
        using var guard = new CancellationTokenSource(FrameWait);

        await using var old = await PtyHostProcess.SpawnAsync(
            "/bin/sh",
            ["-c", $"stty -echo; : > '{files.Done}'; read x; printf AFTER-HANDOFF; read y"],
            files.Directory,
            80,
            24,
            hostOptions: hostOptions,
            ct: guard.Token);
        await files.WaitDoneAsync();

        var nonce = new byte[PtyHandoffMessage.NonceLength];
        Random.Shared.NextBytes(nonce);
        await using var exportPort = UnixPtyHandoffPort.Listen();
        var export = Task.Run(async () =>
        {
            await exportPort.AcceptAsync(guard.Token);
            return await old.ExportHandoffAsync(
                exportPort,
                new PtyHandoffExportOptions { Generation = 7, Nonce = nonce },
                guard.Token);
        });

        await using var importPort = await UnixPtyHandoffPort.ConnectAsync(exportPort.SocketPath!, guard.Token);
        var (adopted, _) = await PtyHostProcess.ImportHandoffAsync(
            importPort,
            new PtyHandoffAdoptOptions
            {
                Nonce = nonce,
                Generation = 7,
                ExpectedGeneration = 7,
                ChildPid = 1,
                HostOptions = hostOptions,
            },
            ct: guard.Token);
        await using var adoptedScope = adopted;
        var exported = await export;
        Assert.True(exported.IsOk, exported.Message);

        // No output byte is read yet. A mode that the new owner holds now
        // came in a frame before any Output frame of the new helper.
        PtyTtyMode? held = null;
        await Poll.UntilAsync(() => (held = adopted.TtyMode) is not null, "the mode of the new owner");
        var first = held!.Value;
        Assert.False(first.Echo, "the new owner starts with the mode that the child set");

        await adopted.StandardInput.WriteAsync("go\n"u8.ToArray(), guard.Token);
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (!text.ToString().Contains("AFTER-HANDOFF", StringComparison.Ordinal))
        {
            var n = await adopted.StandardOutput.ReadAsync(buffer, guard.Token);
            Assert.True(n > 0, "adopted output ended before the marker: " + text);
            text.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }

        Assert.DoesNotContain("go", text.ToString(), StringComparison.Ordinal);
        Assert.Equal(first, adopted.TtyMode);
    }

    /// <summary>Start and done files in a private directory. The child script polls the start file.</summary>
    private sealed class SyncFiles : IDisposable
    {
        public SyncFiles()
        {
            Directory = Path.Combine(Path.GetTempPath(), "hypa-ttymode-" + Guid.NewGuid().ToString("N")[..12]);
            System.IO.Directory.CreateDirectory(Directory);
        }

        public string Directory { get; }

        public string Start => Path.Combine(Directory, "start");

        public string Done => Path.Combine(Directory, "done");

        public void Release() => File.WriteAllText(Start, "");

        public Task WaitDoneAsync() =>
            Poll.UntilAsync(() => File.Exists(Done), "the child to create " + Done);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>What one wait for an output marker saw, in frame order.</summary>
    private sealed record OutputSpan(List<PtyHostFrame> Frames)
    {
        public List<PtyTtyMode> Modes =>
            [.. Frames.Where(f => f.Type == PtyHostFrameType.TtyMode).Select(f => PtyHostFrameCodec.ParseTtyMode(f.Payload))];

        public string Output =>
            string.Concat(Frames.Where(f => f.Type == PtyHostFrameType.Output).Select(f => Encoding.UTF8.GetString(f.Payload)));

        /// <summary>True when a TtyMode frame comes before the Output frame that holds <paramref name="marker"/>.</summary>
        public bool ModeBeforeOutput(string marker)
        {
            var seen = new StringBuilder();
            var mode = false;
            foreach (var frame in Frames)
            {
                if (frame.Type == PtyHostFrameType.TtyMode)
                    mode = true;
                if (frame.Type != PtyHostFrameType.Output)
                    continue;
                seen.Append(Encoding.UTF8.GetString(frame.Payload));
                if (seen.ToString().Contains(marker, StringComparison.Ordinal))
                    return mode;
            }

            return false;
        }
    }

    /// <summary>
    /// One helper with one child, driven through the frame protocol. A
    /// background reader keeps every helper frame in order.
    /// </summary>
    private sealed class HelperSession : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<PtyHostFrame> _frames = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly SemaphoreSlim _arrived = new(0);
        private Task _reader = Task.CompletedTask;
        private int _taken;

        private HelperSession(PtyHostSupervisor supervisor, string helper)
        {
            Supervisor = supervisor;
            Helper = helper;
        }

        public PtyHostSupervisor Supervisor { get; }

        public string Helper { get; }

        public static async Task<HelperSession> SpawnAsync(string script)
        {
            var helper = PtyHostNativeTests.ResolveHelper();
            var supervisor = await PtyHostSupervisor.StartAsync(new PtyHostOptions { HelperPath = helper });
            var session = new HelperSession(supervisor, helper);
            await supervisor.WriteFrameAsync(PtyHostFrameCodec.CreateSpawn(
                80,
                24,
                Path.GetTempPath(),
                ["/bin/sh", "-c", script],
                ChildEnvironmentBuilder.BuildHosted(null)));
            var spawned = await supervisor.ReadFrameAsync().WaitAsync(FrameWait);
            Assert.Equal(PtyHostFrameType.Spawned, spawned.Type);
            session._reader = Task.Run(session.ReadLoopAsync);
            return session;
        }

        public Task WriteInputAsync(string text) =>
            Supervisor.WriteFrameAsync(PtyHostFrameCodec.CreateInput(Encoding.UTF8.GetBytes(text)));

        /// <summary>The next TtyMode frame after the frames taken so far.</summary>
        public async Task<PtyTtyMode> NextModeAsync()
        {
            var deadline = DateTime.UtcNow + FrameWait;
            while (true)
            {
                lock (_gate)
                {
                    for (; _taken < _frames.Count; _taken++)
                    {
                        if (_frames[_taken].Type != PtyHostFrameType.TtyMode)
                            continue;
                        var mode = PtyHostFrameCodec.ParseTtyMode(_frames[_taken].Payload);
                        _taken++;
                        return mode;
                    }
                }

                await WaitFrameAsync(deadline, "a TtyMode frame");
            }
        }

        /// <summary>The frames after the frames taken so far, up to the Output frame that completes <paramref name="marker"/>.</summary>
        public async Task<OutputSpan> ReadUntilOutputAsync(string marker)
        {
            var deadline = DateTime.UtcNow + FrameWait;
            var span = new List<PtyHostFrame>();
            var text = new StringBuilder();
            while (true)
            {
                lock (_gate)
                {
                    for (; _taken < _frames.Count; _taken++)
                    {
                        var frame = _frames[_taken];
                        span.Add(frame);
                        if (frame.Type != PtyHostFrameType.Output)
                            continue;
                        text.Append(Encoding.UTF8.GetString(frame.Payload));
                        if (!text.ToString().Contains(marker, StringComparison.Ordinal))
                            continue;
                        _taken++;
                        return new OutputSpan(span);
                    }
                }

                await WaitFrameAsync(deadline, "output " + marker + " (have '" + text + "')");
            }
        }

        /// <summary>The TtyMode frames after the frames taken so far. Takes nothing.</summary>
        public List<PtyTtyMode> ReceivedModes()
        {
            lock (_gate)
            {
                return [.. _frames.Skip(_taken)
                    .Where(f => f.Type == PtyHostFrameType.TtyMode)
                    .Select(f => PtyHostFrameCodec.ParseTtyMode(f.Payload))];
            }
        }

        public string ReceivedOutput()
        {
            lock (_gate)
            {
                return string.Concat(_frames
                    .Where(f => f.Type == PtyHostFrameType.Output)
                    .Select(f => Encoding.UTF8.GetString(f.Payload)));
            }
        }

        private async Task WaitFrameAsync(DateTime deadline, string what)
        {
            var left = deadline - DateTime.UtcNow;
            Assert.True(left > TimeSpan.Zero, "no " + what + " from the helper. helper=" + Helper);
            await _arrived.WaitAsync(left);
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var frame = await Supervisor.ReadFrameAsync(_cts.Token);
                    lock (_gate)
                        _frames.Add(frame);
                    _arrived.Release();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException
                or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The helper exited or the test ended.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Supervisor.DisposeAsync();
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            _cts.Dispose();
        }
    }
}
