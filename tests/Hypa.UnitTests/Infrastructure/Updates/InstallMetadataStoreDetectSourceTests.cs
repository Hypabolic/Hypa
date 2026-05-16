using Hypa.Infrastructure.Updates;
using Xunit;

namespace Hypa.UnitTests.Infrastructure.Updates;

public sealed class InstallMetadataStoreDetectSourceTests
{
    private const string Home = "/home/user";
    // Forward-slash paths keep tests cross-platform; DetectSource is pure string logic.
    private const string LocalAppData = "C:/Users/user/AppData/Local";

    // Null symlink resolver — simulates a stable dir that is a real directory, not a symlink.
    private static readonly Func<string, string?> NoSymlink = _ => null;

    // ── Unknown / empty ──────────────────────────────────────────────────────

    [Fact]
    public void DetectSource_EmptyPath_ReturnsUnknown() =>
        Assert.Equal("unknown", Detect(""));

    [Fact]
    public void DetectSource_UnrecognizedPath_ReturnsUnknown() =>
        Assert.Equal("unknown", Detect("/usr/local/bin/hypa"));

    // ── Homebrew ─────────────────────────────────────────────────────────────

    [Fact]
    public void DetectSource_CellarPath_ReturnsHomebrew() =>
        Assert.Equal("homebrew", Detect("/usr/local/Cellar/hypa/1.0.0/bin/hypa"));

    [Fact]
    public void DetectSource_HomebrewBinPath_ReturnsHomebrew() =>
        Assert.Equal("homebrew", Detect("/opt/homebrew/bin/hypa"));

    // ── Scoop ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DetectSource_ScoopPath_ReturnsScoop() =>
        Assert.Equal("scoop", Detect("C:/Users/user/scoop/apps/hypa/current/hypa.exe"));

    // ── Script install (Unix stable dir) ─────────────────────────────────────

    [Fact]
    public void DetectSource_InsideUnixStableDir_ReturnsScript() =>
        Assert.Equal("script", Detect($"{Home}/.local/share/hypa/hypa", isWindows: false));

    // ── Script install (Unix versioned dir via symlink resolution) ────────────

    [Fact]
    public void DetectSource_InsideResolvedVersionedDir_ReturnsScript()
    {
        var versionedDir = $"{Home}/.local/share/hypa-abc123";
        var stableDir = $"{Home}/.local/share/hypa";
        Func<string, string?> resolveSymlink = path =>
            path == stableDir ? versionedDir : null;

        var result = InstallMetadataStore.DetectSource(
            $"{versionedDir}/hypa",
            isWindows: false,
            home: Home,
            localAppData: LocalAppData,
            tryResolveSymlink: resolveSymlink);

        Assert.Equal("script", result);
    }

    [Fact]
    public void DetectSource_InsideUnrelatedHypaDashDir_ReturnsUnknown()
    {
        // "hypa-old" is NOT the symlink target — should not be treated as script.
        var stableDir = $"{Home}/.local/share/hypa";
        Func<string, string?> resolveSymlink = path =>
            path == stableDir ? $"{Home}/.local/share/hypa-abc123" : null;

        var result = InstallMetadataStore.DetectSource(
            $"{Home}/.local/share/hypa-old/hypa",
            isWindows: false,
            home: Home,
            localAppData: LocalAppData,
            tryResolveSymlink: resolveSymlink);

        Assert.Equal("unknown", result);
    }

    [Fact]
    public void DetectSource_SymlinkUnresolvable_StillMatchesStableDir()
    {
        // If symlink resolution fails, paths inside the stable dir still match.
        var result = InstallMetadataStore.DetectSource(
            $"{Home}/.local/share/hypa/hypa",
            isWindows: false,
            home: Home,
            localAppData: LocalAppData,
            tryResolveSymlink: _ => null);

        Assert.Equal("script", result);
    }

    // ── Script install (Windows) ──────────────────────────────────────────────

    [Fact]
    public void DetectSource_InsideWindowsScriptDir_ReturnsScript()
    {
        var result = InstallMetadataStore.DetectSource(
            $"{LocalAppData}/Hypa/bin/hypa.exe",
            isWindows: true,
            home: Home,
            localAppData: LocalAppData,
            tryResolveSymlink: NoSymlink);

        Assert.Equal("script", result);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Detect(string processPath, bool isWindows = false) =>
        InstallMetadataStore.DetectSource(
            processPath,
            isWindows: isWindows,
            home: Home,
            localAppData: LocalAppData,
            tryResolveSymlink: NoSymlink);
}
