using System.Runtime.InteropServices;
using Hypa.Infrastructure.Updates;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Updates;

public sealed class RuntimeIdentifierProviderTests
{
    [Theory]
    [InlineData(true,  false, Architecture.X64,   "win-x64")]
    [InlineData(true,  false, Architecture.Arm64, "win-arm64")]
    [InlineData(false, true,  Architecture.X64,   "osx-x64")]
    [InlineData(false, true,  Architecture.Arm64, "osx-arm64")]
    [InlineData(false, false, Architecture.X64,   "linux-x64")]
    [InlineData(false, false, Architecture.Arm64, "linux-arm64")]
    public void Detect_AllSixRids(bool isWindows, bool isOsx, Architecture arch, string expected) =>
        Assert.Equal(expected, RuntimeIdentifierProvider.Detect(isWindows, isOsx, arch));

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Wasm)]
    [InlineData(Architecture.S390x)]
    public void Detect_UnknownArch_FallsBackToX64(Architecture arch) =>
        Assert.Equal("linux-x64", RuntimeIdentifierProvider.Detect(false, false, arch));

    [Fact]
    public void RuntimeIdentifier_MatchesCurrentPlatform()
    {
        var expected = RuntimeIdentifierProvider.Detect(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            RuntimeInformation.ProcessArchitecture);
        Assert.Equal(expected, new RuntimeIdentifierProvider().RuntimeIdentifier);
    }
}
