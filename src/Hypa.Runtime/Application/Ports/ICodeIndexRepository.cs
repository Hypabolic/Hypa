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
}
