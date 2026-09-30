using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Infrastructure.Integrations;

namespace Hypa.AgentRuntime.Application.Integrations;

/// <summary>
// / Official agent install.
/// <c>src/integration/actions.rs</c>, with Hypa-owned names and files.
/// </summary>
public sealed partial class OfficialIntegrationService : IOfficialIntegrationService
{
    private readonly IIntegrationFiles _files;
    private readonly IIntegrationEnvironment _env;

    public OfficialIntegrationService(
        IIntegrationFiles files,
        IIntegrationEnvironment env,
        IIntegrationClock clock)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(clock);
        _files = files;
        _env = env;
    }

    public static OfficialIntegrationService CreateSystem() =>
        new(new SystemIntegrationFiles(), new SystemIntegrationEnvironment(), new SystemIntegrationClock());

    public IReadOnlyList<OfficialIntegrationStatus> ListStatuses()
    {
        var list = new OfficialIntegrationStatus[OfficialIntegrationTargets.All.Count];
        for (var i = 0; i < OfficialIntegrationTargets.All.Count; i++)
            list[i] = StatusOf(OfficialIntegrationTargets.All[i]);
        return list;
    }

    public IntegrationResult<OfficialIntegrationActionResult> Install(OfficialIntegrationTarget target, bool planOnly = false)
    {
        if (planOnly)
            return IntegrationResult<OfficialIntegrationActionResult>.Ok(Action(target, ConsentLines(target)));

        try
        {
            return IntegrationResult<OfficialIntegrationActionResult>.Ok(InstallCore(target));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return IntegrationResult<OfficialIntegrationActionResult>.Fail(
                IntegrationError.Install(IntegrationConsentText.WithNextStep(target, ex)));
        }
    }

    public IntegrationResult<OfficialIntegrationActionResult> Uninstall(OfficialIntegrationTarget target)
    {
        try
        {
            return IntegrationResult<OfficialIntegrationActionResult>.Ok(UninstallCore(target));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return IntegrationResult<OfficialIntegrationActionResult>.Fail(IntegrationError.Uninstall(ex.Message));
        }
    }

    public IReadOnlyList<OfficialIntegrationActionResult> InstallDetected(bool planOnly = false)
    {
        if (!planOnly)
            return ApplyDetected(target => Install(target));

        var planned = new List<OfficialIntegrationActionResult>();
        foreach (var target in DetectedTargets())
            planned.Add(Install(target, planOnly: true).Value);
        return planned;
    }

    public IReadOnlyList<string> ConsentLines(OfficialIntegrationTarget target) =>
        IntegrationConsentText.Lines(_env, target);

    public IReadOnlyList<OfficialIntegrationActionResult> UninstallDetected() =>
        ApplyDetected(Uninstall);

    public IReadOnlyList<OfficialIntegrationTarget> DetectedTargets()
    {
        var list = new List<OfficialIntegrationTarget>();
        foreach (var target in OfficialIntegrationTargets.All)
        {
            if (TargetCommandOnPath(target))
                list.Add(target);
        }

        return list;
    }

    public IntegrationResult<OfficialIntegrationActionResult> InstallRuntimeSkill(OfficialIntegrationTarget target)
    {
        try
        {
            var messages = new List<string>();
            InstallRuntimeGuidance(target, messages);
            return IntegrationResult<OfficialIntegrationActionResult>.Ok(Action(target, messages));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return IntegrationResult<OfficialIntegrationActionResult>.Fail(IntegrationError.Install(ex.Message));
        }
    }

    public IntegrationResult<OfficialIntegrationActionResult> RemoveRuntimeSkill(OfficialIntegrationTarget target)
    {
        try
        {
            var messages = new List<string>();
            RemoveRuntimeGuidance(target, messages);
            return IntegrationResult<OfficialIntegrationActionResult>.Ok(Action(target, messages));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return IntegrationResult<OfficialIntegrationActionResult>.Fail(IntegrationError.Uninstall(ex.Message));
        }
    }

    /// <summary>One result line for a detected install or uninstall.</summary>
    internal static string DetectedLine(OfficialIntegrationActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var body = result.Messages.Count == 0 ? "ok" : string.Join("; ", result.Messages);
        return result.Target.Label() + ": " + body;
    }

    private IReadOnlyList<OfficialIntegrationActionResult> ApplyDetected(
        Func<OfficialIntegrationTarget, IntegrationResult<OfficialIntegrationActionResult>> action)
    {
        var list = new List<OfficialIntegrationActionResult>(OfficialIntegrationTargets.All.Count);
        foreach (var target in OfficialIntegrationTargets.All)
        {
            if (!TargetCommandOnPath(target))
            {
                list.Add(Action(target, ["not found"]));
                continue;
            }

            var result = action(target);
            list.Add(result.IsOk ? result.Value : Failed(target, result.Error.Message));
        }

        return list;
    }

    private bool TargetCommandOnPath(OfficialIntegrationTarget target)
    {
        foreach (var command in target.CommandNames())
        {
            if (CommandAvailable(command))
                return true;
        }

        return false;
    }

    private OfficialIntegrationActionResult InstallCore(OfficialIntegrationTarget target)
    {
        var result = target switch
        {
            OfficialIntegrationTarget.Pi => InstallPi(),
            OfficialIntegrationTarget.Omp => InstallOmp(),
            OfficialIntegrationTarget.Claude => InstallClaude(),
            OfficialIntegrationTarget.Codex => InstallCodex(),
            OfficialIntegrationTarget.Copilot => InstallCopilot(),
            OfficialIntegrationTarget.Devin => InstallDevin(),
            OfficialIntegrationTarget.Droid => InstallDroid(),
            OfficialIntegrationTarget.Kimi => InstallKimi(),
            OfficialIntegrationTarget.Opencode => InstallOpenCode(),
            OfficialIntegrationTarget.Kilo => InstallKilo(),
            OfficialIntegrationTarget.Hermes => InstallHermes(),
            OfficialIntegrationTarget.Qodercli => InstallQodercli(),
            OfficialIntegrationTarget.Qwen => InstallQwen(),
            OfficialIntegrationTarget.Cursor => InstallCursor(),
            OfficialIntegrationTarget.Mastracode => InstallMastracode(),
            OfficialIntegrationTarget.AntigravityCli => InstallAntigravityCli(),
            OfficialIntegrationTarget.Grok => InstallGrok(),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        var messages = result.Messages.ToList();
        InstallRuntimeGuidance(target, messages);
        return Action(target, messages);
    }

    private OfficialIntegrationActionResult UninstallCore(OfficialIntegrationTarget target)
    {
        var result = target switch
        {
            OfficialIntegrationTarget.Pi => UninstallPi(),
            OfficialIntegrationTarget.Omp => UninstallOmp(),
            OfficialIntegrationTarget.Claude => UninstallClaude(),
            OfficialIntegrationTarget.Codex => UninstallCodex(),
            OfficialIntegrationTarget.Copilot => UninstallCopilot(),
            OfficialIntegrationTarget.Devin => UninstallDevin(),
            OfficialIntegrationTarget.Droid => UninstallDroid(),
            OfficialIntegrationTarget.Kimi => UninstallKimi(),
            OfficialIntegrationTarget.Opencode => UninstallOpenCode(),
            OfficialIntegrationTarget.Kilo => UninstallKilo(),
            OfficialIntegrationTarget.Hermes => UninstallHermes(),
            OfficialIntegrationTarget.Qodercli => UninstallQodercli(),
            OfficialIntegrationTarget.Qwen => UninstallQwen(),
            OfficialIntegrationTarget.Cursor => UninstallCursor(),
            OfficialIntegrationTarget.Mastracode => UninstallMastracode(),
            OfficialIntegrationTarget.AntigravityCli => UninstallAntigravityCli(),
            OfficialIntegrationTarget.Grok => UninstallGrok(),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        var messages = result.Messages.ToList();
        RemoveRuntimeGuidance(target, messages);
        return Action(target, messages);
    }

    private OfficialIntegrationStatus StatusOf(OfficialIntegrationTarget target)
    {
        var path = OfficialIntegrationLayout.OwnedFile(_env, target);
        var expected = OfficialIntegrationLayout.ExpectedVersion(target);
        var available = TargetAvailable(target);
        if (!_files.FileExists(path))
        {
            return new OfficialIntegrationStatus
            {
                Target = target,
                Path = path,
                State = OfficialIntegrationStatusKind.NotInstalled,
                InstalledVersion = null,
                ExpectedVersion = expected,
                Available = available,
                SkillState = SkillStateOf(target),
            };
        }

        var installed = OfficialIntegrationLayout.ParseInstalledVersion(_files.ReadAllText(path));
        var state = installed is int version && version >= expected
            ? OfficialIntegrationStatusKind.Current
            : OfficialIntegrationStatusKind.Outdated;
        if (state is OfficialIntegrationStatusKind.Current
            && target is OfficialIntegrationTarget.Grok
            && !GrokCompanionIsCurrent(path))
        {
            state = OfficialIntegrationStatusKind.Outdated;
        }

        if (state is OfficialIntegrationStatusKind.Current
            && target is OfficialIntegrationTarget.Opencode
            && !OpenCodeCompanionIsCurrent(path, expected))
        {
            state = OfficialIntegrationStatusKind.Outdated;
        }

        return new OfficialIntegrationStatus
        {
            Target = target,
            Path = path,
            State = state,
            InstalledVersion = installed,
            ExpectedVersion = expected,
            Available = available || state != OfficialIntegrationStatusKind.NotInstalled,
            SkillState = SkillStateOf(target),
        };
    }

    private OfficialIntegrationSkillState SkillStateOf(OfficialIntegrationTarget target)
    {
        var location = OfficialIntegrationLayout.LocateRuntimeSkill(_env, target);
        if (location.SkillFile is not null)
            return SkillFileState(location.SkillFile);
        if (location.InstructionFile is not null)
            return InstructionBlockState(location.InstructionFile);
        return OfficialIntegrationSkillState.NotApplicable;
    }

    private OfficialIntegrationSkillState SkillFileState(string path)
    {
        if (!_files.FileExists(path))
            return OfficialIntegrationSkillState.Missing;
        var installed = _files.ReadAllText(path);
        return string.Equals(installed, RuntimeSkillText.Read(), StringComparison.Ordinal)
            ? OfficialIntegrationSkillState.Installed
            : OfficialIntegrationSkillState.Outdated;
    }

    private OfficialIntegrationSkillState InstructionBlockState(string path)
    {
        if (!_files.FileExists(path))
            return OfficialIntegrationSkillState.Missing;
        var installed = _files.ReadAllText(path);
        if (!IntegrationInstructionFence.Present(installed))
            return OfficialIntegrationSkillState.Missing;
        var body = RuntimeSkillText.WithoutFrontMatter(RuntimeSkillText.Read());
        return IntegrationInstructionFence.Current(installed, PluginHostService.ProductVersion, body)
            ? OfficialIntegrationSkillState.Installed
            : OfficialIntegrationSkillState.Outdated;
    }

    private OfficialIntegrationActionResult InstallPi()
    {
        var dir = OfficialIntegrationLayout.PiExtensionDir(_env);
        EnsureExtensionDir(dir, "pi");
        var path = OfficialIntegrationLayout.PiOwnedFile(_env);
        WriteOwned(path, OfficialIntegrationAssets.PiExtension, executable: false);
        return Action(OfficialIntegrationTarget.Pi, ["installed pi integration to " + path]);
    }

    private OfficialIntegrationActionResult InstallOmp()
    {
        var dir = OfficialIntegrationLayout.OmpExtensionDir(_env);
        var piDir = OfficialIntegrationLayout.PiExtensionDir(_env);
        if (string.Equals(dir, piDir, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Pi and OMP resolve to the same extension directory at " + dir
                + "; configure separate agent directories before installing OMP");
        }

        EnsureExtensionDir(dir, "omp");
        var removedLegacy = RemoveLegacyPiExtensionFromOmpDir(dir);
        var path = OfficialIntegrationLayout.OmpOwnedFile(_env);
        WriteOwned(path, OfficialIntegrationAssets.OmpExtension, executable: false);
        var messages = new List<string>();
        if (removedLegacy)
        {
            messages.Add(
                "removed legacy pi integration from omp extension directory at "
                + Path.Combine(dir, OfficialIntegrationLayout.PiExtensionFile));
        }

        messages.Add("installed omp integration to " + path);
        return Action(OfficialIntegrationTarget.Omp, messages);
    }

    private OfficialIntegrationActionResult InstallClaude()
    {
        var dir = OfficialIntegrationLayout.ClaudeDir(_env);
        if (!_files.DirectoryExists(dir))
        {
            throw new InvalidOperationException(
                "claude directory not found at " + dir + ". install claude code first");
        }

        var hookPath = OfficialIntegrationLayout.ClaudeOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.ClaudeHook, executable: true);
        var settingsPath = OfficialIntegrationLayout.ClaudeSettingsFile(_env);
        var existing = ReadIfExists(settingsPath) ?? "{}";
        var updated = EditClaudeSettings(existing, hookPath, install: true);
        if (!string.Equals(updated, existing, StringComparison.Ordinal))
            _files.WriteAllText(settingsPath, updated);
        var messages = new List<string>
        {
            "installed claude integration hook to " + hookPath,
            "ensured claude settings at " + settingsPath,
        };
        return Action(OfficialIntegrationTarget.Claude, messages);
    }

    private OfficialIntegrationActionResult InstallCodex()
    {
        var dir = OfficialIntegrationLayout.CodexDir(_env);
        if (!_files.DirectoryExists(dir))
        {
            throw new InvalidOperationException(
                "codex config directory not found at " + dir + ". install codex first");
        }

        var hookPath = OfficialIntegrationLayout.CodexOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.CodexHook, executable: true);
        var hooksPath = OfficialIntegrationLayout.CodexHooksFile(_env);
        var hooksRoot = IntegrationHookJson.ParseObject(ReadIfExists(hooksPath) ?? "{}", "codex hooks file");
        var hooks = IntegrationHookJson.EnsureHooksObject(hooksRoot, "codex hooks file hooks");
        IntegrationHookJson.RemoveOwnedHookCommands(hooks, hookPath);
        IntegrationHookJson.EnsureCommandHook(
            hooks,
            "SessionStart",
            IntegrationHookCommand.ForUnix(hookPath, "session"),
            10,
            matcher: null);
        _files.WriteAllText(hooksPath, IntegrationHookJson.ToPrettyJson(hooksRoot) + "\n");

        var configPath = OfficialIntegrationLayout.CodexConfigFile(_env);
        var existingConfig = ReadIfExists(configPath) ?? "";
        var newConfig = IntegrationTomlEdit.BuildCodexConfigWithHooks(existingConfig);
        if (!string.Equals(newConfig, existingConfig, StringComparison.Ordinal))
            _files.WriteAllText(configPath, newConfig);

        return Action(
            OfficialIntegrationTarget.Codex,
            [
                "installed codex integration hook to " + hookPath,
                "ensured codex hooks at " + hooksPath,
                "ensured codex config at " + configPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallKimi()
    {
        var dir = OfficialIntegrationLayout.KimiDir(_env);
        if (!_files.DirectoryExists(dir))
        {
            throw new InvalidOperationException(
                "kimi code config directory not found at " + dir + ". install kimi code first");
        }

        var hookPath = OfficialIntegrationLayout.KimiOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.KimiHook, executable: true);
        var configPath = OfficialIntegrationLayout.KimiConfigFile(_env);
        var existing = ReadIfExists(configPath) ?? "";
        var updated = IntegrationTomlEdit.BuildKimiConfigWithHooks(existing, hookPath);
        if (!string.Equals(updated, existing, StringComparison.Ordinal))
            _files.WriteAllText(configPath, updated);
        return Action(
            OfficialIntegrationTarget.Kimi,
            [
                "installed kimi integration hook to " + hookPath,
                "ensured kimi config at " + configPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallPi()
    {
        var path = OfficialIntegrationLayout.PiOwnedFile(_env);
        var removed = _files.DeleteFile(path);
        return Action(
            OfficialIntegrationTarget.Pi,
            [
                removed
                    ? "removed pi integration extension at " + path
                    : "no pi integration extension found at " + path,
            ]);
    }

    private OfficialIntegrationActionResult UninstallOmp()
    {
        var path = OfficialIntegrationLayout.OmpOwnedFile(_env);
        var removed = _files.DeleteFile(path);
        return Action(
            OfficialIntegrationTarget.Omp,
            [
                removed
                    ? "removed omp integration extension at " + path
                    : "no omp integration extension found at " + path,
            ]);
    }

    private OfficialIntegrationActionResult UninstallClaude()
    {
        var hookPath = OfficialIntegrationLayout.ClaudeOwnedFile(_env);
        var settingsPath = OfficialIntegrationLayout.ClaudeSettingsFile(_env);
        var updatedSettings = false;
        if (_files.FileExists(settingsPath))
        {
            var existing = _files.ReadAllText(settingsPath);
            var updated = EditClaudeSettings(existing, hookPath, install: false);
            if (!string.Equals(updated, existing, StringComparison.Ordinal))
            {
                _files.WriteAllText(settingsPath, updated);
                updatedSettings = true;
            }
        }

        var removedHook = _files.DeleteFile(hookPath);
        var messages = new List<string>
        {
            removedHook ? "removed claude hook at " + hookPath : "no claude hook found at " + hookPath,
            updatedSettings
                ? "removed hypa claude hook entries from " + settingsPath
                : "no hypa claude hook entries found in " + settingsPath,
        };
        return Action(OfficialIntegrationTarget.Claude, messages);
    }

    private void InstallRuntimeGuidance(OfficialIntegrationTarget target, List<string> messages)
    {
        var location = OfficialIntegrationLayout.LocateRuntimeSkill(_env, target);
        if (location.SkillFile is not null)
        {
            InstallSkillFile(location.SkillFile, messages);
            return;
        }

        if (location.InstructionFile is not null)
            InstallInstructionBlock(location.InstructionFile, messages);
    }

    private void RemoveRuntimeGuidance(OfficialIntegrationTarget target, List<string> messages)
    {
        var location = OfficialIntegrationLayout.LocateRuntimeSkill(_env, target);
        if (location.SkillFile is not null)
        {
            RemoveSkillFile(location.SkillFile, messages);
            return;
        }

        if (location.InstructionFile is not null)
            RemoveInstructionBlock(location.InstructionFile, messages);
    }

    private void InstallSkillFile(string path, List<string> messages)
    {
        var body = RuntimeSkillText.Read();
        if (_files.FileExists(path)
            && string.Equals(_files.ReadAllText(path), body, StringComparison.Ordinal))
        {
            messages.Add("runtime skill unchanged at " + path);
            return;
        }

        WriteOwned(path, body, executable: false);
        messages.Add("installed runtime skill to " + path);
    }

    private void RemoveSkillFile(string path, List<string> messages)
    {
        var removed = _files.DeleteFile(path);
        messages.Add(removed
            ? "removed runtime skill at " + path
            : "no runtime skill found at " + path);
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !_files.DirectoryExists(dir))
            return;
        if (_files.ListFiles(dir).Count > 0 || _files.ListDirectories(dir).Count > 0)
            return;
        if (_files.DeleteDirectory(dir))
            messages.Add("removed runtime skill directory at " + dir);
    }

    private void InstallInstructionBlock(string path, List<string> messages)
    {
        var body = RuntimeSkillText.WithoutFrontMatter(RuntimeSkillText.Read());
        var version = PluginHostService.ProductVersion;
        var existing = ReadIfExists(path) ?? "";
        var updated = IntegrationInstructionFence.Insert(existing, version, body);
        if (_files.FileExists(path) && string.Equals(updated, existing, StringComparison.Ordinal))
        {
            messages.Add("runtime skill block unchanged in " + path);
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !_files.DirectoryExists(dir))
            _files.CreateDirectory(dir);
        _files.WriteAllText(path, updated);
        messages.Add("installed runtime skill block in " + path);
    }

    private void RemoveInstructionBlock(string path, List<string> messages)
    {
        if (!_files.FileExists(path))
        {
            messages.Add("no runtime skill block found in " + path);
            return;
        }

        var existing = _files.ReadAllText(path);
        var updated = IntegrationInstructionFence.Remove(existing);
        if (string.Equals(updated, existing, StringComparison.Ordinal))
        {
            messages.Add("no runtime skill block found in " + path);
            return;
        }

        _files.WriteAllText(path, updated);
        messages.Add("removed runtime skill block from " + path);
    }

    private OfficialIntegrationActionResult UninstallCodex()
    {
        var hookPath = OfficialIntegrationLayout.CodexOwnedFile(_env);
        var hooksPath = OfficialIntegrationLayout.CodexHooksFile(_env);
        var updatedHooks = false;
        if (_files.FileExists(hooksPath))
        {
            var hooksRoot = IntegrationHookJson.ParseObject(_files.ReadAllText(hooksPath), "codex hooks file");
            var hooks = IntegrationHookJson.HooksObjectIfPresent(hooksRoot, "codex hooks file hooks");
            if (hooks is not null && IntegrationHookJson.RemoveOwnedHookCommands(hooks, hookPath))
            {
                _files.WriteAllText(hooksPath, IntegrationHookJson.ToPrettyJson(hooksRoot) + "\n");
                updatedHooks = true;
            }
        }

        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.Codex,
            [
                removedHook ? "removed codex hook at " + hookPath : "no codex hook found at " + hookPath,
                updatedHooks
                    ? "removed hypa codex hook entries from " + hooksPath
                    : "no hypa codex hook entries found in " + hooksPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallKimi()
    {
        var hookPath = OfficialIntegrationLayout.KimiOwnedFile(_env);
        var configPath = OfficialIntegrationLayout.KimiConfigFile(_env);
        var updatedConfig = false;
        if (_files.FileExists(configPath))
        {
            var existing = _files.ReadAllText(configPath);
            var updated = IntegrationTomlEdit.RemoveKimiConfigBlock(existing);
            if (!string.Equals(updated, existing, StringComparison.Ordinal))
            {
                _files.WriteAllText(configPath, updated);
                updatedConfig = true;
            }
        }

        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.Kimi,
            [
                removedHook ? "removed kimi hook at " + hookPath : "no kimi hook found at " + hookPath,
                updatedConfig
                    ? "removed hypa kimi hook entries from " + configPath
                    : "no hypa kimi hook entries found in " + configPath,
            ]);
    }

    private static string EditClaudeSettings(string content, string hookPath, bool install)
    {
        var root = IntegrationHookJson.ParseObject(content, "claude settings");
        if (install)
        {
            var hooks = IntegrationHookJson.EnsureHooksObject(root, "claude settings hooks");
            IntegrationHookJson.RemoveOwnedHookCommands(hooks, hookPath);
            IntegrationHookJson.EnsureCommandHook(
                hooks,
                "SessionStart",
                IntegrationHookCommand.ForUnix(hookPath, "session"),
                10,
                "*");
            return IntegrationHookJson.ToPrettyJson(root) + "\n";
        }

        var present = IntegrationHookJson.HooksObjectIfPresent(root, "claude settings hooks");
        if (present is null)
            return content;
        if (!IntegrationHookJson.RemoveOwnedHookCommands(present, hookPath))
            return content;
        return IntegrationHookJson.ToPrettyJson(root) + "\n";
    }

    private bool RemoveLegacyPiExtensionFromOmpDir(string ompDir)
    {
        var legacyPath = Path.Combine(ompDir, OfficialIntegrationLayout.PiExtensionFile);
        if (!_files.FileExists(legacyPath))
            return false;
        var content = _files.ReadAllText(legacyPath);
        if (!content.Contains(OfficialIntegrationLayout.IdMarker + "pi", StringComparison.Ordinal))
            return false;
        return _files.DeleteFile(legacyPath);
    }

    private void EnsureExtensionDir(string dir, string agent)
    {
        if (_files.DirectoryExists(dir))
            return;
        var parent = Path.GetDirectoryName(dir);
        if (!string.IsNullOrEmpty(parent) && _files.DirectoryExists(parent))
        {
            _files.CreateDirectory(dir);
            return;
        }

        throw new InvalidOperationException(
            agent + " extension directory not found at " + dir + ". install " + agent + " first");
    }

    private void WriteOwned(string path, string contents, bool executable)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !_files.DirectoryExists(dir))
            _files.CreateDirectory(dir);
        if (_files.FileExists(path)
            && string.Equals(_files.ReadAllText(path), contents, StringComparison.Ordinal))
            return;
        _files.WriteAllText(path, contents);
        if (executable)
            _files.SetUnixExecutable(path);
    }

    private string? ReadIfExists(string path) =>
        _files.FileExists(path) ? _files.ReadAllText(path) : null;

    private bool TargetAvailable(OfficialIntegrationTarget target)
    {
        foreach (var command in target.CommandNames())
        {
            if (CommandAvailable(command))
                return true;
        }

        return target is OfficialIntegrationTarget.Codex && CodexStandaloneAvailable();
    }

    /// <summary>
    /// <c>codex_standalone_binary_available</c>.
    /// </summary>
    private bool CodexStandaloneAvailable()
    {
        var releases = Path.Combine(
            OfficialIntegrationLayout.CodexDir(_env),
            "packages",
            "standalone",
            "releases");
        foreach (var release in _files.ListDirectories(releases))
        {
            var binary = Path.Combine(release, "bin", "codex");
            if (_files.FileExists(binary) && _env.FileIsExecutable(binary))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <c>grok_hook_config_is_valid</c>.
    /// </summary>
    private bool GrokCompanionIsCurrent(string hookPath)
    {
        var configPath = OfficialIntegrationLayout.GrokConfigOwnedFile(_env);
        if (!_files.FileExists(configPath))
            return false;
        return OfficialGrokHookConfig.MatchesInstalled(_files.ReadAllText(configPath), hookPath);
    }

    /// <summary>
    /// <c>opencode_tui_integration_is_valid</c>.
    /// </summary>
    private bool OpenCodeCompanionIsCurrent(string pluginPath, int expectedVersion)
    {
        var pluginsDir = Path.GetDirectoryName(pluginPath);
        var configDir = Path.GetDirectoryName(pluginsDir);
        if (string.IsNullOrEmpty(configDir))
            return false;
        var tuiPlugin = Path.Combine(configDir, OfficialIntegrationLayout.OpenCodeTuiPluginFile);
        if (!_files.FileExists(tuiPlugin))
            return false;
        var tuiVersion = OfficialIntegrationLayout.ParseInstalledVersion(_files.ReadAllText(tuiPlugin));
        if (tuiVersion is not int version || version < expectedVersion)
            return false;
        var tuiConfig = OfficialIntegrationLayout.OpenCodeTuiConfigFile(_env);
        var content = _files.FileExists(tuiConfig) ? _files.ReadAllText(tuiConfig) : null;
        return OpenCodeTuiConfig.IsConfigured(content, OfficialIntegrationLayout.OpenCodeTuiPluginSpec);
    }

    private bool CommandAvailable(string command)
    {
        var path = _env.PathVariable;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.Combine(dir, command);
            if (_files.FileExists(candidate) && _env.FileIsExecutable(candidate))
                return true;
        }

        return false;
    }

    private void EnsureConfigDir(string dir, string message)
    {
        if (_files.DirectoryExists(dir))
            return;
        throw new InvalidOperationException(message);
    }

    private static OfficialIntegrationActionResult Action(
        OfficialIntegrationTarget target,
        IReadOnlyList<string> messages) =>
        new() { Target = target, Messages = messages };

    private static OfficialIntegrationActionResult Failed(
        OfficialIntegrationTarget target,
        string message) =>
        new()
        {
            Target = target,
            Messages = [message],
            Succeeded = false,
        };
}
