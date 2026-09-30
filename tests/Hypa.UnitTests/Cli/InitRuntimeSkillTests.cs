using System.CommandLine;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Domain;
using Hypa.Cli.Commands;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Hooks;
using RuntimeUnit = Hypa.Runtime.Domain.Common.Unit;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class InitRuntimeSkillTests : IDisposable
{
    private readonly string _root;

    public InitRuntimeSkillTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-init-runtime-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // teardown
        }
    }

    [Fact]
    public async Task Init_global_writes_runtime_skill_for_each_detected_target_without_a_mux()
    {
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var saved = SaveAgentEnv();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        try
        {
            var bin = Path.Combine(_root, "bin");
            Directory.CreateDirectory(bin);
            WriteCommand(Path.Combine(bin, "claude"));
            WriteCommand(Path.Combine(bin, "codex"));
            Environment.SetEnvironmentVariable("HOME", _root);
            Environment.SetEnvironmentVariable("PATH", bin);
            ClearAgentEnv();

            var adapter = Substitute.For<IAgentHarnessAdapter>();
            adapter.Key.Returns("claude");
            adapter.IsAvailable().Returns(false);
            var registry = Substitute.For<IHarnessRegistry>();
            registry.All.Returns([adapter]);
            var provisioner = Substitute.For<IStorageProvisioner>();
            provisioner.ProvisionAsync(Arg.Any<CancellationToken>())
                .Returns(Hypa.Runtime.Domain.Common.Result<RuntimeUnit, Hypa.Runtime.Domain.Common.Error>.Ok(RuntimeUnit.Value));
            var roots = Substitute.For<IProjectRootDetector>();
            roots.Detect(Arg.Any<string>()).Returns((string?)null);
            var init = new InitService(
                registry,
                Substitute.For<IHookInstaller>(),
                roots,
                Substitute.For<IProjectRegistry>(),
                provisioner);
            var command = new InitCommand(
                init,
                new HypaDataOptions { DataDirectory = Path.Combine(_root, ".hypa") },
                OfficialIntegrationService.CreateSystem());
            var root = new RootCommand("hypa");
            root.Add(command.Build());

            using var output = new StringWriter();
            using var error = new StringWriter();
            Console.SetOut(output);
            Console.SetError(error);
            var exit = await root.Parse(["init", "--global"]).InvokeAsync();

            Assert.True(exit == 0, output + error.ToString());
            var env = new ProbeEnvironment(_root);
            var claude = OfficialIntegrationLayout.RuntimeSkillFile(env, OfficialIntegrationTarget.Claude);
            var codex = OfficialIntegrationLayout.RuntimeSkillFile(env, OfficialIntegrationTarget.Codex);
            var pi = OfficialIntegrationLayout.RuntimeSkillFile(env, OfficialIntegrationTarget.Pi);
            Assert.NotNull(claude);
            Assert.NotNull(codex);
            Assert.NotNull(pi);
            Assert.Equal(RuntimeSkillText.Read(), File.ReadAllText(claude));
            Assert.Equal(RuntimeSkillText.Read(), File.ReadAllText(codex));
            Assert.False(File.Exists(pi));
            Assert.Contains("[runtime skill]", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            RestoreAgentEnv(saved);
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }
    }

    private static void WriteCommand(string path)
    {
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static readonly string[] AgentEnv =
    [
        "CLAUDE_CONFIG_DIR",
        "CODEX_HOME",
        "PI_CODING_AGENT_DIR",
        "PI_CONFIG_DIR",
        "XDG_CONFIG_HOME",
    ];

    private static Dictionary<string, string?> SaveAgentEnv()
    {
        var saved = new Dictionary<string, string?>(AgentEnv.Length, StringComparer.Ordinal);
        foreach (var name in AgentEnv)
            saved[name] = Environment.GetEnvironmentVariable(name);
        return saved;
    }

    private static void ClearAgentEnv()
    {
        foreach (var name in AgentEnv)
            Environment.SetEnvironmentVariable(name, null);
    }

    private static void RestoreAgentEnv(Dictionary<string, string?> saved)
    {
        foreach (var pair in saved)
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
    }

    private sealed class ProbeEnvironment(string home) : IIntegrationEnvironment
    {
        public string? UserHome => home;

        public string? PathVariable => null;

        public string? GetVariable(string name) => null;

        public bool FileIsExecutable(string path) => File.Exists(path);
    }
}
