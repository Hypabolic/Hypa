using System.Diagnostics;
using System.Text;
using Xunit;

namespace Hypa.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class UnifiedCliAttachTests
{
    [Fact]
    public async Task Compression_and_doctor_still_succeed()
    {
        var hypa = FindHypaLaunch();
        var echo = await RunAsync(hypa, ["-c", "echo hello"], timeout: TimeSpan.FromSeconds(30));
        Assert.True(echo.ExitCode == 0, echo.Describe("hypa -c echo hello"));
        Assert.Contains("hello", echo.Stdout, StringComparison.Ordinal);

        var doctor = await RunAsync(hypa, ["doctor"], timeout: TimeSpan.FromSeconds(45));
        Assert.True(doctor.ExitCode == 0, doctor.Describe("hypa doctor"));

        var config = await RunAsync(hypa, ["config", "show"], timeout: TimeSpan.FromSeconds(30));
        Assert.True(config.ExitCode == 0, config.Describe("hypa config show"));
    }

    [Fact]
    public async Task DashC_ping_is_compression_not_mux()
    {
        var hypa = FindHypaLaunch();
        var extraEnv = new Dictionary<string, string?>
        {
            ["HYPA_RUNTIME_SOCKET"] = Path.Combine(Path.GetTempPath(), "hypa-no-such-" + Guid.NewGuid().ToString("N") + ".sock"),
        };

        ProcResult result;
        try
        {
            result = await RunAsync(
                hypa,
                ["--timeout-ms", "1500", "-c", "ping"],
                timeout: TimeSpan.FromSeconds(8),
                extraEnv);
        }
        catch (TimeoutException)
        {
            // Compression started ping (it can run until timeout). That is not mux intercept.
            return;
        }

        Assert.DoesNotContain("Unknown command", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown command", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to connect", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Is the mux server running", result.Stderr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task EmptyArgv_redirected_starts_server_and_prints_snapshot()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux attach gate; not proven on Windows.");

        var hypa = FindHypaLaunch();
        var session = $"h33e-{Guid.NewGuid():N}"[..12];
        var runtimeDir = Path.Combine("/tmp", session);
        Directory.CreateDirectory(runtimeDir);
        var socket = Path.Combine(runtimeDir, "hypa.sock");

        var extraEnv = IsolatedMuxEnv(session, socket, runtimeDir);

        try
        {
            var attach = await RunAsync(
                hypa,
                [],
                timeout: TimeSpan.FromSeconds(20),
                extraEnv);
            Assert.True(attach.ExitCode == 0, attach.Describe("hypa (empty argv)"));
            Assert.Contains("session=" + session, attach.Stdout, StringComparison.Ordinal);
            Assert.Contains("session_id", attach.Stdout, StringComparison.Ordinal);
            Assert.Contains("protocol_version", attach.Stdout, StringComparison.Ordinal);
            Assert.Contains("panes", attach.Stdout, StringComparison.Ordinal);
            // The ping line is part of the redirected output. Install smoke reads it.
            Assert.Contains("\"ok\":true", attach.Stdout.Replace(" ", ""), StringComparison.Ordinal);

            var stop = await RunAsync(
                hypa,
                StopArgs(session, socket),
                timeout: TimeSpan.FromSeconds(15),
                extraEnv);
            Assert.True(stop.ExitCode == 0, stop.Describe("hypa mux stop"));
        }
        finally
        {
            await StopAndCleanupAsync(hypa, session, socket, runtimeDir, extraEnv);
        }
    }

    [SkippableFact]
    public async Task AttachOnce_starts_server_and_prints_ping()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux attach gate; not proven on Windows.");

        var hypa = FindHypaLaunch();
        var session = $"h16-{Guid.NewGuid():N}"[..12];
        var runtimeDir = Path.Combine("/tmp", session);
        Directory.CreateDirectory(runtimeDir);
        var socket = Path.Combine(runtimeDir, "hypa.sock");

        var extraEnv = new Dictionary<string, string?>
        {
            ["HYPA_SESSION"] = session,
            ["HYPA_RUNTIME_SOCKET"] = socket,
            ["HYPA_RUNTIME_STATE_DIR"] = runtimeDir,
        };

