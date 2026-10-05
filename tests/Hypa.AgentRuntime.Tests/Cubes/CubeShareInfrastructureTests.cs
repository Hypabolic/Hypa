using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Cubes;
using Hypa.AgentRuntime.Infrastructure;
using Hypa.AgentRuntime.Infrastructure.Cubes;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests.Cubes;

public sealed class CubeShareInfrastructureTests
{
    [Fact]
    public void Settings_store_round_trips_enabled_share_as_owner_only()
    {
        var dir = TempDir();
        try
        {
            var store = new FileCubeShareSettingsStore(dir);
            Assert.Null(store.LoadEnabled());

            store.SaveEnabled(new CubeShareSettings { Bind = "::", Port = 7500 });

            Assert.Equal(new CubeShareSettings { Bind = "::", Port = 7500 }, store.LoadEnabled());
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(store.FilePath));
            }

            store.Clear();
            Assert.Null(store.LoadEnabled());
            Assert.False(File.Exists(store.FilePath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"enabled":false,"bind":"0.0.0.0","port":7443}""")]
    [InlineData("""{"enabled":true,"bind":"","port":7443}""")]
    [InlineData("""{"enabled":true,"bind":"0.0.0.0","port":70000}""")]
    public void Settings_store_treats_a_bad_file_as_share_off(string text)
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, FileCubeShareSettingsStore.FileName), text);
            Assert.Null(new FileCubeShareSettingsStore(dir).LoadEnabled());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Listener_start_info_ties_the_child_to_the_mux()
    {
        var start = ProcessCubeShareListenerLauncher.CreateStartInfo(
            "/opt/hypa/hypa",
            "default",
            "/home/me/.config/hypa/runtime/default/hypa.sock",
            new CubeShareSettings { Bind = "::", Port = 7500 });

        Assert.Equal("/opt/hypa/hypa", start.FileName);
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.False(start.UseShellExecute);
        Assert.Equal(
            [
                "connectivity", "accept",
                "--session", "default",
                "--socket", "/home/me/.config/hypa/runtime/default/hypa.sock",
                "--bind", "::",
                "--port", "7500",
                "--json",
                "--exit-on-stdin-eof",
            ],
            start.ArgumentList.ToArray());
        Assert.DoesNotContain("--advertise-host", start.ArgumentList);
    }

    [Fact]
    public void Listen_line_parses_the_accept_json()
    {
        var line = """{"ok":true,"bind":"0.0.0.0","port":7443,"tls":true,"quic_listening":false,"quic_detail":"application directory is writable by group or others","certificate_sha256":"abc"}""";

        Assert.True(ProcessCubeShareListenerLauncher.TryParseListen(line, out var listen));

        Assert.Equal("0.0.0.0", listen.Bind);
        Assert.Equal(7443, listen.Port);
        Assert.Equal("abc", listen.CertificateSha256);
        Assert.False(listen.QuicListening);
        Assert.Equal("application directory is writable by group or others", listen.QuicDetail);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bind: 0.0.0.0")]
    [InlineData("""{"ok":false,"port":7443}""")]
    [InlineData("""{"ok":true}""")]
    public void Listen_line_rejects_anything_but_a_ready_document(string line) =>
        Assert.False(ProcessCubeShareListenerLauncher.TryParseListen(line, out _));

    [Fact]
    public void Start_error_names_an_occupied_port()
    {
        Assert.Equal(
            "port 7443 is already in use",
            ProcessCubeShareListenerLauncher.FormatStartError("port 7443 is already in use", 7443));
        Assert.Equal(
            "port 7443 is already in use",
            ProcessCubeShareListenerLauncher.FormatStartError(
                "Unhandled exception: System.Net.Sockets.SocketException (48): Address already in use",
                7443));
        Assert.Equal(
            "mux session is not listening.",
            ProcessCubeShareListenerLauncher.FormatStartError("mux session is not listening.", 7443));
    }

    [Fact]
    public async Task Missing_cli_fails_the_launch_without_throwing()
    {
        var launcher = new ProcessCubeShareListenerLauncher(cliPath: null, "default", "/tmp/hypa.sock");

        var launched = await launcher.LaunchAsync(new CubeShareSettings(), CancellationToken.None);

        Assert.False(launched.IsOk);
        Assert.Contains("hypa binary", launched.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Share_methods_report_unavailable_without_a_share_host()
    {
        var cp = Create(cubeShare: null);

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(
            () => cp.DispatchAsync(ProtocolMethods.CubeShareStatus, Json("{}"), CancellationToken.None));

        Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
        Assert.Equal("share_unavailable", ex.ErrorCode);
    }

    [Fact]
    public async Task Share_start_applies_defaults_and_returns_the_host_status()
    {
        var host = new RecordingHost();
        var cp = Create(host);

        var result = await cp.DispatchAsync(ProtocolMethods.CubeShareStart, Json("{}"), CancellationToken.None);

        Assert.Equal(new CubeShareSettings(), host.Started);
        var status = result.Deserialize(ProtocolJsonContext.Default.CubeShareStatusResult)!;
        Assert.True(status.Enabled);
        Assert.Equal("running", status.State);
        Assert.Equal("0.0.0.0", status.Bind);
        Assert.Equal(7443, status.Port);
        Assert.Equal("abc", status.Listen!.CertificateSha256);
    }

    [Fact]
    public async Task Share_start_rejects_an_out_of_range_port()
    {
        var host = new RecordingHost();
        var cp = Create(host);

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(
            () => cp.DispatchAsync(ProtocolMethods.CubeShareStart, Json("""{"port":0}"""), CancellationToken.None));

        Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
        Assert.Null(host.Started);
    }

    [Fact]
    public async Task Share_stop_disables_share()
    {
        var host = new RecordingHost();
        var cp = Create(host);

        var result = await cp.DispatchAsync(ProtocolMethods.CubeShareStop, Json("{}"), CancellationToken.None);

        Assert.True(host.Stopped);
        Assert.Equal("stopped", result.GetProperty("state").GetString());
        Assert.False(result.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void Fixtures_round_trip()
    {
        foreach (var method in FixtureCatalog.CubeShareMethods)
        {
            var req = JsonSerializer.Deserialize(
                FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(method)),
                ProtocolJsonContext.Default.RpcRequest)!;
            var res = JsonSerializer.Deserialize(
                FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(method)),
                ProtocolJsonContext.Default.RpcResponse)!;
            Assert.Equal(method, req.Method);
            Assert.NotNull(JsonSerializer.Deserialize(res.Result!.Value, ProtocolJsonContext.Default.CubeShareStatusResult));
            if (method == ProtocolMethods.CubeShareStart)
                Assert.NotNull(JsonSerializer.Deserialize(req.Params!.Value, ProtocolJsonContext.Default.CubeShareStartParams));
        }
    }

    private static ControlPlaneService Create(ICubeShareHost? cubeShare)
    {
        var app = new AppState(SessionId.New("cube-share"));
        app.UpdateSession(s => s with { Name = "cube-share", LifecycleState = SessionLifecycle.Ready });
        return new ControlPlaneService(
            app,
            new TestPaneFactories.FailingPaneFactory(0, false),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            cubeShare: cubeShare);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class RecordingHost : ICubeShareHost
    {
        public CubeShareSettings? Started { get; private set; }

        public bool Stopped { get; private set; }

        public CubeShareStatus Status { get; private set; } = CubeShareStatus.Stopped;

        public Task<CubeShareStatus> StartAsync(CubeShareSettings settings, CancellationToken ct)
        {
            Started = settings;
            Status = new CubeShareStatus
            {
                Enabled = true,
                State = CubeShareStates.Running,
                Settings = settings,
                Listen = new CubeShareListen { Bind = settings.Bind, Port = settings.Port, CertificateSha256 = "abc" },
            };
            return Task.FromResult(Status);
        }

        public Task<CubeShareStatus> StopAsync(CancellationToken ct)
        {
            Stopped = true;
            Status = CubeShareStatus.Stopped;
            return Task.FromResult(Status);
        }
    }
}
