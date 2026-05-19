namespace Hypa.IntegrationTests;

internal static class IntegrationTestHelpers
{
    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate Hypa repo root (Hypa.slnx not found).");
    }
}
