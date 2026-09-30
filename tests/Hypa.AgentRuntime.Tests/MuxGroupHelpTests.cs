using Hypa.AgentRuntime.Tests.Support;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class MuxGroupHelpTests
{
    public static TheoryData<string, string, string, string> Groups() => new()
    {
        { "pane", "pane split", "--direction", "--no-focus" },
        { "agent", "agent start", "--kind", "--wait" },
        { "tab", "tab create", "--workspace", "--label" },
    };

    [Theory]
    [MemberData(nameof(Groups))]
    public async Task Group_without_subcommand_prints_help_and_does_not_connect(
        string group, string subcommand, string flag, string otherFlag)
    {
        var missing = Path.Combine(Path.GetTempPath(), "hypa-no-mux-" + Guid.NewGuid().ToString("N") + ".sock");
        var (code, stdout, stderr) = await HypaCliProcess.RunAsync("--socket", missing, group);

        Assert.Equal(0, code);
        Assert.StartsWith("hypa " + group, stdout, StringComparison.Ordinal);
        Assert.Contains(subcommand, stdout, StringComparison.Ordinal);
        Assert.Contains(flag, stdout, StringComparison.Ordinal);
        Assert.Contains(otherFlag, stdout, StringComparison.Ordinal);
        Assert.Contains("--help", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to connect", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("requires subcommand", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace create", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Groups))]
    public async Task Group_help_flag_prints_help_and_does_not_connect(
        string group, string subcommand, string flag, string otherFlag)
    {
        var missing = Path.Combine(Path.GetTempPath(), "hypa-no-mux-" + Guid.NewGuid().ToString("N") + ".sock");
        var (code, stdout, stderr) = await HypaCliProcess.RunAsync("--socket", missing, group, "--help");

        Assert.Equal(0, code);
        Assert.Contains(subcommand, stdout, StringComparison.Ordinal);
        Assert.Contains(flag, stdout, StringComparison.Ordinal);
        Assert.Contains(otherFlag, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Failed to connect", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pane_list_still_tries_to_connect()
    {
        var missing = Path.Combine(Path.GetTempPath(), "hypa-no-mux-" + Guid.NewGuid().ToString("N") + ".sock");
        var (code, _, stderr) = await HypaCliProcess.RunAsync("--socket", missing, "pane", "list");
        Assert.Equal(2, code);
        Assert.Contains("Failed to connect", stderr, StringComparison.Ordinal);
    }
}
