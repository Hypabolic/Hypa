namespace Hypa.Continuity.Application;

/// <summary>
/// Live dest-start resume env reconstructed from a mux pane. File copy is not this path.
/// </summary>
public static class LiveResumeStartArgs
{
    public const string AttemptIdEnv = "HYPA_RESUME_ATTEMPT_ID";
    public const string ReportPathEnv = "HYPA_RESUME_REPORT_PATH";

    public static string DefaultReportFile(string home, string attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        return Path.GetFullPath(Path.Combine(
            Path.GetFullPath(home.Trim()),
            ".hypa",
            "continuity",
            "resume-reports",
            attemptId.Trim() + ".json"));
    }

    /// <summary>
    /// Rebuild the default report path under HOME. A custom path in pane
    /// tokens is not live start args, even when that path is under HOME.
    /// </summary>
    public static HarnessStartArgs? FromLiveHome(
        string home,
        IReadOnlyDictionary<string, string>? tokens)
    {
        if (string.IsNullOrWhiteSpace(home) || tokens is null)
            return null;
        if (!tokens.TryGetValue(AttemptIdEnv, out var attempt) || !IsAttemptId(attempt))
            return null;

        var homeFull = Path.GetFullPath(home.Trim());
        var reportPath = DefaultReportFile(homeFull, attempt);
        if (tokens.TryGetValue(ReportPathEnv, out var rawPath)
            && !string.IsNullOrWhiteSpace(rawPath))
        {
            if (!Path.IsPathRooted(rawPath.Trim()))
                return null;
            var customPath = Path.GetFullPath(rawPath.Trim());
            if (!string.Equals(customPath, reportPath, StringComparison.Ordinal))
                return null;
        }

        if (!IsUnderHome(reportPath, homeFull))
            return null;

        return new HarnessStartArgs
        {
            Argv = [],
            Env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = homeFull,
                ["HYPA_CUBE_HOME"] = homeFull,
                [AttemptIdEnv] = attempt.Trim(),
                [ReportPathEnv] = reportPath,
            },
        };
    }

    public static bool IsAttemptId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                continue;
            return false;
        }

        return true;
    }

    public static bool IsUnderHome(string path, string home)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(home.Trim());
        if (root.Length == 0)
            return false;
        if (!root.EndsWith(Path.DirectorySeparatorChar)
            && !root.EndsWith(Path.AltDirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        return full.StartsWith(root, StringComparison.Ordinal);
    }
}
