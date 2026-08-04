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
    /// Quote a single argument for inclusion in a cmd.exe command string.
    /// Empty args and args with whitespace/meta characters are double-quoted;
    /// embedded quotes are doubled.
    /// </summary>
    internal static string QuoteCmdArgument(string argument)
    {
        if (argument.Length == 0)
            return "\"\"";

        var needsQuoting = false;
        foreach (var c in argument)
        {
            if (char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or '%' or '!' or '(' or ')' or ';')
            {
                needsQuoting = true;
                break;
            }
        }

        if (!needsQuoting)
            return argument;

        var sb = new StringBuilder(argument.Length + 2);
        sb.Append('"');
        foreach (var c in argument)
        {
            if (c == '"')
                sb.Append('"');
            sb.Append(c);
        }

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
        // Search working directory first (cmd / CreateProcess convention), then PATH.
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var hit = FindInDirectory(workingDirectory, name, extensions);
            if (hit is not null)
                return hit;
        }

        foreach (var dir in EnumeratePathDirectories(pathEnv))
        {
            var hit = FindInDirectory(dir, name, extensions);
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
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;

        // No extension: try PATHEXT against this path base, then bare file if present.
        foreach (var ext in extensions)
        {
            var withExt = candidate + ext;
            if (File.Exists(withExt))
                return Path.GetFullPath(withExt);
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
            return File.Exists(exact) ? Path.GetFullPath(exact) : null;
        }

        // PATHEXT order: prefer .COM, .EXE, … over any extensionless sibling.
        foreach (var ext in extensions)
        {
            var candidate = Path.Combine(directory, name + ext);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        // Intentionally skip bare extensionless files (not safe PE; npm ships bash shims).
        return null;
    }

    private static IEnumerable<string> EnumeratePathDirectories(string pathEnv)
    {
        if (string.IsNullOrEmpty(pathEnv))
            yield break;

        foreach (var part in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length > 0)
                yield return part;
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
