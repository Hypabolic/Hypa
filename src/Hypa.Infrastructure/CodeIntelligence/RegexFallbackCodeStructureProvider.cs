using Hypa.Runtime.Application.Ports;
using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Infrastructure.CodeIntelligence;

public sealed class RegexFallbackCodeStructureProvider : ICodeStructureProvider
{
    public string Id => "regex-fallback";
    public string Version => "1";
    public string QueryVersion => "regex-1";

    public bool CanHandle(string language) => true;

    public CodeProviderHealth CheckHealth() =>
        new() { ProviderId = Id, Status = "ok", Message = "Regex fallback provider available." };

    public Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, string content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var provenance = new ProviderProvenance
        {
            ProviderId = Id,
            ProviderVersion = Version,
            QueryVersion = QueryVersion,
            FactKind = "heuristic",
            Confidence = 0.45,
        };
        return Task.FromResult(CodePatternExtractor.Extract(file, content, provenance));
    }
}
