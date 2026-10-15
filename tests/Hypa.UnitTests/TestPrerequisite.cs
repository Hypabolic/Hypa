using Xunit;

namespace Hypa.UnitTests;

/// <summary>
/// A test that needs a tool outside the repo. A missing tool skips the test on a
/// developer host. In CI the same gap must fail the run, or the test would pass
/// without it ever running.
/// </summary>
internal static class TestPrerequisite
{
    /// <summary>Set to 1 where bash, zsh, and the hypa binary must exist.</summary>
    public const string RequireToolsEnv = "HYPA_REQUIRE_UNIT_TOOLS";

    /// <summary>Set to 1 where libghostty-vt must exist (same name as the other test projects).</summary>
    public const string RequireGhosttyEnv = "HYPA_REQUIRE_GHOSTTY_TESTS";

    public static void RequireTool(bool present, string message) => Require(present, message, RequireToolsEnv);

    public static void RequireGhostty(bool present, string message) => Require(present, message, RequireGhosttyEnv);

    private static void Require(bool present, string message, string requireEnv)
    {
        if (present)
            return;
        if (string.Equals(Environment.GetEnvironmentVariable(requireEnv), "1", StringComparison.Ordinal))
            throw new InvalidOperationException($"{requireEnv}=1 hard-gate failed: {message}");
        Skip.If(true, message);
    }
}
