using System.Diagnostics;
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
    public void Passwd_compat_entries_fail_closed()
    {
        string[] passwd = [.. Passwd, "+@netusers::::::"];

        Assert.False(UnixPrivateGroup.IsPrivateTo(1000, 1000, ["alice:x:1000:"], passwd));
    }

    [Fact]
    public void Group_compat_entries_fail_closed()
    {
        Assert.False(UnixPrivateGroup.IsPrivateTo(1000, 1000, ["alice:x:1000:", "+"], Passwd));
    }

    [Theory]
    [InlineData("passwd: files\ngroup: files", false, true)]
    [InlineData("passwd:         files systemd sss\ngroup:          files systemd sss", false, true)]
    [InlineData("passwd: files systemd sss\ngroup: files systemd sss", true, false)]
    [InlineData("passwd: files [NOTFOUND=return] sss\ngroup: files", false, true)]
    [InlineData("passwd: files ldap\ngroup: files ldap", false, false)]
    [InlineData("passwd: compat\ngroup: compat", false, false)]
    [InlineData("passwd: files winbind\ngroup: files", false, false)]
    [InlineData("hosts: files dns\n# group: ldap", false, true)]
    public void Account_sources_must_be_local(string nsswitch, bool sssRunning, bool expected)
    {
        var lines = nsswitch.Split('\n');

        Assert.Equal(expected, UnixPrivateGroup.OnlyLocalAccountSources(1000, lines, sssRunning));
    }

    [Fact]
    public void Systemd_dynamic_gid_fails_closed()
    {
        string[] lines = ["passwd: files systemd", "group: files systemd"];

        Assert.True(UnixPrivateGroup.OnlyLocalAccountSources(1000, lines, sssRunning: false));
        Assert.False(UnixPrivateGroup.OnlyLocalAccountSources(61200, lines, sssRunning: false));
    }

    [Fact]
    public void Missing_nsswitch_means_files()
    {
        Assert.True(UnixPrivateGroup.OnlyLocalAccountSources(1000, null, sssRunning: false));
    }

    [Fact]
    public void Stat_reads_the_owner_and_group_of_a_new_directory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "hypa-gid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(MsQuicNativeIntegrity.TryGetUnixPathOwner(dir, out var uid, out var gid));
            Assert.Equal(Id("-u"), uid);
            Assert.Equal(Id("-g"), gid);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void Group_writable_install_directory_follows_the_private_group_rule()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "hypa-gw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var expected = UnixPrivateGroup.IsPrivateTo(Id("-g"), Id("-u"));

            var ok = MsQuicNativeIntegrity.TryValidateInstallDirectory(dir, out var detail);

            Assert.Equal(expected, ok);
            if (!ok)
                Assert.Contains("group", detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    private static uint Id(string flag)
    {
        using var process = Process.Start(new ProcessStartInfo("id", flag)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var text = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return uint.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Unreadable_files_fail_closed()
    {
        Assert.False(UnixPrivateGroup.IsPrivateTo(1000, 1000, null, Passwd));
        Assert.False(UnixPrivateGroup.IsPrivateTo(1000, 1000, ["alice:x:1000:"], null));
    }
}
