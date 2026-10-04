using System.Reflection;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure;

/// <summary>
/// Reads the product version from the entry assembly and checks that the
/// process executable still exists on disk.
/// </summary>
public sealed class ProcessServerInstallProbe : IServerInstallProbe
{
    // Linux reports a deleted executable as "<path> (deleted)".
    private const string DeletedSuffix = " (deleted)";

    private readonly string? _version;
    private readonly string? _processPath;

    public ProcessServerInstallProbe()
        : this(
            NormalizeVersion(Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion),
            Environment.ProcessPath)
    {
    }

    public ProcessServerInstallProbe(string? version, string? processPath)
    {
        _version = version;
        _processPath = processPath;
    }

    public ServerInstallStatus Probe() => new(_version, IsPresent(_processPath));

    /// <summary>Drop source-link build metadata (<c>1.0.5+abc123</c> → <c>1.0.5</c>).</summary>
    public static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;
        var trimmed = version.Trim();
        var plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? trimmed[..plus] : trimmed;
    }

    private static bool IsPresent(string? processPath)
    {
        // Unknown path: assume present rather than report a false removal.
        if (string.IsNullOrWhiteSpace(processPath))
            return true;
        if (processPath.EndsWith(DeletedSuffix, StringComparison.Ordinal))
            return false;
        try
        {
            return File.Exists(processPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
