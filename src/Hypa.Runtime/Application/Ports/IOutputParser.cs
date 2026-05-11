using Hypa.Runtime.Domain.Parsers;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Runtime.Application.Ports;

public interface IOutputParser<T>
{
    string Id { get; }
    bool CanParse(CommandInvocation invocation);
    ParseResult<T> TryParse(CommandOutput output);
}
