using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application.Integrations;

public sealed partial class OfficialIntegrationService
{
    private static readonly string[] CopilotHookEvents = ["SessionStart"];
    private static readonly string[] CopilotRemovedEvents =
    [
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PostToolUseFailure",
        "Stop",
        "agentStop",
        "SessionEnd",
        "notification",
        "sessionStart",
    ];

    private static readonly (string Event, string Action)[] DevinHookEvents =
    [
        ("SessionStart", "session"),
        ("UserPromptSubmit", "session"),
        ("PreToolUse", "session"),
        ("PostToolUse", "session"),
        ("PermissionRequest", "session"),
        ("Stop", "session"),
    ];

    private static readonly (string Event, string Action)[] DevinRemovedEvents =
    [
        ("UserPromptSubmit", "working"),
        ("PreToolUse", "working"),
        ("PostToolUse", "working"),
        ("PermissionRequest", "blocked"),
        ("Stop", "idle"),
        ("SessionEnd", "release"),
    ];

    private static readonly (string Event, string Action)[] DroidHookEvents =
        [("SessionStart", "session")];

    private static readonly (string Event, string Action)[] DroidRemovedEvents =
    [
        ("SessionStart", "idle"),
        ("UserPromptSubmit", "working"),
        ("PreToolUse", "working"),
        ("PostToolUse", "working"),
        ("Notification", "blocked"),
        ("Stop", "idle"),
        ("SubagentStop", "working"),
        ("PreCompact", "working"),
        ("SessionEnd", "release"),
    ];

    private static readonly (string Event, string Action)[] QodercliHookEvents =
        [("SessionStart", "session")];

    private static readonly (string Event, string Action)[] QodercliRemovedEvents =
    [
        ("SessionStart", "idle"),
        ("UserPromptSubmit", "working"),
        ("PreToolUse", "working"),
        ("PostToolUse", "working"),
        ("PostToolUseFailure", "working"),
        ("SubagentStart", "working"),
        ("SubagentStop", "working"),
        ("PreCompact", "working"),
        ("Notification", "blocked"),
        ("PermissionRequest", "blocked"),
        ("Stop", "idle"),
        ("SessionEnd", "release"),
    ];

    private static readonly (string Event, string Action)[] QwenHookEvents =
        [("SessionStart", "session")];

    private static readonly string[] CursorRemovedEvents =
    [
        "beforeSubmitPrompt",
        "beforeShellExecution",
        "beforeMCPExecution",
        "stop",
        "sessionEnd",
    ];

    private static readonly (string Event, string Action)[] MastracodeHookEvents =
    [
        ("SessionStart", "session"),
        ("UserPromptSubmit", "working"),
        ("AgentStart", "working"),
        ("PreToolUse", "working"),
        ("PermissionRequest", "blocked"),
        ("PermissionResult", "working"),
        ("SubagentStart", "working"),
        ("SubagentEnd", "working"),
        ("Interrupt", "idle"),
        ("AgentEnd", "idle"),
        ("Stop", "idle"),
    ];

    private static readonly (string Event, string Action)[] MastracodeRemovedEvents =
    [
        ("SessionStart", "idle"),
        ("SessionEnd", "release"),
    ];

    private OfficialIntegrationActionResult InstallCopilot()
    {
        var dir = OfficialIntegrationLayout.CopilotDir(_env);
        EnsureConfigDir(
            dir,
            "copilot config directory not found at " + dir + ". install github copilot cli first");
        var hookPath = OfficialIntegrationLayout.CopilotOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.CopilotHook, executable: true);
        var settingsPath = OfficialIntegrationLayout.CopilotSettingsFile(_env);
        var root = IntegrationHookJson.ParseObject(ReadIfExists(settingsPath) ?? "{}", "copilot settings");
        var hooks = IntegrationHookJson.EnsureHooksObject(root, "copilot settings hooks");
        foreach (var eventName in CopilotRemovedEvents)
            IntegrationHookJson.RemoveDirectHookCommands(hooks, eventName, hookPath, null);
        foreach (var eventName in CopilotHookEvents)
            IntegrationHookJson.RemoveDirectHookCommands(hooks, eventName, hookPath, null);
        foreach (var eventName in CopilotHookEvents)
        {
            IntegrationHookJson.EnsureDirectCommandHook(
                hooks,
                eventName,
                IntegrationHookCommand.ForUnix(hookPath, null),
                10,
                matcher: null);
        }

