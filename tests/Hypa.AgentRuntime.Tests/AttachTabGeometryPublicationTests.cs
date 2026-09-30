using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachTabGeometryPublicationTests
{
    [Fact]
    public void Last_interact_owns_shared_tab_and_passive_claim_does_not_steal()
    {
        var store = new AttachTabGeometryPublication();
        Assert.True(store.ClaimUnowned("conn_a", "tab_1"));
        Assert.False(store.ClaimUnowned("conn_b", "tab_1"));
        Assert.True(store.IsController("conn_a", "tab_1"));
        Assert.False(store.IsController("conn_b", "tab_1"));

        Assert.True(store.Claim("conn_b", "tab_1"));
        Assert.True(store.IsController("conn_b", "tab_1"));
        Assert.False(store.Claim("conn_b", "tab_1"));

        Assert.Equal(["tab_1"], store.Release("conn_b"));
        Assert.True(store.ClaimUnowned("conn_a", "tab_1"));
        Assert.Equal("conn_a", store.Controller("tab_1"));
    }

    [Fact]
    public void Different_tabs_keep_independent_controllers()
    {
        var store = new AttachTabGeometryPublication();
        Assert.True(store.ClaimUnowned("conn_a", "tab_1"));
        Assert.True(store.ClaimUnowned("conn_b", "tab_2"));
        Assert.Equal("conn_a", store.Controller("tab_1"));
        Assert.Equal("conn_b", store.Controller("tab_2"));
        Assert.Equal(["tab_1"], store.Release("conn_a"));
        Assert.Equal("conn_b", store.Controller("tab_2"));
    }
}
