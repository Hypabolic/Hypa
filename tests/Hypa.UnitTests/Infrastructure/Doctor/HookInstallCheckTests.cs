using Hypa.Infrastructure.Doctor;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Doctor;

[Collection("SequentialEnvTests")]
public sealed class HookInstallCheckTests
{
    private readonly IHarnessRegistry _registry = Substitute.For<IHarnessRegistry>();
    private readonly IProjectRootDetector _rootDetector = Substitute.For<IProjectRootDetector>();
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();

    public HookInstallCheckTests()
    {
        _rootDetector.Detect(Arg.Any<string>()).Returns((string?)null);
        _fileSystem.GetCurrentDirectory().Returns("/fake/project");
        _fileSystem.FileExists(Arg.Any<string>()).Returns(false);
    }

    [Fact]
    public void Run_NoAdapters_ReturnsFail()
    {
        _registry.All.Returns([]);
        var check = new HookInstallCheck(_registry, _rootDetector, _fileSystem);

        var result = check.Run();

        Assert.Equal(DoctorStatus.Fail, result.Status);
    }

    [Fact]
    public void Run_GlobalScopedAdapterNotInstalled_IncludesGlobalHint()
    {
        var adapter = MakeAdapter("claude", globalSupported: true);
        _registry.All.Returns([adapter]);
        var check = new HookInstallCheck(_registry, _rootDetector, _fileSystem);

        var result = check.Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains("--global", result.Detail);
        Assert.Contains("claude", result.Detail);
    }

    [Fact]
    public void Run_ProjectScopedAdapterNotInstalled_OmitsGlobalFlag()
    {
        var adapter = MakeAdapter("project-agent", globalSupported: false);
        _registry.All.Returns([adapter]);
        var check = new HookInstallCheck(_registry, _rootDetector, _fileSystem);

        var result = check.Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.DoesNotContain("--global", result.Detail);
        Assert.Contains("project-agent", result.Detail);
    }

    [Fact]
    public void Run_MixedAdapters_IncludesBothHints()
    {
        var claude = MakeAdapter("claude", globalSupported: true);
        var codex = MakeAdapter("codex", globalSupported: true);
        _registry.All.Returns([claude, codex]);
        var check = new HookInstallCheck(_registry, _rootDetector, _fileSystem);

        var result = check.Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains("claude", result.Detail);
        Assert.Contains("codex", result.Detail);
        Assert.Contains("--global", result.Detail);
    }

    [Fact]
    public void Run_ClaudeInstalled_ReturnsOk()
    {
        var adapter = MakeAdapter("claude", globalSupported: true);
        _registry.All.Returns([adapter]);
        _fileSystem.FileExists(Arg.Any<string>()).Returns(true);
        _fileSystem.ReadAllText(Arg.Any<string>()).Returns("""{"hooks":{"PreToolUse":[{"matcher":".*","hooks":[{"type":"command","command":"hypa hook"}]}]}}""");
        var check = new HookInstallCheck(_registry, _rootDetector, _fileSystem);

        var result = check.Run();

        Assert.Equal(DoctorStatus.Ok, result.Status);
    }

    [Fact]
    public void Run_CodexGlobalInstalledWithCanonicalHooksFlag_ReturnsOk()
    {
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex-home";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            var adapter = MakeAdapter("codex", globalSupported: true);
            _registry.All.Returns([adapter]);
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns("""{"hooks":{"PreToolUse":[{"matcher":"^(Bash|bash|Shell|shell|command|exec_command|functions\\.exec_command)$","hooks":[{"type":"command","command":"hypa hook --agent codex","timeout":30}]}]}}""");
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[features]\nhooks = true\n\n[mcp_servers.hypa]\ncommand = \"hypa\"\nargs = [\"serve\"]\n");
            var check = new HookInstallCheck(_registry, _rootDetector, _fileSystem);

            var result = check.Run();

            Assert.Equal(DoctorStatus.Ok, result.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    [Fact]
    public void Run_CodexLegacyAliasOnly_ReturnsWarn()
    {
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex-home";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            var adapter = MakeAdapter("codex", globalSupported: true);
            _registry.All.Returns([adapter]);
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns("""{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"command":"hypa hook --agent codex"}]}]}}""");
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[features]\ncodex_hooks = true\n");
            var check = new HookInstallCheck(_registry, _rootDetector, _fileSystem);

            var result = check.Run();

            Assert.Equal(DoctorStatus.Warn, result.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    private static IAgentHarnessAdapter MakeAdapter(string key, bool globalSupported)
    {
        var adapter = Substitute.For<IAgentHarnessAdapter>();
        adapter.Key.Returns(key);

        var globalPlan = globalSupported
            ? new InstallPlan([new InstallOperation.PatchJsonHook("/fake/path", "PreToolUse", "{}")])
            : new InstallPlan([new InstallOperation.NotSupported("project-scoped only")]);

        adapter.GetInstallPlan(Arg.Is(true), Arg.Any<bool>(), Arg.Any<string?>()).Returns(globalPlan);
        adapter.GetInstallPlan(Arg.Is(false), Arg.Any<bool>(), Arg.Any<string?>()).Returns(new InstallPlan([]));

        return adapter;
    }
}
