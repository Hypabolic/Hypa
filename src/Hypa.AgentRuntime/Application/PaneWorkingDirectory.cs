namespace Hypa.AgentRuntime.Application;

/// <summary>Validation shared by live directory observations and terminal reports.</summary>
public static class PaneWorkingDirectory
{
    public static string? Validate(string? path, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || SafeDisplayText.ContainsUnsafeControl(path))
            return null;
        try
        {
            return directoryExists(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? FromOsc7(string report, Func<string, bool> directoryExists)
    {
        for (var i = 0; i < report.Length; i++)
        {
            if (report[i] != '%')
                continue;
            if (i + 2 >= report.Length || !Uri.IsHexDigit(report[i + 1]) || !Uri.IsHexDigit(report[i + 2]))
                return null;
            i += 2;
        }
        if (SafeDisplayText.ContainsUnsafeControl(report)
            || !Uri.TryCreate(report, UriKind.Absolute, out var uri)
            || !uri.IsFile || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
            return null;
        var host = uri.Host;
        if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !host.Equals(Environment.MachineName + ".local", StringComparison.OrdinalIgnoreCase))
            return null;
        return Validate(Uri.UnescapeDataString(uri.AbsolutePath), directoryExists);
    }
}
