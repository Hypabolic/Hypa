using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentIntelligence.Detection;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Bundled TOML screen detection, local override, reload, and control-plane skip.
/// </summary>
public class AgentDetectionManifestTests
{
    private const string MuseWorking =
        "⟩ hello\n\n◆ Working (0s · esc to interrupt)\n\n────────────────\n⟩\n────────────────\ngpt-5.4 · minimal · /workspace";

    private const string MusePicker =
        "Which option should I use?\n\n› 1. Alpha\n  2. Beta\n\nEnter to select · ↑/↓ to move · Tab for an optional note · Esc to interrupt\n\n────────────────\n⟩\n────────────────\ngpt-5.4 · minimal · /workspace";

    private const string MuseCommandApproval =
        "Would you like to run the following command?\n\n$ printf muse-safe-probe\n\n› 1. Allow this stage once (y)\n  2. Always allow in this workspace: printf muse-safe-probe ... (p)\n  3. Abort the entire command (esc)\n────────────────\ngpt-5.4 · minimal · /workspace";

    private const string MuseNetworkApproval =
        "network: example.com:443 https\nrequested by:\n$ curl -fsS https://example.com\n\n› 1. Yes, proceed (y)\n  2. Yes, don't ask again this session (p)  example.com:443 (https)\n  3. No, and tell Muse Code what to do differently (esc)\n────────────────\ngpt-5.4 · minimal · /workspace";

    private const string MuseMenuOverlay =
        "Theme\n\n⟩ Default (active)\n  Dynamic\n\n↑↓ move · enter save · esc go back";

    private const string MuseOrdinaryReply =
        "⟩ say the phrase\n\n◆ Yes, proceed\n\n────────────────\n⟩\n────────────────\ngpt-5.4 · minimal · /workspace";

    [Fact]
    public void Bundled_screen_manifests_load_for_all_known_ids()
    {
        var detector = new HeuristicAgentDetector();
        var summaries = detector.ListSummaries();
        Assert.Equal(21, summaries.Count);
        Assert.All(summaries, s =>
        {
            Assert.Equal("bundled", s.SourceKind);
            Assert.False(string.IsNullOrWhiteSpace(s.ActiveVersion));
            Assert.Null(s.Warning);
        });
    }

    [Fact]
    public void Muse_trust_picker_and_approval_need_paired_controls()
    {
        var detector = new HeuristicAgentDetector();

        var working = detector.Detect(MuseWorking, "muse");
        Assert.Equal(AgentStatus.Working, working.Status);
        Assert.Equal("muse", working.AgentKind);
        Assert.Equal("working_esc_interrupt", working.MatchedRuleId);
        Assert.Equal("bundled", working.ManifestSourceKind);
        Assert.False(string.IsNullOrWhiteSpace(working.ManifestVersion));

        var picker = detector.Detect(MusePicker, "muse");
        Assert.Equal(AgentStatus.Blocked, picker.Status);
        Assert.Equal("pick_request_blocked", picker.MatchedRuleId);

        var command = detector.Detect(MuseCommandApproval, "muse");
        Assert.Equal(AgentStatus.Blocked, command.Status);
        Assert.Equal("blocked_approval", command.MatchedRuleId);

        var network = detector.Detect(MuseNetworkApproval, "muse");
        Assert.Equal(AgentStatus.Blocked, network.Status);
        Assert.Equal("blocked_approval", network.MatchedRuleId);
    }

    [Fact]
    public void Muse_isolated_control_phrase_is_not_blocked()
    {
        var detector = new HeuristicAgentDetector();
        var ordinary = detector.Detect(MuseOrdinaryReply, "muse");
        Assert.Equal(AgentStatus.Idle, ordinary.Status);
        Assert.NotEqual("blocked_approval", ordinary.MatchedRuleId);
        Assert.False(ordinary.SkipStateUpdate);
    }

