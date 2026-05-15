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
}
