using Hypa.Annotate.Application;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotateCaptureTests : IDisposable
{
    private readonly string _root;

    public AnnotateCaptureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-annotate-capture-" + Guid.NewGuid().ToString("N"));
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
    public async Task Capture_with_clipboard_text_writes_pending_file()
    {
        var opener = new RecordingPaneOpener { Result = Result<Unit, string>.Ok(default) };
        var result = await AnnotateCaptureService.RunAsync(
            new AnnotateCaptureRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-1",
                ContextJson = """{"workspace_id":"w1"}""",
                HandoffText = null,
                ClipboardReader = new FixedClipboardReader("clipboard text"),
                PaneOpener = opener,
                NowIso = () => "2026-09-11T00:00:00.000Z",
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(AnnotateCaptureCompletion.EditorOpened, result.Value);
        Assert.NotNull(opener.LastRequest);
        Assert.Single(Directory.GetFiles(_root, "pending-*.json"));
    }

    [Fact]
    public async Task Capture_with_blank_text_exits_without_pending_file()
    {
        var opener = new RecordingPaneOpener { Result = Result<Unit, string>.Ok(default) };
        var result = await AnnotateCaptureService.RunAsync(
            new AnnotateCaptureRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-1",
                ClipboardReader = new FixedClipboardReader("  \n"),
                PaneOpener = opener,
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(AnnotateCaptureCompletion.BlankSelection, result.Value);
        Assert.Equal("Nothing to annotate.", AnnotateCaptureService.BlankSelectionMessage);
        Assert.Equal("Nothing to annotate", AnnotateCaptureService.BlankSelectionTitle);
        Assert.Equal(
            "Select text in the pane or copy text to the clipboard.",
            AnnotateCaptureService.BlankSelectionBody);
        Assert.Empty(Directory.GetFiles(_root, "pending-*.json"));
        Assert.Null(opener.LastRequest);
    }

    [Fact]
    public void Stale_handoff_file_is_ignored_and_removed()
    {
        var handoff = Path.Combine(_root, "selection");
        Directory.CreateDirectory(Path.GetDirectoryName(handoff)!);
        File.WriteAllText(handoff, "stale text");
        File.SetLastWriteTimeUtc(handoff, DateTime.UtcNow.AddSeconds(-30));

        var taken = SelectionHandoff.TakeHandoff(
            handoff,
            DateTimeOffset.UtcNow,
            SelectionHandoff.MaxAge);

        Assert.True(taken.IsOk);
        Assert.Null(taken.Value);
        Assert.False(File.Exists(handoff));
    }

    [Fact]
    public async Task Failed_pane_open_removes_pending_file()
    {
        var opener = new RecordingPaneOpener { Result = Result<Unit, string>.Fail("open failed") };
        var result = await AnnotateCaptureService.RunAsync(
            new AnnotateCaptureRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-1",
                ClipboardReader = new FixedClipboardReader("keep me"),
                PaneOpener = opener,
            },
            CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Empty(Directory.GetFiles(_root, "pending-*.json"));
    }

    [Fact]
    public async Task Throwing_pane_open_removes_pending_file()
    {
        var result = await AnnotateCaptureService.RunAsync(
            new AnnotateCaptureRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-1",
                ClipboardReader = new FixedClipboardReader("keep me"),
                PaneOpener = new ThrowingPaneOpener(new IOException("socket closed")),
            },
            CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("socket closed", result.Error);
        Assert.Empty(Directory.GetFiles(_root, "pending-*.json"));
    }

    [Fact]
    public async Task Protocol_exception_pane_open_removes_pending_file()
    {
        var result = await AnnotateCaptureService.RunAsync(
            new AnnotateCaptureRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-1",
                ClipboardReader = new FixedClipboardReader("keep me"),
                PaneOpener = new ThrowingPaneOpener(new InvalidOperationException("Invalid response")),
            },
            CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("Invalid response", result.Error);
        Assert.Empty(Directory.GetFiles(_root, "pending-*.json"));
    }

    [Fact]
    public async Task Capture_does_not_use_selected_text_from_context_json()
    {
        var opener = new RecordingPaneOpener { Result = Result<Unit, string>.Ok(default) };
        var result = await AnnotateCaptureService.RunAsync(
            new AnnotateCaptureRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-1",
                ContextJson = """{"selected_text":"ignored selection"}""",
                HandoffText = null,
                ClipboardReader = new FixedClipboardReader("from clipboard"),
                PaneOpener = opener,
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        var pendingPath = Assert.Single(Directory.GetFiles(_root, "pending-*.json"));
        var pending = PendingAnnotationFiles.ReadPending(pendingPath);
        Assert.True(pending.IsOk);
        Assert.Equal("from clipboard", pending.Value.SelectedText);
    }

    [Fact]
    public async Task Editor_open_request_is_focused_popup_with_pending_env_and_target_pane()
    {
        var opener = new RecordingPaneOpener { Result = Result<Unit, string>.Ok(default) };
        _ = await AnnotateCaptureService.RunAsync(
            new AnnotateCaptureRequest
            {
                StateDirectory = _root,
                PluginRoot = "/plugin/root",
                TargetPaneId = "pane-target",
                ClipboardReader = new FixedClipboardReader("text"),
                PaneOpener = opener,
            },
            CancellationToken.None);

        Assert.NotNull(opener.LastRequest);
        var request = opener.LastRequest!;
        Assert.Equal("/plugin/root", request.PluginRoot);
        Assert.Equal("pane-target", request.TargetPaneId);
        Assert.StartsWith(_root, request.PendingFilePath, StringComparison.Ordinal);
        Assert.DoesNotContain("HERDR_", request.PendingFilePath, StringComparison.Ordinal);
    }

    private sealed class FixedClipboardReader(string text) : IClipboardReader
    {
        public string ReadText() => text;
    }

    private sealed class RecordingPaneOpener : IPluginPaneOpener
    {
        public Result<Unit, string> Result { get; init; }

        public PluginPaneOpenRequest? LastRequest { get; private set; }

        public Task<Result<Unit, string>> OpenEditorPopupAsync(
            PluginPaneOpenRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }

        public Task<Result<Unit, string>> OpenLastReviewPopupAsync(
            PluginPaneOpenRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }

        public Task<Result<Unit, string>> OpenManagerPopupAsync(
            PluginManagerPaneOpenRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(Result);
        }
    }

    private sealed class ThrowingPaneOpener(Exception error) : IPluginPaneOpener
    {
        public Task<Result<Unit, string>> OpenEditorPopupAsync(
            PluginPaneOpenRequest request,
            CancellationToken cancellationToken)
        {
            throw error;
        }

        public Task<Result<Unit, string>> OpenLastReviewPopupAsync(
            PluginPaneOpenRequest request,
            CancellationToken cancellationToken)
        {
            throw error;
        }

        public Task<Result<Unit, string>> OpenManagerPopupAsync(
            PluginManagerPaneOpenRequest request,
            CancellationToken cancellationToken)
        {
            throw error;
        }
    }
}
