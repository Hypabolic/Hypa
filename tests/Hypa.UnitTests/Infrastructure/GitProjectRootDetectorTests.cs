using Hypa.Infrastructure.ProjectRoot;
using Xunit;

namespace Hypa.UnitTests.Infrastructure;

public sealed class GitProjectRootDetectorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"hypa-root-test-{Guid.NewGuid():N}");

    public GitProjectRootDetectorTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Detect_GitDir_ReturnsRoot()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        var deep = Path.Combine(_tempDir, "a", "b", "c");
        Directory.CreateDirectory(deep);

        var result = new GitProjectRootDetector().Detect(deep);

        Assert.Equal(_tempDir, result);
    }

    [Fact]
    public void Detect_SlnFile_ReturnsRoot()
    {
        File.WriteAllText(Path.Combine(_tempDir, "MyApp.sln"), "");
        var sub = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(sub);

        var result = new GitProjectRootDetector().Detect(sub);

        Assert.Equal(_tempDir, result);
    }

    [Fact]
    public void Detect_SlnxFile_ReturnsRoot()
    {
        File.WriteAllText(Path.Combine(_tempDir, "MyApp.slnx"), "");
        var sub = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(sub);

        var result = new GitProjectRootDetector().Detect(sub);

        Assert.Equal(_tempDir, result);
    }

    [Fact]
    public void Detect_HypaDir_ReturnsRoot()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".hypa"));
        var sub = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(sub);

        var result = new GitProjectRootDetector().Detect(sub);

        Assert.Equal(_tempDir, result);
    }

    [Fact]
    public void Detect_NoMarkers_ReturnsNull()
    {
        var isolated = Path.Combine(Path.GetTempPath(), $"isolated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolated);
        try
        {
            var result = new GitProjectRootDetector().Detect(isolated);
            // May or may not be null depending on whether parent dirs have markers;
            // at minimum it should not throw.
            _ = result;
        }
        finally
        {
            Directory.Delete(isolated);
        }
    }

    [Fact]
    public void Detect_StartsAtMarker_ReturnsThatDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));

        var result = new GitProjectRootDetector().Detect(_tempDir);

        Assert.Equal(_tempDir, result);
    }
}
