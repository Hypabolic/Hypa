using System.Text;
using Hypa.Terminal.Pty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>The foreground process group read from a Linux /proc/pid/stat line.</summary>
public sealed class LinuxStatTpgidTests
{
    [Theory]
    [InlineData("4242 (zsh) S 4241 4242 4242 34816 4300 4194304 1029 0 0 0\n", 4300)]
    // A comm with spaces and a close parenthesis. The last ')' ends it.
    [InlineData("77 (my (odd) cmd) R 1 77 77 34817 81 4194560 10 0\n", 81)]
    // No foreground group: tpgid is -1.
    [InlineData("9 (kthreadd) S 2 0 0 0 -1 2129984 0 0\n", null)]
    [InlineData("9 (zsh) S 1 2 3\n", null)]
    [InlineData("no parenthesis here", null)]
    [InlineData("", null)]
    public void The_parser_reads_tpgid_after_the_command_name(string stat, int? expected) =>
        Assert.Equal(expected, UnixPaneProcessInfoProbe.TryParseLinuxStatTpgid(Encoding.ASCII.GetBytes(stat)));

    [SkippableFact]
    public void The_probe_reads_this_process_and_gives_null_for_a_missing_one()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux has /proc/pid/stat.");
        // A test process can have no controlling terminal, so null is valid here.
        var own = UnixPaneProcessInfoProbe.TryReadLinuxForegroundGroup(Environment.ProcessId);
        Assert.True(own is null or > 0, $"tpgid {own}");
        Assert.Null(UnixPaneProcessInfoProbe.TryReadLinuxForegroundGroup(int.MaxValue));
    }
}
