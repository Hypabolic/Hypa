using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class ServerInstallReportingTests
{
    [Fact]
    public async Task Ping_without_a_probe_keeps_the_original_shape()
    {
        var cp = Create(new TestPaneFactories.FailingPaneFactory(0, false), probe: null);

        var ping = await cp.DispatchAsync(ProtocolMethods.Ping, Empty(), CancellationToken.None);

        Assert.True(ping.GetProperty("ok").GetBoolean());
        Assert.False(ping.TryGetProperty("version", out _));
        Assert.False(ping.TryGetProperty("install_present", out _));
    }

    [Fact]
    public async Task Ping_reports_version_and_removed_install()
    {
        var cp = Create(
            new TestPaneFactories.FailingPaneFactory(0, false),
            new FixedProbe(new ServerInstallStatus("1.0.3", InstallPresent: false)));

        var ping = await cp.DispatchAsync(ProtocolMethods.Ping, Empty(), CancellationToken.None);

        Assert.Equal("1.0.3", ping.GetProperty("version").GetString());
        Assert.False(ping.GetProperty("install_present").GetBoolean());
    }

    [Fact]
    public async Task Pane_start_failure_explains_a_removed_install()
    {
        var cp = Create(
            new TestPaneFactories.FailingPaneFactory(1, failAtCreate: true),
            new FixedProbe(new ServerInstallStatus("1.0.3", InstallPresent: false)));

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() => CreateWorkspaceWithPaneAsync(cp));

        Assert.Equal(ProtocolErrorCodes.PaneStartFailed, ex.Code);
        Assert.Contains("Hypa 1.0.3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("removed by an upgrade", ex.Message, StringComparison.Ordinal);
        Assert.Contains("hypa mux restart --session install-probe", ex.Message, StringComparison.Ordinal);
        Assert.Contains(MuxRestartCommands.SidebarAction, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pane_start_failure_stays_generic_when_install_is_present()
    {
        var cp = Create(
            new TestPaneFactories.FailingPaneFactory(1, failAtCreate: true),
            new FixedProbe(new ServerInstallStatus("1.0.6", InstallPresent: true)));

        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() => CreateWorkspaceWithPaneAsync(cp));

        Assert.Equal(ProtocolErrorCodes.PaneStartFailed, ex.Code);
        Assert.Equal(ProtocolErrors.MeaningOf(ProtocolErrorCodes.PaneStartFailed), ex.Message);
    }

    [Theory]
    [InlineData("1.0.5+3f2a9c1", "1.0.5")]
    [InlineData(" 1.0.5 ", "1.0.5")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Version_drops_build_metadata(string? raw, string? expected) =>
        Assert.Equal(expected, ProcessServerInstallProbe.NormalizeVersion(raw));

    [Fact]
    public void Process_probe_reports_a_deleted_executable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "hypa-runtime");
            File.WriteAllText(exe, "");

            Assert.True(new ProcessServerInstallProbe("1.0.5", exe).Probe().InstallPresent);
            Assert.False(new ProcessServerInstallProbe("1.0.5", exe + " (deleted)").Probe().InstallPresent);

            File.Delete(exe);
            Assert.False(new ProcessServerInstallProbe("1.0.5", exe).Probe().InstallPresent);
            Assert.True(new ProcessServerInstallProbe("1.0.5", null).Probe().InstallPresent);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static ControlPlaneService Create(IPaneRuntimeFactory factory, IServerInstallProbe? probe)
    {
        var app = new AppState(SessionId.New("install-probe"));
        app.UpdateSession(s => s with { Name = "install-probe", LifecycleState = SessionLifecycle.Ready });
        return new ControlPlaneService(
            app,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            installProbe: probe);
    }

    private static Task<JsonElement> CreateWorkspaceWithPaneAsync(ControlPlaneService cp) =>
        cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            JsonDocument.Parse("""{"cwd":"/tmp","command":"sh","label":"probe","create_pane":true}""").RootElement,
            CancellationToken.None);

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement;

    private sealed class FixedProbe(ServerInstallStatus status) : IServerInstallProbe
    {
        public ServerInstallStatus Probe() => status;
    }
}
