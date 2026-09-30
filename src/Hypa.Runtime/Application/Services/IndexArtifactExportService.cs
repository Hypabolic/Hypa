using System.Text;
using Hypa.Runtime.Application.Ports;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Services;

/// <summary>
/// Selects which files the export considers in scope.
/// <see cref="Full"/> is the historical, unfiltered behavior every existing consumer
/// gets by default. <see cref="CodeGraph"/> is an opt-in, non-breaking profile a
/// consumer (e.g. a code-graph indexer) requests explicitly: it drops config-data
/// "key" symbols and known generated/vendored/lockfile/minified noise from the
/// symbol/edge stream while leaving Markdown section emission untouched.
/// </summary>
public enum IndexProfile
{
    Full,
    CodeGraph,
}

/// <summary>
/// How export isolates native tree-sitter parse work from the parent process.
/// Default is <see cref="On"/> so a SIGSEGV in a worker cannot kill the export.
/// </summary>
public enum ParseIsolationMode
{
    /// <summary>Per-file worker process (default).</summary>
    On,

    /// <summary>In-process native parse (debug / perf only).</summary>
    Off,
}

public sealed record IndexArtifactExportOptions
{
    public required string Workspace { get; init; }
    public bool PersistToDatabase { get; init; }
    public bool UseGitIgnore { get; init; } = true;
    public IReadOnlyList<string> IncludeGlobs { get; init; } = [];
    public IReadOnlyList<string> ExcludeGlobs { get; init; } = [];
    public string BinaryVersion { get; init; } = "0.1.0";
    public IndexProfile Profile { get; init; } = IndexProfile.Full;

    /// <summary>
    /// When non-null, runs incremental export over this workspace-relative path set
    /// (the complete changed set including deletions). Null = full export.
    /// </summary>
    public IReadOnlyList<string>? FilesManifest { get; init; }

    /// <summary>
    /// When <see cref="ParseIsolationMode.On"/> (default), tree-sitter and markdown
    /// parse run in a child process via <see cref="INativeParseHost"/>.
    /// </summary>
    public ParseIsolationMode ParseIsolationMode { get; init; } = ParseIsolationMode.On;

    /// <summary>Per-file worker timeout. Default 60s for cold AOT + large files.</summary>
    public TimeSpan ParseWorkerTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record IndexArtifactExportResult
{
    public required IndexArtifactDocument Artifact { get; init; }
    public bool IsPartial => Artifact.Footer.Completeness == "partial";

    /// <summary>
    /// Relative paths dropped by the code-graph noise policy, ordinal-sorted.
    /// Always empty under the default <c>full</c> profile. Exposed so a caller (the
    /// CLI) can surface otherwise-silent drops — e.g. to stderr — making a
    /// false-positive exclusion detectable.
    /// </summary>
    public IReadOnlyList<string> PolicyExcludedPaths { get; init; } = [];

    /// <summary>
    /// Deferred index-db mutations for this export. Callers that emit the artifact
    /// (CLI) must flush stdout first, then invoke this so a crash between emit and
    /// ingest leaves stored state unmutated and a retry re-derives the same records.
    /// Null when there is nothing to persist (e.g. <c>--no-db</c>).
    /// </summary>
    public Func<CancellationToken, Task>? CommitPersistenceAsync { get; init; }
}

/// <summary>
/// Incremental export refused because the persisted index is missing, for a different
/// workspace/profile, or stamped with a different extractor generation. CLI maps this
/// to exit code 4 so the caller falls back to a full export.
/// </summary>
public sealed class IncrementalExportUnavailableException(string message) : Exception(message);

/// <summary>
/// Persisting export failed to write the index db. CLI maps this to exit code 1 so callers
/// never advance watermarks on a silently-stale database.
/// </summary>
public sealed class IndexArtifactPersistenceException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class IndexArtifactExportService(
    CodeStructureProviderRegistry providers,
    ICodeIndexRepository repository,
    IGitFileStateProvider gitProvider,
    INativeParseHost nativeParseHost)
{
    private const long MaxFileBytes = 1_000_000;
    private const string IsolationProvenanceId = "hypa-isolation";

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hypa", ".vs", ".idea", ".vscode", "bin", "obj", "node_modules", "dist", "build", "target",
    };

    public async Task<IndexArtifactExportResult> ExportAsync(IndexArtifactExportOptions options, CancellationToken ct)
    {
        if (options.FilesManifest is not null)
            return await ExportIncrementalAsync(options, ct);

        return await ExportFullAsync(options, ct);
    }

