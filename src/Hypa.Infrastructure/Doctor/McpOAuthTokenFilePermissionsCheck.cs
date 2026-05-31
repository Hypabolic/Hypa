using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Hooks;

namespace Hypa.Infrastructure.Doctor;

public sealed class McpOAuthTokenFilePermissionsCheck : IDoctorCheck
{
    private readonly string _tokenFilePath;

    public McpOAuthTokenFilePermissionsCheck(string dataDirectory)
    {
        _tokenFilePath = Path.Combine(dataDirectory, "mcp-oauth-tokens.json");
    }

    public string Category => "MCP";

    public DoctorCheckResult Run()
    {
        if (!File.Exists(_tokenFilePath))
            return new DoctorCheckResult(
                "OAuth token permissions",
                "not present",
                DoctorStatus.Ok);

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return new DoctorCheckResult(
                "OAuth token permissions",
                "platform skipped",
                DoctorStatus.Ok);

        UnixFileMode mode;
        try
        {
            mode = File.GetUnixFileMode(_tokenFilePath);
        }
        catch (Exception ex)
        {
            return new DoctorCheckResult(
                "OAuth token permissions",
                "unreadable",
                DoctorStatus.Ok,
                $"Could not read file permissions: {ex.Message}");
        }

        const UnixFileMode groupOrOtherBits =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        if ((mode & groupOrOtherBits) != 0)
            return new DoctorCheckResult(
                "OAuth token permissions",
                $"insecure ({FormatMode(mode)})",
                DoctorStatus.Warn,
                $"Token file is group/world-readable. Run: chmod 600 {_tokenFilePath}");

        return new DoctorCheckResult(
            "OAuth token permissions",
            $"secure ({FormatMode(mode)})",
            DoctorStatus.Ok);
    }

    private static string FormatMode(UnixFileMode mode)
    {
        var u = ((int)mode >> 6) & 7;
        var g = ((int)mode >> 3) & 7;
        var o = (int)mode & 7;
        return $"{u}{g}{o}";
    }
}
