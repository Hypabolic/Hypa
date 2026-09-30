using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
// / Official install paths.
/// <c>src/integration/mod.rs</c> version constants, with Hypa-owned names.
/// </summary>
public static class OfficialIntegrationLayout
{
    public const string VersionMarker = "HYPA_INTEGRATION_VERSION=";
    public const string IdMarker = "HYPA_INTEGRATION_ID=";
    public const string PiExtensionFile = "hypa-agent-state.ts";
    public const string OmpExtensionFile = "hypa-omp-agent-state.ts";
    public const string UnixHookFile = "hypa-agent-state.sh";
    public const string QwenHookFile = "hypa-agent-session.sh";
    public const string OpenCodePluginFile = "hypa-agent-state.js";
    public const string OpenCodeTuiPluginFile = "hypa-tui-session.js";
    public const string OpenCodeTuiPluginSpec = "./hypa-tui-session.js";
    public const string KiloPluginFile = "hypa-agent-state.js";
    public const string HermesPluginDirName = "hypa-agent-state";
    public const string HermesManifestFile = "plugin.yaml";
    public const string HermesInitFile = "__init__.py";
    public const string GrokHookConfigFile = "hypa.json";
    public const string SettingsFileName = "settings.json";
    public const string HooksFileName = "hooks.json";
    public const string ConfigTomlFileName = "config.toml";
    public const string ConfigYamlFileName = "config.yaml";
    public const string ConfigJsonFileName = "config.json";
    public const string OpenCodeTuiConfigFileName = "tui.jsonc";
    public const string AntigravityHookBlockName = "hypa";
    public const string PiCodingAgentDir = "PI_CODING_AGENT_DIR";
    public const string OmpConfigDir = "PI_CONFIG_DIR";
    public const string ClaudeConfigDir = "CLAUDE_CONFIG_DIR";
    public const string RuntimeSkillDirectoryName = "hypa-runtime";
    public const string RuntimeSkillFileName = "SKILL.md";
    public const string CodexHome = "CODEX_HOME";
    public const string KimiCodeHome = "KIMI_CODE_HOME";
    public const string CopilotHome = "COPILOT_HOME";
    public const string QoderConfigDir = "QODER_CONFIG_DIR";
    public const string QwenHome = "QWEN_HOME";
    public const string CursorConfigDir = "CURSOR_CONFIG_DIR";
    public const string AntigravityCliConfigDir = "ANTIGRAVITY_CLI_CONFIG_DIR";
    public const string KiloConfigDir = "KILO_CONFIG_DIR";
    public const string GrokConfigDir = "GROK_CONFIG_DIR";
    public const string GrokHome = "GROK_HOME";
    public const string HermesHome = "HERMES_HOME";
    public const string XdgConfigHome = "XDG_CONFIG_HOME";
    public const int PiVersion = 1;
    public const int OmpVersion = 1;
    public const int ClaudeVersion = 1;
    public const int CodexVersion = 1;
    public const int CopilotVersion = 3;
    public const int DevinVersion = 2;
    public const int DroidVersion = 3;
    public const int KimiVersion = 1;
    public const int OpencodeVersion = 11;
    public const int KiloVersion = 4;
    public const int HermesVersion = 5;
    public const int QodercliVersion = 3;
    public const int QwenVersion = 1;
    public const int CursorVersion = 1;
    public const int MastracodeVersion = 2;
    public const int AntigravityCliVersion = 3;
    public const int GrokVersion = 1;
    public const string KimiBlockBegin = "# >>> hypa kimi integration";
    public const string KimiBlockEnd = "# <<< hypa kimi integration";
    public const string AskUserQuestionMatcher = "^AskUserQuestion$";
    public const string OtherToolMatcher = "^(?!AskUserQuestion$).*$";
    public const int AntigravityHookTimeoutSec = 10;
    public const int MastracodeHookTimeoutMs = 10_000;

    public static string ExpandTilde(IIntegrationEnvironment env, string path)
    {
        if (path == "~")
            return env.UserHome ?? path;
        if (path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = env.UserHome;
            if (string.IsNullOrEmpty(home))
                return path;
            return Path.Combine(home, path[2..]);
        }

        return path;
    }

