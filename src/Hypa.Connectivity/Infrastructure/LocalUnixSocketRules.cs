using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Local Unix socket path rules. The path stays on the mux host.
/// It is not a join field and not a rendezvous URL.
/// </summary>
public static class LocalUnixSocketRules
{
    public static ConnectivityOutcome ValidatePath(string? socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "unix socket path is required");
        }

        var trimmed = socketPath.Trim();
        if (trimmed.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("://", StringComparison.Ordinal)
            || trimmed.Contains("0.0.0.0", StringComparison.Ordinal)
            || trimmed.Contains("[::]", StringComparison.Ordinal))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.RendezvousUrlInvalid,
                "unix socket path is local and is not a rendezvous url");
        }

        if (Path.GetDirectoryName(trimmed) is not { Length: > 0 })
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "unix socket path has no parent directory");
        }

        return ConnectivityOutcome.Success();
    }

    public static ConnectivityOutcome EnsurePrivate(string socketPath)
    {
        var pathOk = ValidatePath(socketPath);
        if (!pathOk.Ok)
            return pathOk;

        if (OperatingSystem.IsWindows())
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "local unix bridge requires linux or macos");
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "local unix bridge requires linux or macos");
        }

        var directory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "unix socket parent is missing");
        }

        if (!File.Exists(socketPath) && !Path.Exists(socketPath))
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.PeerUnavailable,
                "unix socket is missing");
        }

#pragma warning disable CA1416
        var dirWant = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var dirMode = File.GetUnixFileMode(directory);
        if ((dirMode & ~dirWant) != 0 || (dirMode & dirWant) != dirWant)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "unix socket parent must be mode 0700");
        }

        var sockWant = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var sockMode = File.GetUnixFileMode(socketPath);
        if ((sockMode & ~sockWant) != 0 || (sockMode & sockWant) != sockWant)
        {
            return ConnectivityOutcome.Failure(
                ConnectivityReasons.Unauthorized,
                "unix socket must be mode 0600");
        }
#pragma warning restore CA1416

        return ConnectivityOutcome.Success();
    }
}
