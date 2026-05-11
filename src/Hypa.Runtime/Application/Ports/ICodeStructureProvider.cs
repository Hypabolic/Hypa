using Hypa.Sdk.CodeIntelligence;

namespace Hypa.Runtime.Application.Ports;

public interface ICodeStructureProvider
{
    string Id { get; }
    string Version { get; }
    string QueryVersion { get; }
    bool CanHandle(string language);
    CodeProviderHealth CheckHealth();
    Task<CodeStructureDocument> ParseAsync(CodeFileIdentity file, string content, CancellationToken ct);
}
