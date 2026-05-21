using Hypa.Infrastructure.Hooks;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Hooks;

public sealed class BinaryRemoverTests : IDisposable
{
    private readonly string _tempDir;

    public BinaryRemoverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── Dry-run: nothing is deleted ──────────────────────────────────────

    [Fact]
    public async Task RemoveAsync_DryRun_ExistingSymlinkAndDir_ReturnsRemovedTrue()
    {
        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var installDir = Path.Combine(_tempDir, "share", "hypa");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "hypa"), "binary");
        File.CreateSymbolicLink(symlinkPath, Path.Combine(installDir, "hypa"));

        var remover = new BinaryRemover(symlinkPath, installDir, processPath: null);
        var result = await remover.RemoveAsync(dryRun: true);

        Assert.True(result.Removed);
        Assert.True(File.Exists(symlinkPath), "dry-run must not delete the symlink");
        Assert.True(Directory.Exists(installDir), "dry-run must not delete the install dir");
    }

    [Fact]
    public async Task RemoveAsync_DryRun_BrokenSymlink_ReturnsRemovedTrue()
    {
        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var installDir = Path.Combine(_tempDir, "share", "hypa");
        Directory.CreateDirectory(installDir);
        // Broken symlink — target doesn't exist
        File.CreateSymbolicLink(symlinkPath, "/nonexistent/target/hypa");

        var remover = new BinaryRemover(symlinkPath, installDir, processPath: null);
        var result = await remover.RemoveAsync(dryRun: true);

        Assert.True(result.Removed);
        // symlink must still exist (dry-run)
        var info = new FileInfo(symlinkPath);
        Assert.True(info.LinkTarget is not null, "dry-run must not delete the broken symlink");
    }

    [Fact]
    public async Task RemoveAsync_DryRun_NothingPresent_ReturnsFalse()
    {
        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var installDir = Path.Combine(_tempDir, "share", "hypa");

        var remover = new BinaryRemover(symlinkPath, installDir, processPath: null);
        var result = await remover.RemoveAsync(dryRun: true);

        Assert.False(result.Removed);
        Assert.NotNull(result.Detail);
        Assert.Contains("Not found", result.Detail);
    }

    // ── Process-path validation ──────────────────────────────────────────

    [Fact]
    public async Task RemoveAsync_WrongProcessPath_ReturnsErrorWithManualInstructions()
    {
        if (OperatingSystem.IsWindows()) return;  // process-path guard is Unix-only; Windows uses deferred cmd script

        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var installDir = Path.Combine(_tempDir, "share", "hypa");
        Directory.CreateDirectory(installDir);
        var wrongPath = "/usr/local/bin/hypa"; // doesn't match symlinkPath or installDir

        var remover = new BinaryRemover(symlinkPath, installDir, processPath: wrongPath);
        var result = await remover.RemoveAsync(dryRun: false);

        Assert.False(result.Removed);
        Assert.Contains("Remove manually", result.Detail);
    }

    [Fact]
    public async Task RemoveAsync_ProcessPathWithSamePrefixButDifferentDir_ReturnsError()
    {
        if (OperatingSystem.IsWindows()) return;  // process-path guard is Unix-only

        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var installDir = Path.Combine(_tempDir, "share", "hypa");
        Directory.CreateDirectory(installDir);
        // sibling dir shares the same string prefix — e.g. "hypa-old" starts with "hypa"
        var ambiguousPath = installDir + "-old" + Path.DirectorySeparatorChar + "hypa";

        var remover = new BinaryRemover(symlinkPath, installDir, processPath: ambiguousPath);
        var result = await remover.RemoveAsync(dryRun: false);

        Assert.False(result.Removed);
        Assert.Contains("Remove manually", result.Detail);
    }

    [Fact]
    public async Task RemoveAsync_ProcessPathMatchesSymlink_Removes()
    {
        if (OperatingSystem.IsWindows()) return;  // Unix removal is synchronous; Windows uses deferred cmd script

        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var installDir = Path.Combine(_tempDir, "share", "hypa");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "hypa"), "binary");
        File.CreateSymbolicLink(symlinkPath, Path.Combine(installDir, "hypa"));

        var remover = new BinaryRemover(symlinkPath, installDir, processPath: symlinkPath);
        var result = await remover.RemoveAsync(dryRun: false);

        Assert.True(result.Removed);
        Assert.False(File.Exists(symlinkPath));
        Assert.False(Directory.Exists(installDir));
    }

    [Fact]
    public async Task RemoveAsync_ProcessPathInsideInstallDir_Removes()
    {
        if (OperatingSystem.IsWindows()) return;  // Unix removal is synchronous; Windows uses deferred cmd script

        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var installDir = Path.Combine(_tempDir, "share", "hypa");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "hypa"), "binary");
        File.CreateSymbolicLink(symlinkPath, Path.Combine(installDir, "hypa"));

        // processPath is the real binary inside installDir
        var remover = new BinaryRemover(symlinkPath, installDir, processPath: Path.Combine(installDir, "hypa"));
        var result = await remover.RemoveAsync(dryRun: false);

        Assert.True(result.Removed);
        Assert.False(Directory.Exists(installDir));
    }

    // ── Actual removal ───────────────────────────────────────────────────

    [Fact]
    public async Task RemoveAsync_ActualRemoval_DeletesSymlinkAndInstallDir()
    {
        if (OperatingSystem.IsWindows()) return;  // Unix removal is synchronous; Windows uses deferred cmd script

        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var installDir = Path.Combine(_tempDir, "share", "hypa");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "hypa"), "binary");
        File.CreateSymbolicLink(symlinkPath, Path.Combine(installDir, "hypa"));

        var remover = new BinaryRemover(symlinkPath, installDir, processPath: null);
        var result = await remover.RemoveAsync(dryRun: false);

        Assert.True(result.Removed);
        Assert.False(File.Exists(symlinkPath));
        Assert.False(Directory.Exists(installDir));
    }

    [Fact]
    public async Task RemoveAsync_ActualRemoval_OnlyInstallDir_DeletesDir()
    {
        if (OperatingSystem.IsWindows()) return;  // Windows uses deferred cmd script; deletion not immediate

        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var installDir = Path.Combine(_tempDir, "share", "hypa");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "hypa"), "binary");
        // No symlink

        var remover = new BinaryRemover(symlinkPath, installDir, processPath: null);
        var result = await remover.RemoveAsync(dryRun: false);

        Assert.True(result.Removed);
        Assert.False(Directory.Exists(installDir));
    }

    // ── Versioned/symlink install layout ─────────────────────────────────

    [Fact]
    public async Task RemoveAsync_VersionedLayout_ProcessPathInVersionedDir_IsRecognizedAsValid()
    {
        if (!CanCreateDirectorySymlinks()) return;

        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var versionedDir = Path.Combine(_tempDir, "share", "hypa-abc123");
        var installDir = Path.Combine(_tempDir, "share", "hypa");  // will be a symlink → versionedDir
        Directory.CreateDirectory(versionedDir);
        File.WriteAllText(Path.Combine(versionedDir, "hypa"), "binary");
        Directory.CreateSymbolicLink(installDir, versionedDir);
        File.CreateSymbolicLink(symlinkPath, Path.Combine(versionedDir, "hypa"));

        // processPath is inside the versioned dir (what Environment.ProcessPath returns on Linux)
        var processPath = Path.Combine(versionedDir, "hypa");
        var remover = new BinaryRemover(symlinkPath, installDir, processPath: processPath);
        var result = await remover.RemoveAsync(dryRun: false);

        Assert.True(result.Removed);
    }

    [Fact]
    public async Task RemoveAsync_VersionedLayout_ActualRemoval_DeletesBothSymlinkAndVersionedDir()
    {
        if (OperatingSystem.IsWindows() || !CanCreateDirectorySymlinks()) return;  // deferred deletion on Windows

        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var versionedDir = Path.Combine(_tempDir, "share", "hypa-abc123");
        var installDir = Path.Combine(_tempDir, "share", "hypa");  // symlink → versionedDir
        Directory.CreateDirectory(versionedDir);
        File.WriteAllText(Path.Combine(versionedDir, "hypa"), "binary");
        Directory.CreateSymbolicLink(installDir, versionedDir);
        File.CreateSymbolicLink(symlinkPath, Path.Combine(versionedDir, "hypa"));

        var remover = new BinaryRemover(symlinkPath, installDir, processPath: null);
        var result = await remover.RemoveAsync(dryRun: false);

        Assert.True(result.Removed);
        Assert.False(Directory.Exists(installDir), "symlink should be gone");
        Assert.False(Directory.Exists(versionedDir), "versioned dir should be gone");
        Assert.False(File.Exists(symlinkPath), "bin symlink should be gone");
    }

    [Fact]
    public async Task RemoveAsync_VersionedLayout_DryRun_DeletesNothing()
    {
        if (!CanCreateDirectorySymlinks()) return;

        var symlinkPath = Path.Combine(_tempDir, "hypa");
        var versionedDir = Path.Combine(_tempDir, "share", "hypa-abc123");
        var installDir = Path.Combine(_tempDir, "share", "hypa");  // symlink → versionedDir
        Directory.CreateDirectory(versionedDir);
        File.WriteAllText(Path.Combine(versionedDir, "hypa"), "binary");
        Directory.CreateSymbolicLink(installDir, versionedDir);
        File.CreateSymbolicLink(symlinkPath, Path.Combine(versionedDir, "hypa"));

        var remover = new BinaryRemover(symlinkPath, installDir, processPath: null);
        var result = await remover.RemoveAsync(dryRun: true);

        Assert.True(result.Removed);
        Assert.True(Directory.Exists(installDir), "dry-run must not delete symlink");
        Assert.True(Directory.Exists(versionedDir), "dry-run must not delete versioned dir");
        Assert.True(File.Exists(symlinkPath), "dry-run must not delete bin symlink");
    }

    private static bool CanCreateDirectorySymlinks()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        // On Windows, directory symlinks need elevation or developer mode; probe first.
        var probe = Path.Combine(Path.GetTempPath(), $"hypa-symlink-probe-{Guid.NewGuid():N}");
        var target = Path.Combine(Path.GetTempPath(), $"hypa-symlink-target-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(probe, target);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(probe); } catch { }
            try { Directory.Delete(target); } catch { }
        }
    }
}
