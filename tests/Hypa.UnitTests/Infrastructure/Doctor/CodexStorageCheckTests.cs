using Hypa.Infrastructure.Doctor;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Ports;
using NSubstitute;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Doctor;

public sealed class CodexStorageCheckTests
{
    private const string FakeDataDir = "/fake/.hypa";
    private const string FakeConfigPath = "/fake/codex/config.toml";
    private readonly HypaDataOptions _options = new() { DataDirectory = FakeDataDir };
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();

    public CodexStorageCheckTests()
    {
        _fileSystem.FileExists(Arg.Any<string>()).Returns(false);
    }

    [Fact]
    public void Category_IsCodexStorage()
    {
        Assert.Equal("Codex Storage", MakeCheck(_ => true).Category);
    }

    [Fact]
    public void Run_WritableDirectory_NoConfig_ReturnsOk()
    {
        var result = MakeCheck(_ => true).Run();
        Assert.Equal(DoctorStatus.Ok, result.Status);
    }

    [Fact]
    public void Run_ReadOnlyDirectory_ReturnsWarn()
    {
        var result = MakeCheck(_ => false).Run();
        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void Run_ConfigMissingWritableRoot_ReturnsWarnWithSnippet()
    {
        _fileSystem.FileExists(FakeConfigPath).Returns(true);
        _fileSystem.ReadAllText(FakeConfigPath).Returns("[features]\nhooks = true\n");

        var result = MakeCheck(_ => true).Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains("sandbox_workspace_write", result.Detail, StringComparison.Ordinal);
        Assert.Contains(FakeDataDir, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ConfigContainsWritableRoot_ReturnsOk()
    {
        _fileSystem.FileExists(FakeConfigPath).Returns(true);
        _fileSystem.ReadAllText(FakeConfigPath)
            .Returns($"[sandbox_workspace_write]\nwritable_roots = [\"{FakeDataDir}\"]\n");

        var result = MakeCheck(_ => true).Run();

        Assert.Equal(DoctorStatus.Ok, result.Status);
    }

    [Fact]
    public void Run_ConfigContainsWritableRootButNotWritable_ReturnsWarn()
    {
        _fileSystem.FileExists(FakeConfigPath).Returns(true);
        _fileSystem.ReadAllText(FakeConfigPath)
            .Returns($"[sandbox_workspace_write]\nwritable_roots = [\"{FakeDataDir}\"]\n");

        var result = MakeCheck(_ => false).Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
    }

    [Fact]
    public void Run_ReportsDataDirectory()
    {
        var result = MakeCheck(_ => true).Run();
        Assert.Contains(FakeDataDir, result.Value, StringComparison.Ordinal);
    }

    private CodexStorageCheck MakeCheck(Func<string, bool> probe) =>
        new(_options, _fileSystem, probe, FakeConfigPath);
}
