using System.Security.Cryptography;
using Hypa.Runtime.Application.Ports;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Services;

public sealed class CodeIndexService(
    IProjectRootDetector rootDetector,
    CodeStructureProviderRegistry providers,
    ICodeIndexRepository repository,
    IGitFileStateProvider gitProvider)
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hypa", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "dist", "build", "target",
    };

    private const long MaxFileBytes = 1_000_000;

    private readonly record struct StaleFileEntry(string AbsolutePath, FileInfo Info, string? CurrentOid);

    public async Task<CodeIndexResult> IndexFullAsync(string? path, CancellationToken ct)
    {
        var requestedPath = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : Path.GetFullPath(path);
        var detectorStart = Directory.Exists(requestedPath) ? requestedPath : Path.GetDirectoryName(requestedPath) ?? requestedPath;
        var root = rootDetector.Detect(detectorStart) ?? detectorStart;
        var files = Directory.Exists(requestedPath) ? EnumerateFiles(requestedPath).ToArray() : [requestedPath];
        var cleanOids = await gitProvider.GetCleanBlobOidsAsync(root, ct);
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

            var absolutePath = Path.GetFullPath(filePath);
            var relativePath = Path.GetRelativePath(root, absolutePath);
            string? gitBlobOid = null;
            cleanOids?.TryGetValue(relativePath, out gitBlobOid);

            var identity = new CodeFileIdentity
            {
                ProjectRoot = root,
                Path = absolutePath,
                RelativePath = relativePath,
                Language = language,
                ContentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                SizeBytes = info.Length,
                IndexedAt = DateTimeOffset.UtcNow,
                GitBlobOid = gitBlobOid,
                MTimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
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

    public async Task<CodeIndexResult> IndexIncrementalAsync(string? path, CancellationToken ct)
    {
        var requestedPath = string.IsNullOrWhiteSpace(path) ? Directory.GetCurrentDirectory() : Path.GetFullPath(path);
        var detectorStart = Directory.Exists(requestedPath) ? requestedPath : Path.GetDirectoryName(requestedPath) ?? requestedPath;
        var root = rootDetector.Detect(detectorStart) ?? detectorStart;

        var storedStates = await repository.QueryFileStatesAsync(root, ct);
        var cleanOids = await gitProvider.GetCleanBlobOidsAsync(root, ct);

        var onDiskAbsolutePaths = new HashSet<string>(StringComparer.Ordinal);
        var staleFiles = new List<StaleFileEntry>();
        var skipped = 0;

        foreach (var absolutePath in EnumerateFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            var language = CodeLanguageRegistry.GetLanguage(absolutePath);
            var info = new FileInfo(absolutePath);
            if (language is null || !info.Exists || info.Length > MaxFileBytes)
            {
                skipped++;
                continue;
            }

            onDiskAbsolutePaths.Add(absolutePath);
            var relativePath = Path.GetRelativePath(root, absolutePath);
            var stored = storedStates.GetValueOrDefault(absolutePath);

            bool stale;
            string? currentOid = null;
            if (cleanOids is not null && cleanOids.TryGetValue(relativePath, out var oid))
            {
                currentOid = oid;
                stale = stored is null || stored.GitBlobOid != currentOid;
            }
            else
            {
                var currentMtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                stale = stored is null
                    || stored.MTimeMs != currentMtime
                    || stored.SizeBytes != info.Length;
            }

            if (stale)
                staleFiles.Add(new StaleFileEntry(absolutePath, info, currentOid));
        }

        var deletedCount = 0;
        foreach (var storedPath in storedStates.Keys)
        {
            if (!onDiskAbsolutePaths.Contains(storedPath))
            {
                await repository.DeleteFileAsync(storedPath, ct);
                deletedCount++;
            }
        }

        var documents = new List<CodeStructureDocument>();
        foreach (var entry in staleFiles)
        {
            ct.ThrowIfCancellationRequested();
            string content;
            try { content = await File.ReadAllTextAsync(entry.AbsolutePath, ct); }
            catch { skipped++; continue; }

            var language = CodeLanguageRegistry.GetLanguage(entry.AbsolutePath)!;
            var identity = new CodeFileIdentity
            {
                ProjectRoot = root,
                Path = entry.AbsolutePath,
                RelativePath = Path.GetRelativePath(root, entry.AbsolutePath),
                Language = language,
                ContentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
                SizeBytes = entry.Info.Length,
                IndexedAt = DateTimeOffset.UtcNow,
                GitBlobOid = entry.CurrentOid,
                MTimeMs = new DateTimeOffset(entry.Info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
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
            FilesIndexed = staleFiles.Count,
            FilesSkipped = skipped,
            FilesDeleted = deletedCount,
            SymbolCount = documents.Sum(d => d.Symbols.Count),
            ReferenceCount = documents.Sum(d => d.References.Count),
            EdgeCount = documents.Sum(d => d.DependencyEdges.Count),
            DiagnosticCount = documents.Sum(d => d.Diagnostics.Count),
            ProviderHealth = health,
        };
    }

    public async Task EnsureFreshAsync(string absolutePath, CancellationToken ct)
    {
        if (!File.Exists(absolutePath)) return;

        var dir = Path.GetDirectoryName(absolutePath) ?? absolutePath;
        var root = rootDetector.Detect(dir) ?? dir;

        var currentOid = await gitProvider.GetCleanBlobOidAsync(absolutePath, root, ct);
        if (currentOid is not null)
        {
            var stored = await repository.QueryFileStateAsync(absolutePath, ct);
            if (stored?.GitBlobOid == currentOid) return;
            await ReIndexFileAsync(absolutePath, root, currentOid, ct);
            return;
        }

        var info = new FileInfo(absolutePath);
        var currentMtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var storedFallback = await repository.QueryFileStateAsync(absolutePath, ct);
        if (storedFallback is not null
            && storedFallback.MTimeMs == currentMtime
            && storedFallback.SizeBytes == info.Length)
            return;

        await ReIndexFileAsync(absolutePath, root, null, ct);
    }

    private async Task ReIndexFileAsync(string absolutePath, string root, string? knownGitBlobOid, CancellationToken ct)
    {
        var info = new FileInfo(absolutePath);
        var language = CodeLanguageRegistry.GetLanguage(absolutePath);
        if (language is null || !info.Exists || info.Length > MaxFileBytes) return;

        string content;
        try { content = await File.ReadAllTextAsync(absolutePath, ct); }
        catch { return; }

        var identity = new CodeFileIdentity
        {
            ProjectRoot = root,
            Path = absolutePath,
            RelativePath = Path.GetRelativePath(root, absolutePath),
            Language = language,
            ContentHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            SizeBytes = info.Length,
            IndexedAt = DateTimeOffset.UtcNow,
            GitBlobOid = knownGitBlobOid,
            MTimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
        };

        try
        {
            var doc = await providers.Select(language).ParseAsync(identity, content, ct);
            await repository.SaveDocumentsAsync([doc], ct);
        }
        catch { }
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
