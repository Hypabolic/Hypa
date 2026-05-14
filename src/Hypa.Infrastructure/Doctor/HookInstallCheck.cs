using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Doctor;

public sealed class HookInstallCheck(IHarnessRegistry registry, IProjectRootDetector projectRootDetector) : IDoctorCheck
{
    public string Category => "Hooks";

    public DoctorCheckResult Run()
    {
        var count = registry.All.Count;
        if (count == 0)
            return new DoctorCheckResult("Harnesses", "none registered", DoctorStatus.Fail);

        var keys = string.Join(", ", registry.All.Select(a => a.Key));
        var missing = CollectMissingHarnesses();

        if (missing.Count == 0)
            return new DoctorCheckResult("Harnesses", keys, DoctorStatus.Ok);

        var hints = string.Join("; ", missing.Select(InstallHint));
        return new DoctorCheckResult("Harnesses", keys, DoctorStatus.Warn, $"Run: {hints}");
    }

    private List<IAgentHarnessAdapter> CollectMissingHarnesses()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projectRoot = projectRootDetector.Detect(Directory.GetCurrentDirectory());
        var missing = new List<IAgentHarnessAdapter>();

        foreach (var adapter in registry.All)
        {
            try
            {
                if (!IsAdapterInstalled(adapter.Key, home, projectRoot))
                    missing.Add(adapter);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable settings; don't crash doctor
            }
        }

        return missing;
    }

    private static string InstallHint(IAgentHarnessAdapter adapter)
    {
        var isProjectScoped = adapter.GetInstallPlan(global: true).Operations
            .All(op => op is InstallOperation.NotSupported);
        return isProjectScoped
            ? $"`hypa init --agent {adapter.Key}`"
            : $"`hypa init --global --agent {adapter.Key}`";
    }

    private static bool IsAdapterInstalled(string key, string home, string? projectRoot) =>
        key switch
        {
            "claude" => IsClaudeInstalled(home),
            "codex" => IsCodexInstalled(projectRoot ?? Directory.GetCurrentDirectory()),
            _ => false,
        };

    private static bool IsClaudeInstalled(string home)
    {
        var settingsPath = Path.Combine(home, ".claude", "settings.json");
        return File.Exists(settingsPath) &&
               File.ReadAllText(settingsPath).Contains("hypa hook", StringComparison.Ordinal);
    }

    private static bool IsCodexInstalled(string projectRoot)
    {
        var hooksPath = Path.Combine(projectRoot, ".codex", "hooks.json");
        return File.Exists(hooksPath) &&
               File.ReadAllText(hooksPath).Contains("hypa hook", StringComparison.Ordinal);
    }
}
