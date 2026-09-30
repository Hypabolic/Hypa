using Hypa.Annotate.Application;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotateCopyArchiveTests : IDisposable
{
    private readonly string _root;

    public AnnotateCopyArchiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-annotate-copy-archive-" + Guid.NewGuid().ToString("N"));
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
    public void CopyArchive_copies_then_archives_active_set()
    {
        AppendAnnotation("one");
        AppendAnnotation("two");
        var clipboard = new RecordingClipboardWriter();

        var result = AnnotateCopyArchiveService.Run(new CopyArchiveRequest
        {
            StateDirectory = _root,
            ClipboardWriter = clipboard,
            Osc52Emitter = new RecordingOsc52Emitter(),
            NewArchiveId = () => "archive-one",
            NowIso = () => "2026-08-26T23:32:00Z",
        });

        Assert.True(result.IsOk);
        Assert.Equal("2 annotations copied as Markdown and archived.", result.Value.Line);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.Empty(AnnotationStore.LoadAnnotations(_root).Value!);
        var archive = Assert.Single(AnnotationStore.LoadArchivedSets(_root).Value!);
        Assert.Equal(2, archive.Annotations.Count);
        Assert.Contains("selection two", clipboard.LastText, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyArchive_failed_copy_leaves_active_set_unchanged()
    {
        AppendAnnotation("one");
        var result = AnnotateCopyArchiveService.Run(new CopyArchiveRequest
        {
            StateDirectory = _root,
            ClipboardWriter = new FailingClipboardWriter("Clipboard unavailable"),
            Osc52Emitter = new RecordingOsc52Emitter(),
        });

        Assert.True(result.IsOk);
        Assert.Equal("Clipboard unavailable", result.Value.Line);
        Assert.Equal(1, result.Value.ExitCode);
        Assert.Single(AnnotationStore.LoadAnnotations(_root).Value!);
        Assert.Empty(AnnotationStore.LoadArchivedSets(_root).Value!);
    }

    [Fact]
    public void CopyArchive_osc52_only_success_still_archives()
    {
        AppendAnnotation("one");
        var result = AnnotateCopyArchiveService.Run(new CopyArchiveRequest
        {
            StateDirectory = _root,
            ClipboardWriter = new FailingClipboardWriter("No supported clipboard writer is available"),
            Osc52Emitter = new RecordingOsc52Emitter { EmitSuccess = true },
            NewArchiveId = () => "archive-one",
            NowIso = () => "2026-08-26T23:32:00Z",
        });

        Assert.True(result.IsOk);
        Assert.Equal("1 annotation copied as Markdown and archived.", result.Value.Line);
        Assert.Empty(AnnotationStore.LoadAnnotations(_root).Value!);
        Assert.Single(AnnotationStore.LoadArchivedSets(_root).Value!);
    }

    [SkippableFact]
    public void CopyArchive_linux_missing_native_clipboard_still_archives_via_osc52()
    {
        Skip.If(!OperatingSystem.IsLinux(), "Linux native clipboard commands only.");

        AppendAnnotation("one");
        WithEmptyPath(() =>
        {
            var native = new NativeClipboardWriter().TryWrite("hello");
            Assert.False(native.IsOk);
            Assert.Equal("No supported clipboard writer is available", native.Error);

            var result = AnnotateCopyArchiveService.Run(new CopyArchiveRequest
            {
                StateDirectory = _root,
                ClipboardWriter = new NativeClipboardWriter(),
                Osc52Emitter = new RecordingOsc52Emitter { EmitSuccess = true },
                NewArchiveId = () => "archive-one",
                NowIso = () => "2026-08-26T23:32:00Z",
            });

            Assert.True(result.IsOk);
            Assert.Equal("1 annotation copied as Markdown and archived.", result.Value.Line);
            Assert.Equal(0, result.Value.ExitCode);
            Assert.Empty(AnnotationStore.LoadAnnotations(_root).Value!);
            Assert.Single(AnnotationStore.LoadArchivedSets(_root).Value!);
        });
    }

    [SkippableFact]
    public void Native_clipboard_missing_command_returns_failed_result()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix PATH lookup for missing clipboard commands.");

        WithEmptyPath(() =>
        {
            var native = new NativeClipboardWriter().TryWrite("hello");
            Assert.False(native.IsOk);
            Assert.Equal("No supported clipboard writer is available", native.Error);
        });
    }

    [Fact]
    public void CopyArchive_empty_store_prints_message_and_exits_zero()
    {
        var result = AnnotateCopyArchiveService.Run(new CopyArchiveRequest
        {
            StateDirectory = _root,
            ClipboardWriter = new RecordingClipboardWriter(),
            Osc52Emitter = new RecordingOsc52Emitter(),
        });

        Assert.True(result.IsOk);
        Assert.Equal(AnnotateCopyArchiveService.EmptyMessage, result.Value.Line);
        Assert.Equal(0, result.Value.ExitCode);
    }

    private void WithEmptyPath(Action body)
    {
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var emptyPath = Path.Combine(_root, "empty-path");
        Directory.CreateDirectory(emptyPath);
        try
        {
            Environment.SetEnvironmentVariable("PATH", emptyPath);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

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

    private sealed class RecordingClipboardWriter : IClipboardWriter
    {
        public string? LastText { get; private set; }

        public Result<bool, string> TryWrite(string text)
        {
            LastText = text;
            return Result<bool, string>.Ok(true);
        }
    }

    private sealed class FailingClipboardWriter(string error) : IClipboardWriter
    {
        public Result<bool, string> TryWrite(string text) =>
            Result<bool, string>.Fail(error);
    }

    private sealed class RecordingOsc52Emitter : IOsc52Emitter
    {
        public bool EmitSuccess { get; init; }

        public bool TryEmit(string sequence) => EmitSuccess;
    }
}