    private async Task<IndexArtifactExportResult> ExportFullAsync(IndexArtifactExportOptions options, CancellationToken ct)
    {
        var workspace = Path.GetFullPath(options.Workspace);
        var gitIgnore = options.UseGitIgnore && Directory.Exists(workspace)
            ? GitIgnoreMatcher.Load(workspace)
            : null;
        var cleanOids = await gitProvider.GetCleanBlobOidsAsync(workspace, ct);
        var workspaceInfo = await gitProvider.GetWorkspaceInfoAsync(workspace, ct);
        var parsed = new List<ParsedDocument>();
        var skipped = new List<IndexArtifactSkippedFile>();
        var policyExcluded = new List<string>();

        foreach (var path in EnumerateCandidateFiles(workspace, gitIgnore, options.IncludeGlobs, options.ExcludeGlobs))
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(workspace, path));
            var outcome = await TryParseCandidateAsync(
                workspace, path, relativePath, options, cleanOids, skipped, policyExcluded, ct);
            if (outcome is not null)
                parsed.Add(outcome);
        }

        parsed = parsed
            .OrderBy(p => p.Document.File.RelativePath, StringComparer.Ordinal)
            .ToList();

        // Slice 3: project-wide second pass — resolve cross-file calls/inherits/implements/
        // imports/overrides against a scope/import-aware symbol table. Symbol IDs unchanged.
        if (parsed.Count > 0)
        {
            var resolvedDocs = new CrossFileDependencyResolver()
                .Resolve(parsed.Select(p => p.Document).ToArray());
            parsed = parsed
                .Zip(resolvedDocs, (p, d) => new ParsedDocument(d, p.Parse))
                .ToList();
        }

        var health = CollectProviderHealth(options);

        var fileRecords = parsed.Select(p => ProjectFile(p, status: "indexed")).ToArray();
        var result = BuildResult(
            options,
            workspaceInfo,
            parsed,
            fileRecords,
            skipped,
            policyExcluded,
            health,
            mode: "full",
            totalFiles: null);

        if (!options.PersistToDatabase)
            return result;

        // Emit-then-persist: CLI flushes the artifact before this runs.
        return result with
        {
            CommitPersistenceAsync = async commitCt =>
            {
                try
                {
                    var storedStates = await repository.QueryFileStatesAsync(workspace, commitCt);
                    var indexedAbsolute = parsed
                        .Select(p => p.Document.File.Path)
                        .ToHashSet(StringComparer.Ordinal);
                    foreach (var absolutePath in storedStates.Keys)
                    {
                        if (!indexedAbsolute.Contains(absolutePath))
                            await repository.DeleteFileAsync(absolutePath, commitCt);
                    }

                    if (parsed.Count > 0)
                        await repository.SaveDocumentsAsync(parsed.Select(p => p.Document).ToArray(), commitCt);

                    await repository.SaveExportMetadataAsync(BuildExportMetadata(workspace, options), commitCt);
                    await repository.SaveProviderHealthAsync(health, commitCt);
                }
                catch (CodeIndexStorageException ex)
                {
                    throw new IndexArtifactPersistenceException(
                        "index database write failed; artifact not safe to consume.", ex);
                }
            },
        };
    }

    private async Task<IndexArtifactExportResult> ExportIncrementalAsync(
        IndexArtifactExportOptions options, CancellationToken ct)
    {
        var workspace = Path.GetFullPath(options.Workspace);
        var manifest = NormalizeManifest(options.FilesManifest!);

        await EnsureIncrementalAvailableAsync(workspace, options, ct);

        var stored = (await repository.LoadDocumentsAsync(workspace, ct))
            .ToDictionary(s => s.Document.File.RelativePath, StringComparer.Ordinal);
        if (stored.Count == 0)
            throw new IncrementalExportUnavailableException(
                "incremental-unavailable: no indexed state for this workspace; run a full export first.");

        // Same selection inputs as full export (gitignore / ignored dirs / symlinks).
        var gitIgnore = options.UseGitIgnore && Directory.Exists(workspace)
            ? GitIgnoreMatcher.Load(workspace)
            : null;

        var cleanOids = await gitProvider.GetCleanBlobOidsAsync(workspace, ct);
        var workspaceInfo = await gitProvider.GetWorkspaceInfoAsync(workspace, ct);
        var skipped = new List<IndexArtifactSkippedFile>();
        var policyExcluded = new List<string>();

        var reparsedRelative = new HashSet<string>(StringComparer.Ordinal);
        var deletedRelative = new HashSet<string>(StringComparer.Ordinal);
        // Symbol monikers hosted by deleted files when still present in stored state.
        // Collected only from RecordDeletedFromStored (never invented on synthetic
        // RecordDeletedFromManifest retries). Post-resolve documents edges reset gone
        // monikers to name-shaped TargetIds, so moniker membership rarely matches those
        // edges; first-pass docs re-emit via edgeChanged (and local-file path match).
        // Mixed manifests can still contribute deletedSymbolIds from a new stored delete
        // while another path is identity-less — see hadIdentitylessDeletion below.
        var deletedSymbolIds = new HashSet<string>(StringComparer.Ordinal);
        // True when any deletion this pass was synthetic (already purged / no moniker
        // identity). Gates the broad documentsReverseOnDelete net so a mixed manifest
        // (new stored delete + already-purged retry) still recovers crash-between-
        // persist-and-ingest consumers. First-pass-only stored deletes leave this false
        // and keep the dirty set tight.
        var hadIdentitylessDeletion = false;
        var reparsed = new List<ParsedDocument>();
        var deletedIdentity = new Dictionary<string, (ArtifactFileIdentity File, IndexArtifactParse Parse)>(StringComparer.Ordinal);
        // Defer db deletes until after emission assembly so a crash mid-loop still leaves
        // state consistent enough for retry; absolute paths of rows to purge.
        var pendingDeleteAbsolute = new List<string>();

        void RecordDeleted(string relativePath, ArtifactFileIdentity identity, IndexArtifactParse parse, string? absoluteToDelete)
        {
            deletedRelative.Add(relativePath);
            deletedIdentity[relativePath] = (identity, parse);
            if (absoluteToDelete is not null)
                pendingDeleteAbsolute.Add(absoluteToDelete);
            stored.Remove(relativePath);
        }

        void RecordDeletedFromStored(string relativePath, StoredIndexDocument existing)
        {
            foreach (var symbol in existing.Document.Symbols)
                deletedSymbolIds.Add(symbol.Id);
            RecordDeleted(
                relativePath,
                ToArtifactIdentity(existing.Document.File),
                existing.Parse,
                existing.Document.File.Path);
        }

        void RecordDeletedFromManifest(string relativePath)
        {
            // Retry-safe: even if the path is already gone from stored state, emit deleted
            // so a consumer that crashed after persist but before ingest still sees the delta.
            if (deletedIdentity.ContainsKey(relativePath))
                return;
            hadIdentitylessDeletion = true;
            var language = CodeLanguageRegistry.GetLanguage(
                Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar)))
                ?? "unknown";
            RecordDeleted(
                relativePath,
                new ArtifactFileIdentity
                {
                    RelativePath = relativePath,
                    Language = language,
                    ContentHash = "sha256:" + new string('0', 64),
                    SizeBytes = 0,
                    GitBlobOid = null,
                },
                new IndexArtifactParse { Provider = "none", Valid = false },
                absoluteToDelete: null);
        }

        foreach (var relativePath in manifest)
        {
            ct.ThrowIfCancellationRequested();
            var absolutePath = Path.GetFullPath(Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            // B5: case-only rename — stored key matches ignore-case but not ordinal → delete old.
            foreach (var storedKey in stored.Keys.ToArray())
            {
                if (string.Equals(storedKey, relativePath, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(storedKey, relativePath, StringComparison.Ordinal)
                    && stored.TryGetValue(storedKey, out var casePeer))
                {
                    RecordDeletedFromStored(storedKey, casePeer);
                }
            }

            // Existence on disk distinguishes re-index from delete. Full-export selection
            // gates (gitignore, ignored dirs, symlinks) and content gates (noise / minified /
            // too-large / no language) also become state deletions for §2 convergence.
            if (!File.Exists(absolutePath)
                || IsExcludedByEnumerationGates(workspace, absolutePath, relativePath, gitIgnore))
            {
                if (stored.TryGetValue(relativePath, out var existing))
                    RecordDeletedFromStored(relativePath, existing);
                else
                    RecordDeletedFromManifest(relativePath);
                continue;
            }

            var language = CodeLanguageRegistry.GetLanguage(absolutePath);
            var appliesCodeGraphFilter = options.Profile == IndexProfile.CodeGraph
                && language is not null
                && language != "markdown";

            if (language is null
                || (appliesCodeGraphFilter
                    && (CodeGraphNoisePolicy.IsNoiseLanguage(language)
                        || CodeGraphNoisePolicy.IsDenylistedPath(relativePath))))
            {
                if (stored.TryGetValue(relativePath, out var existing))
                    RecordDeletedFromStored(relativePath, existing);
                else
                    RecordDeletedFromManifest(relativePath);

                if (language is not null && appliesCodeGraphFilter)
                    policyExcluded.Add(relativePath);
                continue;
            }

            var info = new FileInfo(absolutePath);
            if (info.Length > MaxFileBytes)
            {
                skipped.Add(new IndexArtifactSkippedFile { RelativePath = relativePath, Reason = "too-large" });
                if (stored.TryGetValue(relativePath, out var existing))
                    RecordDeletedFromStored(relativePath, existing);
                else
                    RecordDeletedFromManifest(relativePath);
                continue;
            }

            SourceText source;
            try
            {
                source = await SourceText.ReadUtf8Async(absolutePath, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var reason = ex is IOException or UnauthorizedAccessException ? "io-error" : "parse-failure";
                skipped.Add(new IndexArtifactSkippedFile { RelativePath = relativePath, Reason = reason });
                if (stored.TryGetValue(relativePath, out var unreadableExisting))
                    RecordDeletedFromStored(relativePath, unreadableExisting);
                else
                    RecordDeletedFromManifest(relativePath);
                continue;
            }

            if (appliesCodeGraphFilter && CodeGraphNoisePolicy.LooksMinified(source.Text))
            {
                policyExcluded.Add(relativePath);
                if (stored.TryGetValue(relativePath, out var existing))
                    RecordDeletedFromStored(relativePath, existing);
                else
                    RecordDeletedFromManifest(relativePath);
                continue;
            }

            string? gitBlobOid = null;
            cleanOids?.TryGetValue(relativePath, out gitBlobOid);
            var identity = new CodeFileIdentity
            {
                ProjectRoot = workspace,
                Path = absolutePath,
                RelativePath = relativePath,
                Language = language,
                ContentHash = source.Sha256Hex,
                SizeBytes = source.SizeBytes,
                IndexedAt = DateTimeOffset.UtcNow,
                GitBlobOid = gitBlobOid,
                MTimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            };

            var parsedDoc = await ParseDocumentAsync(identity, source, language, options, ct);

            reparsed.Add(parsedDoc);
            reparsedRelative.Add(relativePath);
            stored.Remove(relativePath);
        }

        // Union: stored (unchanged) ∪ re-parsed − deleted.
        var working = stored.Values
            .Select(s => new ParsedDocument(s.Document, s.Parse))
            .Concat(reparsed)
            .OrderBy(p => p.Document.File.RelativePath, StringComparer.Ordinal)
            .ToList();

        // Snapshot pre-resolution edges on non-manifest stored files for affected detection.
        var preEdges = working
            .Where(p => !reparsedRelative.Contains(p.Document.File.RelativePath)
                        && !deletedRelative.Contains(p.Document.File.RelativePath))
            .ToDictionary(
                p => p.Document.File.RelativePath,
                p => SnapshotEdges(p.Document.DependencyEdges),
                StringComparer.Ordinal);

        // Symbol monikers of re-parsed files — reverse dependents (incoming edges) are
        // re-emitted every time those files reparse so a retry after a successful first
        // run still surfaces Menu.cs when Worker.cs is in the manifest (C1).
        // Also track re-parsed relative paths so KG-H2 section→file (local-file) edges
        // reverse-depend on the linked code file, not only on symbol monikers.
        var reparsedSymbolIds = reparsed
            .SelectMany(p => p.Document.Symbols.Select(s => s.Id))
            .ToHashSet(StringComparer.Ordinal);

        if (working.Count > 0)
        {
            var resolvedDocs = new CrossFileDependencyResolver()
                .Resolve(working.Select(p => p.Document).ToArray());
            working = working
                .Zip(resolvedDocs, (p, d) => new ParsedDocument(WithParse(d, p.Parse), p.Parse))
                .ToList();
        }

        // Symbol-side documents edges flip to name-shaped TargetIds and lose moniker keys.
        // First-pass stored deletes: edgeChanged re-emits flipped docs; local-file edges
        // keep the path in TargetId (deletedRelative path match, documents-kind only).
        // Same-manifest retry after purge (and mixed manifests with at least one synthetic
        // RecordDeletedFromManifest path): moniker identity is unavailable for that path,
        // so re-emit every file that still carries a documents edge — consumer crash-
        // between-persist-and-ingest recovery. Gate on hadIdentitylessDeletion (not global
        // deletedSymbolIds emptiness) so a concurrent new stored delete cannot disable
        // retry recovery. First-pass-only stored deletes leave the flag false and keep
        // the dirty set tight.
        var documentsReverseOnDelete =
            deletedRelative.Count > 0 && hadIdentitylessDeletion;

        var affectedRelative = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in working)
        {
            var relative = doc.Document.File.RelativePath;
            if (reparsedRelative.Contains(relative))
                continue;

            var edges = doc.Document.DependencyEdges;
            var edgeChanged = preEdges.TryGetValue(relative, out var before)
                && !EdgeSnapshotsEqual(before, SnapshotEdges(edges));
            // Reverse deps:
            // - moniker TargetIds (reparsed + deleted-from-stored; residual / non-reset edges)
            // - path TargetIds on documents edges only (local-file reparsed/deleted)
            // - any documents edge when moniker identity is unavailable for this delete pass
            var reverseDependent = edges.Any(e =>
            {
                if (reparsedSymbolIds.Contains(e.TargetId) || deletedSymbolIds.Contains(e.TargetId))
                    return true;
                if (e.Kind == "documents"
                    && (reparsedRelative.Contains(e.TargetId) || deletedRelative.Contains(e.TargetId)))
                    return true;
                return documentsReverseOnDelete && e.Kind == "documents";
            });

            if (edgeChanged || reverseDependent)
                affectedRelative.Add(relative);
        }

        var health = CollectProviderHealth(options);

        // Emit: re-parsed (indexed) + deleted + affected non-manifest (indexed).
        var workingByPath = working.ToDictionary(p => p.Document.File.RelativePath, StringComparer.Ordinal);
        var emitRecords = new List<IndexFileRecord>();

        foreach (var relative in reparsedRelative.Order(StringComparer.Ordinal))
        {
            if (workingByPath.TryGetValue(relative, out var doc))
                emitRecords.Add(ProjectFile(doc, status: "indexed"));
        }

        foreach (var relative in deletedIdentity.Keys.Order(StringComparer.Ordinal))
        {
            var (file, parse) = deletedIdentity[relative];
            emitRecords.Add(new IndexFileRecord
            {
                File = file,
                Status = "deleted",
                Parse = parse,
                Symbols = [],
                References = [],
                DependencyEdges = [],
                Diagnostics = [],
                Sections = [],
                FrontmatterYaml = null,
            });
        }

        foreach (var relative in affectedRelative.Order(StringComparer.Ordinal))
        {
            if (workingByPath.TryGetValue(relative, out var doc))
                emitRecords.Add(ProjectFile(doc, status: "indexed"));
        }

        emitRecords = emitRecords
            .OrderBy(r => r.File.RelativePath, StringComparer.Ordinal)
            .ToList();

        var result = BuildResult(
            options,
            workspaceInfo,
            working,
            emitRecords,
            skipped,
            policyExcluded,
            health,
            mode: "incremental",
            totalFiles: working.Count,
            completenessParsedOverride: reparsed);

        // Emit-then-persist: mutations run only after the caller flushes the artifact.
        var toSave = working
            .Where(p => reparsedRelative.Contains(p.Document.File.RelativePath)
                        || affectedRelative.Contains(p.Document.File.RelativePath))
            .Select(p => p.Document)
            .ToArray();
        var deletes = pendingDeleteAbsolute.Distinct(StringComparer.Ordinal).ToArray();

        return result with
        {
            CommitPersistenceAsync = async commitCt =>
            {
                try
                {
                    foreach (var abs in deletes)
                        await repository.DeleteFileAsync(abs, commitCt);
                    if (toSave.Length > 0)
                        await repository.SaveDocumentsAsync(toSave, commitCt);
                    await repository.SaveProviderHealthAsync(health, commitCt);
                }
                catch (CodeIndexStorageException ex)
                {
                    throw new IndexArtifactPersistenceException(
                        "index database write failed; artifact not safe to consume.", ex);
                }
            },
        };
    }

    private async Task EnsureIncrementalAvailableAsync(
        string workspace, IndexArtifactExportOptions options, CancellationToken ct)
    {
        var metadata = await repository.GetExportMetadataAsync(workspace, ct);
        if (metadata is null)
        {
            throw new IncrementalExportUnavailableException(
                "incremental-unavailable: no export metadata in the index database; run a full export first.");
        }

        var recordedRoot = Path.GetFullPath(metadata.WorkspaceRoot);
        if (!string.Equals(recordedRoot, workspace, StringComparison.Ordinal))
        {
            throw new IncrementalExportUnavailableException(
                $"incremental-unavailable: workspace root mismatch (recorded '{metadata.WorkspaceRoot}', requested '{workspace}').");
        }

        var requestedProfile = ProfileName(options.Profile);
        if (!string.Equals(metadata.Profile, requestedProfile, StringComparison.Ordinal))
        {
            throw new IncrementalExportUnavailableException(
                $"incremental-unavailable: profile mismatch (recorded '{metadata.Profile}', requested '{requestedProfile}').");
        }

        var running = providers.Providers
            .Select(p => new IndexArtifactProvider
            {
                ProviderId = p.Id,
                ProviderVersion = p.Version,
                QueryVersion = p.QueryVersion,
            })
            .ToDictionary(p => p.ProviderId, StringComparer.Ordinal);

        var recordedById = metadata.Providers
            .ToDictionary(p => p.ProviderId, StringComparer.Ordinal);

        foreach (var recorded in metadata.Providers)
        {
            if (!running.TryGetValue(recorded.ProviderId, out var current))
            {
                throw new IncrementalExportUnavailableException(
                    $"incremental-unavailable: provider '{recorded.ProviderId}' is no longer available.");
            }

            if (!string.Equals(recorded.ProviderVersion, current.ProviderVersion, StringComparison.Ordinal)
                || !string.Equals(recorded.QueryVersion, current.QueryVersion, StringComparison.Ordinal))
            {
                throw new IncrementalExportUnavailableException(
                    $"incremental-unavailable: provider/query version mismatch for '{recorded.ProviderId}' " +
                    $"(recorded {recorded.ProviderVersion}/{recorded.QueryVersion}, " +
                    $"running {current.ProviderVersion}/{current.QueryVersion}).");
            }
        }

        // Running binary introduced a provider the stamp does not know about — mixed-
        // generation risk is the same as a version mismatch (D2).
        foreach (var runningId in running.Keys.Order(StringComparer.Ordinal))
        {
            if (!recordedById.ContainsKey(runningId))
            {
                throw new IncrementalExportUnavailableException(
                    $"incremental-unavailable: provider '{runningId}' is present in the running binary " +
                    "but absent from recorded export metadata; run a full export first.");
            }
        }
    }

    private CodeIndexExportMetadata BuildExportMetadata(string workspace, IndexArtifactExportOptions options) =>
        new()
        {
            WorkspaceRoot = workspace,
            Profile = ProfileName(options.Profile),
            BinaryVersion = options.BinaryVersion,
            Providers = providers.Providers
                .Select(p => new IndexArtifactProvider
                {
                    ProviderId = p.Id,
                    ProviderVersion = p.Version,
                    QueryVersion = p.QueryVersion,
                })
                .OrderBy(p => p.ProviderId, StringComparer.Ordinal)
                .ToArray(),
        };

    private static string ProfileName(IndexProfile profile) =>
        profile == IndexProfile.CodeGraph ? "code-graph" : "full";

    private async Task<ParsedDocument?> TryParseCandidateAsync(
        string workspace,
        string path,
        string relativePath,
        IndexArtifactExportOptions options,
        IReadOnlyDictionary<string, string>? cleanOids,
        List<IndexArtifactSkippedFile> skipped,
        List<string> policyExcluded,
        CancellationToken ct)
    {
        var language = CodeLanguageRegistry.GetLanguage(path);
        if (language is null)
            return null;

        var appliesCodeGraphFilter = options.Profile == IndexProfile.CodeGraph && language != "markdown";
        if (appliesCodeGraphFilter
            && (CodeGraphNoisePolicy.IsNoiseLanguage(language) || CodeGraphNoisePolicy.IsDenylistedPath(relativePath)))
        {
            policyExcluded.Add(relativePath);
            return null;
        }

        var info = new FileInfo(path);
        if (!info.Exists)
            return null;

        if (info.Length > MaxFileBytes)
        {
            skipped.Add(new IndexArtifactSkippedFile { RelativePath = relativePath, Reason = "too-large" });
            return null;
        }

        SourceText source;
        try
        {
            source = await SourceText.ReadUtf8Async(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = ex is IOException or UnauthorizedAccessException ? "io-error" : "parse-failure";
            skipped.Add(new IndexArtifactSkippedFile { RelativePath = relativePath, Reason = reason });
            return null;
        }

        if (appliesCodeGraphFilter && CodeGraphNoisePolicy.LooksMinified(source.Text))
        {
            policyExcluded.Add(relativePath);
            return null;
        }

        string? gitBlobOid = null;
        cleanOids?.TryGetValue(relativePath, out gitBlobOid);
        var identity = new CodeFileIdentity
        {
            ProjectRoot = workspace,
            Path = path,
            RelativePath = relativePath,
            Language = language,
            ContentHash = source.Sha256Hex,
            SizeBytes = source.SizeBytes,
            IndexedAt = DateTimeOffset.UtcNow,
            GitBlobOid = gitBlobOid,
            MTimeMs = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
        };

        return await ParseDocumentAsync(identity, source, language, options, ct);
    }

    /// <summary>
    /// Shared parse path for full and incremental export. When isolation is on and the
    /// selected provider is tree-sitter or markdown, parse runs via
    /// <see cref="INativeParseHost"/>. Crash/timeout degrades to in-process regex-fallback.
    /// </summary>
    private async Task<ParsedDocument> ParseDocumentAsync(
        CodeFileIdentity identity,
        SourceText source,
        string language,
        IndexArtifactExportOptions options,
        CancellationToken ct)
    {
        var selected = providers.Select(language);
        var isolationOn = options.ParseIsolationMode != ParseIsolationMode.Off;
        var usesNative = selected.Id is "tree-sitter" or "markdown";

        if (isolationOn && usesNative)
        {
            var hostResult = await nativeParseHost.ParseAsync(
                new NativeParseRequest
                {
                    File = identity,
                    Language = language,
                    AbsolutePath = identity.Path,
                    Timeout = options.ParseWorkerTimeout,
                },
                ct);

            if (hostResult.IsOk)
            {
                // Prefer parent identity (workspace RelativePath, ProjectRoot, GitBlobOid).
                // Worker monikers already use the relative path passed on the CLI; rebinding
                // File keeps the file record aligned if worker content hash / mtime differ.
                var document = hostResult.Value with { File = identity };
                var parse = ParseFor(language, selected.Id, document);
                return new ParsedDocument(WithParse(document, parse), parse);
            }

            var fallback = providers.Providers.First(p => p.Id == "regex-fallback");
            var fallbackDocument = await fallback.ParseAsync(identity, source, ct);
            var diagnostic = MakeIsolationDiagnostic(identity, hostResult.Error);
            fallbackDocument = fallbackDocument with
            {
                Diagnostics = fallbackDocument.Diagnostics.Concat([diagnostic]).ToArray(),
            };
            var degraded = new IndexArtifactParse { Provider = "none", Valid = false };
            return new ParsedDocument(WithParse(fallbackDocument, degraded), degraded);
        }

        try
        {
            var document = await selected.ParseAsync(identity, source, ct);
            var parse = ParseFor(language, selected.Id, document);
            return new ParsedDocument(WithParse(document, parse), parse);
        }
        catch
        {
            var fallback = providers.Providers.First(p => p.Id == "regex-fallback");
            var document = await fallback.ParseAsync(identity, source, ct);
            var parse = new IndexArtifactParse { Provider = "none", Valid = false };
            return new ParsedDocument(WithParse(document, parse), parse);
        }
    }

    private CodeProviderHealth[] CollectProviderHealth(IndexArtifactExportOptions options)
    {
        // Isolation on: never call native CheckHealth before footer flush (SIGSEGV risk).
        if (options.ParseIsolationMode != ParseIsolationMode.Off)
        {
            return providers.Providers
                .Select(p => p.Id is "tree-sitter" or "markdown"
                    ? new CodeProviderHealth
                    {
                        ProviderId = p.Id,
                        Status = "ok",
                        Message = "Native health skipped under parse isolation.",
                    }
                    : p.CheckHealth())
                .OrderBy(h => h.ProviderId, StringComparer.Ordinal)
                .ToArray();
        }

        return providers.Providers
            .Select(p => p.CheckHealth())
            .OrderBy(h => h.ProviderId, StringComparer.Ordinal)
            .ToArray();
    }

    private static CodeDiagnostic MakeIsolationDiagnostic(CodeFileIdentity identity, NativeParseError error)
    {
        var code = error.Kind switch
        {
            NativeParseErrorKind.TimedOut => "native-parse-timeout",
            NativeParseErrorKind.Crashed => "native-parse-crash",
            NativeParseErrorKind.SpawnFailed => "native-parse-spawn-failed",
            NativeParseErrorKind.ProtocolError => "native-parse-protocol-error",
            NativeParseErrorKind.ParseFailure => "native-parse-failure",
            NativeParseErrorKind.IoError => "native-parse-io-error",
            _ => "native-parse-crash",
        };

        // Host messages already include exit codes when known; do not append a second time.
        var message = error.Message;
        if (string.IsNullOrEmpty(message))
        {
            message = error.ExitCode is int exit
                ? $"Native parse isolation failed (exit {exit})."
                : "Native parse isolation failed.";
        }

        return new CodeDiagnostic
        {
            Id = CodeStableId.ForDiagnostic(identity.RelativePath, code, 0),
            FilePath = identity.RelativePath,
            Severity = "warning",
            Code = code,
            Message = message,
            Provenance = new ProviderProvenance
            {
                ProviderId = IsolationProvenanceId,
                ProviderVersion = "1",
                QueryVersion = "isolation-1",
                FactKind = "parse-isolation",
                Confidence = 1.0,
            },
        };
    }

    private IndexArtifactExportResult BuildResult(
        IndexArtifactExportOptions options,
        GitWorkspaceInfo workspaceInfo,
        IReadOnlyList<ParsedDocument> projectParsed,
        IReadOnlyList<IndexFileRecord> fileRecords,
        IReadOnlyList<IndexArtifactSkippedFile> skipped,
        IReadOnlyList<string> policyExcluded,
        IReadOnlyList<CodeProviderHealth> health,
        string mode,
        int? totalFiles,
        IReadOnlyList<ParsedDocument>? completenessParsedOverride = null)
    {
        var completenessSource = completenessParsedOverride ?? projectParsed;
        var providerMetadata = providers.Providers.ToDictionary(
            p => p.Id,
            p => new IndexArtifactProvider { ProviderId = p.Id, ProviderVersion = p.Version, QueryVersion = p.QueryVersion },
            StringComparer.Ordinal);
        var participatedHealthIds = completenessSource
            .Select(p => p.Parse.Provider == "none" ? "regex-fallback" : p.Parse.Provider)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        // Header providers: union of project-set provenance + participated health ids.
        var headerProviderSource = projectParsed.Count > 0 ? projectParsed : completenessSource;

        var providerHealth = health
            .Where(h => participatedHealthIds.Contains(h.ProviderId))
            .Select(h => new IndexArtifactProviderHealth { ProviderId = h.ProviderId, Status = h.Status, Message = h.Message })
            .ToArray();
        var completeness = skipped.Count > 0
            || providerHealth.Any(h => h.Status is "warn" or "error")
            || completenessSource.Any(p => !p.Parse.Valid)
            ? "partial"
            : "complete";

        var artifact = new IndexArtifactDocument
        {
            Header = new IndexArtifactHeader
            {
                Generator = new IndexArtifactGenerator
                {
                    BinaryVersion = options.BinaryVersion,
                    Providers = headerProviderSource
                        .SelectMany(ProviderProjections)
                        .Concat(participatedHealthIds.Select(id => providerMetadata.GetValueOrDefault(id)).OfType<IndexArtifactProvider>())
                        .GroupBy(p => p.ProviderId, StringComparer.Ordinal)
                        .Select(g => g.OrderBy(p => p.ProviderVersion, StringComparer.Ordinal).ThenBy(p => p.QueryVersion, StringComparer.Ordinal).First())
                        .OrderBy(p => p.ProviderId, StringComparer.Ordinal)
                        .ToArray(),
                },
                Workspace = new IndexArtifactWorkspace
                {
                    CommitSha = workspaceInfo.CommitSha,
                    GitDirty = workspaceInfo.GitDirty,
                    Languages = projectParsed.Select(p => p.Document.File.Language).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                },
                Mode = mode,
            },
            Files = fileRecords.ToArray(),
            Footer = new IndexArtifactFooter
            {
                Counts = new IndexArtifactCounts
                {
                    Files = fileRecords.Count,
                    Symbols = fileRecords.Sum(f => f.Symbols.Count),
                    References = fileRecords.Sum(f => f.References.Count),
                    DependencyEdges = fileRecords.Sum(f => f.DependencyEdges.Count),
                    Diagnostics = fileRecords.Sum(f => f.Diagnostics.Count),
                    Sections = fileRecords.Sum(f => f.Sections.Count),
                },
                SkippedFiles = skipped.OrderBy(s => s.RelativePath, StringComparer.Ordinal).ToArray(),
                ProviderHealth = providerHealth,
                Completeness = completeness,
                PolicyExcluded = options.Profile == IndexProfile.CodeGraph ? policyExcluded.Count : null,
                TotalFiles = totalFiles,
            },
        };

        return new IndexArtifactExportResult
        {
            Artifact = artifact,
            PolicyExcludedPaths = policyExcluded.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
        };
    }

    /// <summary>
    /// Validate and normalize a caller-supplied changed-file manifest.
    /// Absolute paths and paths that escape the workspace root are rejected by the CLI
    /// before this is called; this method still normalizes separators and de-duplicates.
    /// </summary>
    internal static IReadOnlyList<string> NormalizeManifest(IReadOnlyList<string> raw)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in raw)
        {
            var trimmed = line.Trim().Replace('\\', '/');
            if (trimmed.Length == 0)
                continue;
            set.Add(trimmed);
        }

        return set.ToArray();
    }

    /// <summary>
    /// Parse and validate a UTF-8 newline-delimited manifest file. Throws
    /// <see cref="ArgumentException"/> with a one-line message for usage errors.
    /// </summary>
    public static IReadOnlyList<string> ReadFilesManifest(string manifestPath, string workspace)
    {
        if (!File.Exists(manifestPath))
            throw new ArgumentException($"--files manifest not found: {manifestPath}");

        string text;
        try
        {
            // Strict UTF-8 — replacement-fallback decoding would silently mangle paths
            // into garbage instead of the D1 unreadable-manifest usage error (exit 2).
            // Strip optional UTF-8 BOM first (same approach as SourceText.FromUtf8Bytes)
            // so a BOM-prefixed manifest does not corrupt the first path with U+FEFF.
            var bytes = File.ReadAllBytes(manifestPath);
            var bomLength = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? 3
                : 0;
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, bomLength, bytes.Length - bomLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new ArgumentException($"--files manifest unreadable: {manifestPath}");
        }

        var workspaceFull = Path.GetFullPath(workspace);
        var paths = new List<string>();
        foreach (var rawLine in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (Path.IsPathRooted(line))
                throw new ArgumentException($"--files path must be workspace-relative: {line}");

            var normalized = line.Replace('\\', '/');
            // Reject ".." escapes after normalization against the workspace root.
            var combined = Path.GetFullPath(Path.Combine(workspaceFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(workspaceFull, combined);
            if (relative.StartsWith("..", StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                throw new ArgumentException($"--files path escapes workspace root: {line}");
            }

            paths.Add(NormalizeRelativePath(relative));
        }

        if (paths.Count == 0)
            throw new ArgumentException("--files manifest is empty (no paths after trimming).");

        return NormalizeManifest(paths);
    }

    /// <summary>
    /// Single source of truth for full-walk and incremental selection gates:
    /// ignored directory segments, nested git worktrees, the file itself being a
    /// symlink, any symlinked ancestor directory, and .gitignore for files.
    /// </summary>
    private static bool IsExcludedByEnumerationGates(
        string workspace,
        string absolutePath,
        string relativePath,
        GitIgnoreMatcher? gitIgnore)
    {
        if (IsSymlink(absolutePath))
            return true;

        if (HasSymlinkedAncestor(workspace, absolutePath))
            return true;

        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (IgnoredDirectories.Contains(segments[i]))
                return true;
        }

        // Nested git roots (full walk refuses to enter dirs that contain .git).
        var dir = Path.GetDirectoryName(absolutePath);
        while (!string.IsNullOrEmpty(dir)
               && dir.Length >= workspace.Length
               && !string.Equals(dir, workspace, StringComparison.Ordinal))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                return true;
            dir = Path.GetDirectoryName(dir);
        }

        if (gitIgnore?.IsIgnored(relativePath, isDirectory: false) == true)
            return true;

        return false;
    }

    /// <summary>
    /// Directory-descent gates for the full walk — must stay in lockstep with
    /// <see cref="IsExcludedByEnumerationGates"/> so a path under an excluded dir
    /// is never reachable from either mode.
    /// </summary>
    private static bool ShouldDescendDirectory(
        string workspace,
        string absoluteDir,
        string relativeDir,
        GitIgnoreMatcher? gitIgnore)
    {
        var name = Path.GetFileName(absoluteDir);
        if (IgnoredDirectories.Contains(name))
            return false;
        if (IsSymlink(absoluteDir))
            return false;
        if (!Directory.Exists(absoluteDir))
            return false;
        if (Directory.Exists(Path.Combine(absoluteDir, ".git"))
            || File.Exists(Path.Combine(absoluteDir, ".git")))
        {
            return false;
        }

        if (gitIgnore?.IsIgnored(relativeDir, isDirectory: true) == true)
            return false;

        return true;
    }

    private static bool HasSymlinkedAncestor(string workspace, string absolutePath)
    {
        var dir = Path.GetDirectoryName(absolutePath);
        while (!string.IsNullOrEmpty(dir)
               && dir.Length >= workspace.Length)
        {
            if (IsSymlink(dir))
                return true;
            if (string.Equals(dir, workspace, StringComparison.Ordinal))
                break;
            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(
        string root,
        GitIgnoreMatcher? gitIgnore,
        IReadOnlyList<string> includeGlobs,
        IReadOnlyList<string> excludeGlobs)
    {
        if (File.Exists(root))
            return IsExcludedByEnumerationGates(root, root, NormalizeRelativePath(Path.GetFileName(root)), gitIgnore)
                ? []
                : [root];

        var includeMatchers = includeGlobs.Select(GitIgnoreMatcher.LoadForGlob).ToArray();
        var excludeMatchers = excludeGlobs.Select(GitIgnoreMatcher.LoadForGlob).ToArray();

        return Enumerate(root)
            .Where(path =>
            {
                var relativePath = NormalizeRelativePath(Path.GetRelativePath(root, path));
                if (IsExcludedByEnumerationGates(root, path, relativePath, gitIgnore))
                    return false;
                if (excludeMatchers.Length > 0 && excludeMatchers.Any(m => m(relativePath)))
                    return false;
                return includeMatchers.Length == 0 || includeMatchers.Any(m => m(relativePath));
            })
            .Order(StringComparer.Ordinal);

        IEnumerable<string> Enumerate(string start)
        {
            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(dir).Order(StringComparer.Ordinal); }
                catch { continue; }

                foreach (var child in dirs.Reverse())
                {
                    var rel = NormalizeRelativePath(Path.GetRelativePath(start, child));
                    if (ShouldDescendDirectory(start, child, rel, gitIgnore))
                        pending.Push(child);
                }

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal); }
                catch { continue; }

                foreach (var file in files)
                    yield return file;
            }
        }
    }

    private static IndexFileRecord ProjectFile(ParsedDocument parsed, string status)
    {
        var document = parsed.Document;
        return new IndexFileRecord
        {
            File = ToArtifactIdentity(document.File),
            Status = status,
            Parse = parsed.Parse,
            Symbols = SortBySpan(document.Symbols).ToArray(),
            References = SortBySpan(document.References).ToArray(),
            DependencyEdges = document.DependencyEdges
                .OrderBy(e => e.SourceSpan?.StartByte ?? int.MaxValue)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .ToArray(),
            Diagnostics = document.Diagnostics
                .OrderBy(d => d.Span?.StartByte ?? int.MaxValue)
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .ToArray(),
            Sections = document.Sections
                .OrderBy(s => s.StartByte)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => new ArtifactMarkdownSection
                {
                    Id = s.Id,
                    FilePath = s.FilePath,
                    HeadingText = s.HeadingText,
                    HeadingLevel = s.HeadingLevel,
                    HeadingPath = s.HeadingPath,
                    HeadingAnchor = s.HeadingAnchor,
                    StartLine = s.StartLine,
                    EndLine = s.EndLine,
                    StartByte = s.StartByte,
                    EndByte = s.EndByte,
                    Provenance = s.Provenance,
                })
                .ToArray(),
            FrontmatterYaml = document.FrontmatterYaml,
        };
    }

    private static ArtifactFileIdentity ToArtifactIdentity(CodeFileIdentity file) => new()
    {
        RelativePath = file.RelativePath,
        Language = file.Language,
        ContentHash = $"sha256:{file.ContentHash}",
        SizeBytes = file.SizeBytes,
        GitBlobOid = file.GitBlobOid,
    };

    private static IEnumerable<IndexArtifactProvider> ProviderProjections(ParsedDocument parsed)
    {
        foreach (var provenance in parsed.Document.Symbols.Select(s => s.Provenance)
                     .Concat(parsed.Document.References.Select(r => r.Provenance))
                     .Concat(parsed.Document.DependencyEdges.Select(e => e.Provenance))
                     .Concat(parsed.Document.Diagnostics.Select(d => d.Provenance))
                     .Concat(parsed.Document.Sections.Select(s => s.Provenance))
                     .Append(parsed.Document.Provenance))
        {
            yield return new IndexArtifactProvider
            {
                ProviderId = provenance.ProviderId,
                ProviderVersion = provenance.ProviderVersion,
                QueryVersion = provenance.QueryVersion,
            };
        }
    }

    private static IEnumerable<T> SortBySpan<T>(IEnumerable<T> items) where T : notnull =>
        items.OrderBy(item => StartByte(item)).ThenBy(item => Id(item), StringComparer.Ordinal);

    private static int StartByte<T>(T item) =>
        item switch
        {
            CodeSymbol s => s.Span.StartByte,
            CodeReference r => r.Span.StartByte,
            _ => int.MaxValue,
        };

    private static string Id<T>(T item) =>
        item switch
        {
            CodeSymbol s => s.Id,
            CodeReference r => r.Id,
            _ => string.Empty,
        };

    private static IndexArtifactParse ParseFor(string language, string providerId, CodeStructureDocument document)
    {
        if (language == "markdown")
            return new IndexArtifactParse { Provider = "markdown", Valid = document.ParseGateValid };

        return providerId == "regex-fallback"
            ? new IndexArtifactParse { Provider = "none", Valid = false }
            : new IndexArtifactParse { Provider = "tree-sitter", Valid = document.ParseGateValid };
    }

    private static CodeStructureDocument WithParse(CodeStructureDocument document, IndexArtifactParse parse) =>
        document with
        {
            ParseProvider = parse.Provider,
            ParseGateValid = parse.Valid,
        };

    private static IReadOnlyList<EdgeSnapshot> SnapshotEdges(IReadOnlyList<CodeDependencyEdge> edges) =>
        edges
            .Select(e => new EdgeSnapshot(e.Id, e.TargetId, e.TargetResolutionStatus, e.TargetName))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();

    private static bool EdgeSnapshotsEqual(IReadOnlyList<EdgeSnapshot> a, IReadOnlyList<EdgeSnapshot> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
                return false;
        }

        return true;
    }

    private static bool IsSymlink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private sealed record ParsedDocument(CodeStructureDocument Document, IndexArtifactParse Parse);

    private readonly record struct EdgeSnapshot(string Id, string TargetId, string ResolutionStatus, string? TargetName);
}
