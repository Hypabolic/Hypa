using System.Security.Cryptography;
using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Infrastructure.Hooks;

public sealed class ReadRedirector(
    IFileSystem fileSystem,
    IProjectRootDetector projectRootDetector,
    CodeStructureProviderRegistry providerRegistry) : IReadRedirector
{
    // Small files don't benefit enough from redirection to justify the overhead.
    private const int SmallFileThreshold = 8_000;

    private static readonly string[] PassthroughSubstrings =
    [
        "CLAUDE.md", "SKILL.md", "AGENTS.md", ".env",
        "settings.json", "settings.local.json",
        "node_modules", ".git/",
    ];

    private static readonly string[] PassthroughExtensions =
    [
        "lock", "png", "jpg", "jpeg", "gif", "webp", "pdf",
        "ico", "svg", "woff", "woff2", "ttf", "eot", "bin", "exe", "dll",
    ];

    public async Task<string?> RedirectAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || ShouldPassthrough(path))
            return null;

        byte[] bytes;
        try { bytes = fileSystem.ReadAllBytes(path); }
        catch { return null; }

        if (bytes.Length < SmallFileThreshold)
            return null;

        var lang = DetectLanguage(path);
        if (lang == "text")
            return null;

        var content = Encoding.UTF8.GetString(bytes);

        try
        {
            var projectRoot = Path.GetFullPath(
                projectRootDetector.Detect(Directory.GetCurrentDirectory())
                ?? Directory.GetCurrentDirectory());
            var resolvedPath = Path.GetFullPath(path);

            var fileId = new CodeFileIdentity
            {
                ProjectRoot = projectRoot,
                Path = resolvedPath,
                RelativePath = Path.GetRelativePath(projectRoot, resolvedPath),
                Language = lang,
                ContentHash = ComputeHash(bytes),
                SizeBytes = bytes.Length,
            };

            var provider = providerRegistry.Select(lang);
            var doc = await provider.ParseAsync(fileId, content, ct);

            if (doc.Symbols.Count == 0)
                return null;

            var outline = BuildOutline(content, doc, resolvedPath);

            // Only redirect if we achieved meaningful compression (>20% reduction).
            if (outline.Length >= content.Length * 0.8)
                return null;

            var tempPath = GetTempPath(path);
            await File.WriteAllTextAsync(tempPath, outline, ct);
            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildOutline(string content, CodeStructureDocument doc, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// hypa: outline of {Path.GetFileName(path)} ({doc.Symbols.Count} symbols)");
        sb.AppendLine("// Full content elided. Use hypa_read MCP tool for full content or specific line ranges.");
        sb.AppendLine();

        foreach (var sym in doc.Symbols.Where(s => s.ParentId is null))
        {
            sb.AppendLine($"{sym.Kind} {sym.Name} (line {sym.Span.StartLine})");
            foreach (var child in doc.Symbols.Where(s => s.ParentId == sym.Id))
                sb.AppendLine($"  {child.Kind} {child.Name} (line {child.Span.StartLine})");
        }

        return sb.ToString();
    }

    private static bool ShouldPassthrough(string path)
    {
        var p = path.Replace('\\', '/');
        if (PassthroughSubstrings.Any(s => p.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return true;
        var ext = Path.GetExtension(p).TrimStart('.').ToLowerInvariant();
        return PassthroughExtensions.Contains(ext);
    }

    private static string DetectLanguage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" => "javascript",
            ".py" => "python",
            ".rs" => "rust",
            ".go" => "go",
            _ => "text",
        };

    private static string ComputeHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..8];

    private static string GetTempPath(string originalPath)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(originalPath + Environment.ProcessId)))
            .ToLowerInvariant()[..16];
        var tempDir = Path.Combine(Path.GetTempPath(), "hypa-hook");
        Directory.CreateDirectory(tempDir);
        return Path.Combine(tempDir, $"{hash}.hypa");
    }
}
