using Hypa.Sdk.CodeIntelligence;
using Hypa.Runtime.Application.Services;

namespace Hypa.Runtime.Application.Ports;

public interface ICodeStructureProvider
{
    string Id { get; }
    string Version { get; }
    string QueryVersion { get; }
    bool CanHandle(string language);
    CodeProviderHealth CheckHealth();
    Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, string content, CancellationToken ct) =>
        ParseAsync(file, SourceText.FromString(content), ct);
    Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, SourceText source, CancellationToken ct);
}
