namespace Hypa.Continuity.Harnesses;

/// <summary>Lock, pid, socket, WAL/SHM, and credential names excluded from store copy.</summary>
internal static class HarnessStoreFileRules
{
    public static bool IsExcluded(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return true;

        if (fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase))
            return true;

        return IsLockResidue(fileName);
    }

    public static bool IsLockResidue(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        return fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pid", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".sock", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);
    }

    public static int CopyLockResidue(
        string sourceDir,
        string destDir,
        ICollection<string> written)
    {
        if (!Directory.Exists(sourceDir))
            return 0;

        Directory.CreateDirectory(destDir);
        var copied = 0;
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (!IsLockResidue(name))
                continue;

            var dest = Path.Combine(destDir, name);
            File.Copy(file, dest, overwrite: true);
            written.Add(dest);
            copied++;
        }

        return copied;
    }

    public static int StripWrittenLocks(IEnumerable<string> writtenPaths)
    {
        var stripped = 0;
        foreach (var path in writtenPaths)
        {
            if (!File.Exists(path))
                continue;
            if (!IsExcluded(Path.GetFileName(path)))
                continue;

            File.Delete(path);
            stripped++;
        }

        return stripped;
    }

    public static void StripLockResidueIn(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsLockResidue(Path.GetFileName(file)))
                File.Delete(file);
        }
    }
}
