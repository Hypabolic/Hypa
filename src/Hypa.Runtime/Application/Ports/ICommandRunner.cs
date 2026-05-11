using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Runtime.Application.Ports;

public interface ICommandRunner
{
    Task<Result<CommandOutput, Error>> RunAsync(CommandInvocation invocation, CancellationToken ct);
}
