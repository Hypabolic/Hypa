using Hypa.Runtime.Domain.Runner;

namespace Hypa.Runtime.Application.Ports;

public interface IOutputCompressor
{
    string Id { get; }
    bool CanHandle(CommandInvocation invocation);
    CompressionResult Compress(CommandInvocation invocation, CommandOutput output, CompressionOptions options);
}
