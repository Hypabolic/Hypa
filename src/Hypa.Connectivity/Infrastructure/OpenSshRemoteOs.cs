using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Windows as a remote host is refused.
/// </summary>
public static class OpenSshRemoteOs
{
    public const string Linux = "linux";
    public const string MacOs = "macos";
    public const string Windows = "windows";
    public const string Unknown = "unknown";

    public static string Classify(string? uname)
    {
        if (string.IsNullOrWhiteSpace(uname))
            return Unknown;

        var trimmed = uname.Trim();
        if (trimmed.Equals("Linux", StringComparison.OrdinalIgnoreCase))
            return Linux;
        if (trimmed.Equals("Darwin", StringComparison.OrdinalIgnoreCase))
            return MacOs;
        if (IsWindowsUname(trimmed))
            return Windows;
        return Unknown;
    }

    public static bool IsSupported(string classification) =>
        classification is Linux or MacOs;

    public static bool IsWindowsUname(string uname)
    {
        if (uname.StartsWith("MINGW", StringComparison.OrdinalIgnoreCase)
            || uname.StartsWith("MSYS", StringComparison.OrdinalIgnoreCase)
            || uname.StartsWith("CYGWIN", StringComparison.OrdinalIgnoreCase)
            || uname.Equals("Windows_NT", StringComparison.OrdinalIgnoreCase)
            || uname.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static ConnectivityOutcome RefuseWindows() =>
        ConnectivityOutcome.Failure(
            PeerReachReasons.WindowsRemoteRefused,
            "Windows as a remote host is refused.");
}
