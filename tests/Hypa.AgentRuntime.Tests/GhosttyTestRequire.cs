using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Shared Ghostty native-asset gate for tests.
/// Default: skip when the library is absent (local F1 / Windows).
/// When <c>HYPA_REQUIRE_GHOSTTY_TESTS=1</c>, missing asset is a hard failure
/// (CI aot-publish-runtime-ghostty after build-libghostty-vt + build-hypa-pty-host).
/// </summary>
internal static class GhosttyTestRequire
{
    public const string RequireEnv = "HYPA_REQUIRE_GHOSTTY_TESTS";

    public static bool IsHardRequired =>
        string.Equals(
            Environment.GetEnvironmentVariable(RequireEnv),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Skip (or hard-fail under <see cref="RequireEnv"/>) when Ghostty cannot run.
    /// </summary>
    public static void RequireNativeLibrary(string? libPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (IsHardRequired)
            {
                throw new InvalidOperationException(
                    "HYPA_REQUIRE_GHOSTTY_TESTS=1 but libghostty-vt is Unix-only on Windows.");
            }

            Skip.If(true, "libghostty-vt is Unix-only.");
            return;
        }

        if (libPath is not null && File.Exists(libPath))
            return;

        var message =
            "libghostty-vt not present; build with scripts/build-libghostty-vt.sh "
            + "or set HYPA_GHOSTTY_VT.";

        if (IsHardRequired)
            throw new InvalidOperationException(
                $"{RequireEnv}=1 hard-gate failed: {message}");

        Skip.If(true, message);
    }

    /// <summary>
    /// Gate on a resolved hypa-pty-host path (null/missing → skip or hard-fail).
    /// Default: skip when helper is absent (local F1 without native build).
    /// When <see cref="RequireEnv"/> is set, missing helper is a hard failure
    /// (same fail-closed pattern as <see cref="RequireNativeLibrary"/>).
    /// Returns the full path when present.
    /// </summary>
    public static string RequirePtyHostHelper(string? helperPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (IsHardRequired)
            {
                throw new InvalidOperationException(
                    $"{RequireEnv}=1 but hypa-pty-host is Unix-only on Windows.");
            }

            Skip.If(true, "hypa-pty-host is Unix-only.");
            return "";
        }

        if (!string.IsNullOrWhiteSpace(helperPath) && File.Exists(helperPath))
            return Path.GetFullPath(helperPath);

        var message =
            "hypa-pty-host not present; build with scripts/build-hypa-pty-host.sh "
            + "or set HYPA_PTY_HOST.";

        if (IsHardRequired)
            throw new InvalidOperationException(
                $"{RequireEnv}=1 hard-gate failed: {message}");

        Skip.If(true, message);
        return "";
    }

    /// <summary>
    /// Resolve libghostty-vt without evaluating <see cref="GhosttyLibraryLoader.LibraryFileName"/>
    /// on unsupported OS. Returns null on Windows and when the file is absent.
    /// </summary>
    public static string? TryResolveNativeLibraryPath()
    {
        var env = Environment.GetEnvironmentVariable("HYPA_GHOSTTY_VT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return Path.GetFullPath(env);

        if (!GhosttyLibraryLoader.TryGetLibraryFileName(out var fileName))
            return null;

        var rid = GhosttyLibraryLoader.GetPortableRid();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Hypa.slnx")))
            {
                var ridPath = Path.Combine(dir.FullName, "native", "runtimes", rid, "native", fileName);
                if (File.Exists(ridPath))
                    return Path.GetFullPath(ridPath);
                break;
            }

            dir = dir.Parent;
        }

        return GhosttyLibraryLoader.FindExistingLibraryPath();
    }
}