        try
        {
            var attach = await RunAsync(
                hypa,
                ["attach", "--once", "--session", session],
                timeout: TimeSpan.FromSeconds(20),
                extraEnv);
            Assert.True(attach.ExitCode == 0, attach.Describe("hypa attach --once"));
            Assert.Contains("session=" + session, attach.Stdout, StringComparison.Ordinal);
            Assert.Contains("session_id", attach.Stdout, StringComparison.Ordinal);
            Assert.Contains("protocol_version", attach.Stdout, StringComparison.Ordinal);
            Assert.Contains("panes", attach.Stdout, StringComparison.Ordinal);

            var stop = await RunAsync(
                hypa,
                StopArgs(session, socket),
                timeout: TimeSpan.FromSeconds(15),
                extraEnv);
            Assert.True(stop.ExitCode == 0, stop.Describe("hypa mux stop"));
        }
        finally
        {
            await StopAndCleanupAsync(hypa, session, socket, runtimeDir, extraEnv);
        }
    }

    [SkippableFact]
    public async Task Attach_prefix_q_detaches_and_leaves_server_running()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux attach prefix+q detach gate; not proven on Windows.");

        var hypa = FindHypaLaunch();
        var session = $"h33q-{Guid.NewGuid():N}"[..12];
        var runtimeDir = Path.Combine("/tmp", session);
        Directory.CreateDirectory(runtimeDir);
        var socket = Path.Combine(runtimeDir, "hypa.sock");
        var extraEnv = IsolatedMuxEnv(session, socket, runtimeDir);

        try
        {
            // One TTY attach starts the mux. prefix+q (ctrl+b then q) detaches.
            // The server stays up. Typed Ctrl+C is a pane key in raw mode.
            using var attach = StartTtyAttach(hypa, ["attach", "--session", session], extraEnv);
            var muxPid = await WaitForStatusPidAsync(runtimeDir, TimeSpan.FromSeconds(20));
            Assert.True(muxPid > 0, "mux status pid missing after attach");

            var sawPaint = await WaitForOutputContainsAsync(
                attach,
                TimeSpan.FromSeconds(15),
                "\u001b[H",
                "\u001b[1;1H");
            Assert.True(
                sawPaint,
                "first TTY attach must paint chrome or a snapshot. " +
                (attach.HasExited ? "exited=" + attach.ExitCode : "still-running"));

            var treePids = CollectDescendantPids(attach.Id);
            treePids.Add(attach.Id);
            var muxPgid = ReadPsInt(muxPid, "pgid");
            var muxSid = ReadPsInt(muxPid, OperatingSystem.IsMacOS() ? "sess" : "sid");
            var selfPgid = ReadPsInt(Environment.ProcessId, "pgid");
            Assert.True(muxPgid is > 0, "could not read mux pgid");
            Assert.True(selfPgid is > 0, "could not read testhost pgid");
            Assert.NotEqual(muxPgid.Value, selfPgid.Value);

            var ttyClientPgid = treePids
                .Where(pid => pid != muxPid)
                .Select(pid => ReadPsInt(pid, "pgid"))
                .FirstOrDefault(pgid => pgid is > 0 && pgid != selfPgid && pgid != muxPgid);
            Assert.True(ttyClientPgid is > 0, "TTY attach client stayed in the testhost process group");
            Assert.NotEqual(muxPgid.Value, ttyClientPgid.Value);

            if (muxSid is > 0)
            {
                var clientSid = treePids
                    .Where(pid => pid != muxPid)
                    .Select(pid => ReadPsInt(pid, OperatingSystem.IsMacOS() ? "sess" : "sid"))
                    .FirstOrDefault(sid => sid is > 0 && sid != muxSid);
                if (clientSid is > 0)
                    Assert.NotEqual(muxSid.Value, clientSid.Value);
            }

            await DetachWithPrefixQAsync(attach, "attach");

            Assert.True(IsPidAlive(muxPid), "prefix+q must not stop an isolated mux");

            var ping = await RunAsync(
                hypa,
                ["ping", "--session", session],
                timeout: TimeSpan.FromSeconds(10),
                extraEnv);
            Assert.True(ping.ExitCode == 0, ping.Describe("hypa ping after prefix+q detach"));
            Assert.Contains("\"ok\":true", ping.Stdout.Replace(" ", ""), StringComparison.Ordinal);

            using var reattach = StartTtyAttach(hypa, ["attach", "--session", session], extraEnv);
            var sawReattachPaint = await WaitForOutputContainsAsync(
                reattach,
                TimeSpan.FromSeconds(15),
                "\u001b[H",
                "\u001b[1;1H");
            Assert.True(
                sawReattachPaint,
                "reattach must paint chrome or a snapshot. " +
                (reattach.HasExited ? "exited=" + reattach.ExitCode : "still-running"));
            await DetachWithPrefixQAsync(reattach, "reattach");

            var onceAgain = await RunAsync(
                hypa,
                ["attach", "--once", "--session", session],
                timeout: TimeSpan.FromSeconds(15),
                extraEnv);
            Assert.True(onceAgain.ExitCode == 0, onceAgain.Describe("hypa attach --once after prefix+q"));

            var stop = await RunAsync(
                hypa,
                StopArgs(session, socket),
                timeout: TimeSpan.FromSeconds(15),
                extraEnv);
            Assert.True(stop.ExitCode == 0, stop.Describe("hypa mux stop after detach"));

            var pingAfter = await RunAsync(
                hypa,
                ["ping", "--session", session],
                timeout: TimeSpan.FromSeconds(10),
                extraEnv);
            Assert.NotEqual(0, pingAfter.ExitCode);
        }
        finally
        {
            await StopAndCleanupAsync(hypa, session, socket, runtimeDir, extraEnv);
        }
    }

    [SkippableFact]
    public async Task Root_session_option_and_session_cli_and_api_snapshot()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux attach gate; not proven on Windows.");

        var hypa = FindHypaLaunch();
        var session = $"h33s-{Guid.NewGuid():N}"[..12];
        var runtimeDir = Path.Combine("/tmp", session);
        Directory.CreateDirectory(runtimeDir);
        var socket = Path.Combine(runtimeDir, "hypa.sock");
        var extraEnv = IsolatedMuxEnv(session, socket, runtimeDir);

        try
        {
            var once = await RunAsync(
                hypa,
                ["--session", session, "attach", "--once"],
                timeout: TimeSpan.FromSeconds(20),
                extraEnv);
            Assert.True(once.ExitCode == 0, once.Describe("hypa --session NAME attach --once"));

            var api = await RunAsync(
                hypa,
                ["api", "snapshot", "--session", session],
                timeout: TimeSpan.FromSeconds(10),
                extraEnv);
            Assert.True(api.ExitCode == 0, api.Describe("hypa api snapshot"));
            Assert.Contains("session_id", api.Stdout, StringComparison.Ordinal);
            Assert.Contains("panes", api.Stdout, StringComparison.Ordinal);

            var status = await RunAsync(
                hypa,
                ["status"],
                timeout: TimeSpan.FromSeconds(10),
                extraEnv);
            Assert.True(status.ExitCode == 0, status.Describe("hypa status"));
            Assert.Contains("server.alive=true", status.Stdout, StringComparison.Ordinal);
            Assert.Contains("client.session=" + session, status.Stdout, StringComparison.Ordinal);

            var list = await RunAsync(
                hypa,
                ["session", "list"],
                timeout: TimeSpan.FromSeconds(10),
                extraEnv);
            Assert.True(list.ExitCode == 0, list.Describe("hypa session list"));
            Assert.Contains(session, list.Stdout, StringComparison.Ordinal);

            var stop = await RunAsync(
                hypa,
                ["session", "stop"],
                timeout: TimeSpan.FromSeconds(15),
                extraEnv);
            Assert.True(stop.ExitCode == 0, stop.Describe("hypa session stop"));

            var pingAfter = await RunAsync(
                hypa,
                ["ping", "--session", session],
                timeout: TimeSpan.FromSeconds(10),
                extraEnv);
            Assert.NotEqual(0, pingAfter.ExitCode);
        }
        finally
        {
            await StopAndCleanupAsync(hypa, session, socket, runtimeDir, extraEnv);
        }
    }

    [SkippableFact]
    public async Task SessionStop_does_not_swap_env_socket_for_other_session()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux stop socket targeting gate; not proven on Windows.");

        var hypa = FindHypaLaunch();
        var session = $"h33x-{Guid.NewGuid():N}"[..12];
        var other = $"h33y-{Guid.NewGuid():N}"[..12];
        var runtimeDir = Path.Combine("/tmp", session);
        Directory.CreateDirectory(runtimeDir);
        var socket = Path.Combine(runtimeDir, "hypa.sock");
        var extraEnv = IsolatedMuxEnv(session, socket, runtimeDir);

        try
        {
            var once = await RunAsync(
                hypa,
                ["attach", "--once", "--session", session],
                timeout: TimeSpan.FromSeconds(20),
                extraEnv);
            Assert.True(once.ExitCode == 0, once.Describe("hypa attach --once (session-stop-other)"));

            var stopOther = await RunAsync(
                hypa,
                ["session", "stop", other],
                timeout: TimeSpan.FromSeconds(15),
                extraEnv);
            Assert.NotEqual(0, stopOther.ExitCode);
            Assert.Contains("HYPA_RUNTIME_SOCKET", stopOther.Stderr, StringComparison.Ordinal);

            var ping = await RunAsync(
                hypa,
                ["ping", "--session", session],
                timeout: TimeSpan.FromSeconds(10),
                extraEnv);
            Assert.True(ping.ExitCode == 0, ping.Describe("live mux must survive session stop other"));
        }
        finally
        {
            await StopAndCleanupAsync(hypa, session, socket, runtimeDir, extraEnv);
        }
    }

    [SkippableFact]
    public async Task MuxStop_does_not_swap_env_socket_for_other_session()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux stop socket targeting gate; not proven on Windows.");

        var hypa = FindHypaLaunch();
        var session = $"h16s-{Guid.NewGuid():N}"[..12];
        var other = $"h16o-{Guid.NewGuid():N}"[..12];
        var runtimeDir = Path.Combine("/tmp", session);
        Directory.CreateDirectory(runtimeDir);
        var socket = Path.Combine(runtimeDir, "hypa.sock");
        var extraEnv = IsolatedMuxEnv(session, socket, runtimeDir);

        try
        {
            var once = await RunAsync(
                hypa,
                ["attach", "--once", "--session", session],
                timeout: TimeSpan.FromSeconds(20),
                extraEnv);
            Assert.True(once.ExitCode == 0, once.Describe("hypa attach --once (env-swap)"));

            var stopOther = await RunAsync(
                hypa,
                ["mux", "stop", "--session", other],
                timeout: TimeSpan.FromSeconds(15),
                extraEnv);
            Assert.NotEqual(0, stopOther.ExitCode);

            var ping = await RunAsync(
                hypa,
                ["ping", "--session", session],
                timeout: TimeSpan.FromSeconds(10),
                extraEnv);
            Assert.True(ping.ExitCode == 0, ping.Describe("live mux must survive stop --session other"));
        }
        finally
        {
            await StopAndCleanupAsync(hypa, session, socket, runtimeDir, extraEnv);
        }
    }

    [SkippableFact]
    public async Task MuxStop_env_only_hits_env_socket_not_default_session()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix mux stop socket targeting gate; not proven on Windows.");

        var hypa = FindHypaLaunch();
        var session = $"h16v-{Guid.NewGuid():N}"[..12];
        var runtimeDir = Path.Combine("/tmp", session);
        Directory.CreateDirectory(runtimeDir);
        var socket = Path.Combine(runtimeDir, "hypa.sock");
        var extraEnv = IsolatedMuxEnv(session, socket, runtimeDir);
        extraEnv["HYPA_SESSION"] = null;

        var defaultSocket = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "hypa", "runtime", "default", "hypa.sock");
        var defaultStatus = Path.Combine(
            Path.GetDirectoryName(defaultSocket) ?? ".",
            "runtime.status.json");
        var defaultStatusBefore = File.Exists(defaultStatus)
            ? await File.ReadAllTextAsync(defaultStatus)
            : null;

        try
        {
            var once = await RunAsync(
                hypa,
                ["attach", "--once", "--session", session],
                timeout: TimeSpan.FromSeconds(20),
                extraEnv);
            Assert.True(once.ExitCode == 0, once.Describe("hypa attach --once (env-stop)"));

            var stop = await RunAsync(
                hypa,
                ["mux", "stop"],
                timeout: TimeSpan.FromSeconds(15),
                extraEnv);
            Assert.True(stop.ExitCode == 0, stop.Describe("hypa mux stop (env socket only)"));

            var ping = await RunAsync(
                hypa,
                ["ping", "--session", session],
                timeout: TimeSpan.FromSeconds(10),
                extraEnv);
            Assert.NotEqual(0, ping.ExitCode);

            if (defaultStatusBefore is null)
                Assert.False(File.Exists(defaultStatus));
            else
                Assert.Equal(defaultStatusBefore, await File.ReadAllTextAsync(defaultStatus));
        }
        finally
        {
            extraEnv["HYPA_SESSION"] = session;
            await StopAndCleanupAsync(hypa, session, socket, runtimeDir, extraEnv);
        }
    }

    /// <summary>
    /// Send prefix+q until the TTY attach exits. The cube splash paints before
    /// the input loop starts, so keys sent at the first paint can arrive too
    /// early. Send the keys again every 2 s, for up to 12 s.
    /// </summary>
    private static async Task DetachWithPrefixQAsync(Process client, string label)
    {
        // Nothing reads the client output after the paint check. A client
        // that repaints more than one pipe buffer would block on write and
        // never read the keys, so drain both streams until it exits.
        _ = client.StandardOutput.ReadToEndAsync();
        _ = client.StandardError.ReadToEndAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (!client.HasExited && DateTime.UtcNow < deadline)
        {
            try
            {
                // script(1) keeps the PTY master in canonical mode. A newline
                // flushes ctrl+b q to the raw slave. Extra LF is unused after detach.
                client.StandardInput.Write("\u0002q\n");
                client.StandardInput.Flush();
            }
            catch (IOException) when (client.HasExited)
            {
                break;
            }
            catch (Exception ex)
            {
                Assert.Fail("could not write prefix+q to " + label + " stdin: " + ex.Message);
            }

            using var step = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await client.WaitForExitAsync(step.Token);
            }
            catch (OperationCanceledException)
            {
                // Not detached yet. Send the keys again.
            }
        }

        if (!client.HasExited)
        {
            try { client.Kill(entireProcessTree: true); } catch { /* ignore */ }
            Assert.Fail(label + " client did not exit after prefix+q");
        }
    }

    private static Dictionary<string, string?> IsolatedMuxEnv(string session, string socket, string runtimeDir) =>
        new()
        {
            ["HYPA_SESSION"] = session,
            ["HYPA_RUNTIME_SOCKET"] = socket,
            ["HYPA_RUNTIME_STATE_DIR"] = runtimeDir,
        };

    private static string[] StopArgs(string session, string socket) =>
        ["mux", "stop", "--session", session, "--socket", socket];

    private static async Task StopAndCleanupAsync(
        HypaLaunch hypa,
        string session,
        string socket,
        string runtimeDir,
        IReadOnlyDictionary<string, string?> extraEnv)
    {
        try
        {
            await RunAsync(
                hypa,
                StopArgs(session, socket),
                timeout: TimeSpan.FromSeconds(10),
                extraEnv);
        }
        catch
        {
            // best-effort
        }

        try
        {
            Directory.Delete(runtimeDir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    private static Process StartTtyAttach(
        HypaLaunch launch,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> extraEnv)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        if (OperatingSystem.IsMacOS())
        {
            psi.FileName = "script";
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("/dev/null");
            psi.ArgumentList.Add(launch.FileName);
            foreach (var prefix in launch.Prefix)
                psi.ArgumentList.Add(prefix);
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
        }
        else
        {
            var quoted = new List<string> { Quote(launch.FileName) };
            foreach (var prefix in launch.Prefix)
                quoted.Add(Quote(prefix));
            foreach (var arg in args)
                quoted.Add(Quote(arg));

            psi.FileName = "script";
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(string.Join(' ', quoted));
            psi.ArgumentList.Add("/dev/null");
        }

        foreach (var (key, value) in extraEnv)
        {
            if (value is null)
                psi.Environment.Remove(key);
            else
                psi.Environment[key] = value;
        }

        return Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start TTY attach via script.");
    }

    private static Task<bool> WaitForOutputContainsAsync(
        Process process,
        string needle,
        TimeSpan timeout) =>
        WaitForOutputContainsAsync(process, timeout, needle);

    private static async Task<bool> WaitForOutputContainsAsync(
        Process process,
        TimeSpan timeout,
        params string[] needles)
    {
        var deadline = DateTime.UtcNow + timeout;
        var buffer = new StringBuilder();
        var chunk = new char[256];
        while (DateTime.UtcNow < deadline && !process.HasExited)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero)
                break;

            var readTask = process.StandardOutput.ReadAsync(chunk, 0, chunk.Length);
            var completed = await Task.WhenAny(readTask, Task.Delay(remaining)).ConfigureAwait(false);
            if (completed != readTask)
                break;

            var n = await readTask.ConfigureAwait(false);
            if (n <= 0)
                break;
            buffer.Append(chunk, 0, n);
            if (ContainsAny(buffer, needles))
                return true;
        }

        return ContainsAny(buffer, needles);
    }

    private static bool ContainsAny(StringBuilder buffer, string[] needles)
    {
        var text = buffer.ToString();
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static HashSet<int> CollectDescendantPids(int rootPid)
    {
        var found = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in ReadChildPids(parent))
            {
                if (found.Add(child))
                    queue.Enqueue(child);
            }
        }

        return found;
    }

    private static List<int> ReadChildPids(int parentPid)
    {
        var children = new List<int>();
        try
        {
            using var pgrep = Process.Start(new ProcessStartInfo
            {
                FileName = "pgrep",
                ArgumentList = { "-P", parentPid.ToString() },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (pgrep is null)
                return children;
            var text = pgrep.StandardOutput.ReadToEnd();
            pgrep.WaitForExit(2000);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(line.Trim(), out var pid) && pid > 0)
                    children.Add(pid);
            }
        }
        catch
        {
            return children;
        }

        return children;
    }

    private static async Task<int> WaitForStatusPidAsync(string runtimeDir, TimeSpan timeout)
    {
        var statusPath = Path.Combine(runtimeDir, "runtime.status.json");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var pid = TryReadStatusPid(statusPath);
            if (pid > 0)
                return pid;
            await Task.Delay(50).ConfigureAwait(false);
        }

        return TryReadStatusPid(statusPath);
    }

    private static int TryReadStatusPid(string statusPath)
    {
        try
        {
            if (!File.Exists(statusPath))
                return 0;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statusPath));
            return doc.RootElement.TryGetProperty("pid", out var pidEl) && pidEl.TryGetInt32(out var pid)
                ? pid
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int? ReadPsInt(int pid, string format)
    {
        try
        {
            using var ps = Process.Start(new ProcessStartInfo
            {
                FileName = "ps",
                ArgumentList = { "-p", pid.ToString(), "-o", format + "=" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (ps is null)
                return null;
            var text = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(2000);
            return int.TryParse(text.Trim(), out var value) && value > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static HypaLaunch FindHypaLaunch()
    {
        var (fileName, prefix) = IntegrationTestHelpers.FindShipHypa();
        return new HypaLaunch(fileName, prefix);
    }

    private static async Task<ProcResult> RunAsync(
        HypaLaunch launch,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? extraEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = launch.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var prefix in launch.Prefix)
            psi.ArgumentList.Add(prefix);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
            {
                if (value is null)
                    psi.Environment.Remove(key);
                else
                    psi.Environment[key] = value;
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start " + launch.FileName);
        process.StandardInput.Close();

        using var cts = new CancellationTokenSource(timeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new TimeoutException(
                $"Timed out running {launch.FileName} {string.Join(' ', args)}");
        }

        return new ProcResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record HypaLaunch(string FileName, string[] Prefix);

    private sealed record ProcResult(int ExitCode, string Stdout, string Stderr)
    {
        public string Describe(string label)
        {
            var sb = new StringBuilder();
            sb.Append(label).Append(" exit=").Append(ExitCode).AppendLine();
            sb.Append("stdout: ").AppendLine(Stdout);
            sb.Append("stderr: ").AppendLine(Stderr);
            return sb.ToString();
        }
    }
}
