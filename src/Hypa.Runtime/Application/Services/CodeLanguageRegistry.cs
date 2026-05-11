namespace Hypa.Runtime.Application.Services;

public static class CodeLanguageRegistry
{
    private static readonly IReadOnlyDictionary<string, string> ExtensionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "c-sharp",
        [".ts"] = "typescript",
        [".tsx"] = "tsx",
        [".js"] = "javascript",
        [".jsx"] = "jsx",
        [".py"] = "python",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".cc"] = "cpp",
        [".cxx"] = "cpp",
        [".hpp"] = "cpp",
        [".sh"] = "bash",
        [".bash"] = "bash",
        [".json"] = "json",
        [".yaml"] = "yaml",
        [".yml"] = "yaml",
        [".toml"] = "toml",
    };

    public static string? GetLanguage(string path) =>
        ExtensionMap.TryGetValue(Path.GetExtension(path), out var language) ? language : null;
}
