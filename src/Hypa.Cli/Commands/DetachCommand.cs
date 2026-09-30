using System.CommandLine;

namespace Hypa.Cli.Commands;

public sealed class DetachCommand
{
    public Command Build()
    {
        var cmd = new Command("detach", "Leave the mux client. The server stays running.");
        cmd.SetAction(_ =>
        {
            Console.WriteLine("Detached. Mux server is still running.");
            Console.WriteLine("Stop the server with: hypa mux stop [--session NAME]");
        });
        return cmd;
    }
}
