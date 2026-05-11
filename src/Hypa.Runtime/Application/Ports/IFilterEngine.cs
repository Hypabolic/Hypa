using Hypa.Runtime.Domain.Filters;

namespace Hypa.Runtime.Application.Ports;

public interface IFilterEngine
{
    FilterResult Apply(CompiledFilterDefinition filter, string text);
}
