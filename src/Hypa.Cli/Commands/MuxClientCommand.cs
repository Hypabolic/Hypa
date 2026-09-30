using System.CommandLine;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;

namespace Hypa.Cli.Commands;

public sealed class MuxClientCommand(
    IAttachConfigLoader? attachConfig = null,
    MuxAttachService? liveAttach = null)
{
    public IReadOnlyList<Command> BuildAll() =>
    [
        BuildPassthrough("ping", "Ping the mux control plane (does not start the server)."),
        BuildPassthrough("snapshot", "Print the mux session snapshot (does not start the server)."),
        BuildPassthrough("workspace", "Mux workspace operations (does not start the server)."),
        BuildPassthrough("tab", "Mux tab operations (does not start the server)."),
        BuildPassthrough("pane", "Mux pane operations (does not start the server)."),
        BuildPassthrough("layout", "Mux layout export (does not start the server)."),
        BuildPassthrough("agent", "Mux agent operations (does not start the server)."),
        BuildPassthrough("integration", "Official agent integrations (does not start the server)."),
        BuildPassthrough("plugin", "Local plugin host (does not start the server)."),
        BuildPassthrough("notification", "Mux notification show (does not start the server)."),
        BuildPassthrough("events", "One-shot mux event wait (does not start the server)."),
        BuildPassthrough("rpc", "Call a mux RPC method (does not start the server)."),
        ClientActionCommand.Build(attachConfig),
    ];

    private Command BuildPassthrough(string name, string description)
    {
        var cmd = new Command(name, description)
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            return await ControlPlaneCliCommands.Run(GetCliArgs(), attachConfig, liveAttach)
                .ConfigureAwait(false);
        });
        return cmd;
    }

    private static string[] GetCliArgs()
    {
        var all = Environment.GetCommandLineArgs();
        return all.Length <= 1 ? [] : all[1..];
    }
}
