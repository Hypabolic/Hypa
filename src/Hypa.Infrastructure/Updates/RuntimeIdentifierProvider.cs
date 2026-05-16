using System.Runtime.InteropServices;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Updates;

public sealed class RuntimeIdentifierProvider : IRuntimeIdentifierProvider
{
    public string RuntimeIdentifier { get; } = Detect(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
        RuntimeInformation.ProcessArchitecture);

    internal static string Detect(bool isWindows, bool isOsx, Architecture arch)
    {
        var os = isWindows ? "win" : isOsx ? "osx" : "linux";
        var archStr = arch switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        return $"{os}-{archStr}";
    }
}
