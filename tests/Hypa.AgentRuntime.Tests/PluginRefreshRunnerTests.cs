using Hypa.AgentRuntime.Application.Plugins;
using Hypa.AgentRuntime.Infrastructure.Plugins;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class PluginRefreshRunnerTests
{
    [Fact]
    public void Refresh_caps_output_and_reports_timeout()
    {
        var runner = new ProcessPluginRefreshRunner();
        var capped = runner.Run(
            "/bin/echo",
            ["abcdefghij"],
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            outputCapBytes: 4,
            timeout: TimeSpan.FromSeconds(2));
        Assert.Contains("[hypa truncated plugin output after 4 bytes]", capped.Stdout, StringComparison.Ordinal);
        Assert.StartsWith("abcd", capped.Stdout, StringComparison.Ordinal);

        var timedOut = runner.Run(
            "/bin/sleep",
            ["8"],
            Directory.GetCurrentDirectory(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            outputCapBytes: 64,
            timeout: TimeSpan.FromMilliseconds(200));
        Assert.Equal("refresh timed out", timedOut.Error);
    }
}
