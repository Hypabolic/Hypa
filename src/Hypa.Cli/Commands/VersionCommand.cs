using System.CommandLine;
using System.Reflection;

namespace Hypa.Cli.Commands;

public sealed class VersionCommand
{
    public Command Build()
    {
        var cmd = new Command("version", "Show version information.");
        cmd.SetHandler(() =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? assembly.GetName().Version?.ToString(3)
                ?? "0.0.0";
            Console.WriteLine($"hypa {version}");
        });
        return cmd;
    }
}
