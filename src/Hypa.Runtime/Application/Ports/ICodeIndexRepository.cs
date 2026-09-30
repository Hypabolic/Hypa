using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Ports;

public interface ICodeIndexRepository
{
    Task SaveDocumentsAsync(IReadOnlyList<CodeStructureDocument> documents, CancellationToken ct);
    Task<IReadOnlyList<CodeSymbol>> QuerySymbolsAsync(CodeSymbolQuery query, CancellationToken ct);
    Task<CodeGraphResult> QueryGraphAsync(CodeGraphQuery query, CancellationToken ct);
    Task<IReadOnlyList<CodeDiagnostic>> QueryDiagnosticsAsync(CancellationToken ct);
    Task<CodeStructureDocument?> QueryMarkdownAsync(string filePath, CancellationToken ct);
    Task<IReadOnlyList<MarkdownSection>> QueryMarkdownSectionsAsync(string filePath, CancellationToken ct);
    Task<IReadOnlyList<CodeReference>> QueryReferencesAsync(string filePath, string kind, CancellationToken ct);
    Task SaveProviderHealthAsync(IReadOnlyList<CodeProviderHealth> health, CancellationToken ct);
    Task<IReadOnlyList<CodeProviderHealth>> GetProviderHealthAsync(CancellationToken ct);

    /// <summary>Stored freshness manifest for all files under a project root.</summary>
    Task<IReadOnlyDictionary<string, FileIndexState>> QueryFileStatesAsync(
        string projectRoot, CancellationToken ct);

    /// <summary>Stored freshness state for a single file. Null if not indexed.</summary>
    Task<FileIndexState?> QueryFileStateAsync(string absolutePath, CancellationToken ct);

    /// <summary>Remove all index records for a file and its derived facts.</summary>
    Task DeleteFileAsync(string absolutePath, CancellationToken ct);

    /// <summary>
    /// Load every indexed document under <paramref name="projectRoot"/> with full
    /// fact payloads (symbols, references, edges, diagnostics, sections) and the
    /// stored parse record for artifact round-trip.
    /// </summary>
    Task<IReadOnlyList<StoredIndexDocument>> LoadDocumentsAsync(string projectRoot, CancellationToken ct);

    /// <summary>Persist export compatibility metadata written at full-export save time.</summary>
    Task SaveExportMetadataAsync(CodeIndexExportMetadata metadata, CancellationToken ct);

    /// <summary>
    /// Read export compatibility metadata for <paramref name="workspaceRoot"/>, or null when never recorded.
    /// Metadata is scoped per normalized workspace root (shared default db hosts many workspaces).
    /// </summary>
    Task<CodeIndexExportMetadata?> GetExportMetadataAsync(string workspaceRoot, CancellationToken ct);
}

/// <summary>
/// Raised when a code-index write (save/delete/metadata) fails. Export mode treats this as a hard
/// error so callers never advance watermarks on a silently-stale db. Interactive index may catch
/// and continue.
/// </summary>
public sealed class CodeIndexStorageException : Exception
{
    public CodeIndexStorageException(string message) : base(message) { }
    public CodeIndexStorageException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A document reconstructed from the SQLite index, including the per-file parse
/// record that full export stamps into the artifact (not derivable from provenance alone).
/// </summary>
public sealed record StoredIndexDocument(
    CodeStructureDocument Document,
    IndexArtifactParse Parse);

/// <summary>
/// Compatibility stamp recorded when a persisting full export completes. Incremental
/// export refuses to run when any field mismatches the running binary/request.
/// </summary>
public sealed record CodeIndexExportMetadata
{
    public required string WorkspaceRoot { get; init; }
    public required string Profile { get; init; }
    public required string BinaryVersion { get; init; }
    public required IReadOnlyList<IndexArtifactProvider> Providers { get; init; }
}
