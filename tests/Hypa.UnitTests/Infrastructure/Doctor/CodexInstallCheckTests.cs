using Hypa.Infrastructure.Doctor;
using Hypa.Runtime.Application.Ports;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Doctor;

[Collection("SequentialEnvTests")]
public sealed class CodexInstallCheckTests : IDisposable
{
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();
    private readonly List<string> _tempFiles = [];

    public CodexInstallCheckTests()
    {
        _fileSystem.FileExists(Arg.Any<string>()).Returns(false);
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
            try { File.Delete(path); } catch { /* best-effort */ }
    }

    [Fact]
    public void Category_IsCodex()
    {
        Assert.Equal("Codex", new CodexInstallCheck(_fileSystem).Category);
    }

    [Fact]
    public void Run_NeitherFilePresent_ReturnsOk()
    {
        var result = new CodexInstallCheck(_fileSystem).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
    }

    // ── MCP absent, no state file (hook mode) ────────────────────────────────

    [Fact]
    public void Run_NoStateFile_MissingMcpServer_ReturnsOkWithHooksRegistered()
    {
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns(BroadMatcherHooksJson());
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[features]\nhooks = true\n");

            var result = new CodexInstallCheck(_fileSystem).Run();

            Assert.Equal(DoctorStatus.Ok, result.Status);
            Assert.Contains("hooks registered", result.Value, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    [Fact]
    public void Run_AllConfigured_NoStateFile_ReturnsOkHooksRegistered()
    {
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns(BroadMatcherHooksJson());
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[features]\nhooks = true\n\n[mcp_servers.hypa]\ncommand = \"hypa\"\nargs = [\"serve\"]\n");

            var result = new CodexInstallCheck(_fileSystem).Run();

            Assert.Equal(DoctorStatus.Ok, result.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    // ── MCP state-aware: initWithMcp = true ──────────────────────────────────

    [Fact]
    public void Run_InitWithMcp_MissingMcpServer_ReturnsWarn()
    {
        var stateFile = WriteTempState("{\"init_with_mcp\":true}");
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns(BroadMatcherHooksJson());
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[features]\nhooks = true\n");

            var result = new CodexInstallCheck(_fileSystem, stateFile).Run();

            Assert.Equal(DoctorStatus.Warn, result.Status);
            var detail = result.Detail ?? result.Value;
            Assert.Contains("MCP", detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--with-mcp", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    [Fact]
    public void Run_InitWithMcp_AllConfigured_ReturnsOkHooksAndMcpRegistered()
    {
        var stateFile = WriteTempState("{\"init_with_mcp\":true}");
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns(BroadMatcherHooksJson());
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[features]\nhooks = true\n\n[mcp_servers.hypa]\ncommand = \"hypa\"\nargs = [\"serve\"]\n");

            var result = new CodexInstallCheck(_fileSystem, stateFile).Run();

            Assert.Equal(DoctorStatus.Ok, result.Status);
            Assert.Contains("MCP", result.Value, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    // ── Existing hook/config checks (unaffected by MCP state) ────────────────

    [Fact]
    public void Run_MissingBroadMatcher_ReturnsWarn()
    {
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns("""{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"command":"hypa hook --agent codex"}]}]}}""");
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[features]\nhooks = true\n\n[mcp_servers.hypa]\ncommand = \"hypa\"\nargs = [\"serve\"]\n");

            var result = new CodexInstallCheck(_fileSystem).Run();

            Assert.Equal(DoctorStatus.Warn, result.Status);
            var detail = result.Detail ?? result.Value;
            Assert.Contains("matcher", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    [Fact]
    public void Run_HooksJsonMissingHypaHook_ReturnsWarn()
    {
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns("""{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"command":"some-other-tool"}]}]}}""");
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[features]\nhooks = true\n\n[mcp_servers.hypa]\ncommand = \"hypa\"\nargs = [\"serve\"]\n");

            var result = new CodexInstallCheck(_fileSystem).Run();

            Assert.Equal(DoctorStatus.Warn, result.Status);
            var detail = result.Detail ?? result.Value;
            Assert.Contains("hook", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    [Fact]
    public void Run_ConfigMissingHooksFeature_ReturnsWarn()
    {
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns(BroadMatcherHooksJson());
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[mcp_servers.hypa]\ncommand = \"hypa\"\nargs = [\"serve\"]\n");

            var result = new CodexInstallCheck(_fileSystem).Run();

            Assert.Equal(DoctorStatus.Warn, result.Status);
            var detail = result.Detail ?? result.Value;
            Assert.Contains("hooks", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    [Fact]
    public void Run_McpServeArgInWrongSection_InitWithMcp_ReturnsWarn()
    {
        var stateFile = WriteTempState("{\"init_with_mcp\":true}");
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns(BroadMatcherHooksJson());
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            // "serve" appears in an unrelated section; [mcp_servers.hypa] has only command
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[features]\nhooks = true\n\n[other]\nargs = [\"serve\"]\n\n[mcp_servers.hypa]\ncommand = \"hypa\"\n");

            var result = new CodexInstallCheck(_fileSystem, stateFile).Run();

            Assert.Equal(DoctorStatus.Warn, result.Status);
            var detail = result.Detail ?? result.Value;
            Assert.Contains("MCP", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    [Fact]
    public void Run_HooksAbsentConfigPresent_ReturnsWarn()
    {
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            _fileSystem.FileExists(Path.Combine(codexHome, "config.toml")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "config.toml"))
                .Returns("[features]\nhooks = true\n\n[mcp_servers.hypa]\ncommand = \"hypa\"\nargs = [\"serve\"]\n");

            var result = new CodexInstallCheck(_fileSystem).Run();

            Assert.Equal(DoctorStatus.Warn, result.Status);
            Assert.Contains("hooks.json", result.Detail ?? result.Value, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    [Fact]
    public void Run_HooksPresentConfigAbsent_ReturnsWarn()
    {
        var oldCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var codexHome = "/fake/codex";
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        try
        {
            _fileSystem.FileExists(Path.Combine(codexHome, "hooks.json")).Returns(true);
            _fileSystem.ReadAllText(Path.Combine(codexHome, "hooks.json"))
                .Returns(BroadMatcherHooksJson());

            var result = new CodexInstallCheck(_fileSystem).Run();

            Assert.Equal(DoctorStatus.Warn, result.Status);
            Assert.Contains("config.toml", result.Detail ?? result.Value, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldCodexHome);
        }
    }

    private string WriteTempState(string json)
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        File.WriteAllText(path, json);
        return path;
    }

    private static string BroadMatcherHooksJson() =>
        """{"hooks":{"PreToolUse":[{"matcher":"^(Bash|bash|Shell|shell|command|exec_command|functions\\.exec_command)$","hooks":[{"type":"command","command":"hypa hook --agent codex","timeout":30}]}]}}""";
}
