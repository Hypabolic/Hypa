using Hypa.Annotate.Application;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotateCopyContextTests
{
    private static int _nextDirectory;

    [Fact]
    public void EmptyStore_isEmptyOutcome()
    {
        var directory = CreateTemporaryDirectory();
        var clipboard = new MemoryClipboard();

        var result = AnnotateCopyContext.Run(directory, clipboard);

        Assert.True(result.IsOk, result.IsOk ? "" : result.Error);
        Assert.Equal(CopyContextKind.Empty, result.Value.Kind);
        Assert.Null(clipboard.Text);
    }

    [Fact]
    public void PopulatedStore_copiesMarkdown()
    {
        var directory = CreateTemporaryDirectory();
        Assert.True(AnnotationStore.AppendAnnotation(
            directory,
            new Annotation
            {
                SelectedText = "selected body",
                CapturedAt = "2026-08-08T00:00:00Z",
                Context = new CaptureContext(),
                Id = "one",
                Comment = "review this",
                CreatedAt = "2026-08-08T00:00:01Z",
            }).IsOk);
        var clipboard = new MemoryClipboard();

        var result = AnnotateCopyContext.Run(directory, clipboard);

        Assert.True(result.IsOk, result.IsOk ? "" : result.Error);
        Assert.Equal(CopyContextKind.Copied, result.Value.Kind);
        Assert.Equal(1, result.Value.Count);
        Assert.Contains("# Annotated context", clipboard.Text);
        Assert.Contains("selected body", clipboard.Text);
        Assert.Contains("review this", clipboard.Text);
        Assert.Equal("1 annotation copied as Markdown.", AnnotateCopyContext.CopiedBody(1));
        Assert.Equal("2 annotations copied as Markdown.", AnnotateCopyContext.CopiedBody(2));
    }

    [Fact]
    public void ClipboardFailure_returnsError()
    {
        var directory = CreateTemporaryDirectory();
        Assert.True(AnnotationStore.AppendAnnotation(
            directory,
            new Annotation
            {
                SelectedText = "body",
                CapturedAt = "2026-08-08T00:00:00Z",
                Context = new CaptureContext(),
                Id = "one",
                Comment = "note",
                CreatedAt = "2026-08-08T00:00:01Z",
            }).IsOk);

        var result = AnnotateCopyContext.Run(directory, new FailingClipboard("no clipboard"));

        Assert.False(result.IsOk);
        Assert.Equal("no clipboard", result.Error);
    }

    private static string CreateTemporaryDirectory()
    {
        var sequence = Interlocked.Increment(ref _nextDirectory);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"hypa-copy-context-{Environment.ProcessId}-{sequence}");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class MemoryClipboard : IClipboardWriter
    {
        public string? Text { get; private set; }

        public Result<bool, string> TryWrite(string text)
        {
            Text = text;
            return Result<bool, string>.Ok(true);
        }
    }

    private sealed class FailingClipboard(string error) : IClipboardWriter
    {
        public Result<bool, string> TryWrite(string text) => Result<bool, string>.Fail(error);
    }
}
