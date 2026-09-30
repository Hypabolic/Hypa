using System.Collections;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
/// Parent GIT_* repo overrides retarget HEAD off the pinned inode. Drop them
/// on every spawn path. Never pass <c>-c</c> or <c>--git-dir</c>.
/// </summary>
internal static class GitProbeEnvironment
{
    internal static readonly string[] RepoOverrideNames =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_INDEX_FILE",
        "GIT_COMMON_DIR",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
    ];

    internal static bool IsRepoOverride(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var blocked in RepoOverrideNames)
        {
            if (string.Equals(name, blocked, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static bool IsForcedGitKey(string? name) =>
        string.Equals(name, "GIT_TERMINAL_PROMPT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "GIT_OPTIONAL_LOCKS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Remove repo-override keys (any case) and force non-interactive git.
    /// </summary>
    internal static void ApplyTo(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var remove = new List<string>();
        foreach (var key in environment.Keys)
        {
            if (IsRepoOverride(key) || IsForcedGitKey(key))
                remove.Add(key);
        }

        foreach (var key in remove)
            environment.Remove(key);

        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GIT_OPTIONAL_LOCKS"] = "0";
    }

    /// <summary>
    /// Parent env for posix_spawn: copy all vars except repo overrides and
    /// the forced keys (re-added as 0).
    /// </summary>
    internal static List<string> BuildParentPairs(IDictionary? parent)
    {
        var pairs = new List<string>();
        if (parent is null)
            return pairs;

        foreach (DictionaryEntry entry in parent)
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrEmpty(key))
                continue;
            if (IsRepoOverride(key) || IsForcedGitKey(key))
                continue;
            pairs.Add(key + "=" + (entry.Value?.ToString() ?? ""));
        }

        pairs.Add("GIT_TERMINAL_PROMPT=0");
        pairs.Add("GIT_OPTIONAL_LOCKS=0");
        return pairs;
    }
}
