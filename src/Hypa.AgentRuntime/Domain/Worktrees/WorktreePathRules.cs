using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Domain.Worktrees;

/// <summary>
// / Pure worktree path and porcelain rules.
/// <c>src/worktree.rs</c> without I/O.
/// </summary>
public static class WorktreePathRules
{
    public const string DefaultWorktreePrefix = "worktree";
    public const string RedactedCheckout = "[checkout]";

    private static readonly string[] Adjectives =
        ["brave", "calm", "clear", "green", "lucky", "quiet", "rapid", "silver"];

    private static readonly string[] Nouns =
        ["river", "cloud", "field", "forest", "harbor", "meadow", "stone", "valley"];

    public static string GeneratedBranchSlug(ulong seed)
    {
        var adjective = Adjectives[(int)(seed % (ulong)Adjectives.Length)];
        var noun = Nouns[(int)((seed / (ulong)Adjectives.Length) % (ulong)Nouns.Length)];
        var suffix = seed & 0xffff;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{DefaultWorktreePrefix}/{adjective}-{noun}-{suffix:x4}");
    }

    public static string BranchToPathSlug(string branch)
    {
        var slug = new StringBuilder();
        var lastWasDash = false;
        foreach (var ch in branch)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                slug.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                slug.Append('-');
                lastWasDash = true;
            }
        }

        var trimmed = slug.ToString().Trim('-');
        return trimmed.Length == 0 ? DefaultWorktreePrefix : trimmed;
    }

    public static string DefaultCheckoutPath(string root, string repoName, string branch) =>
        Path.Combine(root, repoName, BranchToPathSlug(branch));

    public static string ExpandTilde(string path, string? home)
    {
        if (path == "~")
            return string.IsNullOrWhiteSpace(home) ? path : home;
        if (path.StartsWith("~/", StringComparison.Ordinal)
            || (OperatingSystem.IsWindows() && path.StartsWith("~\\", StringComparison.Ordinal)))
        {
            if (string.IsNullOrWhiteSpace(home))
                return path;
            return Path.Combine(home, path[2..]);
        }

        return path;
    }

    public static string CanonicalOrOriginal(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full) || File.Exists(full))
                return ResolveExisting(full);
            return full;
        }
        catch (Exception)
        {
            return path;
        }
    }

    public static bool TryCanonical(string path, out string canonical)
    {
        try
        {
            canonical = CanonicalOrOriginal(path);
            return true;
        }
        catch (Exception)
        {
            canonical = path;
            return false;
        }
    }

    /// <summary>
    /// macOS <c>/var</c> and <c>/private/var</c> share one identity.
    /// </summary>
    private static string ResolveExisting(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
        if (resolved is not null)
            path = resolved.FullName;
        if (!OperatingSystem.IsWindows())
            path = ResolveSymlinkComponents(path);
        return Path.GetFullPath(path);
    }

    private static string ResolveSymlinkComponents(string path)
    {
        var root = Path.GetPathRoot(path) ?? "/";
        var relative = path.Length > root.Length ? path[root.Length..] : string.Empty;
        var acc = root.Length == 0 || root == "/" ? "/" : root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0)
                continue;
            acc = acc == "/" ? "/" + part : Path.Combine(acc, part);
            if (!Directory.Exists(acc) && !File.Exists(acc))
                continue;
            var target = Directory.Exists(acc)
                ? new DirectoryInfo(acc).LinkTarget
                : new FileInfo(acc).LinkTarget;
            if (string.IsNullOrEmpty(target))
                continue;
            acc = Path.IsPathRooted(target)
                ? Path.GetFullPath(target)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(acc) ?? "/", target));
        }

        return acc;
    }

    /// <summary>Reject relative paths, <c>..</c>, and leading dash.</summary>
    public static string? ValidateAbsoluteCheckoutPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "worktree path is required";
        if (path.StartsWith('-'))
            return "worktree path is invalid";
        if (!Path.IsPathRooted(path))
            return "worktree path must be absolute";
        var full = Path.GetFullPath(path);
        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains("..", StringComparer.Ordinal))
            return "worktree path is invalid";
        return null;
    }

    /// <summary>Reject empty, leading dash, and shell metacharacters.</summary>
    public static string? ValidateBranchName(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            return "branch is required";
        if (branch.StartsWith('-'))
            return "branch is invalid";
        foreach (var ch in branch)
        {
            if (ch is ';' or '|' or '$' or '`' or '\n' or '\r' or '\0')
                return "branch is invalid";
        }

        return null;
    }

    public static bool IsDirtyWorktreeRemoveError(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.Contains("contains modified or untracked files", StringComparison.Ordinal)
            && lower.Contains("use --force to delete it", StringComparison.Ordinal);
    }

    public static bool IsNotWorkingTreeRemoveError(string message)
    {
        var lower = message.ToLowerInvariant();
        return lower.Contains("is not a working tree", StringComparison.Ordinal)
            || lower.Contains("is not a worktree", StringComparison.Ordinal);
    }

    public static IReadOnlyList<string> RepositoryGitArgs(string repoRoot, bool trustRepository)
    {
        var args = new List<string>();
        if (trustRepository)
        {
            args.Add("-c");
            args.Add("safe.directory=" + repoRoot);
        }

        args.Add("-C");
        args.Add(repoRoot);
        return args;
    }

    public static WorktreeCommand BuildRemoveCommand(
        string repoRoot, string path, bool force, bool trustRepository)
    {
        var args = new List<string>(RepositoryGitArgs(repoRoot, trustRepository))
        {
            "worktree",
            "remove",
        };
        if (force)
            args.Add("--force");
        args.Add(path);
        return new WorktreeCommand { Program = "git", Args = args };
    }

    public static WorktreeCommand BuildAddNewBranchCommand(
        string repoRoot, string path, string branch, string @base, bool trustRepository)
    {
        var args = new List<string>(RepositoryGitArgs(repoRoot, trustRepository))
        {
            "worktree",
            "add",
            "-b",
            branch,
            path,
            @base,
        };
        return new WorktreeCommand { Program = "git", Args = args };
    }

    public static WorktreeCommand BuildAddExistingBranchCommand(
        string repoRoot, string path, string branch, bool trustRepository)
    {
        var args = new List<string>(RepositoryGitArgs(repoRoot, trustRepository))
        {
            "worktree",
            "add",
            path,
            branch,
        };
        return new WorktreeCommand { Program = "git", Args = args };
    }

    public static IReadOnlyList<ExistingWorktree> ParsePorcelain(string output)
    {
        var entries = new List<ExistingWorktree>();
        string? path = null;
        string? branch = null;
        var isBare = false;
        var isDetached = false;
        var isPrunable = false;

        void Finish()
        {
            if (path is null)
                return;
            entries.Add(new ExistingWorktree
            {
                Path = path,
                Branch = branch,
                IsBare = isBare,
                IsDetached = isDetached,
                IsPrunable = isPrunable,
            });
            path = null;
            branch = null;
            isBare = false;
            isDetached = false;
            isPrunable = false;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                Finish();
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
                path = line["worktree ".Length..];
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var value = line["branch ".Length..];
                branch = value.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? value["refs/heads/".Length..]
                    : value;
            }
            else if (line == "detached")
                isDetached = true;
            else if (line == "bare")
                isBare = true;
            else if (line.StartsWith("prunable", StringComparison.Ordinal))
                isPrunable = true;
        }

        Finish();
        return entries;
    }

    public static bool PathsEqual(string left, string right)
    {
        var a = CanonicalOrOriginal(left);
        var b = CanonicalOrOriginal(right);
        return string.Equals(a, b, StringComparison.Ordinal)
            || string.Equals(
                a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static bool IsUnderDirectory(string child, string parent)
    {
        var childFull = CanonicalOrOriginal(child);
        var parentFull = CanonicalOrOriginal(parent);
        if (!parentFull.EndsWith(Path.DirectorySeparatorChar)
            && !parentFull.EndsWith(Path.AltDirectorySeparatorChar))
            parentFull += Path.DirectorySeparatorChar;
        return childFull.StartsWith(
            parentFull,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Redact an absolute checkout for errors and journal payloads.</summary>
    public static string RedactCheckout(string? path, string? home = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RedactedCheckout;
        if (!string.IsNullOrWhiteSpace(home)
            && path.StartsWith(home, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            return "~" + path[home.Length..].Replace('\\', '/');
        }

        return RedactedCheckout;
    }

    /// <summary>
    /// Stable path-free grouping id. Digest of the canonical git common dir.
    /// Chrome and errors use this value. Operational paths stay off <c>key</c>.
    /// </summary>
    public static string OpaqueRepoKey(string commonDir)
    {
        var identity = CanonicalOrOriginal(commonDir).Replace('\\', '/').TrimEnd('/');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool LooksLikeFilesystemPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.Contains(".git", StringComparison.OrdinalIgnoreCase))
            return true;
        return value.Contains('/', StringComparison.Ordinal)
            || value.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(value);
    }

    /// <summary>
    /// Never delete when registration state is unknown.
    /// </summary>
    public static Result<WorktreeLeftoverDecision, WorktreeError> DecideLeftoverRecovery(
        Result<IReadOnlyList<ExistingWorktree>, WorktreeError> listed,
        string path,
        string repoRoot,
        bool directoryExists,
        bool leftoverMatchesRepo)
    {
        if (!listed.IsOk)
        {
            return Result<WorktreeLeftoverDecision, WorktreeError>.Fail(
                new WorktreeError(
                    listed.Error.Code,
                    RedactMessage(listed.Error.Message, [path, repoRoot])));
        }

        if (listed.Value.Any(e => PathsEqual(e.Path, path)))
        {
            return Result<WorktreeLeftoverDecision, WorktreeError>.Fail(
                new WorktreeError(
                    WorktreeErrorCodes.RemoveFailed,
                    "worktree is still registered"));
        }

        if (!directoryExists)
            return Result<WorktreeLeftoverDecision, WorktreeError>.Ok(WorktreeLeftoverDecision.Nothing);

        if (!leftoverMatchesRepo)
        {
            return Result<WorktreeLeftoverDecision, WorktreeError>.Fail(
                new WorktreeError(
                    WorktreeErrorCodes.RemoveFailed,
                    "refused to delete an unrelated directory"));
        }

        return Result<WorktreeLeftoverDecision, WorktreeError>.Ok(WorktreeLeftoverDecision.Delete);
    }

    public static string RedactMessage(string message, IEnumerable<string> checkoutPaths, string? home = null)
    {
        var redacted = message;
        foreach (var path in checkoutPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (redacted.Contains(path, StringComparison.Ordinal))
                redacted = redacted.Replace(path, RedactCheckout(path, home), StringComparison.Ordinal);
            var full = CanonicalOrOriginal(path);
            if (!string.Equals(full, path, StringComparison.Ordinal)
                && redacted.Contains(full, StringComparison.Ordinal))
                redacted = redacted.Replace(full, RedactCheckout(full, home), StringComparison.Ordinal);
        }

        return redacted;
    }
}
