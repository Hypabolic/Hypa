using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Hooks;
using Hypa.Runtime.Domain.Projects;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class UninstallServiceTests
{
    private readonly IHarnessRegistry _registry = Substitute.For<IHarnessRegistry>();
    private readonly IHookUninstaller _uninstaller = Substitute.For<IHookUninstaller>();
    private readonly IBinaryRemover _binaryRemover = Substitute.For<IBinaryRemover>();
    private readonly IProjectRootDetector _rootDetector = Substitute.For<IProjectRootDetector>();
    private readonly IProjectRegistry _projectRegistry = Substitute.For<IProjectRegistry>();
    private readonly UninstallService _service;

    public UninstallServiceTests()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns((string?)null);
        _projectRegistry.GetByAgentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _projectRegistry.UnregisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Unit, Error>.Ok(Unit.Value));
        _service = new UninstallService(_registry, _uninstaller, _binaryRemover, _rootDetector, _projectRegistry);
    }

    [Fact]
    public async Task UninstallHarnessesAsync_UnknownAgent_ReturnsNull()
    {
        _registry.Find("ghost").Returns((IAgentHarnessAdapter?)null);

        var result = await _service.UninstallHarnessesAsync(global: true, agentKey: "ghost", dryRun: false);

        Assert.Null(result);
        await _uninstaller.DidNotReceive().UninstallAsync(Arg.Any<UninstallPlan>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task UninstallHarnessesAsync_KnownAgent_CallsUninstallerOnce()
    {
        var adapter = MakeAdapter("claude");
        _registry.Find("claude").Returns(adapter);
        _uninstaller.UninstallAsync(Arg.Any<UninstallPlan>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(new UninstallReport("claude", []));

        var result = await _service.UninstallHarnessesAsync(global: true, agentKey: "claude", dryRun: false);

        Assert.NotNull(result);
        Assert.Single(result);
        await _uninstaller.Received(1).UninstallAsync(Arg.Any<UninstallPlan>(), "claude", false);
    }

    [Fact]
    public async Task UninstallHarnessesAsync_NoAgentKey_RunsAllAdapters()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/repo/root");
        var claude = MakeAdapter("claude");
        var codex = MakeAdapter("codex");
        _registry.All.Returns([claude, codex]);
        _uninstaller.UninstallAsync(Arg.Any<UninstallPlan>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(ci => new UninstallReport((string)ci[1], []));

        var result = await _service.UninstallHarnessesAsync(global: false, agentKey: null, dryRun: false);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
    }

    [Fact]
    public async Task UninstallHarnessesAsync_ProjectScopedOutsideProject_ReturnsErrorsAndDoesNotCallAdapters()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns((string?)null);
        var adapter = MakeAdapter("codex");
        _registry.Find("codex").Returns(adapter);

        var result = await _service.UninstallHarnessesAsync(global: false, agentKey: "codex", dryRun: false);

        Assert.NotNull(result);
        var report = Assert.Single(result!);
        Assert.Equal("codex", report.HarnessKey);
        Assert.Equal(UninstallStatus.Error, report.Entries[0].Status);
        Assert.Contains("No project root detected", report.Entries[0].Detail);
        adapter.DidNotReceive().GetUninstallPlan(global: false, projectRoot: Arg.Any<string?>());
        await _uninstaller.DidNotReceive().UninstallAsync(Arg.Any<UninstallPlan>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task UninstallHarnessesAsync_GlobalTrue_MergesGlobalAndProjectPlans()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns("/repo/root");
        var globalOp = new UninstallOperation.DeleteFile("/global/file");
        var projectOp = new UninstallOperation.DeleteFile("/project/file");

        var adapter = Substitute.For<IAgentHarnessAdapter>();
        adapter.Key.Returns("test");
        adapter.GetUninstallPlan(global: true, Arg.Any<string?>()).Returns(new UninstallPlan([globalOp]));
        adapter.GetUninstallPlan(global: false, Arg.Any<string?>()).Returns(new UninstallPlan([projectOp]));

        _registry.Find("test").Returns(adapter);
        _uninstaller.UninstallAsync(Arg.Any<UninstallPlan>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(new UninstallReport("test", []));

        await _service.UninstallHarnessesAsync(global: true, agentKey: "test", dryRun: false);

        await _uninstaller.Received(1).UninstallAsync(
            Arg.Is<UninstallPlan>(p => p.Operations.Count == 2),
            "test", false);
    }

    [Fact]
    public async Task UninstallHarnessesAsync_GlobalTrue_WithNoProjectRoots_DoesNotRequestProjectPlan()
    {
        var globalOp = new UninstallOperation.DeleteFile("/global/file");

        var adapter = Substitute.For<IAgentHarnessAdapter>();
        adapter.Key.Returns("test");
        adapter.GetUninstallPlan(global: true, Arg.Any<string?>()).Returns(new UninstallPlan([globalOp]));

        _registry.Find("test").Returns(adapter);
        _uninstaller.UninstallAsync(Arg.Any<UninstallPlan>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(new UninstallReport("test", []));

        await _service.UninstallHarnessesAsync(global: true, agentKey: "test", dryRun: false);

        adapter.DidNotReceive().GetUninstallPlan(global: false, projectRoot: Arg.Any<string?>());
        await _uninstaller.Received(1).UninstallAsync(
            Arg.Is<UninstallPlan>(p => p.Operations.Count == 1 && p.Operations[0] == globalOp),
            "test", false);
    }

    [Fact]
    public async Task UninstallHarnessesAsync_GlobalTrue_AllNotSupported_FallsBackToGlobalPlan()
    {
        var notSupported = new UninstallOperation.NotSupported("remove manually");

        var adapter = Substitute.For<IAgentHarnessAdapter>();
        adapter.Key.Returns("copilot");
        adapter.GetUninstallPlan(global: true, Arg.Any<string?>()).Returns(new UninstallPlan([notSupported]));
        adapter.GetUninstallPlan(global: false, Arg.Any<string?>()).Returns(new UninstallPlan([notSupported]));

        _registry.Find("copilot").Returns(adapter);
        _uninstaller.UninstallAsync(Arg.Any<UninstallPlan>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(new UninstallReport("copilot", []));

        await _service.UninstallHarnessesAsync(global: true, agentKey: "copilot", dryRun: false);

        await _uninstaller.Received(1).UninstallAsync(
            Arg.Is<UninstallPlan>(p => p.Operations.Count == 1 && p.Operations[0] is UninstallOperation.NotSupported),
            "copilot", false);
    }

    [Fact]
    public async Task UninstallHarnessesAsync_GlobalTrue_NonDryRun_UnregistersAllProjects()
    {
        var adapter = MakeAdapter("codex");
        _registry.Find("codex").Returns(adapter);
        _projectRegistry.GetByAgentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([
            new ProjectRegistration("/repo/a", "codex", DateTimeOffset.UtcNow),
            new ProjectRegistration("/repo/b", "codex", DateTimeOffset.UtcNow),
        ]);
        _uninstaller.UninstallAsync(Arg.Any<UninstallPlan>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(new UninstallReport("codex", []));

        await _service.UninstallHarnessesAsync(global: true, agentKey: "codex", dryRun: false);

        await _projectRegistry.Received(1).UnregisterAsync("/repo/a", "codex", Arg.Any<CancellationToken>());
        await _projectRegistry.Received(1).UnregisterAsync("/repo/b", "codex", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UninstallHarnessesAsync_GlobalTrue_WithErrors_DoesNotUnregister()
    {
        var adapter = MakeAdapter("codex");
        _registry.Find("codex").Returns(adapter);
        _projectRegistry.GetByAgentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([
            new ProjectRegistration("/repo/a", "codex", DateTimeOffset.UtcNow),
        ]);
        _uninstaller.UninstallAsync(Arg.Any<UninstallPlan>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(new UninstallReport("codex", [new UninstallEntry("hook", UninstallStatus.Error, "permission denied")]));

        await _service.UninstallHarnessesAsync(global: true, agentKey: "codex", dryRun: false);

        await _projectRegistry.DidNotReceive().UnregisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UninstallHarnessesAsync_GlobalTrue_DryRun_DoesNotUnregister()
    {
        var adapter = MakeAdapter("codex");
        _registry.Find("codex").Returns(adapter);
        _projectRegistry.GetByAgentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([
            new ProjectRegistration("/repo/a", "codex", DateTimeOffset.UtcNow),
        ]);
        _uninstaller.UninstallAsync(Arg.Any<UninstallPlan>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(new UninstallReport("codex", []));

        await _service.UninstallHarnessesAsync(global: true, agentKey: "codex", dryRun: true);

        await _projectRegistry.DidNotReceive().UnregisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeDataAsync_DirectoryMissing_ReturnsFalse()
    {
        var (removed, error) = await _service.PurgeDataAsync(dryRun: true);

        Assert.Null(error);
        _ = removed;
    }

    [Fact]
    public async Task RemoveBinaryAsync_DelegatesTo_IBinaryRemover()
    {
        _binaryRemover.RemoveAsync(true, Arg.Any<CancellationToken>())
            .Returns(new BinaryRemoveResult(false, "not found"));

        var result = await _service.RemoveBinaryAsync(dryRun: true);

        Assert.False(result.Removed);
        Assert.Equal("not found", result.Detail);
    }

    private static IAgentHarnessAdapter MakeAdapter(string key)
    {
        var adapter = Substitute.For<IAgentHarnessAdapter>();
        adapter.Key.Returns(key);
        adapter.GetUninstallPlan(Arg.Any<bool>(), Arg.Any<string?>())
            .Returns(new UninstallPlan([]));
        return adapter;
    }
}
