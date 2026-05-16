using System.Reflection;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Updates;

public sealed class AssemblyVersionProvider : IVersionProvider
{
    public string CurrentVersion =>
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0";
}
