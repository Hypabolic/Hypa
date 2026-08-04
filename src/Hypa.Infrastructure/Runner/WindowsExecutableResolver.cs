using System.Text;

namespace Hypa.Infrastructure.Runner;

/// <summary>
/// PATHEXT-aware Windows executable resolution for direct spawns
/// (<c>UseShellExecute=false</c>). Resolves only the OS-level FileName/args;
/// callers must keep the logical <c>CommandInvocation.Executable</c> unchanged
/// so compressors and filters still match bare names (npm, git, …).
/// </summary>
internal static class WindowsExecutableResolver
{
    internal static readonly string[] DefaultPathext = [".COM", ".EXE", ".BAT", ".CMD"];

    /// <summary>
    /// How Process.Start should be invoked after Windows resolution.
    /// On non-Windows this is always a passthrough of the original values.
    /// </summary>
    internal readonly record struct SpawnPlan(
        string FileName,
        IReadOnlyList<string> Arguments,
        bool WrappedInCmd,
        string? ResolvedPath);

    /// <summary>
    /// Build a spawn plan for the given executable/args. On non-Windows returns
    /// the inputs unchanged. On Windows, bare names are resolved via PATH+PATHEXT
    /// and .cmd/.bat targets are wrapped in <c>cmd.exe /d /s /c</c>.
    /// </summary>
    internal static SpawnPlan Resolve(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? envOverrides = null)
    {
        if (!OperatingSystem.IsWindows())
            return new SpawnPlan(executable, arguments, WrappedInCmd: false, ResolvedPath: null);

        if (string.IsNullOrWhiteSpace(executable))
            return new SpawnPlan(executable, arguments, WrappedInCmd: false, ResolvedPath: null);

        var pathEnv = GetEffectiveEnv("PATH", envOverrides);
        var pathextEnv = GetEffectiveEnv("PATHEXT", envOverrides);
        var extensions = ParsePathext(pathextEnv);

        var resolved = ResolvePath(executable, workingDirectory, pathEnv, extensions);
        if (resolved is null)
        {
            // Leave original FileName so Process.Start surfaces a clear error.
            return new SpawnPlan(executable, arguments, WrappedInCmd: false, ResolvedPath: null);
        }

        if (IsBatchFile(resolved))
        {
            var cmdLine = BuildCmdCArgument(resolved, arguments);
            return new SpawnPlan(
                // Absolute cmd path so a restricted EnvOverrides PATH still finds the shell.
                FileName: GetCmdExePath(),
                Arguments: ["/d", "/s", "/c", cmdLine],
                WrappedInCmd: true,
                ResolvedPath: resolved);
        }

        return new SpawnPlan(
            FileName: resolved,
            Arguments: arguments,
            WrappedInCmd: false,
            ResolvedPath: resolved);
    }

    /// <summary>
    /// Apply a <see cref="SpawnPlan"/> to <paramref name="psi"/> FileName/args.
    /// Wrapped cmd plans use the raw <see cref="ProcessStartInfo.Arguments"/> string so
    /// cmd-oriented quotes in the /c payload are not re-escaped by ArgumentList.
    /// </summary>
    internal static void ApplySpawnPlan(global::System.Diagnostics.ProcessStartInfo psi, SpawnPlan plan)
    {
        psi.FileName = plan.FileName;

        if (plan.WrappedInCmd)
        {
            // plan.Arguments is ["/d", "/s", "/c", cmdLine] — cmdLine is already
            // quoted for cmd.exe and must be passed literally after /c.
            if (plan.Arguments.Count >= 4)
            {
                psi.Arguments = "/d /s /c " + plan.Arguments[3];
                return;
            }

            // Fallback: join whatever we got without ArgumentList re-escaping.
            psi.Arguments = string.Join(' ', plan.Arguments);
            return;
        }

        foreach (var arg in plan.Arguments)
            psi.ArgumentList.Add(arg);
    }

    /// <summary>
    /// Pure PATH+PATHEXT search. Returns an absolute path when found; otherwise null.
    /// Testable on all platforms (does not check OperatingSystem).
    /// </summary>
    internal static string? ResolvePath(
        string executable,
        string? workingDirectory,
        string pathEnv,
        IReadOnlyList<string> pathextExtensions)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return null;

