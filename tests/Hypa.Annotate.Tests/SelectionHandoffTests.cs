using Hypa.Annotate.Application;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class SelectionHandoffTests : IDisposable
{
    private readonly string _root;

    public SelectionHandoffTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-annotate-handoff-" + Guid.NewGuid().ToString("N"));
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
    public void WriteHandoff_writes_expected_contents()
    {
        var handoff = Path.Combine(_root, "selection");
        var result = SelectionHandoff.WriteHandoff("hello\nworld\n", handoff);

        Assert.True(result.IsOk);
        Assert.Equal("hello\nworld\n", File.ReadAllText(handoff));
    }

    [Fact]
    public void Empty_selection_writes_no_file()
    {
        var handoff = Path.Combine(_root, "selection");
        var blank = SelectionHandoff.WriteHandoff("  \n", handoff);
        var missing = SelectionHandoff.WriteHandoff(null, handoff);

        Assert.True(blank.IsOk);
        Assert.True(missing.IsOk);
        Assert.False(File.Exists(handoff));
    }

    [Fact]
    public void Fresh_handoff_is_returned_and_removed()
    {
        var handoff = Path.Combine(_root, "selection");
        Directory.CreateDirectory(Path.GetDirectoryName(handoff)!);
        File.WriteAllText(handoff, "handoff text");

        var taken = SelectionHandoff.TakeHandoff(
            handoff,
            DateTimeOffset.UtcNow,
            SelectionHandoff.MaxAge);

        Assert.True(taken.IsOk);
        Assert.Equal("handoff text", taken.Value);
        Assert.False(File.Exists(handoff));
    }

    [Fact]
    public async Task Capture_prefers_handoff_over_clipboard()
    {
        var handoff = Path.Combine(_root, "selection");
        Directory.CreateDirectory(Path.GetDirectoryName(handoff)!);
        File.WriteAllText(handoff, "from handoff");

        var taken = SelectionHandoff.TakeHandoff(
            handoff,
            DateTimeOffset.UtcNow,
            SelectionHandoff.MaxAge);
        Assert.True(taken.IsOk);
        Assert.Equal("from handoff", taken.Value);

        var opener = new RecordingPaneOpener { Result = Result<Unit, string>.Ok(default) };
        var result = await AnnotateCaptureService.RunAsync(
            new AnnotateCaptureRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-1",
                HandoffText = taken.Value,
                ClipboardReader = new FixedClipboardReader("from clipboard"),
                PaneOpener = opener,
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        var pendingPath = Assert.Single(Directory.GetFiles(_root, "pending-*.json"));
        var pending = PendingAnnotationFiles.ReadPending(pendingPath);
        Assert.True(pending.IsOk);
        Assert.Equal("from handoff", pending.Value.SelectedText);
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
    public void Handoff_path_uses_hypa_native_directory_segment()
    {
        var path = SelectionHandoff.DefaultHandoffPath();
        Assert.Equal("selection", Path.GetFileName(path));
        Assert.Contains("hypa-annotate-", Path.GetDirectoryName(path), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteHandoff_creates_owner_only_file_on_unix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var handoff = Path.Combine(_root, "selection");
        var result = SelectionHandoff.WriteHandoff("secret", handoff);
        Assert.True(result.IsOk);

        var mode = File.GetUnixFileMode(handoff);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    private sealed class FixedClipboardReader(string text) : IClipboardReader
    {
        public string ReadText() => text;
    }

    private sealed class RecordingPaneOpener : IPluginPaneOpener
    {
        public Result<Unit, string> Result { get; init; }

        public Task<Result<Unit, string>> OpenEditorPopupAsync(
            PluginPaneOpenRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result);

        public Task<Result<Unit, string>> OpenLastReviewPopupAsync(
            PluginPaneOpenRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result);

        public Task<Result<Unit, string>> OpenManagerPopupAsync(
            PluginManagerPaneOpenRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }
}
