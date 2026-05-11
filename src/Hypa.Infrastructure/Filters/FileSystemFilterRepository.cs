using System.Security.Cryptography;
using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Filters;

namespace Hypa.Infrastructure.Filters;

public sealed class FileSystemFilterRepository(ITrustStore trustStore, IProjectRootDetector projectRootDetector) : IFilterRepository
{
    private readonly Lazy<IReadOnlyList<CompiledFilterDefinition>> _all = new(() => Load(trustStore, projectRootDetector));

    public IReadOnlyList<CompiledFilterDefinition> GetAll() => _all.Value;

    public CompiledFilterDefinition? GetById(string id, FilterScope? scope = null) =>
        _all.Value.FirstOrDefault(f => f.Id == id && (scope is null || f.Scope == scope));

    private static IReadOnlyList<CompiledFilterDefinition> Load(ITrustStore trustStore, IProjectRootDetector projectRootDetector)
    {
        var result = new List<CompiledFilterDefinition>(BuiltInFilters.All);

        var userGlobalDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "hypa", "filters");
        result.AddRange(LoadFromDirectory(userGlobalDir, FilterScope.UserGlobal).Select(f => f.Definition));

        var projectRoot = projectRootDetector.Detect(Directory.GetCurrentDirectory());
        if (projectRoot is not null)
        {
            var projectLocalDir = Path.Combine(projectRoot, ".hypa", "filters");
            var projectLocalFilters = LoadFromDirectory(projectLocalDir, FilterScope.ProjectLocal);

            foreach (var filter in projectLocalFilters)
            {
                var hash = ComputeHash(filter.FilePath);
                if (trustStore.IsTrusted(projectRoot, filter.FilePath, hash))
                    result.Add(filter.Definition);
            }
        }

        return result;
    }

    private static IEnumerable<LoadedFilter> LoadFromDirectory(string directory, FilterScope scope)
    {
        if (!Directory.Exists(directory)) yield break;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            FilterDefinition? def;
            try
            {
                var json = File.ReadAllText(file);
                def = JsonSerializer.Deserialize(json, FilterJsonContext.Default.FilterDefinition);
            }
            catch
            {
                continue;
            }

            if (def is null) continue;
            yield return new LoadedFilter(BuiltInFilters.Compile(def with { Scope = scope }), file);
        }
    }

    private sealed record LoadedFilter(CompiledFilterDefinition Definition, string FilePath);

    internal static string ComputeHash(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