    public static string ConfigDirFromEnvOrHome(
        IIntegrationEnvironment env,
        string envVar,
        params string[] homeRelative)
    {
        var overrideDir = env.GetVariable(envVar);
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return ExpandTilde(env, overrideDir.Trim());

        var home = env.UserHome ?? "";
        var path = home;
        foreach (var segment in homeRelative)
            path = Path.Combine(path, segment);
        return path;
    }

    public static string PiAgentDir(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, PiCodingAgentDir, ".pi", "agent");

    public static string PiExtensionDir(IIntegrationEnvironment env) =>
        Path.Combine(PiAgentDir(env), "extensions");

    public static string OmpAgentDir(IIntegrationEnvironment env)
    {
        var coding = env.GetVariable(PiCodingAgentDir);
        if (!string.IsNullOrWhiteSpace(coding))
            return ExpandTilde(env, coding.Trim());

        var configDir = env.GetVariable(OmpConfigDir);
        if (string.IsNullOrWhiteSpace(configDir))
            configDir = ".omp";
        return Path.Combine(env.UserHome ?? "", configDir, "agent");
    }

    public static string OmpExtensionDir(IIntegrationEnvironment env) =>
        Path.Combine(OmpAgentDir(env), "extensions");

    public static string ClaudeDir(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, ClaudeConfigDir, ".claude");

    public static string CodexDir(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, CodexHome, ".codex");

    public static string KimiDir(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, KimiCodeHome, ".kimi-code");

    public static string CopilotDir(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, CopilotHome, ".copilot");

    public static string DevinDir(IIntegrationEnvironment env)
    {
        var xdg = env.GetVariable(XdgConfigHome);
        if (!string.IsNullOrWhiteSpace(xdg))
            return Path.Combine(ExpandTilde(env, xdg.Trim()), "devin");
        return Path.Combine(env.UserHome ?? "", ".config", "devin");
    }

    public static string DroidDir(IIntegrationEnvironment env) =>
        Path.Combine(env.UserHome ?? "", ".factory");

    public static string OpenCodeDir(IIntegrationEnvironment env) =>
        Path.Combine(env.UserHome ?? "", ".config", "opencode");

    public static string KiloDir(IIntegrationEnvironment env) =>
        Path.Combine(env.UserHome ?? "", ".config", "kilo");

    public static string HermesDir(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, HermesHome, ".hermes");

    public static string HermesPluginDir(IIntegrationEnvironment env) =>
        Path.Combine(HermesDir(env), "plugins", HermesPluginDirName);

    public static string QodercliDir(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, QoderConfigDir, ".qoder");

    public static string QwenDir(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, QwenHome, ".qwen");

    public static string CursorDir(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, CursorConfigDir, ".cursor");

    public static string MastracodeDir(IIntegrationEnvironment env) =>
        Path.Combine(env.UserHome ?? "", ".mastracode");

    public static string AntigravityCliDir(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, AntigravityCliConfigDir, ".gemini", "config");

    public static string GrokDir(IIntegrationEnvironment env)
    {
        var overrideDir = env.GetVariable(GrokConfigDir);
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return ExpandTilde(env, overrideDir.Trim());
        return ConfigDirFromEnvOrHome(env, GrokHome, ".grok");
    }

    public static string PiOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(PiExtensionDir(env), PiExtensionFile);

    public static string OmpOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(OmpExtensionDir(env), OmpExtensionFile);

    public static string ClaudeOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(ClaudeDir(env), "hooks", UnixHookFile);

    /// <summary>
    /// Runtime skill file for a target whose agent reads <c>skills/hypa-runtime/SKILL.md</c>.
    /// A null path means this target does not receive a skill file.
    /// </summary>
    public static string? RuntimeSkillFile(IIntegrationEnvironment env, OfficialIntegrationTarget target) =>
        LocateRuntimeSkill(env, target).SkillFile;

    /// <summary>
    /// User instruction file for a target that receives a fenced skill block.
    /// A null path means this target does not receive a block.
    /// </summary>
    public static string? RuntimeInstructionFile(IIntegrationEnvironment env, OfficialIntegrationTarget target) =>
        LocateRuntimeSkill(env, target).InstructionFile;

