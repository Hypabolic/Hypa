using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.ControlPlane;
using Hypa.Terminal;
using Hypa.Terminal.Pty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class TerminalSpawnPolicyTests
{
    [Fact]
    public void Native_window_is_capped_and_the_user_budget_stays()
    {
        Assert.Equal(10_000_000, AttachAdvancedConfig.DefaultScrollbackLimitBytes);
        Assert.Equal(10_000_000, TerminalSpawnPolicy.ResolveScrollbackBytes(10_000_000));
        Assert.Equal(TerminalSpawnPolicy.NativeWindowBytes, TerminalSpawnPolicy.ResolveNativeWindowBytes(10_000_000));
        Assert.True(TerminalSpawnPolicy.NativeWindowBytes < 10_000_000);
        Assert.Equal(1_000_000, TerminalSpawnPolicy.ResolveNativeWindowBytes(1_000_000));
        var bound = TerminalSpawnPolicy.ResolveHistoryStoreRowBound(10_000_000);
        Assert.True(bound >= TerminalSpawnPolicy.HistoryDrainMarginRows);
    }

    [Fact]
    public void Empty_command_honours_default_shell_and_shell_mode()
    {
        var login = TerminalSpawnPolicy.ResolveEmptyCommand(
            new AttachTerminalConfig { DefaultShell = "/bin/zsh", ShellMode = TerminalShellMode.Login },
            shellEnv: "/bin/bash",
            isMacOs: false,
            fileExists: _ => true);
        Assert.Equal("/bin/zsh", login.File);
        Assert.Equal(["-l"], login.Args);

        var nonLogin = TerminalSpawnPolicy.ResolveEmptyCommand(
            new AttachTerminalConfig { DefaultShell = "/bin/zsh", ShellMode = TerminalShellMode.NonLogin },
            shellEnv: "/bin/bash",
            isMacOs: true,
            fileExists: _ => true);
        Assert.Equal("/bin/zsh", nonLogin.File);
        Assert.Empty(nonLogin.Args);

        var autoLinux = TerminalSpawnPolicy.ResolveEmptyCommand(
            AttachTerminalConfig.Default,
            shellEnv: "/bin/bash",
            isMacOs: false,
            fileExists: _ => true);
        Assert.Equal("/bin/bash", autoLinux.File);
        Assert.Empty(autoLinux.Args);

        var autoMac = TerminalSpawnPolicy.ResolveEmptyCommand(
            AttachTerminalConfig.Default,
            shellEnv: "/bin/zsh",
            isMacOs: true,
            fileExists: _ => true);
        Assert.Equal("/bin/zsh", autoMac.File);
        Assert.Equal(["-l"], autoMac.Args);
    }

    [Fact]
    public void Empty_command_falls_back_to_shell_then_sh()
    {
        var fromEnv = TerminalSpawnPolicy.ResolveEmptyCommand(
            AttachTerminalConfig.Default,
            shellEnv: "/usr/local/bin/fish",
            isMacOs: false,
            fileExists: path => path == "/usr/local/bin/fish");
        Assert.Equal("/usr/local/bin/fish", fromEnv.File);

        var fromSh = TerminalSpawnPolicy.ResolveEmptyCommand(
            AttachTerminalConfig.Default,
            shellEnv: "/missing",
            isMacOs: false,
            fileExists: path => path == "/bin/sh");
        Assert.Equal("/bin/sh", fromSh.File);
    }

    [Fact]
    public void New_cwd_follow_home_current_and_path()
    {
        var terminal = AttachTerminalConfig.Default;
        Assert.Equal(
            "/src",
            TerminalSpawnPolicy.ResolveNewCwd(terminal, callerCwd: null, sourceCwd: "/src", processCwd: "/proc", home: "/home/u"));
        Assert.Equal(
            "/home/u",
            TerminalSpawnPolicy.ResolveNewCwd(terminal, callerCwd: null, sourceCwd: null, processCwd: "/proc", home: "/home/u"));
        Assert.Equal(
            "/caller",
            TerminalSpawnPolicy.ResolveNewCwd(terminal, callerCwd: "/caller", sourceCwd: "/src", processCwd: "/proc", home: "/home/u"));

        Assert.Equal(
            "/home/u",
            TerminalSpawnPolicy.ResolveNewCwd(
                terminal with { NewCwd = TerminalNewCwdSpec.Home },
                callerCwd: null,
                sourceCwd: "/src",
                processCwd: "/proc",
                home: "/home/u"));

        Assert.Equal(
            "/proc",
            TerminalSpawnPolicy.ResolveNewCwd(
                terminal with { NewCwd = TerminalNewCwdSpec.Current },
                callerCwd: null,
                sourceCwd: "/src",
                processCwd: "/proc",
                home: "/home/u"));

        Assert.Equal(
            Path.Combine("/home/u", "work"),
            TerminalSpawnPolicy.ResolveNewCwd(
                terminal with { NewCwd = TerminalNewCwdSpec.ForPath("~/work") },
                callerCwd: null,
                sourceCwd: "/src",
                processCwd: "/proc",
                home: "/home/u"));
    }

    [Fact]
    public async Task Scrollback_limit_bytes_reaches_pane_spawn_options()
    {
        var state = new AppState(SessionId.New("h35-scroll"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = new RecordingPaneFactory();
        var config = AttachClientConfig.Default with
        {
            Advanced = new AttachAdvancedConfig { ScrollbackLimitBytes = 2_000_000 },
        };
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachConfig: config);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                System.Text.Json.JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "/bin/true",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            Assert.NotNull(factory.Last);
            Assert.Equal(2_000_000, factory.Last!.ScrollbackLimitBytes);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Workspace_create_without_cwd_applies_new_cwd_home()
    {
        var state = new AppState(SessionId.New("h35-cwd-home"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = new RecordingPaneFactory();
        var config = AttachClientConfig.Default with
        {
            Terminal = AttachTerminalConfig.Default with { NewCwd = TerminalNewCwdSpec.Home },
        };
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachConfig: config);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                System.Text.Json.JsonDocument.Parse("""{"create_pane":true,"command":"/bin/true"}""").RootElement,
                CancellationToken.None);
            var ws = Assert.Single(state.ListWorkspaces());
            var expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(expected))
                expected = Environment.CurrentDirectory;
            Assert.Equal(expected, ws.Cwd);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Workspace_create_without_cwd_or_source_follows_home()
    {
        var state = new AppState(SessionId.New("h35-cwd-follow-home"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = new RecordingPaneFactory();
        var config = AttachClientConfig.Default with
        {
            Terminal = AttachTerminalConfig.Default with { NewCwd = TerminalNewCwdSpec.Follow },
        };
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachConfig: config);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                System.Text.Json.JsonDocument.Parse("""{"create_pane":true,"command":"/bin/true"}""").RootElement,
                CancellationToken.None);
            var ws = Assert.Single(state.ListWorkspaces());
            var expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.False(string.IsNullOrWhiteSpace(expected));
            Assert.Equal(expected, ws.Cwd);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Workspace_create_without_cwd_follows_focused_workspace()
    {
        var first = Path.Combine(Path.GetTempPath(), "h35-follow-a-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(first);
        var state = new AppState(SessionId.New("h35-cwd-follow"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = new RecordingPaneFactory();
        var config = AttachClientConfig.Default with
        {
            Terminal = AttachTerminalConfig.Default with { NewCwd = TerminalNewCwdSpec.Follow },
        };
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachConfig: config);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                System.Text.Json.JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = first,
                    ["command"] = "/bin/true",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            await cp.DispatchAsync(
                "workspace.create",
                System.Text.Json.JsonDocument.Parse("""{"create_pane":true,"command":"/bin/true"}""").RootElement,
                CancellationToken.None);
            var workspaces = state.ListWorkspaces();
            Assert.Equal(2, workspaces.Count);
            Assert.Equal(first, workspaces[0].Cwd);
            Assert.Equal(first, workspaces[1].Cwd);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            try { Directory.Delete(first, recursive: true); } catch { /* teardown */ }
        }
    }

    [Fact]
    public void Hypa_env_is_set_on_children()
    {
        var env = ChildEnvironmentBuilder.BuildHosted(
            explicitEnv: null,
            parentEnv: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PATH"] = "/bin",
                ["HOME"] = "/home/u",
            });
        Assert.Equal("1", env["HYPA_ENV"]);
    }

    [Fact]
    public void PaneRuntime_resolve_uses_terminal_config()
    {
        var (file, args) = PaneRuntime.ResolveCommand(
            "",
            [],
            new AttachTerminalConfig { DefaultShell = "/opt/shell", ShellMode = TerminalShellMode.NonLogin });
        Assert.Equal("/opt/shell", file);
        Assert.Empty(args);
    }

    [Fact]
    public void ResolveRestoreCwd_uses_saved_directory_then_home_then_root()
    {
        bool Exists(string path) => path is "/saved" or "/home/u";
        Assert.Equal(
            "/saved",
            TerminalSpawnPolicy.ResolveRestoreCwd("/saved", "/home/u", Exists));
        Assert.Equal(
            "/home/u",
            TerminalSpawnPolicy.ResolveRestoreCwd("/gone", "/home/u", Exists));
        Assert.Equal(
            "/",
            TerminalSpawnPolicy.ResolveRestoreCwd("/gone", "/missing", Exists));
        Assert.Equal(
            "/",
            TerminalSpawnPolicy.ResolveRestoreCwd(null, null, Exists));
        Assert.Equal(
            "/",
            TerminalSpawnPolicy.ResolveRestoreCwd("  ", "", Exists));
    }

    [Fact]
    public void Scrollback_lines_are_derived_from_bytes()
    {
        Assert.Equal(100, TerminalSpawnPolicy.ResolveScrollbackLines(12_000, cols: 120));
        Assert.Equal(1, TerminalSpawnPolicy.ResolveScrollbackLines(10, cols: 120));
    }

    [Fact]
    public void Ghostty_scrollback_budget_is_bytes_not_divided_by_columns()
    {
        const long budget = 8 * 1024 * 1024;
        Assert.Equal(budget, TerminalSpawnPolicy.ResolveScrollbackBytes(budget));
        Assert.Equal(budget, TerminalSpawnPolicy.ResolveScrollbackBytes(budget));
        Assert.NotEqual(
            TerminalSpawnPolicy.ResolveScrollbackLines(budget, cols: 80),
            TerminalSpawnPolicy.ResolveScrollbackBytes(budget));
        Assert.Equal(1, TerminalSpawnPolicy.ResolveScrollbackBytes(0));
    }

    private sealed class RecordingPaneFactory : IPaneRuntimeFactory
    {
        public PaneSpawnOptions? Last { get; private set; }

        public IPaneRuntime Create(PaneSpawnOptions options)
        {
            Last = options;
            return new GraphPaneRuntime(options.Id);
        }
    }

    private sealed class GraphPaneRuntime(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive => true;
        public int? ExitCode => null;
        public int? Pid => 351;
#pragma warning disable CS0067
        public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
        public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteTextAsync(string text, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct) => ValueTask.CompletedTask;
        public string ReadVisibleText() => "";
        public string ReadRecentText(int maxLines) => "";
        public string ReadRecentUnwrappedText(int maxLines) => ReadRecentText(maxLines);
        public string ReadDetectionText() => "";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
