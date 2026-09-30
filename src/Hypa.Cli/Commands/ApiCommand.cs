using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.ControlPlane;

namespace Hypa.Cli.Commands;

public sealed class ApiCommand(IAttachConfigLoader? attachConfig = null)
{
    public Command Build()
    {
        var cmd = new Command("api", "Print control-plane RPC results.");
        cmd.Add(BuildSnapshot());
        cmd.Add(BuildSchema());
        return cmd;
    }

    private Command BuildSnapshot()
    {
        var sessionOpt = new Option<string?>("--session")
        {
            Description = "Mux session name.",
        };
        var socketOpt = new Option<string?>("--socket") { Description = "Unix socket path." };
        var cmd = new Command("snapshot", "Print session.snapshot JSON.");
        cmd.Add(sessionOpt);
        cmd.Add(socketOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var sessionOption = parseResult.GetValue(sessionOpt);
            if (!TryResolveSession(sessionOption, out var session))
                return 1;
            var socketOverride = parseResult.GetValue(socketOpt);
            string socket;
            try
            {
                socket = socketOverride ?? UnixSocketServer.ResolveSocketPath(session);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 2;
            }

            try
            {
                await using var client = new ControlPlaneClient(socket);
                await client.ConnectAsync(ct).ConfigureAwait(false);
                var result = await client.CallAsync(ProtocolMethods.SessionSnapshot, parameters: new JsonObject(), ct)
                    .ConfigureAwait(false);
                Console.WriteLine(result.ValueKind == System.Text.Json.JsonValueKind.Undefined
                    ? "{}"
                    : result.GetRawText());
                return 0;
            }
            catch (ControlPlaneClientTimeoutException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return 3;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Failed to connect to {socket}: {ex.Message}")
                    .ConfigureAwait(false);
                return 2;
            }
        });
        return cmd;
    }

    private static Command BuildSchema()
    {
        var jsonOpt = new Option<bool>("--json")
        {
            Description = "Print the full schema JSON.",
        };
        var cmd = new Command("schema", "Print the Hypa protocol schema.");
        cmd.Add(jsonOpt);
        cmd.SetAction(parseResult =>
        {
            var document = ProtocolSchemaCatalog.Create();
            if (parseResult.GetValue(jsonOpt))
            {
                Console.Write(JsonSerializer.Serialize(
                    document,
                    ProtocolJsonContext.Default.ProtocolSchemaDocument));
            }
            else
            {
                Console.Write(ProtocolSchemaText.Render(document));
            }

            return 0;
        });
        return cmd;
    }

    private bool TryResolveSession(string? sessionOption, out string session)
    {
        if (!AttachConfigErrors.TryLoad(attachConfig, Console.Error, out var config))
        {
            session = "";
            return false;
        }

        session = AttachSessionResolver.Resolve(sessionOption, config);
        return true;
    }
}
