using Hypa.Infrastructure.Doctor;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Doctor;

public sealed class HookInstallCheckTests
{
    private readonly IHarnessRegistry _registry = Substitute.For<IHarnessRegistry>();
    private readonly IProjectRootDetector _rootDetector = Substitute.For<IProjectRootDetector>();

    public HookInstallCheckTests()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns((string?)null);
    }

    [Fact]
    public void Run_NoAdapters_ReturnsFail()
    {
        _registry.All.Returns([]);
        var check = new HookInstallCheck(_registry, _rootDetector);

        var result = check.Run();

        Assert.Equal(DoctorStatus.Fail, result.Status);
    }

    [Fact]
    public void Run_GlobalScopedAdapterNotInstalled_IncludesGlobalHint()
    {
        var adapter = MakeAdapter("claude", globalSupported: true);
        _registry.All.Returns([adapter]);
        var check = new HookInstallCheck(_registry, _rootDetector);

        var result = check.Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains("--global", result.Detail);
        Assert.Contains("claude", result.Detail);
    }

    [Fact]
    public void Run_ProjectScopedAdapterNotInstalled_OmitsGlobalFlag()
    {
        var adapter = MakeAdapter("codex", globalSupported: false);
        _registry.All.Returns([adapter]);
        var check = new HookInstallCheck(_registry, _rootDetector);

        var result = check.Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.DoesNotContain("--global", result.Detail);
        Assert.Contains("codex", result.Detail);
    }

    [Fact]
    public void Run_MixedAdapters_IncludesBothHints()
    {
        var claude = MakeAdapter("claude", globalSupported: true);
        var codex = MakeAdapter("codex", globalSupported: false);
        _registry.All.Returns([claude, codex]);
        var check = new HookInstallCheck(_registry, _rootDetector);

        var result = check.Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains("claude", result.Detail);
        Assert.Contains("codex", result.Detail);
        Assert.Contains("--global", result.Detail);
    }

    private static IAgentHarnessAdapter MakeAdapter(string key, bool globalSupported)
    {
        var adapter = Substitute.For<IAgentHarnessAdapter>();
        adapter.Key.Returns(key);

        var globalPlan = globalSupported
            ? new InstallPlan([new InstallOperation.PatchJsonHook("/fake/path", "PreToolUse", "{}")])
            : new InstallPlan([new InstallOperation.NotSupported("project-scoped only")]);

        adapter.GetInstallPlan(global: true, projectRoot: Arg.Any<string?>()).Returns(globalPlan);
        adapter.GetInstallPlan(global: false, projectRoot: Arg.Any<string?>()).Returns(new InstallPlan([]));

        return adapter;
    }
}
