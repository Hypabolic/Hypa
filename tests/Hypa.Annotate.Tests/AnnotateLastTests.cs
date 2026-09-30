using Hypa.Annotate.Application;
using Hypa.Annotate.Application.Tui;
using Hypa.Annotate.Domain;
using Xunit;

namespace Hypa.Annotate.Tests;

public sealed class AnnotateLastTests : IDisposable
{
    private readonly string _root;

    public AnnotateLastTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hypa-annotate-last-" + Guid.NewGuid().ToString("N"));
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
    public void Pi_reader_returns_latest_visible_assistant_message()
    {
        var path = FixturePath("basic-transcript.jsonl");
        var result = PiTranscriptReader.ReadLastVisibleAssistantMessage(path);
        Assert.True(result.IsOk);
        Assert.Equal("latest visible reply", result.Value);
    }

    [Fact]
    public void Pi_reader_skips_empty_assistant_replies()
    {
        var path = FixturePath("empty-assistant-skipped.jsonl");
        var result = PiTranscriptReader.ReadLastVisibleAssistantMessage(path);
        Assert.True(result.IsOk);
        Assert.Equal("visible after empty", result.Value);
    }

    [Fact]
    public void Pi_reader_skips_malformed_records()
    {
        var path = FixturePath("malformed-records.jsonl");
        var result = PiTranscriptReader.ReadLastVisibleAssistantMessage(path);
        Assert.True(result.IsOk);
        Assert.Equal("survives malformed line", result.Value);
    }

    [Fact]
    public void Pi_reader_refuses_when_no_assistant_message_exists()
    {
        var path = FixturePath("no-assistant.jsonl");
        var result = PiTranscriptReader.ReadLastVisibleAssistantMessage(path);
        Assert.False(result.IsOk);
        Assert.Equal(AnnotateLastMessages.UnreadableTranscript, result.Error);
    }

    [Fact]
    public void Pi_reader_refuses_missing_transcript()
    {
        var result = PiTranscriptReader.ReadLastVisibleAssistantMessage(
            Path.Combine(_root, "missing.jsonl"));
        Assert.False(result.IsOk);
        Assert.Equal(AnnotateLastMessages.UnreadableTranscript, result.Error);
    }

    [Fact]
    public async Task Last_without_agent_session_refuses_and_sends_nothing()
    {
        var opener = new RecordingPaneOpener { Result = Result<Unit, string>.Ok(default) };
        var sender = new RecordingPaneSender { Result = Result<Unit, string>.Ok(default) };

        var result = await AnnotateLastService.RunAsync(
            new AnnotateLastRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-1",
                ContextJson = """{"workspace_id":"w1"}""",
                PaneOpener = opener,
            },
            CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal(AnnotateLastMessages.NoAgentSession, result.Error);
        Assert.Null(opener.LastRequest);
        Assert.Null(sender.LastRequest);
        Assert.Empty(Directory.GetFiles(_root, "last-pending-*.json"));
    }

    [Fact]
    public async Task Last_with_id_kind_session_refuses_and_sends_nothing()
    {
        var opener = new RecordingPaneOpener { Result = Result<Unit, string>.Ok(default) };
        var context = """
            {
              "agent_session": {
                "kind": "id",
                "value": "sess-1",
                "source": "plugin:claude",
                "agent": "claude"
              }
            }
            """;

        var result = await AnnotateLastService.RunAsync(
            new AnnotateLastRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-1",
                ContextJson = context,
                PaneOpener = opener,
            },
            CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal(AnnotateLastMessages.UnsupportedAgent, result.Error);
        Assert.Null(opener.LastRequest);
        Assert.Empty(Directory.GetFiles(_root, "last-pending-*.json"));
    }

    [Fact]
    public async Task Last_with_unreadable_transcript_writes_no_bytes_to_any_pane()
    {
        var opener = new RecordingPaneOpener { Result = Result<Unit, string>.Ok(default) };
        var sender = new RecordingPaneSender { Result = Result<Unit, string>.Ok(default) };
        var context = """
            {
              "agent_session": {
                "kind": "path",
                "value": "/tmp/missing-pi-session.jsonl",
                "source": "plugin:pi",
                "agent": "pi"
              }
            }
            """;

        var result = await AnnotateLastService.RunAsync(
            new AnnotateLastRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-1",
                ContextJson = context,
                PaneOpener = opener,
            },
            CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal(AnnotateLastMessages.UnreadableTranscript, result.Error);
        Assert.Null(opener.LastRequest);
        Assert.Null(sender.LastRequest);
        Assert.Empty(Directory.GetFiles(_root, "last-pending-*.json"));
    }

