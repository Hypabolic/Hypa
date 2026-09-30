using Hypa.AgentRuntime.Application;
using Hypa.Annotate.Application;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Copy;
using Hypa.Cli.Attach.Mouse;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class AttachSelectionHandoffTests : IDisposable
{
    private readonly string _root;

    public AttachSelectionHandoffTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-attach-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void WriteCurrentSelection_prefers_copy_mode_over_mouse()
    {
        var copy = new CopyModeSession();
        copy.Seed(BuildSnapshot("copy mode text"), recentText: null, viewportRows: 1);
        copy.StartSelection();
        _ = copy.MoveRight();
        _ = copy.MoveRight();
        _ = copy.MoveRight();
        _ = copy.MoveRight();

        var mouse = new MouseSelection();
        mouse.SeedText("mouse text", cols: 40);
        mouse.Begin(0, 0);
        mouse.Extend(0, 4);

        var handoff = Path.Combine(_root, "selection");
        var result = SelectionHandoffWriter.WriteCurrentSelection(copy, mouse, handoff);

        Assert.True(result.IsOk);
        var written = File.ReadAllText(handoff);
        Assert.StartsWith("copy", written, StringComparison.Ordinal);
        Assert.DoesNotContain("mouse", written, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteCurrentSelection_uses_mouse_when_copy_is_empty()
    {
        var copy = new CopyModeSession();
        var mouse = new MouseSelection();
        mouse.SeedText("mouse only", cols: 40);
        mouse.Begin(0, 0);
        mouse.Extend(0, 9);

        var handoff = Path.Combine(_root, "selection");
        var result = SelectionHandoffWriter.WriteCurrentSelection(copy, mouse, handoff);

        Assert.True(result.IsOk);
        Assert.Equal("mouse only", File.ReadAllText(handoff));
    }

    [Fact]
    public void WriteCurrentSelection_writes_no_file_for_empty_selection()
    {
        var copy = new CopyModeSession();
        var mouse = new MouseSelection();
        var handoff = Path.Combine(_root, "selection");

        var result = SelectionHandoffWriter.WriteCurrentSelection(copy, mouse, handoff);

        Assert.True(result.IsOk);
        Assert.False(File.Exists(handoff));
    }

    [Fact]
    public void Host_default_path_matches_plugin_read_path()
    {
        Assert.Equal(SelectionHandoff.DefaultHandoffPath(), SelectionHandoffFiles.DefaultPath());
        Assert.Equal(SelectionHandoff.MaxAge, SelectionHandoffFiles.MaxAge);
    }

    [Fact]
    public void Plugin_reads_host_written_handoff()
    {
        var handoff = Path.Combine(_root, "selection");
        var written = SelectionHandoffFiles.Write("from host", handoff);
        Assert.True(written.IsOk);

        var taken = SelectionHandoff.TakeHandoff(
            handoff,
            DateTimeOffset.UtcNow,
            SelectionHandoff.MaxAge);
        Assert.True(taken.IsOk);
        Assert.Equal("from host", taken.Value);
        Assert.False(File.Exists(handoff));
    }

    [Fact]
    public void Host_write_creates_owner_only_file_on_unix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var handoff = Path.Combine(_root, "selection");
        var result = SelectionHandoffFiles.Write("secret", handoff);
        Assert.True(result.IsOk);

        var mode = File.GetUnixFileMode(handoff);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public void Host_write_resets_owner_only_mode_on_existing_unix_file()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var handoff = Path.Combine(_root, "selection");
        File.WriteAllText(handoff, "old");
        File.SetUnixFileMode(
            handoff,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var result = SelectionHandoffFiles.Write("secret", handoff);
        Assert.True(result.IsOk);
        Assert.Equal("secret", File.ReadAllText(handoff));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(handoff));
    }

    [Fact]
    public void ExtractSelection_uses_prefilled_copy_text_before_leave_copy_reset()
    {
        var copy = new CopyModeSession();
        var mouse = new MouseSelection();
        mouse.SeedText("ignored", cols: 20);
        mouse.Begin(0, 0);
        mouse.Extend(0, 6);

        var text = SelectionHandoffWriter.ExtractSelection(copy, mouse, "saved copy");

        Assert.Equal("saved copy", text);
    }

    private static AssembledSnapshot BuildSnapshot(string text, int cols = 40)
    {
        var line = text.PadRight(cols);
        var cells = new List<AssembledCell>(cols);
        foreach (var ch in line)
            cells.Add(new AssembledCell(ch.ToString(), 1, false, AssembledStyle.Default));

        return new AssembledSnapshot(
            "p1",
            cols,
            1,
            "basic",
            "main",
            [cells],
            default,
            AssembledCursor.Default);
    }
}
