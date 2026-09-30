using Hypa.Runtime.Domain.Runner;

namespace Hypa.Runtime.Application.Ports;

public interface IPackageManagerScriptResolver
{
    ResolvedPackageScript? TryResolve(CommandInvocation invocation);
}
