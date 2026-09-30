using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Hidden-pane recipes against a test mux.
/// The calls match the skill recipes. The tests do not read the skill file.
/// </summary>
public class BackgroundPaneRecipeTests
{
    /// <summary>
    /// Command text the recipe passes to <c>pane run</c>.
    /// <c>pane run</c> appends Enter (CR). The text has no LF.
    /// </summary>
    private const string ReadyCommand = "printf 'READY'";

    private const string OtherCommand = "printf 'OTHER'";

    private const string PromptText = "Review the diff. Report actions only.";

    private static readonly IReadOnlyDictionary<string, string?> IsolatedEnv =
        new Dictionary<string, string?>
        {
            [PaneIdEnvironment.HypaPaneId] = null,
            [PaneIdEnvironment.HypaPaneToken] = null,
        };

    [SkippableFact]
    public async Task Hidden_terminal_reads_output_and_shows_only_after_a_failed_wait()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        await using var mux = await TestMux.StartAsync();

        var success = await CreateHiddenTerminalAsync(mux);
        await AssertNoTiledLeafAsync(mux.Socket, success.PaneId);
        var ran = await CliAsync(mux.Socket, "pane", "run", success.PaneId, ReadyCommand);
        Assert.True(ran.GetProperty("ok").GetBoolean(), ran.GetRawText());
        Assert.Equal(ReadyCommand.Length + 1, ran.GetProperty("accepted_bytes").GetInt32());

        var waited = await CliAsync(
            mux.Socket,
            "--timeout-ms", "12000",
            "pane", "wait-output", success.PaneId,
            "--match", "READY",
            "--timeout", "8000");
        Assert.True(waited.GetProperty("matched").GetBoolean(), waited.GetRawText());
        Assert.Contains("READY", waited.GetProperty("text").GetString(), StringComparison.Ordinal);
        await AssertNoTiledLeafAsync(mux.Socket, success.PaneId);

        var read = await CliAsync(
            mux.Socket,
            "pane", "read", success.PaneId,
            "--source", "recent-unwrapped",
            "--lines", "120");
        Assert.Contains("READY", read.GetProperty("text").GetString(), StringComparison.Ordinal);

        var closed = await CliAsync(mux.Socket, "pane", "close", success.PaneId);
        Assert.True(closed.GetProperty("ok").GetBoolean(), closed.GetRawText());
        await AssertPaneAbsentAsync(mux.Socket, success.PaneId);

        var failed = await CreateHiddenTerminalAsync(mux);
        await AssertNoTiledLeafAsync(mux.Socket, failed.PaneId);
        var other = await CliAsync(mux.Socket, "pane", "run", failed.PaneId, OtherCommand);
        Assert.Equal(OtherCommand.Length + 1, other.GetProperty("accepted_bytes").GetInt32());
        var missed = await RunAsync(
            mux.Socket,
            "--timeout-ms", "5000",
            "pane", "wait-output", failed.PaneId,
            "--match", "READY",
            "--timeout", "400");
        Assert.Equal(1, missed.Code);
        Assert.Contains(PaneWaitForOutputErrors.TimeoutMessage, missed.Stderr, StringComparison.Ordinal);
        await AssertNoTiledLeafAsync(mux.Socket, failed.PaneId);

        await AssertShowHideSequenceAsync(mux.Socket, failed.PaneId, failed.ParentCap);

