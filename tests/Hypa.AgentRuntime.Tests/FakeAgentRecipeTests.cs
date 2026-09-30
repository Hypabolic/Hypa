using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Helper-agent recipe against a test mux: split with --no-focus, agent start
/// of the fake-agent script, agent prompt --wait, agent read, and pane close.
/// </summary>
public class FakeAgentRecipeTests
{
    [SkippableFact]
    public async Task Helper_agent_recipe_splits_starts_prompts_reads_and_closes()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-fake-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-agent.sh");
        Assert.True(File.Exists(script), script);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var state = new AppState(SessionId.New("fake-agent"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Create(intel),
            intel,
            new HeuristicAgentDetector(),
            runtimeSocketPath: sock);
        await using var server = new UnixSocketServer(cp, sock);
        try
        {
            await server.StartAsync(CancellationToken.None);

            var created = await CliAsync(sock, "workspace", "create", "--cwd", dir, "--command", "/bin/sleep", "--args", "180");
            var parentId = created.GetProperty("pane").GetProperty("pane_id").GetString();
            var tabId = created.GetProperty("pane").GetProperty("tab_id").GetString();
            Assert.False(string.IsNullOrEmpty(parentId));
            Assert.False(string.IsNullOrEmpty(tabId));

            var split = await CliAsync(
                sock,
                "pane", "split", parentId!,
                "--direction", "right",
                "--no-focus",
                "--command", "/bin/sleep",
                "--args", "180");
            var childId = split.GetProperty("pane_id").GetString();
            Assert.False(string.IsNullOrEmpty(childId));
            Assert.NotEqual(parentId, childId);

            var tab = await CliAsync(sock, "tab", "get", tabId!);
            Assert.Equal(parentId, tab.GetProperty("focused_pane_id").GetString());

            var cli = HypaCliProcess.CliPath();
            var started = await CliAsync(
                sock,
                "agent", "start", childId!,
                "--command", script,
                "--args", sock,
                "--args", cli);
            Assert.Equal(childId, started.GetProperty("pane_id").GetString());
            Assert.True(started.GetProperty("alive").GetBoolean());

            var prompt = await CliAsync(
                sock,
                "--timeout-ms", "60000",
                "agent", "prompt", childId!,
                "hello-from-recipe",
                "--wait",
                "--timeout", "45000");
            Assert.True(
                prompt.GetProperty("state").GetString() == "blocked",
                prompt.GetRawText());
            Assert.Equal("fake", prompt.GetProperty("agent").GetString());
            Assert.False(prompt.GetProperty("wait").GetProperty("timed_out").GetBoolean());

            var read = await ReadUntilAsync(sock, childId!, "hello-from-recipe");
            var raw = read.GetProperty("raw").GetString() ?? "";
            var text = read.GetProperty("text").GetString() ?? "";
            Assert.True(
                raw.Contains("hello-from-recipe", StringComparison.Ordinal)
                || text.Contains("hello-from-recipe", StringComparison.Ordinal),
                raw + text);
            Assert.Equal("blocked", read.GetProperty("agent_status").GetString());

            var closed = await CliAsync(sock, "pane", "close", childId!);
            Assert.True(closed.GetProperty("ok").GetBoolean(), closed.GetRawText());
            Assert.Equal(childId, closed.GetProperty("pane_id").GetString());

            var listed = await CliAsync(sock, "pane", "list");
            var paneIds = listed.EnumerateArray()
                .Select(pane => pane.GetProperty("pane_id").GetString())
                .ToArray();
            Assert.Contains(parentId, paneIds);
            Assert.DoesNotContain(childId, paneIds);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            try { Directory.Delete(dir, recursive: true); } catch { /* teardown */ }
        }
    }

    private static async Task<JsonElement> ReadUntilAsync(string sock, string paneId, string needle)
    {
        JsonElement last = default;
        for (var i = 0; i < 20; i++)
        {
            last = await CliAsync(sock, "agent", "read", paneId);
            var raw = last.GetProperty("raw").GetString() ?? "";
            var text = last.GetProperty("text").GetString() ?? "";
            if (raw.Contains(needle, StringComparison.Ordinal) || text.Contains(needle, StringComparison.Ordinal))
                return last;
            await Task.Delay(100);
        }

        return last;
    }

    private static async Task<JsonElement> CliAsync(string sock, params string[] args)
    {
        var argv = new List<string> { "--session", "default", "--socket", sock };
        argv.AddRange(args);
        var (code, stdout, stderr) = await HypaCliProcess.RunAsync(argv.ToArray());
        Assert.True(code == 0, stderr + stdout);
        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.Clone();
    }
}