    /// <summary>
    /// Where install writes the runtime skill for one target.
    /// Skill file wins when the agent documents a skill directory under the config root.
    /// Otherwise the instruction file receives a fenced block.
    /// Both null means <c>not_applicable</c>.
    /// </summary>
    public static RuntimeSkillLocation LocateRuntimeSkill(
        IIntegrationEnvironment env,
        OfficialIntegrationTarget target)
    {
        var skill = SkillFileFor(env, target);
        return new RuntimeSkillLocation(skill, skill is null ? InstructionFileFor(env, target) : null);
    }

    private static string SkillUnder(string root) =>
        Path.Combine(root, "skills", RuntimeSkillDirectoryName, RuntimeSkillFileName);

    private static string? SkillFileFor(IIntegrationEnvironment env, OfficialIntegrationTarget target) =>
        target switch
        {
            // https://pi.dev/docs/latest/skills — ~/.pi/agent/skills/
            OfficialIntegrationTarget.Pi => SkillUnder(PiAgentDir(env)),
            // https://github.com/can1357/oh-my-pi/blob/main/docs/skills.md — ~/.omp/agent/skills/
            OfficialIntegrationTarget.Omp => SkillUnder(OmpAgentDir(env)),
            // https://code.claude.com/docs/en/skills — ~/.claude/skills/
            OfficialIntegrationTarget.Claude => SkillUnder(ClaudeDir(env)),
            // https://developers.openai.com/codex/skills documents $HOME/.agents/skills as the user root.
            // The Codex loader (codex-rs/ext/skills host_roots.rs) still scans $CODEX_HOME/skills as a
            // deprecated compatibility root. Hypa uses that root so CODEX_HOME moves the install.
            OfficialIntegrationTarget.Codex => SkillUnder(CodexDir(env)),
            // https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills
            OfficialIntegrationTarget.Copilot => SkillUnder(CopilotDir(env)),
            // https://docs.devin.ai/cli/extensibility/skills/overview — ~/.config/devin/skills/
            OfficialIntegrationTarget.Devin => SkillUnder(DevinDir(env)),
            // https://docs.factory.ai/harness/skills — ~/.factory/skills/
            OfficialIntegrationTarget.Droid => SkillUnder(DroidDir(env)),
            // https://www.kimi.com/code/docs/en/kimi-code-cli/customization/skills.html
            OfficialIntegrationTarget.Kimi => SkillUnder(KimiDir(env)),
            // https://opencode.ai/docs/skills/ — ~/.config/opencode/skills/
            OfficialIntegrationTarget.Opencode => SkillUnder(OpenCodeDir(env)),
            // https://kilo.ai/docs/customize/skills — ~/.kilo/skills/<name>/SKILL.md.
            // KILO_CONFIG_DIR is scanned for {skill,skills}/**/SKILL.md and moves this root.
            OfficialIntegrationTarget.Kilo => SkillUnder(KiloSkillRoot(env)),
            // https://hermes-agent.nousresearch.com/docs/reference/skills-catalog — ~/.hermes/skills/
            OfficialIntegrationTarget.Hermes => SkillUnder(HermesDir(env)),
            // https://docs.qoder.com/cli/Skills — ~/.qoder/skills/
            OfficialIntegrationTarget.Qodercli => SkillUnder(QodercliDir(env)),
            // https://qwenlm.github.io/qwen-code-docs/en/users/features/skills/ — ~/.qwen/skills/
            OfficialIntegrationTarget.Qwen => SkillUnder(QwenDir(env)),
            // https://cursor.com/docs/skills — ~/.cursor/skills/
            OfficialIntegrationTarget.Cursor => SkillUnder(CursorDir(env)),
            // https://code.mastra.ai/configuration — ~/.mastracode/skills/
            OfficialIntegrationTarget.Mastracode => SkillUnder(MastracodeDir(env)),
            // https://antigravity.google/docs/skills — CLI global skills are
            // ~/.gemini/antigravity-cli/skills/<name>/SKILL.md.
            // ANTIGRAVITY_CLI_CONFIG_DIR moves this root under the override.
            OfficialIntegrationTarget.AntigravityCli => SkillUnder(AntigravityCliSkillRoot(env)),
            // https://docs.x.ai/build/features/skills-plugins-marketplaces — ~/.grok/skills/
            OfficialIntegrationTarget.Grok => SkillUnder(GrokDir(env)),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    /// <summary>
    /// User-level Kilo skills live in <c>~/.kilo</c>.
    /// <c>KILO_CONFIG_DIR</c> replaces that root.
    /// </summary>
    private static string KiloSkillRoot(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, KiloConfigDir, ".kilo");

    /// <summary>
    /// Antigravity CLI global skills live in <c>~/.gemini/antigravity-cli</c>.
    /// <c>ANTIGRAVITY_CLI_CONFIG_DIR</c> replaces that root.
    /// </summary>
    private static string AntigravityCliSkillRoot(IIntegrationEnvironment env) =>
        ConfigDirFromEnvOrHome(env, AntigravityCliConfigDir, ".gemini", "antigravity-cli");

    private static string? InstructionFileFor(IIntegrationEnvironment env, OfficialIntegrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(env);
        return target switch
        {
            _ => null,
        };
    }

    public static string CodexOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(CodexDir(env), UnixHookFile);

    public static string CopilotOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(CopilotDir(env), "hooks", UnixHookFile);

    public static string DevinOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(DevinDir(env), UnixHookFile);

    public static string DroidOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(DroidDir(env), "hooks", UnixHookFile);

    public static string KimiOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(KimiDir(env), "hooks", UnixHookFile);

    public static string OpenCodeOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(OpenCodeDir(env), "plugins", OpenCodePluginFile);

    public static string OpenCodeTuiOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(OpenCodeDir(env), OpenCodeTuiPluginFile);

    public static string KiloOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(KiloDir(env), "plugin", KiloPluginFile);

    public static string HermesOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(HermesPluginDir(env), HermesInitFile);

    public static string QodercliOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(QodercliDir(env), "hooks", UnixHookFile);

    public static string QwenOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(QwenDir(env), "hooks", QwenHookFile);

    public static string CursorOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(CursorDir(env), UnixHookFile);

    public static string MastracodeOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(MastracodeDir(env), "hooks", UnixHookFile);

    public static string AntigravityCliOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(AntigravityCliDir(env), "hooks", UnixHookFile);

    public static string GrokOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(GrokDir(env), "hooks", UnixHookFile);

    public static string GrokConfigOwnedFile(IIntegrationEnvironment env) =>
        Path.Combine(GrokDir(env), "hooks", GrokHookConfigFile);

    public static string ClaudeSettingsFile(IIntegrationEnvironment env) =>
        Path.Combine(ClaudeDir(env), SettingsFileName);

    public static string CodexHooksFile(IIntegrationEnvironment env) =>
        Path.Combine(CodexDir(env), HooksFileName);

    public static string CodexConfigFile(IIntegrationEnvironment env) =>
        Path.Combine(CodexDir(env), ConfigTomlFileName);

    public static string CopilotSettingsFile(IIntegrationEnvironment env) =>
        Path.Combine(CopilotDir(env), SettingsFileName);

    public static string DevinConfigFile(IIntegrationEnvironment env) =>
        Path.Combine(DevinDir(env), ConfigJsonFileName);

    public static string DroidSettingsFile(IIntegrationEnvironment env) =>
        Path.Combine(DroidDir(env), SettingsFileName);

    public static string DroidHooksFile(IIntegrationEnvironment env) =>
        Path.Combine(DroidDir(env), HooksFileName);

    public static string KimiConfigFile(IIntegrationEnvironment env) =>
        Path.Combine(KimiDir(env), ConfigTomlFileName);

    public static string OpenCodeTuiConfigFile(IIntegrationEnvironment env) =>
        Path.Combine(OpenCodeDir(env), OpenCodeTuiConfigFileName);

    public static string HermesManifestPath(IIntegrationEnvironment env) =>
        Path.Combine(HermesPluginDir(env), HermesManifestFile);

    public static string HermesConfigFile(IIntegrationEnvironment env) =>
        Path.Combine(HermesDir(env), ConfigYamlFileName);

    public static string QodercliSettingsFile(IIntegrationEnvironment env) =>
        Path.Combine(QodercliDir(env), SettingsFileName);

    public static string QwenSettingsFile(IIntegrationEnvironment env) =>
        Path.Combine(QwenDir(env), SettingsFileName);

    public static string CursorHooksFile(IIntegrationEnvironment env) =>
        Path.Combine(CursorDir(env), HooksFileName);

    public static string MastracodeHooksFile(IIntegrationEnvironment env) =>
        Path.Combine(MastracodeDir(env), HooksFileName);

    public static string AntigravityCliHooksFile(IIntegrationEnvironment env) =>
        Path.Combine(AntigravityCliDir(env), HooksFileName);

    /// <summary>
    /// Directory that must exist before install writes.
    /// Mastracode creates this directory during the write.
    /// </summary>
    public static string InstallRoot(IIntegrationEnvironment env, OfficialIntegrationTarget target) =>
        target switch
        {
            OfficialIntegrationTarget.Pi => PiAgentDir(env),
            OfficialIntegrationTarget.Omp => OmpAgentDir(env),
            OfficialIntegrationTarget.Claude => ClaudeDir(env),
            OfficialIntegrationTarget.Codex => CodexDir(env),
            OfficialIntegrationTarget.Copilot => CopilotDir(env),
            OfficialIntegrationTarget.Devin => DevinDir(env),
            OfficialIntegrationTarget.Droid => DroidDir(env),
            OfficialIntegrationTarget.Kimi => KimiDir(env),
            OfficialIntegrationTarget.Opencode => OpenCodeDir(env),
            OfficialIntegrationTarget.Kilo => KiloDir(env),
            OfficialIntegrationTarget.Hermes => HermesDir(env),
            OfficialIntegrationTarget.Qodercli => QodercliDir(env),
            OfficialIntegrationTarget.Qwen => QwenDir(env),
            OfficialIntegrationTarget.Cursor => CursorDir(env),
            OfficialIntegrationTarget.Mastracode => MastracodeDir(env),
            OfficialIntegrationTarget.AntigravityCli => AntigravityCliDir(env),
            OfficialIntegrationTarget.Grok => GrokDir(env),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    /// <summary>
    /// A file an install removes when that file contains a legacy marker.
    /// Condition is the plain clause that follows the path in the consent text.
    /// </summary>
    public readonly record struct LegacyRemoval(string Path, string Condition);

    /// <summary>
    /// Files an install may remove.
    /// The list is empty when the target removes nothing.
    /// </summary>
    public static IReadOnlyList<LegacyRemoval> LegacyRemovals(
        IIntegrationEnvironment env,
        OfficialIntegrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (target == OfficialIntegrationTarget.Omp)
        {
            return
            [
                new LegacyRemoval(
                    Path.Combine(OmpExtensionDir(env), PiExtensionFile),
                    "when that file has the Hypa Pi marker"),
            ];
        }

        return [];
    }

    /// <summary>
    /// Every file an install writes for <paramref name="target"/>.
    /// The owned file is first. The runtime skill file is last when the target has one.
    /// A path is included when install can create the file or change an existing file.
    /// </summary>
    public static IReadOnlyList<string> WrittenFiles(
        IIntegrationEnvironment env,
        OfficialIntegrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(env);
        var files = new List<string> { OwnedFile(env, target) };
        switch (target)
        {
            case OfficialIntegrationTarget.Claude:
                files.Add(ClaudeSettingsFile(env));
                break;
            case OfficialIntegrationTarget.Codex:
                files.Add(CodexHooksFile(env));
                files.Add(CodexConfigFile(env));
                break;
            case OfficialIntegrationTarget.Copilot:
                files.Add(CopilotSettingsFile(env));
                break;
            case OfficialIntegrationTarget.Devin:
                files.Add(DevinConfigFile(env));
                break;
            case OfficialIntegrationTarget.Droid:
                files.Add(DroidSettingsFile(env));
                files.Add(DroidHooksFile(env));
                break;
            case OfficialIntegrationTarget.Kimi:
                files.Add(KimiConfigFile(env));
                break;
            case OfficialIntegrationTarget.Opencode:
                files.Add(OpenCodeTuiOwnedFile(env));
                files.Add(OpenCodeTuiConfigFile(env));
                break;
            case OfficialIntegrationTarget.Hermes:
                files.Add(HermesManifestPath(env));
                files.Add(HermesConfigFile(env));
                break;
            case OfficialIntegrationTarget.Qodercli:
                files.Add(QodercliSettingsFile(env));
                break;
            case OfficialIntegrationTarget.Qwen:
                files.Add(QwenSettingsFile(env));
                break;
            case OfficialIntegrationTarget.Cursor:
                files.Add(CursorHooksFile(env));
                break;
            case OfficialIntegrationTarget.Mastracode:
                files.Add(MastracodeHooksFile(env));
                break;
            case OfficialIntegrationTarget.AntigravityCli:
                files.Add(AntigravityCliHooksFile(env));
                break;
            case OfficialIntegrationTarget.Grok:
                files.Add(GrokConfigOwnedFile(env));
                break;
        }

        var skill = RuntimeSkillFile(env, target);
        if (skill is not null)
            files.Add(skill);
        var instruction = RuntimeInstructionFile(env, target);
        if (instruction is not null)
            files.Add(instruction);
        return files;
    }

    public static int ExpectedVersion(OfficialIntegrationTarget target) =>
        target switch
        {
            OfficialIntegrationTarget.Pi => PiVersion,
            OfficialIntegrationTarget.Omp => OmpVersion,
            OfficialIntegrationTarget.Claude => ClaudeVersion,
            OfficialIntegrationTarget.Codex => CodexVersion,
            OfficialIntegrationTarget.Copilot => CopilotVersion,
            OfficialIntegrationTarget.Devin => DevinVersion,
            OfficialIntegrationTarget.Droid => DroidVersion,
            OfficialIntegrationTarget.Kimi => KimiVersion,
            OfficialIntegrationTarget.Opencode => OpencodeVersion,
            OfficialIntegrationTarget.Kilo => KiloVersion,
            OfficialIntegrationTarget.Hermes => HermesVersion,
            OfficialIntegrationTarget.Qodercli => QodercliVersion,
            OfficialIntegrationTarget.Qwen => QwenVersion,
            OfficialIntegrationTarget.Cursor => CursorVersion,
            OfficialIntegrationTarget.Mastracode => MastracodeVersion,
            OfficialIntegrationTarget.AntigravityCli => AntigravityCliVersion,
            OfficialIntegrationTarget.Grok => GrokVersion,
            _ => 1,
        };

    public static string OwnedFile(IIntegrationEnvironment env, OfficialIntegrationTarget target) =>
        target switch
        {
            OfficialIntegrationTarget.Pi => PiOwnedFile(env),
            OfficialIntegrationTarget.Omp => OmpOwnedFile(env),
            OfficialIntegrationTarget.Claude => ClaudeOwnedFile(env),
            OfficialIntegrationTarget.Codex => CodexOwnedFile(env),
            OfficialIntegrationTarget.Copilot => CopilotOwnedFile(env),
            OfficialIntegrationTarget.Devin => DevinOwnedFile(env),
            OfficialIntegrationTarget.Droid => DroidOwnedFile(env),
            OfficialIntegrationTarget.Kimi => KimiOwnedFile(env),
            OfficialIntegrationTarget.Opencode => OpenCodeOwnedFile(env),
            OfficialIntegrationTarget.Kilo => KiloOwnedFile(env),
            OfficialIntegrationTarget.Hermes => HermesOwnedFile(env),
            OfficialIntegrationTarget.Qodercli => QodercliOwnedFile(env),
            OfficialIntegrationTarget.Qwen => QwenOwnedFile(env),
            OfficialIntegrationTarget.Cursor => CursorOwnedFile(env),
            OfficialIntegrationTarget.Mastracode => MastracodeOwnedFile(env),
            OfficialIntegrationTarget.AntigravityCli => AntigravityCliOwnedFile(env),
            OfficialIntegrationTarget.Grok => GrokOwnedFile(env),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };

    public static int? ParseInstalledVersion(string content)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim().TrimStart('/').TrimStart('#').Trim();
            if (line.StartsWith(VersionMarker, StringComparison.Ordinal)
                && int.TryParse(line[VersionMarker.Length..].Trim(), out var version))
            {
                return version;
            }
        }

        return null;
    }
}
