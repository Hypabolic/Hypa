using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class OfficialAgentResumeTests
{
    [Fact]
    public void Planner_only_accepts_official_session_references()
    {
        var official = new NativeAgentSessionRef
        {
            Kind = NativeAgentSessionRef.KindId,
            Value = "sess-1",
            Source = OfficialAgentSources.Claude,
            Agent = "claude",
        };
        var plan = OfficialAgentResumePlanner.TryPlan(official, resumeEnabled: true);
        Assert.NotNull(plan);
        Assert.Equal(["claude", "--resume", "sess-1"], plan!.Argv);

        var custom = official with { Source = "pane" };
        Assert.Null(OfficialAgentResumePlanner.TryPlan(custom, resumeEnabled: true));
        Assert.Null(OfficialAgentResumePlanner.TryPlan(official, resumeEnabled: false));
    }

    [Fact]
    public void Planner_quotes_pi_path_with_semicolon()
    {
        var session = new NativeAgentSessionRef
        {
            Kind = NativeAgentSessionRef.KindPath,
            Value = "/tmp/session;name.jsonl",
            Source = OfficialAgentSources.Pi,
            Agent = "pi",
        };
        var plan = OfficialAgentResumePlanner.TryPlan(session, resumeEnabled: true);
        Assert.NotNull(plan);
        var command = OfficialAgentResumePlanner.ToShellCommand(plan!);
        Assert.Equal("pi --session '/tmp/session;name.jsonl'", command);
        Assert.DoesNotContain(" --session /tmp/session;name.jsonl", command, StringComparison.Ordinal);

        var piped = OfficialAgentResumePlanner.TryPlan(
            session with { Value = "/tmp/session|name.jsonl" }, resumeEnabled: true);
        Assert.NotNull(piped);
        Assert.Equal(
            "pi --session '/tmp/session|name.jsonl'",
            OfficialAgentResumePlanner.ToShellCommand(piped!));
    }

    [Fact]
    public void Planner_quotes_like_herdr_allowlist_and_apostrophe()
    {
        var session = new NativeAgentSessionRef
        {
            Kind = NativeAgentSessionRef.KindId,
            Value = "session with ' quote",
            Source = OfficialAgentSources.Claude,
            Agent = "claude",
        };
        var plan = OfficialAgentResumePlanner.TryPlan(session, resumeEnabled: true);
        Assert.NotNull(plan);
        Assert.Equal(
            @"claude --resume 'session with '\'' quote'",
            OfficialAgentResumePlanner.ToShellCommand(plan!));
    }

    [Fact]
    public void Planner_rejects_control_characters_in_stored_refs()
    {
        var withNewline = new NativeAgentSessionRef
        {
            Kind = NativeAgentSessionRef.KindId,
            Value = "sess\n1",
            Source = OfficialAgentSources.Claude,
            Agent = "claude",
        };
        Assert.Null(OfficialAgentResumePlanner.TryPlan(withNewline, resumeEnabled: true));

        var withTab = new NativeAgentSessionRef
        {
            Kind = NativeAgentSessionRef.KindPath,
            Value = "/tmp/session\tname.jsonl",
            Source = OfficialAgentSources.Pi,
            Agent = "pi",
        };
        Assert.Null(OfficialAgentResumePlanner.TryPlan(withTab, resumeEnabled: true));
    }

    [Fact]
    public void Planner_rejects_relative_session_path()
    {
        var relative = new NativeAgentSessionRef
        {
            Kind = NativeAgentSessionRef.KindPath,
            Value = "tmp/session.jsonl",
            Source = OfficialAgentSources.Pi,
            Agent = "pi",
        };
        Assert.False(NativeAgentSessionRef.IsValidPath(relative.Value));
        Assert.Null(OfficialAgentResumePlanner.TryPlan(relative, resumeEnabled: true));

        var absolute = relative with { Value = "/tmp/session.jsonl" };
        Assert.True(NativeAgentSessionRef.IsValidPath(absolute.Value));
        Assert.NotNull(OfficialAgentResumePlanner.TryPlan(absolute, resumeEnabled: true));
    }

    [Theory]
    [InlineData(OfficialAgentSources.Copilot, "copilot", NativeAgentSessionRef.KindId, "sess-1", "copilot --resume=sess-1")]
    [InlineData(OfficialAgentSources.Devin, "devin", NativeAgentSessionRef.KindId, "sess-1", "devin --resume sess-1")]
    [InlineData(OfficialAgentSources.Droid, "droid", NativeAgentSessionRef.KindId, "sess-1", "droid --resume sess-1")]
    [InlineData(OfficialAgentSources.Opencode, "opencode", NativeAgentSessionRef.KindId, "sess-1", "opencode --session sess-1")]
    [InlineData(OfficialAgentSources.Kilo, "kilo", NativeAgentSessionRef.KindId, "sess-1", "kilo --session sess-1")]
    [InlineData(OfficialAgentSources.Hermes, "hermes", NativeAgentSessionRef.KindId, "sess-1", "hermes --resume sess-1")]
    [InlineData(OfficialAgentSources.Qodercli, "qodercli", NativeAgentSessionRef.KindId, "sess-1", "qodercli --resume sess-1")]
    [InlineData(OfficialAgentSources.Qwen, "qwen", NativeAgentSessionRef.KindId, "sess-1", "qwen --resume sess-1")]
    [InlineData(OfficialAgentSources.Cursor, "cursor", NativeAgentSessionRef.KindId, "sess-1", "cursor-agent --resume sess-1")]
    [InlineData(OfficialAgentSources.Mastracode, "mastracode", NativeAgentSessionRef.KindId, "sess-1", "mastracode --thread sess-1")]
    [InlineData(OfficialAgentSources.AntigravityCli, "agy", NativeAgentSessionRef.KindId, "sess-1", "agy --conversation sess-1")]
    [InlineData(OfficialAgentSources.Grok, "grok", NativeAgentSessionRef.KindId, "sess-1", "grok --resume sess-1")]
    public void Planner_matches_herdr_resume_argv(
        string source,
        string agent,
        string kind,
        string value,
        string command)
    {
        var session = new NativeAgentSessionRef
        {
            Kind = kind,
            Value = value,
            Source = source,
            Agent = agent,
        };
        var plan = OfficialAgentResumePlanner.TryPlan(session, resumeEnabled: true);
        Assert.NotNull(plan);
        Assert.Equal(command, OfficialAgentResumePlanner.ToShellCommand(plan!));
    }

    [Fact]
    public async Task Report_rejects_relative_session_path()
    {
        var (_, pane, _, cp) = CreateReportPane("relative-session-path");
        try
        {
            using var report = JsonDocument.Parse(
                "{\"pane_id\":" + Quote(pane.Id.Value)
                + ",\"source\":\"hypa:pi\",\"agent\":\"pi\",\"seq\":1"
                + ",\"agent_session_path\":\"tmp/session.jsonl\"}");
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneReportAgentSession,
                    report.RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
            Assert.Contains("fully qualified", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restore_reserves_duplicate_session_before_type()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "hypa-resume-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var app = new AppState(SessionId.New("restore-dup-session"));
        app.UpdateSession(s => s with
        {
            Name = "restore-dup-session",
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var ws = app.CreateWorkspace(cwd, "ws");
        NativeAgentSessionRef Session() => new()
        {
            Kind = NativeAgentSessionRef.KindId,
            Value = "sess-dup",
            Source = OfficialAgentSources.Claude,
            Agent = "claude",
        };
        var first = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.FocusedTabId!.Value,
            WorkspaceId = ws.Id,
            Cwd = cwd,
            Cols = 80,
            Rows = 24,
            LifecycleState = PaneLifecycle.Orphaned,
            OccupantGeneration = 1,
            AgentSession = Session(),
        });
        var second = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.FocusedTabId!.Value,
            WorkspaceId = ws.Id,
            Cwd = cwd,
            Cols = 80,
            Rows = 24,
            LifecycleState = PaneLifecycle.Orphaned,
            OccupantGeneration = 1,
            AgentSession = Session(),
        });
        var capturing = TestPaneFactories.Capturing();
        var attach = AttachClientConfig.Default with
        {
            Session = AttachSessionConfig.Default with { ResumeAgentsOnRestore = true },
            Terminal = AttachTerminalConfig.Default with
            {
                DefaultShell = "/bin/sh",
                ShellMode = TerminalShellMode.NonLogin,
            },
        };
        var cp = new ControlPlaneService(
            app,
            capturing,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachConfig: attach);
        try
        {
            await cp.RestoreSpawnAsync(CancellationToken.None);
            await cp.FlushOfficialAgentResumesAsync(CancellationToken.None);
            var typed = capturing.Writes.Count(w => w.Contains("claude --resume sess-dup", StringComparison.Ordinal));
            Assert.Equal(1, typed);
            var withSession = new[] { app.GetPane(first.Id)!, app.GetPane(second.Id)! }
                .Count(p => p.AgentSession is not null);
            Assert.Equal(1, withSession);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            TryDelete(cwd);
        }
    }

    [Fact]
    public async Task Restore_does_not_type_resume_until_flush()
    {
        var (_, _, capturing, cp) = CreateRestore("restore-defer-flush", resume: true, OfficialAgentSources.Claude);
        try
        {
            await cp.RestoreSpawnAsync(CancellationToken.None);
            Assert.Equal("/bin/sh", capturing.LastOptions!.Command);
            Assert.Empty(capturing.Writes);

            await cp.FlushOfficialAgentResumesAsync(CancellationToken.None);
            Assert.Contains(capturing.Writes, w => w.Contains("claude --resume sess-1", StringComparison.Ordinal));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restore_types_official_resume_when_enabled()
    {
        var (app, pane, capturing, cp) = CreateRestore("restore-official-on", resume: true, OfficialAgentSources.Claude);
        try
        {
            await cp.RestoreSpawnAsync(CancellationToken.None);
            await cp.FlushOfficialAgentResumesAsync(CancellationToken.None);
            Assert.Equal("/bin/sh", capturing.LastOptions!.Command);
            Assert.Contains(capturing.Writes, w => w.Contains("claude --resume sess-1", StringComparison.Ordinal));
            Assert.Equal("sess-1", app.GetPane(pane.Id)!.AgentSession?.Value);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restore_leaves_shell_when_resume_disabled()
    {
        var (_, _, capturing, cp) = CreateRestore("restore-official-off", resume: false, OfficialAgentSources.Claude);
        try
        {
            await cp.RestoreSpawnAsync(CancellationToken.None);
            await cp.FlushOfficialAgentResumesAsync(CancellationToken.None);
            Assert.Equal("/bin/sh", capturing.LastOptions!.Command);
            Assert.Empty(capturing.Writes);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restore_leaves_shell_for_non_official_session()
    {
        var (_, _, capturing, cp) = CreateRestore("restore-custom-ref", resume: true, "pane");
        try
        {
            await cp.RestoreSpawnAsync(CancellationToken.None);
            await cp.FlushOfficialAgentResumesAsync(CancellationToken.None);
            Assert.Equal("/bin/sh", capturing.LastOptions!.Command);
            Assert.Empty(capturing.Writes);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Claude_hook_id_payload_stores_id_ref_that_restore_can_use()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "hypa-claude-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var app = new AppState(SessionId.New("claude-hook-id"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var ws = app.CreateWorkspace(cwd, "ws");
        var pane = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.FocusedTabId!.Value,
            WorkspaceId = ws.Id,
            Cwd = cwd,
            Cols = 80,
            Rows = 24,
            LifecycleState = PaneLifecycle.Orphaned,
            OccupantGeneration = 1,
        });
        var capturing = TestPaneFactories.Capturing();
        var attach = AttachClientConfig.Default with
        {
            Session = AttachSessionConfig.Default with { ResumeAgentsOnRestore = true },
            Terminal = AttachTerminalConfig.Default with
            {
                DefaultShell = "/bin/sh",
                ShellMode = TerminalShellMode.NonLogin,
            },
        };
        var cp = new ControlPlaneService(
            app,
            capturing,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachConfig: attach);
        try
        {
            // Hook payload when both session_id and transcript_path exist: id only.
            using var report = JsonDocument.Parse(
                "{\"pane_id\":" + Quote(pane.Id.Value)
                + ",\"source\":\"hypa:claude\",\"agent\":\"claude\",\"seq\":1"
                + ",\"agent_session_id\":\"claude-sess-abc\""
                + ",\"session_start_source\":\"startup\"}");
            var result = await cp.DispatchAsync(
                ProtocolMethods.PaneReportAgentSession,
                report.RootElement,
                CancellationToken.None);
            Assert.True(result.GetProperty("ok").GetBoolean());

            var stored = app.GetPane(pane.Id)!.AgentSession;
            Assert.NotNull(stored);
            Assert.Equal(NativeAgentSessionRef.KindId, stored!.Kind);
            Assert.Equal("claude-sess-abc", stored.Value);
            Assert.Equal(OfficialAgentSources.Claude, stored.Source);

            var plan = OfficialAgentResumePlanner.TryPlan(stored, resumeEnabled: true);
            Assert.NotNull(plan);
            Assert.Equal(["claude", "--resume", "claude-sess-abc"], plan!.Argv);

            await cp.RestoreSpawnAsync(CancellationToken.None);
            Assert.Empty(capturing.Writes);
            await cp.FlushOfficialAgentResumesAsync(CancellationToken.None);
            Assert.Contains(
                capturing.Writes,
                w => w.Contains("claude --resume claude-sess-abc", StringComparison.Ordinal));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            TryDelete(cwd);
        }
    }

    [Fact]
    public async Task Claude_hook_path_only_payload_stores_path_ref()
    {
        var (app, pane, _, cp) = CreateReportPane("claude-hook-path");
        try
        {
            using var report = JsonDocument.Parse(
                "{\"pane_id\":" + Quote(pane.Id.Value)
                + ",\"source\":\"hypa:claude\",\"agent\":\"claude\",\"seq\":1"
                + ",\"agent_session_path\":\"/tmp/claude/transcript.jsonl\"}");
            var result = await cp.DispatchAsync(
                ProtocolMethods.PaneReportAgentSession,
                report.RootElement,
                CancellationToken.None);
            Assert.True(result.GetProperty("ok").GetBoolean());

            var stored = app.GetPane(pane.Id)!.AgentSession;
            Assert.NotNull(stored);
            Assert.Equal(NativeAgentSessionRef.KindPath, stored!.Kind);
            Assert.Equal("/tmp/claude/transcript.jsonl", stored.Value);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Report_rejects_id_and_path_together()
    {
        var (_, pane, _, cp) = CreateReportPane("claude-hook-both");
        try
        {
            using var report = JsonDocument.Parse(
                "{\"pane_id\":" + Quote(pane.Id.Value)
                + ",\"source\":\"hypa:claude\",\"agent\":\"claude\",\"seq\":1"
                + ",\"agent_session_id\":\"claude-sess-abc\""
                + ",\"agent_session_path\":\"/tmp/claude/transcript.jsonl\"}");
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneReportAgentSession,
                    report.RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Report_rejects_control_characters_in_session_id()
    {
        var (_, pane, _, cp) = CreateReportPane("claude-hook-control");
        try
        {
            using var report = JsonDocument.Parse(
                "{\"pane_id\":" + Quote(pane.Id.Value)
                + ",\"source\":\"hypa:claude\",\"agent\":\"claude\",\"seq\":1"
                + ",\"agent_session_id\":\"sess\\u00011\"}");
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneReportAgentSession,
                    report.RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, ex.Code);
            Assert.Contains("control character", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Custom_source_cannot_steal_official_authority()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "hypa-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var app = new AppState(SessionId.New("authority-official"));
        app.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready });
        var ws = app.CreateWorkspace(cwd, "ws");
        var pane = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.FocusedTabId!.Value,
            WorkspaceId = ws.Id,
            Cwd = cwd,
            LifecycleState = PaneLifecycle.Running,
            OccupantGeneration = 1,
        });
        var cp = new ControlPlaneService(
            app,
            TestPaneFactories.Stub(),
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector());
        try
        {
            using var officialDoc = JsonDocument.Parse(
                "{\"pane_id\":" + Quote(pane.Id.Value)
                + ",\"source\":\"hypa:claude\",\"agent\":\"claude\",\"state\":\"working\",\"seq\":1}");
            var official = await cp.DispatchAsync(
                ProtocolMethods.PaneReportAgent,
                officialDoc.RootElement,
                CancellationToken.None);
            Assert.True(official.GetProperty("ok").GetBoolean());

            using var stealDoc = JsonDocument.Parse(
                "{\"pane_id\":" + Quote(pane.Id.Value)
                + ",\"source\":\"plugin:foo\",\"agent\":\"foo\",\"state\":\"idle\",\"seq\":1}");
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.PaneReportAgent,
                    stealDoc.RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
            Assert.Contains("source does not own pane agent authority", ex.Message, StringComparison.Ordinal);
            Assert.Equal("claude", app.GetPane(pane.Id)!.AgentKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            TryDelete(cwd);
        }
    }

    private static (AppState App, PaneState Pane, TestPaneFactories.CapturingPaneFactory Capturing, ControlPlaneService Cp)
        CreateRestore(string session, bool resume, string source) =>
        CreateRestore(
            session,
            resume,
            new NativeAgentSessionRef
            {
                Kind = NativeAgentSessionRef.KindId,
                Value = "sess-1",
                Source = source,
                Agent = "claude",
            });

    private static (AppState App, PaneState Pane, TestPaneFactories.CapturingPaneFactory Capturing, ControlPlaneService Cp)
        CreateReportPane(string session) =>
        CreateRestore(session, resume: true, sessionRef: null);

    private static (AppState App, PaneState Pane, TestPaneFactories.CapturingPaneFactory Capturing, ControlPlaneService Cp)
        CreateRestore(string session, bool resume, NativeAgentSessionRef? sessionRef)
    {
        var cwd = Path.Combine(Path.GetTempPath(), "hypa-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var app = new AppState(SessionId.New(session));
        app.UpdateSession(s => s with
        {
            Name = session,
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var ws = app.CreateWorkspace(cwd, "ws");
        var pane = app.RegisterPane(new PaneState
        {
            Id = PaneId.New(),
            TabId = ws.FocusedTabId!.Value,
            WorkspaceId = ws.Id,
            Cwd = cwd,
            Command = "claude",
            Args = ["--resume", "sess-1"],
            Cols = 80,
            Rows = 24,
            LifecycleState = PaneLifecycle.Orphaned,
            OccupantGeneration = 1,
            AgentKind = "claude",
            AgentSession = sessionRef,
        });
        var capturing = TestPaneFactories.Capturing();
        var attach = AttachClientConfig.Default with
        {
            Session = AttachSessionConfig.Default with { ResumeAgentsOnRestore = resume },
            Terminal = AttachTerminalConfig.Default with
            {
                DefaultShell = "/bin/sh",
                ShellMode = TerminalShellMode.NonLogin,
            },
        };
        var cp = new ControlPlaneService(
            app,
            capturing,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            attachConfig: attach);
        return (app, pane, capturing, cp);
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static void TryDelete(string cwd)
    {
        try
        {
            Directory.Delete(cwd, recursive: true);
        }
        catch
        {
            // teardown
        }
    }
}
