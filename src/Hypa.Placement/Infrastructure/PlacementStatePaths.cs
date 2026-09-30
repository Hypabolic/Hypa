using System.Diagnostics;

namespace Hypa.Placement.Infrastructure;

/// <summary>
/// Independent Placement directory state root.
/// Do not use UserProfile or HOME. Dest HOME can remap those.
/// </summary>
public static class PlacementStatePaths
{
    public const string OperatorHomeVariable = "HYPA_OPERATOR_HOME";

    public static string ResolveFromEnvironment() =>
        Resolve(
            Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
            Environment.GetEnvironmentVariable(OperatorHomeVariable),
            ResolveLoginHome());

    public static string Resolve(
        string? xdgStateHome,
        string? operatorHome,
        string? loginHome)
    {
        if (!string.IsNullOrWhiteSpace(xdgStateHome))
            return Path.GetFullPath(Path.Combine(xdgStateHome, "hypa", "placements"));
        if (!string.IsNullOrWhiteSpace(operatorHome))
            return Path.GetFullPath(Path.Combine(operatorHome, ".local", "state", "hypa", "placements"));
        if (!string.IsNullOrWhiteSpace(loginHome))
            return Path.GetFullPath(Path.Combine(loginHome, ".local", "state", "hypa", "placements"));
        throw new InvalidDataException("placement state root is missing");
    }

    public static string? ResolveLoginHome()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return TryPasswdHome();
    }

    private static string? TryPasswdHome()
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user))
            return null;

        var fromPasswd = TryPasswdFileHome(user);
        if (!string.IsNullOrWhiteSpace(fromPasswd))
            return fromPasswd;

        if (OperatingSystem.IsMacOS())
        {
            var fromDscl = TryMacDirectoryHome(user);
            if (!string.IsNullOrWhiteSpace(fromDscl))
                return fromDscl;

            var users = Path.Combine("/Users", user);
            if (Directory.Exists(users))
                return users;
        }

        return null;
    }

    private static string? TryPasswdFileHome(string user)
    {
        try
        {
            if (!File.Exists("/etc/passwd"))
                return null;

            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var parts = line.Split(':');
                if (parts.Length < 6)
                    continue;
                if (!string.Equals(parts[0], user, StringComparison.Ordinal))
                    continue;
                var home = parts[5];
                if (!string.IsNullOrWhiteSpace(home))
                    return home;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string? TryMacDirectoryHome(string user)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/dscl",
                ArgumentList = { ".", "-read", "/Users/" + user, "NFSHomeDirectory" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            if (process.ExitCode != 0)
                return null;
            foreach (var token in output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith('/') && Directory.Exists(token))
                    return token;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }
}
