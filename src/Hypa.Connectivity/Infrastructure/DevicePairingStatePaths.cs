using System.Diagnostics;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Operator pairing state root. Dest HOME is not this path.
/// Pairing store file: pairing.json under the connectivity segment.
/// </summary>
public static class DevicePairingStatePaths
{
    public static string ResolveFromEnvironment() =>
        Resolve(
            Environment.GetEnvironmentVariable(DevicePairingPaths.PairingStoreVariable),
            Environment.GetEnvironmentVariable("XDG_STATE_HOME"),
            Environment.GetEnvironmentVariable(DevicePairingPaths.OperatorHomeVariable),
            ResolveLoginHome());

    public static string Resolve(
        string? pairingStore,
        string? xdgStateHome,
        string? operatorHome,
        string? loginHome)
    {
        if (!string.IsNullOrWhiteSpace(pairingStore))
            return Path.GetFullPath(pairingStore);

        if (!string.IsNullOrWhiteSpace(xdgStateHome))
        {
            return Path.GetFullPath(Path.Combine(
                xdgStateHome,
                "hypa",
                DevicePairingPaths.StateSegment));
        }

        if (!string.IsNullOrWhiteSpace(operatorHome))
        {
            return Path.GetFullPath(Path.Combine(
                operatorHome,
                ".local",
                "state",
                "hypa",
                DevicePairingPaths.StateSegment));
        }

        if (!string.IsNullOrWhiteSpace(loginHome))
        {
            return Path.GetFullPath(Path.Combine(
                loginHome,
                ".local",
                "state",
                "hypa",
                DevicePairingPaths.StateSegment));
        }

        throw new InvalidDataException("pairing state root is missing");
    }

    public static string StoreFile(string directory) =>
        Path.Combine(Path.GetFullPath(directory), DevicePairingPaths.StoreFileName);

    public static string FallbackKeyDirectory(string directory) =>
        Path.Combine(Path.GetFullPath(directory), DevicePairingPaths.KeyDirectoryName);

    public static string? ResolveLoginHome()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return TryPasswdHome();
    }

    public static string? ResolvePlatformKeyRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                return null;
            return Path.Combine(
                local,
                DevicePairingPaths.PlatformApplicationName,
                DevicePairingPaths.StateSegment,
                DevicePairingPaths.KeyDirectoryName);
        }

        if (OperatingSystem.IsMacOS())
        {
            var appSupport = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appSupport))
            {
                var home = TryPasswdHome();
                if (string.IsNullOrWhiteSpace(home))
                    return null;
                appSupport = Path.Combine(home, "Library", "Application Support");
            }

            return Path.Combine(
                appSupport,
                DevicePairingPaths.PlatformApplicationName,
                DevicePairingPaths.StateSegment,
                DevicePairingPaths.KeyDirectoryName);
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return Path.Combine(
                xdg,
                DevicePairingPaths.PlatformApplicationName.ToLowerInvariant(),
                DevicePairingPaths.StateSegment,
                DevicePairingPaths.KeyDirectoryName);
        }

        var login = TryPasswdHome();
        if (string.IsNullOrWhiteSpace(login))
            return null;
        return Path.Combine(
            login,
            ".local",
            "share",
            DevicePairingPaths.PlatformApplicationName.ToLowerInvariant(),
            DevicePairingPaths.StateSegment,
            DevicePairingPaths.KeyDirectoryName);
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
