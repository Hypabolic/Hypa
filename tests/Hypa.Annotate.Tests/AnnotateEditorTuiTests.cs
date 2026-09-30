using System.Runtime.InteropServices;
using Hypa.Annotate.Application;
using Hypa.Annotate.Application.Tui;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotateEditorTuiTests : IDisposable
{
    private readonly string _root;

    public AnnotateEditorTuiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-annotate-editor-tui-" + Guid.NewGuid().ToString("N"));
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
    public void Render_inner_grid_contains_selection_comment_and_footer()
    {
        var app = CreateApp("first selected line\nsecond line");
        var rows = app.Render(AnnotateEditorApp.InnerCols, AnnotateEditorApp.InnerRows);
        Assert.Equal(AnnotateEditorApp.InnerRows, rows.Count);
        Assert.Contains(rows, row => row.Contains("Selected text", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("first selected line", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("Comment", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(AnnotateEditorApp.FooterHint, StringComparison.Ordinal));
    }

    [Fact]
    public void Control_s_with_comment_appends_one_annotation_and_shows_saved()
    {
        var app = CreateApp("selected");
        Type(app, "saved comment");
        Assert.True(app.HandleKey(Control('s')));
        Assert.True(app.TrySave(_root));
        Assert.Equal(AnnotateEditorApp.SavedStatus, app.Status);
        Assert.Contains(
            app.Render(AnnotateEditorApp.InnerCols, AnnotateEditorApp.InnerRows),
            row => row.Contains("Saved.", StringComparison.Ordinal));

        var loaded = AnnotationStore.LoadAnnotations(_root);
        Assert.True(loaded.IsOk);
        var annotation = Assert.Single(loaded.Value);
        Assert.Equal("saved comment", annotation.Comment);
        Assert.Equal("selected", annotation.SelectedText);
    }

    [Fact]
    public void Escape_writes_no_annotation()
    {
        var app = CreateApp("selected");
        Type(app, "discarded");
        app.HandleKey(Key(AnnotateKeyCode.Esc));
        Assert.True(app.Quit);
        Assert.False(app.Saved);
        var loaded = AnnotationStore.LoadAnnotations(_root);
        Assert.True(loaded.IsOk);
        Assert.Empty(loaded.Value);
    }

    [Fact]
    public void Empty_control_s_keeps_popup_open()
    {
        var app = CreateApp("selected");
        Assert.True(app.HandleKey(Control('s')));
        Assert.False(app.TrySave(_root));
        Assert.False(app.Quit);
        Assert.Equal(AnnotateEditorApp.EmptyCommentStatus, app.Status);
        Assert.Empty(AnnotationStore.LoadAnnotations(_root).Value!);
    }

    [Fact]
    public void Escape_after_typing_does_not_accept_comment()
    {
        var app = CreateApp("selected");
        Type(app, "typed comment");
        app.HandleKey(Key(AnnotateKeyCode.Esc));
        Assert.True(app.Quit);
        Assert.False(app.Accepted);
        Assert.False(app.TryAcceptComment(out var comment));
        Assert.Equal("", comment);
        Assert.False(app.TrySave(_root));
        Assert.Empty(AnnotationStore.LoadAnnotations(_root).Value!);
    }

    [Fact]
    public void Last_review_titles_use_agent_message_and_review()
    {
        var app = new AnnotateEditorApp(new PendingAnnotation
        {
            SelectedText = "agent text",
            CapturedAt = "2026-09-11T00:00:00.000Z",
            Context = new CaptureContext(),
        })
        {
            SelectionTitle = "Agent message",
            CommentTitle = "Review",
            Hint = "Ctrl+S send  ·  Esc cancel  ·  Enter new line",
        };
        var rows = app.Render(AnnotateEditorApp.InnerCols, AnnotateEditorApp.InnerRows);
        Assert.Contains(rows, row => row.Contains("Agent message", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("Review", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("Selected text", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_dims_selected_preview_and_places_comment_cursor()
    {
        var app = CreateApp("first selected line");
        Type(app, "hi");
        var grid = app.RenderGrid(AnnotateEditorApp.InnerCols, AnnotateEditorApp.InnerRows);
        var previewY = -1;
        for (var y = 0; y < grid.RowsCount; y++)
        {
            if (grid.Rows[y].Contains("first selected line", StringComparison.Ordinal))
            {
                previewY = y;
                break;
            }
        }

        Assert.True(previewY >= 0);
        var previewX = grid.Rows[previewY].IndexOf("first selected line", StringComparison.Ordinal);
        Assert.Equal(CellPaintStyle.Dim, grid.StyleAt(previewX, previewY));
        Assert.Equal(CellPaintStyle.Bold, grid.StyleAt(2, 1));
        Assert.NotNull(grid.CursorCol);
        Assert.NotNull(grid.CursorRow);
        var ansi = grid.PaintAnsi();
        Assert.Contains("\u001b[2m", ansi, StringComparison.Ordinal);
        Assert.Contains(
            "\u001b[" + (grid.CursorRow.Value + 1) + ";" + (grid.CursorCol.Value + 1) + "H",
            ansi,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_popup_size_is_eighty_eight_by_twenty_four()
    {
        Assert.Equal(88, HypaBinPluginPaneOpener.EditorWidth);
        Assert.Equal(24, HypaBinPluginPaneOpener.EditorHeight);
        Assert.Equal(86, AnnotateEditorApp.InnerCols);
        Assert.Equal(22, AnnotateEditorApp.InnerRows);
    }

    [SkippableFact]
    public void Missing_tty_returns_nonzero_without_line_save()
    {
        Skip.If(
            !Console.IsInputRedirected && !Console.IsOutputRedirected,
            "stdin is a TTY.");
        var app = CreateApp("selected");
        Type(app, "should not save");
        var code = PluginTuiLoop.RunEditor(app, _root, saveToStore: true);
        Assert.Equal(1, code);
        Assert.False(app.Saved);
        Assert.Empty(AnnotationStore.LoadAnnotations(_root).Value!);
    }

    [SkippableFact]
    public void Hangup_interrupt_and_term_register_quit_callbacks()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "POSIX signals.");
        Assert.Contains(PosixSignal.SIGHUP, PluginTuiLoop.PluginTuiSignals.Handled);
        Assert.Contains(PosixSignal.SIGINT, PluginTuiLoop.PluginTuiSignals.Handled);
        Assert.Contains(PosixSignal.SIGTERM, PluginTuiLoop.PluginTuiSignals.Handled);
        using var signals = new PluginTuiLoop.PluginTuiSignals(() => { });
        Assert.Equal(PluginTuiLoop.PluginTuiSignals.Handled.Length, signals.RegistrationCount);
    }

    [Fact]
    public void Send_text_arguments_do_not_append_a_second_newline()
    {
        var arguments = HypaBinPluginPaneSender.BuildSendTextArguments("pane-1", "review note");
        Assert.Equal(["plugin", "pane", "send-text", "pane-1", "--text", "review note"], arguments);
        Assert.DoesNotContain("\n", arguments[^1], StringComparison.Ordinal);
    }

    private AnnotateEditorApp CreateApp(string selectedText) =>
        new(new PendingAnnotation
        {
            SelectedText = selectedText,
            CapturedAt = "2026-09-11T00:00:00.000Z",
            Context = new CaptureContext(),
        });

    private static void Type(AnnotateEditorApp app, string text)
    {
        foreach (var character in text)
            app.HandleKey(Key(AnnotateKeyCode.Char, character));
    }

    private static AnnotateKey Control(char character) =>
        new(AnnotateKeyCode.Char, character, Control: true, Alt: false, Super: false);

    private static AnnotateKey Key(AnnotateKeyCode code, char character = '\0') =>
        new(code, character, Control: false, Alt: false, Super: false);
}
