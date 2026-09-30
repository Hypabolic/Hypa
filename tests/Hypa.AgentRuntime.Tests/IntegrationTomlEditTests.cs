using Hypa.AgentRuntime.Application.Integrations;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class IntegrationTomlEditTests
{
    [Fact]
    public void Codex_install_enables_features_hooks_and_leaves_commented_tools_table()
    {
        const string input =
            """
            [features]
            hooks = false
            [tools] # user settings
            hooks = "keep"
            """;

        var updated = IntegrationTomlEdit.BuildCodexConfigWithHooks(input);
        Assert.Contains("[features]\nhooks = true\n", updated, StringComparison.Ordinal);
        Assert.Contains("[tools] # user settings\nhooks = \"keep\"", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks = false", updated, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(updated, "[features]"));
    }

    [Fact]
    public void Codex_install_recognizes_features_header_with_trailing_comment()
    {
        const string input =
            """
            [features] # enabled
            hooks = false
            """;

        var updated = IntegrationTomlEdit.BuildCodexConfigWithHooks(input);
        Assert.Contains("[features] # enabled\nhooks = true", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks = false", updated, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(updated, "[features]"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var start = 0;
        while (true)
        {
            var found = haystack.IndexOf(needle, start, StringComparison.Ordinal);
            if (found < 0)
                return count;
            count++;
            start = found + needle.Length;
        }
    }
}