        var after = await CliAsync(mux.Socket, "pane", "close", failed.PaneId);
        Assert.True(after.GetProperty("ok").GetBoolean(), after.GetRawText());
        Assert.Equal(failed.PaneId, after.GetProperty("pane_id").GetString());
        await AssertPaneAbsentAsync(mux.Socket, failed.PaneId);
    }

    [SkippableFact]
    public async Task Hidden_helper_agent_shows_when_blocked_then_hides_and_closes()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-agent.sh");
        Assert.True(File.Exists(script), script);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await using var mux = await TestMux.StartAsync();
        var child = await CreateHiddenShellAsync(mux.Socket);
        await AssertNoTiledLeafAsync(mux.Socket, child.PaneId);

        var cli = HypaCliProcess.CliPath();
        var started = await CliAsync(
            mux.Socket,
            "agent", "start", child.PaneId,
            "--command", script,
            "--args", mux.Socket,
            "--args", cli);
        Assert.Equal(child.PaneId, started.GetProperty("pane_id").GetString());
        Assert.True(started.GetProperty("alive").GetBoolean());

        var prompted = await CliAsync(
            mux.Socket,
            "--timeout-ms", "130000",
            "agent", "prompt", child.PaneId,
            PromptText,
            "--wait",
            "--until", "blocked",
            "--timeout", "120000");
        Assert.Equal("blocked", prompted.GetProperty("state").GetString());
        Assert.False(prompted.GetProperty("wait").GetProperty("timed_out").GetBoolean());
        await AssertNoTiledLeafAsync(mux.Socket, child.PaneId);

        var status = await CliAsync(mux.Socket, "agent", "status", child.PaneId);
        Assert.Equal("blocked", status.GetProperty("state").GetString());
        await AssertNoTiledLeafAsync(mux.Socket, child.PaneId);

        await AssertShowHideSequenceAsync(mux.Socket, child.PaneId, child.ParentCap);

        var closed = await CliAsync(mux.Socket, "pane", "close", child.PaneId);
        Assert.True(closed.GetProperty("ok").GetBoolean(), closed.GetRawText());
        Assert.Equal(child.PaneId, closed.GetProperty("pane_id").GetString());
        await AssertPaneAbsentAsync(mux.Socket, child.PaneId);
    }

    /// <summary>
    /// A failed show keeps <c>seq</c> 1. The same <c>seq</c> then shows the pane.
    /// A later call with that <c>seq</c> is stale. Hide uses the next <c>seq</c>.
    /// A repeat of that hide <c>seq</c> is stale and the PTY stays alive.
    /// </summary>
    private static async Task AssertShowHideSequenceAsync(string sock, string paneId, string parentCap)
    {
        var before = await TabListTextAsync(sock);
        await AssertNoTiledLeafAsync(sock, paneId);

        var failed = await RunAsync(
            sock,
            "pane", "show",
            "--pane-id", paneId,
            "--parent-capability", parentCap,
            "--mode", "tiled",
            "--seq", "1",
            "--ratio", "2");
        Assert.Equal(1, failed.Code);
        Assert.Contains("ratio", failed.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, await TabListTextAsync(sock));
        await AssertNoTiledLeafAsync(sock, paneId);
        var stillHidden = await CliAsync(sock, "pane", "get", paneId);
        Assert.Equal("hidden", stillHidden.GetProperty("placement").GetString());

        var shown = await CliAsync(
            sock,
            "pane", "show",
            "--pane-id", paneId,
            "--parent-capability", parentCap,
            "--mode", "tiled",
            "--seq", "1");
        Assert.True(shown.GetProperty("changed").GetBoolean(), shown.GetRawText());
        Assert.Equal("tiled", shown.GetProperty("placement").GetString());
        var shownTab = shown.GetProperty("tab_id").GetString();
        Assert.False(string.IsNullOrEmpty(shownTab));
        await AssertHasTiledLeafAsync(sock, paneId);

        var afterShow = await TabListTextAsync(sock);
        var staleShow = await CliAsync(
            sock,
            "pane", "show",
            "--pane-id", paneId,
            "--parent-capability", parentCap,
            "--mode", "tiled",
            "--seq", "1");
        Assert.False(staleShow.GetProperty("changed").GetBoolean(), staleShow.GetRawText());
        Assert.Equal(shownTab, staleShow.GetProperty("tab_id").GetString());
        Assert.Equal("tiled", staleShow.GetProperty("placement").GetString());
        Assert.Equal(afterShow, await TabListTextAsync(sock));
        await AssertHasTiledLeafAsync(sock, paneId);

        var hidden = await CliAsync(
            sock,
            "pane", "hide",
            "--pane-id", paneId,
            "--parent-capability", parentCap,
            "--seq", "2");
        Assert.True(hidden.GetProperty("changed").GetBoolean(), hidden.GetRawText());
        Assert.Equal("hidden", hidden.GetProperty("placement").GetString());
        await AssertNoTiledLeafAsync(sock, paneId);
        await AssertPtyLiveAsync(sock, paneId);

        var afterHide = await TabListTextAsync(sock);
        var staleHide = await CliAsync(
            sock,
            "pane", "hide",
            "--pane-id", paneId,
            "--parent-capability", parentCap,
            "--seq", "2");
        Assert.False(staleHide.GetProperty("changed").GetBoolean(), staleHide.GetRawText());
        Assert.Equal("hidden", staleHide.GetProperty("placement").GetString());
        Assert.Equal(afterHide, await TabListTextAsync(sock));
        await AssertNoTiledLeafAsync(sock, paneId);
        await AssertPtyLiveAsync(sock, paneId);
    }

    private static async Task AssertPtyLiveAsync(string sock, string paneId)
    {
        var pane = await CliAsync(sock, "pane", "get", paneId);
        Assert.True(pane.GetProperty("alive").GetBoolean(), pane.GetRawText());
        Assert.Equal("hidden", pane.GetProperty("placement").GetString());
    }

    private static async Task AssertNoTiledLeafAsync(string sock, string paneId)
    {
        var tabs = await CliAsync(sock, "tab", "list");
        Assert.DoesNotContain(tabs.EnumerateArray(), tab => LayoutContains(tab, paneId));
    }

    private static async Task AssertHasTiledLeafAsync(string sock, string paneId)
    {
        var tabs = await CliAsync(sock, "tab", "list");
        Assert.Contains(tabs.EnumerateArray(), tab => LayoutContains(tab, paneId));
    }

    private static bool LayoutContains(JsonElement tab, string paneId)
    {
        if (!tab.TryGetProperty("layout", out var layout))
            return false;
        return LayoutNodeContains(layout, paneId);
    }

    private static bool LayoutNodeContains(JsonElement node, string paneId)
    {
        if (node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return false;
        if (node.TryGetProperty("pane_id", out var id)
            && string.Equals(id.GetString(), paneId, StringComparison.Ordinal))
        {
            return true;
        }

        if (node.TryGetProperty("first", out var first) && LayoutNodeContains(first, paneId))
            return true;
        return node.TryGetProperty("second", out var second) && LayoutNodeContains(second, paneId);
    }

    private static async Task<string> TabListTextAsync(string sock)
    {
        var (code, stdout, stderr) = await RunAsync(sock, "tab", "list");
        Assert.True(code == 0, stderr + stdout);
        return stdout;
    }

    private static async Task AssertPaneAbsentAsync(string sock, string paneId)
    {
        var listed = await CliAsync(sock, "pane", "list");
        var paneIds = listed.EnumerateArray()
            .Select(pane => pane.GetProperty("pane_id").GetString())
            .ToArray();
        Assert.DoesNotContain(paneId, paneIds);
    }

    /// <summary>
    /// Bash that runs each submitted line when Enter arrives.
    /// Process-io has no PTY line discipline, so CR is not mapped to NL.
    /// A PTY shell runs the line when <c>pane run</c> sends Enter.
    /// This shell treats CR as that Enter. The command text has no LF.
    /// </summary>
    private static async Task<string> WriteLineShellAsync(string dir)
    {
        var path = Path.Combine(dir, "line-shell.sh");
        await File.WriteAllTextAsync(path, """
            line=""
            while IFS= read -r -n 1 ch; do
              if [ "$ch" = $'\r' ] || [ "$ch" = $'\n' ]; then
                if [ -n "$line" ]; then
                  eval "$line"
                fi
                line=""
              else
                line="${line}${ch}"
              fi
            done
            """);
        return path;
    }

    private static async Task<HiddenChild> CreateHiddenTerminalAsync(TestMux mux)
    {
        var shell = await WriteLineShellAsync(mux.Dir);
        var created = await CliAsync(
            mux.Socket,
            "pane", "create",
            "--placement", "hidden",
            "--command", "/bin/bash",
            "--args", shell);
        return HiddenChildFrom(created);
    }

    private static async Task<HiddenChild> CreateHiddenShellAsync(string sock)
    {
        var created = await CliAsync(
            sock,
            "pane", "create",
            "--placement", "hidden",
            "--command", "/bin/sh");
        return HiddenChildFrom(created);
    }

    private static HiddenChild HiddenChildFrom(JsonElement created)
    {
        var paneId = created.GetProperty("pane_id").GetString();
        var parentCap = created.GetProperty("parent_capability").GetString();
        Assert.False(string.IsNullOrEmpty(paneId));
        Assert.StartsWith("par_", parentCap);
        Assert.Equal("hidden", created.GetProperty("placement").GetString());
        Assert.True(created.GetProperty("hidden").GetBoolean());
        return new HiddenChild(paneId!, parentCap!);
    }

    private static async Task<JsonElement> CliAsync(string sock, params string[] args)
    {
        var (code, stdout, stderr) = await RunAsync(sock, args);
        Assert.True(code == 0, stderr + stdout);
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.Clone();
    }

    private static Task<(int Code, string Stdout, string Stderr)> RunAsync(string sock, params string[] args)
    {
        var argv = new List<string> { "--session", "default", "--socket", sock };
        argv.AddRange(args);
        return HypaCliProcess.RunAsync(IsolatedEnv, argv.ToArray());
    }

    private sealed record HiddenChild(string PaneId, string ParentCap);

    private sealed class TestMux : IAsyncDisposable
    {
        private readonly ControlPlaneService _controlPlane;
        private readonly UnixSocketServer _server;

        private TestMux(string dir, string socket, ControlPlaneService controlPlane, UnixSocketServer server)
        {
            Dir = dir;
            Socket = socket;
            _controlPlane = controlPlane;
            _server = server;
        }

        public string Dir { get; }

        public string Socket { get; }

        public static async Task<TestMux> StartAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-bg-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var sock = Path.Combine(dir, "s.sock");
            var state = new AppState(SessionId.New("background-pane"));
            state.UpdateSession(session => session with
            {
                LifecycleState = SessionLifecycle.Ready,
                Placement = "local",
            });
            var intel = new PaneIntelligencePipeline();
            var controlPlane = new ControlPlaneService(
                state,
                TestPaneFactories.Create(intel),
                intel,
                new HeuristicAgentDetector(),
                runtimeSocketPath: sock);
            var server = new UnixSocketServer(controlPlane, sock);
            await server.StartAsync(CancellationToken.None);
            var mux = new TestMux(dir, sock, controlPlane, server);
            try
            {
                var created = await CliAsync(
                    sock,
                    "workspace", "create",
                    "--cwd", dir,
                    "--command", "/bin/sleep",
                    "--args", "180");
                var parentId = created.GetProperty("pane").GetProperty("pane_id").GetString();
                Assert.False(string.IsNullOrEmpty(parentId));
                await AssertHasTiledLeafAsync(sock, parentId!);
                return mux;
            }
            catch
            {
                await mux.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _controlPlane.ShutdownAsync(CancellationToken.None);
            }
            finally
            {
                await _server.DisposeAsync();
                try
                {
                    Directory.Delete(Dir, recursive: true);
                }
                catch
                {
                    // Teardown must not hide the test result.
                }
            }
        }
    }
}
