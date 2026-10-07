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
        // Normal repos use a `.git` directory. Worktrees and submodules use a `.git`
        // *file* (`gitdir: ...` / `gitdir: ../.git/modules/...`). Either form marks a root.
        var gitPath = Path.Combine(dir.FullName, ".git");
        if (Directory.Exists(gitPath) || File.Exists(gitPath)) return true;
        if (Directory.Exists(Path.Combine(dir.FullName, ".hypa"))) return true;
        return HasFile(dir, "*.sln") || HasFile(dir, "*.slnx") || HasFile(dir, "*.csproj");
    }

    // A sandbox can let a process pass through a parent directory (such as `/`)
    // without letting it list that directory. A directory that cannot be listed has
    // no marker; it must not stop the walk or crash the command.
    private static bool HasFile(DirectoryInfo dir, string pattern)
    {
        try
        {
            return dir.EnumerateFiles(pattern).Any();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
