using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// hypa pane/workspace create must express the argv0 + explicit-wrap contract.
/// </summary>
public class ControlPlaneSpawnArgsTests
{
    [Fact]
    public void Pane_create_cli_forwards_repeatable_args()
    {
        var p = ControlPlaneCliCommands.BuildPaneCreateParams(
        [
            "create",
            "--command", "/bin/bash",
            "--args", "-lc",
            "--args", "echo hello",
            "--run", "run_1",
            "--step", "step_1",
        ]);

        Assert.Equal("/bin/bash", p["command"]?.GetValue<string>());
        Assert.Equal(["-lc", "echo hello"], ReadArgs(p));
        Assert.Equal("run_1", p["run_id"]?.GetValue<string>());
        Assert.Equal("step_1", p["step_id"]?.GetValue<string>());
    }

    [Fact]
    public void Pane_create_cli_forwards_placement()
    {
        var p = ControlPlaneCliCommands.BuildPaneCreateParams(
        [
            "create",
            "--command", "/bin/echo",
            "--placement", "hidden",
        ]);

        Assert.Equal("hidden", p["placement"]?.GetValue<string>());
    }

    [Fact]
    public void Workspace_create_cli_forwards_repeatable_args()
    {
        var p = ControlPlaneCliCommands.BuildWorkspaceCreateParams(
        [
            "create",
            "--cwd", "/tmp",
            "--command", "/bin/bash",
            "--args", "-lc",
            "--args", "ls -la",
        ]);

        Assert.Equal("/tmp", p["cwd"]?.GetValue<string>());
        Assert.Equal("/bin/bash", p["command"]?.GetValue<string>());
        Assert.Equal(["-lc", "ls -la"], ReadArgs(p));
        Assert.True(p["create_pane"]?.GetValue<bool>());
    }

    [Fact]
    public void Workspace_create_cli_omits_cwd_unless_explicit()
    {
        var p = ControlPlaneCliCommands.BuildWorkspaceCreateParams(
        [
            "create",
            "--command", "/bin/bash",
        ]);
        Assert.False(p.ContainsKey("cwd"));
        Assert.True(p["create_pane"]?.GetValue<bool>());
    }

    [Fact]
    public void Cli_help_states_argv0_and_explicit_wrap()
    {
        var help = ControlPlaneCliCommands.GetHelpText();
        Assert.Contains("[--args ARG]...", help, StringComparison.Ordinal);
        Assert.Contains("argv0", help, StringComparison.Ordinal);
        Assert.Contains("A space in CMD does not wrap a shell.", help, StringComparison.Ordinal);
        Assert.Contains("Empty --command starts terminal.default_shell or $SHELL", help, StringComparison.Ordinal);
        Assert.Contains("--command /bin/bash --args -lc --args", help, StringComparison.Ordinal);
        Assert.Contains("tab create", help, StringComparison.Ordinal);
        Assert.Contains("layout export", help, StringComparison.Ordinal);
        Assert.Contains("pane split", help, StringComparison.Ordinal);
        Assert.Contains("agent start", help, StringComparison.Ordinal);
        Assert.Contains("rpc layout.apply", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeatable_args_keeps_dash_values()
    {
        var args = ControlPlaneCliCommands.RepeatableArg(
            ["--command", "/bin/bash", "--args", "-lc", "--args", "-n"],
            "--args");
        Assert.Equal(["-lc", "-n"], args);
    }

    [Fact]
    public async Task Workspace_create_forwards_args_to_pane_factory()
    {
        var state = new AppState(SessionId.New("h21-ws-args"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });

        var factory = new RecordingPaneFactory();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector());

        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["label"] = "ws-args",
                    ["command"] = "/bin/bash",
                    ["args"] = new JsonArray("-lc", "echo hello"),
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            Assert.NotNull(factory.Last);
            Assert.Equal("/bin/bash", factory.Last!.Command);
            Assert.Equal(["-lc", "echo hello"], factory.Last.Args);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Workspace_create_without_args_still_spawns_empty_argv()
    {
        var state = new AppState(SessionId.New("h21-ws-empty"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });

        var factory = new RecordingPaneFactory();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector());

        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "/bin/bash",
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            Assert.NotNull(factory.Last);
            Assert.Equal("/bin/bash", factory.Last!.Command);
            Assert.Empty(factory.Last.Args);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static string[] ReadArgs(JsonObject p)
    {
        var arr = p["args"] as JsonArray;
        Assert.NotNull(arr);
        return arr!.Select(n => n!.GetValue<string>()).ToArray();
    }

    private sealed class RecordingPaneFactory : IPaneRuntimeFactory
    {
        public PaneSpawnOptions? Last { get; private set; }

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            Last = options;
            return new StubPaneRuntime(options.Id);
        }
    }

    private sealed class StubPaneRuntime(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; }
        public int? Pid { get; } = 42_121;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct)
        {
            IsAlive = true;
            return Task.CompletedTask;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }
}
