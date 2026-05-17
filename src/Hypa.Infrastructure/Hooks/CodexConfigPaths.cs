namespace Hypa.Infrastructure.Hooks;

internal static class CodexConfigPaths
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

    public static string ResolveRoot(bool global, string? projectRoot = null) =>
        global
            ? ResolveHome()
            : projectRoot ?? Directory.GetCurrentDirectory();
}
