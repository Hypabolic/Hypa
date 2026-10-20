using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.ReleaseNotes;
using Hypa.ControlPlane;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A real <c>hypa attach</c> on a PTY of hypa-pty-host, with an isolated
/// mux. A shell holds the PTY, as in a terminal window. A Ghostty engine
/// gets all bytes from the PTY master and plays the host terminal: its
/// replies go back to the attach. After a <c>kill -9</c> of the attach, the
/// host mode guard must reset the host modes. After a clean detach, the
/// guard must write nothing.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed partial class HostModeGuardPtyTests(ITestOutputHelper log)
{
    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    // The modes of the acceptance criteria. The attach sets 1004, 1006, 1003,
    // 1049 and 2004. The seed sets 1, 66, 1000 and 1002: an earlier program
    // can leave them on, and the guard resets all. The attach does not set
    // 1 and 66, because it does not start the .NET terminal setup.
    private static readonly int[] GuardedModes = [1, 66, 1000, 1002, 1003, 1004, 1006, 1049, 2004];

    private const string HostSeed = "\u001b[?1h\u001b=\u001b[?1000h\u001b[?1002h";

    [SkippableFact]
    public async Task Killed_attach_leaves_the_host_modes_reset()
    {
        await using var attach = await AttachOnPty.StartAsync(LibPath, log);
        await attach.WaitForAttachModesAsync();
        Assert.True(attach.KittyPushed, "the attach pushed the kitty flags: " + attach.Tail());
        var guardPid = attach.GuardPid();
        Assert.True(guardPid > 0, "one host mode guard runs beside the attach");
        foreach (var mode in GuardedModes)
            Assert.Equal(1, attach.ModeState(mode));
        Assert.True(attach.KittyFlags() > 0, "the host has kitty flags before the kill");
        Assert.False(attach.TtyCanonicalEcho(), "the attach owns the PTY in raw mode");
        var mark = attach.OutputLength;

        Assert.Equal(0, kill(attach.AttachPid, Sigkill));

        var crash = ShellHostModeGuard.CrashBytes(pushed: true);
        await attach.WaitForOutputAsync(text => text.IndexOf(crash, mark, StringComparison.Ordinal) >= 0, "the guard restore bytes");
        await Poll.UntilAsync(() => !IsAlive(guardPid), "the guard exits after its write");
        foreach (var mode in GuardedModes)
            Assert.True(attach.ModeState(mode) == 2, $"mode {mode} is reset after the kill");
        Assert.Equal(0, attach.KittyFlags());
        Assert.True(attach.TtyCanonicalEcho(), "the guard ran stty sane");
    }

    [SkippableFact]
    public async Task Clean_detach_writes_the_restore_once_and_the_guard_writes_nothing()
    {
        await using var attach = await AttachOnPty.StartAsync(LibPath, log);
        await attach.WaitForAttachModesAsync();
        var guardPid = attach.GuardPid();
        Assert.True(guardPid > 0, "one host mode guard runs beside the attach");

        await attach.DetachWithPrefixQAsync();
        await Poll.UntilAsync(() => !IsAlive(guardPid), "the guard exits after the disarm");
        // The PTY keeps the order of its writes. A marker written after the
        // guard exit comes after each byte that the guard wrote.
        await attach.WriteMarkerAfterTheGuardAsync();

        var text = attach.Text();
        Assert.Equal(1, Count(text, SnapshotPainter.HostModeResetSequence));
        Assert.Equal(1, Count(text, SnapshotPainter.DisableFocusReport));
        Assert.Equal(0, Count(text, SnapshotPainter.CancelSequence));
        foreach (var mode in GuardedModes)
            Assert.True(attach.ModeState(mode) == 2, $"mode {mode} is reset after the detach");
        Assert.Equal(0, attach.KittyFlags());
        Assert.True(attach.TtyCanonicalEcho(), "the restore put back cooked mode");
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += part.Length;
        }

        return count;
    }

    private static bool IsAlive(int pid) => kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() != Esrch;

    private const int Sigkill = 9;
    private const int Esrch = 3;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    /// <summary>
    /// One isolated mux, and one attach on a PTY of hypa-pty-host. Latin-1
    /// text keeps one char for each byte of the PTY output.
    /// </summary>
    private sealed partial class AttachOnPty : IAsyncDisposable
    {
        private const int Cols = 100;
        private const int Rows = 30;

        private readonly ITestOutputHelper _log;
        private readonly string _dir;
        private readonly object _gate = new();
        private readonly List<byte> _output = [];
        private readonly CancellationTokenSource _stop = new();
        private Process? _mux;
        private StringBuilder _muxErr = new();
        private IPtyProcess? _pty;
        private GhosttyVtEngine? _host;
        private Task _pump = Task.CompletedTask;
        private int _guardPid;

        private AttachOnPty(ITestOutputHelper log)
        {
            _log = log;
            _dir = Path.Combine("/tmp", "hmg-" + Guid.NewGuid().ToString("N")[..8]);
        }

        public int AttachPid { get; private set; }

        public string TtyPath { get; private set; } = "";

        public bool KittyPushed => Text().Contains(HostKeyboardMode.PushSequence, StringComparison.Ordinal);

        public int OutputLength
        {
            get { lock (_gate) return _output.Count; }
        }

        public static async Task<AttachOnPty> StartAsync(string? libPath, ITestOutputHelper log)
        {
            Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Linux and macOS are the mux hosts.");
            GhosttyTestRequire.RequireNativeLibrary(libPath);
            var helper = GhosttyTestRequire.RequirePtyHostHelper(PtyHostSupervisor.ResolveHelperPath());
            var attach = new AttachOnPty(log);
            try
            {
                await attach.StartCoreAsync(libPath!, helper);
                return attach;
            }
            catch
            {
                await attach.DisposeAsync();
                throw;
            }
        }

        private async Task StartCoreAsync(string libPath, string helper)
        {
            var (hypa, prefix) = FindHypa();
            var session = Path.GetFileName(_dir);
            var socket = Path.Combine(_dir, "hypa.sock");
            var home = Path.Combine(_dir, "home");
            var config = Path.Combine(home, ".config", "hypa", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            await File.WriteAllTextAsync(config, "onboarding = false\n[ui]\nstartup_splash = false\n");
            // A new home shows the notes of this version once, and the first
            // key only closes them. Mark the notes seen, so the first key
            // goes to the attach.
            await File.WriteAllTextAsync(
                Path.Combine(Path.GetDirectoryName(config)!, PackNotesSeenStore.FileName),
                new JsonObject { ["Version"] = PackNotesLoader.ResolveCurrentVersion() }.ToJsonString());
            var env = new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["HYPA_CUBE_HOME"] = home,
                ["HYPA_CONFIG_PATH"] = config,
                ["HYPA_SESSION"] = session,
                ["HYPA_RUNTIME_SOCKET"] = socket,
                ["HYPA_RUNTIME_STATE_DIR"] = _dir,
                ["HYPA_GHOSTTY_VT"] = libPath,
                ["HYPA_VT_PROVIDER"] = "ghostty",
                ["HYPA_PTY_PROVIDER"] = "process-io",
                ["SHELL"] = "/bin/sh",
                ["TERM"] = "xterm-256color",
                ["DOTNET_ROOT"] = DotnetRoot(),
            };

            StartMux(hypa, prefix, session, env);
            await WaitForPingAsync(socket);

            _host = new GhosttyVtEngine(Cols, Rows, libraryPathOverride: libPath);
            FeedHost(Encoding.ASCII.GetBytes(HostSeed));

            // The outer shell holds the PTY, as a login shell does. The inner
            // shell prints its pid and its terminal, then becomes the attach.
            const string script =
                "unset HYPA_ENV; "
                + "/bin/sh -c 'printf \"HYPA-ATTACH %s %s\\n\" \"$$\" \"$(tty)\"; exec \"$@\"' attach \"$@\"; "
                + "printf 'HYPA-ATTACH-EXIT %s\\n' \"$?\"; exec sleep 600";
            var args = new List<string> { "-c", script, "hmg", hypa };
            args.AddRange(prefix);
            args.AddRange(["attach", "--session", session]);
            var factory = new PtyProcessFactory(new PtyProviderOptions
            {
                Provider = PtyProviderKind.HypaPtyHost,
                HelperPath = helper,
            });
            _pty = factory.Spawn("/bin/sh", args, _dir, Cols, Rows, env);
            _pump = Task.Run(PumpAsync);

            await WaitForOutputAsync(text => AttachLine().IsMatch(text), "the attach pid line");
            var match = AttachLine().Match(Text());
            AttachPid = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            TtyPath = match.Groups[2].Value;
        }

        public async Task WaitForAttachModesAsync()
        {
            await WaitForOutputAsync(
                text => text.Contains(SnapshotPainter.EnableFocusReport, StringComparison.Ordinal)
                    && text.Contains(SnapshotPainter.EnableBracketedPaste, StringComparison.Ordinal)
                    && text.Contains(Hypa.Cli.Attach.Mouse.MouseCapture.EnableSequence, StringComparison.Ordinal)
                    && text.Contains(HostKeyboardMode.PushSequence, StringComparison.Ordinal),
                "the attach host modes and the kitty push");
        }

        /// <summary>
        /// Send prefix+q once, then wait for the attach exit. Call this after
        /// <see cref="WaitForAttachModesAsync"/>: the attach pushes the kitty
        /// flags when its input loop reads the host reply, so the input loop
        /// then reads the key.
        /// </summary>
        public async Task DetachWithPrefixQAsync()
        {
            Assert.True(KittyPushed, "the input loop of the attach runs: " + Tail());
            await _pty!.StandardInput.WriteAsync("\u0002q"u8.ToArray());
            await _pty.StandardInput.FlushAsync();
            await WaitForOutputAsync(
                text => text.Contains("HYPA-ATTACH-EXIT", StringComparison.Ordinal),
                "the attach exit after prefix+q");
        }

        /// <summary>
        /// Writes a marker to the PTY from a new process, then waits until the
        /// pump reads it.
        /// </summary>
        public async Task WriteMarkerAfterTheGuardAsync()
        {
            var marker = "HYPA-MARK-" + Guid.NewGuid().ToString("N")[..8];
            RunText("/bin/sh", "-c", "printf '%s\\n' \"$2\" > \"$1\"", "mark", TtyPath, marker);
            await WaitForOutputAsync(text => text.Contains(marker, StringComparison.Ordinal), "the marker after the guard");
        }

        public async Task WaitForOutputAsync(Func<string, bool> ready, string what)
        {
            await Poll.UntilAsync(
                () => _pump.IsCompleted || ready(Text()),
                () => $"{what}. mux stderr: {_muxErr}\noutput tail: {Tail()}");
            if (!ready(Text()))
                Assert.Fail($"the PTY closed before {what}. mux stderr: {_muxErr}\noutput tail: {Tail()}");
        }

        public string Text()
        {
            lock (_gate)
                return Encoding.Latin1.GetString(_output.ToArray());
        }

        public string Tail()
        {
            var text = Text();
            var tail = text.Length > 1500 ? text[^1500..] : text;
            return tail.Replace("\u001b", "\\e", StringComparison.Ordinal);
        }

        /// <summary>DECRQM state of a DEC private mode: 1 set, 2 reset.</summary>
        public int ModeState(int mode)
        {
            var reply = QueryHost($"\u001b[?{mode}$p");
            var match = Regex.Match(reply, @"\[\?" + mode + @";(\d)\$y");
            Assert.True(match.Success, $"DECRQM {mode} reply: {reply.Replace("\u001b", "\\e", StringComparison.Ordinal)}");
            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        public int KittyFlags()
        {
            var reply = QueryHost("\u001b[?u");
            var match = Regex.Match(reply, @"\[\?(\d+)u");
            Assert.True(match.Success, "kitty flags reply: " + reply.Replace("\u001b", "\\e", StringComparison.Ordinal));
            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>True when the PTY has ICANON and ECHO on (cooked mode).</summary>
        public bool TtyCanonicalEcho()
        {
            var flags = RunText("/bin/sh", "-c", "stty -a < \"$1\"", "stty", TtyPath);
            return Regex.IsMatch(flags, @"(?<![-\w])icanon\b") && Regex.IsMatch(flags, @"(?<![-\w])echo\b");
        }

        /// <summary>The pid of the host mode guard child of the attach, or 0.</summary>
        public int GuardPid()
        {
            if (_guardPid > 0)
                return _guardPid;
            foreach (var line in RunText("ps", "-A", "-o", "pid=,ppid=,command=").Split('\n'))
            {
                var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3
                    && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var ppid)
                    && ppid == AttachPid
                    && parts[2].Contains("hypa-host-mode-guard", StringComparison.Ordinal))
                {
                    _guardPid = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    return _guardPid;
                }
            }

            return 0;
        }

        private string QueryHost(string query)
        {
            var replies = new List<byte[]>();
            lock (_gate)
                _host!.FeedChildOutput(Encoding.ASCII.GetBytes(query), replies);
            return string.Concat(replies.Select(r => Encoding.ASCII.GetString(r)));
        }

        private void FeedHost(byte[] bytes)
        {
            var replies = new List<byte[]>();
            lock (_gate)
                _host!.FeedChildOutput(bytes, replies);
        }

        private async Task PumpAsync()
        {
            var buf = new byte[16384];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var n = await _pty!.StandardOutput.ReadAsync(buf, _stop.Token);
                    if (n <= 0)
                        return;
                    var replies = new List<byte[]>();
                    lock (_gate)
                    {
                        _output.AddRange(buf.AsSpan(0, n));
                        _host!.FeedChildOutput(buf.AsSpan(0, n), replies);
                    }

                    // The host answers the attach queries, as a terminal does.
                    foreach (var reply in replies)
                        await _pty.StandardInput.WriteAsync(reply, _stop.Token);
                    if (replies.Count > 0)
                        await _pty.StandardInput.FlushAsync(_stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        private void StartMux(string hypa, IReadOnlyList<string> prefix, string session, Dictionary<string, string> env)
        {
            var psi = new ProcessStartInfo(hypa)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _dir,
            };
            foreach (var arg in prefix)
                psi.ArgumentList.Add(arg);
            foreach (var arg in new[] { "mux", "serve", "--session", session, "--cwd", _dir, "--state-dir", _dir })
                psi.ArgumentList.Add(arg);
            psi.Environment.Remove("HYPA_ENV");
            foreach (var (key, value) in env)
                psi.Environment[key] = value;
            psi.Environment["HYPA_MUX_LOG"] = Path.Combine(_dir, "mux.log");
            _mux = Process.Start(psi) ?? throw new InvalidOperationException("mux serve did not start");
            var err = _muxErr;
            _mux.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    lock (err) err.AppendLine(e.Data);
            };
            _mux.BeginErrorReadLine();
            _ = _mux.StandardOutput.ReadToEndAsync();
            _mux.StandardInput.Close();
        }

        private async Task WaitForPingAsync(string socket)
        {
            Exception? last = null;
            var answered = false;
            await Poll.UntilAsync(
                async () =>
                {
                    if (_mux!.HasExited)
                        return true;
                    try
                    {
                        await using var client = new ControlPlaneClient(socket, connectTimeout: TimeSpan.FromSeconds(1));
                        await client.ConnectAsync();
                        _ = await client.CallAsync(ProtocolMethods.Ping);
                        answered = true;
                        return true;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        return false;
                    }
                },
                () => $"the mux ping on {socket}: {last?.Message}; stderr={_muxErr}");

            if (!answered)
                Assert.Fail($"the mux exited before it answered ping on {socket}: {last?.Message}; stderr={_muxErr}");
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            // A dead pid can belong to another process now. Kill only a
            // process that is still the guard or the attach of this test.
            KillIfOurs(_guardPid, "hypa-host-mode-guard");
            KillIfOurs(AttachPid, "--session " + Path.GetFileName(_dir));
            if (_pty is not null)
            {
                try { await _pty.DisposeAsync(); }
                catch (Exception ex) { _log.WriteLine("pty dispose: " + ex.Message); }
            }

            try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }

            if (_mux is { HasExited: false })
            {
                try
                {
                    _mux.Kill(entireProcessTree: true);
                    await _mux.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) { _log.WriteLine("mux stop: " + ex.Message); }
            }

            _mux?.Dispose();
            _host?.Dispose();
            _stop.Dispose();
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void KillIfOurs(int pid, string marker)
        {
            if (pid <= 0)
                return;
            var command = RunText("ps", "-o", "command=", "-p", pid.ToString(CultureInfo.InvariantCulture));
            if (command.Contains(marker, StringComparison.Ordinal))
                _ = kill(pid, Sigkill);
        }

        private static (string FileName, string[] Prefix) FindHypa()
        {
            var native = Path.Combine(AppContext.BaseDirectory, "hypa");
            if (File.Exists(native))
                return (native, []);
            var dll = Path.Combine(AppContext.BaseDirectory, "hypa.dll");
            Assert.True(File.Exists(dll), "the test output has no hypa CLI: " + AppContext.BaseDirectory);
            return (Path.Combine(DotnetRoot(), "dotnet"), ["exec", dll]);
        }

        // The runtime directory is <root>/shared/Microsoft.NETCore.App/<version>.
        private static string DotnetRoot() =>
            Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root
                ? root
                : Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

        private static string RunText(string file, params string[] args)
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi)!;
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return text;
        }

        [GeneratedRegex(@"HYPA-ATTACH (\d+) (/dev/\S+)")]
        private static partial Regex AttachLine();
    }
}
