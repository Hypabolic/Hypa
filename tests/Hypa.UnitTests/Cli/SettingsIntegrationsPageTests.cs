using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Integrations;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Infrastructure.Integrations;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Mux;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Onboarding;
using Hypa.Cli.Attach.Settings;
using Hypa.ControlPlane;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class SettingsIntegrationsPageTests
{
    [Fact]
    public async Task Local_attach_integrations_page_lists_targets_from_the_endpoint()
    {
        await using var peer = await LoopbackPeer.StartAsync();
        await using var client = ControlPlaneClient.FromConnectedStream(peer.ClientStream);
        await client.ConnectAsync();

        var live = ActivatedLocal(client);
        var command = new ControlPlaneAttachCommandPort(client);
        command.UseShellLane(live);
        var port = new LoggingAttachCommandPort(command, live);
        live.RenderPort = port;
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Engine.EnterSettings(SettingsPageRegistry.IntegrationsId);

        var refresh = AttachSession.RefreshSettingsIntegrationsAsync(
            live,
            port,
            CancellationToken.None);
        var finished = await Task.WhenAny(refresh, Task.Delay(TimeSpan.FromSeconds(3)));
        var sawList = peer.Saw(ProtocolMethods.IntegrationList);
        Assert.True(
            ReferenceEquals(finished, refresh),
            "refresh did not finish. saw=" + sawList
            + " error=" + live.StatusError
            + " count=" + live.Engine.Settings.Integrations.Count);
        await refresh;

        Assert.True(
            live.Engine.Settings.Integrations.Count >= 2,
            "empty page. saw=" + sawList
            + " error=" + live.StatusError
            + " outcomes=" + string.Join(",", live.EndpointCommands.Outcomes.Select(o => o.MethodName + ":" + o.Code)));
        Assert.Contains(live.Engine.Settings.Integrations, status => status.Target == OfficialIntegrationTarget.Claude);
        Assert.Contains(live.Engine.Settings.Integrations, status => status.Target == OfficialIntegrationTarget.Pi);
    }

    [Fact]
    public async Task Integrations_tab_click_lists_targets_on_a_local_attach()
    {
        await using var peer = await LoopbackPeer.StartAsync();
        await using var client = ControlPlaneClient.FromConnectedStream(peer.ClientStream);
        await client.ConnectAsync();

        var live = ActivatedLocal(client);
        var command = new ControlPlaneAttachCommandPort(client);
        command.UseShellLane(live);
        var port = new LoggingAttachCommandPort(command, live);
        live.RenderPort = port;
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Engine.EnterSettings();
        var layout = SettingsPainter.Measure(live.Engine.Settings, 120, 40);
        live.Engine.Settings.Layout = layout;
        var tab = Assert.Single(layout.Tabs, hit => hit.Id == SettingsPageRegistry.IntegrationsId);

        await AttachSession.ApplySettingsPointerAsync(
            live,
            tab.Rect.Col,
            tab.Rect.Row,
            port,
            tty: null,
            CancellationToken.None);
        Assert.NotNull(live.SettingsIntegrationsLoad);
        await live.SettingsIntegrationsLoad!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SettingsPageRegistry.IntegrationsId, live.Engine.Settings.ActivePageId);
        Assert.True(
            live.Engine.Settings.Integrations.Count >= 2,
            "click left the page empty. saw=" + peer.Saw(ProtocolMethods.IntegrationList)
            + " error=" + live.StatusError);
    }

    [Fact]
    public async Task Inactive_surface_does_not_bind_an_empty_integration_list()
    {
        await using var peer = await LoopbackPeer.StartAsync();
        await using var client = ControlPlaneClient.FromConnectedStream(peer.ClientStream);
        await client.ConnectAsync();

        var live = ActivatedLocal(client);
        live.SetEndpointSurfaceActive("local", false);
        var command = new ControlPlaneAttachCommandPort(client);
        command.UseShellLane(live);
        var port = new LoggingAttachCommandPort(command, live);
        live.RenderPort = port;
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Engine.EnterSettings(SettingsPageRegistry.IntegrationsId);
        live.Engine.Settings.BindIntegrations(
        [
            new OfficialIntegrationStatus
            {
                Target = OfficialIntegrationTarget.Claude,
                Path = "/tmp/claude",
                State = OfficialIntegrationStatusKind.Current,
                InstalledVersion = 1,
                ExpectedVersion = 1,
                Available = true,
            },
        ]);

        await AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);

        Assert.Contains(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Claude);
        var host = new HostFrame();
        host.Resize(120, 40);
        SettingsPainter.Stamp(new HostFrameCellSink(host), live.Engine.Settings, 120, 40);
        var text = FrameText(host);
        Assert.Contains("interrupted", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Enter_installs_available_and_outdated_targets_then_refreshes()
    {
        await using var peer = await LoopbackPeer.StartAsync();
        await using var client = ControlPlaneClient.FromConnectedStream(peer.ClientStream);
        await client.ConnectAsync();

        var live = ActivatedLocal(client);
        var command = new ControlPlaneAttachCommandPort(client);
        command.UseShellLane(live);
        var port = new LoggingAttachCommandPort(command, live);
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Engine.EnterSettings(SettingsPageRegistry.IntegrationsId);
        await AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        Assert.True(live.Engine.Settings.Apply());

        await AttachSession.HandleSettingsOverlayAsync(
            new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: SettingsOverlayModel.FilterApply),
            live,
            port,
            CancellationToken.None);

        Assert.True(peer.Saw(ProtocolMethods.IntegrationInstall));
        Assert.Contains(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Claude
                && status.State == OfficialIntegrationStatusKind.Current);
        Assert.Contains(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Pi
                && status.State == OfficialIntegrationStatusKind.Current);
        Assert.Contains(live.Engine.Settings.IntegrationMessages, message => message.Contains("installed", StringComparison.Ordinal));
    }

    [Fact]
    public void Integrations_page_shows_title_markers_loading_empty_and_badge()
    {
        var model = OpenIntegrations();
        model.BeginLoadingIntegrations();
        var loading = Frame(model, 100, 40);
        Assert.Contains("agent integrations", loading, StringComparison.Ordinal);
        Assert.Contains("let agents report state directly", loading, StringComparison.Ordinal);
        Assert.Contains("loading integrations", loading, StringComparison.Ordinal);

        model.BindIntegrations([]);
        var empty = Frame(model, 100, 40);
        Assert.Contains("no integration targets available", empty, StringComparison.Ordinal);
        Assert.Equal(22, model.Layout!.Panel.Rows);

        model.BindIntegrations(
        [
            Row(OfficialIntegrationTarget.Claude, OfficialIntegrationStatusKind.Current, true, 1),
            Row(OfficialIntegrationTarget.Pi, OfficialIntegrationStatusKind.Outdated, true, 0),
            Row(OfficialIntegrationTarget.Codex, OfficialIntegrationStatusKind.NotInstalled, true, null),
            Row(OfficialIntegrationTarget.Grok, OfficialIntegrationStatusKind.NotInstalled, false, null),
        ]);
        var rows = Frame(model, 120, 48);
        Assert.Contains("agent integrations", rows, StringComparison.Ordinal);
        Assert.Contains("let agents report state directly", rows, StringComparison.Ordinal);
        Assert.Contains("✓", rows, StringComparison.Ordinal);
        Assert.Contains("installed", rows, StringComparison.Ordinal);
        Assert.Contains("↻", rows, StringComparison.Ordinal);
        Assert.Contains("update available", rows, StringComparison.Ordinal);
        Assert.Contains("+", rows, StringComparison.Ordinal);
        Assert.Contains("available", rows, StringComparison.Ordinal);
        Assert.Contains("–", rows, StringComparison.Ordinal);
        Assert.Contains("not found", rows, StringComparison.Ordinal);
        Assert.Contains("●", rows, StringComparison.Ordinal);
        Assert.Contains("settings", rows, StringComparison.Ordinal);
        Assert.True(model.Layout!.Panel.Rows >= 22);
        Assert.True(model.Layout.Panel.Rows >= 14 + 4);

        var many = new OfficialIntegrationStatus[17];
        for (var i = 0; i < many.Length; i++)
            many[i] = Row(OfficialIntegrationTarget.Claude, OfficialIntegrationStatusKind.Current, true, 1);
        model.BindIntegrations(many);
        _ = Frame(model, 120, 48);
        Assert.Equal(14 + many.Length, model.Layout.Panel.Rows);
    }

    [Fact]
    public void Integrations_page_keeps_a_space_between_every_label_and_its_state()
    {
        var model = OpenIntegrations();
        model.BindIntegrations(
            Enum.GetValues<OfficialIntegrationTarget>()
                .Select(target => Row(target, OfficialIntegrationStatusKind.NotInstalled, false, null))
                .ToArray());

        var rows = Frame(model, 120, 48);

        foreach (var target in Enum.GetValues<OfficialIntegrationTarget>())
        {
            var label = target.Label();
            Assert.DoesNotContain(label + "not found", rows, StringComparison.Ordinal);
            Assert.Matches(
                "(?m)" + System.Text.RegularExpressions.Regex.Escape(label) + " +not found",
                rows);
        }
    }

    [Fact]
    public void Integrations_page_shows_install_messages_and_installing()
    {
        var model = OpenIntegrations();
        model.BindIntegrations(
            [Row(OfficialIntegrationTarget.Claude, OfficialIntegrationStatusKind.NotInstalled, true, null)],
            ["one", "two", "three", "four", "five", "six", "seven"]);
        var messages = Frame(model, 100, 40);
        Assert.Contains("one", messages, StringComparison.Ordinal);
        Assert.Contains("six", messages, StringComparison.Ordinal);
        Assert.DoesNotContain("seven", messages, StringComparison.Ordinal);

        Assert.True(model.Apply());
        var installing = Frame(model, 100, 40);
        Assert.Contains("installing", installing, StringComparison.Ordinal);
    }

    [Fact]
    public void Outdated_skill_shows_the_outdated_marker_and_enter_installs_it()
    {
        var status = new OfficialIntegrationStatus
        {
            Target = OfficialIntegrationTarget.Claude,
            Path = "/tmp/claude",
            State = OfficialIntegrationStatusKind.Current,
            InstalledVersion = 1,
            ExpectedVersion = 1,
            Available = true,
            SkillState = OfficialIntegrationSkillState.Outdated,
        };
        var model = OpenIntegrations();
        model.BindIntegrations([status]);
        var wide = Frame(model, 120, 48);
        Assert.Contains("↻", wide, StringComparison.Ordinal);
        Assert.DoesNotContain("✓", wide, StringComparison.Ordinal);
        Assert.Contains("installed", wide, StringComparison.Ordinal);
        Assert.Contains("skill outdated", wide, StringComparison.Ordinal);
        Assert.DoesNotContain("claude  skill outdated", wide, StringComparison.Ordinal);
        Assert.True(model.Layout!.Panel.Rows >= 22);
        Assert.True(model.Apply());
        Assert.Equal(OfficialIntegrationTarget.Claude, Assert.Single(model.PendingInstallTargets!));

        var narrowModel = OpenIntegrations();
        narrowModel.BindIntegrations([status]);
        var narrow = Frame(narrowModel, 40, 40);
        Assert.Contains("↻", narrow, StringComparison.Ordinal);
        Assert.Contains("skill outdated", narrow, StringComparison.Ordinal);
        Assert.Contains("claude  skill outdated", narrow, StringComparison.Ordinal);
        Assert.Contains("installed", narrow, StringComparison.Ordinal);
        Assert.Equal(22, narrowModel.Layout!.Panel.Rows);
    }

    [Fact]
    public void Outdated_skill_marks_the_integrations_tab()
    {
        var model = OpenIntegrations();
        model.BindIntegrations(
        [
            new OfficialIntegrationStatus
            {
                Target = OfficialIntegrationTarget.Claude,
                Path = "/tmp/claude",
                State = OfficialIntegrationStatusKind.Current,
                InstalledVersion = 1,
                ExpectedVersion = 1,
                Available = true,
                SkillState = OfficialIntegrationSkillState.Outdated,
            },
        ]);

        var frame = Frame(model, 120, 48);
        var page = Assert.Single(model.Pages, item => item.Id == SettingsPageRegistry.IntegrationsId);
        Assert.Equal("● integrations", model.TabLabel(page));
        Assert.Contains("●", frame, StringComparison.Ordinal);
        Assert.Contains("installed", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("update available", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void Skill_states_that_do_not_fit_stay_visible_past_six_install_lines()
    {
        var skills = new[]
        {
            OfficialIntegrationSkillState.Outdated,
            OfficialIntegrationSkillState.Missing,
            OfficialIntegrationSkillState.Installed,
        };
        var statuses = new OfficialIntegrationStatus[OfficialIntegrationTargets.All.Count];
        for (var i = 0; i < statuses.Length; i++)
        {
            var target = OfficialIntegrationTargets.All[i];
            statuses[i] = new OfficialIntegrationStatus
            {
                Target = target,
                Path = "/tmp/" + target.WireName(),
                State = OfficialIntegrationStatusKind.Current,
                InstalledVersion = 1,
                ExpectedVersion = 1,
                Available = true,
                SkillState = skills[i % skills.Length],
            };
        }

        var model = OpenIntegrations();
        model.BindIntegrations(
            statuses,
            ["one", "two", "three", "four", "five", "six", "seven"]);
        var installLines = 6;
        var expectedRows = 14 + statuses.Length + installLines + statuses.Length;
        var frame = Frame(model, 40, expectedRows + 8);

        Assert.Equal(expectedRows, model.Layout!.Panel.Rows);
        Assert.Contains("six", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("seven", frame, StringComparison.Ordinal);
        for (var i = 0; i < statuses.Length; i++)
        {
            var skill = skills[i % skills.Length] switch
            {
                OfficialIntegrationSkillState.Outdated => "skill outdated",
                OfficialIntegrationSkillState.Missing => "skill missing",
                _ => "skill installed",
            };
            Assert.Contains(
                statuses[i].Target.Label() + "  " + skill,
                frame,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Overflow_skill_lines_scroll_into_view_at_40_by_48()
    {
        var skills = new[]
        {
            OfficialIntegrationSkillState.Outdated,
            OfficialIntegrationSkillState.Missing,
            OfficialIntegrationSkillState.Installed,
        };
        var statuses = new OfficialIntegrationStatus[OfficialIntegrationTargets.All.Count];
        for (var i = 0; i < statuses.Length; i++)
        {
            var target = OfficialIntegrationTargets.All[i];
            statuses[i] = new OfficialIntegrationStatus
            {
                Target = target,
                Path = "/tmp/" + target.WireName(),
                State = OfficialIntegrationStatusKind.Current,
                InstalledVersion = 1,
                ExpectedVersion = 1,
                Available = true,
                SkillState = skills[i % skills.Length],
            };
        }

        var lines = new string[statuses.Length];
        for (var i = 0; i < statuses.Length; i++)
        {
            var skill = skills[i % skills.Length] switch
            {
                OfficialIntegrationSkillState.Outdated => "skill outdated",
                OfficialIntegrationSkillState.Missing => "skill missing",
                _ => "skill installed",
            };
            lines[i] = statuses[i].Target.Label() + "  " + skill;
        }

        var model = OpenIntegrations();
        model.BindIntegrations(
            statuses,
            ["one", "two", "three", "four", "five", "six", "seven"]);
        var first = Frame(model, 40, 48);
        Assert.Equal(46, model.Layout!.Panel.Rows);
        Assert.Contains("six", first, StringComparison.Ordinal);
        Assert.DoesNotContain("seven", first, StringComparison.Ordinal);
        Assert.Contains(lines, line => !first.Contains(line, StringComparison.Ordinal));

        var seen = new bool[lines.Length];
        MarkSeen(first, lines, seen);
        var offsets = new HashSet<int> { model.ListOffset };
        for (var step = 0; step < 64; step++)
        {
            Assert.True(model.MoveList(1));
            if (!offsets.Add(model.ListOffset))
                break;
            var frame = Frame(model, 40, 48);
            Assert.DoesNotContain("seven", frame, StringComparison.Ordinal);
            MarkSeen(frame, lines, seen);
            if (Array.TrueForAll(seen, line => line))
                break;
        }

        Assert.All(seen, line => Assert.True(line));
    }

    private static void MarkSeen(string frame, string[] lines, bool[] seen)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (frame.Contains(lines[i], StringComparison.Ordinal))
                seen[i] = true;
        }
    }

    [Fact]
    public async Task Onboarding_complete_lists_targets_on_an_active_endpoint()
    {
        await using var peer = await LoopbackPeer.StartAsync();
        await using var client = ControlPlaneClient.FromConnectedStream(peer.ClientStream);
        await client.ConnectAsync();

        var live = ActivatedLocal(client);
        var command = new ControlPlaneAttachCommandPort(client);
        command.UseShellLane(live);
        var port = new LoggingAttachCommandPort(command, live);
        live.RenderPort = port;
        live.ConfigLoader = new MemoryConfigLoader();
        foreach (var ev in live.Engine.EnterOnboarding())
            _ = ev;
        live.Engine.Onboarding.RequestComplete();

        var done = await AttachSession.TryCompleteOnboardingAsync(live, port, CancellationToken.None);
        Assert.NotNull(live.SettingsIntegrationsLoad);
        await live.SettingsIntegrationsLoad!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(done);
        Assert.Equal(AttachClientMode.Settings, live.Engine.Mode);
        Assert.Equal(SettingsPageRegistry.IntegrationsId, live.Engine.Settings.ActivePageId);
        Assert.True(peer.Saw(ProtocolMethods.IntegrationList));
        Assert.Contains(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Claude);
        Assert.Contains(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Pi);
    }

    [Fact]
    public async Task Integration_retry_stops_after_the_limit()
    {
        var live = ReadySettings();
        var port = new ScriptedListPort { Fail = ScriptedListFailure.Cancelled };

        await AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        Assert.True(live.Engine.Settings.IntegrationRetryArmed);
        Assert.Empty(live.Engine.Settings.Integrations);

        var spins = 0;
        while (live.Engine.Settings.IntegrationRetryArmed)
        {
            await AttachSession.ObserveSettingsIntegrationsAsync(live, port, null, CancellationToken.None);
            spins++;
            Assert.True(spins <= SettingsOverlayModel.IntegrationRetryLimit);
        }

        Assert.Equal(1 + SettingsOverlayModel.IntegrationRetryLimit, port.Lists);
        Assert.False(live.Engine.Settings.IntegrationRetryArmed);
        var settled = port.Lists;
        await AttachSession.ObserveSettingsIntegrationsAsync(live, port, null, CancellationToken.None);
        Assert.Equal(settled, port.Lists);
    }

    [Fact]
    public async Task Non_retryable_integration_error_does_not_stay_armed()
    {
        var live = ReadySettings();
        var port = new ScriptedListPort { Fail = ScriptedListFailure.Failed };

        await AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);

        Assert.False(live.Engine.Settings.IntegrationRetryArmed);
        Assert.Equal(1, port.Lists);
        await AttachSession.ObserveSettingsIntegrationsAsync(live, port, null, CancellationToken.None);
        Assert.Equal(1, port.Lists);
    }

    [Fact]
    public async Task Endpoint_switch_while_the_page_is_open_reloads_targets()
    {
        var live = ReadySettings();
        var port = new EndpointListPort(live);
        await AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        Assert.Contains(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Claude);

        ActivateCube(live);
        await AttachSession.ObserveSettingsIntegrationsAsync(live, port, null, CancellationToken.None);

        Assert.Contains(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Codex);
        Assert.DoesNotContain(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Claude);
        Assert.Equal(2, port.Lists);
        await AttachSession.ObserveSettingsIntegrationsAsync(live, port, null, CancellationToken.None);
        Assert.Equal(2, port.Lists);
    }

    [Fact]
    public async Task Cancelled_install_list_keeps_rows_and_retries()
    {
        var live = ReadySettings();
        live.Engine.Settings.BindIntegrations(
            [Row(OfficialIntegrationTarget.Claude, OfficialIntegrationStatusKind.NotInstalled, true, null)]);
        Assert.True(live.Engine.Settings.Apply());
        var port = new ScriptedListPort { Fail = ScriptedListFailure.Cancelled };

        await AttachSession.ApplySettingsIntegrationsAsync(live, port, CancellationToken.None);

        Assert.Contains(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Claude);
        Assert.True(live.Engine.Settings.IntegrationRetryArmed);
        Assert.Contains(
            live.Engine.Settings.IntegrationMessages,
            message => message.Contains("installed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pointer_and_observer_serialize_and_discard_a_stale_list()
    {
        var live = ReadySettings();
        live.Engine.Settings.BindIntegrations(
            [Row(OfficialIntegrationTarget.Pi, OfficialIntegrationStatusKind.Current, true, 1)]);
        var layout = SettingsPainter.Measure(live.Engine.Settings, 120, 40);
        live.Engine.Settings.Layout = layout;
        var tab = Assert.Single(layout.Tabs, hit => hit.Id == SettingsPageRegistry.IntegrationsId);
        var port = new HoldingListPort(live);
        var gate = new SemaphoreSlim(1, 1);
        var observer = Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                await AttachSession.ObserveSettingsIntegrationsAsync(
                    live,
                    port,
                    null,
                    CancellationToken.None);
            }
            finally
            {
                gate.Release();
            }
        });
        var entered = await Task.WhenAny(port.Entered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(port.Entered.Task, entered);
        ActivateCube(live);
        var pointer = AttachSession.ApplySettingsPointerUnderGateAsync(
            live,
            tab.Rect.Col,
            tab.Rect.Row,
            port,
            null,
            gate,
            CancellationToken.None);
        port.Release.TrySetResult();
        var both = Task.WhenAll(observer, pointer);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(both.IsCompleted, "pointer and observer did not finish");
        await both;

        Assert.Equal(1, port.MaxInFlight);
        Assert.True(port.SecondCallStillHadPi);
        Assert.False(port.SecondCallSawClaude);
        Assert.Contains(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Codex);
        _ = finished;
    }

    [Fact]
    public async Task Keyboard_page_change_and_observer_share_one_list_call()
    {
        var live = ReadySettings();
        var port = new HoldingListPort(live);
        var keyboard = AttachSession.HandleSettingsOverlayAsync(
            new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: SettingsOverlayModel.FilterPage),
            live,
            port,
            CancellationToken.None);
        var observer = AttachSession.ObserveSettingsIntegrationsAsync(
            live,
            port,
            null,
            CancellationToken.None);
        var entered = await Task.WhenAny(port.Entered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(port.Entered.Task, entered);
        Assert.Equal(1, port.MaxInFlight);
        Assert.Equal(1, port.Calls);
        port.Release.TrySetResult();
        var both = Task.WhenAll(keyboard, observer);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(both.IsCompleted, "keyboard page change and observer did not finish");
        await both;
        Assert.Equal(1, port.MaxInFlight);
        Assert.Equal(1, port.Calls);
        _ = finished;
    }

    [Fact]
    public async Task Observer_first_then_keyboard_page_change_share_one_list_call()
    {
        var live = ReadySettings();
        var port = new HoldingListPort(live);
        var observer = AttachSession.ObserveSettingsIntegrationsAsync(
            live,
            port,
            null,
            CancellationToken.None);
        var entered = await Task.WhenAny(port.Entered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(port.Entered.Task, entered);
        var keyboard = AttachSession.HandleSettingsOverlayAsync(
            new KeyEngineEvent(KeyEngineEventKind.HelpFilter, Filter: SettingsOverlayModel.FilterPage),
            live,
            port,
            CancellationToken.None);
        port.Release.TrySetResult();
        await Task.WhenAll(observer, keyboard).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, port.MaxInFlight);
        Assert.Equal(1, port.Calls);
        Assert.NotEmpty(live.Engine.Settings.Integrations);
    }

    [Fact]
    public async Task Pointer_tab_click_waits_for_a_host_paint()
    {
        var live = ReadySettings();
        var port = new HoldingListPort(live);
        port.Release.TrySetResult();
        var settings = live.Engine.Settings;
        settings.Layout = SettingsPainter.Measure(settings, 120, 40);
        var theme = settings.Layout.Tabs.Single(tab => tab.Id == SettingsPageRegistry.ThemeId);

        using var painting = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var painter = new Thread(() =>
        {
            live.WithPaint(() =>
            {
                painting.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            });
        });
        painter.Start();
        Assert.True(painting.Wait(TimeSpan.FromSeconds(3)));

        var click = Task.Run(() => AttachSession.ApplySettingsPointerAsync(
            live, theme.Rect.Col, theme.Rect.Row, port, null, CancellationToken.None));
        await Task.Delay(200);
        Assert.False(click.IsCompleted);
        Assert.Equal(SettingsPageRegistry.IntegrationsId, settings.ActivePageId);

        release.Set();
        painter.Join();
        await click.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(SettingsPageRegistry.ThemeId, settings.ActivePageId);
    }

    [Fact]
    public async Task Close_during_an_install_sends_no_follow_up_list()
    {
        var live = ReadySettings();
        live.Engine.Settings.BindIntegrations([Row(OfficialIntegrationTarget.Claude, OfficialIntegrationStatusKind.NotInstalled, available: true, installed: null)]);
        Assert.True(live.Engine.Settings.Apply());
        var port = new BlockingInstallPort();
        var apply = AttachSession.ApplySettingsIntegrationsAsync(live, port, CancellationToken.None);
        var entered = await Task.WhenAny(port.InstallEntered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(port.InstallEntered.Task, entered);

        live.WithPaint(() => live.Engine.Settings.Close());
        port.ReleaseInstall.TrySetResult();
        await apply.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, port.ListCalls);
    }

    private sealed class BlockingInstallPort : IAttachCommandPort
    {
        private int _listCalls;

        public TaskCompletionSource InstallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseInstall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ListCalls => Volatile.Read(ref _listCalls);

        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (string.Equals(method, ProtocolMethods.IntegrationInstall, StringComparison.Ordinal))
            {
                InstallEntered.TrySetResult();
                await ReleaseInstall.Task.WaitAsync(ct).ConfigureAwait(false);
                return JsonDocument.Parse("""{"messages":["installed"]}""").RootElement.Clone();
            }

            if (string.Equals(method, ProtocolMethods.IntegrationList, StringComparison.Ordinal))
                Interlocked.Increment(ref _listCalls);
            return JsonDocument.Parse("""{"integrations":[]}""").RootElement.Clone();
        }
    }

    [Fact]
    public async Task Ui_busy_revert_waits_for_a_host_paint_before_closing_settings()
    {
        var live = ReadySettings();
        Assert.Equal(AttachClientMode.Settings, live.Engine.Mode);
        using var painting = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var painter = new Thread(() =>
        {
            live.WithPaint(() =>
            {
                painting.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            });
        });
        painter.Start();
        Assert.True(painting.Wait(TimeSpan.FromSeconds(3)));

        var revert = Task.Run(() => AttachSession.RevertRejectedHumanModal(live));
        await Task.Delay(200);
        Assert.False(revert.IsCompleted);
        Assert.True(live.Engine.Settings.IsOpen);

        release.Set();
        painter.Join();
        await revert.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(live.Engine.Settings.IsOpen);
    }

    [Fact]
    public void Loading_line_stays_visible_when_the_list_is_scrolled()
    {
        var model = OpenIntegrations();
        var rows = OfficialIntegrationTargets.All
            .Select(target => Row(target, OfficialIntegrationStatusKind.NotInstalled, available: true, installed: null))
            .ToList();
        model.BindIntegrations(rows);
        for (var i = 0; i < rows.Count - 1; i++)
            model.MoveList(1);
        model.SyncListOffset(3);
        Assert.True(model.ListOffset > 0);

        model.BeginLoadingIntegrations();
        var frame = Frame(model, 60, 24);
        Assert.Contains(SettingsOverlayModel.IntegrationsLoading.Trim(), frame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_title_names_the_mux_host_when_the_endpoint_is_remote()
    {
        var local = ReadySettings();
        await AttachSession.RefreshSettingsIntegrationsAsync(local, new SilentPort(), CancellationToken.None);
        Assert.Equal(SettingsPainter.Title, local.Engine.Settings.PageTitle);
        var localFrame = Frame(local.Engine.Settings, 80, 24);
        Assert.Contains(SettingsPainter.Title, localFrame, StringComparison.Ordinal);
        Assert.DoesNotContain("lab-host", localFrame, StringComparison.Ordinal);

        var remote = ReadySettings();
        remote.RemoteDestination = true;
        remote.PlacementDisplayName = "lab-host";
        ActivateCube(remote);
        await AttachSession.RefreshSettingsIntegrationsAsync(remote, new SilentPort(), CancellationToken.None);
        Assert.Equal("settings · lab-host", remote.Engine.Settings.PageTitle);
        var frame = Frame(remote.Engine.Settings, 100, 30);
        Assert.Contains("settings · lab-host", frame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_cube_reconnect_while_the_page_is_open_reloads_targets()
    {
        var live = ReadySettings();
        var port = new EndpointListPort(live);
        ActivateCube(live);
        live.ConnectedPlacementId = "cube-a";
        await AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        Assert.Equal(1, port.Lists);
        await AttachSession.ObserveSettingsIntegrationsAsync(live, port, null, CancellationToken.None);
        Assert.Equal(1, port.Lists);

        // A reconnect to the same cube changes only the destination generation.
        live.TransportEnvelope.StampServerGeneration(5);
        await AttachSession.ObserveSettingsIntegrationsAsync(live, port, null, CancellationToken.None);
        Assert.Equal(2, port.Lists);
    }

    [Fact]
    public async Task Entering_the_page_shows_loading_before_the_list_returns()
    {
        var live = ReadySettings();
        var port = new HoldingListPort(live);
        await AttachSession.HandleSettingsOverlayAsync(
            new KeyEngineEvent(KeyEngineEventKind.EnterMode, Mode: AttachClientMode.Settings),
            live,
            port,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

        var entered = await Task.WhenAny(port.Entered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(port.Entered.Task, entered);
        Assert.True(live.Engine.Settings.LoadingIntegrations);
        Assert.True(live.SettingsIntegrationsLoad is { IsCompleted: false });

        port.Release.TrySetResult();
        await live.SettingsIntegrationsLoad!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotEmpty(live.Engine.Settings.Integrations);
        Assert.True(live.HasSettingsPaintPending);
    }

    [Fact]
    public async Task Leaving_and_returning_during_a_list_reloads_for_the_new_visit()
    {
        var live = ReadySettings();
        var port = new HoldingListPort(live);
        var first = AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        var entered = await Task.WhenAny(port.Entered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(port.Entered.Task, entered);

        live.WithPaint(() => live.Engine.Settings.SelectPage(SettingsPageRegistry.ThemeId));
        live.WithPaint(() => live.Engine.Settings.SelectPage(SettingsPageRegistry.IntegrationsId));
        var second = AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);

        port.Release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, port.Calls);
        Assert.NotEmpty(live.Engine.Settings.Integrations);
    }

    [Fact]
    public async Task Close_while_a_list_is_in_flight_does_not_commit_rows()
    {
        var live = ReadySettings();
        var port = new HoldingListPort(live);
        var refresh = AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        var entered = await Task.WhenAny(port.Entered.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(port.Entered.Task, entered);

        live.Engine.Settings.Close();
        port.Release.TrySetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Empty(live.Engine.Settings.Integrations);
        Assert.False(live.Engine.Settings.IntegrationRetryArmed);
        Assert.Null(live.Engine.Settings.IntegrationLoadError);
    }

    [Fact]
    public async Task Close_while_a_list_waits_on_the_gate_sends_nothing()
    {
        var live = ReadySettings();
        var port = new HoldingListPort(live);
        await live.IntegrationListGate.WaitAsync();
        var refresh = AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        Assert.False(refresh.IsCompleted);

        live.Engine.Settings.Close();
        live.IntegrationListGate.Release();
        await refresh.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, port.Calls);
        Assert.Empty(live.Engine.Settings.Integrations);
    }

    [Fact]
    public void Integrations_row_stays_inside_a_13_column_frame()
    {
        var model = OpenIntegrations();
        model.BindIntegrations(
            [Row(OfficialIntegrationTarget.Claude, OfficialIntegrationStatusKind.Current, true, 1)]);
        var host = new HostFrame();
        host.Resize(13, 30);
        SettingsPainter.Stamp(new HostFrameCellSink(host), model, 13, 30);
        var panel = model.Layout!.Panel;
        var borderCol = panel.EndCol - 1;
        var sawBorder = false;
        for (var row = panel.Row; row < panel.EndRow; row++)
        {
            if (host.CellAt(panel.Col, row).Text != "│")
                continue;
            sawBorder = true;
            Assert.Equal("│", host.CellAt(borderCol, row).Text);
        }

        Assert.True(sawBorder);
        Assert.Contains("claude", FrameText(host), StringComparison.Ordinal);
        var paint = SettingsPainter.Paint(model, 13, 30);
        Assert.True(MaxPaintColumn(paint) <= 13);
    }

    [Fact]
    public async Task Continue_opens_integrations_and_escape_writes_no_agent_config()
    {
        using var home = new IntegrationHome();
        home.Command("claude");
        Directory.CreateDirectory(Path.Combine(home.Home, ".claude"));
        var port = new ServiceIntegrationPort(home.Service);
        var loader = new RecordingConfigLoader();
        var live = ReadyAttached();
        live.ConfigLoader = loader;
        live.RenderPort = port;
        foreach (var ev in live.Engine.EnterOnboarding())
            _ = ev;

        var card = OnboardingPainter.Paint(live.Engine.Onboarding, 80, 24);
        Assert.Contains("nothing is installed until you confirm.", card, StringComparison.Ordinal);

        live.Engine.Onboarding.RequestComplete();
        var done = await AttachSession.TryCompleteOnboardingAsync(live, port, CancellationToken.None);
        Assert.NotNull(live.SettingsIntegrationsLoad);
        await live.SettingsIntegrationsLoad!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(done);
        Assert.Equal(AttachClientMode.Settings, live.Engine.Mode);
        Assert.Equal(SettingsPageRegistry.IntegrationsId, live.Engine.Settings.ActivePageId);
        var patch = Assert.Single(loader.Patches);
        Assert.Equal("onboarding", patch.Path);
        Assert.Equal("false", patch.TomlLiteral);
        Assert.False(loader.Config.Onboarding);
        Assert.Equal(0, port.InstallCalls);
        Assert.False(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(home.Env)));
        Assert.False(File.Exists(OfficialIntegrationLayout.RuntimeSkillFile(home.Env, OfficialIntegrationTarget.Claude)!));

        live.Engine.Feed(KeyChord.Parse("esc"));
        Assert.Equal(AttachClientMode.Terminal, live.Engine.Mode);
        Assert.Equal(0, port.InstallCalls);
        Assert.False(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(home.Env)));

        var second = ReadyAttached();
        AttachSession.ApplyOnboardingAtStart(second, loader.Config);
        Assert.Equal(AttachClientMode.Terminal, second.Engine.Mode);
    }

    [Fact]
    public async Task Enter_installs_only_the_selected_target()
    {
        using var home = new IntegrationHome();
        home.Command("claude");
        home.Command("codex");
        Directory.CreateDirectory(Path.Combine(home.Home, ".claude"));
        var codexDir = Path.Combine(home.Home, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Combine(codexDir, "marker.txt"), "keep");

        var live = ReadySettings();
        var port = new ServiceIntegrationPort(home.Service);
        await AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        Assert.True(live.Engine.Settings.IsIntegrationSelected(OfficialIntegrationTarget.Claude));
        Assert.True(live.Engine.Settings.IsIntegrationSelected(OfficialIntegrationTarget.Codex));

        var codexIndex = IndexOf(live, OfficialIntegrationTarget.Codex);
        live.Engine.Settings.SelectItem(codexIndex);
        await ApplyHelpFilters(live, port, live.Engine.Feed(KeyChord.Parse("space")));
        Assert.False(live.Engine.Settings.IsIntegrationSelected(OfficialIntegrationTarget.Codex));
        Assert.True(live.Engine.Settings.IsIntegrationSelected(OfficialIntegrationTarget.Claude));
        Assert.Empty(port.InstallTargets);

        await ApplyHelpFilters(live, port, live.Engine.Feed(KeyChord.Parse("enter")));

        Assert.Equal(["claude"], port.InstallTargets);
        Assert.True(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(home.Env)));
        Assert.True(File.Exists(OfficialIntegrationLayout.RuntimeSkillFile(home.Env, OfficialIntegrationTarget.Claude)!));
        Assert.False(File.Exists(OfficialIntegrationLayout.CodexOwnedFile(home.Env)));
        var codexSkill = OfficialIntegrationLayout.RuntimeSkillFile(home.Env, OfficialIntegrationTarget.Codex);
        Assert.NotNull(codexSkill);
        Assert.False(File.Exists(codexSkill));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(codexDir, "marker.txt")));
    }

    [Fact]
    public async Task Click_on_the_selection_box_toggles_and_the_row_only_highlights()
    {
        using var home = new IntegrationHome();
        home.Command("claude");
        Directory.CreateDirectory(Path.Combine(home.Home, ".claude"));
        var live = ReadySettings();
        var port = new ServiceIntegrationPort(home.Service);
        await AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        var index = IndexOf(live, OfficialIntegrationTarget.Claude);
        Assert.True(live.Engine.Settings.IsIntegrationSelected(OfficialIntegrationTarget.Claude));

        SettingsPainter.Stamp(new HostFrameCellSink(new HostFrame()), live.Engine.Settings, 80, 24);
        var box = BoxOf(live, index);
        await AttachSession.ApplySettingsPointerAsync(live, box.Col, box.Row, port, null, CancellationToken.None);
        Assert.False(live.Engine.Settings.IsIntegrationSelected(OfficialIntegrationTarget.Claude));
        Assert.Empty(port.InstallTargets);

        SettingsPainter.Stamp(new HostFrameCellSink(new HostFrame()), live.Engine.Settings, 80, 24);
        var row = ItemOf(live, index);
        await AttachSession.ApplySettingsPointerAsync(
            live, row.Rect.Col + 8, row.Rect.Row, port, null, CancellationToken.None);
        Assert.False(live.Engine.Settings.IsIntegrationSelected(OfficialIntegrationTarget.Claude));
        Assert.Equal(index, live.Engine.Settings.ItemIndex);
        Assert.Empty(port.InstallTargets);
    }

    [Fact]
    public void Row_detail_keeps_the_full_hook_and_skill_paths()
    {
        var home = Path.Combine(Path.GetTempPath(), "hypa-home-" + Guid.NewGuid().ToString("N"));
        var custom = Path.Combine(Path.GetTempPath(), "hypa-claude-override-" + Guid.NewGuid().ToString("N"));
        var env = new HomeEnv { UserHome = home };
        env.Variables[OfficialIntegrationLayout.ClaudeConfigDir] = custom;
        var lines = IntegrationConsentText.Lines(env, OfficialIntegrationTarget.Claude);
        var hook = IntegrationConsentText.DisplayPath(env, OfficialIntegrationLayout.ClaudeOwnedFile(env));
        var skillFile = OfficialIntegrationLayout.RuntimeSkillFile(env, OfficialIntegrationTarget.Claude);
        Assert.NotNull(skillFile);
        var skill = IntegrationConsentText.DisplayPath(env, skillFile);
        Assert.Contains(custom.Replace('\\', '/'), hook, StringComparison.Ordinal);
        Assert.Contains(custom.Replace('\\', '/'), skill, StringComparison.Ordinal);

        var model = OpenIntegrations();
        model.BindIntegrations(
        [
            new OfficialIntegrationStatus
            {
                Target = OfficialIntegrationTarget.Claude,
                Path = hook,
                State = OfficialIntegrationStatusKind.NotInstalled,
                ExpectedVersion = 1,
                Available = true,
                Consent = lines,
            },
        ]);

        foreach (var (cols, rows) in new[] { (80, 24), (40, 48) })
        {
            var text = InnerPanelText(model, cols, rows);
            Assert.Contains(hook, text, StringComparison.Ordinal);
            Assert.Contains(skill, text, StringComparison.Ordinal);
            var paint = SettingsPainter.Paint(model, cols, rows);
            Assert.True(MaxPaintColumn(paint) <= cols);
        }

        Assert.False(Directory.Exists(custom));
    }

    [Fact]
    public async Task Failed_target_shows_a_reason_and_the_other_target_installs()
    {
        using var home = new IntegrationHome();
        home.Command("claude");
        home.Command("codex");
        Directory.CreateDirectory(Path.Combine(home.Home, ".codex"));
        var live = ReadySettings();
        var port = new ServiceIntegrationPort(home.Service);
        await AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        Assert.True(live.Engine.Settings.IsIntegrationSelected(OfficialIntegrationTarget.Claude));
        Assert.True(live.Engine.Settings.IsIntegrationSelected(OfficialIntegrationTarget.Codex));

        await ApplyHelpFilters(live, port, live.Engine.Feed(KeyChord.Parse("enter")));

        Assert.Contains("claude", port.InstallTargets);
        Assert.Contains("codex", port.InstallTargets);
        Assert.False(File.Exists(OfficialIntegrationLayout.ClaudeOwnedFile(home.Env)));
        Assert.True(File.Exists(OfficialIntegrationLayout.CodexOwnedFile(home.Env)));
        Assert.True(File.Exists(OfficialIntegrationLayout.RuntimeSkillFile(home.Env, OfficialIntegrationTarget.Codex)!));

        var claudeIndex = IndexOf(live, OfficialIntegrationTarget.Claude);
        live.Engine.Settings.SelectItem(claudeIndex);
        var detail = string.Join(" ", live.Engine.Settings.HighlightedDetail());
        Assert.Contains("directory not found", detail, StringComparison.Ordinal);
        Assert.Contains("press Enter again", detail, StringComparison.Ordinal);
        Assert.True(live.Engine.Settings.TryIntegrationResult(
            OfficialIntegrationTarget.Claude, out var failed));
        Assert.Equal(IntegrationRowResultKind.Failed, failed.Kind);

        var summary = live.Engine.Settings.IntegrationSummary ?? "";
        Assert.Contains("1 installed", summary, StringComparison.Ordinal);
        Assert.Contains("1 failed", summary, StringComparison.Ordinal);
        Assert.Contains("Restart the agent to load the skill.", summary, StringComparison.Ordinal);

        var first = Frame(live.Engine.Settings, 80, 24);
        Assert.Contains("✗ failed", first, StringComparison.Ordinal);
        var sawReason = first.Contains("directory not found", StringComparison.Ordinal)
            && first.Contains("press Enter again", StringComparison.Ordinal);
        for (var step = 0; step < 80 && !sawReason && live.Engine.Settings.MoveList(1); step++)
        {
            var frame = Frame(live.Engine.Settings, 80, 24);
            sawReason = frame.Contains("directory not found", StringComparison.Ordinal)
                && frame.Contains("press Enter again", StringComparison.Ordinal);
        }

        Assert.True(sawReason);
    }

    [Fact]
    public async Task Remove_key_deletes_the_hook_and_the_skill()
    {
        using var home = new IntegrationHome();
        home.Command("claude");
        Directory.CreateDirectory(Path.Combine(home.Home, ".claude"));
        Assert.True(home.Service.Install(OfficialIntegrationTarget.Claude).IsOk);
        var hook = OfficialIntegrationLayout.ClaudeOwnedFile(home.Env);
        var skill = OfficialIntegrationLayout.RuntimeSkillFile(home.Env, OfficialIntegrationTarget.Claude);
        Assert.NotNull(skill);
        Assert.True(File.Exists(hook));
        Assert.True(File.Exists(skill));

        var live = ReadySettings();
        var port = new ServiceIntegrationPort(home.Service);
        await AttachSession.RefreshSettingsIntegrationsAsync(live, port, CancellationToken.None);
        var index = IndexOf(live, OfficialIntegrationTarget.Claude);
        live.Engine.Settings.SelectItem(index);
        var before = live.Engine.Settings.Integrations[index];
        Assert.Equal(OfficialIntegrationStatusKind.Current, before.State);

        await ApplyHelpFilters(live, port, live.Engine.Feed(KeyChord.Parse("u")));

        Assert.False(File.Exists(hook));
        Assert.False(File.Exists(skill));
        var after = Assert.Single(
            live.Engine.Settings.Integrations,
            status => status.Target == OfficialIntegrationTarget.Claude);
        Assert.True(after.Available);
        Assert.Equal(OfficialIntegrationStatusKind.NotInstalled, after.State);
        Assert.Contains("+", Frame(live.Engine.Settings, 80, 24), StringComparison.Ordinal);
        Assert.Contains("available", Frame(live.Engine.Settings, 80, 24), StringComparison.Ordinal);
    }

    private static SettingsOverlayModel OpenIntegrations()
    {
        var model = new SettingsOverlayModel(SettingsPageRegistry.Product());
        model.Bind(Hypa.AgentRuntime.Domain.Theme.ThemeRuntime.Default, AttachUiConfig.Default);
        model.OpenAt(SettingsPageRegistry.IntegrationsId);
        return model;
    }

    private static string Frame(SettingsOverlayModel model, int cols, int rows)
    {
        var host = new HostFrame();
        host.Resize(cols, rows);
        SettingsPainter.Stamp(new HostFrameCellSink(host), model, cols, rows);
        return FrameText(host);
    }

    private static OfficialIntegrationStatus Row(
        OfficialIntegrationTarget target,
        OfficialIntegrationStatusKind state,
        bool available,
        int? installed) =>
        new()
        {
            Target = target,
            Path = "/tmp/" + target.WireName(),
            State = state,
            InstalledVersion = installed,
            ExpectedVersion = 1,
            Available = available,
        };

    private static AttachLiveState ActivatedLocal(ControlPlaneClient client)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new SilentPort(), "w1", "t1", "p1", "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = "p1",
            InputLease = "lease-in",
            ResizeLease = "lease-r",
            AttachClientId = "cli_local",
            ChromeEnabled = true,
            ControlSlot = new AttachControlSlot { Client = client },
        };
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.SourceEndpointBootId = "boot-local";
        live.EndpointCommands.SnapshotBootId = "boot-local";
        live.ActiveProjectionEndpointId = "local";
        live.SetEndpointSurfaceActive("local", true);
        return live;
    }

    private static AttachLiveState ReadyAttached()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new SilentPort(), "w1", "t1", "p1", "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "w1",
            TabId = "t1",
            PaneId = "p1",
            InputLease = "lease-in",
            ResizeLease = "lease-r",
            AttachClientId = "cli_local",
            ChromeEnabled = true,
        };
        live.SourceTransportEnvelope.StampServerGeneration(1);
        live.SourceEndpointBootId = "boot-local";
        live.EndpointCommands.SnapshotBootId = "boot-local";
        live.ActiveProjectionEndpointId = "local";
        live.SetEndpointSurfaceActive("local", true);
        return live;
    }

    private static AttachLiveState ReadySettings()
    {
        var live = ReadyAttached();
        live.Engine.Settings.Bind(live.Theme, live.Ui);
        live.Engine.EnterSettings(SettingsPageRegistry.IntegrationsId);
        return live;
    }

    private static async Task ApplyHelpFilters(
        AttachLiveState live,
        IAttachCommandPort port,
        IReadOnlyList<KeyEngineEvent> events)
    {
        foreach (var ev in events)
        {
            if (ev.Kind is KeyEngineEventKind.HelpFilter)
            {
                await AttachSession.HandleSettingsOverlayAsync(ev, live, port, CancellationToken.None);
            }
        }
    }

    private static int IndexOf(AttachLiveState live, OfficialIntegrationTarget target)
    {
        var list = live.Engine.Settings.Integrations;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Target == target)
                return i;
        }

        throw new InvalidOperationException("missing " + target.WireName());
    }

    private static CellRect BoxOf(AttachLiveState live, int index)
    {
        var layout = live.Engine.Settings.Layout;
        Assert.NotNull(layout);
        foreach (var item in layout.Items)
        {
            if (item.Index == index && item.Box is { } box)
                return box;
        }

        throw new InvalidOperationException("missing selection box");
    }

    private static SettingsItemHit ItemOf(AttachLiveState live, int index)
    {
        var layout = live.Engine.Settings.Layout;
        Assert.NotNull(layout);
        foreach (var item in layout.Items)
        {
            if (item.Index == index)
                return item;
        }

        throw new InvalidOperationException("missing row");
    }

    private static string InnerPanelText(SettingsOverlayModel model, int cols, int rows)
    {
        var host = new HostFrame();
        host.Resize(cols, rows);
        SettingsPainter.Stamp(new HostFrameCellSink(host), model, cols, rows);
        var panel = model.Layout!.Panel;
        var sb = new StringBuilder();
        var innerCol = panel.Col + 1;
        var innerCols = Math.Max(0, panel.Cols - 2);
        for (var row = panel.Row + 1; row < panel.EndRow - 1; row++)
        {
            var line = new StringBuilder();
            for (var c = 0; c < innerCols; c++)
                line.Append(host.CellAt(innerCol + c, row).Text);
            sb.Append(line.ToString().TrimEnd());
        }

        return sb.ToString();
    }

    private static void ActivateCube(AttachLiveState live)
    {
        live.ActiveProjectionEndpointId = "cube-a";
        live.TransportEnvelope.StampServerGeneration(4);
        live.ConnectedBootId = "boot-cube";
        live.SetEndpointSurfaceActive("cube-a", true);
    }

    private static JsonElement IntegrationList(string target, string state)
    {
        var json = "{\"integrations\":[{\"target\":\"" + target
            + "\",\"available\":true,\"state\":\"" + state
            + "\",\"expected_version\":1,\"path\":\"/tmp/" + target + "\"}]}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static int MaxPaintColumn(string text)
    {
        var col = 0;
        var max = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\u001b')
            {
                i++;
                if (i < text.Length && text[i] == '[')
                {
                    i++;
                    var start = i;
                    while (i < text.Length && text[i] < '@')
                        i++;
                    if (i < text.Length && text[i] == 'H')
                    {
                        var body = text[start..i];
                        var parts = body.Split(';');
                        if (parts.Length == 2 && int.TryParse(parts[1], out var cup))
                            col = Math.Max(0, cup - 1);
                    }

                    if (i < text.Length)
                        i++;
                }

                continue;
            }

            col++;
            if (col > max)
                max = col;
            i++;
        }

        return max;
    }

    private static string FrameText(HostFrame host)
    {
        var sb = new StringBuilder();
        for (var row = 0; row < host.Rows; row++)
        {
            for (var col = 0; col < host.Cols; col++)
                sb.Append(host.CellAt(col, row).Text);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private sealed class SilentPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
    }

    private sealed class LoopbackPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _clientSocket;
        private readonly TcpClient _server;
        private readonly List<string> _methods = [];
        private int _installs;
        private Task _reader = Task.CompletedTask;

        private LoopbackPeer(TcpListener listener, TcpClient clientSocket, TcpClient server, NetworkStream clientStream)
        {
            _listener = listener;
            _clientSocket = clientSocket;
            _server = server;
            ClientStream = clientStream;
        }

        private Task Reader
        {
            set => _reader = value;
        }

        internal NetworkStream ClientStream { get; }

        internal static async Task<LoopbackPeer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var clientSocket = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await clientSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await accept;
            var clientStream = clientSocket.GetStream();
            var serverStream = server.GetStream();
            var peer = new LoopbackPeer(listener, clientSocket, server, clientStream);
            peer.Reader = Task.Run(() => peer.ReadAsync(serverStream));
            return peer;
        }

        internal bool Saw(string method)
        {
            lock (_methods)
                return _methods.Contains(method);
        }

        public async ValueTask DisposeAsync()
        {
            _server.Dispose();
            _clientSocket.Dispose();
            _listener.Stop();
            try
            {
                await _reader.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
            }
        }

        private async Task ReadAsync(NetworkStream server)
        {
            using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                        return;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("method", out var methodEl))
                        continue;
                    var method = methodEl.GetString() ?? "";
                    lock (_methods)
                        _methods.Add(method);
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "\"0\"";
                    string reply;
                    if (string.Equals(method, ProtocolMethods.IntegrationInstall, StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _installs);
                        reply = "{\"id\":" + id + ",\"result\":{\"target\":\"claude\",\"messages\":[\"installed claude\"]}}";
                    }
                    else if (string.Equals(method, ProtocolMethods.IntegrationList, StringComparison.Ordinal))
                    {
                        var installed = Volatile.Read(ref _installs) > 0;
                        var claudeState = installed ? "current" : "not_installed";
                        var piState = installed ? "current" : "outdated";
                        var claudeVersion = installed ? ",\"installed_version\":1" : "";
                        var piVersion = installed ? ",\"installed_version\":1" : ",\"installed_version\":0";
                        reply = "{\"id\":" + id + ",\"result\":{\"integrations\":["
                            + "{\"target\":\"claude\",\"label\":\"claude\",\"command\":\"claude\",\"available\":true,\"state\":\"" + claudeState + "\",\"expected_version\":1" + claudeVersion + ",\"path\":\"/tmp/claude\"},"
                            + "{\"target\":\"pi\",\"label\":\"pi\",\"command\":\"pi\",\"available\":true,\"state\":\"" + piState + "\",\"expected_version\":1" + piVersion + ",\"path\":\"/tmp/pi\"}"
                            + "]}}";
                    }
                    else
                    {
                        reply = "{\"id\":" + id + ",\"result\":{}}";
                    }

                    var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                    await server.WriteAsync(bytes);
                    await server.FlushAsync();
                }
            }
            catch (Exception)
            {
            }
        }
    }

    private enum ScriptedListFailure
    {
        None,
        Cancelled,
        Failed,
    }

    private sealed class MemoryConfigLoader : IAttachConfigLoader
    {
        public string ResolvePath() => "config.toml";

        public AttachConfigResult<AttachClientConfig> Load() =>
            AttachConfigResult<AttachClientConfig>.Ok(AttachClientConfig.Default);

        public AttachConfigResult<AttachClientConfig> Parse(string text) =>
            AttachConfigResult<AttachClientConfig>.Ok(AttachClientConfig.Default);

        public string DefaultToml() => "";

        public AttachConfigResult<AttachConfigResetResult> ResetKeys() =>
            AttachConfigResult<AttachConfigResetResult>.Ok(
                new AttachConfigResetResult("ok", null, null, false));

        public AttachConfigResult<AttachClientConfig> Patch(IReadOnlyList<AttachConfigAssignment> assignments) =>
            AttachConfigResult<AttachClientConfig>.Ok(AttachClientConfig.Default);
    }

    private sealed class ScriptedListPort : IAttachCommandPort
    {
        public int Lists { get; private set; }

        public ScriptedListFailure Fail { get; init; }

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (string.Equals(method, ProtocolMethods.IntegrationInstall, StringComparison.Ordinal))
            {
                return Task.FromResult(JsonDocument.Parse("{\"messages\":[\"installed claude\"]}").RootElement.Clone());
            }

            if (!string.Equals(method, ProtocolMethods.IntegrationList, StringComparison.Ordinal))
                return Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());

            Lists++;
            if (Fail is ScriptedListFailure.Cancelled)
            {
                throw new ControlPlaneException(
                    0,
                    AttachEndpointCommands.InterruptedMessage,
                    AttachEndpointCommands.CancelledCode);
            }

            if (Fail is ScriptedListFailure.Failed)
                throw new InvalidOperationException("request failed");

            return Task.FromResult(IntegrationList("claude", "current"));
        }
    }

    private sealed class EndpointListPort : IAttachCommandPort
    {
        private readonly AttachLiveState _live;

        public EndpointListPort(AttachLiveState live) => _live = live;

        public int Lists { get; private set; }

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (!string.Equals(method, ProtocolMethods.IntegrationList, StringComparison.Ordinal))
                return Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());

            Lists++;
            var target = string.Equals(_live.ActiveProjectionEndpointId, "cube-a", StringComparison.Ordinal)
                ? "codex"
                : "claude";
            return Task.FromResult(IntegrationList(target, "current"));
        }
    }

    private sealed class HoldingListPort : IAttachCommandPort
    {
        private readonly AttachLiveState _live;
        private int _inFlight;
        private int _calls;

        public HoldingListPort(AttachLiveState live) => _live = live;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxInFlight { get; private set; }

        public int Calls => Volatile.Read(ref _calls);

        public bool SecondCallStillHadPi { get; private set; }

        public bool SecondCallSawClaude { get; private set; }

        public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (!string.Equals(method, ProtocolMethods.IntegrationList, StringComparison.Ordinal))
                return JsonDocument.Parse("{}").RootElement.Clone();

            var flight = Interlocked.Increment(ref _inFlight);
            if (flight > MaxInFlight)
                MaxInFlight = flight;
            var endpoint = _live.ActiveProjectionEndpointId;
            var call = Interlocked.Increment(ref _calls);
            try
            {
                if (call == 1)
                {
                    Entered.TrySetResult();
                    await Release.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                else if (call == 2)
                {
                    SecondCallStillHadPi = _live.Engine.Settings.Integrations.Any(
                        status => status.Target == OfficialIntegrationTarget.Pi);
                    SecondCallSawClaude = _live.Engine.Settings.Integrations.Any(
                        status => status.Target == OfficialIntegrationTarget.Claude);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }

            var target = string.Equals(endpoint, "cube-a", StringComparison.Ordinal) ? "codex" : "claude";
            return IntegrationList(target, "current");
        }
    }

    private sealed class RecordingConfigLoader : IAttachConfigLoader
    {
        public AttachClientConfig Config { get; private set; } = new() { Onboarding = true };

        public List<AttachConfigAssignment> Patches { get; } = [];

        public string ResolvePath() => "config.toml";

        public AttachConfigResult<AttachClientConfig> Load() =>
            AttachConfigResult<AttachClientConfig>.Ok(Config);

        public AttachConfigResult<AttachClientConfig> Parse(string text) =>
            AttachConfigResult<AttachClientConfig>.Ok(Config);

        public string DefaultToml() => "";

        public AttachConfigResult<AttachConfigResetResult> ResetKeys() =>
            AttachConfigResult<AttachConfigResetResult>.Ok(
                new AttachConfigResetResult("ok", null, null, false));

        public AttachConfigResult<AttachClientConfig> Patch(IReadOnlyList<AttachConfigAssignment> assignments)
        {
            Patches.AddRange(assignments);
            foreach (var assignment in assignments)
            {
                if (assignment.Path == "onboarding" && assignment.TomlLiteral == "false")
                    Config = Config with { Onboarding = false };
            }

            return AttachConfigResult<AttachClientConfig>.Ok(Config);
        }
    }

    private sealed class HomeEnv : IIntegrationEnvironment
    {
        public required string UserHome { get; init; }

        public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

        public string? PathVariable { get; init; }

        public string? GetVariable(string name) =>
            Variables.TryGetValue(name, out var value) ? value : null;

        public bool FileIsExecutable(string path) => File.Exists(path);
    }

    private sealed class IntegrationHome : IDisposable
    {
        public IntegrationHome()
        {
            Home = Path.Combine(Path.GetTempPath(), "hypa-consent-" + Guid.NewGuid().ToString("N"));
            var bin = Path.Combine(Home, "bin");
            Directory.CreateDirectory(bin);
            Env = new HomeEnv { UserHome = Home, PathVariable = bin };
            Service = new OfficialIntegrationService(new SystemIntegrationFiles(), Env, new SystemIntegrationClock());
        }

        public string Home { get; }

        public HomeEnv Env { get; }

        public OfficialIntegrationService Service { get; }

        public void Command(string name) =>
            File.WriteAllText(Path.Combine(Home, "bin", name), "#!/bin/sh\n");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Home))
                    Directory.Delete(Home, recursive: true);
            }
            catch
            {
                // teardown
            }
        }
    }

    private sealed class ServiceIntegrationPort : IAttachCommandPort
    {
        private readonly OfficialIntegrationService _service;

        public ServiceIntegrationPort(OfficialIntegrationService service) => _service = service;

        public List<string> InstallTargets { get; } = [];

        public int InstallCalls { get; private set; }

        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            if (string.Equals(method, ProtocolMethods.IntegrationInstall, StringComparison.Ordinal))
            {
                InstallCalls++;
                var name = parameters?["target"]?.GetValue<string>() ?? "";
                InstallTargets.Add(name);
                if (!OfficialIntegrationTargets.TryParse(name, out var target))
                    throw new ControlPlaneException(-32602, "unknown integration target: " + name);
                var plan = parameters?["plan"] is JsonValue planNode && planNode.GetValue<bool>();
                var result = _service.Install(target, plan);
                if (!result.IsOk)
                    throw new ControlPlaneException(-32603, result.Error.Message);
                return Task.FromResult(ActionElement(result.Value));
            }

            if (string.Equals(method, ProtocolMethods.IntegrationUninstall, StringComparison.Ordinal))
            {
                var name = parameters?["target"]?.GetValue<string>() ?? "";
                if (!OfficialIntegrationTargets.TryParse(name, out var target))
                    throw new ControlPlaneException(-32602, "unknown integration target: " + name);
                var result = _service.Uninstall(target);
                if (!result.IsOk)
                    throw new ControlPlaneException(-32603, result.Error.Message);
                return Task.FromResult(ActionElement(result.Value));
            }

            if (string.Equals(method, ProtocolMethods.IntegrationList, StringComparison.Ordinal))
                return Task.FromResult(ListElement());

            return Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone());
        }

        private JsonElement ListElement()
        {
            var statuses = _service.ListStatuses();
            var items = new IntegrationInfoDto[statuses.Count];
            for (var i = 0; i < statuses.Count; i++)
            {
                var status = statuses[i];
                items[i] = new IntegrationInfoDto
                {
                    Target = status.Target.WireName(),
                    Label = status.Target.Label(),
                    Command = status.Target.CommandName(),
                    Available = status.Available,
                    State = status.State.WireName(),
                    InstalledVersion = status.InstalledVersion,
                    ExpectedVersion = status.ExpectedVersion,
                    Path = status.Path,
                    SkillState = status.SkillState.WireName(),
                    Consent = _service.ConsentLines(status.Target),
                };
            }

            return JsonSerializer.SerializeToElement(
                new IntegrationListResult { Integrations = items },
                ProtocolJsonContext.Default.IntegrationListResult);
        }

        private static JsonElement ActionElement(OfficialIntegrationActionResult result) =>
            JsonSerializer.SerializeToElement(
                new IntegrationActionResult
                {
                    Target = result.Target.WireName(),
                    Messages = result.Messages,
                },
                ProtocolJsonContext.Default.IntegrationActionResult);
    }
}
