using Hypa.Runtime.Domain.Config;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Hypa.UnitTests.Domain;

public sealed class HypaConfigTests
{
    [Fact]
    public void Default_Enabled_True()
    {
        Assert.True(HypaConfig.Default.Enabled);
    }

    [Fact]
    public void Default_StoragePath_ContainsDotHypa()
    {
        Assert.Contains(".hypa", HypaConfig.Default.StoragePath);
    }

    [Fact]
    public void Default_ExcludeCommands_Empty()
    {
        Assert.Empty(HypaConfig.Default.ExcludeCommands);
    }

    [Fact]
    public void Default_LogLevel_Warning()
    {
        Assert.Equal(LogLevel.Warning, HypaConfig.Default.LogLevel);
    }

    [Fact]
    public void RecordEquality_SameValues_Equal()
    {
        var a = new HypaConfig { Enabled = true, StoragePath = "/tmp", ExcludeCommands = [], LogLevel = LogLevel.Warning };
        var b = new HypaConfig { Enabled = true, StoragePath = "/tmp", ExcludeCommands = [], LogLevel = LogLevel.Warning };
        Assert.Equal(a, b);
    }

    [Fact]
    public void WithExpression_ProducesModifiedCopy()
    {
        var original = HypaConfig.Default;
        var modified = original with { Enabled = false };
        Assert.False(modified.Enabled);
        Assert.True(original.Enabled);
    }
}
