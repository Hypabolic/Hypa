using Hypa.Runtime.Domain.Filters;

namespace Hypa.Runtime.Application.Ports;

public interface IFilterRepository
{
    IReadOnlyList<CompiledFilterDefinition> GetAll();
    CompiledFilterDefinition? GetById(string id, FilterScope? scope = null);
}
