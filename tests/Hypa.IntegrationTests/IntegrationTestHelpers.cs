namespace Hypa.IntegrationTests;

internal static class IntegrationTestHelpers
{
    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate Hypa repo root (Hypa.slnx not found).");
    }

    /// <summary>
    /// Testhost apphost output. published tests must not use this path.
    /// A published pack is a native <c>hypa</c> without <c>.dll</c> / <c>.deps.json</c>.
    /// </summary>
    internal static bool IsTesthostApphost(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        return File.Exists(Path.Combine(dir, stem + ".dll"))
            || File.Exists(Path.Combine(dir, stem + ".deps.json"));
    }

    /// <summary>
    /// Unix TTY proof. Fails closed when <c>HYPA_H45_BIN</c> is missing
    /// or is testhost apphost output.
    /// </summary>
    internal static (string FileName, string[] Prefix) RequirePublishedHypa()
    {
        var env = Environment.GetEnvironmentVariable("HYPA_H45_BIN");
        if (string.IsNullOrWhiteSpace(env) || !File.Exists(env))
        {
            throw new InvalidOperationException(
                "HYPA_H45_BIN is required and must be a published hypa pack "
                + "(SQLite + hypa-pty-host), not testhost apphost.");
        }

        RequirePublishedPack(env);
        if (env.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return ("dotnet", ["exec", env]);
        return (env, []);
    }

    private static void RequirePublishedPack(string path)
    {
        if (IsTesthostApphost(path))
        {
            throw new InvalidOperationException(
                "HYPA_H45_BIN is testhost apphost output, not a published pack: " + path);
        }

        AssertShipName(path);
        var dir = Path.GetDirectoryName(path) ?? "";
        var hasSqlite = File.Exists(Path.Combine(dir, "libe_sqlite3.so"))
            || File.Exists(Path.Combine(dir, "libe_sqlite3.dylib"))
            || File.Exists(Path.Combine(dir, "e_sqlite3.dll"));
        var hasHelper = File.Exists(Path.Combine(dir, "hypa-pty-host"))
            || File.Exists(Path.Combine(dir, "hypa-pty-host.exe"));
        var hasAttach = File.Exists(Path.Combine(dir, "hypa-attach"))
            || File.Exists(Path.Combine(dir, "hypa-attach.exe"));
        var hasAnnotate = File.Exists(Path.Combine(dir, "hypa-annotate"))
            || File.Exists(Path.Combine(dir, "hypa-annotate.exe"));
        var hasRuntime = File.Exists(Path.Combine(dir, "hypa-runtime"))
            || File.Exists(Path.Combine(dir, "hypa-runtime.exe"));
        if (!hasSqlite)
        {
            throw new InvalidOperationException(
                "H-45 published pack requires SQLite beside hypa: " + dir);
        }

        if (!OperatingSystem.IsWindows() && !hasHelper)
        {
            throw new InvalidOperationException(
                "H-45 published pack requires hypa-pty-host beside hypa: " + dir);
        }

        if (!hasAttach)
        {
            throw new InvalidOperationException(
                "published pack requires hypa-attach beside hypa: " + dir);
        }

        if (!hasAnnotate)
        {
            throw new InvalidOperationException(
                "published pack requires hypa-annotate beside hypa: " + dir);
        }

        if (!hasRuntime)
        {
            throw new InvalidOperationException(
                "published pack requires hypa-runtime beside hypa: " + dir);
        }
    }

    /// <summary>
    /// Ship <c>hypa</c> for attach tests. Prefer <c>HYPA_H45_BIN</c>, then
    /// <c>HYPA_BINARY_PATH</c>, then the Release apphost. Refuse
    /// <c>hypa-runtime</c> and two-binary packs. published goldens must
    /// use <see cref="RequirePublishedHypa"/> instead.
    /// </summary>
    internal static (string FileName, string[] Prefix) FindShipHypa()
    {
        foreach (var key in new[] { "HYPA_H45_BIN", "HYPA_BINARY_PATH" })
        {
            var env = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(env))
                continue;
            if (!File.Exists(env))
                throw new InvalidOperationException(key + " does not exist: " + env);
            AssertShipName(env);
            if (env.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return ("dotnet", ["exec", env]);
            return (env, []);
        }

        var repo = FindRepoRoot();
        var names = OperatingSystem.IsWindows()
            ? new[] { "hypa.exe", "hypa.dll" }
            : new[] { "hypa", "hypa.dll" };
        foreach (var config in new[] { "Release", "Debug" })
        {
            var dir = Path.Combine(repo, "src", "Hypa.Cli", "bin", config, "net10.0");
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path))
                    continue;
                AssertShipName(path);
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    return ("dotnet", ["exec", path]);
                return (path, []);
            }
        }

        throw new InvalidOperationException(
            "ship hypa binary not found. Build src/Hypa.Cli -c Release or set HYPA_BINARY_PATH.");
    }

    /// <summary>
    /// The built <c>hypa.dll</c>. CI builds Release; a local <c>dotnet test</c>
    /// builds Debug. Prefer Release, then Debug.
    /// </summary>
    internal static string FindCliDll()
    {
        var repo = FindRepoRoot();
        foreach (var config in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(repo, "src", "Hypa.Cli", "bin", config, "net10.0", "hypa.dll");
            if (File.Exists(path))
                return path;
        }

        return Path.Combine(repo, "src", "Hypa.Cli", "bin", "Release", "net10.0", "hypa.dll");
    }

    internal static void AssertShipName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.Equals(name, "hypa-runtime", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "hypa-runtime-cli", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "H-45 published artifact is ship hypa, not " + name);
        }

        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var isProjectOutput = File.Exists(Path.Combine(dir, stem + ".dll"))
            || File.Exists(Path.Combine(dir, stem + ".deps.json"));
        if (isProjectOutput)
            return;

        var hasSqlite = File.Exists(Path.Combine(dir, "libe_sqlite3.so"))
            || File.Exists(Path.Combine(dir, "libe_sqlite3.dylib"))
            || File.Exists(Path.Combine(dir, "e_sqlite3.dll"));
        var hasHelper = File.Exists(Path.Combine(dir, "hypa-pty-host"))
            || File.Exists(Path.Combine(dir, "hypa-pty-host.exe"));
        if (File.Exists(Path.Combine(dir, "hypa-runtime")) && (!hasSqlite || !hasHelper))
        {
            throw new InvalidOperationException(
                "H-45 refuses a two-binary tarball without SQLite + hypa-pty-host");
        }
    }
}
