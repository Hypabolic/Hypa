using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class AttachmentRegistryTests
{
    [Fact]
    public void Create_and_drop_by_subscription_frees_quota()
    {
        var reg = new InMemoryAttachmentRegistry(maxAttachments: 2);

        var a = reg.Create("p1", "c1", "sub_1", AttachmentModes.Observe, null);
        Assert.True(a.Ok);
        var b = reg.Create("p1", "c1", "sub_2", AttachmentModes.Observe, null);
        Assert.True(b.Ok);
        Assert.True(reg.Create("p1", "c1", "sub_3", AttachmentModes.Observe, null).LimitExceeded);

        var dropped = reg.DropBySubscription("sub_1");
        Assert.Single(dropped);
        Assert.Equal(1, reg.Count);

        var c = reg.Create("p1", "c1", "sub_3", AttachmentModes.Observe, null);
        Assert.True(c.Ok);
        Assert.Equal(2, reg.Count);
    }

    [Fact]
    public void Drop_single_attachment_rolls_back_slot()
    {
        var reg = new InMemoryAttachmentRegistry(maxAttachments: 1);
        var created = reg.Create("p1", "c1", "sub_1", AttachmentModes.Observe, null);
        Assert.True(created.Ok);
        Assert.NotNull(created.Attachment);

        var dropped = reg.Drop(created.Attachment!.AttachmentId);
        Assert.NotNull(dropped);
        Assert.Equal(0, reg.Count);

        var again = reg.Create("p1", "c1", "sub_1", AttachmentModes.Observe, null);
        Assert.True(again.Ok);
    }

    [Fact]
    public void GetOrCreate_reuses_same_slot()
    {
        var reg = new InMemoryAttachmentRegistry(maxAttachments: 1);
        var first = reg.GetOrCreate("p1", "c1", "sub_1", AttachmentModes.Observe, null);
        Assert.True(first.Ok);
        Assert.False(first.Reused);

        var second = reg.GetOrCreate("p1", "c1", "sub_1", AttachmentModes.Observe, null);
        Assert.True(second.Ok);
        Assert.True(second.Reused);
        Assert.Equal(first.Attachment!.AttachmentId, second.Attachment!.AttachmentId);
        Assert.Equal(1, reg.Count);
    }

    [Fact]
    public void Drop_connection_clears_subscription_index()
    {
        var reg = new InMemoryAttachmentRegistry();
        reg.Create("p1", "c1", "sub_1", AttachmentModes.Observe, null);
        reg.Create("p2", "c1", "sub_1", AttachmentModes.Observe, null);
        Assert.Equal(2, reg.Count);

        var dropped = reg.DropConnection("c1");
        Assert.Equal(2, dropped.Count);
        Assert.Equal(0, reg.Count);
        Assert.Empty(reg.DropBySubscription("sub_1"));
    }

    [Fact]
    public void Control_requires_lease_id()
    {
        var reg = new InMemoryAttachmentRegistry();
        var bad = reg.Create("p1", "c1", "sub_1", AttachmentModes.Control, leaseId: null);
        Assert.True(bad.Invalid);

        var ok = reg.Create("p1", "c1", "sub_1", AttachmentModes.Control, "lease_1");
        Assert.True(ok.Ok);
    }

    [Fact]
    public void DropByPane_frees_quota_without_unsubscribe()
    {
        var reg = new InMemoryAttachmentRegistry(maxAttachments: 2);
        Assert.True(reg.Create("p1", "c1", "sub_1", AttachmentModes.Observe, null).Ok);
        Assert.True(reg.Create("p2", "c1", "sub_1", AttachmentModes.Observe, null).Ok);
        Assert.True(reg.Create("p3", "c1", "sub_1", AttachmentModes.Observe, null).LimitExceeded);

        var dropped = reg.DropByPane("p1");
        Assert.Single(dropped);
        Assert.Equal("p1", dropped[0].PaneId);
        Assert.Equal(1, reg.Count);

        // Slot free for a new pane on the same subscription.
        var again = reg.Create("p3", "c1", "sub_1", AttachmentModes.Observe, null);
        Assert.True(again.Ok);
        Assert.Equal(2, reg.Count);

        // Dropping the other pane does not affect p3.
        _ = reg.DropByPane("p2");
        Assert.Equal(1, reg.Count);
        Assert.NotNull(reg.Get(again.Attachment!.AttachmentId));
    }
}