        _files.WriteAllText(settingsPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        return Action(
            OfficialIntegrationTarget.Copilot,
            [
                "installed copilot integration hook to " + hookPath,
                "ensured copilot settings at " + settingsPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallCopilot()
    {
        var hookPath = OfficialIntegrationLayout.CopilotOwnedFile(_env);
        var settingsPath = OfficialIntegrationLayout.CopilotSettingsFile(_env);
        var updatedSettings = false;
        if (_files.FileExists(settingsPath))
        {
            var root = IntegrationHookJson.ParseObject(_files.ReadAllText(settingsPath), "copilot settings");
            var hooks = IntegrationHookJson.HooksObjectIfPresent(root, "copilot settings hooks");
            if (hooks is not null)
            {
                foreach (var eventName in CopilotHookEvents.Concat(CopilotRemovedEvents))
                    updatedSettings |= IntegrationHookJson.RemoveDirectHookCommands(hooks, eventName, hookPath, null);
            }

            if (updatedSettings)
                _files.WriteAllText(settingsPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        }

        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.Copilot,
            [
                removedHook ? "removed copilot hook at " + hookPath : "no copilot hook found at " + hookPath,
                updatedSettings
                    ? "removed hypa copilot hook entries from " + settingsPath
                    : "no hypa copilot hook entries found in " + settingsPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallDevin()
    {
        var dir = OfficialIntegrationLayout.DevinDir(_env);
        EnsureConfigDir(dir, "devin config directory not found at " + dir + ". install devin cli first");
        var hookPath = OfficialIntegrationLayout.DevinOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.DevinHook, executable: true);
        var settingsPath = OfficialIntegrationLayout.DevinConfigFile(_env);
        var root = IntegrationHookJson.ParseObject(ReadIfExists(settingsPath) ?? "{}", "devin settings");
        var hooks = IntegrationHookJson.EnsureHooksObject(root, "devin settings hooks");
        foreach (var (eventName, action) in DevinRemovedEvents)
            IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
        foreach (var (eventName, action) in DevinHookEvents)
            IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
        foreach (var (eventName, action) in DevinHookEvents)
        {
            IntegrationHookJson.EnsureCommandHook(
                hooks,
                eventName,
                IntegrationHookCommand.ForUnix(hookPath, action),
                10,
                matcher: null);
        }

        _files.WriteAllText(settingsPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        return Action(
            OfficialIntegrationTarget.Devin,
            [
                "installed devin integration hook to " + hookPath,
                "ensured devin settings at " + settingsPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallDevin()
    {
        var hookPath = OfficialIntegrationLayout.DevinOwnedFile(_env);
        var settingsPath = OfficialIntegrationLayout.DevinConfigFile(_env);
        var updatedSettings = false;
        if (_files.FileExists(settingsPath))
        {
            var root = IntegrationHookJson.ParseObject(_files.ReadAllText(settingsPath), "devin settings");
            var hooks = IntegrationHookJson.HooksObjectIfPresent(root, "devin settings hooks");
            if (hooks is not null)
            {
                foreach (var (eventName, action) in DevinRemovedEvents.Concat(DevinHookEvents))
                    updatedSettings |= IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
            }

            if (updatedSettings)
                _files.WriteAllText(settingsPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        }

        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.Devin,
            [
                removedHook ? "removed devin hook at " + hookPath : "no devin hook found at " + hookPath,
                updatedSettings
                    ? "removed hypa devin hook entries from " + settingsPath
                    : "no hypa devin hook entries found in " + settingsPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallDroid()
    {
        var dir = OfficialIntegrationLayout.DroidDir(_env);
        EnsureConfigDir(dir, "droid config directory not found at " + dir + ". install droid first");
        var hookPath = OfficialIntegrationLayout.DroidOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.DroidHook, executable: true);
        var settingsPath = OfficialIntegrationLayout.DroidSettingsFile(_env);
        var root = IntegrationHookJson.ParseObject(ReadIfExists(settingsPath) ?? "{}", "droid settings");
        var hooks = IntegrationHookJson.EnsureHooksObject(root, "droid settings hooks");
        IntegrationHookJson.RemoveNestedHookCommands(hooks, "SessionStart", hookPath, null);
        foreach (var (eventName, action) in DroidRemovedEvents)
            IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
        foreach (var (eventName, action) in DroidHookEvents)
            IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
        foreach (var (eventName, action) in DroidHookEvents)
        {
            IntegrationHookJson.EnsureCommandHook(
                hooks,
                eventName,
                IntegrationHookCommand.ForUnix(hookPath, action),
                10,
                matcher: null);
        }

        _files.WriteAllText(settingsPath, IntegrationHookJson.ToPrettyJson(root) + "\n");

        var hooksPath = OfficialIntegrationLayout.DroidHooksFile(_env);
        if (_files.FileExists(hooksPath))
        {
            var hooksFile = IntegrationHookJson.ParseObject(_files.ReadAllText(hooksPath), "droid hooks file");
            var legacy = IntegrationHookJson.HooksObjectIfPresent(hooksFile, "droid hooks file hooks");
            var updatedLegacy = false;
            if (legacy is not null)
            {
                updatedLegacy = IntegrationHookJson.RemoveNestedHookCommands(legacy, "SessionStart", hookPath, null);
                foreach (var (eventName, action) in DroidRemovedEvents.Concat(DroidHookEvents))
                    updatedLegacy |= IntegrationHookJson.RemoveNestedHookCommands(legacy, eventName, hookPath, action);
            }

            if (updatedLegacy)
                _files.WriteAllText(hooksPath, IntegrationHookJson.ToPrettyJson(hooksFile) + "\n");
        }

        return Action(
            OfficialIntegrationTarget.Droid,
            [
                "installed droid integration hook to " + hookPath,
                "ensured droid settings at " + settingsPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallDroid()
    {
        var hookPath = OfficialIntegrationLayout.DroidOwnedFile(_env);
        var hooksPath = OfficialIntegrationLayout.DroidHooksFile(_env);
        var settingsPath = OfficialIntegrationLayout.DroidSettingsFile(_env);
        var updatedHooks = false;
        var updatedSettings = false;
        if (_files.FileExists(hooksPath))
        {
            var hooksFile = IntegrationHookJson.ParseObject(_files.ReadAllText(hooksPath), "droid hooks file");
            var hooks = IntegrationHookJson.HooksObjectIfPresent(hooksFile, "droid hooks file hooks");
            if (hooks is not null)
            {
                updatedHooks = IntegrationHookJson.RemoveNestedHookCommands(hooks, "SessionStart", hookPath, null);
                foreach (var (eventName, action) in DroidRemovedEvents.Concat(DroidHookEvents))
                    updatedHooks |= IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
            }

            if (updatedHooks)
                _files.WriteAllText(hooksPath, IntegrationHookJson.ToPrettyJson(hooksFile) + "\n");
        }

        if (_files.FileExists(settingsPath))
        {
            var settings = IntegrationHookJson.ParseObject(_files.ReadAllText(settingsPath), "droid settings");
            var hooks = IntegrationHookJson.HooksObjectIfPresent(settings, "droid settings hooks");
            if (hooks is not null)
            {
                updatedSettings = IntegrationHookJson.RemoveNestedHookCommands(hooks, "SessionStart", hookPath, null);
                foreach (var (eventName, action) in DroidRemovedEvents.Concat(DroidHookEvents))
                    updatedSettings |= IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
            }

            if (updatedSettings)
                _files.WriteAllText(settingsPath, IntegrationHookJson.ToPrettyJson(settings) + "\n");
        }

        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.Droid,
            [
                removedHook ? "removed droid hook at " + hookPath : "no droid hook found at " + hookPath,
                updatedSettings
                    ? "removed hypa droid hook entries from " + settingsPath
                    : "no hypa droid hook entries found in " + settingsPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallOpenCode()
    {
        var dir = OfficialIntegrationLayout.OpenCodeDir(_env);
        EnsureConfigDir(dir, "opencode config directory not found at " + dir + ". install opencode first");
        var tuiPath = OfficialIntegrationLayout.OpenCodeTuiConfigFile(_env);
        OpenCodeTuiConfig.Validate(ReadIfExists(tuiPath), tuiPath);
        var pluginPath = OfficialIntegrationLayout.OpenCodeOwnedFile(_env);
        WriteOwned(pluginPath, OfficialIntegrationAssets.OpenCodePlugin, executable: false);
        var tuiPluginPath = OfficialIntegrationLayout.OpenCodeTuiOwnedFile(_env);
        WriteOwned(tuiPluginPath, OfficialIntegrationAssets.OpenCodeTuiPlugin, executable: false);
        var updated = OpenCodeTuiConfig.AddPlugin(
            ReadIfExists(tuiPath) ?? "{}\n",
            tuiPath,
            OfficialIntegrationLayout.OpenCodeTuiPluginSpec);
        _files.WriteAllText(tuiPath, updated);
        return Action(
            OfficialIntegrationTarget.Opencode,
            [
                "installed opencode integration plugin to " + pluginPath,
                "installed opencode tui plugin to " + tuiPluginPath,
                "ensured opencode tui config at " + tuiPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallOpenCode()
    {
        var tuiPath = OfficialIntegrationLayout.OpenCodeTuiConfigFile(_env);
        var pluginPath = OfficialIntegrationLayout.OpenCodeOwnedFile(_env);
        var tuiPluginPath = OfficialIntegrationLayout.OpenCodeTuiOwnedFile(_env);
        var updatedTui = false;
        if (_files.FileExists(tuiPath))
        {
            var existing = _files.ReadAllText(tuiPath);
            var updated = OpenCodeTuiConfig.RemovePlugin(
                existing,
                tuiPath,
                OfficialIntegrationLayout.OpenCodeTuiPluginSpec);
            if (updated is not null && !string.Equals(updated, existing, StringComparison.Ordinal))
            {
                _files.WriteAllText(tuiPath, updated);
                updatedTui = true;
            }
        }

        var removedPlugin = _files.DeleteFile(pluginPath);
        var removedTuiPlugin = _files.DeleteFile(tuiPluginPath);
        return Action(
            OfficialIntegrationTarget.Opencode,
            [
                removedPlugin
                    ? "removed opencode plugin at " + pluginPath
                    : "no opencode plugin found at " + pluginPath,
                removedTuiPlugin
                    ? "removed opencode tui plugin at " + tuiPluginPath
                    : "no opencode tui plugin found at " + tuiPluginPath,
                updatedTui
                    ? "removed hypa opencode tui plugin from " + tuiPath
                    : "no hypa opencode tui plugin found in " + tuiPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallKilo()
    {
        var dir = OfficialIntegrationLayout.KiloDir(_env);
        EnsureConfigDir(dir, "kilo config directory not found at " + dir + ". install kilo first");
        var pluginPath = OfficialIntegrationLayout.KiloOwnedFile(_env);
        WriteOwned(pluginPath, OfficialIntegrationAssets.KiloPlugin, executable: false);
        return Action(OfficialIntegrationTarget.Kilo, ["installed kilo integration plugin to " + pluginPath]);
    }

    private OfficialIntegrationActionResult UninstallKilo()
    {
        var pluginPath = OfficialIntegrationLayout.KiloOwnedFile(_env);
        var removed = _files.DeleteFile(pluginPath);
        return Action(
            OfficialIntegrationTarget.Kilo,
            [
                removed
                    ? "removed kilo plugin at " + pluginPath
                    : "no kilo plugin found at " + pluginPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallHermes()
    {
        var dir = OfficialIntegrationLayout.HermesDir(_env);
        EnsureConfigDir(dir, "hermes config directory not found at " + dir + ". install hermes agent first");
        var pluginDir = OfficialIntegrationLayout.HermesPluginDir(_env);
        WriteOwned(
            OfficialIntegrationLayout.HermesManifestPath(_env),
            OfficialIntegrationAssets.HermesManifest,
            executable: false);
        WriteOwned(
            Path.Combine(pluginDir, OfficialIntegrationLayout.HermesInitFile),
            OfficialIntegrationAssets.HermesInit,
            executable: false);
        var configPath = OfficialIntegrationLayout.HermesConfigFile(_env);
        var existing = ReadIfExists(configPath) ?? "";
        var updated = IntegrationHermesYaml.EnsureEnabled(existing);
        if (!string.Equals(updated, existing, StringComparison.Ordinal))
            _files.WriteAllText(configPath, updated);
        return Action(
            OfficialIntegrationTarget.Hermes,
            [
                "installed hermes integration plugin to " + pluginDir,
                "ensured hermes config at " + configPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallHermes()
    {
        var pluginDir = OfficialIntegrationLayout.HermesPluginDir(_env);
        var configPath = OfficialIntegrationLayout.HermesConfigFile(_env);
        var updatedConfig = false;
        if (_files.FileExists(configPath))
        {
            var existing = _files.ReadAllText(configPath);
            var updated = IntegrationHermesYaml.RemoveEnabled(existing);
            if (!string.Equals(updated, existing, StringComparison.Ordinal))
            {
                _files.WriteAllText(configPath, updated);
                updatedConfig = true;
            }
        }

        var removedPlugin = _files.DeleteDirectory(pluginDir);
        return Action(
            OfficialIntegrationTarget.Hermes,
            [
                removedPlugin
                    ? "removed hermes plugin at " + pluginDir
                    : "no hermes plugin found at " + pluginDir,
                updatedConfig
                    ? "removed hypa hermes plugin from " + configPath
                    : "no hypa hermes plugin found in " + configPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallQodercli()
    {
        var dir = OfficialIntegrationLayout.QodercliDir(_env);
        EnsureConfigDir(dir, "qodercli config directory not found at " + dir + ". install qodercli first");
        var hookPath = OfficialIntegrationLayout.QodercliOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.QodercliHook, executable: true);
        var settingsPath = OfficialIntegrationLayout.QodercliSettingsFile(_env);
        var root = IntegrationHookJson.ParseObject(ReadIfExists(settingsPath) ?? "{}", "qodercli settings");
        var hooks = IntegrationHookJson.EnsureHooksObject(root, "qodercli settings hooks");
        foreach (var (eventName, action) in QodercliRemovedEvents)
            IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
        foreach (var (eventName, action) in QodercliHookEvents)
            IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
        foreach (var (eventName, action) in QodercliHookEvents)
        {
            IntegrationHookJson.EnsureCommandHook(
                hooks,
                eventName,
                IntegrationHookCommand.ForUnix(hookPath, action),
                10,
                "*");
        }

        _files.WriteAllText(settingsPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        return Action(
            OfficialIntegrationTarget.Qodercli,
            [
                "installed qodercli integration hook to " + hookPath,
                "ensured qodercli settings at " + settingsPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallQodercli()
    {
        var hookPath = OfficialIntegrationLayout.QodercliOwnedFile(_env);
        var settingsPath = OfficialIntegrationLayout.QodercliSettingsFile(_env);
        var updatedSettings = false;
        if (_files.FileExists(settingsPath))
        {
            var root = IntegrationHookJson.ParseObject(_files.ReadAllText(settingsPath), "qodercli settings");
            var hooks = IntegrationHookJson.HooksObjectIfPresent(root, "qodercli settings hooks");
            if (hooks is not null)
            {
                foreach (var (eventName, action) in QodercliRemovedEvents.Concat(QodercliHookEvents))
                    updatedSettings |= IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
            }

            if (updatedSettings)
                _files.WriteAllText(settingsPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        }

        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.Qodercli,
            [
                removedHook ? "removed qodercli hook at " + hookPath : "no qodercli hook found at " + hookPath,
                updatedSettings
                    ? "removed hypa qodercli hook entries from " + settingsPath
                    : "no hypa qodercli hook entries found in " + settingsPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallQwen()
    {
        var dir = OfficialIntegrationLayout.QwenDir(_env);
        EnsureConfigDir(dir, "qwen code config directory not found at " + dir + ". install qwen code first");
        var hookPath = OfficialIntegrationLayout.QwenOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.QwenHook, executable: true);
        var settingsPath = OfficialIntegrationLayout.QwenSettingsFile(_env);
        var root = IntegrationHookJson.ParseObject(ReadIfExists(settingsPath) ?? "{}", "qwen settings");
        var hooks = IntegrationHookJson.EnsureHooksObject(root, "qwen settings hooks");
        foreach (var (eventName, action) in QwenHookEvents)
        {
            IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
            IntegrationHookJson.EnsureCommandHook(
                hooks,
                eventName,
                IntegrationHookCommand.ForUnix(hookPath, action),
                10_000,
                "*");
        }

        _files.WriteAllText(settingsPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        return Action(
            OfficialIntegrationTarget.Qwen,
            [
                "installed qwen integration hook to " + hookPath,
                "ensured qwen settings at " + settingsPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallQwen()
    {
        var hookPath = OfficialIntegrationLayout.QwenOwnedFile(_env);
        var settingsPath = OfficialIntegrationLayout.QwenSettingsFile(_env);
        var updatedSettings = false;
        if (_files.FileExists(settingsPath))
        {
            var root = IntegrationHookJson.ParseObject(_files.ReadAllText(settingsPath), "qwen settings");
            var hooks = IntegrationHookJson.HooksObjectIfPresent(root, "qwen settings hooks");
            if (hooks is not null)
            {
                foreach (var (eventName, action) in QwenHookEvents)
                    updatedSettings |= IntegrationHookJson.RemoveNestedHookCommands(hooks, eventName, hookPath, action);
            }

            if (updatedSettings)
                _files.WriteAllText(settingsPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        }

        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.Qwen,
            [
                removedHook ? "removed qwen hook at " + hookPath : "no qwen hook found at " + hookPath,
                updatedSettings
                    ? "removed hypa qwen hook entries from " + settingsPath
                    : "no hypa qwen hook entries found in " + settingsPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallCursor()
    {
        var dir = OfficialIntegrationLayout.CursorDir(_env);
        EnsureConfigDir(
            dir,
            "cursor config directory not found at " + dir + ". install cursor agent cli first");
        var hookPath = OfficialIntegrationLayout.CursorOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.CursorHook, executable: true);
        var hooksPath = OfficialIntegrationLayout.CursorHooksFile(_env);
        var root = IntegrationHookJson.ParseObject(ReadIfExists(hooksPath) ?? """{"version":1}""", "cursor hooks file");
        if (root["version"] is null)
            root["version"] = 1;
        var hooks = IntegrationHookJson.EnsureHooksObject(root, "cursor hooks file hooks");
        var sessionCommand = IntegrationHookCommand.ForUnix(hookPath, "session");
        foreach (var eventName in CursorRemovedEvents)
            IntegrationHookJson.RemoveSimpleCommandHook(hooks, eventName, sessionCommand);
        IntegrationHookJson.EnsureSimpleCommandHook(hooks, "sessionStart", sessionCommand);
        _files.WriteAllText(hooksPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        return Action(
            OfficialIntegrationTarget.Cursor,
            [
                "installed cursor integration hook to " + hookPath,
                "ensured cursor hooks at " + hooksPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallCursor()
    {
        var hookPath = OfficialIntegrationLayout.CursorOwnedFile(_env);
        var hooksPath = OfficialIntegrationLayout.CursorHooksFile(_env);
        var updatedHooks = false;
        if (_files.FileExists(hooksPath))
        {
            var root = IntegrationHookJson.ParseObject(_files.ReadAllText(hooksPath), "cursor hooks file");
            var hooks = IntegrationHookJson.HooksObjectIfPresent(root, "cursor hooks file hooks");
            if (hooks is not null)
            {
                var sessionCommand = IntegrationHookCommand.ForUnix(hookPath, "session");
                updatedHooks |= IntegrationHookJson.RemoveSimpleCommandHook(hooks, "sessionStart", sessionCommand);
                foreach (var eventName in CursorRemovedEvents)
                    updatedHooks |= IntegrationHookJson.RemoveSimpleCommandHook(hooks, eventName, sessionCommand);
            }

            if (updatedHooks)
                _files.WriteAllText(hooksPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        }

        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.Cursor,
            [
                removedHook ? "removed cursor hook at " + hookPath : "no cursor hook found at " + hookPath,
                updatedHooks
                    ? "removed hypa cursor hook entries from " + hooksPath
                    : "no hypa cursor hook entries found in " + hooksPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallMastracode()
    {
        var hookPath = OfficialIntegrationLayout.MastracodeOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.MastracodeHook, executable: true);
        var hooksPath = OfficialIntegrationLayout.MastracodeHooksFile(_env);
        var root = IntegrationHookJson.ParseObject(ReadIfExists(hooksPath) ?? "{}", "mastracode hooks file");
        foreach (var (eventName, action) in MastracodeRemovedEvents)
        {
            IntegrationHookJson.RemoveFlatCommandHook(
                root,
                eventName,
                IntegrationHookCommand.ForUnix(hookPath, action));
        }

        foreach (var (eventName, action) in MastracodeHookEvents)
        {
            var command = IntegrationHookCommand.ForUnix(hookPath, action);
            IntegrationHookJson.RemoveFlatCommandHook(root, eventName, command);
            IntegrationHookJson.EnsureFlatCommandHook(
                root,
                eventName,
                command,
                OfficialIntegrationLayout.MastracodeHookTimeoutMs,
                "Report MastraCode agent state to Hypa");
        }

        _files.WriteAllText(hooksPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        return Action(
            OfficialIntegrationTarget.Mastracode,
            [
                "installed mastracode integration hook to " + hookPath,
                "ensured mastracode hooks at " + hooksPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallMastracode()
    {
        var hookPath = OfficialIntegrationLayout.MastracodeOwnedFile(_env);
        var hooksPath = OfficialIntegrationLayout.MastracodeHooksFile(_env);
        var updatedHooks = false;
        if (_files.FileExists(hooksPath))
        {
            var root = IntegrationHookJson.ParseObject(_files.ReadAllText(hooksPath), "mastracode hooks file");
            foreach (var (eventName, action) in MastracodeHookEvents.Concat(MastracodeRemovedEvents))
            {
                updatedHooks |= IntegrationHookJson.RemoveFlatCommandHook(
                    root,
                    eventName,
                    IntegrationHookCommand.ForUnix(hookPath, action));
            }

            if (updatedHooks)
                _files.WriteAllText(hooksPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        }

        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.Mastracode,
            [
                removedHook ? "removed mastracode hook at " + hookPath : "no mastracode hook found at " + hookPath,
                updatedHooks
                    ? "removed hypa mastracode hook entries from " + hooksPath
                    : "no hypa mastracode hook entries found in " + hooksPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallAntigravityCli()
    {
        var dir = OfficialIntegrationLayout.AntigravityCliDir(_env);
        EnsureConfigDir(
            dir,
            "antigravity cli config directory not found at " + dir + ". install antigravity cli first");
        var hookPath = OfficialIntegrationLayout.AntigravityCliOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.AntigravityCliHook, executable: true);
        var hooksPath = OfficialIntegrationLayout.AntigravityCliHooksFile(_env);
        var root = IntegrationHookJson.ParseObject(ReadIfExists(hooksPath) ?? "{}", "antigravity cli hooks file");
        var handler = new JsonObject
        {
            ["type"] = "command",
            ["command"] = IntegrationHookCommand.ForUnix(hookPath, "session"),
            ["timeout"] = OfficialIntegrationLayout.AntigravityHookTimeoutSec,
        };
        var handlers = new JsonArray();
        handlers.Add((JsonNode)handler);
        var block = new JsonObject { ["PreInvocation"] = handlers };
        root[OfficialIntegrationLayout.AntigravityHookBlockName] = block;
        _files.WriteAllText(hooksPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        return Action(
            OfficialIntegrationTarget.AntigravityCli,
            [
                "installed antigravity-cli integration hook to " + hookPath,
                "ensured antigravity-cli hooks at " + hooksPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallAntigravityCli()
    {
        var hookPath = OfficialIntegrationLayout.AntigravityCliOwnedFile(_env);
        var hooksPath = OfficialIntegrationLayout.AntigravityCliHooksFile(_env);
        var updatedHooks = false;
        if (_files.FileExists(hooksPath))
        {
            var root = IntegrationHookJson.ParseObject(
                _files.ReadAllText(hooksPath),
                "antigravity cli hooks file");
            updatedHooks = root.Remove(OfficialIntegrationLayout.AntigravityHookBlockName);
            if (updatedHooks)
                _files.WriteAllText(hooksPath, IntegrationHookJson.ToPrettyJson(root) + "\n");
        }

        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.AntigravityCli,
            [
                removedHook
                    ? "removed antigravity-cli hook at " + hookPath
                    : "no antigravity-cli hook found at " + hookPath,
                updatedHooks
                    ? "removed hypa antigravity-cli hook entries from " + hooksPath
                    : "no hypa antigravity-cli hook entries found in " + hooksPath,
            ]);
    }

    private OfficialIntegrationActionResult InstallGrok()
    {
        var dir = OfficialIntegrationLayout.GrokDir(_env);
        EnsureConfigDir(dir, "grok config directory not found at " + dir + ". install grok cli first");
        var hookPath = OfficialIntegrationLayout.GrokOwnedFile(_env);
        WriteOwned(hookPath, OfficialIntegrationAssets.GrokHook, executable: true);
        var configPath = OfficialIntegrationLayout.GrokConfigOwnedFile(_env);
        _files.WriteAllText(configPath, OfficialGrokHookConfig.Format(hookPath));
        return Action(
            OfficialIntegrationTarget.Grok,
            [
                "installed grok integration hook to " + hookPath,
                "installed grok hook config to " + configPath,
            ]);
    }

    private OfficialIntegrationActionResult UninstallGrok()
    {
        var hookPath = OfficialIntegrationLayout.GrokOwnedFile(_env);
        var configPath = OfficialIntegrationLayout.GrokConfigOwnedFile(_env);
        var removedConfig = _files.DeleteFile(configPath);
        var removedHook = _files.DeleteFile(hookPath);
        return Action(
            OfficialIntegrationTarget.Grok,
            [
                removedHook ? "removed grok hook at " + hookPath : "no grok hook found at " + hookPath,
                removedConfig
                    ? "removed grok hook config at " + configPath
                    : "no grok hook config found at " + configPath,
            ]);
    }
}
