using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.AgentRuntime.Infrastructure.Logging;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Cli.Attach.Settings;
using Hypa.Cli.Doctor;
using Hypa.Cli.Mux;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.ControlPlane;
using Hypa.ControlPlane.Unix;
using Hypa.Placement.Application;
using Hypa.Runtime.Application.Ports;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class SettingsApplyAndAttachLogTests
{
    [Fact]
    public async Task Settings_apply_pushes_one_reload_and_sets_suppress_flag()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var engine = new KeyEngine(table, settingsPages: SettingsPageRegistry.Product());
        var loader = new StubLoader();
        var live = new AttachLiveState
        {
            Engine = engine,
            Table = table,
            Dispatcher = null!,
            ConfigLoader = loader,
            SessionName = "sess",
            AttachClientId = "c1",
            ProcessLog = new CapturingProcessLogSink(ProcessLogLevel.Debug),
        };
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Engine.Settings.OpenAt(SettingsPageRegistry.ThemeId);
        Assert.True(live.Engine.Settings.SelectItem(1));
        Assert.True(live.Engine.Settings.Apply());
        var expectedTheme = live.Engine.Settings.PendingPatch![0].TomlLiteral.Trim('"');

        var port = new RecordingPort();
        var ok = await AttachSession.ApplySettingsPatchAsync(live, port, CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(new[] { ProtocolMethods.ServerReloadConfig }, port.Methods);
        Assert.True(live.SuppressNextConfigReloadedLoad);
        Assert.Equal(expectedTheme, loader.Config.Theme.Name);
        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Contains(sink.Records, r => r.Event == ProcessLogEvents.SettingsRequested
            && r.SettingKey == SettingsValuePolicy.ThemeNameKey);
        Assert.Contains(sink.Records, r => r.Event == ProcessLogEvents.SettingsOutcome
            && r.Outcome == ProcessLogEvents.OutcomeApplied
            && r.SettingKey == SettingsValuePolicy.ThemeNameKey);
        Assert.Equal(expectedTheme, loader.Load().Value.Theme.Name);
    }

    [Fact]
    public async Task Failed_reload_does_not_set_applied_or_suppress()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var engine = new KeyEngine(table, settingsPages: SettingsPageRegistry.Product());
        var live = new AttachLiveState
        {
            Engine = engine,
            Table = table,
            Dispatcher = null!,
            ConfigLoader = new StubLoader(),
            ProcessLog = new CapturingProcessLogSink(ProcessLogLevel.Debug),
        };
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Engine.Settings.OpenAt(SettingsPageRegistry.ThemeId);
        live.Engine.Settings.SelectItem(1);
        live.Engine.Settings.Apply();
        var port = new RecordingPort { Status = "failed" };
        var ok = await AttachSession.ApplySettingsPatchAsync(live, port, CancellationToken.None);
        Assert.False(ok);
        Assert.False(live.SuppressNextConfigReloadedLoad);
        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.DoesNotContain(sink.Records, r => r.Outcome == ProcessLogEvents.OutcomeApplied);
    }

    [Fact]
    public void Theme_name_is_cleartext_paths_are_digested()
    {
        var clear = SettingsValuePolicy.Project(SettingsValuePolicy.ThemeNameKey, "catppuccin");
        Assert.Equal("catppuccin", clear.Value);
        Assert.Null(clear.Digest);
        var path = SettingsValuePolicy.Project("plugin.token", "/secret/path");
        Assert.Null(path.Value);
        Assert.False(string.IsNullOrWhiteSpace(path.Digest));
        Assert.StartsWith("sha256:", path.Digest, StringComparison.Ordinal);
    }

    [Fact]
    public void Connect_then_detach_is_ordered_with_disconnect_reason()
    {
        var sink = new CapturingProcessLogSink();
        AttachProcessLog.Connect(sink, "sess");
        AttachProcessLog.Snapshot(sink, "sess");
        AttachProcessLog.Lease(sink, "sess", null);
        AttachProcessLog.Subscribe(sink, "sess", "c1", "sub_1");
        AttachProcessLog.Detach(sink, "sess", "c1");
        AttachProcessLog.Disconnect(sink, "sess", "c1", "detach");
        Assert.Equal(
            new[]
            {
                ProcessLogEvents.AttachConnect,
                ProcessLogEvents.AttachSnapshot,
                ProcessLogEvents.AttachLease,
                ProcessLogEvents.AttachSubscribe,
                ProcessLogEvents.AttachDetach,
                ProcessLogEvents.AttachDisconnect,
            },
            sink.Records.Select(r => r.Event).ToArray());
        Assert.Equal("c1", sink.Records.Last().AttachClientId);
        Assert.Equal("detach", sink.Records.Last().DisconnectReason);
        Assert.All(
            sink.Records.Skip(3),
            r => Assert.Equal("c1", r.AttachClientId));
    }

    [Fact]
    public void Failed_no_pane_startup_writes_snapshot_error_then_error_disconnect()
    {
        var sink = new CapturingProcessLogSink();
        AttachProcessLog.Connect(sink, "sess");
        AttachSession.RecordStartupFailure(sink, "snapshot", "sess", null, "no live pane");
        AttachSession.RecordSessionEnd(sink, "sess", null, "no-live-pane", failed: true);

        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachSnapshot
                && r.Outcome == ProcessLogEvents.OutcomeError
                && r.Action == "snapshot"
                && r.Err == "no live pane");
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachDetach
                && r.Outcome == ProcessLogEvents.OutcomeError);
        var disconnect = Assert.Single(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachDisconnect);
        Assert.Equal(ProcessLogEvents.OutcomeError, disconnect.Outcome);
        Assert.Equal("no-live-pane", disconnect.DisconnectReason);
        Assert.DoesNotContain(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachDisconnect
                && r.Outcome == ProcessLogEvents.OutcomeCompleted);

        var line = System.Text.Encoding.UTF8.GetString(
            ProcessLogJsonWriter.WriteLine(sink.Records.First(r => r.Outcome == ProcessLogEvents.OutcomeError)));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(ProcessLogEvents.AttachSnapshot, doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("snapshot", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("no live pane", doc.RootElement.GetProperty("err").GetString());
    }

    [Fact]
    public void Failed_connect_stage_writes_connect_error_with_redacted_err()
    {
        var sink = new CapturingProcessLogSink();
        var secret = "sk-abcdefghijklmnopqrstuvwxyz0123";
        AttachSession.RecordStartupFailure(sink, "connect", "sess", null, "boom " + secret);
        AttachSession.RecordSessionEnd(sink, "sess", null, "connect-error", failed: true);

        var fail = Assert.Single(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachConnect
                && r.Outcome == ProcessLogEvents.OutcomeError);
        Assert.Equal("connect", fail.Action);
        Assert.False(string.IsNullOrWhiteSpace(fail.Err));
        Assert.DoesNotContain(secret, fail.Err, StringComparison.Ordinal);
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachDisconnect
                && r.Outcome == ProcessLogEvents.OutcomeError
                && r.DisconnectReason == "connect-error");
    }

    [Fact]
    public void Failed_run_stage_uses_attach_fail_event()
    {
        var sink = new CapturingProcessLogSink();
        AttachProcessLog.Failed(sink, "run", "sess", "c1", "boom");
        var fail = Assert.Single(sink.Records);
        Assert.Equal(ProcessLogEvents.AttachFail, fail.Event);
        Assert.Equal(ProcessLogEvents.OutcomeError, fail.Outcome);
        Assert.Equal("run", fail.Action);
        Assert.Equal("boom", fail.Err);
        Assert.Equal("c1", fail.AttachClientId);
    }

    [Fact]
    public async Task Invalid_key_configuration_writes_structured_error_and_error_disconnect()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-attach-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var socket = Path.Combine(dir, "hypa.sock");
        const int pid = 4242;
        AttachTtyPrelude? prelude = null;
        try
        {
            prelude = await AttachSession.OpenAttachThenValidateAsync(
                socket,
                pid,
                remoteAttach: false,
                "sess",
                AttachClientConfig.Default with
                {
                    Keys = AttachClientConfig.Default.Keys with { Prefix = AttachBindingSpec.Unset },
                });
            Assert.Equal(1, prelude.ExitCode);
            AssertLogFileHasStartupFailure(ProcessLogPaths.ResolveAttachPath(socket, pid));
        }
        finally
        {
            (prelude?.AttachLog as IDisposable)?.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_theme_configuration_writes_structured_error_and_error_disconnect()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-attach-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var socket = Path.Combine(dir, "hypa.sock");
        const int pid = 4243;
        AttachTtyPrelude? prelude = null;
        try
        {
            prelude = await AttachSession.OpenAttachThenValidateAsync(
                socket,
                pid,
                remoteAttach: false,
                "sess",
                AttachClientConfig.Default with
                {
                    Theme = new AttachThemeConfig { Name = "tokyonight" },
                });
            Assert.Equal(1, prelude.ExitCode);
            AssertLogFileHasStartupFailure(ProcessLogPaths.ResolveAttachPath(socket, pid));
        }
        finally
        {
            (prelude?.AttachLog as IDisposable)?.Dispose();
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Startup_cancellation_during_connect_writes_error_then_error_disconnect()
    {
        var sink = new CapturingProcessLogSink();
        Assert.True(AttachSession.TryRecordStartupCancellation(
            sink,
            "connect",
            "sess",
            null,
            "The operation was canceled.",
            liveSessionEstablished: false,
            out var reason));
        Assert.Equal("connect-cancelled", reason);
        AttachSession.RecordSessionEnd(sink, "sess", null, reason, failed: true);

        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachConnect
                && r.Outcome == ProcessLogEvents.OutcomeError
                && r.Action == "connect");
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachDisconnect
                && r.Outcome == ProcessLogEvents.OutcomeError
                && r.DisconnectReason == "connect-cancelled");
        Assert.DoesNotContain(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachDisconnect
                && r.Outcome == ProcessLogEvents.OutcomeCompleted);
    }

    [Fact]
    public void Startup_cancellation_during_snapshot_writes_error_then_error_disconnect()
    {
        var sink = new CapturingProcessLogSink();
        Assert.True(AttachSession.TryRecordStartupCancellation(
            sink,
            "snapshot",
            "sess",
            null,
            "The operation was canceled.",
            liveSessionEstablished: false,
            out var reason));
        Assert.Equal("snapshot-cancelled", reason);
        AttachSession.RecordSessionEnd(sink, "sess", null, reason, failed: true);

        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachSnapshot
                && r.Outcome == ProcessLogEvents.OutcomeError
                && r.Action == "snapshot");
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachDisconnect
                && r.Outcome == ProcessLogEvents.OutcomeError
                && r.DisconnectReason == "snapshot-cancelled");
    }

    [Fact]
    public void Post_attach_cancellation_keeps_successful_detach()
    {
        var sink = new CapturingProcessLogSink();
        Assert.False(AttachSession.TryRecordStartupCancellation(
            sink,
            "run",
            "sess",
            "c1",
            "The operation was canceled.",
            liveSessionEstablished: true,
            out var reason));
        Assert.Null(reason);
        Assert.Empty(sink.Records);
        AttachSession.RecordSessionEnd(sink, "sess", "c1", "detach", failed: false);
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachDetach
                && r.Outcome == ProcessLogEvents.OutcomeOk);
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.AttachDisconnect
                && r.Outcome == ProcessLogEvents.OutcomeCompleted);
        Assert.DoesNotContain(sink.Records, r => r.Outcome == ProcessLogEvents.OutcomeError);
    }

    [Fact]
    public void Tab_create_rename_focus_move_close_each_write_requested_and_outcome()
    {
        var sink = new CapturingProcessLogSink();
        foreach (var action in new[] { "create", "rename", "focus", "move", "close" })
        {
            AttachProcessLog.TabRequested(sink, action, "s", "c", "t2", "w", "t1", "t2");
            AttachProcessLog.TabOutcome(
                sink, action, ProcessLogEvents.OutcomeOk, "s", "c", "t2", "w", "t1", "t2", "7");
        }

        Assert.Equal(10, sink.Records.Count);
        Assert.Equal(5, sink.Records.Count(r => r.Event == ProcessLogEvents.TabRequested));
        Assert.Equal(5, sink.Records.Count(r => r.Event == ProcessLogEvents.TabOutcome));
        Assert.Contains(sink.Records, r => r.Action == "close" && r.Outcome == ProcessLogEvents.OutcomeOk);
        Assert.Equal("7", sink.Records.Last().RequestId);
    }

    [Fact]
    public async Task Logging_port_writes_requested_and_outcome_for_tab_mutations()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            ProcessLog = new CapturingProcessLogSink(),
            SessionName = "s",
            AttachClientId = "c",
            TabId = "t1",
            WorkspaceId = "w",
        };
        var inner = new RecordingPort();
        var port = new LoggingAttachCommandPort(inner, live);
        await port.CallAsync(ProtocolMethods.TabCreate, new JsonObject { ["cwd"] = "/tmp" }, CancellationToken.None);
        await port.CallAsync(
            ProtocolMethods.TabRename,
            new JsonObject { ["tab_id"] = "t2", ["name"] = "n" },
            CancellationToken.None);
        await port.CallAsync(
            ProtocolMethods.TabMove,
            new JsonObject { ["tab_id"] = "t2", ["index"] = 0 },
            CancellationToken.None);
        await port.CallAsync(
            ProtocolMethods.TabClose,
            new JsonObject { ["tab_id"] = "t2" },
            CancellationToken.None);
        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Equal(4, sink.Records.Count(r => r.Event == ProcessLogEvents.TabRequested));
        Assert.Equal(4, sink.Records.Count(r => r.Event == ProcessLogEvents.TabOutcome
            && r.Outcome == ProcessLogEvents.OutcomeOk));
        Assert.Contains(sink.Records, r => r.Action == "create");
        Assert.Contains(sink.Records, r => r.Action == "rename");
        Assert.Contains(sink.Records, r => r.Action == "move");
        Assert.Contains(sink.Records, r => r.Action == "close");

        var rejectedLive = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            ProcessLog = new CapturingProcessLogSink(),
            SessionName = "s",
            AttachClientId = "c",
            TabId = "t1",
            WorkspaceId = "w",
        };
        var throwing = new LoggingAttachCommandPort(new ThrowingPort(), rejectedLive);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            throwing.CallAsync(
                ProtocolMethods.TabClose,
                new JsonObject { ["tab_id"] = "t1" },
                CancellationToken.None));
        var rejected = (CapturingProcessLogSink)rejectedLive.ProcessLog;
        Assert.Contains(
            rejected.Records,
            r => r.Event == ProcessLogEvents.TabRequested && r.Action == "close");
        Assert.Contains(
            rejected.Records,
            r => r.Event == ProcessLogEvents.TabOutcome
                && r.Action == "close"
                && r.Outcome == ProcessLogEvents.OutcomeRejected);
        Assert.DoesNotContain(rejected.Records, r => r.Event == ProcessLogEvents.SettingsOutcome);
    }

    [Fact]
    public async Task Live_key_path_tab_rename_writes_requested_and_outcome()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var inner = new RecordingPort();
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(inner, "w", "t1", "p1", "lease-r"),
            ProcessLog = new CapturingProcessLogSink(),
            SessionName = "s",
            AttachClientId = "c",
            TabId = "t1",
            WorkspaceId = "w",
        };
        var logging = new LoggingAttachCommandPort(inner, live);
        live.RenderPort = logging;
        live.Dispatcher.RebindPort(logging);

        await AttachSession.DispatchLayoutActionAsync(
            new KeyActionRequest(KeyActionId.RenameTab, PromptText: "renamed"),
            live,
            AttachSession.LiveControlPort(live, inner),
            CancellationToken.None);

        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.TabRequested && r.Action == "rename");
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.TabOutcome
                && r.Action == "rename"
                && r.Outcome == ProcessLogEvents.OutcomeOk);
        Assert.Contains(inner.Methods, m => m == ProtocolMethods.TabRename);
    }

    [Fact]
    public async Task Chrome_mouse_tab_focus_writes_requested_and_outcome_through_live_port()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var inner = new RecordingPort();
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(inner, "w", "t1", "p1", "lease-r"),
            ProcessLog = new CapturingProcessLogSink(),
            SessionName = "s",
            AttachClientId = "c",
            TabId = "t1",
            WorkspaceId = "w",
            PaneId = "p1",
            ChromeEnabled = false,
        };
        var logging = new LoggingAttachCommandPort(inner, live);
        live.RenderPort = logging;
        live.Dispatcher.RebindPort(logging);

        await AttachSession.ApplyChromeHitAsync(
            new ChromeHit(ChromeHitKind.Tab, TabId: "t2"),
            live,
            inner,
            tty: null,
            CancellationToken.None);

        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.TabRequested && r.Action == "focus");
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.TabOutcome
                && r.Action == "focus"
                && r.Outcome == ProcessLogEvents.OutcomeOk);
        Assert.Contains(inner.Methods, m => m == ProtocolMethods.TabFocus);
    }

    [Fact]
    public async Task Chrome_mouse_global_menu_writes_overlay_requested_and_outcome()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var inner = new RecordingPort();
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(inner, "w", "t1", "p1", "lease-r"),
            ProcessLog = new CapturingProcessLogSink(),
            SessionName = "s",
            AttachClientId = "c",
            TabId = "t1",
            WorkspaceId = "w",
            PaneId = "p1",
            ChromeEnabled = false,
        };
        var logging = new LoggingAttachCommandPort(inner, live);
        live.RenderPort = logging;
        live.Dispatcher.RebindPort(logging);

        await AttachSession.ApplyChromeHitAsync(
            new ChromeHit(ChromeHitKind.SidebarMenu),
            live,
            inner,
            tty: null,
            CancellationToken.None);

        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Contains(sink.Records, r => r.Event == ProcessLogEvents.OverlayRequested);
        Assert.Contains(sink.Records, r => r.Event == ProcessLogEvents.OverlayOutcome);
        Assert.Contains(inner.Methods, m => m == ProtocolMethods.UiClientMode);
    }

    [Fact]
    public async Task Cube_pairing_keyboard_esc_writes_overlay_requested_and_outcome()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var inner = new RecordingPort();
        var fallback = new RecordingPort();
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(inner, "w", "t1", "p1", "lease-r"),
            ProcessLog = new CapturingProcessLogSink(),
            SessionName = "s",
            AttachClientId = "c",
            TabId = "t1",
            WorkspaceId = "w",
            PaneId = "p1",
            ChromeEnabled = false,
        };
        var logging = new LoggingAttachCommandPort(inner, live);
        live.RenderPort = logging;
        live.Dispatcher.RebindPort(logging);

        AttachSession.OpenAddCubeDialog(live);
        await AttachSession.ReportAttachClientModeAsync(
            fallback,
            live,
            CancellationToken.None);

        await AttachSession.HandleCubePairingDialogKeysAsync(
            tty: null,
            live,
            [(byte)0x1b],
            fallback,
            CancellationToken.None);

        Assert.False(live.CubesPairing.IsOpen);
        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.OverlayRequested
                && r.Surface == AttachClientModePublication.CubesPairingToken);
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.OverlayOutcome
                && r.Surface == AttachClientModePublication.CubesPairingToken
                && r.Outcome == ProcessLogEvents.OutcomeOpened);
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.OverlayRequested
                && r.Surface != AttachClientModePublication.CubesPairingToken);
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.OverlayOutcome
                && r.Outcome == ProcessLogEvents.OutcomeClosed);
        Assert.Empty(fallback.Methods);
        Assert.Contains(inner.Methods, m => m == ProtocolMethods.UiClientMode);
    }

    [SkippableFact]
    public async Task Stall_reconnect_keeps_logging_port_for_chrome_tab_and_overlay()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "POSIX socket fixture");
        var dir = Path.Combine(Path.GetTempPath(), "hypa-stall-log-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            await using var mux = await StallReconnectMux.StartAsync(dir);
            var created = await mux.CreateWorkspaceAsync();
            await using var setup = new ControlPlaneClient(mux.SocketPath);
            await setup.ConnectAsync();
            var second = await setup.CallAsync(
                ProtocolMethods.TabCreate,
                new JsonObject { ["workspace_id"] = created.WorkspaceId, ["cwd"] = "/tmp" });
            var tab2 = second.GetProperty("tab_id").GetString()!;

            var reconnect = new AttachReconnectService();
            var session = new AttachSession(reconnect: reconnect);
            var attached = reconnect.Attach(IssueJoinCapability("nonce101"));
            Assert.True(attached.Ok, attached.Detail);
            Assert.True(reconnect.NoteObserved(MuxControlFrame(0, 4, """{"seq":4}""")).Ok);
            Assert.True(reconnect.NoteObserved(MuxBinaryFrame(1, 2, "grid")).Ok);

            await using var placeholder = new ControlPlaneClient(mux.SocketPath);
            await placeholder.ConnectAsync();
            var commandPort = new ControlPlaneAttachCommandPort(placeholder);
            var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
            using var capture = new MemoryStream();
            using var tty = new UnixRawTerminal(capture, 8, 4);
            using var gate = new SemaphoreSlim(1, 1);
            var renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask);
            var sink = new CapturingProcessLogSink();
            var live = new AttachLiveState
            {
                Engine = new KeyEngine(table),
                Table = table,
                Dispatcher = new AttachCommandDispatcher(
                    commandPort, created.WorkspaceId, created.TabId, created.PaneId),
                Renew = renew,
                WorkspaceId = created.WorkspaceId,
                TabId = created.TabId,
                PaneId = created.PaneId,
                ChromeEnabled = false,
                ReconnectPort = reconnect,
                ProcessLog = sink,
                SessionName = "s",
                AttachClientId = "c",
            };
            var loggingBefore = new LoggingAttachCommandPort(commandPort, live);
            live.Dispatcher.RebindPort(loggingBefore);
            live.RenderPort = loggingBefore;

            var recovered = await session.ReconnectAfterStallAsync(
                new MuxReadyInfo("stall-log", mux.SocketPath, """{"ok":true}"""),
                commandPort,
                live,
                new SnapshotAssembler(),
                tty,
                gate,
                renew,
                CancellationToken.None);
            Assert.True(recovered.Ok, recovered.Detail);
            Assert.IsType<LoggingAttachCommandPort>(live.RenderPort);

            var fallback = new RecordingPort();
            await AttachSession.ApplyChromeHitAsync(
                new ChromeHit(ChromeHitKind.Tab, TabId: tab2),
                live,
                fallback,
                tty: null,
                CancellationToken.None);
            Assert.Contains(
                sink.Records,
                r => r.Event == ProcessLogEvents.TabRequested && r.Action == "focus");
            Assert.Contains(
                sink.Records,
                r => r.Event == ProcessLogEvents.TabOutcome
                    && r.Action == "focus"
                    && r.Outcome == ProcessLogEvents.OutcomeOk);
            Assert.Empty(fallback.Methods);

            await AttachSession.ApplyChromeHitAsync(
                new ChromeHit(ChromeHitKind.SidebarMenu),
                live,
                fallback,
                tty: null,
                CancellationToken.None);
            Assert.Contains(sink.Records, r => r.Event == ProcessLogEvents.OverlayRequested);
            Assert.Contains(sink.Records, r => r.Event == ProcessLogEvents.OverlayOutcome);
            Assert.Empty(fallback.Methods);

            if (recovered.Control is not null)
                await recovered.Control.DisposeAsync();
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Cubes_connect_steps_write_log_records_not_tmp_files()
    {
        var timeout = Path.Combine(Path.GetTempPath(), "hypa-activation-timeout.txt");
        var sync = Path.Combine(Path.GetTempPath(), "hypa-connect-sync.txt");
        var committed = Path.Combine(Path.GetTempPath(), "hypa-connect-committed.txt");
        var beforeTimeout = File.Exists(timeout);
        var beforeSync = File.Exists(sync);
        var beforeCommitted = File.Exists(committed);
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            ProcessLog = new CapturingProcessLogSink(),
            SessionName = "s",
            AttachClientId = "c",
            Cubes =
            [
                new SidebarCubeItem
                {
                    Id = "local",
                    Name = "here",
                    Kind = SidebarCubeKind.Local,
                    Reachability = SidebarCubeReachability.Local,
                },
            ],
        };
        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "local"),
            live,
            new RecordingPort(),
            tty: null,
            CancellationToken.None);
        Assert.Equal(beforeTimeout, File.Exists(timeout));
        Assert.Equal(beforeSync, File.Exists(sync));
        Assert.Equal(beforeCommitted, File.Exists(committed));
        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Contains(sink.Records, r => r.Event == ProcessLogEvents.CubesConnectRequested);
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.CubesConnectOutcome
                && r.Outcome == ProcessLogEvents.OutcomeOk);
    }

    [Fact]
    public async Task Remote_cube_without_connect_service_writes_rejected_outcome()
    {
        var timeout = Path.Combine(Path.GetTempPath(), "hypa-activation-timeout.txt");
        var beforeTimeout = File.Exists(timeout);
        var live = NewRemoteCubeLive();
        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "peer"),
            live,
            new RecordingPort(),
            tty: null,
            CancellationToken.None);
        Assert.Equal(beforeTimeout, File.Exists(timeout));
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeRejected);
    }

    [Fact]
    public async Task Failed_cubes_connect_writes_error_outcome()
    {
        var timeout = Path.Combine(Path.GetTempPath(), "hypa-activation-timeout.txt");
        var beforeTimeout = File.Exists(timeout);
        var live = NewRemoteCubeLive();
        live.CubesConnect = new StubCubesConnect { Outcome = FailedCubesOutcome() };
        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "peer"),
            live,
            new RecordingPort(),
            tty: null,
            CancellationToken.None);
        await AttachSession.FlushDestConnectPrepForTests(live);
        Assert.Equal(beforeTimeout, File.Exists(timeout));
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeError);
        Assert.Equal("connect failed", live.StatusError);
    }

    [Fact]
    public async Task Cancelled_cubes_connect_writes_rejected_outcome()
    {
        var timeout = Path.Combine(Path.GetTempPath(), "hypa-activation-timeout.txt");
        var beforeTimeout = File.Exists(timeout);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var live = NewRemoteCubeLive();
        live.CubesConnect = new StubCubesConnect
        {
            Throw = new OperationCanceledException(cts.Token),
        };
        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "peer"),
            live,
            new RecordingPort(),
            tty: null,
            cts.Token);
        Assert.Equal(beforeTimeout, File.Exists(timeout));
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeRejected);
    }

    [Fact]
    public async Task Missing_dest_leases_writes_error_outcome()
    {
        var timeout = Path.Combine(Path.GetTempPath(), "hypa-activation-timeout.txt");
        var beforeTimeout = File.Exists(timeout);
        await using var dest = new ControlPlaneClient("/tmp/hypa-cubes-missing-lease.sock");
        var live = NewRemoteCubeLive();
        live.CubesConnect = new StubCubesConnect
        {
            Outcome = FailedCubesOutcome() with
            {
                Ok = true,
                DestClient = dest,
                DestInputLease = null,
                DestResizeLease = null,
            },
        };
        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "peer"),
            live,
            new RecordingPort(),
            tty: null,
            CancellationToken.None);
        await AttachSession.FlushDestConnectPrepForTests(live);
        Assert.Equal(beforeTimeout, File.Exists(timeout));
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeError);
        Assert.Equal(AttachEndpointUserCopy.Unavailable, live.StatusError);
    }

    [Fact]
    public async Task Already_active_cubes_connect_writes_ok_outcome()
    {
        await using var peer = new ControlPlaneClient("/tmp/hypa-cubes-already-active.sock");
        var live = NewRemoteCubeLive();
        live.CubesConnect = new StubCubesConnect { Outcome = FailedCubesOutcome() };
        live.ConnectedPlacementId = "peer";
        live.ControlSlot = new AttachControlSlot { Client = peer };
        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "peer"),
            live,
            new RecordingPort(),
            tty: null,
            CancellationToken.None);
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeOk);
        Assert.Empty(((StubCubesConnect)live.CubesConnect).Calls);
    }

    [Fact]
    public async Task Connected_cube_with_a_dead_client_dials_again()
    {
        var live = NewRemoteCubeLive();
        live.CubesConnect = new StubCubesConnect { Outcome = FailedCubesOutcome() };
        live.ConnectedPlacementId = "peer";
        live.ControlSlot = null;

        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "peer"),
            live,
            new RecordingPort(),
            tty: null,
            CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (((StubCubesConnect)live.CubesConnect).Calls.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.NotEmpty(((StubCubesConnect)live.CubesConnect).Calls);
    }

    [Fact]
    public async Task Pending_cubes_retarget_writes_ok_outcome()
    {
        await using var source = new ControlPlaneClient("/tmp/hypa-cubes-pending-retarget.sock");
        var live = NewRemoteCubeLive();
        live.CubesConnect = new StubCubesConnect { Outcome = FailedCubesOutcome() };
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.InputFrozen = true;
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.ReleasingSource("off-1"),
            ActivationLease("local"),
            ActivationLease("peer"));
        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "peer"),
            live,
            new RecordingPort(),
            tty: null,
            CancellationToken.None);
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeOk);
        Assert.Empty(((StubCubesConnect)live.CubesConnect).Calls);
    }

    [Fact]
    public async Task Stale_ssh_cubes_connect_writes_error_outcome()
    {
        await using var dest = new ControlPlaneClient("/tmp/hypa-cubes-stale-ssh.sock");
        var live = NewRemoteCubeLive();
        live.SshEndpoints = new SshPlacementEndpointRegistry();
        live.CubesConnect = new StubCubesConnect
        {
            Outcome = OkCubesOutcome(dest) with
            {
                SshConnectGeneration = 3,
                DestEndpoint = new SshAttachEndpoint(
                    new RemoteMuxPath
                    {
                        LocalSocketPath = "/tmp/hypa-cubes-stale-ssh-endpoint.sock",
                        Session = "s",
                        Target = "host",
                        Generation = 3,
                    }),
            },
        };
        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "peer"),
            live,
            new RecordingPort(),
            tty: null,
            CancellationToken.None);
        await AttachSession.FlushDestConnectPrepForTests(live);
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeError);
    }

    [Fact]
    public async Task Unready_source_snapshot_writes_error_outcome()
    {
        await using var source = new ControlPlaneClient("/tmp/hypa-cubes-unready-source.sock");
        await using var dest = new ControlPlaneClient("/tmp/hypa-cubes-unready-dest.sock");
        var live = NewRemoteCubeLive();
        live.ConnectedPlacementId = "local";
        live.ControlSlot = new AttachControlSlot { Client = source };
        live.CubesConnect = new StubCubesConnect { Outcome = OkCubesOutcome(dest) };
        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "peer"),
            live,
            new RecordingPort(),
            tty: null,
            CancellationToken.None);
        await AttachSession.FlushDestConnectPrepForTests(live);
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeError);
        Assert.Equal("endpoint metadata is not ready for this connection", live.StatusError);
    }

    [Fact]
    public async Task Preflight_failed_cubes_connect_writes_error_outcome()
    {
        await using var dest = new ControlPlaneClient("/tmp/hypa-cubes-preflight.sock");
        var live = NewRemoteCubeLive();
        live.CubesConnect = new StubCubesConnect { Outcome = OkCubesOutcome(dest) };
        await AttachSession.ApplyCubesConnectAsync(
            new MouseEngineResult(MouseCommandKind.ApplyMenu, PlacementId: "peer"),
            live,
            new RecordingPort(),
            tty: null,
            CancellationToken.None);
        await AttachSession.FlushDestConnectPrepForTests(live);
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeError);
        Assert.Equal(AttachEndpointUserCopy.Incompatible("there"), live.StatusError);
    }

    [Fact]
    public async Task Restored_source_without_successor_writes_connect_error_outcome()
    {
        var timeout = Path.Combine(Path.GetTempPath(), "hypa-activation-timeout.txt");
        var beforeTimeout = File.Exists(timeout);
        await using var source = new ControlPlaneClient("/tmp/hypa-cubes-restored-source-log.sock");
        var live = NewRemoteCubeLive();
        live.ControlSlot = new AttachControlSlot { Client = source };
        AttachProcessLog.CubesRequested(live.ProcessLog, "connect", live.SessionName, live.AttachClientId);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                ActivationLease("local"),
                "9:1:local-boot",
                true,
                new ActivationCompletion.RestoredSource("endpoint handoff was rolled back", null)),
            ActivationLease("local"),
            ActivationLease("peer"));
        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(successor);
        Assert.Equal(beforeTimeout, File.Exists(timeout));
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeError);
        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Equal(
            1,
            sink.Records.Count(r => r.Event == ProcessLogEvents.CubesConnectOutcome
                && r.Action == "connect"));
        Assert.Equal(CubesConnectActions.Noop, live.CubesConnectAction);
    }

    [Fact]
    public async Task Pending_activation_disconnect_unavailable_writes_connect_error_outcome()
    {
        var timeout = Path.Combine(Path.GetTempPath(), "hypa-activation-timeout.txt");
        var beforeTimeout = File.Exists(timeout);
        await using var source = new ControlPlaneClient("/tmp/hypa-cubes-disconnect-unavailable-log.sock");
        var live = NewRemoteCubeLive();
        live.ControlSlot = new AttachControlSlot { Client = source };
        AttachProcessLog.CubesRequested(live.ProcessLog, "connect", live.SessionName, live.AttachClientId);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.RestoringSource("on", 2, new EndpointActivationEvidence()),
            ActivationLease("local"),
            ActivationLease("peer"));
        AttachSession.HandleEndpointDisconnect(live, "local", 0, "endpoint gone", tty: null);
        Assert.Null(live.PendingActivation);
        Assert.Equal(beforeTimeout, File.Exists(timeout));
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeError);
        Assert.Equal(
            "endpoint connection was lost while activating endpoint gone",
            live.StatusError);
    }

    [Fact]
    public async Task Restored_source_with_successor_does_not_write_connect_outcome()
    {
        var timeout = Path.Combine(Path.GetTempPath(), "hypa-activation-timeout.txt");
        var beforeTimeout = File.Exists(timeout);
        await using var source = new ControlPlaneClient("/tmp/hypa-cubes-restored-successor-log.sock");
        var live = NewRemoteCubeLive();
        live.ControlSlot = new AttachControlSlot { Client = source };
        AttachProcessLog.CubesRequested(live.ProcessLog, "connect", live.SessionName, live.AttachClientId);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                ActivationLease("local"),
                "9:1:local-boot",
                true,
                new ActivationCompletion.RestoredSource(
                    "endpoint handoff was rolled back",
                    new EndpointActivationIntent { EndpointId = "other-remote" })),
            ActivationLease("local"),
            ActivationLease("peer"));
        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.NotNull(successor);
        Assert.Equal("other-remote", successor.EndpointId);
        Assert.Equal(beforeTimeout, File.Exists(timeout));
        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Contains(sink.Records, r => r.Event == ProcessLogEvents.CubesConnectRequested);
        Assert.DoesNotContain(
            sink.Records,
            r => r.Event == ProcessLogEvents.CubesConnectOutcome);
        Assert.Equal(CubesConnectActions.Noop, live.CubesConnectAction);
    }

    [Fact]
    public async Task Queued_local_cube_during_frozen_activation_does_not_write_extra_cubes_outcome()
    {
        var timeout = Path.Combine(Path.GetTempPath(), "hypa-activation-timeout.txt");
        var beforeTimeout = File.Exists(timeout);
        await using var source = new ControlPlaneClient("/tmp/hypa-cubes-queued-local-log.sock");
        var live = NewRemoteCubeLive();
        live.Cubes =
        [
            new SidebarCubeItem
            {
                Id = "local",
                Name = "here",
                Kind = SidebarCubeKind.Local,
                Reachability = SidebarCubeReachability.Local,
            },
            live.Cubes[0],
        ];
        live.ControlSlot = new AttachControlSlot { Client = source };
        AttachProcessLog.CubesRequested(live.ProcessLog, "connect", live.SessionName, live.AttachClientId);
        live.PendingActivation = PendingEndpointActivation.ForTests(
            new ActivationPhase.AwaitingPresentationEffects(
                ActivationLease("local"),
                "9:1:local-boot",
                true,
                new ActivationCompletion.RestoredSource("endpoint handoff was rolled back", null)),
            ActivationLease("local"),
            ActivationLease("peer"));

        await AttachSession.ApplyMouseResultAsync(
            new MouseEngineResult(
                MouseCommandKind.ApplyChromeHit,
                Hit: new ChromeHit(ChromeHitKind.SidebarCube, PlacementId: "local")),
            live,
            new RecordingPort(),
            tty: null,
            linked: null,
            CancellationToken.None);

        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Equal(1, sink.Records.Count(r => r.Event == ProcessLogEvents.CubesConnectRequested));
        Assert.DoesNotContain(
            sink.Records,
            r => r.Event == ProcessLogEvents.CubesConnectOutcome);

        var successor = AttachSession.CompleteEndpointActivation(live, tty: null);
        Assert.Null(successor);
        Assert.Equal(beforeTimeout, File.Exists(timeout));
        AssertCubesRequestedThenOutcome(live, ProcessLogEvents.OutcomeError);
        Assert.Equal(
            1,
            sink.Records.Count(r => r.Event == ProcessLogEvents.CubesConnectOutcome
                && r.Action == "connect"));
        Assert.DoesNotContain(
            sink.Records,
            r => r.Event == ProcessLogEvents.CubesConnectOutcome
                && r.Outcome == ProcessLogEvents.OutcomeOk);
    }

    [Fact]
    public void Tab_records_include_previous_and_current_ids()
    {
        var sink = new CapturingProcessLogSink();
        AttachProcessLog.TabRequested(sink, "focus", "s", "c", "t2", "w", "t1", "t2");
        AttachProcessLog.TabOutcome(sink, "focus", ProcessLogEvents.OutcomeOk, "s", "c", "t2", "w", "t1", "t2");
        Assert.Equal("t1", sink.Records[0].PreviousTabId);
        Assert.Equal("t2", sink.Records[0].CurrentTabId);
        Assert.Equal("t1", sink.Records[1].PreviousTabId);
        Assert.Equal("t2", sink.Records[1].CurrentTabId);
    }

    [Fact]
    public void Overlay_and_cubes_records_have_surface_or_action()
    {
        var sink = new CapturingProcessLogSink();
        AttachProcessLog.OverlayRequested(sink, "settings", "s", "c");
        AttachProcessLog.OverlayOutcome(sink, "settings", ProcessLogEvents.OutcomeOpened, "s", "c");
        AttachProcessLog.CubesRequested(sink, "connect", "s", "c");
        AttachProcessLog.CubesOutcome(sink, "sync", ProcessLogEvents.OutcomeOk, "s", "c");
        AttachProcessLog.CubesOutcome(sink, "commit", ProcessLogEvents.OutcomeOk, "s", "c");
        Assert.Equal("settings", sink.Records[0].Surface);
        Assert.Equal(ProcessLogEvents.OutcomeOpened, sink.Records[1].Outcome);
        Assert.DoesNotContain(sink.Records, r => r.Outcome == ProcessLogEvents.OutcomeApplied
            && r.Subsystem == ProcessLogEvents.SubsystemSettings);
    }

    [Fact]
    public void Logging_port_does_not_write_settings_applied()
    {
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(KeyBindingTable.CompileOrThrow(KeysConfig.Default())),
            Table = KeyBindingTable.CompileOrThrow(KeysConfig.Default()),
            Dispatcher = null!,
            ProcessLog = new CapturingProcessLogSink(),
        };
        var inner = new RecordingPort();
        var port = new LoggingAttachCommandPort(inner, live);
        _ = port.CallAsync(ProtocolMethods.ServerReloadConfig, new JsonObject(), CancellationToken.None);
        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.DoesNotContain(sink.Records, r => r.Outcome == ProcessLogEvents.OutcomeApplied);
    }

    [Fact]
    public void Doctor_reports_disabled_sink()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "mux.log");
            File.WriteAllText(path, "{}\n");
            File.WriteAllText(ProcessLogPaths.DisabledMarkerPath(path), "1");
            var check = new MuxLogDoctorCheck(path);
            var result = check.Run();
            Assert.Equal(DoctorStatus.Warn, result.Status);
            Assert.Contains("disabled", result.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public void Doctor_warns_on_shared_parent_and_symlink()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix mode");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.OtherWrite);
            var path = Path.Combine(dir, "mux.log");
            File.WriteAllText(path, "{}\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var parent = new MuxLogDoctorCheck(path).Run();
            Assert.Equal(DoctorStatus.Warn, parent.Status);
            Assert.Contains("parent", parent.Detail, StringComparison.OrdinalIgnoreCase);

            var target = Path.Combine(dir, "real.log");
            File.WriteAllText(target, "{}\n");
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var link = Path.Combine(dir, "link.log");
            File.CreateSymbolicLink(link, target);
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var linked = new MuxLogDoctorCheck(link).Run();
            Assert.Equal(DoctorStatus.Warn, linked.Status);
            Assert.Contains("symlink", linked.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public void Doctor_warns_when_read_only_parent_disables_sink()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Unix mode");

        var dir = Path.Combine(Path.GetTempPath(), "hypa-doc-ro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var path = Path.Combine(dir, "mux.log");
            using (var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information))
            {
                sink.Write(new ProcessLogRecord
                {
                    Event = ProcessLogEvents.AppStartup,
                    Subsystem = ProcessLogEvents.SubsystemMux,
                    Outcome = ProcessLogEvents.OutcomeStarted,
                    Ts = DateTimeOffset.UtcNow,
                    Pid = 1,
                    Level = ProcessLogLevel.Information,
                });
                Assert.True(sink.Disabled);
            }

            var result = new MuxLogDoctorCheck(path).Run();
            Assert.Equal(DoctorStatus.Warn, result.Status);
            Assert.False(
                string.Equals(result.Detail, "log file is not present", StringComparison.Ordinal));
            Assert.True(
                ContainsAny(result.Detail, "disabled", "unusable", "read-only", "permissions"),
                result.Detail);
        }
        finally
        {
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Blocked_sink_does_not_drop_live_render()
    {
        var blocking = new BlockingSink();
        await using var hub = new EventSubscriptionHub(blocking, "sess");
        var sink = new RenderSink();
        var sub = hub.Register(new EventSubscriptionRequest
        {
            SubscriptionId = "sub_r",
            ConnectionId = sink.ConnectionId,
            FromSeq = 0,
            Classes = new HashSet<EventClass> { EventClass.Render },
            ReplayBudget = 0,
            Live = true,
            Sink = sink,
            HasClassTokens = true,
        });
        sub.AttachPane("p1");
        sub.EnableLive();

        for (var i = 1; i <= 1000; i++)
        {
            Assert.True(hub.PostLive(new RuntimeEventRecord
            {
                Seq = i,
                Class = EventClass.Render,
                Reliability = EventReliability.Render,
                Type = ProtocolEventTypes.TerminalRender,
                OccurredAt = DateTimeOffset.UtcNow,
                PayloadJson = """{"pane_id":"p1"}""",
                PaneKey = "p1",
            }));
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sink.Count < 1000 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        blocking.Release();
        Assert.Equal(1000, sink.Count);
    }

    [Fact]
    public async Task Slow_output_subscriber_writes_events_queue_drop_count()
    {
        var logs = new CapturingProcessLogSink(ProcessLogLevel.Information);
        await using var hub = new EventSubscriptionHub(logs, "sess");
        var sub = hub.Register(new EventSubscriptionRequest
        {
            SubscriptionId = "sub_out",
            ConnectionId = "c_out",
            FromSeq = 0,
            Classes = new HashSet<EventClass> { EventClass.Output },
            ReplayBudget = 0,
            Live = true,
            Sink = new RenderSink(),
            HasClassTokens = true,
        });
        sub.AttachPane("p1");
        sub.EnableLive();
        for (var i = 1; i <= EventSubscription.LiveQueueCapacity + 8; i++)
        {
            sub.TryEnqueueLive(new RuntimeEventRecord
            {
                Seq = i,
                Class = EventClass.Output,
                Reliability = EventReliability.Output,
                Type = ProtocolEventTypes.TerminalOutput,
                OccurredAt = DateTimeOffset.UtcNow,
                PayloadJson = """{"pane_id":"p1"}""",
                PaneKey = "p1",
            }, out _);
        }

        Assert.True(hub.ReadQueueSnapshot().OutputDrops > 0);
        Assert.True(hub.PostLive(new RuntimeEventRecord
        {
            Seq = EventSubscription.LiveQueueCapacity + 20,
            Class = EventClass.Control,
            Reliability = EventReliability.Reliable,
            Type = ProtocolEventTypes.NotificationShown,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{}",
        }));

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!logs.Records.Any(r => r.Event == ProcessLogEvents.EventsQueue) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var queue = logs.Records.Single(r => r.Event == ProcessLogEvents.EventsQueue);
        Assert.True(queue.DropCount is > 0);
    }

    [Fact]
    public async Task Prefix_reload_pushes_one_reload_when_settings_suppress_is_false()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var port = new RecordingPort();
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(port, "w1", "t1", "p1", "lease-r"),
            ConfigLoader = new StubLoader(),
            ProcessLog = new CapturingProcessLogSink(),
            SuppressNextConfigReloadedLoad = false,
        };
        await AttachSession.ReloadAttachConfigAsync(live, port, tty: null, CancellationToken.None);
        Assert.Equal(new[] { ProtocolMethods.ServerReloadConfig }, port.Methods);
    }

    [Fact]
    public void Mux_reload_from_disk_sees_patched_theme_and_invalid_keeps_current()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.toml");
            File.WriteAllText(path, "[theme]\nname = \"catppuccin\"\n");
            var loader = new FileAttachConfigLoader(new PathEnv(path));
            var loaded = loader.Load();
            Assert.True(loaded.IsOk, loaded.IsOk ? null : loaded.Error.ToString());
            var runtime = new LiveAttachConfigRuntime(loader, loaded.Value);
            var patched = loader.Patch(
            [
                new AttachConfigAssignment(SettingsValuePolicy.ThemeNameKey, "\"tokyo-night\""),
            ]);
            Assert.True(patched.IsOk, patched.IsOk ? null : patched.Error.ToString());
            var applied = runtime.ReloadFromDisk();
            Assert.Equal(ConfigReloadStatus.Applied, applied.Status);
            Assert.Equal("tokyo-night", runtime.Current.Theme.Name);

            var second = new LiveAttachConfigRuntime(loader, AttachClientConfig.Default);
            Assert.NotEqual("tokyo-night", second.Current.Theme.Name);
            var seen = second.ReloadFromDisk();
            Assert.Equal(ConfigReloadStatus.Applied, seen.Status);
            Assert.Equal("tokyo-night", second.Current.Theme.Name);

            File.WriteAllText(path, "[theme]\nname = \"not-a-theme\"\n");
            var failed = runtime.ReloadFromDisk();
            Assert.Equal(ConfigReloadStatus.Failed, failed.Status);
            Assert.Equal("tokyo-night", runtime.Current.Theme.Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void AssertLogFileHasStartupFailure(string path)
    {
        Assert.True(File.Exists(path), path);
        var events = new List<(string Event, string? Outcome)>();
        foreach (var line in File.ReadAllLines(path))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            events.Add((
                root.GetProperty("event").GetString()!,
                root.TryGetProperty("outcome", out var outcome) ? outcome.GetString() : null));
        }

        Assert.Contains(
            events,
            e => e.Event == ProcessLogEvents.AttachFail && e.Outcome == ProcessLogEvents.OutcomeError);
        Assert.Contains(
            events,
            e => e.Event == ProcessLogEvents.AttachDetach && e.Outcome == ProcessLogEvents.OutcomeError);
        Assert.Contains(
            events,
            e => e.Event == ProcessLogEvents.AttachDisconnect && e.Outcome == ProcessLogEvents.OutcomeError);
    }

    private static AttachLiveState NewRemoteCubeLive()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new RecordingPort(), "w", "t1", "p1", "lease-r"),
            ProcessLog = new CapturingProcessLogSink(),
            SessionName = "s",
            AttachClientId = "c",
            Cubes =
            [
                new SidebarCubeItem
                {
                    Id = "peer",
                    Name = "there",
                    Kind = SidebarCubeKind.Peer,
                    Reachability = SidebarCubeReachability.Reachable,
                },
            ],
        };
    }

    private static void AssertCubesRequestedThenOutcome(AttachLiveState live, string outcome)
    {
        var sink = (CapturingProcessLogSink)live.ProcessLog;
        Assert.Contains(sink.Records, r => r.Event == ProcessLogEvents.CubesConnectRequested);
        Assert.Contains(
            sink.Records,
            r => r.Event == ProcessLogEvents.CubesConnectOutcome
                && r.Action == "connect"
                && r.Outcome == outcome);
    }

    private static CubesConnectRetargetOutcome FailedCubesOutcome() =>
        new()
        {
            Ok = false,
            Reason = CubesConnectReasons.DestConnectFailed,
            Detail = "connect failed",
            Action = CubesConnectActions.Noop,
            DestinationKind = SidebarCubeKind.Peer,
            TransportKind = "unix",
            SourceMuxAlive = true,
            NestedAttachBlocked = false,
            NestedAttachEnabled = false,
            SpawnsHypaAttach = false,
            WorkMoved = false,
            AllowNestedMutated = false,
            CalledServerStop = false,
            ProcessStartCount = 0,
            ProcessStartCommands = [],
            PaneProcessCommands = [],
        };

    private static CubesConnectRetargetOutcome OkCubesOutcome(ControlPlaneClient dest) =>
        FailedCubesOutcome() with
        {
            Ok = true,
            Reason = null,
            Detail = null,
            DestClient = dest,
            DestInputLease = "dest-in",
            DestResizeLease = "dest-r",
            DestSubscribeId = "sub_dest",
        };

    private static EndpointActivationLease ActivationLease(string endpointId) =>
        new()
        {
            EndpointId = endpointId,
            ConnectionGeneration = 1,
            BootId = "boot-" + endpointId,
            MinimumProjectionRevision = 0,
            ClientId = "c",
            LeaseId = "lease-" + endpointId,
        };

    private static bool ContainsAny(string? text, params string[] tokens)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private sealed class StubCubesConnect : ICubesConnectRetargeter
    {
        public CubesConnectRetargetOutcome? Outcome { get; init; }

        public Exception? Throw { get; init; }

        public List<CubesConnectRequest> Calls { get; } = [];

        public Task<CubesConnectRetargetOutcome> ConnectAsync(
            CubesConnectRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add(request);
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(Outcome!);
        }
    }

    private sealed class StubLoader : IAttachConfigLoader
    {
        public AttachClientConfig Config { get; set; } = AttachClientConfig.Default;

        public string ResolvePath() => "/tmp/hypa-stub.toml";

        public AttachConfigResult<AttachClientConfig> Load() =>
            AttachConfigResult<AttachClientConfig>.Ok(Config);

        public AttachConfigResult<AttachClientConfig> Parse(string text) => Load();

        public string DefaultToml() => "";

        public AttachConfigResult<AttachConfigResetResult> ResetKeys() =>
            AttachConfigResult<AttachConfigResetResult>.Fail(
                AttachConfigError.Value("reset", "no", null));

        public AttachConfigResult<AttachClientConfig> Patch(IReadOnlyList<AttachConfigAssignment> assignments)
        {
            foreach (var assignment in assignments)
            {
                if (assignment.Path == SettingsValuePolicy.ThemeNameKey)
                {
                    var name = assignment.TomlLiteral.Trim('"');
                    Config = Config with { Theme = Config.Theme with { Name = name } };
                }
            }

            return AttachConfigResult<AttachClientConfig>.Ok(Config);
        }
    }

    private sealed class PathEnv(string path) : IAttachConfigEnvironment
    {
        public string? GetVariable(string name) =>
            name == FileAttachConfigLoader.ConfigPathVariable ? path : null;

        public string UserHome => Path.GetDirectoryName(path) ?? "/tmp";

        public string? AppData => null;

        public bool IsWindows => false;

        public bool IsMacOs => true;
    }

    private sealed class FailingLoader : IAttachConfigLoader
    {
        public string ResolvePath() => "/tmp/hypa-fail.toml";

        public AttachConfigResult<AttachClientConfig> Load() =>
            AttachConfigResult<AttachClientConfig>.Fail(
                AttachConfigError.Value("toml", "invalid", null));

        public AttachConfigResult<AttachClientConfig> Parse(string text) => Load();

        public string DefaultToml() => "";

        public AttachConfigResult<AttachConfigResetResult> ResetKeys() =>
            AttachConfigResult<AttachConfigResetResult>.Fail(
                AttachConfigError.Value("reset", "no", null));
    }

    private sealed class RecordingPort : IAttachCommandPort
    {
        public List<string> Methods { get; } = [];
        public string Status { get; init; } = "applied";

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            Methods.Add(method);
            if (method.StartsWith("tab.", StringComparison.Ordinal))
            {
                var tabId = parameters?["tab_id"]?.GetValue<string>() ?? "t2";
                var json = "{\"tab_id\":\"" + tabId + "\"}";
                return Task.FromResult(JsonDocument.Parse(json).RootElement.Clone());
            }

            var body = "{\"status\":\"" + Status + "\",\"diagnostics\":[]}";
            return Task.FromResult(JsonDocument.Parse(body).RootElement.Clone());
        }
    }

    private sealed class ThrowingPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            throw new InvalidOperationException("rejected");
    }

    private sealed class BlockingSink : IProcessLogSink
    {
        private readonly ManualResetEventSlim _gate = new(false);
        public bool Disabled => false;
        public string? Path => "/blocked";
        public bool IsEnabled(ProcessLogLevel level) => true;
        public void Write(ProcessLogRecord record) => _gate.Wait(TimeSpan.FromSeconds(8));
        public void Release() => _gate.Set();
    }

    private sealed class RenderSink : IClientConnection
    {
        private int _count;
        public string ConnectionId { get; } = "c_render";
        public int Count => Volatile.Read(ref _count);
        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }
    }

    private static JoinCapability IssueJoinCapability(string nonce)
    {
        Assert.True(JoinNonce.TryParse(nonce, out var parsed));
        Assert.True(DeviceId.TryParse("dev_cli001", out var parsedDevice));
        Assert.True(Hypa.Connectivity.Domain.PlacementId.TryParse("plc_reconn", out var parsedPlacement));
        var issued = new DeveloperJoinCapabilityIssuer().Issue(
            parsedPlacement,
            parsedDevice,
            JoinRole.Client,
            parsed,
            DateTimeOffset.UtcNow.AddMinutes(1),
            DateTimeOffset.UtcNow);
        Assert.True(issued.Ok, issued.Detail);
        return issued.Value!;
    }

    private static StreamFrame MuxControlFrame(uint channel, ulong sequence, string json) =>
        StreamFrame.Control(
            StreamDirection.MuxToClient,
            sequence,
            Encoding.UTF8.GetBytes(json),
            channel);

    private static StreamFrame MuxBinaryFrame(uint channel, ulong sequence, string text) =>
        StreamFrame.Binary(
            StreamDirection.MuxToClient,
            channel,
            sequence,
            Encoding.UTF8.GetBytes(text));

    private sealed class StallReconnectMux : IAsyncDisposable
    {
        private readonly UnixSocketServer _server;

        private StallReconnectMux(ControlPlaneService cp, UnixSocketServer server, string socketPath)
        {
            ControlPlane = cp;
            _server = server;
            SocketPath = socketPath;
        }

        public ControlPlaneService ControlPlane { get; }

        public string SocketPath { get; }

        public static async Task<StallReconnectMux> StartAsync(string socketDir)
        {
            var socket = Path.Combine(socketDir, "hypa.sock");
            var state = new AppState(SessionId.New("stall-log"));
            var journal = new MemoryJournal();
            var hub = new EventSubscriptionHub();
            var intel = new PaneIntelligencePipeline();
            var cp = new ControlPlaneService(
                state,
                TestPaneFactories.Stub(),
                intel,
                new HeuristicAgentDetector(),
                journal: journal,
                subscriptions: hub,
                leases: new InMemoryLeaseRegistry(),
                attachments: new InMemoryAttachmentRegistry());
            var server = new UnixSocketServer(cp, socket);
            await server.StartAsync(CancellationToken.None).ConfigureAwait(false);
            return new StallReconnectMux(cp, server, socket);
        }

        public async Task<(string WorkspaceId, string TabId, string PaneId)> CreateWorkspaceAsync()
        {
            await using var client = new ControlPlaneClient(SocketPath);
            await client.ConnectAsync().ConfigureAwait(false);
            var created = await client.CallAsync(
                    ProtocolMethods.WorkspaceCreate,
                    new JsonObject
                    {
                        ["cwd"] = "/tmp/stall-log",
                        ["create_pane"] = true,
                        ["command"] = "stub",
                        ["label"] = "main",
                    })
                .ConfigureAwait(false);
            return (
                created.GetProperty("workspace_id").GetString()!,
                created.GetProperty("focused_tab_id").GetString()!,
                created.GetProperty("pane").GetProperty("pane_id").GetString()!);
        }

        public async ValueTask DisposeAsync()
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            await ControlPlane.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class MemoryJournal : IRuntimeEventJournal
    {
        private long _seq = 1;
        public List<RuntimeEventRecord> Records { get; } = [];

        public Task<RuntimeResult<JournalHealth>> RecoverAsync(CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<JournalHealth>.Ok(GetHealth()));

        public Task<RuntimeResult<RuntimeEventRecord>> AppendAsync(
            EventClass @class,
            EventReliability reliability,
            string type,
            string payloadJson,
            DateTimeOffset? occurredAt = null,
            CancellationToken ct = default)
        {
            var rec = new RuntimeEventRecord
            {
                Seq = _seq++,
                Class = @class,
                Reliability = reliability,
                Type = type,
                OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
                PayloadJson = payloadJson,
            };
            Records.Add(rec);
            return Task.FromResult(RuntimeResult<RuntimeEventRecord>.Ok(rec));
        }

        public Task<RuntimeResult<IReadOnlyList<RuntimeEventRecord>>> ReadRangeAsync(
            long fromSeqExclusive,
            IReadOnlySet<EventClass>? classes,
            int budget,
            CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<IReadOnlyList<RuntimeEventRecord>>.Ok(
                Records.Where(r => r.Seq > fromSeqExclusive).Take(budget).ToList()));

        public Task<RuntimeResult<RuntimeUnit>> CloseOpenSegmentAsync(CancellationToken ct = default) =>
            Task.FromResult(RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value));

        public JournalHealth GetHealth() => new()
        {
            NextSeq = _seq,
            ReplayComplete = true,
            Bytes = 0,
        };

        public long NextSeq => _seq;
    }
}
