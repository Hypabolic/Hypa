using Hypa.Placement.Application;
using Hypa.Placement.Domain;

namespace Hypa.Cli.Mux;

/// <summary>
/// Root attach flags for SSH.
/// </summary>
public sealed record RemoteAttachArgs
{
    public required string Target { get; init; }
    public RemoteKeybindingsMode Keybindings { get; init; } = RemoteKeybindingsMode.Local;
    public bool LiveHandoff { get; init; }

    public static bool TryParse(
        IReadOnlyList<string> args,
        out RemoteAttachArgs? remote,
        out string? error)
    {
        remote = null;
        error = null;
        string? target = null;
        var keybindings = RemoteKeybindingsMode.Local;
        var keybindingsSeen = false;
        var liveHandoff = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
                break;
            if (arg == "--handoff")
            {
                liveHandoff = true;
                continue;
            }

            if (arg == "--remote")
            {
                if (target is not null)
                {
                    error = "--remote can only be specified once";
                    return false;
                }

                if (i + 1 >= args.Count)
                {
                    error = "missing value for --remote";
                    return false;
                }

                if (!RemoteTarget.TryValidate(args[++i], out var normalized, out error))
                    return false;
                target = normalized;
                continue;
            }

            if (arg.StartsWith("--remote=", StringComparison.Ordinal))
            {
                if (target is not null)
                {
                    error = "--remote can only be specified once";
                    return false;
                }

                if (!RemoteTarget.TryValidate(arg["--remote=".Length..], out var normalized, out error))
                    return false;
                target = normalized;
                continue;
            }

            if (arg == "--remote-keybindings")
            {
                if (keybindingsSeen)
                {
                    error = "--remote-keybindings can only be specified once";
                    return false;
                }

                if (i + 1 >= args.Count)
                {
                    error = "missing value for --remote-keybindings";
                    return false;
                }

                if (!TryParseKeybindings(args[++i], out keybindings, out error))
                    return false;
                keybindingsSeen = true;
                continue;
            }

            if (arg.StartsWith("--remote-keybindings=", StringComparison.Ordinal))
            {
                if (keybindingsSeen)
                {
                    error = "--remote-keybindings can only be specified once";
                    return false;
                }

                if (!TryParseKeybindings(arg["--remote-keybindings=".Length..], out keybindings, out error))
                    return false;
                keybindingsSeen = true;
            }
        }

        if (target is null)
        {
            if (keybindingsSeen)
            {
                error = "--remote-keybindings requires --remote";
                return false;
            }

            if (liveHandoff)
            {
                error = "--handoff requires --remote";
                return false;
            }

            return true;
        }

        remote = new RemoteAttachArgs
        {
            Target = target,
            Keybindings = keybindings,
            LiveHandoff = liveHandoff,
        };
        return true;
    }

    public static bool TryParseKeybindings(
        string value,
        out RemoteKeybindingsMode mode,
        out string? error)
    {
        mode = RemoteKeybindingsMode.Local;
        error = null;
        if (value == "local")
        {
            mode = RemoteKeybindingsMode.Local;
            return true;
        }

        if (value == "server")
        {
            mode = RemoteKeybindingsMode.Server;
            return true;
        }

        error = "--remote-keybindings must be 'local' or 'server'";
        return false;
    }
}
