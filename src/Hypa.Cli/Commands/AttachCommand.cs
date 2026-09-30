using System.CommandLine;
using Hypa.Cli.Mux;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;

namespace Hypa.Cli.Commands;

public sealed class AttachCommand(MuxAttachService attachService)
{
    internal const string RemoteDestinationOptionDescription =
        "Attach stays local. Detach copy does not advise mux stop.";

    internal const string RemoteOptionDescription =
        "Attach to a remote mux through OpenSSH. This is not Atomic gateway attach.";

    internal const string RemoteKeybindingsOptionDescription =
        "Keybinding source for --remote: local or server. Default is local.";

    internal const string HandoffOptionDescription =
        "Experimental Unix-only remote server replacement. Requires --remote. This does not move Work.";

    public Command Build()
    {
        var sessionOpt = new Option<string?>("--session") { Description = "Mux session name." };
        var cwdOpt = new Option<string?>("--cwd")
        {
            Description = "Workspace working directory for a newly started mux server. When omitted, terminal.new_cwd applies.",
        };
        var onceOpt = new Option<bool>("--once") { Description = "Ping the mux server, print the snapshot, and exit (no wait)." };
        var remoteDestinationOpt = new Option<bool>("--remote-destination")
        {
            Description = RemoteDestinationOptionDescription,
        };
        var remoteOpt = new Option<string?>("--remote")
        {
            Description = RemoteOptionDescription,
        };
        var remoteKeybindingsOpt = new Option<string?>("--remote-keybindings")
        {
            Description = RemoteKeybindingsOptionDescription,
        };
        var handoffOpt = new Option<bool>("--handoff")
        {
            Description = HandoffOptionDescription,
        };
        var connectPlacementOpt = new Option<string?>("--connect-placement")
        {
            Description = "After local attach is live, run the same Cubes Connect path as a sidebar click.",
        };

        var cmd = new Command(
            "attach",
            "Ensure the mux server is running and attach as a thin client.");
        cmd.Add(sessionOpt);
        cmd.Add(cwdOpt);
        cmd.Add(onceOpt);
        cmd.Add(remoteDestinationOpt);
        cmd.Add(remoteOpt);
        cmd.Add(remoteKeybindingsOpt);
        cmd.Add(handoffOpt);
        cmd.Add(connectPlacementOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var sessionResult = parseResult.GetResult(sessionOpt);
            var sessionExplicit = sessionResult is { Tokens.Count: > 0 };
            var cwdResult = parseResult.GetResult(cwdOpt);
            var cwdExplicit = cwdResult is { Tokens.Count: > 0 };
            var cwd = cwdExplicit ? parseResult.GetValue(cwdOpt) : null;
            var once = parseResult.GetValue(onceOpt);
            var remoteDestination = parseResult.GetValue(remoteDestinationOpt);
            if (!TryReadRemote(
                    parseResult.GetValue(remoteOpt),
                    parseResult.GetValue(remoteKeybindingsOpt),
                    parseResult.GetValue(handoffOpt),
                    out var remote,
                    out var remoteError))
            {
                await Console.Error.WriteLineAsync("hypa attach: " + remoteError).ConfigureAwait(false);
                return 2;
            }

            return await attachService
                .AttachAsync(
                    parseResult.GetValue(sessionOpt),
                    cwd,
                    once,
                    socketOverride: null,
                    sessionExplicit,
                    ct,
                    remoteDestination,
                    remote: remote,
                    connectPlacementId: parseResult.GetValue(connectPlacementOpt))
                .ConfigureAwait(false);
        });
        return cmd;
    }

    internal static bool TryReadRemote(
        string? remoteTarget,
        string? keybindings,
        bool handoff,
        out RemoteAttachArgs? remote,
        out string? error)
    {
        remote = null;
        error = null;
        if (string.IsNullOrEmpty(remoteTarget))
        {
            if (!string.IsNullOrEmpty(keybindings))
            {
                error = "--remote-keybindings requires --remote";
                return false;
            }

            if (handoff)
            {
                error = "--handoff requires --remote";
                return false;
            }

            return true;
        }

        if (!RemoteTarget.TryValidate(remoteTarget, out var target, out error))
            return false;

        var mode = RemoteKeybindingsMode.Local;
        if (!string.IsNullOrEmpty(keybindings)
            && !RemoteAttachArgs.TryParseKeybindings(keybindings, out mode, out error))
        {
            return false;
        }

        remote = new RemoteAttachArgs
        {
            Target = target,
            Keybindings = mode,
            LiveHandoff = handoff,
        };
        return true;
    }
}
