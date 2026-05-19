using Hypa.Infrastructure.Hooks;
using Hypa.Runtime.Domain.Hooks;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class HookInstallerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly HookInstaller _installer = new();

    public HookInstallerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // --- PatchJsonHook ---

    [Fact]
    public async Task PatchJsonHook_NewFile_CreatesFileWithHook()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var plan = new InstallPlan([
            new InstallOperation.PatchJsonHook(settingsPath, "PreToolUse", """{"type":"command","command":"hypa hook","timeout":5}""")
        ]);

        var report = await _installer.InstallAsync(plan, "claude", dryRun: false);

        Assert.Single(report.Entries);
        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        Assert.True(File.Exists(settingsPath));
        var content = await File.ReadAllTextAsync(settingsPath);
        Assert.Contains("hypa hook", content);
        Assert.Contains("PreToolUse", content);
    }

    [Fact]
    public async Task PatchJsonHook_AlreadyInstalled_ReportsAlreadyPresent()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var hookJson = """{"type":"command","command":"hypa hook","timeout":5}""";
        var plan = new InstallPlan([new InstallOperation.PatchJsonHook(settingsPath, "PreToolUse", hookJson)]);

        await _installer.InstallAsync(plan, "claude", dryRun: false);
        var second = await _installer.InstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(InstallStatus.AlreadyPresent, second.Entries[0].Status);
    }

    [Fact]
    public async Task PatchJsonHook_InvalidExistingJson_ReportsError()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{ not valid json }");

        var plan = new InstallPlan([
            new InstallOperation.PatchJsonHook(settingsPath, "PreToolUse", """{"type":"command","command":"hypa hook"}""")
        ]);
        var report = await _installer.InstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(InstallStatus.Error, report.Entries[0].Status);
        Assert.NotNull(report.Entries[0].Detail);
    }

    [Fact]
    public async Task PatchJsonHook_DryRun_DoesNotWriteFile()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var plan = new InstallPlan([
            new InstallOperation.PatchJsonHook(settingsPath, "PreToolUse", """{"type":"command","command":"hypa hook"}""")
        ]);

        var report = await _installer.InstallAsync(plan, "claude", dryRun: true);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        Assert.False(File.Exists(settingsPath));
    }

    // --- WriteFile ---

    [Fact]
    public async Task WriteFile_NewFile_CreatesFile()
    {
        var filePath = Path.Combine(_tempDir, "subdir", "SKILL.md");
        var plan = new InstallPlan([new InstallOperation.WriteFile(filePath, "# Skill content")]);

        var report = await _installer.InstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        Assert.Equal("# Skill content", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task WriteFile_SameContent_ReportsAlreadyPresent()
    {
        var filePath = Path.Combine(_tempDir, "SKILL.md");
        await File.WriteAllTextAsync(filePath, "# Skill content");
        var plan = new InstallPlan([new InstallOperation.WriteFile(filePath, "# Skill content")]);

        var report = await _installer.InstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(InstallStatus.AlreadyPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task WriteFile_DryRun_DoesNotWriteFile()
    {
        var filePath = Path.Combine(_tempDir, "SKILL.md");
        var plan = new InstallPlan([new InstallOperation.WriteFile(filePath, "content")]);

        await _installer.InstallAsync(plan, "claude", dryRun: true);

        Assert.False(File.Exists(filePath));
    }

    // --- InjectLine ---

    [Fact]
    public async Task InjectLine_NewFile_CreatesFileWithLine()
    {
        var filePath = Path.Combine(_tempDir, "AGENTS.md");
        var plan = new InstallPlan([new InstallOperation.InjectLine(filePath, "@HYPA.md", CreateIfMissing: true)]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("@HYPA.md", content);
    }

    [Fact]
    public async Task InjectLine_AlreadyPresent_ReportsAlreadyPresent()
    {
        var filePath = Path.Combine(_tempDir, "AGENTS.md");
        await File.WriteAllTextAsync(filePath, "# Agents\n@HYPA.md\n");
        var plan = new InstallPlan([new InstallOperation.InjectLine(filePath, "@HYPA.md")]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.AlreadyPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task InjectLine_FileNotFound_CreateIfMissingFalse_ReportsSkipped()
    {
        var filePath = Path.Combine(_tempDir, "AGENTS.md");
        var plan = new InstallPlan([new InstallOperation.InjectLine(filePath, "@HYPA.md", CreateIfMissing: false)]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.Skipped, report.Entries[0].Status);
    }

    [Fact]
    public async Task InjectLine_DryRun_DoesNotWriteFile()
    {
        var filePath = Path.Combine(_tempDir, "AGENTS.md");
        var plan = new InstallPlan([new InstallOperation.InjectLine(filePath, "@HYPA.md")]);

        await _installer.InstallAsync(plan, "codex", dryRun: true);

        Assert.False(File.Exists(filePath));
    }

    // --- PatchTomlKey ---

    [Fact]
    public async Task PatchTomlKey_NewFile_CreatesFileWithKeyUnderSection()
    {
        var tomlPath = Path.Combine(_tempDir, ".codex", "config.toml");
        var plan = new InstallPlan([new InstallOperation.PatchTomlKey(tomlPath, "features", "codex_hooks", "true")]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("[features]", content);
        Assert.Contains("codex_hooks = true", content);
    }

    [Fact]
    public async Task PatchTomlKey_AlreadyPresent_ReportsAlreadyPresent()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(tomlPath, "[features]\ncodex_hooks = true\n");
        var plan = new InstallPlan([new InstallOperation.PatchTomlKey(tomlPath, "features", "codex_hooks", "true")]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.AlreadyPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task PatchTomlKey_ExistingSectionWithOtherKeys_AppendsUnderSection()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(tomlPath, "[features]\nother_flag = false\n");
        var plan = new InstallPlan([new InstallOperation.PatchTomlKey(tomlPath, "features", "codex_hooks", "true")]);

        await _installer.InstallAsync(plan, "codex", dryRun: false);

        var content = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("other_flag = false", content);
        Assert.Contains("codex_hooks = true", content);
    }

    [Fact]
    public async Task PatchTomlKey_DryRun_DoesNotWriteFile()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        var plan = new InstallPlan([new InstallOperation.PatchTomlKey(tomlPath, "features", "codex_hooks", "true")]);

        await _installer.InstallAsync(plan, "codex", dryRun: true);

        Assert.False(File.Exists(tomlPath));
    }

    // --- EnsureCodexHooksFeature ---

    [Fact]
    public async Task EnsureCodexHooksFeature_NewFile_CreatesCanonicalHooksFlag()
    {
        var tomlPath = Path.Combine(_tempDir, ".codex", "config.toml");
        var plan = new InstallPlan([new InstallOperation.EnsureCodexHooksFeature(tomlPath)]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("[features]", content);
        Assert.Contains("hooks = true", content);
        Assert.DoesNotContain("codex_hooks", content);
    }

    [Fact]
    public async Task EnsureCodexHooksFeature_MigratesLegacyCodexHooksFlag()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(tomlPath, "[features]\ncodex_hooks = false # old flag\n");
        var plan = new InstallPlan([new InstallOperation.EnsureCodexHooksFeature(tomlPath)]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("hooks = true  # old flag", content);
        Assert.DoesNotContain("codex_hooks", content);
    }

    [Fact]
    public async Task EnsureCodexHooksFeature_ReplacesDisabledCanonicalFlag()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(tomlPath, "[features]\nhooks = false\n");
        var plan = new InstallPlan([new InstallOperation.EnsureCodexHooksFeature(tomlPath)]);

        await _installer.InstallAsync(plan, "codex", dryRun: false);

        var content = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("hooks = true", content);
        Assert.DoesNotContain("hooks = false", content);
    }

    [Fact]
    public async Task EnsureCodexHooksFeature_MovesStrayAssignmentIntoFeatures()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(tomlPath, "[mcp_servers.foo]\ncommand = \"foo\"\ncodex_hooks = true\n");
        var plan = new InstallPlan([new InstallOperation.EnsureCodexHooksFeature(tomlPath)]);

        await _installer.InstallAsync(plan, "codex", dryRun: false);

        var content = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("[features]", content);
        Assert.Contains("hooks = true", content);
        Assert.DoesNotContain("codex_hooks = true", content);
    }

    [Fact]
    public async Task EnsureCodexHooksFeature_AlreadyCanonical_ReportsAlreadyPresent()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(tomlPath, "[features]\nhooks = true\n");
        var plan = new InstallPlan([new InstallOperation.EnsureCodexHooksFeature(tomlPath)]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.AlreadyPresent, report.Entries[0].Status);
    }

    // --- NotSupported ---

    [Fact]
    public async Task NotSupported_ReportsSkippedWithMessage()
    {
        var plan = new InstallPlan([new InstallOperation.NotSupported("configure via settings UI")]);
        var report = await _installer.InstallAsync(plan, "copilot-vscode", dryRun: false);

        Assert.Equal(InstallStatus.Skipped, report.Entries[0].Status);
        Assert.Contains("configure via settings UI", report.Entries[0].Detail);
    }

    // --- Idempotence: structural hook comparison ---

    [Fact]
    public async Task PatchJsonHook_DifferentCommandHook_DoesNotFalsePositive()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var existingHook = """{"type":"command","command":"hypa hook","timeout":5}""";
        var newHook = """{"type":"command","command":"hypa hook --agent codex","timeout":30}""";

        var firstPlan = new InstallPlan([new InstallOperation.PatchJsonHook(settingsPath, "PreToolUse", existingHook)]);
        await _installer.InstallAsync(firstPlan, "claude", dryRun: false);

        var secondPlan = new InstallPlan([new InstallOperation.PatchJsonHook(settingsPath, "PreToolUse", newHook)]);
        var report = await _installer.InstallAsync(secondPlan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(settingsPath);
        Assert.Contains("hypa hook --agent codex", content);
        Assert.Contains("hypa hook\"", content);
    }

    [Fact]
    public async Task PatchJsonHook_SameHookDifferentKeyOrder_ReportsAlreadyPresent()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var originalHook = """{"type":"command","command":"hypa hook","timeout":5}""";
        var reorderedHook = """{"command":"hypa hook","timeout":5,"type":"command"}""";

        var plan1 = new InstallPlan([new InstallOperation.PatchJsonHook(settingsPath, "PreToolUse", originalHook)]);
        await _installer.InstallAsync(plan1, "claude", dryRun: false);

        var plan2 = new InstallPlan([new InstallOperation.PatchJsonHook(settingsPath, "PreToolUse", reorderedHook)]);
        var report = await _installer.InstallAsync(plan2, "claude", dryRun: false);

        Assert.Equal(InstallStatus.AlreadyPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task PatchJsonHook_CodexNestedHook_IdempotentOnSecondRun()
    {
        var hooksPath = Path.Combine(_tempDir, "hooks.json");
        var codexHook = """{"matcher":"^Bash$","hooks":[{"type":"command","command":"hypa hook --agent codex","timeout":30}]}""";
        var plan = new InstallPlan([new InstallOperation.PatchJsonHook(hooksPath, "PreToolUse", codexHook)]);

        await _installer.InstallAsync(plan, "codex", dryRun: false);
        var second = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.AlreadyPresent, second.Entries[0].Status);
    }

    [Fact]
    public async Task HookInstaller_DoesNotDuplicateBroadMatcher()
    {
        var hooksPath = Path.Combine(_tempDir, "hooks.json");
        var broadHook = """{"matcher":"^(Bash|bash|Shell|shell|command|exec_command|functions\\.exec_command)$","hooks":[{"type":"command","command":"hypa hook --agent codex","timeout":30}]}""";
        var plan = new InstallPlan([new InstallOperation.PatchJsonHook(hooksPath, "PreToolUse", broadHook)]);

        await _installer.InstallAsync(plan, "codex", dryRun: false);
        var second = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.AlreadyPresent, second.Entries[0].Status);
    }

    // --- PatchTomlKey: replace existing key ---

    [Fact]
    public async Task PatchTomlKey_ExistingKeyWithDifferentValue_ReplacesInPlace()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(tomlPath, "[features]\ncodex_hooks = false\n");
        var plan = new InstallPlan([new InstallOperation.PatchTomlKey(tomlPath, "features", "codex_hooks", "true")]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("codex_hooks = true", content);
        Assert.DoesNotContain("codex_hooks = false", content);
        Assert.Equal(1, content.Split("codex_hooks").Length - 1);
    }

    // --- PatchTomlSection ---

    [Fact]
    public async Task HookInstaller_PatchTomlSection_AddsMcpServerSection()
    {
        var tomlPath = Path.Combine(_tempDir, ".codex", "config.toml");
        var content = "command = \"/usr/bin/hypa\"\nargs = [\"serve\"]";
        var plan = new InstallPlan([
            new InstallOperation.PatchTomlSection(tomlPath, "mcp_servers.hypa", content)
        ]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        var written = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("[mcp_servers.hypa]", written);
        Assert.Contains("command = \"/usr/bin/hypa\"", written);
        Assert.Contains("args = [\"serve\"]", written);
    }

    [Fact]
    public async Task HookInstaller_PatchTomlSection_ReplacesDescendantChildTables()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(tomlPath,
            "[mcp_servers.hypa]\ncommand = \"/old/hypa\"\n\n[mcp_servers.hypa.env]\nLEGACY = \"1\"\n\n[mcp_servers.other]\ncommand = \"other\"\n");

        var content = "command = \"/usr/bin/hypa\"\nargs = [\"serve\"]";
        var plan = new InstallPlan([
            new InstallOperation.PatchTomlSection(tomlPath, "mcp_servers.hypa", content)
        ]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        var written = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("[mcp_servers.hypa]", written);
        Assert.Contains("command = \"/usr/bin/hypa\"", written);
        Assert.DoesNotContain("[mcp_servers.hypa.env]", written);
        Assert.DoesNotContain("LEGACY", written);
        Assert.Contains("[mcp_servers.other]", written);
    }

    [Fact]
    public async Task HookInstaller_PatchTomlSection_IsIdempotent()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        var content = "command = \"/usr/bin/hypa\"\nargs = [\"serve\"]";
        var plan = new InstallPlan([
            new InstallOperation.PatchTomlSection(tomlPath, "mcp_servers.hypa", content)
        ]);

        await _installer.InstallAsync(plan, "codex", dryRun: false);
        var firstContent = await File.ReadAllTextAsync(tomlPath);
        var second = await _installer.InstallAsync(plan, "codex", dryRun: false);
        var secondContent = await File.ReadAllTextAsync(tomlPath);

        Assert.Equal(InstallStatus.AlreadyPresent, second.Entries[0].Status);
        Assert.Equal(firstContent, secondContent);
    }

    [Fact]
    public async Task HookInstaller_PatchTomlSection_IsIdempotent_WhenHeaderHasTrailingComment()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        var content = "command = \"/usr/bin/hypa\"\nargs = [\"serve\"]";
        await File.WriteAllTextAsync(tomlPath,
            "[mcp_servers.hypa] # written by codex\ncommand = \"/usr/bin/hypa\"\nargs = [\"serve\"]\n");

        var plan = new InstallPlan([
            new InstallOperation.PatchTomlSection(tomlPath, "mcp_servers.hypa", content)
        ]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.AlreadyPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task HookInstaller_PatchTomlSection_Replaces_WhenHeaderHasTrailingComment()
    {
        var tomlPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(tomlPath,
            "[mcp_servers.hypa] # old config\ncommand = \"/old/hypa\"\n");

        var newContent = "command = \"/usr/bin/hypa\"\nargs = [\"serve\"]";
        var plan = new InstallPlan([
            new InstallOperation.PatchTomlSection(tomlPath, "mcp_servers.hypa", newContent)
        ]);

        var report = await _installer.InstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(InstallStatus.Installed, report.Entries[0].Status);
        var written = await File.ReadAllTextAsync(tomlPath);
        Assert.Contains("command = \"/usr/bin/hypa\"", written);
        Assert.Contains("args = [\"serve\"]", written);
        Assert.DoesNotContain("/old/hypa", written);
    }

    // --- Report structure ---

    [Fact]
    public async Task Report_HasCorrectHarnessKey()
    {
        var plan = new InstallPlan([]);
        var report = await _installer.InstallAsync(plan, "test-harness", dryRun: false);
        Assert.Equal("test-harness", report.HarnessKey);
    }
}
