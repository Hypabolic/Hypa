namespace Hypa.Infrastructure.Hooks;

public static class CodexConfigPaths
{
    public static string ResolveHome()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
            return codexHome;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
    }

    public static string ResolveRoot(bool global, string? projectRoot = null)
    {
        if (global)
            return ResolveHome();

        return projectRoot ?? throw new ArgumentException(
            "Project root is required for project-scoped Codex paths.",
            nameof(projectRoot));
    }

    public static string ResolveConfigPath() =>
        Path.Combine(ResolveHome(), "config.toml");
}
