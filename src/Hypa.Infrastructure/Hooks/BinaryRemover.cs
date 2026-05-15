using System.Diagnostics;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Hooks;

public sealed class BinaryRemover : IBinaryRemover
{
    private readonly string _symlinkPath;
    private readonly string _installDir;
    private readonly string? _processPath;

    public BinaryRemover()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _installDir = Path.Combine(localAppData, "Hypa");
            _symlinkPath = string.Empty;
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _symlinkPath = Path.Combine(home, ".local", "bin", "hypa");
            _installDir = Path.Combine(home, ".local", "share", "hypa");
        }
        _processPath = Environment.ProcessPath;
    }

    internal BinaryRemover(string symlinkPath, string installDir, string? processPath = null)
    {
        _symlinkPath = symlinkPath;
        _installDir = installDir;
        _processPath = processPath;
    }

    public async Task<BinaryRemoveResult> RemoveAsync(bool dryRun, CancellationToken ct = default)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? await RemoveWindowsAsync(dryRun, ct)
                : RemoveUnix(dryRun);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BinaryRemoveResult(false,
                $"Could not remove binary ({ex.Message}). Remove manually:\n  rm {_symlinkPath}\n  rm -rf {_installDir}");
        }
    }

    private BinaryRemoveResult RemoveUnix(bool dryRun)
    {
        // Validate: the running binary should be the one we're removing
        if (_processPath is not null
            && !IsInsideInstallDir(_processPath)
            && !string.Equals(_processPath, _symlinkPath, StringComparison.Ordinal))
        {
            return new BinaryRemoveResult(false,
                $"Running binary ({_processPath}) is not the installed copy. Remove manually:\n  rm {_symlinkPath}\n  rm -rf {_installDir}");
        }

        var removed = new List<string>();
        var missing = new List<string>();

        // Use symlink-aware check: FileInfo.LinkTarget detects broken symlinks that File.Exists misses
        var symlinkInfo = new FileInfo(_symlinkPath);
        if (symlinkInfo.Exists || symlinkInfo.LinkTarget is not null)
        {
            if (!dryRun) File.Delete(_symlinkPath);
            removed.Add(_symlinkPath);
        }
        else
        {
            missing.Add(_symlinkPath);
        }

        if (Directory.Exists(_installDir))
        {
            if (!dryRun) Directory.Delete(_installDir, recursive: true);
            removed.Add(_installDir);
        }
        else
        {
            missing.Add(_installDir);
        }

        if (removed.Count == 0)
            return new BinaryRemoveResult(false, $"Not found at expected locations: {string.Join(", ", missing)}");

        return new BinaryRemoveResult(true, string.Join(", ", removed) + (dryRun ? " would be removed" : " removed"));
    }

    private bool IsInsideInstallDir(string path)
    {
        var boundary = _installDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        return path.StartsWith(boundary, StringComparison.Ordinal);
    }

    private async Task<BinaryRemoveResult> RemoveWindowsAsync(bool dryRun, CancellationToken ct)
    {
        if (!Directory.Exists(_installDir))
            return new BinaryRemoveResult(false, $"Not found at expected location: {_installDir}");

        if (dryRun)
            return new BinaryRemoveResult(true, $"{_installDir} would be scheduled for removal");

        var scriptPath = Path.Combine(Path.GetTempPath(), "hypa-cleanup.cmd");
        var script = $"""
            @echo off
            timeout /t 2 /nobreak >nul
            rmdir /s /q "{_installDir}"
            del "%~f0"
            """;

        await File.WriteAllTextAsync(scriptPath, script, ct);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        return new BinaryRemoveResult(true, "Binary removal scheduled — exit the terminal to complete.");
    }
}
