using Hypa.Runtime.Domain.Common;
using Hypa.Runtime.Domain.Runner;

namespace Hypa.Runtime.Application.Ports;

public interface ICommandRunnerService
{
    Task<Result<BufferedRunOutput, Error>> RunBufferedAsync(
        CommandInvocation invocation,
        CompressionOptions options,
        CancellationToken ct);

    Task<Result<int, Error>> RunPassthroughAsync(
        CommandInvocation invocation,
        CancellationToken ct);
}
