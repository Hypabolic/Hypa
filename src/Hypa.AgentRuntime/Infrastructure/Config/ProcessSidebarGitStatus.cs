using System.Diagnostics;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Infrastructure.Persistence;

namespace Hypa.AgentRuntime.Infrastructure.Config;

/// <summary>
/// Last-known git cache. Resolve never starts a process. RequestRefresh
/// admits cwd (allowlist + canonical + nofollow) then probes on a worker.
/// </summary>
public sealed class ProcessSidebarGitStatus : ISidebarGitStatus
{
    public const int MaxEntries = 64;

    private readonly string _gitExecutable;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _allowedRoots;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private int _paintReady;

    public ProcessSidebarGitStatus(
        string? gitExecutable = null,
        TimeSpan? timeout = null,
        TimeProvider? time = null,
        IReadOnlyList<string>? allowedRoots = null)
    {
        _gitExecutable = string.IsNullOrWhiteSpace(gitExecutable) ? "git" : gitExecutable;
        _timeout = timeout ?? TimeSpan.FromMilliseconds(250);
        _time = time ?? TimeProvider.System;
        _allowedRoots = NormalizeRoots(allowedRoots);
    }

    public SidebarGitInfo Resolve(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return new SidebarGitInfo();

        lock (_gate)
        {
            return _cache.TryGetValue(cwd, out var hit) ? hit.Info : new SidebarGitInfo();
        }
    }

    public void RequestRefresh(string cwd)
    {
        if (!TryAdmit(cwd, _allowedRoots, out _))
            return;

        lock (_gate)
        {
            if (_pending.Contains(cwd))
                return;
            if (_cache.TryGetValue(cwd, out var hit) && hit.Expires > _time.GetUtcNow())
                return;
            if (_pending.Count >= 8)
                return;
            _pending.Add(cwd);
        }

        _ = Task.Run(() => RefreshCore(cwd));
    }

    public bool TryConsumeRefresh() => Interlocked.Exchange(ref _paintReady, 0) != 0;

    private void RefreshCore(string cwd)
    {
        try
        {
            if (!TryAdmit(cwd, _allowedRoots, out var admitted))
            {
                lock (_gate)
                    _pending.Remove(cwd);
                return;
            }

            var info = Probe(admitted);
            lock (_gate)
            {
                EvictUnlocked();
                _cache[cwd] = new CacheEntry(info, _time.GetUtcNow().AddSeconds(5), _time.GetUtcNow());
                _pending.Remove(cwd);
            }

            Interlocked.Exchange(ref _paintReady, 1);
        }
        catch (Exception)
        {
            lock (_gate)
                _pending.Remove(cwd);
        }
    }

    private void EvictUnlocked()
    {
        if (_cache.Count < MaxEntries)
            return;
        var oldest = default(KeyValuePair<string, CacheEntry>);
        var found = false;
        foreach (var pair in _cache)
        {
            if (!found || pair.Value.Touched < oldest.Value.Touched)
            {
                oldest = pair;
                found = true;
            }
        }

        if (found)
            _cache.Remove(oldest.Key);
    }

    private SidebarGitInfo Probe(string cwd)
    {
        try
        {
            var branch = Run(cwd, ["rev-parse", "--abbrev-ref", "HEAD"]);
            if (branch is null)
                return new SidebarGitInfo();
            var porcelain = Run(cwd, ["status", "--porcelain"]);
            var dirty = porcelain is { Length: > 0 };
            return new SidebarGitInfo(branch.Trim(), dirty ? "*" : "");
        }
        catch (Exception)
        {
            return new SidebarGitInfo();
        }
    }

    private string? Run(string cwd, IReadOnlyList<string> args)
    {
        if (!TryAdmit(cwd, _allowedRoots, out var admitted))
            return null;

        var start = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = admitted,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start);
        if (process is null)
            return null;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)_timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            return null;
        }

        _ = stderrTask;
        if (process.ExitCode != 0)
            return null;
        return stdoutTask.GetAwaiter().GetResult();
    }

    internal static bool TryAdmit(string? cwd, IReadOnlyList<string> allowedRoots, out string admitted)
    {
        admitted = "";
        if (!SidebarGitCwdGuard.TryCanonicalize(cwd, out var canonical))
            return false;
        if (!SidebarGitCwdGuard.TryFindAllowedRoot(canonical, allowedRoots, out var allowedRoot))
            return false;
        if (!IsNofollowUnderRoot(canonical, allowedRoot))
            return false;

        admitted = canonical;
        return true;
    }

    internal static IReadOnlyList<string> DefaultAllowedRoots()
    {
        var list = new List<string>();
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        try
        {
            Add(Environment.CurrentDirectory);
        }
        catch (Exception)
        {
        }

        Add(Path.GetTempPath());
        return list;

        void Add(string? path)
        {
            if (SidebarGitCwdGuard.TryCanonicalize(path, out var canonical)
                && !list.Exists(existing => string.Equals(existing, canonical, StringComparison.Ordinal)))
            {
                list.Add(canonical);
            }
        }
    }

    private static IReadOnlyList<string> NormalizeRoots(IReadOnlyList<string>? roots)
    {
        if (roots is null)
            return DefaultAllowedRoots();

        var list = new List<string>(roots.Count);
        foreach (var root in roots)
        {
            if (SidebarGitCwdGuard.TryCanonicalize(root, out var canonical)
                && !list.Exists(existing => string.Equals(existing, canonical, StringComparison.Ordinal)))
            {
                list.Add(canonical);
            }
        }

        return list;
    }

    private static bool IsNofollowUnderRoot(string canonical, string allowedRoot)
    {
        if (string.Equals(canonical, allowedRoot, PathComparison()))
            return IsNofollowDirectory(canonical);

        var relative = canonical.Length > allowedRoot.Length
            ? canonical[allowedRoot.Length..].TrimStart(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            : "";
        if (relative.Length == 0)
            return IsNofollowDirectory(canonical);

        var current = allowedRoot;
        foreach (var part in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!IsNofollowDirectory(current))
                return false;
        }

        return true;
    }

    private static bool IsNofollowDirectory(string path)
    {
        if (!NoFollowWorkspaceWalker.TryLstatClassify(path, out var isSymlink, out var isDirectory, out _))
            return false;
        return isDirectory && !isSymlink;
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly record struct CacheEntry(SidebarGitInfo Info, DateTimeOffset Expires, DateTimeOffset Touched);
}
