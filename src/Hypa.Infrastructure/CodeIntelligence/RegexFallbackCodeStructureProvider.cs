using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Application.Services;
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

    public Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, string content, CancellationToken ct) =>
        ParseAsync(file, SourceText.FromString(content), ct);

    public Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, SourceText source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var provenance = new ProviderProvenance
        {
            ProviderId = Id,
            ProviderVersion = Version,
            QueryVersion = QueryVersion,
            FactKind = "symbol-declaration",
            Confidence = 0.45,
        };
        return Task.FromResult(CodePatternExtractor.Extract(file, source, provenance) with { ParseGateValid = false });
    }
}
