using System.Runtime.InteropServices;

namespace Hypa.Cli;

/// <summary>
/// Linux host requirement for OpenSSL 3.
/// .NET loads libssl.so.3 and libcrypto.so.3 from the host.
/// The check runs only for commands that use TLS or QUIC.
/// Local commands, help, and version do not need the libraries.
/// </summary>
internal static class LinuxOpenSsl3Requirement
{
    // Keep this text identical to the message in install.sh.
    internal const string MissingMessage =
        "error: OpenSSL 3 is required. libssl.so.3 and libcrypto.so.3 were not found.\n"
        + "Install OpenSSL 3 with one of these commands:\n"
        + "  apt-get install -y libssl3\n"
        + "  dnf install -y openssl-libs\n"
        + "  zypper install -y libopenssl3\n";

    internal static readonly string[] LibraryDirectories =
    [
        "/lib",
        "/usr/lib",
        "/lib64",
        "/usr/lib64",
        "/usr/lib/x86_64-linux-gnu",
        "/lib/x86_64-linux-gnu",
        "/usr/lib/aarch64-linux-gnu",
        "/lib/aarch64-linux-gnu",
    ];

    internal static bool TryReject(string[] args, TextWriter error)
    {
        if (!OperatingSystem.IsLinux())
            return false;
        if (!CommandNeedsOpenSsl3(args))
            return false;
        if (HostHasOpenSsl3())
            return false;

        error.Write(MissingMessage);
        return true;
    }

    // Options that take the next argv token. A boolean flag must not be listed here.
    private static readonly string[] ValueOptions =
    [
        "--session",
        "--socket",
        "--timeout-ms",
        "--cwd",
        "--remote",
        "--remote-keybindings",
        "--connect-placement",
        "--client",
        "--tab",
        "--placement",
        "--page",
        "--endpoint",
        "--generation",
        "--db",
        "--identity",
        "--name",
        "--target",
        "--store",
    ];

    internal static bool CommandNeedsOpenSsl3(string[] args)
    {
        if (args.Length == 0)
            return false;

        foreach (var arg in args)
        {
            if (arg is "--help" or "-h" or "-?" or "--version" or "help")
                return false;
        }

        var positionals = Positionals(args);
        // `hypa --remote HOST` is root attach. It has no positional command.
        if (positionals.Count == 0)
            return RootRemoteAttach(args);

        return positionals[0] switch
        {
            "version" => false,
            "connectivity" => true,
            "update" => true,
            "doctor" => positionals.Count == 1 || positionals[1] != "code-intelligence",
            "attach" => AttachUsesCrypto(args),
            "client" => ClientConnectsRemotePlacement(positionals),
            "work" => WorkPairingUsesCrypto(positionals),
            // Pair and approve sign with ECDSA and hash keys with SHA-256.
            // List and revoke only read and write the local pairing store.
            "device" => positionals.Count >= 2 && positionals[1] is "pair" or "approve",
            _ => false,
        };
    }

    private static bool RootRemoteAttach(string[] args)
    {
        foreach (var arg in args)
        {
            if (OptionIs(arg, "--remote") || OptionIs(arg, "--handoff"))
                return true;
        }

        return false;
    }

    private static bool AttachUsesCrypto(string[] args)
    {
        foreach (var arg in args)
        {
            if (OptionIs(arg, "--remote")
                || OptionIs(arg, "--connect-placement")
                || arg == "--handoff")
            {
                return true;
            }
        }

        return false;
    }

    private static bool ClientConnectsRemotePlacement(List<string> positionals) =>
        positionals.Count >= 3
        && positionals[1] == "action"
        && positionals[2] == "placement.connect";

    private static bool WorkPairingUsesCrypto(List<string> positionals)
    {
        if (positionals.Count >= 2 && positionals[1] == "handoff")
            return true;

        return positionals.Count >= 3
            && positionals[1] == "placements"
            && positionals[2] is "add-quic" or "add-ssh";
    }

    private static List<string> Positionals(string[] args)
    {
        var positionals = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                for (var j = i + 1; j < args.Length; j++)
                    positionals.Add(args[j]);
                break;
            }

            if (arg.StartsWith('-'))
            {
                if (!arg.Contains('=')
                    && IsValueOption(arg)
                    && i + 1 < args.Length
                    && !args[i + 1].StartsWith('-'))
                {
                    i++;
                }

                continue;
            }

            positionals.Add(arg);
        }

        return positionals;
    }

    private static bool IsValueOption(string arg)
    {
        foreach (var option in ValueOptions)
        {
            if (arg == option)
                return true;
        }

        return false;
    }

    private static bool OptionIs(string arg, string name) =>
        arg == name || arg.StartsWith(name + "=", StringComparison.Ordinal);

    internal static bool IsSatisfied(string? ldconfigText, Func<string, bool> pathExists)
    {
        if (LdconfigLists(ldconfigText, "libssl.so.3") && LdconfigLists(ldconfigText, "libcrypto.so.3"))
            return true;

        var ssl = false;
        var crypto = false;
        foreach (var directory in LibraryDirectories)
        {
            if (!ssl && pathExists(Path.Combine(directory, "libssl.so.3")))
                ssl = true;
            if (!crypto && pathExists(Path.Combine(directory, "libcrypto.so.3")))
                crypto = true;
            if (ssl && crypto)
                return true;
        }

        return false;
    }

    internal static bool LdconfigLists(string? text, string soname)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < soname.Length)
                continue;
            if (!line.StartsWith(soname, StringComparison.Ordinal))
                continue;
            if (line.Length == soname.Length)
                return true;

            var next = line[soname.Length];
            if (next is ' ' or '\t' or '(')
                return true;
        }

        return false;
    }

    internal static bool LibrariesLoad(Func<string, bool> tryLoad) =>
        tryLoad("libssl.so.3") && tryLoad("libcrypto.so.3");

    private static bool HostHasOpenSsl3() =>
        LibrariesLoad(static name =>
        {
            try
            {
                return NativeLibrary.TryLoad(name, out _);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or ArgumentException)
            {
                return false;
            }
        });
}
