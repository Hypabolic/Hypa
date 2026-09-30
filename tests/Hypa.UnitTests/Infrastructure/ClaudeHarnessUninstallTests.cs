using System.CommandLine;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.Cli.Commands;
using Hypa.Infrastructure.Hooks;
using Hypa.Infrastructure.Hooks.Adapters;
using Hypa.Infrastructure.Skills;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Projects;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

public sealed class ClaudeHarnessUninstallTests : IDisposable
{
    private readonly string _root;

    public ClaudeHarnessUninstallTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-claude-harness-uninstall-" + Guid.NewGuid().ToString("N"));
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

    [Fact(DisplayName = "hypa uninstall --agent claude removes the compression skill and the runtime skill")]
    public async Task Hypa_uninstall_agent_claude_removes_compression_skill_and_runtime_skill()
    {
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        var previousClaude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var previousCodex = Environment.GetEnvironmentVariable("CODEX_HOME");
        var previousIn = Console.In;
        var previousOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("HOME", _root);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            Environment.SetEnvironmentVariable("CODEX_HOME", null);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(_root, home);

            var compression = Path.Combine(home, ".claude", "skills", "hypa", "SKILL.md");
            var runtimeDir = Path.Combine(home, ".claude", "skills", "hypa-runtime");
            var runtime = Path.Combine(runtimeDir, "SKILL.md");
            var userFile = Path.Combine(home, ".claude", "keep.txt");
            var codexRuntime = Path.Combine(home, ".codex", "skills", "hypa-runtime", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(compression)!);
            Directory.CreateDirectory(runtimeDir);
            Directory.CreateDirectory(Path.GetDirectoryName(codexRuntime)!);
            File.WriteAllText(compression, "compression\n");
            File.WriteAllBytes(runtime, "runtime-skill\n"u8.ToArray());
            File.WriteAllText(userFile, "user\n");
            File.WriteAllText(codexRuntime, "codex-runtime\n");

            var projects = Substitute.For<IProjectRegistry>();
            projects.GetByAgentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ProjectRegistration>>([]));
            projects.UnregisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Result<Unit, Error>.Ok(Unit.Value));
            var roots = Substitute.For<IProjectRootDetector>();
            roots.Detect(Arg.Any<string>()).Returns((string?)null);

            var service = new UninstallService(
                new HarnessRegistry([new ClaudeCodeAdapter(new SkillRenderer())]),
                new HookUninstaller(),
                Substitute.For<IBinaryRemover>(),
                roots,
                projects);
            var root = new RootCommand("hypa");
            root.Add(new UninstallCommand(service, OfficialIntegrationService.CreateSystem()).Build());

            using var output = new StringWriter();
            Console.SetIn(new StringReader("y\n"));
            Console.SetOut(output);
            var exit = await root.Parse(["uninstall", "--agent", "claude"]).InvokeAsync();

            Assert.True(exit == 0, output.ToString());
            Assert.False(Directory.Exists(Path.GetDirectoryName(compression)));
            Assert.False(File.Exists(runtime));
            Assert.Equal("user\n", File.ReadAllText(userFile));
            Assert.Equal("codex-runtime\n", File.ReadAllText(codexRuntime));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousClaude);
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodex);
            Console.SetIn(previousIn);
            Console.SetOut(previousOut);
        }
    }

    [Fact]
    public async Task Hypa_uninstall_removes_runtime_skill_and_compression_skill()
    {
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        var saved = SaveAgentEnv();
        var previousIn = Console.In;
        var previousOut = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("HOME", _root);
            ClearAgentEnv();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(_root, home);

            var compression = Path.Combine(home, ".claude", "skills", "hypa", "SKILL.md");
            var runtime = Path.Combine(home, ".claude", "skills", "hypa-runtime", "SKILL.md");
            var userFile = Path.Combine(home, ".claude", "keep.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(compression)!);
            Directory.CreateDirectory(Path.GetDirectoryName(runtime)!);
            File.WriteAllText(compression, "compression\n");
            File.WriteAllText(runtime, "runtime-skill\n");
            File.WriteAllText(userFile, "user\n");

            var projects = Substitute.For<IProjectRegistry>();
            projects.GetByAgentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ProjectRegistration>>([]));
            projects.UnregisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Result<Unit, Error>.Ok(Unit.Value));
            var roots = Substitute.For<IProjectRootDetector>();
            roots.Detect(Arg.Any<string>()).Returns((string?)null);
            var binary = Substitute.For<IBinaryRemover>();
            binary.RemoveAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new BinaryRemoveResult(true));

            var service = new UninstallService(
                new HarnessRegistry([new ClaudeCodeAdapter(new SkillRenderer())]),
                new HookUninstaller(),
                binary,
                roots,
                projects);
            var root = new RootCommand("hypa");
            root.Add(new UninstallCommand(service, OfficialIntegrationService.CreateSystem()).Build());

            using var output = new StringWriter();
            Console.SetOut(output);
            var exit = await root.Parse(["uninstall", "--yes"]).InvokeAsync();

            Assert.True(exit == 0, output.ToString());
            Assert.False(File.Exists(compression));
            Assert.False(File.Exists(runtime));
            Assert.Equal("user\n", File.ReadAllText(userFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            RestoreAgentEnv(saved);
            Console.SetIn(previousIn);
            Console.SetOut(previousOut);
        }
    }

    private static readonly string[] AgentEnv =
    [
        "CLAUDE_CONFIG_DIR",
        "CODEX_HOME",
        "PI_CODING_AGENT_DIR",
        "PI_CONFIG_DIR",
        "KIMI_CODE_HOME",
        "COPILOT_HOME",
        "QODER_CONFIG_DIR",
        "QWEN_HOME",
        "CURSOR_CONFIG_DIR",
        "ANTIGRAVITY_CLI_CONFIG_DIR",
        "KILO_CONFIG_DIR",
        "GROK_CONFIG_DIR",
        "GROK_HOME",
        "HERMES_HOME",
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
}
