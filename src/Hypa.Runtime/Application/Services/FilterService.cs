using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Filters;

namespace Hypa.Runtime.Application.Services;

public sealed class FilterService(IFilterRepository repository, IFilterEngine engine)
{
    public IReadOnlyList<CompiledFilterDefinition> ListFilters() => repository.GetAll();

    public string TestFilter(string id, string inputText)
    {
        var filter = repository.GetById(id);
        if (filter is null)
            return $"Filter '{id}' not found.";
        var result = engine.Apply(filter, inputText);
        return result.Text;
    }

    public IReadOnlyList<CompiledFilterDefinition> GetApplicableFilters(string executable, string? command = null)
    {
        var all = repository.GetAll();
        return all
            .Select((filter, index) => new { Filter = filter, Index = index })
            .Where(item =>
                (item.Filter.AppliesTo.Count == 0 || item.Filter.AppliesTo.Contains(executable))
                && (item.Filter.CompiledMatchCommand is null
                    || (command is not null && item.Filter.CompiledMatchCommand.IsMatch(command))))
            .OrderBy(item => item.Filter.AppliesTo.Count == 0 ? 1 : 0)
            .ThenBy(item => item.Filter.CompiledMatchCommand is null ? 1 : 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Filter)
            .ToList();
    }
}
