using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Hooks;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class InitServiceTests
{
    private readonly IHarnessRegistry _registry = Substitute.For<IHarnessRegistry>();
    private readonly IHookInstaller _installer = Substitute.For<IHookInstaller>();
    private readonly IProjectRootDetector _rootDetector = Substitute.For<IProjectRootDetector>();
    private readonly IProjectRegistry _projectRegistry = Substitute.For<IProjectRegistry>();
    private readonly InitService _service;

    public InitServiceTests()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/repo/root");
        _service = new InitService(_registry, _installer, _rootDetector, _projectRegistry);
    }

    [Fact]
    public async Task InstallAsync_AdapterNotDetected_ReportsSkipped()
    {
        var adapter = MakeAdapter("claude", detected: false);
        _registry.All.Returns([adapter]);

        var reports = await _service.InstallAsync(global: false, agentKey: null, dryRun: false);

        Assert.Single(reports);
        Assert.Equal("claude", reports[0].HarnessKey);
        Assert.Equal(InstallStatus.Skipped, reports[0].Entries[0].Status);
        await _installer.DidNotReceive().InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ExplicitAgent_InstallsEvenWhenNotDetected()
    {
        var adapter = MakeAdapter("claude", detected: false);
        _registry.Find("claude").Returns(adapter);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        var reports = await _service.InstallAsync(global: false, agentKey: "claude", dryRun: false);

        Assert.Single(reports);
        await _installer.Received(1).InstallAsync(Arg.Any<InstallPlan>(), "claude", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_DetectedAdapter_CallsInstaller()
    {
        var adapter = MakeAdapter("claude", detected: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        var reports = await _service.InstallAsync(global: false, agentKey: null, dryRun: false);

        Assert.Single(reports);
        await _installer.Received(1).InstallAsync(Arg.Any<InstallPlan>(), "claude", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_UnknownExplicitAgent_ReturnsEmpty()
    {
        _registry.Find("nonexistent").Returns((IAgentHarnessAdapter?)null);

        var reports = await _service.InstallAsync(global: false, agentKey: "nonexistent", dryRun: false);

        Assert.Empty(reports);
    }

    [Fact]
    public async Task InstallAsync_Global_PassesNullProjectRoot()
    {
        var adapter = MakeAdapter("claude", detected: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        await _service.InstallAsync(global: true, agentKey: null, dryRun: false);

        adapter.Received(1).GetInstallPlan(global: true, projectRoot: null);
    }

    [Fact]
    public async Task InstallAsync_Local_PassesDetectedProjectRoot()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/detected/root");
        var adapter = MakeAdapter("claude", detected: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        await _service.InstallAsync(global: false, agentKey: null, dryRun: false);

        adapter.Received(1).GetInstallPlan(global: false, projectRoot: "/detected/root");
    }

    [Fact]
    public async Task InstallAsync_DryRun_ForwardsDryRunToInstaller()
    {
        var adapter = MakeAdapter("claude", detected: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        await _service.InstallAsync(global: false, agentKey: null, dryRun: true);

        await _installer.Received(1).InstallAsync(Arg.Any<InstallPlan>(), "claude", dryRun: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ProjectScoped_NonDryRun_WithInstalledEntries_RegistersProject()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/my/project");
        var adapter = MakeAdapter("codex", detected: true);
        _registry.Find("codex").Returns(adapter);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), "codex", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("codex", [new InstallEntry("hook", InstallStatus.Installed)]));

        await _service.InstallAsync(global: false, agentKey: "codex", dryRun: false);

        await _projectRegistry.Received(1).RegisterAsync("/my/project", "codex", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ProjectScoped_DryRun_DoesNotRegister()
    {
        var adapter = MakeAdapter("codex", detected: true);
        _registry.Find("codex").Returns(adapter);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), "codex", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("codex", [new InstallEntry("hook", InstallStatus.Installed)]));

        await _service.InstallAsync(global: false, agentKey: "codex", dryRun: true);

        await _projectRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_Global_DoesNotRegister()
    {
        var adapter = MakeAdapter("claude", detected: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", [new InstallEntry("hook", InstallStatus.Installed)]));

        await _service.InstallAsync(global: true, agentKey: null, dryRun: false);

        await _projectRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ProjectScoped_AllErrors_DoesNotRegister()
    {
        var adapter = MakeAdapter("codex", detected: true);
        _registry.Find("codex").Returns(adapter);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), "codex", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("codex", [new InstallEntry("hook", InstallStatus.Error, "permission denied")]));

        await _service.InstallAsync(global: false, agentKey: "codex", dryRun: false);

        await _projectRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static IAgentHarnessAdapter MakeAdapter(string key, bool detected)
    {
        var adapter = Substitute.For<IAgentHarnessAdapter>();
        adapter.Key.Returns(key);
        adapter.IsDetected(Arg.Any<bool>(), Arg.Any<string?>()).Returns(detected);
        adapter.GetInstallPlan(Arg.Any<bool>(), Arg.Any<string?>()).Returns(new InstallPlan([]));
        return adapter;
    }
}
