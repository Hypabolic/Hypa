using Hypa.Infrastructure.Doctor;
using Hypa.Runtime.Application.Ports;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Doctor;

public sealed class McpOAuthTokenFilePermissionsCheckTests : IDisposable
{
    private readonly string _dataDir;

    public McpOAuthTokenFilePermissionsCheckTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"hypa-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private McpOAuthTokenFilePermissionsCheck CreateSut() =>
        new(_dataDir);

    private string TokenFilePath => Path.Combine(_dataDir, "mcp-oauth-tokens.json");

    [Fact]
    public void Run_MissingFile_ReturnsOk()
    {
        var result = CreateSut().Run();

        Assert.Equal(DoctorStatus.Ok, result.Status);
    }

    [Fact]
    public void Category_IsMcp()
    {
        Assert.Equal("MCP", CreateSut().Category);
    }

    [Fact]
    public void Run_SecurePermissions_ReturnsOk()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        File.WriteAllText(TokenFilePath, "{}");
        File.SetUnixFileMode(TokenFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var result = CreateSut().Run();

        Assert.Equal(DoctorStatus.Ok, result.Status);
    }

    [Fact]
    public void Run_GroupReadable_ReturnsWarn()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        File.WriteAllText(TokenFilePath, "{}");
        File.SetUnixFileMode(TokenFilePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var result = CreateSut().Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
        Assert.Contains("chmod 600", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_WorldReadable_ReturnsWarn()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        File.WriteAllText(TokenFilePath, "{}");
        File.SetUnixFileMode(TokenFilePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var result = CreateSut().Run();

        Assert.Equal(DoctorStatus.Warn, result.Status);
    }
}