    [Fact]
    public void Isolated_grok_phrase_does_not_classify_a_shell_process()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("Talked to Grok about the patch\n$ ", "zsh");
        Assert.Null(result.AgentKind);
        Assert.Equal(AgentStatus.Unknown, result.Status);
    }

    [Fact]
    public void Grok_osc_title_idle_matches_process_kind()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("$ ", "grok", oscTitle: "grok", oscProgress: "");
        Assert.Equal("grok", result.AgentKind);
        Assert.Equal(AgentStatus.Idle, result.Status);
        Assert.Equal("osc_title_idle", result.MatchedRuleId);
    }

    [Fact]
    public void Claude_osc_title_braille_prefix_is_working()
    {
        var detector = new HeuristicAgentDetector();
        var title = char.ConvertFromUtf32(0x280B) + " session";
        var result = detector.Detect("$ ", "claude", oscTitle: title, oscProgress: "");
        Assert.Equal("claude", result.AgentKind);
        Assert.Equal(AgentStatus.Working, result.Status);
        Assert.Equal("osc_title_working", result.MatchedRuleId);
    }

    [Fact]
    public void Muse_trust_pair_after_done_stays_blocked()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect(
            "Done.\nDo you trust this workspace?\nTrust and continue",
            "muse");
        Assert.Equal(AgentStatus.Blocked, result.Status);
        Assert.Equal("workspace_trust_blocked", result.MatchedRuleId);
        Assert.Equal("bundled", result.ManifestSourceKind);
        Assert.False(string.IsNullOrWhiteSpace(result.ManifestVersion));
        Assert.NotEqual(AgentStatus.Done, result.Status);
    }

    [Fact]
    public void Muse_isolated_press_enter_is_idle_fallback()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("Press Enter to continue", "muse");
        Assert.Equal(AgentStatus.Idle, result.Status);
        Assert.Equal("default_known_agent_idle_fallback", result.FallbackReason);
        Assert.Null(result.MatchedRuleId);
        Assert.NotEqual(AgentStatus.Blocked, result.Status);
    }

    [Fact]
    public void Muse_menu_overlay_sets_skip_state_update()
    {
        var detector = new HeuristicAgentDetector();
        var menu = detector.Detect(MuseMenuOverlay, "muse");
        Assert.True(menu.SkipStateUpdate);
        Assert.Equal(AgentStatus.Unknown, menu.Status);
        Assert.Equal("menu_overlay", menu.MatchedRuleId);
        Assert.Equal("muse", menu.AgentKind);
    }

    [Fact]
    public void Claude_working_from_interrupt_hint()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("⏸ Thinking… esc to interrupt", "claude");
        Assert.Equal(AgentStatus.Working, result.Status);
        Assert.Equal("claude", result.AgentKind);
        Assert.Equal("bundled", result.ManifestSourceKind);
        Assert.False(string.IsNullOrWhiteSpace(result.ManifestVersion));
    }

    [Fact]
    public void Codex_working_from_interrupt_hint()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("• Working (0s • esc to interrupt)", "codex");
        Assert.Equal(AgentStatus.Working, result.Status);
        Assert.Equal("codex", result.AgentKind);
        Assert.Equal("screen_working_fallback", result.MatchedRuleId);
        Assert.Equal("bundled", result.ManifestSourceKind);
    }

    [Fact]
    public void OpenCode_permission_is_blocked()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("△ Permission required", "opencode");
        Assert.Equal(AgentStatus.Blocked, result.Status);
        Assert.Equal("opencode", result.AgentKind);
        Assert.Equal("permission_required", result.MatchedRuleId);
    }

    [Fact]
    public void Copilot_paired_esc_and_enter_is_blocked()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("esc to cancel\nenter to select", "copilot");
        Assert.Equal(AgentStatus.Blocked, result.Status);
        Assert.Equal("copilot", result.AgentKind);
        Assert.Equal("selection_blocker", result.MatchedRuleId);
    }

    [Fact]
    public void Copilot_identifies_github_copilot_process_name()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("esc to cancel", "github-copilot");
        Assert.Equal("copilot", result.AgentKind);
        Assert.Equal(AgentStatus.Working, result.Status);
    }

    [Fact]
    public void Recorded_done_still_wins_over_manifest_idle()
    {
        var detector = new HeuristicAgentDetector();
        var result = detector.Detect("Churned for 2s\nDone.\n› next", "codex");
        Assert.Equal(AgentStatus.Done, result.Status);
        Assert.Equal("codex", result.AgentKind);
    }

    [Fact]
    public void Manifest_check_defaults_enabled()
    {
        Assert.True(AttachUpdateConfig.Default.ManifestCheck);
        Assert.True(AttachClientConfig.Default.Update.ManifestCheck);
        var empty = TomlAttachConfigBinder.Bind("");
        Assert.True(empty.IsOk, empty.IsOk ? "" : empty.Error.ToString());
        Assert.True(empty.Value.Update.ManifestCheck);
        var disabled = TomlAttachConfigBinder.Bind("update.manifest_check = false");
        Assert.True(disabled.IsOk, disabled.IsOk ? "" : disabled.Error.ToString());
        Assert.False(disabled.Value.Update.ManifestCheck);
    }

    [Fact]
    public void Local_override_wins()
    {
        var (env, files, _) = ManifestDirs("local-wins");
        files.WriteAllText(
            OverridePath(env, "codex"),
            """
            id = "codex"

            [[rules]]
            id = "test"
            state = "idle"
            contains = ["local-ready"]
            """);
        var detector = new HeuristicAgentDetector(env, files);
        var result = detector.Detect("local-ready", "codex");
        Assert.Equal(AgentStatus.Idle, result.Status);
        Assert.Equal("local override", result.ManifestSourceKind);
        Assert.Equal("test", result.MatchedRuleId);
        var row = detector.ListSummaries().Single(s => s.Agent == "codex");
        Assert.Equal("local override", row.SourceKind);
    }

    [Fact]
    public void Local_override_blocked_done_phrase_wins()
    {
        var (env, files, _) = ManifestDirs("local-done-blocked");
        files.WriteAllText(
            OverridePath(env, "muse"),
            """
            id = "muse"

            [[rules]]
            id = "done_blocked"
            state = "blocked"
            contains = ["Done."]
            """);
        var detector = new HeuristicAgentDetector(env, files);
        var result = detector.Detect("Done.", "muse");
        Assert.Equal(AgentStatus.Blocked, result.Status);
        Assert.Equal("local override", result.ManifestSourceKind);
        Assert.Equal("done_blocked", result.MatchedRuleId);
        Assert.NotEqual(AgentStatus.Done, result.Status);
    }

    [Fact]
    public void Local_override_supplementary_unicode_escape_matches()
    {
        var translated = ManifestRegex.TranslateRustHexEscapes(@"\u{1F600}");
        Assert.DoesNotContain("\\U", translated, StringComparison.Ordinal);
        Assert.Equal("\\ud83d\\ude00", translated);

        var (env, files, _) = ManifestDirs("local-supplementary");
        files.WriteAllText(
            OverridePath(env, "codex"),
            """
            id = "codex"

            [[rules]]
            id = "grinning_face"
            state = "blocked"
            regex = ['\u{1F600}']
            """);
        var detector = new HeuristicAgentDetector(env, files);
        var result = detector.Detect(char.ConvertFromUtf32(0x1F600), "codex");
        Assert.Null(result.Warning);
        Assert.Equal(AgentStatus.Blocked, result.Status);
        Assert.Equal("local override", result.ManifestSourceKind);
        Assert.Equal("grinning_face", result.MatchedRuleId);
    }

    [Fact]
    public void Invalid_override_warns_and_falls_back()
    {
        var (env, files, _) = ManifestDirs("invalid-override");
        files.WriteAllText(
            RemotePath(env, "codex"),
            """
            id = "codex"
            version = "9999.01.01.1"
            min_engine_version = 1
            updated_at = "2026-06-10T12:00:00Z"

            [[rules]]
            id = "test"
            state = "blocked"
            contains = ["remote-ready"]
            """);
        files.WriteAllText(OverridePath(env, "codex"), "id = ");
        var detector = new HeuristicAgentDetector(env, files);
        var result = detector.Detect("remote-ready", "codex");
        Assert.Equal(AgentStatus.Blocked, result.Status);
        Assert.Equal("remote", result.ManifestSourceKind);
        Assert.False(string.IsNullOrWhiteSpace(result.Warning));
        Assert.Contains("ignored override", result.Warning, StringComparison.Ordinal);
        var row = detector.ListSummaries().Single(s => s.Agent == "codex");
        Assert.Equal("remote", row.SourceKind);
        Assert.False(string.IsNullOrWhiteSpace(row.Warning));
    }

    [Fact]
    public void Reload_applies_local_edits_without_new_detector()
    {
        var (env, files, _) = ManifestDirs("reload-cache");
        files.WriteAllText(
            RemotePath(env, "codex"),
            """
            id = "codex"
            version = "9999.01.01.1"
            min_engine_version = 1
            updated_at = "2026-06-10T12:00:00Z"

            [[rules]]
            id = "test"
            state = "blocked"
            contains = ["cached-ready"]
            """);
        var detector = new HeuristicAgentDetector(env, files);
        var cached = detector.Detect("cached-ready", "codex");
        Assert.Equal(AgentStatus.Blocked, cached.Status);
        Assert.Equal("test", cached.MatchedRuleId);

        files.WriteAllText(
            RemotePath(env, "codex"),
            """
            id = "codex"
            version = "9999.01.01.2"
            min_engine_version = 1
            updated_at = "2026-06-10T12:00:00Z"

            [[rules]]
            id = "test"
            state = "working"
            contains = ["new-ready"]
            """);

        var unchanged = detector.Detect("new-ready", "codex");
        Assert.Equal(AgentStatus.Idle, unchanged.Status);
        Assert.Equal("default_known_agent_idle_fallback", unchanged.FallbackReason);

        detector.Reload();
        var reloaded = detector.Detect("new-ready", "codex");
        Assert.Equal(AgentStatus.Working, reloaded.Status);
        Assert.Equal("test", reloaded.MatchedRuleId);
        Assert.Equal("9999.01.01.2", reloaded.ManifestVersion);
    }

    [Fact]
    public async Task Overlay_scan_keeps_previous_status()
    {
        var state = new AppState(SessionId.New("manifest-overlay"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(state, factory, intel, new HeuristicAgentDetector());
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "muse",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            var paneId = runtime.Id.Value;
            runtime.FireOutput(MuseWorking);
            cp.ScanDetectionNowForTests(paneId);
            var working = state.GetPane(runtime.Id);
            Assert.NotNull(working);
            Assert.Equal(AgentStatus.Working, working.AgentStatus);
            Assert.Equal("muse", working.AgentKind);

            runtime.FireOutput(MuseMenuOverlay);
            cp.ScanDetectionNowForTests(paneId);
            var overlay = state.GetPane(runtime.Id);
            Assert.NotNull(overlay);
            Assert.Equal(AgentStatus.Working, overlay.AgentStatus);
            Assert.Equal("muse", overlay.AgentKind);
            Assert.True(runtime.IsAlive);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Server_agent_manifests_reports_bundled_sources()
    {
        var state = new AppState(SessionId.New("manifest-list"));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready, Placement = "local" });
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(
            state,
            TestPaneFactories.Stub(),
            intel,
            new HeuristicAgentDetector());
        try
        {
            var listed = await cp.DispatchAsync(
                ProtocolMethods.ServerAgentManifests, parameters: null, CancellationToken.None);
            Assert.Equal(21, listed.GetProperty("manifests").GetArrayLength());
            var claude = listed.GetProperty("manifests").EnumerateArray()
                .Single(e => e.GetProperty("agent").GetString() == "claude");
            Assert.Equal("bundled", claude.GetProperty("source_kind").GetString());
            Assert.Equal("bundled", claude.GetProperty("source").GetString());
            Assert.False(claude.GetProperty("local_override_shadowing_remote").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Reload_agent_manifests_does_not_restart_panes()
    {
        var state = new AppState(SessionId.New("manifest-reload"));
        state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.Ready, Placement = "local" });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(state, factory, intel, new HeuristicAgentDetector());
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "muse",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            Assert.True(runtime.IsAlive);
            var reloaded = await cp.DispatchAsync(
                ProtocolMethods.ServerReloadAgentManifests, parameters: null, CancellationToken.None);
            Assert.Equal(21, reloaded.GetProperty("manifests").GetArrayLength());
            Assert.True(runtime.IsAlive);
            Assert.Same(runtime, factory.Created[0]);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Protocol_fixtures_round_trip()
    {
        foreach (var method in ProtocolMethods.AgentManifests)
        {
            var reqJson = FixtureCatalog.Load(FixtureCatalog.MethodRequestPath(method));
            var req = JsonSerializer.Deserialize(reqJson, ProtocolJsonContext.Default.RpcRequest);
            Assert.NotNull(req);
            Assert.Equal(method, req.Method);
            Assert.True(
                ProtocolEnvelopeValidator.TryValidateMethodParams(method, req.Params, out var err),
                err);
            var resJson = FixtureCatalog.Load(FixtureCatalog.MethodResponsePath(method));
            var res = JsonSerializer.Deserialize(resJson, ProtocolJsonContext.Default.RpcResponse);
            Assert.NotNull(res);
            Assert.True(res.Result.HasValue);
        }
    }

    [Fact]
    public async Task Foreground_process_kind_wins_over_spawn_shell_command()
    {
        var state = new AppState(SessionId.New("foreground-kind"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new FixedForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            var pane = state.GetPane(runtime.Id);
            Assert.NotNull(pane);
            Assert.Equal("claude", pane.AgentKind);
            Assert.Equal(AgentStatus.Working, pane.AgentStatus);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Confirmed_foreground_miss_clears_kind()
    {
        var state = new AppState(SessionId.New("foreground-miss"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);

            probe.Command = "vim";
            for (var i = 1; i < AgentDetectionPresence.MissConfirmationAttempts; i++)
            {
                probe.GroupId++;
                cp.ScanDetectionNowForTests(runtime.Id.Value);
                Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);
            }

            probe.GroupId++;
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Null(state.GetPane(runtime.Id)!.AgentKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task First_agent_acquisition_keeps_startup_osc()
    {
        var state = new AppState(SessionId.New("osc-keep"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new FixedForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.DetectionOscTitle = "startup title";
            runtime.DetectionOscProgress = "4;1;";
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);

            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);
            Assert.Equal(0, runtime.OscClears);
            Assert.Equal("startup title", runtime.DetectionOscTitle);
            Assert.Equal("4;1;", runtime.DetectionOscProgress);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Replacement_agent_clears_startup_osc()
    {
        var state = new AppState(SessionId.New("osc-replace"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.DetectionOscTitle = "claude title";
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Equal(0, runtime.OscClears);

            probe.Command = "grok";
            runtime.DetectionOscTitle = "grok title";
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.True(runtime.OscClears >= 1);
            Assert.Equal("", runtime.DetectionOscTitle);
            Assert.Equal("grok", state.GetPane(runtime.Id)!.AgentKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Seeded_kind_clears_when_foreground_is_unidentified()
    {
        var state = new AppState(SessionId.New("seeded-kind"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var cp = new ControlPlaneService(state, factory, intel, new HeuristicAgentDetector());
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            var paneId = runtime.Id;
            state.UpdatePane(paneId, p => p with { AgentKind = "grok" });
            runtime.FireOutput("$ ");
            cp.ScanDetectionNowForTests(paneId.Value);
            Assert.Null(state.GetPane(paneId)!.AgentKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Process_identified_idle_publishes_without_screen_text()
    {
        var state = new AppState(SessionId.New("empty-idle"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new FixedForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            var pane = state.GetPane(runtime.Id);
            Assert.NotNull(pane);
            Assert.Equal("claude", pane.AgentKind);
            Assert.Equal(AgentStatus.Idle, pane.AgentStatus);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Process_only_kind_empty_screen_is_idle()
    {
        var state = new AppState(SessionId.New("omp-idle"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new FixedForegroundProbe("omp");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            var pane = state.GetPane(runtime.Id);
            Assert.NotNull(pane);
            Assert.Equal("omp", pane.AgentKind);
            Assert.Equal(AgentStatus.Idle, pane.AgentStatus);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Observed_empty_foreground_does_not_use_spawn_command()
    {
        var state = new AppState(SessionId.New("empty-fg"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("zsh")
        {
            Command = "",
            ObserveEmptyCommand = true,
        };
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "grok",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("$ ");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Null(state.GetPane(runtime.Id)!.AgentKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pane_shell_return_reports_idle_then_clears_kind()
    {
        var state = new AppState(SessionId.New("pane-shell-clear"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);
            Assert.True(cp.HasOccupancyRecheckForTests(runtime.Id.Value));

            probe.Command = "zsh";
            probe.ForegroundIsPaneShell = true;
            cp.ScanOccupancyRecheckNowForTests(runtime.Id.Value);
            var idle = state.GetPane(runtime.Id)!;
            Assert.Equal("claude", idle.AgentKind);
            Assert.Equal(AgentStatus.Idle, idle.AgentStatus);

            cp.ScanOccupancyRecheckNowForTests(runtime.Id.Value);
            Assert.Null(state.GetPane(runtime.Id)!.AgentKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Pane_shell_clear_fires_from_scanner_without_new_bytes()
    {
        var state = new AppState(SessionId.New("scanner-shell-clear"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("claude");
        var time = new ManualTimeProvider();
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            timeProvider: time,
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);

            probe.Command = "zsh";
            probe.ForegroundIsPaneShell = true;
            time.Advance(AgentDetectionPresence.ProcessRecheckIdentified);
            var idle = state.GetPane(runtime.Id)!;
            Assert.Equal("claude", idle.AgentKind);
            Assert.Equal(AgentStatus.Idle, idle.AgentStatus);

            time.Advance(PaneDetectionScanner.DetectionTick);
            Assert.Null(state.GetPane(runtime.Id)!.AgentKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Null_probe_after_live_group_does_not_revive_spawn_command()
    {
        var state = new AppState(SessionId.New("null-after-live"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "grok",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);

            probe.Command = null;
            cp.ScanOccupancyRecheckNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);

            var explained = await cp.DispatchAsync(
                ProtocolMethods.AgentExplain,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = runtime.Id.Value,
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            Assert.Equal("claude", explained.GetProperty("agent").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Same_agent_after_pane_shell_clears_osc()
    {
        var state = new AppState(SessionId.New("replace-same-agent"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.DetectionOscTitle = "claude title";
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Equal(0, runtime.OscClears);

            probe.Command = "zsh";
            probe.ForegroundIsPaneShell = true;
            cp.ScanOccupancyRecheckNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);
            Assert.Equal(AgentStatus.Idle, state.GetPane(runtime.Id)!.AgentStatus);

            probe.Command = "claude";
            probe.ForegroundIsPaneShell = false;
            runtime.DetectionOscTitle = "second claude";
            cp.ScanOccupancyRecheckNowForTests(runtime.Id.Value);
            Assert.True(runtime.OscClears >= 1);
            Assert.Equal("", runtime.DetectionOscTitle);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Explain_after_clear_does_not_revive_spawn_command()
    {
        var state = new AppState(SessionId.New("explain-no-spawn"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "grok",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);

            probe.Command = "zsh";
            probe.ForegroundIsPaneShell = true;
            cp.ScanOccupancyRecheckNowForTests(runtime.Id.Value);
            cp.ScanOccupancyRecheckNowForTests(runtime.Id.Value);
            Assert.Null(state.GetPane(runtime.Id)!.AgentKind);

            probe.Command = null;
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.AgentExplain,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["pane_id"] = runtime.Id.Value,
                    }.ToJsonString()).RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
            Assert.Contains("does not have a detected agent label", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Explain_reads_live_process_after_occupancy_clear()
    {
        var state = new AppState(SessionId.New("explain-live-after-clear"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "grok",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);

            probe.Command = "zsh";
            probe.ForegroundIsPaneShell = true;
            cp.ScanOccupancyRecheckNowForTests(runtime.Id.Value);
            cp.ScanOccupancyRecheckNowForTests(runtime.Id.Value);
            Assert.Null(state.GetPane(runtime.Id)!.AgentKind);

            probe.ForegroundIsPaneShell = false;
            probe.Command = "codex";
            var explained = await cp.DispatchAsync(
                ProtocolMethods.AgentExplain,
                JsonDocument.Parse(new JsonObject
                {
                    ["pane_id"] = runtime.Id.Value,
                }.ToJsonString()).RootElement,
                CancellationToken.None);
            Assert.Equal("codex", explained.GetProperty("agent").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Same_group_skips_argv_probe_until_identified_recheck()
    {
        var state = new AppState(SessionId.New("elapsed-skip"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);

            probe.SetCommandKeepingGroup("vim");
            for (var i = 0; i < AgentDetectionPresence.MissConfirmationAttempts; i++)
                cp.ScanOccupancyRecheckNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Explain_after_occupant_bump_does_not_keep_previous_presence()
    {
        var state = new AppState(SessionId.New("explain-bump"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        var factory = TestPaneFactories.Scripted();
        var intel = new PaneIntelligencePipeline();
        var probe = new MutableForegroundProbe("claude");
        var cp = new ControlPlaneService(
            state,
            factory,
            intel,
            new HeuristicAgentDetector(),
            processInfoProbe: probe);
        try
        {
            await cp.DispatchAsync(
                "workspace.create",
                JsonDocument.Parse(new JsonObject
                {
                    ["cwd"] = Path.GetTempPath(),
                    ["command"] = "zsh",
                    ["create_pane"] = true,
                }.ToJsonString()).RootElement,
                CancellationToken.None);

            var runtime = Assert.Single(factory.Created);
            runtime.FireOutput("⏸ Thinking… esc to interrupt");
            cp.ScanDetectionNowForTests(runtime.Id.Value);
            Assert.Equal("claude", state.GetPane(runtime.Id)!.AgentKind);

            await cp.BumpOccupantGenerationAsync(runtime.Id.Value, CancellationToken.None);
            probe.Command = "zsh";
            var ex = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                cp.DispatchAsync(
                    ProtocolMethods.AgentExplain,
                    JsonDocument.Parse(new JsonObject
                    {
                        ["pane_id"] = runtime.Id.Value,
                    }.ToJsonString()).RootElement,
                    CancellationToken.None));
            Assert.Equal(ProtocolErrorCodes.InvalidState, ex.Code);
            Assert.Contains("does not have a detected agent label", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Remote_catalog_fetch_updates_cached_manifest()
    {
        var (env, files, _) = ManifestDirs("remote-catalog");
        env.Set(AgentDetectionPaths.CatalogUrlVariable, "https://example.test/catalog.toml");
        var fetcher = new MapFetcher();
        fetcher.Responses["https://example.test/catalog.toml"] = new AgentManifestFetchResult(
            true,
            """
            schema_version = 1

            [[agents]]
            id = "codex"
            path = "codex.toml"
            """,
            null);
        fetcher.Responses["https://example.test/codex.toml"] = new AgentManifestFetchResult(
            true,
            """
            id = "codex"
            version = "9999.01.01.1"
            min_engine_version = 1
            updated_at = "2026-06-10T12:00:00Z"

            [[rules]]
            id = "remote-ready"
            state = "blocked"
            contains = ["remote-ready"]
            """,
            null);
        var detector = new HeuristicAgentDetector(env, files, fetcher);
        detector.CheckRemoteUpdates(true);
        var result = detector.Detect("remote-ready", "codex");
        Assert.Equal(AgentStatus.Blocked, result.Status);
        Assert.Equal("remote", result.ManifestSourceKind);
        Assert.Equal("remote-ready", result.MatchedRuleId);
        Assert.Equal("checked", detector.LastResult);
        Assert.NotNull(detector.LastCheckUnix);
    }

    private sealed class FixedForegroundProbe(string command) : IPaneProcessInfoProbe
    {
        public bool ForegroundIsPaneShell { get; init; }

        public int? TryGetForegroundGroup(int shellPid) => 99;

        public PaneForegroundInfo? TryGetForegroundInfo(int shellPid) =>
            new()
            {
                GroupId = 99,
                Pid = 99,
                Command = command,
                ForegroundIsPaneShell = ForegroundIsPaneShell,
            };
    }

    private sealed class MutableForegroundProbe : IPaneProcessInfoProbe
    {
        private string? _command;

        public MutableForegroundProbe(string command) => _command = command;

        public string? Command
        {
            get => _command;
            set
            {
                _command = value;
                GroupId++;
            }
        }

        public int GroupId { get; set; } = 99;

        public bool ObserveEmptyCommand { get; set; }

        public bool ForegroundIsPaneShell { get; set; }

        public void SetCommandKeepingGroup(string? command) => _command = command;

        public int? TryGetForegroundGroup(int shellPid)
        {
            if (string.IsNullOrWhiteSpace(Command) && !ObserveEmptyCommand)
                return null;
            return GroupId;
        }

        public PaneForegroundInfo? TryGetForegroundInfo(int shellPid)
        {
            if (string.IsNullOrWhiteSpace(Command) && !ObserveEmptyCommand)
                return null;
            return new PaneForegroundInfo
            {
                GroupId = GroupId,
                Pid = GroupId,
                Command = Command ?? "",
                ForegroundIsPaneShell = ForegroundIsPaneShell,
            };
        }
    }

    private sealed class MapFetcher : IAgentManifestTextFetcher
    {
        public Dictionary<string, AgentManifestFetchResult> Responses { get; } = new(StringComparer.Ordinal);

        public AgentManifestFetchResult Fetch(string url, int maxBytes) =>
            Responses.TryGetValue(url, out var result)
                ? result
                : new AgentManifestFetchResult(false, "", "missing " + url);
    }

    private static (MapEnv Env, MapFiles Files, string Home) ManifestDirs(string name)
    {
        var home = Path.Combine(Path.GetTempPath(), "hypa-manifest-" + name + "-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(home, "config");
        var state = Path.Combine(home, "state");
        var env = new MapEnv(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AgentDetectionPaths.XdgConfigHomeVariable] = config,
                [AgentDetectionPaths.XdgStateHomeVariable] = state,
            },
            home);
        return (env, new MapFiles(), home);
    }

    private static string OverridePath(MapEnv env, string agentId) =>
        new AgentDetectionPaths(env).OverridePath(agentId);

    private static string RemotePath(MapEnv env, string agentId) =>
        new AgentDetectionPaths(env).RemotePath(agentId);

    private sealed class MapEnv(
        Dictionary<string, string> vars,
        string home) : IAttachConfigEnvironment
    {
        public string? GetVariable(string name) =>
            vars.TryGetValue(name, out var value) ? value : null;

        public void Set(string name, string value) => vars[name] = value;

        public string UserHome { get; } = home;
        public string? AppData => null;
        public bool IsWindows => false;
        public bool IsMacOs => true;
    }

    private sealed class MapFiles : IAttachConfigFiles
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public bool FileExists(string path) => _files.ContainsKey(path);

        public string ReadAllText(string path) => _files[path];

        public void WriteAllText(string path, string contents) => _files[path] = contents;

        public void CopyFile(string source, string destination) => _files[destination] = _files[source];
    }
}
