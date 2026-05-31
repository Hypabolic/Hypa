using Hypa.Runtime.Application.Ports;

namespace Hypa.Runtime.Application.Services;

public sealed class CodeStructureProviderRegistry(IEnumerable<ICodeStructureProvider> providers)
{
    private readonly IReadOnlyList<ICodeStructureProvider> _providers = providers.ToList();

    public IReadOnlyList<ICodeStructureProvider> Providers => _providers;

    public ICodeStructureProvider Select(string language)
    {
        return _providers.FirstOrDefault(p => p.Id != "regex-fallback" && p.CanHandle(language))
            ?? _providers.First(p => p.Id == "regex-fallback");
    }
}
