using Hypa.Runtime.Application.Ports;

namespace Hypa.Runtime.Application.Services;

public sealed class CodeStructureProviderRegistry(IEnumerable<ICodeStructureProvider> providers)
{
    private readonly IReadOnlyList<ICodeStructureProvider> _providers = providers.ToList();

    public IReadOnlyList<ICodeStructureProvider> Providers => _providers;

    public ICodeStructureProvider Select(string language)
    {
        var treeSitter = _providers.FirstOrDefault(p => p.Id == "tree-sitter" && p.CanHandle(language));
        if (treeSitter is not null)
            return treeSitter;

        return _providers.First(p => p.Id == "regex-fallback");
    }
}
