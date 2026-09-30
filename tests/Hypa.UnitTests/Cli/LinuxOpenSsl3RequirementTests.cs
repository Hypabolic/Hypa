using Hypa.Cli;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class LinuxOpenSsl3RequirementTests
{
    [Fact]
    public void Help_and_version_do_not_need_openssl()
    {
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3([]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["--help"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["-h"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["-?"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["--version"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["version"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["doctor", "--help"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["help", "doctor"]));
    }

    [Fact]
    public void Crypto_commands_need_openssl()
    {
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["doctor"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["doctor", "code-intelligence"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["connectivity", "quic-capability"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["update"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["--remote", "user@host"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["--remote=user@host"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["--handoff"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["attach", "--remote", "user@host"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["attach", "--remote=user@host"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["attach", "--connect-placement", "plc_quic01"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["attach", "--handoff", "--remote", "user@host"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(
            ["client", "action", "placement.connect", "--placement", "plc_quic01"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(
            ["work", "placements", "add-quic", "--name", "peer"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(
            ["work", "placements", "add-ssh", "--name", "peer"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["work", "handoff", "plc_quic01"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["device", "pair"]));
        Assert.True(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["device", "approve", "--code", "x"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["device", "--help"]));
    }

    [Fact]
    public void Local_commands_do_not_need_openssl()
    {
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["ping"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["--session", "default", "ping"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["mux", "serve"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["mux", "serve", "--session", "default"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["attach"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["attach", "--once"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["client", "action", "tab.focus", "--tab", "1"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["compress"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["git", "status"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["docker", "ps"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["dotnet", "test"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["kubectl", "get", "pods"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["rewrite", "ls"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["work", "status"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["work", "placements", "list"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["device", "list"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["device", "revoke", "--device-id", "d1"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["-c", "git", "status"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["--session", "default"]));
        Assert.False(LinuxOpenSsl3Requirement.CommandNeedsOpenSsl3(["--remote", "user@host", "--help"]));
    }

    [Fact]
    public void OpenSsl_probe_requires_both_libraries()
    {
        Assert.True(LinuxOpenSsl3Requirement.LibrariesLoad(
            name => name is "libssl.so.3" or "libcrypto.so.3"));
        Assert.False(LinuxOpenSsl3Requirement.LibrariesLoad(name => name == "libssl.so.3"));
        Assert.False(LinuxOpenSsl3Requirement.LibrariesLoad(_ => false));
    }

    [Fact]
    public void Ldconfig_or_library_directories_satisfy_openssl3()
    {
        const string both =
            "libssl.so.3 (libc6,x86-64) => /usr/lib/libssl.so.3\n"
            + "libcrypto.so.3 (libc6,x86-64) => /usr/lib/libcrypto.so.3\n";
        Assert.True(LinuxOpenSsl3Requirement.IsSatisfied(both, _ => false));

        Assert.False(LinuxOpenSsl3Requirement.IsSatisfied(
            "libssl.so.3 (libc6,x86-64) => /usr/lib/libssl.so.3\n",
            _ => false));

        Assert.False(LinuxOpenSsl3Requirement.IsSatisfied(
            "libssl.so.30 (libc6) => /tmp/libssl.so.30\n",
            _ => false));

        Assert.True(LinuxOpenSsl3Requirement.IsSatisfied(
            null,
            path => path is "/usr/lib/x86_64-linux-gnu/libssl.so.3"
                or "/usr/lib/x86_64-linux-gnu/libcrypto.so.3"));

        Assert.True(LinuxOpenSsl3Requirement.IsSatisfied(
            "",
            path => path is "/usr/lib64/libssl.so.3" or "/usr/lib64/libcrypto.so.3"));

        Assert.False(LinuxOpenSsl3Requirement.IsSatisfied(null, _ => false));
    }

    [Fact]
    public void Missing_message_names_apt_dnf_and_zypper()
    {
        Assert.Contains("apt-get install -y libssl3", LinuxOpenSsl3Requirement.MissingMessage, StringComparison.Ordinal);
        Assert.Contains("dnf install -y openssl-libs", LinuxOpenSsl3Requirement.MissingMessage, StringComparison.Ordinal);
        Assert.Contains("zypper install -y libopenssl3", LinuxOpenSsl3Requirement.MissingMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_linux_host_does_not_reject()
    {
        if (OperatingSystem.IsLinux())
            return;

        using var error = new StringWriter();
        Assert.False(LinuxOpenSsl3Requirement.TryReject(["doctor"], error));
        Assert.Equal(string.Empty, error.ToString());
    }
}
