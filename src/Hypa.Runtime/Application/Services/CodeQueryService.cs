using Hypa.Runtime.Application.Ports;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Services;

public sealed class CodeQueryService(ICodeIndexRepository repository)
{
    public Task<IReadOnlyList<CodeSymbol>> QuerySymbolsAsync(CodeSymbolQuery query, CancellationToken ct) =>
        repository.QuerySymbolsAsync(query, ct);

    public Task<CodeGraphResult> QueryGraphAsync(CodeGraphQuery query, CancellationToken ct) =>
        repository.QueryGraphAsync(query, ct);
}