        if (HasDirectorySeparator(executable))
            return ResolvePathWithDirectory(executable, workingDirectory, pathextExtensions);

        return ResolveBareName(executable, workingDirectory, pathEnv, pathextExtensions);
    }

    internal static bool IsBatchFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build the single argument passed to <c>cmd.exe /d /s /c</c>: quoted
    /// executable path plus quoted args, using cmd-safe quoting.
    /// </summary>
    internal static string BuildCmdCArgument(string executablePath, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        sb.Append(QuoteCmdArgument(executablePath));
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            sb.Append(QuoteCmdArgument(arg));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quote a single argument for inclusion in a cmd.exe <c>/c</c> command string.
    /// Percent signs are doubled so cmd does not expand env vars before the target
    /// receives the arg (direct CreateProcess would pass them literally). Empty args
    /// and args with whitespace/meta characters are double-quoted; embedded quotes
    /// are doubled; trailing backslashes before the closer are doubled so the quote
    /// is not escaped.
    /// </summary>
    internal static string QuoteCmdArgument(string argument)
    {
        // cmd expands %VAR% while parsing the /c command line; double percents first.
        var escaped = argument.Replace("%", "%%", StringComparison.Ordinal);

        if (escaped.Length == 0)
            return "\"\"";

        var needsQuoting = false;
        foreach (var c in escaped)
        {
            if (char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or '!' or '(' or ')' or ';' or ',')
            {
                needsQuoting = true;
                break;
            }
        }

        if (!needsQuoting)
            return escaped;

        var sb = new StringBuilder(escaped.Length + 2);
        sb.Append('"');
        foreach (var c in escaped)
        {
            if (c == '"')
                sb.Append('"');
            sb.Append(c);
        }

        // Trailing backslashes would escape the closing quote; double them.
        var trailingSlashes = 0;
        for (var i = escaped.Length - 1; i >= 0 && escaped[i] == '\\'; i--)
            trailingSlashes++;
        if (trailingSlashes > 0)
            sb.Append('\\', trailingSlashes);

        sb.Append('"');
        return sb.ToString();
    }

    internal static IReadOnlyList<string> ParsePathext(string? pathextEnv)
    {
        if (string.IsNullOrWhiteSpace(pathextEnv))
            return DefaultPathext;

        var parts = pathextEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return DefaultPathext;

        var result = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            // PATHEXT entries are normally ".EXE"; tolerate missing leading dots.
            result[i] = p.StartsWith('.') ? p : "." + p;
        }

        return result;
    }

    private static string? ResolveBareName(
        string name,
        string? workingDirectory,
        string pathEnv,
        IReadOnlyList<string> extensions)
    {
        // Effective process cwd: explicit WorkingDirectory, else the host process cwd.
        // CreateProcess / cmd always search the current directory before PATH.
        var effectiveCwd = string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : workingDirectory;

        var hit = FindInDirectory(effectiveCwd, name, extensions);
        if (hit is not null)
            return hit;

        foreach (var dir in EnumeratePathDirectories(pathEnv, effectiveCwd))
        {
            // Already searched effectiveCwd first; skip duplicate empty-PATH hits.
            if (PathsEqual(dir, effectiveCwd))
                continue;

            hit = FindInDirectory(dir, name, extensions);
            if (hit is not null)
                return hit;
        }

        return null;
    }

    private static string? ResolvePathWithDirectory(
        string executable,
        string? workingDirectory,
        IReadOnlyList<string> extensions)
    {
        var candidate = ExpandRelative(executable, workingDirectory);

        // Path already has an extension: use as-is when present; do not invent PATHEXT.
        if (HasFileExtension(executable))
            return FindExistingFileWindows(candidate);

        // No extension: try PATHEXT against this path base, then bare file if present.
        foreach (var ext in extensions)
        {
            var hit = FindExistingFileWindows(candidate + ext);
            if (hit is not null)
                return hit;
        }

        // Skip extensionless non-PATHEXT matches (often npm bash shims / scripts).
        return null;
    }

    private static string? FindInDirectory(
        string directory,
        string name,
        IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        // Name already carries an extension (e.g. "tool.exe"): exact match only.
        if (HasFileExtension(name))
        {
            var exact = Path.Combine(directory, name);
            return FindExistingFileWindows(exact);
        }

        // PATHEXT order: prefer .COM, .EXE, … over any extensionless sibling.
        foreach (var ext in extensions)
        {
            var candidate = Path.Combine(directory, name + ext);
            var hit = FindExistingFileWindows(candidate);
            if (hit is not null)
                return hit;
        }

        // Intentionally skip bare extensionless files (not safe PE; npm ships bash shims).
        return null;
    }

    /// <summary>
    /// Windows path lookup is case-insensitive. Unit tests exercise this helper on
    /// Linux (case-sensitive FS) with PATHEXT entries like <c>.CMD</c> against files
    /// written as <c>.cmd</c> — match case-insensitively so CI mirrors Windows.
    /// </summary>
    private static string? FindExistingFileWindows(string candidate)
    {
        if (File.Exists(candidate))
            return Path.GetFullPath(candidate);

        // Native Windows File.Exists is already case-insensitive.
        if (OperatingSystem.IsWindows())
            return null;

        string? directory;
        string fileName;
        try
        {
            directory = Path.GetDirectoryName(candidate);
            fileName = Path.GetFileName(candidate);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            return null;

        if (!Directory.Exists(directory))
            return null;

        try
        {
            foreach (var entry in Directory.EnumerateFiles(directory))
            {
                if (string.Equals(Path.GetFileName(entry), fileName, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(entry);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Enumerate PATH directories. Empty segments (leading/trailing/double separators)
    /// mean the current directory on Windows and are mapped to <paramref name="currentDirectory"/>.
    /// </summary>
    private static IEnumerable<string> EnumeratePathDirectories(string pathEnv, string currentDirectory)
    {
        if (string.IsNullOrEmpty(pathEnv))
            yield break;

        // Keep empty entries — on Windows ";." / ";;" / trailing ";" imply cwd.
        foreach (var raw in pathEnv.Split(Path.PathSeparator))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                if (!string.IsNullOrEmpty(currentDirectory))
                    yield return currentDirectory;
                continue;
            }

            yield return part;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ExpandRelative(string path, string? workingDirectory)
    {
        if (Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(workingDirectory))
            return path;

        return Path.Combine(workingDirectory, path);
    }

    private static bool HasDirectorySeparator(string path) =>
        path.Contains(Path.DirectorySeparatorChar)
        || path.Contains(Path.AltDirectorySeparatorChar)
        // Windows drive-relative like "C:foo" is unusual; rooted "C:\foo" has separators.
        // Treat "C:\"-style roots via Path.IsPathRooted for absolute paths without further seps? rare.
        || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static bool HasFileExtension(string path)
    {
        // Path.GetExtension returns "" for extensionless and ".exe" for "a.exe".
        // Names like ".env" are edge cases; treat trailing-dot-something as having an extension.
        var ext = Path.GetExtension(path);
        return ext.Length > 0;
    }

    /// <summary>
    /// Absolute path to cmd.exe (ComSpec, then SystemDirectory). Avoids depending on PATH
    /// after EnvOverrides may have replaced it.
    /// </summary>
    internal static string GetCmdExePath()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(comSpec) && File.Exists(comSpec))
            return comSpec;

        try
        {
            var system = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            if (File.Exists(system))
                return system;
        }
        catch
        {
            // SystemDirectory can throw in restricted hosts; fall through.
        }

        return "cmd.exe";
    }

    private static string GetEffectiveEnv(
        string key,
        IReadOnlyDictionary<string, string>? envOverrides)
    {
        if (envOverrides is not null)
        {
            if (envOverrides.TryGetValue(key, out var direct))
                return direct ?? string.Empty;

            foreach (var (k, v) in envOverrides)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    return v ?? string.Empty;
            }
        }

        return Environment.GetEnvironmentVariable(key) ?? string.Empty;
    }
}