    [Fact]
    public async Task Last_with_pi_path_transcript_opens_review_popup()
    {
        var transcript = FixturePath("basic-transcript.jsonl");
        var opener = new RecordingPaneOpener { Result = Result<Unit, string>.Ok(default) };
        var context = $$"""
            {
              "agent_session": {
                "kind": "path",
                "value": {{System.Text.Json.JsonSerializer.Serialize(transcript)}},
                "source": "plugin:pi",
                "agent": "pi"
              }
            }
            """;

        var result = await AnnotateLastService.RunAsync(
            new AnnotateLastRequest
            {
                StateDirectory = _root,
                PluginRoot = _root,
                TargetPaneId = "pane-42",
                ContextJson = context,
                PaneOpener = opener,
                NowIso = () => "2026-09-11T00:00:00.000Z",
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.NotNull(opener.LastRequest);
        Assert.Equal("pane-42", opener.LastRequest.TargetPaneId);
        var pendingPath = Assert.Single(Directory.GetFiles(_root, "last-pending-*.json"));
        var pending = PendingLastReviewFiles.ReadPending(pendingPath);
        Assert.True(pending.IsOk);
        Assert.Equal("latest visible reply", pending.Value.AgentMessage);
        Assert.Equal("pane-42", pending.Value.TargetPaneId);
    }

    [Fact]
    public async Task Review_sends_bound_pane_text_without_trailing_newline()
    {
        var pendingPath = WritePending("agent text", "pane-target");
        var sender = new RecordingPaneSender { Result = Result<Unit, string>.Ok(default) };
        var result = await AnnotateLastReviewService.RunAsync(
            new AnnotateLastReviewRequest
            {
                PendingPath = pendingPath,
                ReviewSession = new FixedReviewSession(new AnnotateLastReviewSessionResult
                {
                    Outcome = AnnotateLastReviewOutcome.Sent,
                    Comment = "review note",
                }),
                PaneSender = sender,
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.False(File.Exists(pendingPath));
        Assert.NotNull(sender.LastRequest);
        Assert.Equal("pane-target", sender.LastRequest.TargetPaneId);
        Assert.Equal("review note", sender.LastRequest.Text);
        Assert.DoesNotContain('\n', sender.LastRequest.Text);
    }

    [Fact]
    public async Task Review_to_unrelated_pane_fails_closed_without_extra_send()
    {
        var pendingPath = WritePending("agent text", "pane-target");
        var sender = new RecordingPaneSender
        {
            Result = Result<Unit, string>.Fail("capability_missing"),
        };

        var result = await AnnotateLastReviewService.RunAsync(
            new AnnotateLastReviewRequest
            {
                PendingPath = pendingPath,
                ReviewSession = new FixedReviewSession(new AnnotateLastReviewSessionResult
                {
                    Outcome = AnnotateLastReviewOutcome.Sent,
                    Comment = "review note",
                }),
                PaneSender = sender,
            },
            CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal("capability_missing", result.Error);
        Assert.NotNull(sender.LastRequest);
        Assert.Equal("pane-target", sender.LastRequest.TargetPaneId);
    }

    [Fact]
    public async Task Review_cancel_writes_no_send()
    {
        var pendingPath = WritePending("agent text", "pane-target");
        var sender = new RecordingPaneSender { Result = Result<Unit, string>.Ok(default) };
        var result = await AnnotateLastReviewService.RunAsync(
            new AnnotateLastReviewRequest
            {
                PendingPath = pendingPath,
                ReviewSession = new FixedReviewSession(new AnnotateLastReviewSessionResult
                {
                    Outcome = AnnotateLastReviewOutcome.Cancelled,
                }),
                PaneSender = sender,
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Null(sender.LastRequest);
    }

    [Fact]
    public async Task Review_failed_session_returns_nonzero_without_send()
    {
        var pendingPath = WritePending("agent text", "pane-target");
        var sender = new RecordingPaneSender { Result = Result<Unit, string>.Ok(default) };
        var result = await AnnotateLastReviewService.RunAsync(
            new AnnotateLastReviewRequest
            {
                PendingPath = pendingPath,
                ReviewSession = new FixedReviewSession(new AnnotateLastReviewSessionResult
                {
                    Outcome = AnnotateLastReviewOutcome.Failed,
                }),
                PaneSender = sender,
            },
            CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(1, result.Value);
        Assert.Null(sender.LastRequest);
    }

    [SkippableFact]
    public void Missing_tty_last_review_is_failed_not_cancelled()
    {
        Skip.If(
            !Console.IsInputRedirected && !Console.IsOutputRedirected,
            "stdin is a TTY.");
        var session = new TuiLastReviewSession();
        var result = session.Run(new PendingLastReview
        {
            AgentMessage = "agent text",
            TargetPaneId = "pane-target",
            CapturedAt = "2026-09-11T00:00:00.000Z",
        });
        Assert.Equal(AnnotateLastReviewOutcome.Failed, result.Outcome);
        Assert.Null(result.Comment);
    }

    private string WritePending(string agentMessage, string targetPaneId)
    {
        var pending = new PendingLastReview
        {
            AgentMessage = agentMessage,
            TargetPaneId = targetPaneId,
            CapturedAt = "2026-09-11T00:00:00.000Z",
        };
        var written = PendingLastReviewFiles.WritePending(_root, pending);
        Assert.True(written.IsOk);
        return written.Value;
    }

    private static string FixturePath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pi", name));

    private sealed class FixedReviewSession(AnnotateLastReviewSessionResult result) : IAnnotateLastReviewSession
    {
        public AnnotateLastReviewSessionResult Run(PendingLastReview pending) => result;
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

    private sealed class RecordingPaneSender : IPluginPaneSender
    {
        public Result<Unit, string> Result { get; init; }

        public PluginPaneSendTextRequest? LastRequest { get; private set; }

        public Task<Result<Unit, string>> SendTextAsync(
            PluginPaneSendTextRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }
}
