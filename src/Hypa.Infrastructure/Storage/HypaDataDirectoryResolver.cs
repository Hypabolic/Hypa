using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Config;

namespace Hypa.Infrastructure.Storage;

internal static class HypaDataDirectoryResolver
{
    public static string Resolve(
        string preferredPath,
        bool isExplicit,
        IProjectRootDetector projectRootDetector,
        Func<string, bool> canWrite)
    {
        if (isExplicit || canWrite(preferredPath))
            return preferredPath;

        var projectRoot = projectRootDetector.Detect(Directory.GetCurrentDirectory());
        return projectRoot is null
            ? preferredPath
            : Path.Combine(projectRoot, ".hypa", "data");
    }

    public static bool CanWrite(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".hypa-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsDefaultPath(string path) =>
        string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(HypaConfig.Default.StoragePath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
