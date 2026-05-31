using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Hooks;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

[Trait("Category", "InitImport")]
public sealed class InitImportIntegrationTests
{
    private readonly IHarnessRegistry _registry = Substitute.For<IHarnessRegistry>();
    private readonly IHookInstaller _installer = Substitute.For<IHookInstaller>();
    private readonly IProjectRootDetector _rootDetector = Substitute.For<IProjectRootDetector>();
    private readonly IProjectRegistry _projectRegistry = Substitute.For<IProjectRegistry>();
    private readonly IStorageProvisioner _provisioner = Substitute.For<IStorageProvisioner>();
    private readonly IMcpServerImportService _importService = Substitute.For<IMcpServerImportService>();

    private InitService Sut(IMcpServerImportService? importService = null) =>
        new(_registry, _installer, _rootDetector, _projectRegistry, _provisioner, importService);

    private IAgentHarnessAdapter MakeAdapter(string key, bool available = true)
    {
        var adapter = Substitute.For<IAgentHarnessAdapter>();
        adapter.Key.Returns(key);
        adapter.IsAvailable().Returns(available);
        adapter.GetInstallPlan(Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new InstallPlan([]));
        return adapter;
    }

    public InitImportIntegrationTests()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/repo/root");
        _provisioner.ProvisionAsync(Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _projectRegistry.RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _installer.InstallAsync(Arg.Any<InstallPlan>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new InstallReport("claude", [new InstallEntry("Installed", InstallStatus.Installed)]));
        _importService.ImportAsync(Arg.Any<McpImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<McpImportReport, Error>.Ok(new McpImportReport([], 0, 0, 0, 0)));
    }

    [Fact]
    public async Task InstallAsync_SuccessfulInstall_CallsImportService()
    {
        var adapter = MakeAdapter("claude");
        _registry.All.Returns([adapter]);

        await Sut(_importService).InstallAsync(
            InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false);

        await _importService.Received(1).ImportAsync(
            Arg.Is<McpImportRequest>(r =>
                r.Scope == McpImportScope.Global &&
                r.Replace == false &&
                r.DryRun == false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_ImportFails_HarnessInstallStillSucceeds_NoErrorMessage()
    {
        var adapter = MakeAdapter("claude");
        _registry.All.Returns([adapter]);
        _importService.ImportAsync(Arg.Any<McpImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<McpImportReport, Error>.Fail(new Error("ImportError", "import boom")));

        var result = await Sut(_importService).InstallAsync(
            InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false);

        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task InstallAsync_DryRun_PassesDryRunToImportService()
    {
        var adapter = MakeAdapter("claude");
        _registry.All.Returns([adapter]);

        await Sut(_importService).InstallAsync(
            InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: true);

        await _importService.Received(1).ImportAsync(
            Arg.Is<McpImportRequest>(r => r.DryRun == true),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_SkipMcpImport_DoesNotCallImportService()
    {
        var adapter = MakeAdapter("claude");
        _registry.All.Returns([adapter]);

        await Sut(_importService).InstallAsync(
            InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false,
            skipMcpImport: true);

        await _importService.DidNotReceive().ImportAsync(
            Arg.Any<McpImportRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_NoImportService_DoesNotThrow()
    {
        var adapter = MakeAdapter("claude");
        _registry.All.Returns([adapter]);

        var result = await Sut(importService: null).InstallAsync(
            InitScope.Global, agentKey: null, projectRootOverride: null, dryRun: false);

        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task InstallAsync_ProjectScope_PassesProjectRootToImportService()
    {
        var adapter = MakeAdapter("claude");
        _registry.Find("claude").Returns(adapter);

        await Sut(_importService).InstallAsync(
            InitScope.Project, agentKey: "claude", projectRootOverride: "/my/repo", dryRun: false);

        await _importService.Received(1).ImportAsync(
            Arg.Is<McpImportRequest>(r =>
                r.Scope == McpImportScope.Project &&
                r.ProjectRoot == "/my/repo"),
            Arg.Any<CancellationToken>());
    }
}
