using Hypa.Annotate.Application;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotateManagerTests : IDisposable
{
    private readonly string _root;

    public AnnotateManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-annotate-manager-" + Guid.NewGuid().ToString("N"));
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
    public void Empty_store_prints_message_and_exits_zero()
    {
        var output = new StringWriter();
        var result = AnnotateManagerService.Run(new AnnotateManagerRequest
        {
            StateDirectory = _root,
            ManagerSession = new FixedManagerSession([]),
            ClipboardWriter = new RecordingClipboardWriter(),
            Osc52Emitter = new RecordingOsc52Emitter(),
            Output = output,
        });

        Assert.True(result.IsOk);
        Assert.Equal(AnnotateManagerService.EmptyStoreMessage + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void Manager_copies_one_annotation()
    {
        AppendAnnotation("one");
        AppendAnnotation("two");
        var clipboard = new RecordingClipboardWriter();
        var output = new StringWriter();

        var result = AnnotateManagerService.Run(new AnnotateManagerRequest
        {
            StateDirectory = _root,
            ManagerSession = new FixedManagerSession([
                new AnnotateManagerCommand { Action = AnnotateManagerAction.CopyOne, Index = 1 },
            ]),
            ClipboardWriter = clipboard,
            Osc52Emitter = new RecordingOsc52Emitter(),
            Output = output,
        });

        Assert.True(result.IsOk);
        Assert.Contains("selection two", clipboard.LastText, StringComparison.Ordinal);
        Assert.Equal(2, AnnotationStore.LoadAnnotations(_root).Value!.Count);
    }

    [Fact]
    public void Manager_archives_active_set()
    {
        AppendAnnotation("one");
        AppendAnnotation("two");
        var clipboard = new RecordingClipboardWriter();

        var result = AnnotateManagerService.Run(new AnnotateManagerRequest
        {
            StateDirectory = _root,
            ManagerSession = new FixedManagerSession([
                new AnnotateManagerCommand { Action = AnnotateManagerAction.ArchiveAll },
            ]),
            ClipboardWriter = clipboard,
            Osc52Emitter = new RecordingOsc52Emitter(),
            Output = new StringWriter(),
            NewArchiveId = () => "archive-one",
            NowIso = () => "2026-08-26T23:32:00Z",
        });

        Assert.True(result.IsOk);
        Assert.Empty(AnnotationStore.LoadAnnotations(_root).Value!);
        var archives = AnnotationStore.LoadArchivedSets(_root);
        Assert.True(archives.IsOk);
        var archive = Assert.Single(archives.Value);
        Assert.Equal("archive-one", archive.Id);
        Assert.Equal(2, archive.Annotations.Count);
        Assert.Contains("selection two", clipboard.LastText, StringComparison.Ordinal);
    }

    [Fact]
    public void Manager_restores_archived_set()
    {
        AppendAnnotation("live");
        var archived = new ArchivedAnnotationSet
        {
            Id = "archive-one",
            ArchivedAt = "2026-08-26T23:32:00Z",
            Annotations = [SampleAnnotation("restored")],
        };
        Assert.True(AnnotationStore.AppendArchivedSet(_root, archived).IsOk);

        var result = AnnotateManagerService.Run(new AnnotateManagerRequest
        {
            StateDirectory = _root,
            ManagerSession = new FixedManagerSession([
                new AnnotateManagerCommand { Action = AnnotateManagerAction.RestoreArchive, Index = 1 },
            ]),
            ClipboardWriter = new RecordingClipboardWriter(),
            Osc52Emitter = new RecordingOsc52Emitter(),
            Output = new StringWriter(),
        });

        Assert.True(result.IsOk);
        var active = AnnotationStore.LoadAnnotations(_root);
        Assert.True(active.IsOk);
        Assert.Contains(active.Value, item => item.Id == "restored");
        Assert.Contains(active.Value, item => item.Id == "live");
        Assert.Empty(AnnotationStore.LoadArchivedSets(_root).Value!);
    }

    [Fact]
    public void Manager_deletes_active_and_archived_entries()
    {
        AppendAnnotation("one");
        Assert.True(AnnotationStore.AppendArchivedSet(
            _root,
            new ArchivedAnnotationSet
            {
                Id = "archive-one",
                ArchivedAt = "2026-08-26T23:32:00Z",
                Annotations = [SampleAnnotation("archived")],
            }).IsOk);

        var result = AnnotateManagerService.Run(new AnnotateManagerRequest
        {
            StateDirectory = _root,
            ManagerSession = new FixedManagerSession([
                new AnnotateManagerCommand { Action = AnnotateManagerAction.DeleteActive, Index = 1 },
                new AnnotateManagerCommand { Action = AnnotateManagerAction.DeleteArchive, Index = 1 },
            ]),
            ClipboardWriter = new RecordingClipboardWriter(),
            Osc52Emitter = new RecordingOsc52Emitter(),
            Output = new StringWriter(),
        });

        Assert.True(result.IsOk);
        Assert.Empty(AnnotationStore.LoadAnnotations(_root).Value!);
        Assert.Empty(AnnotationStore.LoadArchivedSets(_root).Value!);
    }

    [Fact]
    public void Manager_lists_newest_first()
    {
        AppendAnnotation("one");
        AppendAnnotation("two");
        var output = new StringWriter();
        var session = new CaptureManagerSession([
            new AnnotateManagerCommand { Action = AnnotateManagerAction.Quit },
        ]);

        _ = AnnotateManagerService.Run(new AnnotateManagerRequest
        {
            StateDirectory = _root,
            ManagerSession = session,
            ClipboardWriter = new RecordingClipboardWriter(),
            Osc52Emitter = new RecordingOsc52Emitter(),
            Output = output,
        });

        var rendered = session.Rendered!;
        var two = rendered.IndexOf("selection two", StringComparison.Ordinal);
        var one = rendered.IndexOf("selection one", StringComparison.Ordinal);
        Assert.True(two >= 0 && one >= 0);
        Assert.True(two < one);
    }

    [Fact]
    public void Manager_list_sanitizes_and_clips_selected_text()
    {
        Assert.True(AnnotationStore.AppendAnnotation(
            _root,
            SampleAnnotation("ctrl") with
            {
                SelectedText = "safe\u001b[2J\ttext\nnext " + new string('x', 120),
            }).IsOk);

        var session = new CaptureManagerSession([
            new AnnotateManagerCommand { Action = AnnotateManagerAction.Quit },
        ]);
        _ = AnnotateManagerService.Run(new AnnotateManagerRequest
        {
            StateDirectory = _root,
            ManagerSession = session,
            ClipboardWriter = new RecordingClipboardWriter(),
            Osc52Emitter = new RecordingOsc52Emitter(),
            Output = new StringWriter(),
        });

        var rendered = session.Rendered!;
        Assert.DoesNotContain("\u001b", rendered, StringComparison.Ordinal);
        Assert.Contains("safe[2J text next", rendered, StringComparison.Ordinal);
        Assert.Contains("…", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 120), rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Manager_text_sanitize_matches_control_character_rules()
    {
        Assert.Equal(
            "safe[2J    text\nnext",
            AnnotateTerminalText.Sanitize("safe\u001b[2J\ttext\nnext"));
        Assert.Equal(
            "safe[2J text next",
            AnnotateTerminalText.ClipForList("safe\u001b[2J\ttext\nnext", 96));
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

    private sealed class FixedManagerSession(IReadOnlyList<AnnotateManagerCommand> commands) : IAnnotateManagerSession
    {
        private int _index;

        public AnnotateManagerCommand? ReadCommand(AnnotateManagerState state)
        {
            if (_index >= commands.Count)
                return new AnnotateManagerCommand { Action = AnnotateManagerAction.Quit };

            return commands[_index++];
        }
    }

    private sealed class CaptureManagerSession(IReadOnlyList<AnnotateManagerCommand> commands) : IAnnotateManagerSession
    {
        private int _index;

        public string? Rendered { get; private set; }

        public AnnotateManagerCommand? ReadCommand(AnnotateManagerState state)
        {
            var buffer = new StringWriter();
            ConsoleAnnotateManagerSession.Render(state, buffer);
            Rendered = buffer.ToString();
            if (_index >= commands.Count)
                return new AnnotateManagerCommand { Action = AnnotateManagerAction.Quit };

            return commands[_index++];
        }
    }

    private sealed class RecordingClipboardWriter : IClipboardWriter
    {
        public string? LastText { get; private set; }

        public Result<bool, string> TryWrite(string text)
        {
            LastText = text;
            return Result<bool, string>.Ok(true);
        }
    }

    private sealed class RecordingOsc52Emitter : IOsc52Emitter
    {
        public bool TryEmit(string sequence) => false;
    }
}
