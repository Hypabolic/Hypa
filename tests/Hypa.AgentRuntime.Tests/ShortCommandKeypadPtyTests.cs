using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A short <c>hypa</c> command on a PTY of hypa-pty-host. A shell holds the
/// PTY, as in a terminal window. A Ghostty engine gets all bytes from the
/// PTY master and plays the host terminal. After the command, the host must
/// be in normal cursor mode (1) and normal keypad mode (66), so the arrow
/// keys of the shell send <c>ESC [ A</c>.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public sealed partial class ShortCommandKeypadPtyTests(ITestOutputHelper log)
{
    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    private const string KeypadXmit = "\u001b[?1h";

    public static TheoryData<string[]> ShortCommands => new()
    {
        { new[] { "--version" } },
        { new[] { "--help" } },
        { new[] { "ping" } },
        { new[] { "config", "show" } },
        { new[] { "no-such-command" } },
        // The worker entry paths parse with a root of their own.
        { new[] { "code", "parse-worker", "--no-such-option" } },
        { new[] { "code", "index", "--emit", "out.json", "--no-such-option" } },
    };

    [SkippableTheory]
    [MemberData(nameof(ShortCommands))]
    public async Task Short_command_leaves_normal_cursor_and_keypad_modes(string[] command)
    {
        await using var run = await CommandOnPty.StartAsync(LibPath, log, "\"$@\"", command);
        await run.WaitForExitAsync();

        Assert.Equal(2, run.ModeState(1));
        Assert.Equal(2, run.ModeState(66));
    }

    [SkippableFact]
    public async Task Version_writes_its_text_and_no_keypad_bytes()
    {
        await using var run = await CommandOnPty.StartAsync(LibPath, log, "\"$@\"", ["--version"]);
        await run.WaitForExitAsync();

        Assert.Equal(0, run.ExitStatus);
        Assert.Matches(@"\d+\.\d+\.\d+", run.Text());
        Assert.DoesNotContain(KeypadXmit, run.Text(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Version_into_a_pipe_writes_no_escape_bytes_and_leaves_normal_modes()
    {
        await using var run = await CommandOnPty.StartAsync(LibPath, log, "\"$@\" | cat > \"$HYPA_TEST_PIPE_OUT\"", ["--version"]);
        await run.WaitForExitAsync();

        var piped = await File.ReadAllBytesAsync(run.PipeOutputPath);
        Assert.NotEmpty(piped);
        Assert.DoesNotContain((byte)0x1b, piped);
        Assert.Matches(@"^\d+\.\d+\.\d+", Encoding.UTF8.GetString(piped));
        Assert.Equal(2, run.ModeState(1));
        Assert.Equal(2, run.ModeState(66));
    }

    [SkippableFact]
    public async Task Mux_started_with_an_ampersand_leaves_normal_modes_after_it_stops()
    {
        // The shell starts the mux in the background, waits for its socket,
        // then stops it with SIGTERM, as a user does with `kill %1`.
        const string script =
            "\"$@\" mux serve --session \"$HYPA_SESSION\" --cwd \"$HYPA_RUNTIME_STATE_DIR\" --state-dir \"$HYPA_RUNTIME_STATE_DIR\" & mux=$!; "
            + "i=0; while [ ! -S \"$HYPA_RUNTIME_SOCKET\" ] && [ $i -lt 600 ]; do sleep 0.1; i=$((i+1)); done; "
            + "[ -S \"$HYPA_RUNTIME_SOCKET\" ] && printf 'HYPA-MUX-READY\\n'; "
            + "sleep 0.5; kill -TERM $mux; wait $mux";
        await using var run = await CommandOnPty.StartAsync(LibPath, log, script, []);
        await run.WaitForExitAsync();

        Assert.Contains("HYPA-MUX-READY", run.Text(), StringComparison.Ordinal);
        Assert.Contains("hypa mux session=", run.Text(), StringComparison.Ordinal);
        Assert.DoesNotContain(KeypadXmit, run.Text(), StringComparison.Ordinal);
        Assert.Equal(2, run.ModeState(1));
        Assert.Equal(2, run.ModeState(66));
    }

    /// <summary>
    /// One shell on a PTY of hypa-pty-host that runs <c>hypa</c> through a
    /// script, with an isolated home, session, socket and state directory.
    /// Latin-1 text keeps one char for each byte of the PTY output.
    /// </summary>
    private sealed partial class CommandOnPty : IAsyncDisposable
    {
        private const int Cols = 100;
        private const int Rows = 30;
        private const string ExitMarker = "HYPA-SHORT-EXIT";

        private readonly ITestOutputHelper _log;
        private readonly string _dir;
        private readonly object _gate = new();
        private readonly List<byte> _output = [];
        private readonly CancellationTokenSource _stop = new();
        private IPtyProcess? _pty;
        private GhosttyVtEngine? _host;
        private Task _pump = Task.CompletedTask;

        private CommandOnPty(ITestOutputHelper log)
        {
            _log = log;
            _dir = Path.Combine("/tmp", "sck-" + Guid.NewGuid().ToString("N")[..8]);
            PipeOutputPath = Path.Combine(_dir, "pipe.out");
        }

        public string PipeOutputPath { get; }

        public int ExitStatus { get; private set; } = -1;

        public static async Task<CommandOnPty> StartAsync(
            string? libPath,
            ITestOutputHelper log,
            string script,
            IReadOnlyList<string> command)
        {
            Skip.If(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(), "Linux and macOS are the mux hosts.");
            GhosttyTestRequire.RequireNativeLibrary(libPath);
            var helper = GhosttyTestRequire.RequirePtyHostHelper(PtyHostSupervisor.ResolveHelperPath());
            var run = new CommandOnPty(log);
            try
            {
                await run.StartCoreAsync(libPath!, helper, script, command);
                return run;
            }
            catch
            {
                await run.DisposeAsync();
                throw;
            }
        }

        private async Task StartCoreAsync(string libPath, string helper, string script, IReadOnlyList<string> command)
        {
            var (hypa, prefix) = FindHypa();
            var home = Path.Combine(_dir, "home");
            var config = Path.Combine(home, ".config", "hypa", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            await File.WriteAllTextAsync(config, "onboarding = false\n[ui]\nstartup_splash = false\n");
            var env = new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["HYPA_CUBE_HOME"] = home,
                ["HYPA_CONFIG_PATH"] = config,
                ["HYPA_SESSION"] = Path.GetFileName(_dir),
                ["HYPA_RUNTIME_SOCKET"] = Path.Combine(_dir, "hypa.sock"),
                ["HYPA_RUNTIME_STATE_DIR"] = _dir,
                ["HYPA_MUX_LOG"] = Path.Combine(_dir, "mux.log"),
                ["HYPA_GHOSTTY_VT"] = libPath,
                ["HYPA_VT_PROVIDER"] = "ghostty",
                ["HYPA_PTY_PROVIDER"] = "process-io",
                ["HYPA_TEST_PIPE_OUT"] = PipeOutputPath,
                ["SHELL"] = "/bin/sh",
                ["TERM"] = "xterm-256color",
                ["DOTNET_ROOT"] = DotnetRoot(),
            };

            _host = new GhosttyVtEngine(Cols, Rows, libraryPathOverride: libPath);

            // The shell runs the script with "$@" set to the hypa command line,
            // prints the exit status, and keeps the PTY open.
            var shell = "unset HYPA_ENV; " + script + "; printf '\\n" + ExitMarker + " %s\\n' \"$?\"; exec sleep 600";
            var args = new List<string> { "-c", shell, "sck", hypa };
            args.AddRange(prefix);
            args.AddRange(command);
            var factory = new PtyProcessFactory(new PtyProviderOptions
            {
                Provider = PtyProviderKind.HypaPtyHost,
                HelperPath = helper,
            });
            _pty = factory.Spawn("/bin/sh", args, _dir, Cols, Rows, env);
            _pump = Task.Run(PumpAsync);
        }

        public async Task WaitForExitAsync()
        {
            await Poll.UntilAsync(
                () => _pump.IsCompleted || ExitLine().IsMatch(Text()),
                () => "the command to finish. output tail: " + Tail());
            var match = ExitLine().Match(Text());
            if (!match.Success)
                Assert.Fail("the PTY closed before the command finished. output tail: " + Tail());
            ExitStatus = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            _log.WriteLine("output: " + Tail());
        }

        public string Text()
        {
            lock (_gate)
                return Encoding.Latin1.GetString(_output.ToArray());
        }

        /// <summary>DECRQM state of a DEC private mode: 1 set, 2 reset.</summary>
        public int ModeState(int mode)
        {
            var replies = new List<byte[]>();
            lock (_gate)
                _host!.FeedChildOutput(Encoding.ASCII.GetBytes($"\u001b[?{mode}$p"), replies);
            var reply = string.Concat(replies.Select(r => Encoding.ASCII.GetString(r)));
            var match = Regex.Match(reply, @"\[\?" + mode + @";(\d)\$y");
            Assert.True(match.Success, $"DECRQM {mode} reply: {reply.Replace("\u001b", "\\e", StringComparison.Ordinal)}");
            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private string Tail()
        {
            var text = Text();
            var tail = text.Length > 1500 ? text[^1500..] : text;
            return tail.Replace("\u001b", "\\e", StringComparison.Ordinal);
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

                    // The host answers queries, as a terminal does.
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

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            if (_pty is not null)
            {
                try { await _pty.DisposeAsync(); }
                catch (Exception ex) { _log.WriteLine("pty dispose: " + ex.Message); }
            }

            try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }

            _host?.Dispose();
            _stop.Dispose();
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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

        [GeneratedRegex(ExitMarker + @" (\d+)")]
        private static partial Regex ExitLine();
    }
}
