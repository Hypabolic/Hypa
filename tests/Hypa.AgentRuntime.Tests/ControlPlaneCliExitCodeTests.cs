using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ControlPlaneCliExitCodeTests
{
    [Fact]
    public async Task Connect_miss_is_exit_2()
    {
        var missing = Path.Combine(Path.GetTempPath(), "hypa-missing-" + Guid.NewGuid().ToString("N") + ".sock");
        var code = await ControlPlaneCliCommands.Run(["--session", "default", "--socket", missing, "ping"]);
        Assert.Equal(2, code);
    }

    [Fact]
    public async Task Unknown_flag_is_exit_4()
    {
        var code = await ControlPlaneCliCommands.Run(["pane", "send", "--not-a-flag", "x"]);
        Assert.Equal(4, code);
    }

    [Fact]
    public async Task Ping_dry_run_is_a_usage_error()
    {
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            var code = await ControlPlaneCliCommands.Run(["ping", "--dry-run"]);
            Assert.Equal(4, code);
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.Contains("--dry-run applies to integration install", stderr.ToString(), StringComparison.Ordinal);
        var uninstall = await ControlPlaneCliCommands.Run(["integration", "uninstall", "claude", "--dry-run"]);
        Assert.Equal(4, uninstall);
        var parsed = ControlPlaneCliCommands.Parse(["integration", "install", "claude", "--dry-run"]);
        Assert.Contains("--dry-run", parsed.Switches);
    }

    [SkippableFact]
    public async Task Protocol_error_is_exit_1()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-cli1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        try
        {
            var state = new AppState(SessionId.New("cli1"));
            var intel = new PaneIntelligencePipeline();
            var cp = new ControlPlaneService(state, TestPaneFactories.Create(intel), intel, new HeuristicAgentDetector());
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);

            var code = await ControlPlaneCliCommands.Run(
                ["--session", "default", "--socket", sock, "rpc", "no.such.method"]);
            Assert.Equal(1, code);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* teardown */ }
        }
    }

    [SkippableFact]
    public async Task Call_timeout_against_listen_only_socket_is_exit_3()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-cli3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        Socket? listener = null;
        try
        {
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(sock));
            listener.Listen(8);

            var code = await ControlPlaneCliCommands.Run(
                ["--session", "default", "--socket", sock, "--timeout-ms", "200", "ping"]);
            Assert.Equal(3, code);
        }
        finally
        {
            listener?.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { /* teardown */ }
        }
    }

    [SkippableFact]
    public async Task Agent_attach_run_invokes_live_attach_and_does_not_print_control_json()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var dir = Path.Combine(Path.GetTempPath(), "h8a" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        try
        {
            var state = new AppState(SessionId.New("cli-attach"));
            var intel = new PaneIntelligencePipeline();
            var cp = new ControlPlaneService(state, TestPaneFactories.Scripted(), intel, new HeuristicAgentDetector());
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);

            using var create = JsonDocument.Parse(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["command"] = "muse",
                ["create_pane"] = true,
            }.ToJsonString());
            await cp.DispatchAsync("workspace.create", create.RootElement.Clone(), CancellationToken.None);

            var paneId = state.ListPanes()[0].Id.Value;
            var driver = new RecordingDriver();
            var supervisor = new ReadySupervisor(sock);
            ILiveAttachHost liveAttach = new MuxAttachService(supervisor, driver);
            var captured = new StringWriter();
            var capturedErr = new StringWriter();
            var previous = Console.Out;
            var previousErr = Console.Error;
            Console.SetOut(captured);
            Console.SetError(capturedErr);
            int code;
            try
            {
                code = await ControlPlaneCliCommands.Run(
                    ["--session", "default", "--socket", sock, "agent", "attach", paneId],
                    attachConfig: null,
                    liveAttach);
            }
            finally
            {
                Console.SetOut(previous);
                Console.SetError(previousErr);
            }

            Assert.True(code == 0, capturedErr.ToString());
            Assert.Equal(paneId, driver.Last?.FocusPaneId);
            Assert.DoesNotContain("lease_id", captured.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("subscription_id", captured.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* teardown */ }
        }
    }

    private sealed class ReadySupervisor(string socketPath) : IMuxSupervisor
    {
        public Task<MuxReadyInfo> EnsureReadyAsync(
            string session,
            string? cwd,
            string? socketOverride,
            CancellationToken ct) =>
            Task.FromResult(new MuxReadyInfo(session, socketOverride ?? socketPath, """{"ok":true}"""));
    }

    private sealed class RecordingDriver : IMuxAttachDriver
    {
        public MuxAttachRequest? Last { get; private set; }

        public Task<int> RunAsync(MuxReadyInfo ready, MuxAttachRequest request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(0);
        }
    }
}
