using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.ProjectRoot;

public sealed class GitProjectRootDetector : IProjectRootDetector
{
    public string? Detect(string startPath)
    {
        var current = new DirectoryInfo(startPath);
        while (current is not null)
        {
            if (HasMarker(current))
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static bool HasMarker(DirectoryInfo dir)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return true;
        if (Directory.Exists(Path.Combine(dir.FullName, ".hypa"))) return true;
        if (dir.GetFiles("*.sln").Length > 0) return true;
        if (dir.GetFiles("*.slnx").Length > 0) return true;
        if (dir.GetFiles("*.csproj").Length > 0) return true;
        return false;
    }
}
