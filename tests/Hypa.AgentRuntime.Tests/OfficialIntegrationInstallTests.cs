using System.Text;
using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.Cli;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Integrations;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class OfficialIntegrationInstallTests : IDisposable
{
    private readonly string _home;
    private readonly TestIntegrationEnvironment _env;
    private readonly OfficialIntegrationService _service;

    public OfficialIntegrationInstallTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "hypa-official-integrations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _env = new TestIntegrationEnvironment { UserHome = _home };
        _service = new OfficialIntegrationService(new SystemIntegrationFiles(), _env, new SystemIntegrationClock());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_home))
                Directory.Delete(_home, recursive: true);
        }
        catch
        {
            // teardown
        }
    }

    [Fact]
    public void Install_and_uninstall_pi_write_only_owned_extension()
    {
        var agent = Path.Combine(_home, ".pi", "agent");
        Directory.CreateDirectory(agent);
        var userFile = Path.Combine(agent, "extensions", "user-plugin.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        File.WriteAllText(userFile, "export default function () {}");

        var installed = _service.Install(OfficialIntegrationTarget.Pi);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var owned = OfficialIntegrationLayout.PiOwnedFile(_env);
        Assert.True(File.Exists(owned));
        Assert.Contains("HYPA_INTEGRATION_VERSION=1", File.ReadAllText(owned), StringComparison.Ordinal);
        Assert.Contains("hypa:pi", File.ReadAllText(owned), StringComparison.Ordinal);
        Assert.DoesNotContain("HERDR_", File.ReadAllText(owned), StringComparison.Ordinal);
        Assert.True(File.Exists(userFile));
        Assert.Empty(HerdrNamedFiles());

        File.WriteAllText(owned, "// stale\n");
        var edited = _service.Install(OfficialIntegrationTarget.Pi);
        Assert.True(edited.IsOk);
        Assert.Contains("HYPA_INTEGRATION_VERSION=1", File.ReadAllText(owned), StringComparison.Ordinal);

        var removed = _service.Uninstall(OfficialIntegrationTarget.Pi);
        Assert.True(removed.IsOk);
        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(userFile));
    }

    [Fact]
    public void Install_and_uninstall_omp_write_only_owned_extension()
    {
        var agent = Path.Combine(_home, ".omp", "agent");
        Directory.CreateDirectory(agent);
        var userFile = Path.Combine(agent, "extensions", "keep.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        File.WriteAllText(userFile, "// user");

        var installed = _service.Install(OfficialIntegrationTarget.Omp);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var owned = OfficialIntegrationLayout.OmpOwnedFile(_env);
        Assert.True(File.Exists(owned));
        Assert.Contains("hypa:omp", File.ReadAllText(owned), StringComparison.Ordinal);
        Assert.True(File.Exists(userFile));

        var removed = _service.Uninstall(OfficialIntegrationTarget.Omp);
        Assert.True(removed.IsOk);
        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(userFile));
    }

    [Fact]
    public void Install_edit_and_uninstall_claude_preserves_user_settings()
    {
        var claude = Path.Combine(_home, ".claude");
        Directory.CreateDirectory(claude);
        var settingsPath = Path.Combine(claude, "settings.json");
        File.WriteAllText(
            settingsPath,
            """
            {
              "hooks": {
                "Notification": [{"matcher": "keep", "hooks": []}]
              }
            }
            """);

        var installed = _service.Install(OfficialIntegrationTarget.Claude);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var owned = OfficialIntegrationLayout.ClaudeOwnedFile(_env);
        Assert.True(File.Exists(owned));
        var settings = File.ReadAllText(settingsPath);
        Assert.Contains("SessionStart", settings, StringComparison.Ordinal);
        Assert.Contains("hypa-agent-state.sh", settings, StringComparison.Ordinal);
        Assert.Contains("Notification", settings, StringComparison.Ordinal);
        Assert.Contains("keep", settings, StringComparison.Ordinal);

        File.WriteAllText(owned, "# stale\n");
        Assert.True(_service.Install(OfficialIntegrationTarget.Claude).IsOk);
        Assert.Contains("HYPA_INTEGRATION_VERSION=1", File.ReadAllText(owned), StringComparison.Ordinal);

        var removed = _service.Uninstall(OfficialIntegrationTarget.Claude);
        Assert.True(removed.IsOk);
        Assert.False(File.Exists(owned));
        var after = File.ReadAllText(settingsPath);
        Assert.Contains("Notification", after, StringComparison.Ordinal);
        Assert.Contains("keep", after, StringComparison.Ordinal);
        Assert.DoesNotContain("hypa-agent-state.sh", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_and_uninstall_codex_leave_user_hooks_and_config()
    {
        var dir = Path.Combine(_home, ".codex");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hooks.json"), """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo keep"}]}]}}""");
        File.WriteAllText(Path.Combine(dir, "config.toml"), "model = \"gpt\"\n");

        var installed = _service.Install(OfficialIntegrationTarget.Codex);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var owned = OfficialIntegrationLayout.CodexOwnedFile(_env);
        Assert.True(File.Exists(owned));
        var hooks = File.ReadAllText(Path.Combine(dir, "hooks.json"));
        Assert.Contains("SessionStart", hooks, StringComparison.Ordinal);
        Assert.Contains("echo keep", hooks, StringComparison.Ordinal);
        var config = File.ReadAllText(Path.Combine(dir, "config.toml"));
        Assert.Contains("hooks = true", config, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt\"", config, StringComparison.Ordinal);

        var removed = _service.Uninstall(OfficialIntegrationTarget.Codex);
        Assert.True(removed.IsOk);
        Assert.False(File.Exists(owned));
        var afterHooks = File.ReadAllText(Path.Combine(dir, "hooks.json"));
        Assert.Contains("echo keep", afterHooks, StringComparison.Ordinal);
        Assert.DoesNotContain("hypa-agent-state.sh", afterHooks, StringComparison.Ordinal);
        Assert.Equal(config, File.ReadAllText(Path.Combine(dir, "config.toml")));
    }

    [Fact]
    public void Install_and_uninstall_codex_preserve_commented_unrelated_table()
    {
        var dir = Path.Combine(_home, ".codex");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hooks.json"), """{"hooks":{}}""");
        const string input =
            """
            [features]
            hooks = false
            [tools] # user settings
            hooks = "keep"
            """;
        File.WriteAllText(Path.Combine(dir, "config.toml"), input);

        var installed = _service.Install(OfficialIntegrationTarget.Codex);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var config = File.ReadAllText(Path.Combine(dir, "config.toml"));
        Assert.Contains("[features]\nhooks = true\n", config, StringComparison.Ordinal);
        Assert.Contains("[tools] # user settings\nhooks = \"keep\"", config, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks = false", config, StringComparison.Ordinal);

        var removed = _service.Uninstall(OfficialIntegrationTarget.Codex);
        Assert.True(removed.IsOk);
        var after = File.ReadAllText(Path.Combine(dir, "config.toml"));
        Assert.Equal(config, after);
        Assert.Contains("hooks = \"keep\"", after, StringComparison.Ordinal);
        Assert.Contains("hooks = true", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_and_uninstall_kimi_reverts_owned_block()
    {
        var dir = Path.Combine(_home, ".kimi-code");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.toml"), "theme = \"dark\"\n");

        var installed = _service.Install(OfficialIntegrationTarget.Kimi);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var owned = OfficialIntegrationLayout.KimiOwnedFile(_env);
        Assert.True(File.Exists(owned));
        var config = File.ReadAllText(Path.Combine(dir, "config.toml"));
        Assert.Contains("hypa kimi integration", config, StringComparison.Ordinal);
        Assert.Contains("theme = \"dark\"", config, StringComparison.Ordinal);

        var removed = _service.Uninstall(OfficialIntegrationTarget.Kimi);
        Assert.True(removed.IsOk);
        Assert.False(File.Exists(owned));
        var after = File.ReadAllText(Path.Combine(dir, "config.toml"));
        Assert.Contains("theme = \"dark\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("hypa kimi integration", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_lists_installed_versions()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".pi", "agent"));
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Assert.True(_service.Install(OfficialIntegrationTarget.Pi).IsOk);
        Assert.True(_service.Install(OfficialIntegrationTarget.Claude).IsOk);
        File.WriteAllText(OfficialIntegrationLayout.PiOwnedFile(_env), "// HYPA_INTEGRATION_VERSION=0\n");

        var statuses = _service.ListStatuses();
        Assert.Equal(17, statuses.Count);
        var pi = Assert.Single(statuses, s => s.Target is OfficialIntegrationTarget.Pi);
        Assert.Equal(OfficialIntegrationStatusKind.Outdated, pi.State);
        Assert.Equal(0, pi.InstalledVersion);
        Assert.Equal(1, pi.ExpectedVersion);
        var claude = Assert.Single(statuses, s => s.Target is OfficialIntegrationTarget.Claude);
        Assert.Equal(OfficialIntegrationStatusKind.Current, claude.State);
        Assert.Equal(1, claude.InstalledVersion);
        var kimi = Assert.Single(statuses, s => s.Target is OfficialIntegrationTarget.Kimi);
        Assert.Equal(OfficialIntegrationStatusKind.NotInstalled, kimi.State);
        Assert.Null(kimi.InstalledVersion);
    }

    [Fact]
    public async Task List_rpc_includes_versions()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".pi", "agent"));
        Assert.True(_service.Install(OfficialIntegrationTarget.Pi).IsOk);
        var app = new AppState(SessionId.New("integrations-list"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            integrations: _service);
        try
        {
            var listed = await cp.DispatchAsync(
                ProtocolMethods.IntegrationList,
                null,
                CancellationToken.None);
            Assert.Equal(JsonValueKind.Array, listed.GetProperty("integrations").ValueKind);
            var pi = listed.GetProperty("integrations").EnumerateArray()
                .Single(el => el.GetProperty("target").GetString() == "pi");
            Assert.Equal(1, pi.GetProperty("installed_version").GetInt32());
            Assert.Equal(1, pi.GetProperty("expected_version").GetInt32());
            Assert.Equal("current", pi.GetProperty("state").GetString());
            Assert.Equal(17, listed.GetProperty("integrations").GetArrayLength());
            var agy = listed.GetProperty("integrations").EnumerateArray()
                .Single(el => el.GetProperty("target").GetString() == "antigravity_cli");
            Assert.Equal("antigravity-cli", agy.GetProperty("label").GetString());
            Assert.Equal("agy", agy.GetProperty("command").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(OfficialIntegrationTarget.Copilot)]
    [InlineData(OfficialIntegrationTarget.Devin)]
    [InlineData(OfficialIntegrationTarget.Droid)]
    [InlineData(OfficialIntegrationTarget.Opencode)]
    [InlineData(OfficialIntegrationTarget.Kilo)]
    [InlineData(OfficialIntegrationTarget.Hermes)]
    [InlineData(OfficialIntegrationTarget.Qodercli)]
    [InlineData(OfficialIntegrationTarget.Qwen)]
    [InlineData(OfficialIntegrationTarget.Cursor)]
    [InlineData(OfficialIntegrationTarget.Mastracode)]
    [InlineData(OfficialIntegrationTarget.AntigravityCli)]
    [InlineData(OfficialIntegrationTarget.Grok)]
    public void Install_and_uninstall_unix_target_writes_only_owned_files(
        OfficialIntegrationTarget target)
    {
        EnsureTargetHome(target);
        var installed = _service.Install(target);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var owned = OfficialIntegrationLayout.OwnedFile(_env, target);
        Assert.True(File.Exists(owned), owned);
        var content = File.ReadAllText(owned);
        Assert.Contains("HYPA_INTEGRATION_VERSION=", content, StringComparison.Ordinal);
        Assert.DoesNotContain("HERDR_", content, StringComparison.Ordinal);
        Assert.DoesNotContain("herdr:", content, StringComparison.Ordinal);
        Assert.Empty(HerdrNamedFiles());

        var removed = _service.Uninstall(target);
        Assert.True(removed.IsOk, removed.IsOk ? "" : removed.Error.Message);
        Assert.False(File.Exists(owned));
        if (target is OfficialIntegrationTarget.Grok)
            Assert.False(File.Exists(OfficialIntegrationLayout.GrokConfigOwnedFile(_env)));
        if (target is OfficialIntegrationTarget.Opencode)
            Assert.False(File.Exists(OfficialIntegrationLayout.OpenCodeTuiOwnedFile(_env)));
        if (target is OfficialIntegrationTarget.Hermes)
            Assert.False(Directory.Exists(OfficialIntegrationLayout.HermesPluginDir(_env)));
        Assert.Empty(HerdrNamedFiles());
    }

    [Fact]
    public void Install_opencode_refuses_invalid_tui_plugin_list()
    {
        var dir = Path.Combine(_home, ".config", "opencode");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tui.jsonc"), """{"plugin":{}}""");
        var result = _service.Install(OfficialIntegrationTarget.Opencode);
        Assert.False(result.IsOk);
        Assert.False(File.Exists(OfficialIntegrationLayout.OpenCodeOwnedFile(_env)));
    }

    [Fact]
    public void Install_hermes_enables_plugin_and_preserves_user_yaml()
    {
        var dir = Path.Combine(_home, ".hermes");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.yaml"), "theme: dark\n");
        Assert.True(_service.Install(OfficialIntegrationTarget.Hermes).IsOk);
        var config = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        Assert.Contains("theme: dark", config, StringComparison.Ordinal);
        Assert.Contains("hypa-agent-state", config, StringComparison.Ordinal);
        Assert.True(_service.Uninstall(OfficialIntegrationTarget.Hermes).IsOk);
        var after = File.ReadAllText(Path.Combine(dir, "config.yaml"));
        Assert.Contains("theme: dark", after, StringComparison.Ordinal);
        Assert.DoesNotContain("hypa-agent-state", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_marks_opencode_outdated_without_tui_companion()
    {
        EnsureTargetHome(OfficialIntegrationTarget.Opencode);
        Assert.True(_service.Install(OfficialIntegrationTarget.Opencode).IsOk);
        var current = Assert.Single(
            _service.ListStatuses(),
            s => s.Target is OfficialIntegrationTarget.Opencode);
        Assert.Equal(OfficialIntegrationStatusKind.Current, current.State);

        File.Delete(OfficialIntegrationLayout.OpenCodeTuiOwnedFile(_env));
        var missingPlugin = Assert.Single(
            _service.ListStatuses(),
            s => s.Target is OfficialIntegrationTarget.Opencode);
        Assert.Equal(OfficialIntegrationStatusKind.Outdated, missingPlugin.State);

        Assert.True(_service.Install(OfficialIntegrationTarget.Opencode).IsOk);
        File.WriteAllText(Path.Combine(_home, ".config", "opencode", "tui.jsonc"), "{}\n");
        var missingConfig = Assert.Single(
            _service.ListStatuses(),
            s => s.Target is OfficialIntegrationTarget.Opencode);
        Assert.Equal(OfficialIntegrationStatusKind.Outdated, missingConfig.State);
    }

    [Fact]
    public void Status_marks_grok_outdated_without_hook_config()
    {
        EnsureTargetHome(OfficialIntegrationTarget.Grok);
        Assert.True(_service.Install(OfficialIntegrationTarget.Grok).IsOk);
        var current = Assert.Single(
            _service.ListStatuses(),
            s => s.Target is OfficialIntegrationTarget.Grok);
        Assert.Equal(OfficialIntegrationStatusKind.Current, current.State);

        File.Delete(OfficialIntegrationLayout.GrokConfigOwnedFile(_env));
        var missing = Assert.Single(
            _service.ListStatuses(),
            s => s.Target is OfficialIntegrationTarget.Grok);
        Assert.Equal(OfficialIntegrationStatusKind.Outdated, missing.State);

        File.WriteAllText(OfficialIntegrationLayout.GrokConfigOwnedFile(_env), "{}\n");
        var stale = Assert.Single(
            _service.ListStatuses(),
            s => s.Target is OfficialIntegrationTarget.Grok);
        Assert.Equal(OfficialIntegrationStatusKind.Outdated, stale.State);
    }

    [Fact]
    public void Status_treats_codex_standalone_release_as_available()
    {
        var binary = Path.Combine(
            _home,
            ".codex",
            "packages",
            "standalone",
            "releases",
            "0.1.0",
            "bin",
            "codex");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "#!/bin/sh\n");
        var status = Assert.Single(
            _service.ListStatuses(),
            s => s.Target is OfficialIntegrationTarget.Codex);
        Assert.True(status.Available);
        Assert.Equal(OfficialIntegrationStatusKind.NotInstalled, status.State);
    }

    [Fact(DisplayName = "integration.install for claude with a temp home writes skills/hypa-runtime/SKILL.md")]
    public async Task Integration_install_for_claude_with_a_temp_home_writes_skills_hypa_runtime_skill()
    {
        var repo = RepositorySkillBytes();
        Assert.Equal(repo, Encoding.UTF8.GetBytes(RuntimeSkillPrinter.Text()));

        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        var reply = await InstallIntegrationAsync("claude");
        Assert.Equal("claude", reply.GetProperty("target").GetString());
        var skill = Path.Combine(_home, ".claude", "skills", "hypa-runtime", "SKILL.md");
        Assert.Equal(skill, OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Claude));
        Assert.Equal(repo, File.ReadAllBytes(skill));
        Assert.Contains(
            reply.GetProperty("messages").EnumerateArray(),
            message => (message.GetString() ?? "").Contains("installed runtime skill", StringComparison.Ordinal));
    }

    [Fact]
    public void Install_claude_writes_runtime_skill_under_config_dir()
    {
        var custom = Path.Combine(_home, "claude-config");
        Directory.CreateDirectory(custom);
        _env.Variables[OfficialIntegrationLayout.ClaudeConfigDir] = custom;

        var installed = _service.Install(OfficialIntegrationTarget.Claude);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var skill = Path.Combine(custom, "skills", "hypa-runtime", "SKILL.md");
        Assert.Equal(skill, OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Claude));
        Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(skill));
        Assert.False(File.Exists(Path.Combine(_home, ".claude", "skills", "hypa-runtime", "SKILL.md")));
    }

    [Fact]
    public void Second_claude_install_leaves_hook_and_runtime_skill_unchanged()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Assert.True(_service.Install(OfficialIntegrationTarget.Claude).IsOk);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Claude)!;
        var hook = OfficialIntegrationLayout.ClaudeOwnedFile(_env);
        var settings = Path.Combine(_home, ".claude", "settings.json");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(skill, stamp);
        File.SetLastWriteTimeUtc(hook, stamp);
        File.SetLastWriteTimeUtc(settings, stamp);
        var skillBefore = File.GetLastWriteTimeUtc(skill);
        var hookBefore = File.GetLastWriteTimeUtc(hook);
        var settingsBefore = File.GetLastWriteTimeUtc(settings);

        var second = _service.Install(OfficialIntegrationTarget.Claude);
        Assert.True(second.IsOk, second.IsOk ? "" : second.Error.Message);
        Assert.Equal(skillBefore, File.GetLastWriteTimeUtc(skill));
        Assert.Equal(hookBefore, File.GetLastWriteTimeUtc(hook));
        Assert.Equal(settingsBefore, File.GetLastWriteTimeUtc(settings));
        Assert.Contains(second.Value.Messages, message => message.Contains("unchanged", StringComparison.Ordinal));
        Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(skill));
    }

    [Fact]
    public async Task Changed_runtime_skill_lists_outdated_and_install_restores_it()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Assert.True(_service.Install(OfficialIntegrationTarget.Claude).IsOk);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Claude)!;
        File.WriteAllText(skill, "changed\n");

        var outdated = await ListIntegrationsAsync();
        var claude = SingleTarget(outdated, "claude");
        Assert.Equal("outdated", claude.GetProperty("skill_state").GetString());

        var restored = _service.Install(OfficialIntegrationTarget.Claude);
        Assert.True(restored.IsOk, restored.IsOk ? "" : restored.Error.Message);
        Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(skill));
        var current = await ListIntegrationsAsync();
        Assert.Equal("installed", SingleTarget(current, "claude").GetProperty("skill_state").GetString());
    }

    [Fact]
    public async Task List_reports_skill_state_for_each_target()
    {
        var listed = await ListIntegrationsAsync();
        var items = listed.GetProperty("integrations");
        Assert.Equal(OfficialIntegrationTargets.All.Count, items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            var target = item.GetProperty("target").GetString();
            var skill = item.GetProperty("skill_state").GetString();
            Assert.Equal(ExpectedSkillBeforeInstall(target), skill);
        }

        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Assert.True(_service.Install(OfficialIntegrationTarget.Claude).IsOk);
        var after = await ListIntegrationsAsync();
        foreach (var item in after.GetProperty("integrations").EnumerateArray())
        {
            var target = item.GetProperty("target").GetString();
            var skill = item.GetProperty("skill_state").GetString();
            var expected = target == "claude" ? "installed" : "missing";
            Assert.Equal(expected, skill);
        }
    }

    [Fact]
    public async Task Install_writes_a_skill_file_or_one_instruction_block_for_every_target()
    {
        PrepareConfigDirs();
        foreach (var target in OfficialIntegrationTargets.All)
        {
            var installed = _service.Install(target);
            Assert.True(installed.IsOk, target.WireName() + " " + (installed.IsOk ? "" : installed.Error.Message));
        }

        var listed = await ListIntegrationsAsync();
        Assert.Equal(OfficialIntegrationTargets.All.Count, listed.GetProperty("integrations").GetArrayLength());
        foreach (var item in listed.GetProperty("integrations").EnumerateArray())
        {
            var name = item.GetProperty("target").GetString();
            Assert.True(OfficialIntegrationTargets.TryParse(name, out var target));
            var location = OfficialIntegrationLayout.LocateRuntimeSkill(_env, target);
            var skill = item.GetProperty("skill_state").GetString();
            if (location.IsNotApplicable)
            {
                Assert.Equal("not_applicable", skill);
                continue;
            }

            Assert.Equal("installed", skill);
            if (location.SkillFile is not null)
            {
                Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(location.SkillFile));
                continue;
            }

            var instruction = File.ReadAllText(location.InstructionFile!);
            Assert.Equal(1, CountSkillBlocks(instruction));
            var marker = IntegrationInstructionFence.Begin(PluginHostService.ProductVersion);
            var at = instruction.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0);
            Assert.StartsWith("\n# Hypa runtime", instruction[(at + marker.Length)..], StringComparison.Ordinal);
            var end = instruction.IndexOf(IntegrationInstructionFence.End(PluginHostService.ProductVersion), at, StringComparison.Ordinal);
            var block = instruction[at..end];
            Assert.DoesNotContain("name: hypa-runtime", block, StringComparison.Ordinal);
        }

        foreach (var target in OfficialIntegrationTargets.All)
        {
            var second = _service.Install(target);
            Assert.True(second.IsOk, target.WireName() + " " + (second.IsOk ? "" : second.Error.Message));
            var location = OfficialIntegrationLayout.LocateRuntimeSkill(_env, target);
            if (location.SkillFile is not null)
                Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(location.SkillFile));
            if (location.InstructionFile is not null)
                Assert.Equal(1, CountSkillBlocks(File.ReadAllText(location.InstructionFile)));
        }
    }

    [Fact]
    public void Environment_overrides_move_the_runtime_skill_or_block()
    {
        AssertOverride(
            OfficialIntegrationTarget.Claude,
            OfficialIntegrationLayout.ClaudeConfigDir,
            Path.Combine(_home, "override-claude"));
        AssertOverride(
            OfficialIntegrationTarget.Codex,
            OfficialIntegrationLayout.CodexHome,
            Path.Combine(_home, "override-codex"));
        AssertOverride(
            OfficialIntegrationTarget.Copilot,
            OfficialIntegrationLayout.CopilotHome,
            Path.Combine(_home, "override-copilot"));
        AssertOverride(
            OfficialIntegrationTarget.Cursor,
            OfficialIntegrationLayout.CursorConfigDir,
            Path.Combine(_home, "override-cursor"));
        AssertOverride(
            OfficialIntegrationTarget.Pi,
            OfficialIntegrationLayout.PiCodingAgentDir,
            Path.Combine(_home, "override-pi"));
        var ompAgent = Path.Combine(_home, "override-omp");
        Directory.CreateDirectory(ompAgent);
        _env.Variables.Clear();
        _env.Variables[OfficialIntegrationLayout.PiCodingAgentDir] = ompAgent;
        var ompSkillUnderCodingDir = OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Omp);
        Assert.Equal(
            Path.Combine(ompAgent, "skills", "hypa-runtime", "SKILL.md"),
            ompSkillUnderCodingDir);
        var shared = _service.Install(OfficialIntegrationTarget.Omp);
        Assert.False(shared.IsOk);
        _env.Variables.Clear();
        _env.Variables[OfficialIntegrationLayout.OmpConfigDir] = "omp-alt";
        Directory.CreateDirectory(Path.Combine(_home, "omp-alt", "agent"));
        var omp = _service.Install(OfficialIntegrationTarget.Omp);
        Assert.True(omp.IsOk, omp.IsOk ? "" : omp.Error.Message);
        var ompSkill = OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Omp);
        Assert.Equal(
            Path.Combine(_home, "omp-alt", "agent", "skills", "hypa-runtime", "SKILL.md"),
            ompSkill);
        Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(ompSkill!));
        AssertOverride(
            OfficialIntegrationTarget.Grok,
            OfficialIntegrationLayout.GrokHome,
            Path.Combine(_home, "override-grok-home"));
        AssertOverride(
            OfficialIntegrationTarget.Grok,
            OfficialIntegrationLayout.GrokConfigDir,
            Path.Combine(_home, "override-grok-config"));
        AssertOverride(
            OfficialIntegrationTarget.Devin,
            OfficialIntegrationLayout.XdgConfigHome,
            Path.Combine(_home, "override-xdg"),
            configDir: Path.Combine(_home, "override-xdg", "devin"));
        AssertOverride(
            OfficialIntegrationTarget.Kimi,
            OfficialIntegrationLayout.KimiCodeHome,
            Path.Combine(_home, "override-kimi"));
        AssertOverride(
            OfficialIntegrationTarget.Hermes,
            OfficialIntegrationLayout.HermesHome,
            Path.Combine(_home, "override-hermes"));
        AssertOverride(
            OfficialIntegrationTarget.Qodercli,
            OfficialIntegrationLayout.QoderConfigDir,
            Path.Combine(_home, "override-qoder"));
        AssertOverride(
            OfficialIntegrationTarget.Qwen,
            OfficialIntegrationLayout.QwenHome,
            Path.Combine(_home, "override-qwen"));
        AssertOverride(
            OfficialIntegrationTarget.AntigravityCli,
            OfficialIntegrationLayout.AntigravityCliConfigDir,
            Path.Combine(_home, "override-agy"));
        Directory.CreateDirectory(Path.Combine(_home, ".config", "kilo"));
        AssertOverride(
            OfficialIntegrationTarget.Kilo,
            OfficialIntegrationLayout.KiloConfigDir,
            Path.Combine(_home, "override-kilo"));
    }

    [Fact]
    public void Instruction_fence_replaces_an_older_block_and_remove_keeps_user_text()
    {
        const string prefix = "alpha line\n";
        const string suffix = "omega line\n";
        var stale = prefix + "<!-- hypa-runtime 0.0.1 -->\nstale guidance\n<!-- /hypa-runtime 0.0.1 -->" + suffix;
        var body = RuntimeSkillText.WithoutFrontMatter(RuntimeSkillText.Read());
        var updated = IntegrationInstructionFence.Insert(stale, PluginHostService.ProductVersion, body);
        Assert.StartsWith(prefix, updated);
        Assert.EndsWith(suffix, updated);
        Assert.Equal(1, CountSkillBlocks(updated));
        Assert.DoesNotContain("hypa-runtime 0.0.1", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("stale guidance", updated, StringComparison.Ordinal);
        Assert.Equal(updated, IntegrationInstructionFence.Insert(updated, PluginHostService.ProductVersion, body));
        Assert.Equal(prefix + suffix, IntegrationInstructionFence.Remove(updated));
    }

    [Fact]
    public async Task Kilo_skill_install_reports_status_is_idempotent_and_uninstalls()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".config", "kilo"));
        var sibling = Path.Combine(_home, ".kilo", "skills", "user-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllText(sibling, "user skill\n");
        var notesDir = Path.Combine(_home, ".kilo", "notes.md");
        File.WriteAllText(notesDir, "keep notes\n");

        var before = await ListIntegrationsAsync();
        Assert.Equal("missing", SingleTarget(before, "kilo").GetProperty("skill_state").GetString());

        var installed = _service.Install(OfficialIntegrationTarget.Kilo);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Kilo);
        Assert.Equal(
            Path.Combine(_home, ".kilo", "skills", "hypa-runtime", "SKILL.md"),
            skill);
        Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(skill!));
        var after = await ListIntegrationsAsync();
        Assert.Equal("installed", SingleTarget(after, "kilo").GetProperty("skill_state").GetString());

        File.WriteAllText(skill!, "stale skill\n");
        var stale = await ListIntegrationsAsync();
        Assert.Equal("outdated", SingleTarget(stale, "kilo").GetProperty("skill_state").GetString());
        Assert.True(_service.Install(OfficialIntegrationTarget.Kilo).IsOk);
        Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(skill!));

        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(skill!, stamp);
        var second = _service.Install(OfficialIntegrationTarget.Kilo);
        Assert.True(second.IsOk, second.IsOk ? "" : second.Error.Message);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(skill!));
        Assert.Contains(second.Value.Messages, message => message.Contains("unchanged", StringComparison.Ordinal));
        Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(skill!));

        var removed = _service.Uninstall(OfficialIntegrationTarget.Kilo);
        Assert.True(removed.IsOk, removed.IsOk ? "" : removed.Error.Message);
        Assert.False(File.Exists(skill!));
        Assert.Equal("user skill\n", File.ReadAllText(sibling));
        Assert.Equal("keep notes\n", File.ReadAllText(notesDir));
        var gone = await ListIntegrationsAsync();
        Assert.Equal("missing", SingleTarget(gone, "kilo").GetProperty("skill_state").GetString());
    }

    [Fact]
    public async Task Antigravity_cli_skill_install_reports_status_is_idempotent_and_uninstalls()
    {
        var config = Path.Combine(_home, ".gemini", "config");
        Directory.CreateDirectory(config);
        var rules = Path.Combine(config, "GEMINI.md");
        const string userRules = "alpha line\nomega line\n";
        File.WriteAllText(rules, userRules);
        var sibling = Path.Combine(_home, ".gemini", "antigravity-cli", "skills", "user-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllText(sibling, "user skill\n");

        var before = await ListIntegrationsAsync();
        Assert.Equal("missing", SingleTarget(before, "antigravity_cli").GetProperty("skill_state").GetString());

        var installed = _service.Install(OfficialIntegrationTarget.AntigravityCli);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.AntigravityCli);
        Assert.Equal(
            Path.Combine(_home, ".gemini", "antigravity-cli", "skills", "hypa-runtime", "SKILL.md"),
            skill);
        Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(skill!));
        Assert.Equal(userRules, File.ReadAllText(rules));
        var after = await ListIntegrationsAsync();
        Assert.Equal("installed", SingleTarget(after, "antigravity_cli").GetProperty("skill_state").GetString());

        File.WriteAllText(skill!, "stale skill\n");
        var stale = await ListIntegrationsAsync();
        Assert.Equal("outdated", SingleTarget(stale, "antigravity_cli").GetProperty("skill_state").GetString());
        Assert.True(_service.Install(OfficialIntegrationTarget.AntigravityCli).IsOk);
        Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(skill!));

        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(skill!, stamp);
        var second = _service.Install(OfficialIntegrationTarget.AntigravityCli);
        Assert.True(second.IsOk, second.IsOk ? "" : second.Error.Message);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(skill!));
        Assert.Contains(second.Value.Messages, message => message.Contains("unchanged", StringComparison.Ordinal));
        Assert.Single(Directory.GetFiles(
            Path.GetDirectoryName(skill!)!,
            OfficialIntegrationLayout.RuntimeSkillFileName,
            SearchOption.TopDirectoryOnly));

        var removed = _service.Uninstall(OfficialIntegrationTarget.AntigravityCli);
        Assert.True(removed.IsOk, removed.IsOk ? "" : removed.Error.Message);
        Assert.False(File.Exists(skill!));
        Assert.Equal("user skill\n", File.ReadAllText(sibling));
        Assert.Equal(userRules, File.ReadAllText(rules));
        var gone = await ListIntegrationsAsync();
        Assert.Equal("missing", SingleTarget(gone, "antigravity_cli").GetProperty("skill_state").GetString());
    }

    [Fact]
    public void Uninstall_claude_removes_runtime_skill_and_keeps_compression_skill()
    {
        var claude = Path.Combine(_home, ".claude");
        Directory.CreateDirectory(claude);
        var sibling = Path.Combine(claude, "skills", "hypa", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllText(sibling, "compression\n");

        Assert.True(_service.Install(OfficialIntegrationTarget.Claude).IsOk);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Claude)!;
        var skillDir = Path.GetDirectoryName(skill)!;
        Assert.True(File.Exists(skill));

        var removed = _service.Uninstall(OfficialIntegrationTarget.Claude);
        Assert.True(removed.IsOk, removed.IsOk ? "" : removed.Error.Message);
        Assert.False(File.Exists(skill));
        Assert.False(Directory.Exists(skillDir));
        Assert.Equal("compression\n", File.ReadAllText(sibling));
    }

    [Fact]
    public void Uninstall_claude_keeps_runtime_skill_directory_when_it_holds_another_file()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Assert.True(_service.Install(OfficialIntegrationTarget.Claude).IsOk);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Claude)!;
        var skillDir = Path.GetDirectoryName(skill)!;
        var notes = Path.Combine(skillDir, "notes.txt");
        File.WriteAllText(notes, "keep\n");

        Assert.True(_service.Uninstall(OfficialIntegrationTarget.Claude).IsOk);
        Assert.False(File.Exists(skill));
        Assert.True(Directory.Exists(skillDir));
        Assert.Equal("keep\n", File.ReadAllText(notes));
    }

    private async Task<JsonElement> InstallIntegrationAsync(string target)
    {
        var app = new AppState(SessionId.New("runtime-skill-install"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            integrations: _service);
        try
        {
            return await cp.DispatchAsync(
                ProtocolMethods.IntegrationInstall,
                JsonSerializer.SerializeToElement(
                    new IntegrationTargetParams { Target = target },
                    ProtocolJsonContext.Default.IntegrationTargetParams),
                CancellationToken.None);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private async Task<JsonElement> ListIntegrationsAsync()
    {
        var app = new AppState(SessionId.New("runtime-skill"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            integrations: _service);
        try
        {
            return await cp.DispatchAsync(
                ProtocolMethods.IntegrationList,
                null,
                CancellationToken.None);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    private static JsonElement SingleTarget(JsonElement listed, string target) =>
        listed.GetProperty("integrations").EnumerateArray()
            .Single(item => item.GetProperty("target").GetString() == target);

    private static byte[] RepositorySkillBytes()
    {
        var path = Path.Combine(VtRecordingReplay.FindRepoRoot(), "skills", "hypa-runtime", "SKILL.md");
        return File.ReadAllBytes(path);
    }

    private static string ExpectedSkillBeforeInstall(string? target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        return "missing";
    }

    private void PrepareConfigDirs()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".pi", "agent"));
        Directory.CreateDirectory(Path.Combine(_home, ".omp", "agent"));
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        Directory.CreateDirectory(Path.Combine(_home, ".kimi-code"));
        Directory.CreateDirectory(Path.Combine(_home, ".copilot"));
        Directory.CreateDirectory(Path.Combine(_home, ".config", "devin"));
        Directory.CreateDirectory(Path.Combine(_home, ".factory"));
        Directory.CreateDirectory(Path.Combine(_home, ".config", "opencode"));
        Directory.CreateDirectory(Path.Combine(_home, ".config", "kilo"));
        Directory.CreateDirectory(Path.Combine(_home, ".hermes"));
        Directory.CreateDirectory(Path.Combine(_home, ".qoder"));
        Directory.CreateDirectory(Path.Combine(_home, ".qwen"));
        Directory.CreateDirectory(Path.Combine(_home, ".cursor"));
        Directory.CreateDirectory(Path.Combine(_home, ".mastracode"));
        Directory.CreateDirectory(Path.Combine(_home, ".gemini", "config"));
        Directory.CreateDirectory(Path.Combine(_home, ".grok"));
    }

    private void AssertOverride(
        OfficialIntegrationTarget target,
        string variable,
        string overridePath,
        string? configDir = null)
    {
        _env.Variables.Clear();
        var root = configDir ?? overridePath;
        Directory.CreateDirectory(root);
        _env.Variables[variable] = overridePath;
        var installed = _service.Install(target);
        Assert.True(installed.IsOk, target.WireName() + " " + (installed.IsOk ? "" : installed.Error.Message));
        var location = OfficialIntegrationLayout.LocateRuntimeSkill(_env, target);
        if (location.SkillFile is not null)
        {
            Assert.StartsWith(root, location.SkillFile, StringComparison.Ordinal);
            Assert.Equal(RepositorySkillBytes(), File.ReadAllBytes(location.SkillFile));
            return;
        }

        Assert.NotNull(location.InstructionFile);
        Assert.StartsWith(root, location.InstructionFile, StringComparison.Ordinal);
        Assert.Contains(
            IntegrationInstructionFence.Begin(PluginHostService.ProductVersion),
            File.ReadAllText(location.InstructionFile!),
            StringComparison.Ordinal);
    }

    private static int CountSkillBlocks(string text)
    {
        var marker = "<!-- " + IntegrationInstructionFence.MarkerName + " ";
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private void EnsureTargetHome(OfficialIntegrationTarget target)
    {
        var dir = target switch
        {
            OfficialIntegrationTarget.Copilot => Path.Combine(_home, ".copilot"),
            OfficialIntegrationTarget.Devin => Path.Combine(_home, ".config", "devin"),
            OfficialIntegrationTarget.Droid => Path.Combine(_home, ".factory"),
            OfficialIntegrationTarget.Opencode => Path.Combine(_home, ".config", "opencode"),
            OfficialIntegrationTarget.Kilo => Path.Combine(_home, ".config", "kilo"),
            OfficialIntegrationTarget.Hermes => Path.Combine(_home, ".hermes"),
            OfficialIntegrationTarget.Qodercli => Path.Combine(_home, ".qoder"),
            OfficialIntegrationTarget.Qwen => Path.Combine(_home, ".qwen"),
            OfficialIntegrationTarget.Cursor => Path.Combine(_home, ".cursor"),
            OfficialIntegrationTarget.Mastracode => Path.Combine(_home, ".mastracode"),
            OfficialIntegrationTarget.AntigravityCli => Path.Combine(_home, ".gemini", "config"),
            OfficialIntegrationTarget.Grok => Path.Combine(_home, ".grok"),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        Directory.CreateDirectory(dir);
    }

    [Fact]
    public void Install_detected_installs_commands_on_path_and_reports_the_rest()
    {
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(bin, "codex"), "#!/bin/sh\n");
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        var user = Path.Combine(_home, ".claude", "keep.txt");
        File.WriteAllText(user, "user-file");
        var piUser = Path.Combine(_home, ".pi", "agent", "keep.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(piUser)!);
        File.WriteAllText(piUser, "user");

        var env = new TestIntegrationEnvironment
        {
            UserHome = _home,
            PathVariable = bin,
        };
        var service = new OfficialIntegrationService(new SystemIntegrationFiles(), env, new SystemIntegrationClock());

        var installed = service.InstallDetected();
        Assert.Equal(OfficialIntegrationTargets.All.Count, installed.Count);
        foreach (var result in installed)
        {
            var line = OfficialIntegrationService.DetectedLine(result);
            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
            Assert.StartsWith(result.Target.Label() + ": ", line, StringComparison.Ordinal);
            Assert.True(result.Succeeded);
            if (result.Target is OfficialIntegrationTarget.Claude or OfficialIntegrationTarget.Codex)
            {
                Assert.DoesNotContain("not found", line, StringComparison.Ordinal);
                Assert.True(result.Messages.Count > 0);
            }
            else
            {
                Assert.Contains("not found", line, StringComparison.Ordinal);
            }
        }

        Assert.True(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(env)));
        Assert.True(File.Exists(OfficialIntegrationLayout.CodexOwnedFile(env)));
        var claudeSkill = OfficialIntegrationLayout.RuntimeSkillFile(env, OfficialIntegrationTarget.Claude);
        var codexSkill = OfficialIntegrationLayout.RuntimeSkillFile(env, OfficialIntegrationTarget.Codex);
        Assert.NotNull(claudeSkill);
        Assert.NotNull(codexSkill);
        Assert.Equal(RuntimeSkillText.Read(), File.ReadAllText(claudeSkill));
        Assert.Equal(RuntimeSkillText.Read(), File.ReadAllText(codexSkill));
        Assert.Equal("user-file", File.ReadAllText(user));

        var removed = service.UninstallDetected();
        Assert.Equal(installed.Count, removed.Count);
        Assert.False(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(env)));
        Assert.False(File.Exists(OfficialIntegrationLayout.CodexOwnedFile(env)));
        Assert.False(File.Exists(claudeSkill));
        Assert.False(File.Exists(codexSkill));
        Assert.Equal("user-file", File.ReadAllText(user));
        Assert.Equal("user", File.ReadAllText(piUser));
        foreach (var result in removed)
        {
            var line = OfficialIntegrationService.DetectedLine(result);
            Assert.DoesNotContain("\n", line, StringComparison.Ordinal);
            Assert.True(result.Succeeded);
            if (result.Target is not (OfficialIntegrationTarget.Claude or OfficialIntegrationTarget.Codex))
                Assert.Contains("not found", line, StringComparison.Ordinal);
        }
    }

    [SkippableFact]
    public async Task Install_detected_keeps_a_failed_target_and_the_cli_exits_1()
    {
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(bin, "codex"), "#!/bin/sh\n");
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        var service = ServiceOnPath(bin);

        var installed = service.Service.InstallDetected();
        var claude = Assert.Single(installed, result => result.Target == OfficialIntegrationTarget.Claude);
        var codex = Assert.Single(installed, result => result.Target == OfficialIntegrationTarget.Codex);
        var pi = Assert.Single(installed, result => result.Target == OfficialIntegrationTarget.Pi);
        Assert.False(claude.Succeeded);
        Assert.Contains("claude directory not found", string.Join("; ", claude.Messages), StringComparison.Ordinal);
        Assert.Contains("press Enter again", string.Join("; ", claude.Messages), StringComparison.Ordinal);
        Assert.True(codex.Succeeded);
        Assert.DoesNotContain("not found", string.Join("; ", codex.Messages), StringComparison.Ordinal);
        Assert.True(pi.Succeeded);
        Assert.Contains("not found", string.Join("; ", pi.Messages), StringComparison.Ordinal);
        Assert.True(File.Exists(OfficialIntegrationLayout.CodexOwnedFile(service.Env)));
        Assert.False(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(service.Env)));

        var run = await RunIntegrationCliAsync(service.Service, "install");
        Assert.Equal(1, run.Code);
        Assert.Contains("error -32603: integration install failed", run.Stderr, StringComparison.Ordinal);
        var claudeLine = Assert.Single(Lines(run.Stdout), line => line.StartsWith("claude:", StringComparison.Ordinal));
        var codexLine = Assert.Single(Lines(run.Stdout), line => line.StartsWith("codex:", StringComparison.Ordinal));
        var piLine = Assert.Single(Lines(run.Stdout), line => line.StartsWith("pi:", StringComparison.Ordinal));
        Assert.Contains("claude directory not found", claudeLine, StringComparison.Ordinal);
        Assert.Contains("press Enter again", claudeLine, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", codexLine, StringComparison.Ordinal);
        Assert.Contains("not found", piLine, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Install_detected_exits_0_when_targets_succeed_or_are_not_found()
    {
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(bin, "codex"), "#!/bin/sh\n");
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        var service = ServiceOnPath(bin);

        var run = await RunIntegrationCliAsync(service.Service, "install");
        Assert.Equal(0, run.Code);
        Assert.DoesNotContain("error -32603", run.Stderr, StringComparison.Ordinal);
        var claudeLine = Assert.Single(Lines(run.Stdout), line => line.StartsWith("claude:", StringComparison.Ordinal));
        var piLine = Assert.Single(Lines(run.Stdout), line => line.StartsWith("pi:", StringComparison.Ordinal));
        Assert.DoesNotContain("not found", claudeLine, StringComparison.Ordinal);
        Assert.Contains("not found", piLine, StringComparison.Ordinal);
        Assert.True(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(service.Env)));
        Assert.True(File.Exists(OfficialIntegrationLayout.CodexOwnedFile(service.Env)));
        Assert.False(File.Exists(OfficialIntegrationLayout.PiOwnedFile(service.Env)));
    }

    [SkippableFact]
    public async Task Install_claude_installs_only_claude()
    {
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(bin, "codex"), "#!/bin/sh\n");
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        var service = ServiceOnPath(bin);

        var run = await RunIntegrationCliAsync(service.Service, "install", "claude");

        Assert.Equal(0, run.Code);
        Assert.Contains("\"target\":\"claude\"", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\"outcomes\"", run.Stdout, StringComparison.Ordinal);
        Assert.True(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(service.Env)));
        Assert.False(File.Exists(OfficialIntegrationLayout.CodexOwnedFile(service.Env)));
        Assert.False(File.Exists(OfficialIntegrationLayout.PiOwnedFile(service.Env)));
    }

    [SkippableFact]
    public async Task Uninstall_with_no_target_exits_with_the_usage_status()
    {
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        var service = ServiceOnPath(bin);

        var run = await RunIntegrationCliAsync(service.Service, "uninstall");

        Assert.Equal(4, run.Code);
        Assert.Contains("usage: hypa integration uninstall", run.Stderr, StringComparison.Ordinal);
        Assert.Contains("usage: hypa integration uninstall --all", run.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(service.Env)));
    }

    [SkippableFact]
    public async Task Uninstall_all_removes_each_detected_target_and_keeps_user_files()
    {
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(bin, "codex"), "#!/bin/sh\n");
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        var user = Path.Combine(_home, ".claude", "keep.txt");
        File.WriteAllText(user, "user-file");
        var service = ServiceOnPath(bin);

        var installed = await RunIntegrationCliAsync(service.Service, "install");
        Assert.Equal(0, installed.Code);
        Assert.True(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(service.Env)));
        Assert.True(File.Exists(OfficialIntegrationLayout.CodexOwnedFile(service.Env)));

        var removed = await RunIntegrationCliAsync(service.Service, "uninstall", "--all");
        Assert.Equal(0, removed.Code);
        Assert.Contains("claude:", removed.Stdout, StringComparison.Ordinal);
        Assert.Contains("not found", removed.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(service.Env)));
        Assert.False(File.Exists(OfficialIntegrationLayout.CodexOwnedFile(service.Env)));
        Assert.Equal("user-file", File.ReadAllText(user));
    }

    [Fact]
    public void Plan_only_lists_override_paths_and_writes_nothing()
    {
        var custom = Path.Combine(Path.GetTempPath(), "hypa-plan-" + Guid.NewGuid().ToString("N"));
        _env.Variables[OfficialIntegrationLayout.ClaudeConfigDir] = custom;
        var planned = _service.Install(OfficialIntegrationTarget.Claude, planOnly: true);
        Assert.True(planned.IsOk, planned.IsOk ? "" : planned.Error.Message);
        var text = string.Join('\n', planned.Value.Messages);
        var hook = OfficialIntegrationLayout.ClaudeOwnedFile(_env);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Claude);
        Assert.NotNull(skill);
        Assert.StartsWith(custom, hook, StringComparison.Ordinal);
        Assert.Contains(IntegrationConsentText.DisplayPath(_env, hook), text, StringComparison.Ordinal);
        Assert.Contains(IntegrationConsentText.DisplayPath(_env, skill), text, StringComparison.Ordinal);
        Assert.Contains(
            IntegrationConsentText.DisplayPath(_env, OfficialIntegrationLayout.ClaudeSettingsFile(_env)),
            text,
            StringComparison.Ordinal);
        Assert.Contains(IntegrationConsentText.HookPurpose, text, StringComparison.Ordinal);
        Assert.Contains(IntegrationConsentText.SkillPurpose, text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(custom));
    }

    [SkippableFact]
    public async Task Install_detected_dry_run_prints_planned_files_and_writes_nothing()
    {
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "claude"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(bin, "codex"), "#!/bin/sh\n");
        var service = ServiceOnPath(bin);
        var claudeHook = OfficialIntegrationLayout.ClaudeOwnedFile(service.Env);
        var codexHook = OfficialIntegrationLayout.CodexOwnedFile(service.Env);
        var claudeSkill = OfficialIntegrationLayout.RuntimeSkillFile(service.Env, OfficialIntegrationTarget.Claude);
        var codexSkill = OfficialIntegrationLayout.RuntimeSkillFile(service.Env, OfficialIntegrationTarget.Codex);
        Assert.NotNull(claudeSkill);
        Assert.NotNull(codexSkill);

        var run = await RunIntegrationCliAsync(service.Service, "install", "--dry-run");

        Assert.Equal(0, run.Code);
        Assert.DoesNotContain("{", run.Stdout, StringComparison.Ordinal);
        Assert.Contains(IntegrationConsentText.DisplayPath(service.Env, claudeHook), run.Stdout, StringComparison.Ordinal);
        Assert.Contains(IntegrationConsentText.DisplayPath(service.Env, claudeSkill), run.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            IntegrationConsentText.DisplayPath(service.Env, OfficialIntegrationLayout.ClaudeSettingsFile(service.Env)),
            run.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(IntegrationConsentText.DisplayPath(service.Env, codexHook), run.Stdout, StringComparison.Ordinal);
        Assert.Contains(IntegrationConsentText.DisplayPath(service.Env, codexSkill), run.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            IntegrationConsentText.DisplayPath(service.Env, OfficialIntegrationLayout.CodexHooksFile(service.Env)),
            run.Stdout,
            StringComparison.Ordinal);
        Assert.Contains(
            IntegrationConsentText.DisplayPath(service.Env, OfficialIntegrationLayout.CodexConfigFile(service.Env)),
            run.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("hypa integration uninstall claude", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("hypa integration uninstall codex", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("uninstall pi", run.Stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(claudeHook));
        Assert.False(File.Exists(codexHook));
        Assert.False(File.Exists(claudeSkill));
        Assert.False(File.Exists(codexSkill));
    }

    [SkippableFact]
    public async Task Install_claude_dry_run_prints_planned_files_and_writes_nothing()
    {
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        var service = ServiceOnPath(bin);
        var hook = OfficialIntegrationLayout.ClaudeOwnedFile(service.Env);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(service.Env, OfficialIntegrationTarget.Claude);
        Assert.NotNull(skill);

        var run = await RunIntegrationCliAsync(service.Service, "install", "claude", "--dry-run");

        Assert.Equal(0, run.Code);
        Assert.Contains(IntegrationConsentText.DisplayPath(service.Env, hook), run.Stdout, StringComparison.Ordinal);
        Assert.Contains(IntegrationConsentText.DisplayPath(service.Env, skill), run.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            IntegrationConsentText.DisplayPath(service.Env, OfficialIntegrationLayout.ClaudeSettingsFile(service.Env)),
            run.Stdout,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_home, ".claude")));
    }

    [SkippableFact]
    public async Task Install_omp_dry_run_names_the_legacy_pi_file_and_deletes_nothing()
    {
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        var service = ServiceOnPath(bin);
        var legacy = Path.Combine(
            OfficialIntegrationLayout.OmpExtensionDir(service.Env),
            OfficialIntegrationLayout.PiExtensionFile);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        var marker = "// " + OfficialIntegrationLayout.IdMarker + "pi\n";
        File.WriteAllText(legacy, marker);

        var run = await RunIntegrationCliAsync(service.Service, "install", "omp", "--dry-run");

        Assert.Equal(0, run.Code);
        Assert.Contains(
            IntegrationConsentText.DisplayPath(service.Env, legacy),
            run.Stdout,
            StringComparison.Ordinal);
        Assert.Contains("Hypa Pi marker", run.Stdout, StringComparison.Ordinal);
        Assert.Equal(marker, File.ReadAllText(legacy));
        Assert.False(File.Exists(OfficialIntegrationLayout.OmpOwnedFile(service.Env)));
    }

    public static IEnumerable<object[]> EveryTarget() =>
        OfficialIntegrationTargets.All.Select(target => new object[] { target });

    [Theory]
    [MemberData(nameof(EveryTarget))]
    public void Install_writes_only_files_named_in_the_consent_text(OfficialIntegrationTarget target)
    {
        var home = Path.Combine(Path.GetTempPath(), "hypa-writes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var env = new TestIntegrationEnvironment { UserHome = home };
            Directory.CreateDirectory(OfficialIntegrationLayout.InstallRoot(env, target));
            var seeds = SeedFilesTheInstallChanges(env, target);
            var before = SnapshotContents(home);
            var service = new OfficialIntegrationService(
                new SystemIntegrationFiles(),
                env,
                new SystemIntegrationClock());
            var installed = service.Install(target);
            Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);

            var consent = string.Join('\n', service.ConsentLines(target));
            var planned = OfficialIntegrationLayout.WrittenFiles(env, target)
                .Select(path => RelativeTo(home, path))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var path in OfficialIntegrationLayout.WrittenFiles(env, target))
            {
                Assert.Contains(
                    IntegrationConsentText.DisplayPath(env, path),
                    consent,
                    StringComparison.Ordinal);
            }

            foreach (var removal in OfficialIntegrationLayout.LegacyRemovals(env, target))
            {
                Assert.Contains(
                    IntegrationConsentText.DisplayPath(env, removal.Path),
                    consent,
                    StringComparison.Ordinal);
                Assert.Contains(removal.Condition, consent, StringComparison.Ordinal);
            }

            var after = SnapshotContents(home);
            foreach (var (relative, content) in after)
            {
                if (before.TryGetValue(relative, out var previous)
                    && string.Equals(previous, content, StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.True(
                    planned.Contains(relative),
                    target.WireName() + " wrote " + relative + " and the consent text omits it");
            }

            foreach (var relative in before.Keys)
            {
                if (after.ContainsKey(relative))
                    continue;
                var absolute = Path.Combine(home, relative);
                Assert.Contains(
                    IntegrationConsentText.DisplayPath(env, absolute),
                    consent,
                    StringComparison.Ordinal);
            }

            foreach (var path in seeds.Rewritten)
            {
                var relative = RelativeTo(home, path);
                Assert.True(before.ContainsKey(relative), relative);
                Assert.True(after.TryGetValue(relative, out var next), relative);
                Assert.NotEqual(before[relative], next);
            }

            foreach (var path in seeds.Removed)
            {
                var relative = RelativeTo(home, path);
                Assert.Contains(relative, before.Keys);
                Assert.DoesNotContain(relative, after.Keys);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch
            {
                // teardown
            }
        }
    }

    [Fact]
    public void Install_access_denied_includes_a_next_step()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        var service = new OfficialIntegrationService(
            new ThrowingIntegrationFiles(new UnauthorizedAccessException("Access to the path is denied.")),
            _env,
            new SystemIntegrationClock());
        var result = service.Install(OfficialIntegrationTarget.Claude);
        Assert.False(result.IsOk);
        Assert.Contains("denied", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permissions", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("press Enter again", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_io_error_includes_a_next_step()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        var service = new OfficialIntegrationService(
            new ThrowingIntegrationFiles(new IOException("disk full")),
            _env,
            new SystemIntegrationClock());
        var result = service.Install(OfficialIntegrationTarget.Claude);
        Assert.False(result.IsOk);
        Assert.Contains("disk full", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Check the directory", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("press Enter again", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_plan_rejects_and_deletes_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        var installed = _service.Install(OfficialIntegrationTarget.Claude);
        Assert.True(installed.IsOk, installed.IsOk ? "" : installed.Error.Message);
        var hook = OfficialIntegrationLayout.ClaudeOwnedFile(_env);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(_env, OfficialIntegrationTarget.Claude);
        var settings = OfficialIntegrationLayout.ClaudeSettingsFile(_env);
        Assert.True(File.Exists(hook));
        Assert.NotNull(skill);
        Assert.True(File.Exists(skill));
        Assert.True(File.Exists(settings));

        var app = new AppState(SessionId.New("uninstall-plan"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            integrations: _service);
        using var doc = JsonDocument.Parse("""{"target":"claude","plan":true}""");
        var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
            cp.DispatchAsync(ProtocolMethods.IntegrationUninstall, doc.RootElement, CancellationToken.None));
        Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
        Assert.Contains("plan applies to install", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(hook));
        Assert.True(File.Exists(skill));
        Assert.True(File.Exists(settings));
        Assert.Equal(RuntimeSkillText.Read(), File.ReadAllText(skill));
    }

    private readonly record struct InstallSeeds(IReadOnlyList<string> Rewritten, IReadOnlyList<string> Removed);

    private const string SentinelJson = "{\"keep\":\"sentinel\"}\n";

    /// <summary>
    /// Plants each file an install rewrites or removes when the file already exists.
    /// Pi and Kilo only create new files. OMP removes the legacy Pi extension.
    /// </summary>
    private static InstallSeeds SeedFilesTheInstallChanges(
        TestIntegrationEnvironment env,
        OfficialIntegrationTarget target)
    {
        var rewritten = new List<string>();
        var removed = new List<string>();
        switch (target)
        {
            case OfficialIntegrationTarget.Pi:
            case OfficialIntegrationTarget.Kilo:
                break;
            case OfficialIntegrationTarget.Omp:
                removed.Add(Seed(
                    Path.Combine(
                        OfficialIntegrationLayout.OmpExtensionDir(env),
                        OfficialIntegrationLayout.PiExtensionFile),
                    "// " + OfficialIntegrationLayout.IdMarker + "pi\n"));
                break;
            case OfficialIntegrationTarget.Claude:
                rewritten.Add(Seed(OfficialIntegrationLayout.ClaudeSettingsFile(env), SentinelJson));
                break;
            case OfficialIntegrationTarget.Codex:
                rewritten.Add(Seed(
                    OfficialIntegrationLayout.CodexHooksFile(env),
                    """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo keep"}]}]}}"""));
                rewritten.Add(Seed(OfficialIntegrationLayout.CodexConfigFile(env), "model = \"sentinel\"\n"));
                break;
            case OfficialIntegrationTarget.Copilot:
                rewritten.Add(Seed(OfficialIntegrationLayout.CopilotSettingsFile(env), SentinelJson));
                break;
            case OfficialIntegrationTarget.Devin:
                rewritten.Add(Seed(OfficialIntegrationLayout.DevinConfigFile(env), SentinelJson));
                break;
            case OfficialIntegrationTarget.Droid:
                rewritten.Add(Seed(OfficialIntegrationLayout.DroidSettingsFile(env), SentinelJson));
                rewritten.Add(Seed(
                    OfficialIntegrationLayout.DroidHooksFile(env),
                    LegacyDroidHooks(env)));
                break;
            case OfficialIntegrationTarget.Kimi:
                rewritten.Add(Seed(OfficialIntegrationLayout.KimiConfigFile(env), "model = \"sentinel\"\n"));
                break;
            case OfficialIntegrationTarget.Opencode:
                rewritten.Add(Seed(OfficialIntegrationLayout.OpenCodeTuiConfigFile(env), "{\"plugin\":[]}\n"));
                break;
            case OfficialIntegrationTarget.Hermes:
                rewritten.Add(Seed(OfficialIntegrationLayout.HermesConfigFile(env), "model: sentinel\n"));
                break;
            case OfficialIntegrationTarget.Qodercli:
                rewritten.Add(Seed(OfficialIntegrationLayout.QodercliSettingsFile(env), SentinelJson));
                break;
            case OfficialIntegrationTarget.Qwen:
                rewritten.Add(Seed(OfficialIntegrationLayout.QwenSettingsFile(env), SentinelJson));
                break;
            case OfficialIntegrationTarget.Cursor:
                rewritten.Add(Seed(
                    OfficialIntegrationLayout.CursorHooksFile(env),
                    "{\"version\":1,\"keep\":\"sentinel\"}\n"));
                break;
            case OfficialIntegrationTarget.Mastracode:
                rewritten.Add(Seed(OfficialIntegrationLayout.MastracodeHooksFile(env), SentinelJson));
                break;
            case OfficialIntegrationTarget.AntigravityCli:
                rewritten.Add(Seed(OfficialIntegrationLayout.AntigravityCliHooksFile(env), SentinelJson));
                break;
            case OfficialIntegrationTarget.Grok:
                rewritten.Add(Seed(OfficialIntegrationLayout.GrokConfigOwnedFile(env), SentinelJson));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }

        return new InstallSeeds(rewritten, removed);
    }

    private static string LegacyDroidHooks(IIntegrationEnvironment env)
    {
        var command = IntegrationHookCommand.ForUnix(
            OfficialIntegrationLayout.DroidOwnedFile(env),
            "idle");
        return "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":"
            + JsonSerializer.Serialize(command)
            + "}]}]}}\n";
    }

    private static string Seed(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, contents);
        return path;
    }

    private static Dictionary<string, string> SnapshotContents(string home)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(home))
            return files;
        foreach (var path in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories))
            files[RelativeTo(home, path)] = File.ReadAllText(path);
        return files;
    }

    private static string RelativeTo(string home, string path)
    {
        var root = Path.GetFullPath(home);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.Ordinal))
            return full.Replace('\\', '/');
        var rest = full.Length == root.Length
            ? ""
            : full[root.Length..].TrimStart(Path.DirectorySeparatorChar, '/');
        return rest.Replace('\\', '/');
    }

    private (OfficialIntegrationService Service, TestIntegrationEnvironment Env) ServiceOnPath(string bin)
    {
        var env = new TestIntegrationEnvironment
        {
            UserHome = _home,
            PathVariable = bin,
        };
        return (new OfficialIntegrationService(new SystemIntegrationFiles(), env, new SystemIntegrationClock()), env);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunIntegrationCliAsync(
        OfficialIntegrationService service,
        params string[] tail)
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix domain sockets only.");

        var dir = Path.Combine(Path.GetTempPath(), "h296cli" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var sock = Path.Combine(dir, "s.sock");
        var app = new AppState(SessionId.New("detected-install"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            integrations: service);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previous = Console.Out;
        var previousErr = Console.Error;
        try
        {
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var argv = new List<string> { "--session", "default", "--socket", sock, "integration" };
            argv.AddRange(tail);
            var code = await ControlPlaneCliCommands.Run(argv.ToArray());
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previous);
            Console.SetError(previousErr);
            await cp.ShutdownAsync(CancellationToken.None);
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // teardown
            }
        }
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private IReadOnlyList<string> HerdrNamedFiles()
    {
        if (!Directory.Exists(_home))
            return [];
        return Directory.GetFiles(_home, "*herdr*", SearchOption.AllDirectories);
    }

    private sealed class TestIntegrationEnvironment : IIntegrationEnvironment
    {
        public required string UserHome { get; init; }

        public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

        public string? PathVariable { get; init; }

        public string? GetVariable(string name) =>
            Variables.TryGetValue(name, out var value) ? value : null;

        public bool FileIsExecutable(string path) => File.Exists(path);
    }

    private sealed class ThrowingIntegrationFiles(Exception failure) : IIntegrationFiles
    {
        private readonly SystemIntegrationFiles _inner = new();

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public string ReadAllText(string path) => _inner.ReadAllText(path);

        public void WriteAllText(string path, string contents) => throw failure;

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public bool DeleteFile(string path) => _inner.DeleteFile(path);

        public bool DeleteDirectory(string path) => _inner.DeleteDirectory(path);

        public IReadOnlyList<string> ListDirectories(string path) => _inner.ListDirectories(path);

        public IReadOnlyList<string> ListFiles(string path) => _inner.ListFiles(path);

        public void SetUnixExecutable(string path) => _inner.SetUnixExecutable(path);
    }
}
