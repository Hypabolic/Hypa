using Hypa.Continuity.Domain;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class WorkIdTests
{
    [Theory]
    [InlineData("wrk_big")]
    [InlineData("wrk_secret")]
    [InlineData("wrk_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    public void Parse_accepts_safe_wire_ids(string value)
    {
        var id = WorkId.Parse(value);
        Assert.Equal(value, id.Value);
        Assert.True(WorkId.TryParse(value, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void New_is_a_safe_wire_id()
    {
        var id = WorkId.New();
        Assert.True(WorkId.TryParse(id.Value, out var parsed));
        Assert.Equal(id, parsed);
        Assert.StartsWith("wrk_", id.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wrk_")]
    [InlineData("not_a_work")]
    [InlineData("wrk_/evil")]
    [InlineData("../evil")]
    [InlineData("wrk_..")]
    [InlineData("wrk_foo.bar")]
    [InlineData("wrk_foo-bar")]
    [InlineData("wrk_foo/bar")]
    public void Parse_rejects_unsafe_or_invalid_ids(string? value)
    {
        Assert.False(WorkId.TryParse(value, out _));
        Assert.Throws<ArgumentException>(() => WorkId.Parse(value!));
    }

    [Fact]
    public void Stable_dest_workspace_is_work_id_under_work_root()
    {
        var workId = WorkId.Parse("wrk_01ARZ3NDEKTSV4RRFFQ69G5FAV");
        Assert.Equal("/work/wrk_01ARZ3NDEKTSV4RRFFQ69G5FAV", StableDestWorkspace.ForWork(workId));
        Assert.Equal("/work", StableDestWorkspace.Root);
    }
}
