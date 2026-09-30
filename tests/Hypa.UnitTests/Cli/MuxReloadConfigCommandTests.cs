using System.CommandLine;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Commands;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class MuxReloadConfigCommandTests
{
    [Fact]
    public void Help_lists_reload_config()
    {
        var mux = new MuxCommand().Build();
        Assert.Contains(mux.Subcommands, c => c.Name == "reload-config");
        var reload = mux.Subcommands.Single(c => c.Name == "reload-config");
        Assert.Contains("server.reload_config", reload.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extra_args_exit_4()
    {
        var root = new RootCommand("hypa");
        root.Add(new MuxCommand().Build());
        var exit = await root.Parse(["mux", "reload-config", "extra"]).InvokeAsync();
        Assert.Equal(4, exit);
    }

    [Fact]
    public void Reload_session_does_not_use_file_session_name()
    {
        var previous = Environment.GetEnvironmentVariable(AttachSessionResolver.SessionEnv);
        Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, null);
        try
        {
            Assert.Equal(AttachSessionResolver.DefaultName, MuxCommand.ResolveReloadSession(null, false));
            Assert.Equal("work", MuxCommand.ResolveReloadSession("work", true));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, previous);
        }
    }

    [Fact]
    public void Reload_socket_without_flags_is_default_not_file_session_name()
    {
        var previousSession = Environment.GetEnvironmentVariable(AttachSessionResolver.SessionEnv);
        var previousSocket = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
        Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, null);
        Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", null);
        try
        {
            var session = MuxCommand.ResolveReloadSession(null, false);
            var path = MuxCommand.ResolveStopSocket(session, socketOverride: null, sessionExplicit: false);
            Assert.Equal(AttachSessionResolver.DefaultName, session);
            Assert.Contains(Path.Combine("runtime", "default", "hypa.sock"), path, StringComparison.Ordinal);
            Assert.DoesNotContain("renamed", path, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, previousSession);
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", previousSocket);
        }
    }

    [Fact]
    public void Reload_session_uses_hypa_session_then_explicit()
    {
        var previous = Environment.GetEnvironmentVariable(AttachSessionResolver.SessionEnv);
        Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, "from-env");
        try
        {
            Assert.Equal("from-env", MuxCommand.ResolveReloadSession(null, false));
            Assert.Equal("flag", MuxCommand.ResolveReloadSession("flag", true));
        }
        finally
        {
            Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, previous);
        }
    }

    [Fact]
    public async Task Invalid_toml_still_calls_server_reload_config()
    {
        var loader = new CountingLoader(fail: true, sessionName: "renamed");
        var root = new RootCommand("hypa");
        root.Add(new MuxCommand(loader).Build());
        var socket = Path.Combine(
            Path.GetTempPath(),
            "hypa-h52-missing-" + Guid.NewGuid().ToString("N")[..12],
            "hypa.sock");
        var captured = new StringWriter();
        var old = Console.Error;
        Console.SetError(captured);
        try
        {
            var exit = await root.Parse(["mux", "reload-config", "--socket", socket]).InvokeAsync();
            Assert.Equal(2, exit);
            Assert.Equal(0, loader.LoadCount);
            Assert.Contains(socket, captured.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(old);
        }
    }

    [SkippableFact]
    public async Task Live_rpc_applied_reaches_server_reload_config()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var runtime = new ScriptedRuntime(AttachConfigReloadReport.Applied());
        await using var fixture = await LiveMux.StartAsync(runtime);
        var root = new RootCommand("hypa");
        root.Add(new MuxCommand().Build());
        var captured = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            var exit = await root.Parse(["mux", "reload-config", "--socket", fixture.SocketPath])
                .InvokeAsync();
            Assert.Equal(0, exit);
            Assert.Equal(1, runtime.ReloadCount);
            Assert.Contains("applied", captured.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [SkippableFact]
    public async Task Live_rpc_failed_status_is_exit_1()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var runtime = new ScriptedRuntime(AttachConfigReloadReport.Failed(["Invalid TOML."]));
        await using var fixture = await LiveMux.StartAsync(runtime);
        var root = new RootCommand("hypa");
        root.Add(new MuxCommand().Build());
        var captured = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            var exit = await root.Parse(["mux", "reload-config", "--socket", fixture.SocketPath])
                .InvokeAsync();
            Assert.Equal(1, exit);
            Assert.Equal(1, runtime.ReloadCount);
            Assert.Contains("failed", captured.ToString(), StringComparison.Ordinal);
            Assert.Contains("Invalid TOML.", captured.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public async Task Session_name_change_does_not_retarget_running_mux()
    {
        var loader = new CountingLoader(fail: false, sessionName: "renamed");
        var root = new RootCommand("hypa");
        root.Add(new MuxCommand(loader).Build());
        var session = "h52-reload-" + Guid.NewGuid().ToString("N")[..12];
        var previousSession = Environment.GetEnvironmentVariable(AttachSessionResolver.SessionEnv);
        var previousSocket = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
        Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, null);
        Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", null);
        var captured = new StringWriter();
        var old = Console.Error;
        Console.SetError(captured);
        try
        {
            var exit = await root.Parse(["mux", "reload-config", "--session", session]).InvokeAsync();
            Assert.Equal(2, exit);
            Assert.Equal(0, loader.LoadCount);
            var err = captured.ToString();
            var expected = UnixSocketServer.ResolveSocketPath(session, honorEnvironment: false);
            var renamed = UnixSocketServer.ResolveSocketPath("renamed", honorEnvironment: false);
            Assert.Contains(expected, err, StringComparison.Ordinal);
            Assert.DoesNotContain(renamed, err, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(old);
            Environment.SetEnvironmentVariable(AttachSessionResolver.SessionEnv, previousSession);
            Environment.SetEnvironmentVariable("HYPA_RUNTIME_SOCKET", previousSocket);
        }
    }

    private sealed class CountingLoader : IAttachConfigLoader
    {
        private readonly bool _fail;
        private readonly AttachClientConfig _config;

        public CountingLoader(bool fail, string sessionName)
        {
            _fail = fail;
            _config = AttachClientConfig.Default with
            {
                Session = new AttachSessionConfig { Name = sessionName },
            };
        }

        public int LoadCount { get; private set; }

        public string ResolvePath() => "/tmp/h52-attach.toml";

        public AttachConfigResult<AttachClientConfig> Load()
        {
            LoadCount++;
            return _fail
                ? AttachConfigResult<AttachClientConfig>.Fail(AttachConfigError.Toml("Invalid TOML."))
                : AttachConfigResult<AttachClientConfig>.Ok(_config);
        }

        public AttachConfigResult<AttachClientConfig> Parse(string text) => Load();

        public string DefaultToml() => "";

        public AttachConfigResult<AttachConfigResetResult> ResetKeys() =>
            AttachConfigResult<AttachConfigResetResult>.Ok(new("ok", null, null, false));
    }

    private sealed class ScriptedRuntime(AttachConfigReloadReport report) : IAttachConfigRuntime
    {
        public int ReloadCount { get; private set; }

        public AttachClientConfig Current { get; } = AttachClientConfig.Default;

        public AttachConfigReloadReport ReloadFromDisk()
        {
            ReloadCount++;
            return report;
        }
    }

    private sealed class LiveMux : IAsyncDisposable
    {
        private LiveMux(
            string dir,
            string socketPath,
            ControlPlaneService controlPlane,
            UnixSocketServer server)
        {
            _dir = dir;
            SocketPath = socketPath;
            _controlPlane = controlPlane;
            _server = server;
        }

        private readonly string _dir;
        private readonly ControlPlaneService _controlPlane;
        private readonly UnixSocketServer _server;

        public string SocketPath { get; }

        public static async Task<LiveMux> StartAsync(IAttachConfigRuntime runtime)
        {
            var dir = Path.Combine(
                Path.GetTempPath(),
                "hypa-h52-cli-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(dir);
            var socketPath = Path.Combine(dir, "hypa.sock");
            var state = new AppState(SessionId.New("h52-cli"));
            state.UpdateSession(s => s with
            {
                Name = "h52-cli",
                LifecycleState = SessionLifecycle.Ready,
            });
            var controlPlane = new ControlPlaneService(
                state,
                new GraphPaneFactory(),
                new NullIntel(),
                new NullDetector(),
                attachConfigRuntime: runtime);
            var server = new UnixSocketServer(controlPlane, socketPath);
            await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
            return new LiveMux(dir, socketPath, controlPlane, server);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            await _controlPlane.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // tmp
            }
        }
    }

    private sealed class GraphPaneFactory : IPaneRuntimeFactory
    {
        public IPaneRuntime Create(PaneSpawnOptions options) => new GraphPaneRuntime(options.Id);
    }

    private sealed class GraphPaneRuntime(PaneId id) : IPaneRuntime
    {
        public PaneId Id { get; } = id;
        public bool IsAlive { get; private set; } = true;
        public int? ExitCode { get; private set; }
        public int? Pid { get; private set; } = 52_002;
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

    private sealed class NullIntel : IIntelligencePipeline
    {
        public void OnPaneOutput(PaneId paneId, ReadOnlySpan<byte> data)
        {
        }

        public void OnPaneInput(PaneId paneId, string text)
        {
        }

        public string CompressForAgent(PaneId paneId, string rawText, string? commandHint = null) =>
            rawText;

        public void BindAtomic(PaneId paneId, AtomicBinding binding)
        {
        }

        public void RemovePane(PaneId paneId)
        {
        }
    }

    private sealed class NullDetector : IAgentDetector
    {
        public DetectionResult Detect(string snapshotText, string? processName = null) => new();
    }
}
