using Hypa.Runtime.Application.Ports;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Services;

public sealed class CodeQueryService(ICodeIndexRepository repository)
{
    public Task<IReadOnlyList<CodeSymbol>> QuerySymbolsAsync(CodeSymbolQuery query, CancellationToken ct) =>
        repository.QuerySymbolsAsync(query, ct);

    public Task<CodeGraphResult> QueryGraphAsync(CodeGraphQuery query, CancellationToken ct) =>
        repository.QueryGraphAsync(query, ct);

    public Task<CodeStructureDocument?> QueryMarkdownAsync(string filePath, CancellationToken ct) =>
        repository.QueryMarkdownAsync(filePath, ct);

    public Task<IReadOnlyList<MarkdownSection>> QueryMarkdownSectionsAsync(string filePath, CancellationToken ct) =>
        repository.QueryMarkdownSectionsAsync(filePath, ct);

    public async Task<IReadOnlyList<MarkdownSection>> QueryTocAsync(string filePath, int maxDepth = 3, CancellationToken ct = default)
    {
        var sections = await repository.QueryMarkdownSectionsAsync(filePath, ct);
        return sections.Where(s => s.HeadingLevel <= maxDepth).ToArray();
    }

    public async Task<string?> QueryFrontmatterAsync(string filePath, CancellationToken ct)
    {
        var document = await repository.QueryMarkdownAsync(filePath, ct);
        return document?.FrontmatterYaml;
    }
}
