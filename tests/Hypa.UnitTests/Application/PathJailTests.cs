using Hypa.Runtime.Application;
using Xunit;

namespace Hypa.UnitTests.Application;

public sealed class PathJailTests
{
    // Platform-correct paths — built with Path.Combine so they work on Windows and Unix alike.
    private static readonly string Root = Path.Combine(
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "/",
        "home", "user", ".hypa");

    private static readonly string ChildPath = Path.Combine(Root, "data", "file.db");
    private static readonly string ChildData = Path.Combine(Root, "data");
    private static readonly string RootWithSep = Root + Path.DirectorySeparatorChar;
    private static readonly string Outside = Path.Combine(
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "/",
        "home", "user", "other");
    private static readonly string EvilSibling = Root + "-evil";

    // ── Basic containment ────────────────────────────────────────────────────

    [Fact]
    public void IsWithinRoot_ExactMatch_ReturnsTrue()
    {
        Assert.True(PathJail.IsWithinRoot(Root, Root));
    }

    [Fact]
    public void IsWithinRoot_ChildPath_ReturnsTrue()
    {
        Assert.True(PathJail.IsWithinRoot(ChildPath, Root));
    }

    [Fact]
    public void IsWithinRoot_TrailingSeparatorOnRoot_StillMatches()
    {
        Assert.True(PathJail.IsWithinRoot(ChildData, RootWithSep));
    }

    [Fact]
    public void IsWithinRoot_PathOutsideRoot_ReturnsFalse()
    {
        Assert.False(PathJail.IsWithinRoot(Outside, Root));
    }

    [Fact]
    public void IsWithinRoot_PrefixWithoutSeparator_ReturnsFalse()
    {
        // "<root>-evil" must NOT be considered inside "<root>"
        Assert.False(PathJail.IsWithinRoot(EvilSibling, Root));
    }

    // ── OS-conditional case-sensitivity (Linux/macOS: case-SENSITIVE) ────────

    [Fact]
    public void IsWithinRoot_DifferentCase_ReturnsFalseOnLinuxMacOs()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows is case-insensitive — skip this assertion there

        // On Linux/macOS the file system is case-sensitive, so casing must match exactly.
        var upperChild = Path.Combine(Root.ToUpperInvariant(), "data");
        var upperRoot = Root.ToUpperInvariant();
        Assert.False(PathJail.IsWithinRoot(upperChild, Root));
        Assert.False(PathJail.IsWithinRoot(upperRoot, Root));
    }

    [Fact]
    public void IsWithinRoot_SameCaseChild_ReturnsTrueOnLinuxMacOs()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.True(PathJail.IsWithinRoot(Path.Combine(Root, "sub", "file"), Root));
    }

    [Fact]
    public void IsWithinRoot_DifferentCase_ReturnsTrueOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return; // Linux/macOS is case-sensitive — skip this assertion there

        // On Windows the check must be case-insensitive.
        var winRoot = @"C:\Users\Me\.hypa";
        var winChild = @"C:\Users\Me\.HYPA\data";
        Assert.True(PathJail.IsWithinRoot(winChild, winRoot));
    }
}
