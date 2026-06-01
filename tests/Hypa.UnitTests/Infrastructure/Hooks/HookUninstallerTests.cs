using Hypa.Infrastructure.Hooks;
using Hypa.Runtime.Domain.Hooks;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class HookUninstallerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly HookUninstaller _uninstaller = new();

    public HookUninstallerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // --- RemoveJsonHook ---

    [Fact]
    public async Task RemoveJsonHook_HookPresent_RemovesHook()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "PreToolUse": [
                  {"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}
                ]
              }
            }
            """);

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonHook(path, "PreToolUse",
                """{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}""")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("hypa hook", content);
    }

    [Fact]
    public async Task RemoveJsonHook_HookAbsent_ReturnsNotPresent()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, """{"hooks":{}}""");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonHook(path, "PreToolUse",
                """{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}""")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(UninstallStatus.NotPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task RemoveJsonHook_FileMissing_ReturnsNotPresent()
    {
        var path = Path.Combine(_tempDir, "missing.json");
        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonHook(path, "PreToolUse", """{"matcher":""}""")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(UninstallStatus.NotPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task RemoveJsonHook_DryRun_DoesNotWriteFile()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var original = """
            {"hooks":{"PreToolUse":[{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}]}}
            """;
        await File.WriteAllTextAsync(path, original);

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonHook(path, "PreToolUse",
                """{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}""")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: true);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        Assert.Equal(original, await File.ReadAllTextAsync(path)); // file unchanged
    }

    [Fact]
    public async Task RemoveJsonHook_LastHook_RemovesHooksObject()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, """
            {"hooks":{"PreToolUse":[{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}]}}
            """);

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonHook(path, "PreToolUse",
                """{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}""")
        ]);

        await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("\"hooks\"", content);
    }

    // --- RemoveJsonObject ---

    [Fact]
    public async Task RemoveJsonObject_KeyPresent_RemovesKey()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, """
            {"mcpServers":{"hypa":{"type":"stdio","command":"hypa","args":["serve"]}}}
            """);

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonObject(path, "mcpServers", "hypa")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("mcpServers", content);
    }

    [Fact]
    public async Task RemoveJsonObject_KeyAbsent_ReturnsNotPresent()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, """{"mcpServers":{}}""");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonObject(path, "mcpServers", "hypa")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(UninstallStatus.NotPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task RemoveJsonObject_DryRun_DoesNotModifyFile()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var original = """{"mcpServers":{"hypa":{"type":"stdio"}}}""";
        await File.WriteAllTextAsync(path, original);

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonObject(path, "mcpServers", "hypa")
        ]);

        await _uninstaller.UninstallAsync(plan, "claude", dryRun: true);

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    // --- RemoveFencedBlock ---

    [Fact]
    public async Task RemoveFencedBlock_BlockPresent_RemovesBlock()
    {
        var path = Path.Combine(_tempDir, "CLAUDE.md");
        await File.WriteAllTextAsync(path, "# Header\n\n<!-- hypa -->\nHypa content\n<!-- /hypa -->\n\n## Footer\n");

        var plan = new UninstallPlan([new UninstallOperation.RemoveFencedBlock(path, "hypa")]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("hypa", content);
        Assert.Contains("# Header", content);
        Assert.Contains("## Footer", content);
    }

    [Fact]
    public async Task RemoveFencedBlock_BlockAbsent_ReturnsNotPresent()
    {
        var path = Path.Combine(_tempDir, "CLAUDE.md");
        await File.WriteAllTextAsync(path, "# No hypa block here\n");

        var plan = new UninstallPlan([new UninstallOperation.RemoveFencedBlock(path, "hypa")]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(UninstallStatus.NotPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task RemoveFencedBlock_DryRun_DoesNotModifyFile()
    {
        var path = Path.Combine(_tempDir, "CLAUDE.md");
        var original = "<!-- hypa -->\nHypa content\n<!-- /hypa -->\n";
        await File.WriteAllTextAsync(path, original);

        var plan = new UninstallPlan([new UninstallOperation.RemoveFencedBlock(path, "hypa")]);

        await _uninstaller.UninstallAsync(plan, "claude", dryRun: true);

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RemoveFencedBlock_CollapsesConsecutiveBlankLines()
    {
        var path = Path.Combine(_tempDir, "CLAUDE.md");
        await File.WriteAllTextAsync(path, "First\n\n<!-- hypa -->\nblock\n<!-- /hypa -->\n\nLast\n");

        var plan = new UninstallPlan([new UninstallOperation.RemoveFencedBlock(path, "hypa")]);

        await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("\n\n\n", content);
    }

    // --- RemoveTomlKey ---

    [Fact]
    public async Task RemoveTomlKey_KeyPresent_RemovesKeyAndSection()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(path, "[features]\ncodex_hooks = true\n");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveTomlKey(path, "features", "codex_hooks")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("codex_hooks", content);
        Assert.DoesNotContain("[features]", content);
    }

    [Fact]
    public async Task RemoveTomlKey_SectionHasOtherKeys_PreservesSection()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(path, "[features]\nother_flag = true\ncodex_hooks = true\n");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveTomlKey(path, "features", "codex_hooks")
        ]);

        await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("[features]", content);
        Assert.Contains("other_flag", content);
        Assert.DoesNotContain("codex_hooks", content);
    }

    [Fact]
    public async Task RemoveTomlKey_SectionWithCommentOnly_RemovesHeader()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(path, "[features]\n# a comment\ncodex_hooks = true\n");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveTomlKey(path, "features", "codex_hooks")
        ]);

        await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("[features]", content);
    }

    [Fact]
    public async Task RemoveTomlKey_KeyAbsent_ReturnsNotPresent()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(path, "[features]\nother_key = true\n");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveTomlKey(path, "features", "codex_hooks")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.NotPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task RemoveTomlKey_DryRun_DoesNotModifyFile()
    {
        var path = Path.Combine(_tempDir, "config.toml");
        var original = "[features]\ncodex_hooks = true\n";
        await File.WriteAllTextAsync(path, original);

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveTomlKey(path, "features", "codex_hooks")
        ]);

        await _uninstaller.UninstallAsync(plan, "codex", dryRun: true);

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    // --- RemoveCodexHooksFeatureIfUnused ---

    [Fact]
    public async Task RemoveCodexHooksFeatureIfUnused_NoHooksRemain_RemovesCanonicalAndLegacyFlags()
    {
        var configPath = Path.Combine(_tempDir, "config.toml");
        var hooksPath = Path.Combine(_tempDir, "hooks.json");
        await File.WriteAllTextAsync(configPath, "[features]\nhooks = true\ncodex_hooks = true\n");
        await File.WriteAllTextAsync(hooksPath, """{"hooks":{}}""");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveCodexHooksFeatureIfUnused(configPath, hooksPath)
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(configPath);
        Assert.DoesNotContain("hooks = true", content);
        Assert.DoesNotContain("codex_hooks", content);
    }

    [Fact]
    public async Task RemoveCodexHooksFeatureIfUnused_OtherHooksRemain_Skips()
    {
        var configPath = Path.Combine(_tempDir, "config.toml");
        var hooksPath = Path.Combine(_tempDir, "hooks.json");
        await File.WriteAllTextAsync(configPath, "[features]\nhooks = true\n");
        await File.WriteAllTextAsync(hooksPath, """{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"command":"other"}]}]}}""");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveCodexHooksFeatureIfUnused(configPath, hooksPath)
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.Skipped, report.Entries[0].Status);
        Assert.Contains("hooks = true", await File.ReadAllTextAsync(configPath));
    }

    [Fact]
    public async Task RemoveCodexHooksFeatureIfUnused_DryRun_DoesNotModifyFile()
    {
        var configPath = Path.Combine(_tempDir, "config.toml");
        var hooksPath = Path.Combine(_tempDir, "hooks.json");
        var original = "[features]\nhooks = true\n";
        await File.WriteAllTextAsync(configPath, original);
        await File.WriteAllTextAsync(hooksPath, """{"hooks":{}}""");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveCodexHooksFeatureIfUnused(configPath, hooksPath)
        ]);

        await _uninstaller.UninstallAsync(plan, "codex", dryRun: true);

        Assert.Equal(original, await File.ReadAllTextAsync(configPath));
    }

    // --- RemoveLine ---

    [Fact]
    public async Task RemoveLine_LinePresent_RemovesLine()
    {
        var path = Path.Combine(_tempDir, "AGENTS.md");
        await File.WriteAllTextAsync(path, "# Agents\n@HYPA.md\n");

        var plan = new UninstallPlan([new UninstallOperation.RemoveLine(path, "@HYPA.md")]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        Assert.DoesNotContain("@HYPA.md", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RemoveLine_LineWithWhitespace_MatchesByTrimmedContent()
    {
        var path = Path.Combine(_tempDir, "AGENTS.md");
        await File.WriteAllTextAsync(path, "  @HYPA.md  \n");

        var plan = new UninstallPlan([new UninstallOperation.RemoveLine(path, "@HYPA.md")]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
    }

    [Fact]
    public async Task RemoveLine_LineAbsent_ReturnsNotPresent()
    {
        var path = Path.Combine(_tempDir, "AGENTS.md");
        await File.WriteAllTextAsync(path, "# No hypa line\n");

        var plan = new UninstallPlan([new UninstallOperation.RemoveLine(path, "@HYPA.md")]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.NotPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task RemoveLine_DryRun_DoesNotModifyFile()
    {
        var path = Path.Combine(_tempDir, "AGENTS.md");
        var original = "# Header\n@HYPA.md\n";
        await File.WriteAllTextAsync(path, original);

        var plan = new UninstallPlan([new UninstallOperation.RemoveLine(path, "@HYPA.md")]);

        await _uninstaller.UninstallAsync(plan, "codex", dryRun: true);

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    // --- DeleteFile ---

    [Fact]
    public async Task DeleteFile_FileExists_DeletesFile()
    {
        var path = Path.Combine(_tempDir, "HYPA.md");
        await File.WriteAllTextAsync(path, "content");

        var plan = new UninstallPlan([new UninstallOperation.DeleteFile(path)]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteFile_DryRun_DoesNotDeleteFile()
    {
        var path = Path.Combine(_tempDir, "HYPA.md");
        await File.WriteAllTextAsync(path, "content");

        var plan = new UninstallPlan([new UninstallOperation.DeleteFile(path)]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: true);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        Assert.True(File.Exists(path)); // file must still exist after dry run
    }

    [Fact]
    public async Task DeleteFile_FileMissing_ReturnsNotPresent()
    {
        var path = Path.Combine(_tempDir, "missing.md");
        var plan = new UninstallPlan([new UninstallOperation.DeleteFile(path)]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.NotPresent, report.Entries[0].Status);
    }

    // --- DeleteDirectory ---

    [Fact]
    public async Task DeleteDirectory_DirectoryExists_DeletesDirectory()
    {
        var dir = Path.Combine(_tempDir, "skills", "hypa");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"), "content");

        var plan = new UninstallPlan([new UninstallOperation.DeleteDirectory(dir)]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task DeleteDirectory_DryRun_DoesNotDeleteDirectory()
    {
        var dir = Path.Combine(_tempDir, "skills", "hypa");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"), "content");

        var plan = new UninstallPlan([new UninstallOperation.DeleteDirectory(dir)]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: true);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        Assert.True(Directory.Exists(dir)); // directory must still exist after dry run
    }

    [Fact]
    public async Task DeleteDirectory_Missing_ReturnsNotPresent()
    {
        var dir = Path.Combine(_tempDir, "nonexistent");
        var plan = new UninstallPlan([new UninstallOperation.DeleteDirectory(dir)]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(UninstallStatus.NotPresent, report.Entries[0].Status);
    }

    // --- NotSupported ---

    [Fact]
    public async Task NotSupported_ReturnsSkipped()
    {
        var plan = new UninstallPlan([new UninstallOperation.NotSupported("Manual removal required")]);

        var report = await _uninstaller.UninstallAsync(plan, "copilot", dryRun: false);

        Assert.Equal(UninstallStatus.Skipped, report.Entries[0].Status);
    }

    // --- Backup ---

    [Fact]
    public async Task RemoveJsonHook_SharedFile_CreatesBackup()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, """
            {"hooks":{"PreToolUse":[{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}]}}
            """);

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonHook(path, "PreToolUse",
                """{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}""")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "claude", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        Assert.NotNull(report.Entries[0].Detail);
        Assert.Contains("backup:", report.Entries[0].Detail);
        Assert.True(File.Exists(path + ".hypa.bak"));
    }

    // --- RemoveTomlSection ---

    [Fact]
    public async Task HookUninstaller_RemoveTomlSection_RemovesOnlyHypaMcpServer()
    {
        var configPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(configPath,
            "[mcp_servers.hypa]\ncommand = \"/usr/bin/hypa\"\nargs = [\"serve\"]\n\n[mcp_servers.other]\ncommand = \"other\"\n");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveTomlSection(configPath, "mcp_servers.hypa")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(configPath);
        Assert.DoesNotContain("[mcp_servers.hypa]", content);
        Assert.Contains("[mcp_servers.other]", content);
    }

    [Fact]
    public async Task HookUninstaller_RemoveTomlSection_RemovesDescendantChildTables()
    {
        var configPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(configPath,
            "[mcp_servers.hypa]\ncommand = \"/usr/bin/hypa\"\n\n[mcp_servers.hypa.env]\nFOO = \"bar\"\n\n[mcp_servers.other]\ncommand = \"other\"\n");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveTomlSection(configPath, "mcp_servers.hypa")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(configPath);
        Assert.DoesNotContain("[mcp_servers.hypa]", content);
        Assert.DoesNotContain("[mcp_servers.hypa.env]", content);
        Assert.Contains("[mcp_servers.other]", content);
    }

    [Fact]
    public async Task HookUninstaller_RemoveTomlSection_RemovesSection_WhenHeaderHasTrailingComment()
    {
        var configPath = Path.Combine(_tempDir, "config.toml");
        await File.WriteAllTextAsync(configPath,
            "[mcp_servers.hypa] # managed by hypa\ncommand = \"/usr/bin/hypa\"\nargs = [\"serve\"]\n\n[mcp_servers.other]\ncommand = \"other\"\n");

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveTomlSection(configPath, "mcp_servers.hypa")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "codex", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(configPath);
        Assert.DoesNotContain("mcp_servers.hypa", content);
        Assert.Contains("[mcp_servers.other]", content);
    }

    [Fact]
    public async Task RemoveJsonArrayValue_ValuePresent_RemovesValue()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, """
            { "packages": ["/repo/packages/pi-hypa", "npm:other"] }
            """);
        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonArrayValue(path, "packages", "/repo/packages/pi-hypa")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "pi", dryRun: false);

        Assert.Equal(UninstallStatus.Removed, report.Entries[0].Status);
        var content = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("/repo/packages/pi-hypa", content);
        Assert.Contains("npm:other", content);
    }

    [Fact]
    public async Task RemoveJsonArrayValue_ValueAbsent_ReturnsNotPresent()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, """
            { "packages": ["npm:other"] }
            """);
        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonArrayValue(path, "packages", "/repo/packages/pi-hypa")
        ]);

        var report = await _uninstaller.UninstallAsync(plan, "pi", dryRun: false);

        Assert.Equal(UninstallStatus.NotPresent, report.Entries[0].Status);
    }

    [Fact]
    public async Task RemoveJsonHook_DryRun_DoesNotCreateBackup()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, """
            {"hooks":{"PreToolUse":[{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}]}}
            """);

        var plan = new UninstallPlan([
            new UninstallOperation.RemoveJsonHook(path, "PreToolUse",
                """{"matcher":"","hooks":[{"type":"command","command":"hypa hook","timeout":5}]}""")
        ]);

        await _uninstaller.UninstallAsync(plan, "claude", dryRun: true);

        Assert.False(File.Exists(path + ".hypa.bak"));
    }
}
