using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Hooks.Adapters;

public sealed class PiAdapter : IAgentHarnessAdapter
{
    public string Key => "pi";
    public HarnessCapability Capability => HarnessCapability.PreToolUse;

    public AgentHookInput? Parse(JsonElement json) => null;

    public AgentHookOutput Format(HookDecision decision, AgentHookInput input) => new(0, null);

    public bool IsAvailable()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(Path.Combine(home, ".pi", "agent")) || CommandExists("pi");
    }

    public bool IsDetected(bool global, string? projectRoot = null)
    {
        var settingsPath = ResolveSettingsPath(global, projectRoot);
        if (!File.Exists(settingsPath))
            return false;

        return SettingsContainsPackage(settingsPath, ResolvePackageSource(projectRoot));
    }

    public InstallPlan GetInstallPlan(bool global, bool includeMcp, string? projectRoot = null)
    {
        var settingsPath = ResolveSettingsPath(global, projectRoot);
        var source = ResolvePackageSource(projectRoot);
        return new InstallPlan([
            new InstallOperation.PatchJsonArrayValue(settingsPath, "packages", source),
        ]);
    }

    public UninstallPlan GetUninstallPlan(bool global, string? projectRoot = null)
    {
        var settingsPath = ResolveSettingsPath(global, projectRoot);
        var source = ResolvePackageSource(projectRoot);
        return new UninstallPlan([
            new UninstallOperation.RemoveJsonArrayValue(settingsPath, "packages", source),
        ]);
    }

    private static string ResolveSettingsPath(bool global, string? projectRoot)
    {
        if (global)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".pi", "agent", "settings.json");
        }

        var root = projectRoot ?? throw new ArgumentException(
            "Project root is required for project-scoped Pi install plans.",
            nameof(projectRoot));
        return Path.Combine(root, ".pi", "settings.json");
    }

    private static string ResolvePackageSource(string? projectRoot)
    {
        var localPackage = FindLocalPackagePath(projectRoot ?? Directory.GetCurrentDirectory());
        return localPackage ?? "npm:@hypabolic/pi-hypa";
    }

    private static string? FindLocalPackagePath(string startDirectory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "packages", "pi-hypa");
            if (File.Exists(Path.Combine(candidate, "package.json")))
                return candidate;

            dir = dir.Parent;
        }

        return null;
    }

    private static bool SettingsContainsPackage(string settingsPath, string source)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!document.RootElement.TryGetProperty("packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var item in packages.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() == source)
                    return true;

                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("source", out var sourceProperty) &&
                    sourceProperty.ValueKind == JsonValueKind.String &&
                    sourceProperty.GetString() == source)
                    return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        return false;
    }

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, command)))
                return true;

            if (OperatingSystem.IsWindows() && File.Exists(Path.Combine(dir, command + ".exe")))
                return true;
        }

        return false;
    }
}
