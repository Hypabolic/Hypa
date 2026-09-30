using Hypa.Annotate.Application;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotateEditorTests : IDisposable
{
    private readonly string _root;

    public AnnotateEditorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-annotate-editor-" + Guid.NewGuid().ToString("N"));
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
    public void Saving_appends_one_annotation_to_store()
    {
        var pendingPath = WritePending("selected", "2026-09-11T00:00:00.000Z");
        var result = AnnotateEditorService.Run(new AnnotateEditorRequest
        {
            StateDirectory = _root,
            PendingPath = pendingPath,
            EditorSession = new FixedEditorSession(new AnnotateEditorSessionResult
            {
                Outcome = AnnotateEditorOutcome.Saved,
                Comment = "saved comment",
            }),
            NewId = () => "annotation-1",
            NowIso = () => "2026-09-11T00:00:01.000Z",
        });

        Assert.True(result.IsOk);
        Assert.False(File.Exists(pendingPath));
        var loaded = AnnotationStore.LoadAnnotations(_root);
        Assert.True(loaded.IsOk);
        var annotation = Assert.Single(loaded.Value);
        Assert.Equal("annotation-1", annotation.Id);
        Assert.Equal("saved comment", annotation.Comment);
        Assert.Equal("selected", annotation.SelectedText);
    }

    [Fact]
    public void Cancelling_writes_no_annotation()
    {
        var pendingPath = WritePending("selected", "2026-09-11T00:00:00.000Z");
        var result = AnnotateEditorService.Run(new AnnotateEditorRequest
        {
            StateDirectory = _root,
            PendingPath = pendingPath,
            EditorSession = new FixedEditorSession(new AnnotateEditorSessionResult
            {
                Outcome = AnnotateEditorOutcome.Cancelled,
            }),
        });

        Assert.True(result.IsOk);
        var loaded = AnnotationStore.LoadAnnotations(_root);
        Assert.True(loaded.IsOk);
        Assert.Empty(loaded.Value);
    }

    private string WritePending(string selectedText, string capturedAt)
    {
        var pending = new PendingAnnotation
        {
            SelectedText = selectedText,
            CapturedAt = capturedAt,
            Context = new CaptureContext(),
        };
        var written = PendingAnnotationFiles.WritePending(_root, pending);
        Assert.True(written.IsOk);
        return written.Value;
    }

    private sealed class FixedEditorSession(AnnotateEditorSessionResult result) : IAnnotateEditorSession
    {
        public AnnotateEditorSessionResult Run(PendingAnnotation pending) => result;
    }
}
