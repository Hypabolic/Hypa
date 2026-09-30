using Hypa.AgentRuntime.Application.Plugins;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Cli.Doctor;

/// <summary>
/// Generic plugin program checks plus plugin-owned <c>[[doctor]]</c> argv.
/// </summary>
public sealed class PluginDoctorCheckSource(
    IPluginRegistry registry,
    IPluginFiles files,
    IPluginPathRoots paths,
    IPluginDoctorRunner runner,
    string? hostBinary = null) : IDoctorCheckSource
{
    public static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(5);

    public IEnumerable<IDoctorCheck> GetChecks()
    {
        var loaded = registry.Load();
        if (!loaded.IsOk)
        {
            yield return new StaticPluginDoctorCheck(
                "plugins: registry",
                "registry unreadable",
                DoctorStatus.Warn,
                loaded.Error.Message);
            yield break;
        }

        foreach (var plugin in loaded.Value)
        {
            yield return new PluginProgramDoctorCheck(plugin, files, hostBinary);
            if (!plugin.Enabled)
                continue;
            foreach (var spec in plugin.Doctor)
                yield return new PluginChildDoctorCheck(plugin, spec, paths, runner, hostBinary);
        }
    }
}

internal sealed class StaticPluginDoctorCheck(
    string label,
    string value,
    DoctorStatus status,
    string? detail) : IDoctorCheck
{
    public string Category => "Plugins";

    public DoctorCheckResult Run() => new(label, value, status, detail);
}

internal sealed class PluginProgramDoctorCheck(
    InstalledPlugin plugin,
    IPluginFiles files,
    string? hostBinary) : IDoctorCheck
{
    public string Category => "Plugins";

    public DoctorCheckResult Run()
    {
        var label = plugin.PluginId + ": program";
        if (!plugin.Enabled)
            return new DoctorCheckResult(label, "disabled", DoctorStatus.Warn);
        if (!files.DirectoryExists(plugin.PluginRoot))
            return new DoctorCheckResult(label, "plugin root missing", DoctorStatus.Warn);
        foreach (var program in CollectPrograms(plugin))
        {
            var resolved = PluginProgramPath.Resolve(program, plugin.PluginRoot, hostBinary);
            if (!files.FileExists(resolved))
                return new DoctorCheckResult(label, "program path missing", DoctorStatus.Warn);
        }

        return new DoctorCheckResult(label, "linked and enabled", DoctorStatus.Ok);
    }

    private static IEnumerable<string> CollectPrograms(InstalledPlugin plugin)
    {
        foreach (var spec in plugin.Build)
        {
            if (spec.Command.Count > 0)
                yield return spec.Command[0];
        }

        foreach (var spec in plugin.Startup)
        {
            if (spec.Command.Count > 0)
                yield return spec.Command[0];
        }

        foreach (var action in plugin.Actions)
        {
            if (action.Command.Count > 0)
                yield return action.Command[0];
        }

        foreach (var pane in plugin.Panes)
        {
            if (pane.Command.Count > 0)
                yield return pane.Command[0];
        }

        foreach (var hook in plugin.Events)
        {
            if (hook.Command.Count > 0)
                yield return hook.Command[0];
        }

        foreach (var spec in plugin.Doctor)
        {
            if (spec.Command.Count > 0)
                yield return spec.Command[0];
        }
    }
}

internal sealed class PluginChildDoctorCheck(
    InstalledPlugin plugin,
    PluginManifestDoctor spec,
    IPluginPathRoots paths,
    IPluginDoctorRunner runner,
    string? hostBinary) : IDoctorCheck
{
    public string Category => "Plugins";

    public DoctorCheckResult Run()
    {
        var label = plugin.PluginId + ": " + spec.Label;
        if (spec.Command.Count == 0)
            return new DoctorCheckResult(label, "doctor command missing", DoctorStatus.Fail);

        var program = PluginProgramPath.Resolve(spec.Command[0], plugin.PluginRoot, hostBinary);
        var args = spec.Command.Skip(1).ToArray();
        var env = PluginEnv.Build(
            plugin,
            paths.PluginConfigDir(plugin.PluginId),
            paths.PluginStateDir(plugin.PluginId),
            "{}",
            grantToken: null,
            binPath: hostBinary,
            socketPath: null,
            context: new PluginInvocationContext(),
            actionId: null,
            eventName: null,
            eventJson: null,
            entrypointId: null);
        var ran = runner.Run(
            program,
            args,
            plugin.PluginRoot,
            env,
            PluginDoctorCheckSource.ChildTimeout);
        return PluginDoctorStdoutParser.ToResult(label, ran);
    }
}
