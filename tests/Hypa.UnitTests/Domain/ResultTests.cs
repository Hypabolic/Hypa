using Hypa.Runtime.Domain.Common;
using Xunit;

namespace Hypa.UnitTests.Domain;

public sealed class ResultTests
{
    [Fact]
    public void Ok_IsOk_True_And_Value_Accessible()
    {
        var result = Result<int, string>.Ok(42);
        Assert.True(result.IsOk);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Ok_Error_Throws()
    {
        var result = Result<int, string>.Ok(42);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Fail_IsOk_False_And_Error_Accessible()
    {
        var result = Result<int, string>.Fail("oops");
        Assert.False(result.IsOk);
        Assert.Equal("oops", result.Error);
    }

    [Fact]
    public void Fail_Value_Throws()
    {
        var result = Result<int, string>.Fail("oops");
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Map_Ok_AppliesFunction()
    {
        var result = Result<int, string>.Ok(5).Map(x => x * 2);
        Assert.True(result.IsOk);
        Assert.Equal(10, result.Value);
    }

    [Fact]
    public void Map_Fail_PreservesError()
    {
        var result = Result<int, string>.Fail("err").Map(x => x * 2);
        Assert.False(result.IsOk);
        Assert.Equal("err", result.Error);
    }

    [Fact]
    public void MapError_Fail_AppliesFunction()
    {
        var result = Result<int, string>.Fail("err").MapError(e => e.Length);
        Assert.False(result.IsOk);
        Assert.Equal(3, result.Error);
    }

    [Fact]
    public void MapError_Ok_PreservesValue()
    {
        var result = Result<int, string>.Ok(5).MapError(e => e.Length);
        Assert.True(result.IsOk);
        Assert.Equal(5, result.Value);
    }

    [Fact]
    public void Deconstruct_Ok_CorrectValues()
    {
        var result = Result<int, string>.Ok(7);
        var (isOk, value, error) = result;
        Assert.True(isOk);
        Assert.Equal(7, value);
        Assert.Null(error);
    }

    [Fact]
    public void Deconstruct_Fail_CorrectValues()
    {
        var result = Result<int, string>.Fail("bad");
        var (isOk, value, error) = result;
        Assert.False(isOk);
        Assert.Equal(0, value);
        Assert.Equal("bad", error);
    }
}
