using System.Security.Cryptography;
using Hypa.Runtime.Application.Ports;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Services;

public sealed class CodeIndexService(
    IProjectRootDetector rootDetector,
    CodeStructureProviderRegistry providers,
    ICodeIndexRepository repository)
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hypa", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "dist", "build", "target",
    };

    private const long MaxFileBytes = 1_000_000;

    public async Task<CodeIndexResult> IndexAsync(string? path, CancellationToken ct)
    {
        var requestedPath = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : Path.GetFullPath(path);
        var detectorStart = Directory.Exists(requestedPath) ? requestedPath : Path.GetDirectoryName(requestedPath) ?? requestedPath;
        var root = rootDetector.Detect(detectorStart) ?? detectorStart;
        var files = Directory.Exists(requestedPath) ? EnumerateFiles(requestedPath).ToArray() : [requestedPath];
        var documents = new List<CodeStructureDocument>();
        var skipped = 0;

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(filePath);
            var language = CodeLanguageRegistry.GetLanguage(filePath);
            if (language is null || !info.Exists || info.Length > MaxFileBytes)
            {
                skipped++;
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(filePath, ct);
            }
            catch
            {
                skipped++;
                continue;
            }

            var identity = new CodeFileIdentity
            {
                ProjectRoot = root,
                Path = Path.GetFullPath(filePath),
                RelativePath = Path.GetRelativePath(root, filePath),
                Language = language,
                ContentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                SizeBytes = info.Length,
                IndexedAt = DateTimeOffset.UtcNow,
            };

            try
            {
                documents.Add(await providers.Select(language).ParseAsync(identity, content, ct));
            }
            catch (Exception ex)
            {
                var fallback = providers.Providers.First(p => p.Id == "regex-fallback");
                var fallbackDocument = await fallback.ParseAsync(identity, content, ct);
                documents.Add(fallbackDocument with
                {
                    Diagnostics = fallbackDocument.Diagnostics.Concat([
                        new CodeDiagnostic
                        {
                            Id = CodeStableId.ForDiagnostic(identity.RelativePath, "provider-fallback", 0),
                            FilePath = identity.RelativePath,
                            Severity = "warning",
                            Code = "provider-fallback",
                            Message = ex.InnerException?.Message ?? ex.Message,
                            Provenance = fallbackDocument.Provenance,
                        },
                    ]).ToArray(),
                });
            }
        }

        await repository.SaveDocumentsAsync(documents, ct);
        var health = providers.Providers.Select(p => p.CheckHealth()).ToArray();
        await repository.SaveProviderHealthAsync(health, ct);

        return new CodeIndexResult
        {
            FilesIndexed = documents.Count,
            FilesSkipped = skipped,
            SymbolCount = documents.Sum(d => d.Symbols.Count),
            ReferenceCount = documents.Sum(d => d.References.Count),
            EdgeCount = documents.Sum(d => d.DependencyEdges.Count),
            DiagnosticCount = documents.Sum(d => d.Diagnostics.Count),
            ProviderHealth = health,
        };
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir); }
            catch { continue; }

            foreach (var child in children)
            {
                if (!IgnoredDirectories.Contains(Path.GetFileName(child)))
                    pending.Push(child);
            }

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var file in files)
                yield return file;
        }
    }
}
