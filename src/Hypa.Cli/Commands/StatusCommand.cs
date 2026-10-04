using System.CommandLine;
using Hypa.AgentRuntime.Application;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;

namespace Hypa.Cli.Commands;

public sealed class StatusCommand(MuxSessionCatalog catalog, IAttachConfigLoader? attachConfig = null)
{
    public Command Build()
    {
        var cmd = new Command("status", "Report mux client and server status.");
        cmd.Add(BuildClient());
        cmd.Add(BuildServer());
        cmd.SetAction(async (_, ct) =>
        {
            if (!TryResolveSession(out var session))
                return 1;
            PrintClient(session);
            return await PrintServerAsync(session, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    private Command BuildClient()
    {
        var cmd = new Command("client", "Report this process attach and lease state.");
        cmd.SetAction(_ =>
        {
            if (!TryResolveSession(out var session))
                return 1;
            PrintClient(session);
            return 0;
        });
        return cmd;
    }

    private Command BuildServer()
    {
        var cmd = new Command("server", "Report runtime.status.json and a live ping.");
        cmd.SetAction(async (_, ct) =>
        {
            if (!TryResolveSession(out var session))
                return 1;
            return await PrintServerAsync(session, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    private bool TryResolveSession(out string session)
    {
        if (!AttachConfigErrors.TryLoad(attachConfig, Console.Error, out var config))
        {
            session = "";
            return false;
        }

        session = AttachSessionResolver.Resolve(sessionOption: null, config);
        return true;
    }

    private void PrintClient(string session)
    {
        var state = catalog.TryReadAttachState(session);
        Console.WriteLine($"client.session={session}");
        Console.WriteLine($"pack.channel={PackChannelStatus.Report()}");
        Console.WriteLine($"client.tty={(!(Console.IsInputRedirected || Console.IsOutputRedirected) ? "true" : "false")}");
        if (state is null)
        {
            Console.WriteLine("client.attached=false");
            return;
        }

        Console.WriteLine($"client.attached={(state.Attached ? "true" : "false")}");
        Console.WriteLine($"client.pane_id={state.PaneId}");
        Console.WriteLine($"client.input_lease_id={state.InputLeaseId}");
        Console.WriteLine($"client.resize_lease_id={state.ResizeLeaseId}");
        Console.WriteLine($"client.connections={state.Connections}");
    }

    private async Task<int> PrintServerAsync(string session, CancellationToken ct)
    {
        var status = await catalog.TryReadStatusAsync(session, ct).ConfigureAwait(false);
        string socket;
        try
        {
            socket = UnixSocketServer.ResolveSocketPath(session);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var ping = await MuxControlPlane.TryPingAsync(socket, ct).ConfigureAwait(false);
        Console.WriteLine($"server.session={status?.Session ?? session}");
        Console.WriteLine($"server.socket={status?.Socket ?? socket}");
        Console.WriteLine($"server.cwd={status?.Cwd}");
        Console.WriteLine($"server.pid={status?.Pid ?? 0}");
        Console.WriteLine($"server.alive={(ping is not null ? "true" : "false")}");
        if (ping is not null)
            Console.WriteLine($"server.ping={ping}");
        var check = MuxServerVersionCheck.FromPing(ping, MuxServerVersionCheck.CurrentClientVersion());
        if (check is not null)
        {
            Console.WriteLine($"client.version={check.ClientVersion}");
            Console.WriteLine($"server.version={check.ServerVersion}");
            Console.WriteLine($"server.install_present={(check.InstallPresent ? "true" : "false")}");
            Console.WriteLine($"server.stale={(check.IsStale ? "true" : "false")}");
            if (check.IsStale)
            {
                Console.Error.WriteLine(
                    $"hypa status: {check.Describe(session)} {MuxStaleServerGuard.RestartHint}");
            }
        }

        return ping is null && status is null ? 1 : 0;
    }
}
