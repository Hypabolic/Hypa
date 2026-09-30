using Hypa.Annotate.Application;
using Hypa.Annotate.Application.Tui;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotateManagerTuiTests : IDisposable
{
    private readonly string _root;

    public AnnotateManagerTuiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-annotate-manager-tui-" + Guid.NewGuid().ToString("N"));
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
    public void Empty_active_view_shows_no_active_annotations()
    {
        var app = CreateApp();
        var rows = app.Render(AnnotateManagerApp.InnerCols, AnnotateManagerApp.InnerRows);
        Assert.Contains(rows, row => row.Contains(AnnotateManagerApp.EmptyActive, StringComparison.Ordinal));
        Assert.Equal("No active annotations.", AnnotateManagerApp.EmptyActive);
        Assert.Contains(rows, row => row.Contains(AnnotateManagerApp.ActiveFooter, StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_archive_view_shows_no_archived_sets()
    {
        var app = CreateApp();
        app.HandleKey(Key(AnnotateKeyCode.Tab));
        Assert.Equal(ManagerView.Archives, app.View);
        var rows = app.Render(AnnotateManagerApp.InnerCols, AnnotateManagerApp.InnerRows);
        Assert.Contains(rows, row => row.Contains(AnnotateManagerApp.EmptyArchives, StringComparison.Ordinal));
        Assert.Equal("No archived sets.", AnnotateManagerApp.EmptyArchives);
    }

    [Fact]
    public void Tab_switches_between_active_and_archive_views()
    {
        var app = CreateApp();
        Assert.Equal(ManagerView.Active, app.View);
        app.HandleKey(Key(AnnotateKeyCode.Tab));
        Assert.Equal(ManagerView.Archives, app.View);
        app.HandleKey(Key(AnnotateKeyCode.Tab));
        Assert.Equal(ManagerView.Active, app.View);
    }

    [Fact]
    public void Shift_d_footer_asks_for_confirm()
    {
        AppendAnnotation("one");
        var app = CreateApp();
        app.HandleKey(Char('D'));
        var rows = app.Render(AnnotateManagerApp.InnerCols, AnnotateManagerApp.InnerRows);
        Assert.Contains(
            rows,
            row => row.Contains(AnnotateManagerApp.ClearConfirmFooter, StringComparison.Ordinal));
        Assert.Single(AnnotationStore.LoadAnnotations(_root).Value!);
    }

    [Fact]
    public void Shift_c_with_failing_clipboard_leaves_store_unchanged()
    {
        AppendAnnotation("one");
        var app = new AnnotateManagerApp(
            _root,
            new FailingClipboardWriter("Clipboard unavailable"),
            new SilentOsc52Emitter());
        app.HandleKey(Char('C'));
        Assert.False(app.Quit);
        Assert.Equal("Clipboard unavailable", app.Status);
        Assert.Single(AnnotationStore.LoadAnnotations(_root).Value!);
        Assert.Empty(AnnotationStore.LoadArchivedSets(_root).Value!);
    }

    [Fact]
    public void Manager_popup_size_is_one_hundred_by_thirty()
    {
        Assert.Equal(100, HypaBinPluginPaneOpener.ManagerWidth);
        Assert.Equal(30, HypaBinPluginPaneOpener.ManagerHeight);
        Assert.Equal(98, AnnotateManagerApp.InnerCols);
        Assert.Equal(28, AnnotateManagerApp.InnerRows);
        var arguments = HypaBinPluginPaneOpener.BuildOpenArguments(
            "annotate",
            "manager",
            "/plugin/root",
            "pane-1",
            HypaBinPluginPaneOpener.ManagerWidth,
            HypaBinPluginPaneOpener.ManagerHeight,
            envName: null,
            envValue: null);
        Assert.Contains("--placement", arguments);
        Assert.Contains("popup", arguments);
        Assert.Contains("--focus", arguments);
        Assert.Equal("100", arguments[arguments.IndexOf("--width") + 1]);
        Assert.Equal("30", arguments[arguments.IndexOf("--height") + 1]);
        Assert.DoesNotContain(arguments, argument => argument.Contains("HERDR_", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void Missing_tty_returns_nonzero()
    {
        Skip.If(
            !Console.IsInputRedirected && !Console.IsOutputRedirected,
            "stdin is a TTY.");
        var app = CreateApp();
        Assert.Equal(1, PluginTuiLoop.RunManager(app));
        Assert.False(app.Quit);
    }

    private AnnotateManagerApp CreateApp() =>
        new(_root, new RecordingClipboardWriter(), new SilentOsc52Emitter());

    private void AppendAnnotation(string id) =>
        Assert.True(AnnotationStore.AppendAnnotation(_root, SampleAnnotation(id)).IsOk);

    private static Annotation SampleAnnotation(string id) =>
        new()
        {
            SelectedText = $"selection {id}",
            CapturedAt = "2026-08-08T00:00:00Z",
            Context = new CaptureContext(),
            Id = id,
            Comment = $"comment {id}",
            CreatedAt = "2026-08-08T00:00:01Z",
        };

    private static AnnotateKey Char(char character) =>
        new(AnnotateKeyCode.Char, character, Control: false, Alt: false, Super: false);

    private static AnnotateKey Key(AnnotateKeyCode code) =>
        new(code, '\0', Control: false, Alt: false, Super: false);

    private sealed class RecordingClipboardWriter : IClipboardWriter
    {
        public Result<bool, string> TryWrite(string text) => Result<bool, string>.Ok(true);
    }

    private sealed class FailingClipboardWriter(string error) : IClipboardWriter
    {
        public Result<bool, string> TryWrite(string text) => Result<bool, string>.Fail(error);
    }

    private sealed class SilentOsc52Emitter : IOsc52Emitter
    {
        public bool TryEmit(string sequence) => false;
    }
}
