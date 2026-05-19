using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
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
    private readonly IStorageProvisioner _provisioner = Substitute.For<IStorageProvisioner>();
    private readonly InitService _service;

    public InitServiceTests()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/repo/root");
        _provisioner.ProvisionAsync(Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _projectRegistry.RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _service = new InitService(_registry, _installer, _rootDetector, _projectRegistry, _provisioner);
    }

    [Fact]
    public async Task InstallAsync_Global_AdapterUnavailable_ReportsSkipped()
    {
        var adapter = MakeAdapter("claude", available: false);
        _registry.All.Returns([adapter]);

        var result = await _service.InstallAsync(InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false);

        var claudeReport = result.Reports.Single(r => r.HarnessKey == "claude");
        Assert.Equal(InstallStatus.Skipped, claudeReport.Entries[0].Status);
        await _installer.DidNotReceive().InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ExplicitAgent_InstallsEvenWhenNotDetected()
    {
        var adapter = MakeAdapter("claude", available: false);
        _registry.Find("claude").Returns(adapter);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        var result = await _service.InstallAsync(InitScope.Global, agentKey: "claude", projectRootOverride: null, dryRun: false);

        Assert.Contains(result.Reports, r => r.HarnessKey == "claude");
        await _installer.Received(1).InstallAsync(Arg.Any<InstallPlan>(), "claude", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_DetectedAdapter_CallsInstaller()
    {
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        var result = await _service.InstallAsync(InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false);

        Assert.Contains(result.Reports, r => r.HarnessKey == "claude");
        await _installer.Received(1).InstallAsync(Arg.Any<InstallPlan>(), "claude", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_UnknownExplicitAgent_ReturnsEmpty()
    {
        _registry.Find("nonexistent").Returns((IAgentHarnessAdapter?)null);

        var result = await _service.InstallAsync(InitScope.Global, agentKey: "nonexistent", projectRootOverride: null, dryRun: false);

        Assert.Empty(result.Reports);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task InstallAsync_Global_PassesNullProjectRoot()
    {
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        await _service.InstallAsync(InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false);

        adapter.Received(1).GetInstallPlan(global: true, projectRoot: null);
    }

    [Fact]
    public async Task InstallAsync_Project_PassesDetectedProjectRoot()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/detected/root");
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        var result = await _service.InstallAsync(InitScope.Project, agentKey: null, projectRootOverride: null, dryRun: false);

        Assert.Equal("/detected/root", result.ProjectRoot);
        adapter.Received(1).GetInstallPlan(global: false, projectRoot: "/detected/root");
    }

    [Fact]
    public async Task InstallAsync_Project_InstallsWhenHarnessAvailableButProjectNotConfigured()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/clean/repo");
        var adapter = MakeAdapter("codex", available: true, detected: false);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), "codex", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("codex", [new InstallEntry("hook", InstallStatus.Installed)]));

        var result = await _service.InstallAsync(InitScope.Project, agentKey: null, projectRootOverride: null, dryRun: false);

        Assert.Contains(result.Reports, r => r.HarnessKey == "codex");
        await _installer.Received(1).InstallAsync(Arg.Any<InstallPlan>(), "codex", false, Arg.Any<CancellationToken>());
        adapter.DidNotReceive().IsDetected(Arg.Any<bool>(), Arg.Any<string?>());
        await _projectRegistry.Received(1).RegisterAsync("/clean/repo", "codex", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_Project_SkipsWhenHarnessUnavailable()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/clean/repo");
        var adapter = MakeAdapter("codex", available: false);
        _registry.All.Returns([adapter]);

        var result = await _service.InstallAsync(InitScope.Project, agentKey: null, projectRootOverride: null, dryRun: false);

        var codexReport = result.Reports.Single(r => r.HarnessKey == "codex");
        Assert.Equal(InstallStatus.Skipped, codexReport.Entries[0].Status);
        await _installer.DidNotReceive().InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _projectRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_Project_WithExplicitRoot_UsesExplicitRoot()
    {
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        var result = await _service.InstallAsync(InitScope.Project, agentKey: null, projectRootOverride: "/explicit/root", dryRun: false);

        Assert.Equal(Path.GetFullPath("/explicit/root"), result.ProjectRoot);
        adapter.Received(1).GetInstallPlan(global: false, projectRoot: Path.GetFullPath("/explicit/root"));
    }

    [Fact]
    public async Task InstallAsync_Project_OutsideProject_ReturnsErrorAndDoesNotInstall()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns((string?)null);
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);

        var result = await _service.InstallAsync(InitScope.Project, agentKey: null, projectRootOverride: null, dryRun: false);

        Assert.NotNull(result.ErrorMessage);
        Assert.True(result.ProjectSkipped);
        Assert.Empty(result.Reports);
        await _installer.DidNotReceive().InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _projectRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_All_InsideProject_InstallsGlobalAndProject()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/detected/root");
        var adapter = MakeAdapter("codex", available: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), "codex", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("codex", [new InstallEntry("hook", InstallStatus.Installed)]));

        var result = await _service.InstallAsync(InitScope.All, agentKey: null, projectRootOverride: null, dryRun: false);

        Assert.Equal(3, result.Reports.Count); // storage + global codex + project codex
        Assert.False(result.ProjectSkipped);
        adapter.Received(1).GetInstallPlan(global: true, projectRoot: null);
        adapter.Received(1).GetInstallPlan(global: false, projectRoot: "/detected/root");
        await _projectRegistry.Received(1).RegisterAsync("/detected/root", "codex", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_All_OutsideProject_InstallsOnlyGlobal()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns((string?)null);
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        var result = await _service.InstallAsync(InitScope.All, agentKey: null, projectRootOverride: null, dryRun: false);

        Assert.Equal(2, result.Reports.Count); // storage + global claude
        Assert.True(result.ProjectSkipped);
        adapter.Received(1).GetInstallPlan(global: true, projectRoot: null);
        adapter.DidNotReceive().GetInstallPlan(global: false, projectRoot: Arg.Any<string?>());
    }

    [Fact]
    public async Task InstallAsync_DryRun_ForwardsDryRunToInstaller()
    {
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        await _service.InstallAsync(InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: true);

        await _installer.Received(1).InstallAsync(Arg.Any<InstallPlan>(), "claude", dryRun: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ProjectScoped_NonDryRun_WithInstalledEntries_RegistersProject()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/my/project");
        var adapter = MakeAdapter("codex", available: true);
        _registry.Find("codex").Returns(adapter);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), "codex", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("codex", [new InstallEntry("hook", InstallStatus.Installed)]));

        await _service.InstallAsync(InitScope.Project, agentKey: "codex", projectRootOverride: null, dryRun: false);

        await _projectRegistry.Received(1).RegisterAsync("/my/project", "codex", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ProjectScoped_DryRun_DoesNotRegister()
    {
        var adapter = MakeAdapter("codex", available: true);
        _registry.Find("codex").Returns(adapter);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), "codex", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("codex", [new InstallEntry("hook", InstallStatus.Installed)]));

        await _service.InstallAsync(InitScope.Project, agentKey: "codex", projectRootOverride: null, dryRun: true);

        await _projectRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_Global_DoesNotRegister()
    {
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", [new InstallEntry("hook", InstallStatus.Installed)]));

        await _service.InstallAsync(InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false);

        await _projectRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ProjectScoped_AllErrors_DoesNotRegister()
    {
        var adapter = MakeAdapter("codex", available: true);
        _registry.Find("codex").Returns(adapter);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), "codex", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("codex", [new InstallEntry("hook", InstallStatus.Error, "permission denied")]));

        await _service.InstallAsync(InitScope.Project, agentKey: "codex", projectRootOverride: null, dryRun: false);

        await _projectRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ProjectScoped_PartialError_DoesNotRegister()
    {
        var adapter = MakeAdapter("codex", available: true);
        _registry.Find("codex").Returns(adapter);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), "codex", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("codex", [
                new InstallEntry("hook", InstallStatus.Installed),
                new InstallEntry("rules", InstallStatus.Error, "permission denied"),
            ]));

        await _service.InstallAsync(InitScope.Project, agentKey: "codex", projectRootOverride: null, dryRun: false);

        await _projectRegistry.DidNotReceive().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitService_CallsStorageProvisioner_WhenNotDryRun()
    {
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        await _service.InstallAsync(InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false);

        await _provisioner.Received(1).ProvisionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitService_DryRun_SkipsStorageProvisioner()
    {
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", []));

        await _service.InstallAsync(InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: true);

        await _provisioner.DidNotReceive().ProvisionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitService_StorageProvisionFailure_ReturnsInstallError()
    {
        _provisioner.ProvisionAsync(Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Fail(new Error("storage.access_denied", "Permission denied")));
        var adapter = MakeAdapter("claude", available: true);
        _registry.All.Returns([adapter]);

        var result = await _service.InstallAsync(InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false);

        Assert.NotNull(result.ErrorMessage);
        Assert.Single(result.Reports);
        Assert.Equal("storage", result.Reports[0].HarnessKey);
        Assert.Equal(InstallStatus.Error, result.Reports[0].Entries[0].Status);
        await _installer.DidNotReceive().InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitService_ProjectRegistryFailure_AddsWarningAndDoesNotFailInstall()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/my/project");
        var adapter = MakeAdapter("codex", available: true);
        _registry.Find("codex").Returns(adapter);
        _installer.InstallAsync(Arg.Any<InstallPlan>(), "codex", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("codex", [new InstallEntry("hook", InstallStatus.Installed)]));
        _projectRegistry.RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Fail(new Error("storage.access_denied", "Permission denied")));

        var result = await _service.InstallAsync(InitScope.Project, agentKey: "codex", projectRootOverride: null, dryRun: false);

        Assert.Null(result.ErrorMessage);
        var report = result.Reports.Single(r => r.HarnessKey == "codex");
        Assert.Contains(report.Entries, e =>
            e.Description == "Project registration" &&
            e.Status == InstallStatus.Warning &&
            e.Detail == "Permission denied");
    }

    private static IAgentHarnessAdapter MakeAdapter(string key, bool available, bool detected = true)
    {
        var adapter = Substitute.For<IAgentHarnessAdapter>();
        adapter.Key.Returns(key);
        adapter.IsAvailable().Returns(available);
        adapter.IsDetected(Arg.Any<bool>(), Arg.Any<string?>()).Returns(detected);
        adapter.GetInstallPlan(Arg.Any<bool>(), Arg.Any<string?>()).Returns(new InstallPlan([]));
        return adapter;
    }
}
