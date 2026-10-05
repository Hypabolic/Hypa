using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class UnixPrivateGroupTests
{
    private static readonly string[] Passwd =
    [
        "root:x:0:0:root:/root:/bin/bash",
        "alice:x:1000:1000:Alice:/home/alice:/bin/zsh",
        "bob:x:1001:1001:Bob:/home/bob:/bin/bash",
    ];

    [Fact]
    public void Owner_primary_group_without_members_is_private()
    {
        string[] group = ["root:x:0:", "alice:x:1000:", "bob:x:1001:", "sudo:x:27:alice,bob"];

        Assert.True(UnixPrivateGroup.IsPrivateTo(1000, 1000, group, Passwd));
    }

    [Fact]
    public void Group_listing_only_the_owner_is_private()
    {
        string[] group = ["alice:x:1000:alice"];

        Assert.True(UnixPrivateGroup.IsPrivateTo(1000, 1000, group, Passwd));
    }

    [Fact]
    public void Group_with_another_member_is_not_private()
    {
        string[] group = ["alice:x:1000:bob"];

        Assert.False(UnixPrivateGroup.IsPrivateTo(1000, 1000, group, Passwd));
    }

    [Fact]
    public void Group_that_is_another_account_primary_group_is_not_private()
    {
        string[] passwd = [.. Passwd, "carol:x:1002:1000:Carol:/home/carol:/bin/sh"];
        string[] group = ["alice:x:1000:"];

        Assert.False(UnixPrivateGroup.IsPrivateTo(1000, 1000, group, passwd));
    }

    [Fact]
    public void Shared_group_that_is_not_the_owner_primary_group_is_not_private()
    {
        string[] group = ["staff:x:50:"];

        Assert.False(UnixPrivateGroup.IsPrivateTo(50, 1000, group, Passwd));
    }

    [Fact]
    public void Group_missing_from_the_group_file_fails_closed()
    {
        Assert.False(UnixPrivateGroup.IsPrivateTo(1000, 1000, ["bob:x:1001:"], Passwd));
    }

    [Fact]
    public void Owner_missing_from_the_passwd_file_fails_closed()
    {
        Assert.False(UnixPrivateGroup.IsPrivateTo(2000, 2000, ["dave:x:2000:"], Passwd));
    }

    [Fact]
    public void Duplicate_group_entries_fail_closed()
    {
        string[] group = ["alice:x:1000:", "shadow-alice:x:1000:bob"];

        Assert.False(UnixPrivateGroup.IsPrivateTo(1000, 1000, group, Passwd));
    }

    [Fact]
    public void Unreadable_files_fail_closed()
    {
        Assert.False(UnixPrivateGroup.IsPrivateTo(1000, 1000, null, Passwd));
        Assert.False(UnixPrivateGroup.IsPrivateTo(1000, 1000, ["alice:x:1000:"], null));
    }
}
