using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;
using Hypa.Runtime.Domain.Common;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class ClientShellIndependentAttachTests
{
    [Fact]
    public async Task Two_client_shells_send_keys_to_one_pane_without_exclusive_lease()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var first = new FakeConnection("conn_a");
        var second = new FakeConnection("conn_b");
        try
        {
            await HelloAndActivateAsync(cp, first, "cli_a", columns: 120, rows: 40);
            await HelloAndActivateAsync(cp, second, "cli_b", columns: 80, rows: 24);
            await FocusAsync(cp, first, "cli_a", tabId, paneId);
            await FocusAsync(cp, second, "cli_b", tabId, paneId);

            var firstKeys = await SendKeysAsync(cp, first, paneId, "one");
            var secondKeys = await SendKeysAsync(cp, second, paneId, "two");
            Assert.Equal(3, firstKeys.GetProperty("accepted_bytes").GetInt32());
            Assert.Equal(3, secondKeys.GetProperty("accepted_bytes").GetInt32());
            await WaitWritesAsync(factory, 2);
            Assert.Contains("one", factory.Writes);
            Assert.Contains("two", factory.Writes);
            Assert.Null(cp.Leases.GetActive(paneId, LeaseScopes.Input));
            Assert.Null(cp.Leases.GetActive(paneId, LeaseScopes.Resize));

            var subA = await SubscribeAsync(cp, first);
            var subB = await SubscribeAsync(cp, second);
            Assert.Equal(first.ConnectionId, subA.GetProperty("attach_client_id").GetString());
            Assert.Equal(second.ConnectionId, subB.GetProperty("attach_client_id").GetString());
            Assert.NotEqual(
                subA.GetProperty("attach_client_id").GetString(),
                subB.GetProperty("attach_client_id").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Dest_observe_succeeds_while_another_client_shell_views_the_pane()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var shell = new FakeConnection("conn_shell");
        var dest = new FakeConnection("conn_dest");
        try
        {
            await HelloAndActivateAsync(cp, shell, "cli_shell");
            await FocusAsync(cp, shell, "cli_shell", tabId, paneId);
            Assert.Null(cp.Leases.GetActive(paneId, LeaseScopes.Input));

            var subscribed = await SubscribeAsync(cp, dest);
            var subId = subscribed.GetProperty("subscription_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(subId));
            await cp.DispatchAsync(
                ProtocolMethods.TerminalVisibleSet,
                Params(new JsonObject
                {
                    ["subscription_id"] = subId,
                    ["pane_ids"] = new JsonArray(paneId),
                }),
                dest,
                CancellationToken.None);
            var observed = await cp.DispatchAsync(
                ProtocolMethods.TerminalObserve,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["subscription_id"] = subId,
                    ["replace"] = true,
                }),
                dest,
                CancellationToken.None);
            Assert.Equal(paneId, observed.GetProperty("pane_id").GetString());
            Assert.Equal(AttachmentModes.Observe, observed.GetProperty("mode").GetString());
            Assert.Null(cp.Leases.GetActive(paneId, LeaseScopes.Input));
            Assert.Null(cp.Leases.GetActive(paneId, LeaseScopes.Resize));

            await SendKeysAsync(cp, shell, paneId, "stay");
            await WaitWritesAsync(factory, 1);
            Assert.Contains("stay", factory.Writes);
            Assert.NotNull(cp.AttachClientViews.Get(shell.ConnectionId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Non_shell_caller_still_needs_an_input_lease()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, _) = await SeedPaneAsync(factory);
        var plugin = new FakeConnection("conn_plugin");
        try
        {
            var missing = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                SendKeysAsync(cp, plugin, paneId, "x"));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, missing.Code);

            var claimed = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["scope"] = "input",
                    ["ttl_ms"] = 30_000,
                    ["takeover"] = false,
                }),
                plugin,
                CancellationToken.None);
            Assert.Equal("granted", claimed.GetProperty("outcome").GetString());
            var leaseId = claimed.GetProperty("lease_id").GetString()!;
            await SendKeysAsync(cp, plugin, paneId, "ok", leaseId);
            await WaitWritesAsync(factory, 1);
            Assert.Contains("ok", factory.Writes);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Terminal_control_still_rejects_a_second_controller_without_takeover()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, _) = await SeedPaneAsync(factory);
        var first = new FakeConnection("conn_ctrl_a");
        var second = new FakeConnection("conn_ctrl_b");
        try
        {
            var subA = await SubscribeAsync(cp, first);
            var granted = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["scope"] = "input",
                    ["ttl_ms"] = 30_000,
                    ["takeover"] = false,
                }),
                first,
                CancellationToken.None);
            var leaseId = granted.GetProperty("lease_id").GetString()!;
            await cp.DispatchAsync(
                ProtocolMethods.TerminalControl,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["lease_id"] = leaseId,
                    ["subscription_id"] = subA.GetProperty("subscription_id").GetString(),
                    ["replace"] = true,
                }),
                first,
                CancellationToken.None);

            var denied = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["scope"] = "input",
                    ["ttl_ms"] = 30_000,
                    ["takeover"] = false,
                }),
                second,
                CancellationToken.None);
            Assert.Equal("denied", denied.GetProperty("outcome").GetString());

            var foreign = await Assert.ThrowsAsync<ControlPlaneException>(() =>
                SendKeysAsync(cp, second, paneId, "no"));
            Assert.Equal(ProtocolErrorCodes.InvalidParams, foreign.Code);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Client_shell_focus_does_not_need_an_exclusive_lease()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var holder = new FakeConnection("conn_hold");
        var first = new FakeConnection("conn_a");
        var second = new FakeConnection("conn_b");
        try
        {
            var held = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["scope"] = "input",
                    ["ttl_ms"] = 30_000,
                    ["takeover"] = false,
                }),
                holder,
                CancellationToken.None);
            Assert.Equal("granted", held.GetProperty("outcome").GetString());

            await HelloAndActivateAsync(cp, first, "cli_a");
            await HelloAndActivateAsync(cp, second, "cli_b");
            var (tabTwo, paneTwo) = await CreateTabAsync(cp);

            await FocusAsync(cp, first, "cli_a", tabId, paneId);
            await FocusAsync(cp, second, "cli_b", tabTwo, paneTwo);

            var focused = await cp.DispatchAsync(
                ProtocolMethods.PaneFocus,
                Params(new JsonObject { ["pane_id"] = paneTwo }),
                second,
                CancellationToken.None);
            Assert.Equal(paneTwo, focused.GetProperty("pane_id").GetString());
            Assert.Equal(tabId, cp.AttachClientViews.Get(first.ConnectionId)!.FocusedTabId());
            Assert.Equal(tabTwo, cp.AttachClientViews.Get(second.ConnectionId)!.FocusedTabId());

            var snapA = await SnapshotAsync(cp, first);
            var snapB = await SnapshotAsync(cp, second);
            Assert.Equal(tabId, snapA.GetProperty("focused_tab_id").GetString());
            Assert.Equal(tabTwo, snapB.GetProperty("focused_tab_id").GetString());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Shared_tab_geometry_follows_last_interact_and_restores_on_detach()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var first = new FakeConnection("conn_a");
        var second = new FakeConnection("conn_b");
        try
        {
            await HelloAndActivateAsync(cp, first, "cli_a", columns: 120, rows: 40);
            await FocusAsync(cp, first, "cli_a", tabId, paneId);
            Assert.Equal((120, 40), factory.LastSize(paneId));
            Assert.True(cp.TabGeometry.IsController(first.ConnectionId, tabId));

            await HelloAndActivateAsync(cp, second, "cli_b", columns: 80, rows: 24);
            Assert.Equal((120, 40), factory.LastSize(paneId));
            Assert.True(cp.TabGeometry.IsController(first.ConnectionId, tabId));

            var ignored = await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["cols"] = 80,
                    ["rows"] = 24,
                }),
                second,
                CancellationToken.None);
            Assert.Equal(120, ignored.GetProperty("cols").GetInt32());
            Assert.Equal(40, ignored.GetProperty("rows").GetInt32());
            Assert.Equal((120, 40), factory.LastSize(paneId));

            await SendKeysAsync(cp, second, paneId, "x");
            await WaitWritesAsync(factory, 1);
            Assert.True(cp.TabGeometry.IsController(second.ConnectionId, tabId));
            Assert.Equal((80, 24), factory.LastSize(paneId));

            cp.OnClientDisconnected(second);
            Assert.True(cp.TabGeometry.IsController(first.ConnectionId, tabId));
            Assert.Equal((120, 40), factory.LastSize(paneId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Frozen_session_still_drops_leases_on_shell_disconnect()
    {
        var factory = new RecordingPaneFactory();
        var state = ReadyState();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory, state);
        var first = new FakeConnection("conn_a");
        var second = new FakeConnection("conn_b");
        try
        {
            await HelloAndActivateAsync(cp, first, "cli_a", columns: 120, rows: 40);
            await HelloAndActivateAsync(cp, second, "cli_b", columns: 80, rows: 24);
            await FocusAsync(cp, first, "cli_a", tabId, paneId);
            await FocusAsync(cp, second, "cli_b", tabId, paneId);

            var claimed = await cp.DispatchAsync(
                ProtocolMethods.RuntimeLeaseClaim,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["scope"] = "input",
                    ["ttl_ms"] = 30_000,
                    ["takeover"] = false,
                }),
                second,
                CancellationToken.None);
            Assert.Equal("granted", claimed.GetProperty("outcome").GetString());
            Assert.NotNull(cp.Leases.GetActive(paneId, LeaseScopes.Input));

            state.UpdateSession(s => s with { LifecycleState = SessionLifecycle.FrozenReadOnly });
            cp.OnClientDisconnected(second);
            Assert.Null(cp.Leases.GetActive(paneId, LeaseScopes.Input));

            var off = await cp.HandleAttachSurfaceInterestAsync(
                new AttachSurfaceInterestRequest
                {
                    RequestId = "off",
                    ClientId = "cli_a",
                    Active = false,
                    GeometryRevision = 1,
                },
                first,
                CancellationToken.None);
            Assert.False(off.GetProperty("active").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Published_pane_size_survives_controller_flip_and_detach()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var first = new FakeConnection("conn_a");
        var second = new FakeConnection("conn_b");
        try
        {
            await HelloAndActivateAsync(cp, first, "cli_a", columns: 120, rows: 40);
            await FocusAsync(cp, first, "cli_a", tabId, paneId);
            Assert.Equal((120, 40), factory.LastSize(paneId));

            var published = await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["cols"] = 118,
                    ["rows"] = 37,
                }),
                first,
                CancellationToken.None);
            Assert.Equal(118, published.GetProperty("cols").GetInt32());
            Assert.Equal(37, published.GetProperty("rows").GetInt32());
            Assert.Equal((118, 37), factory.LastSize(paneId));

            await HelloAndActivateAsync(cp, second, "cli_b", columns: 80, rows: 24);
            await FocusAsync(cp, second, "cli_b", tabId, paneId);
            await SendKeysAsync(cp, second, paneId, "x");
            await WaitWritesAsync(factory, 1);
            Assert.True(cp.TabGeometry.IsController(second.ConnectionId, tabId));
            Assert.Equal((80, 24), factory.LastSize(paneId));

            cp.OnClientDisconnected(second);
            Assert.True(cp.TabGeometry.IsController(first.ConnectionId, tabId));
            Assert.Equal((118, 37), factory.LastSize(paneId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Split_tab_geometry_does_not_give_every_pane_the_host_size()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var first = new FakeConnection("conn_a");
        try
        {
            var split = await cp.DispatchAsync(
                ProtocolMethods.PaneSplit,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["direction"] = "right",
                }),
                CancellationToken.None);
            var paneTwo = split.GetProperty("pane_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(paneTwo));

            await HelloAndActivateAsync(cp, first, "cli_a", columns: 120, rows: 40);
            await FocusAsync(cp, first, "cli_a", tabId, paneId);
            await SendKeysAsync(cp, first, paneId, "x");
            await WaitWritesAsync(factory, 1);

            var left = factory.LastSize(paneId);
            var right = factory.LastSize(paneTwo!);
            Assert.NotNull(left);
            Assert.NotNull(right);
            Assert.NotEqual((120, 40), left);
            Assert.NotEqual((120, 40), right);
            Assert.Equal(40, left.Value.Rows);
            Assert.Equal(40, right.Value.Rows);
            Assert.True(left.Value.Cols + right.Value.Cols <= 120);
            Assert.True(left.Value.Cols > 0);
            Assert.True(right.Value.Cols > 0);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Resize_of_an_unowned_tab_applies_the_content_box()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var client = new FakeConnection("conn_a");
        try
        {
            await HelloAndActivateAsync(cp, client, "cli_a", columns: 101, rows: 37);
            await FocusAsync(cp, client, "cli_a", tabId, paneId);
            var (tabTwo, paneTwo) = await CreateTabAsync(cp);
            await cp.DispatchAsync(
                ProtocolMethods.TabFocus,
                Params(new JsonObject { ["tab_id"] = tabTwo }),
                client,
                CancellationToken.None);
            Assert.Equal(tabTwo, cp.AttachClientViews.Get(client.ConnectionId)!.FocusedTabId());
            Assert.False(cp.TabGeometry.IsController(client.ConnectionId, tabTwo));
            Assert.Null(factory.LastSize(paneTwo));

            var resized = await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                Params(new JsonObject
                {
                    ["pane_id"] = paneTwo,
                    ["cols"] = 74,
                    ["rows"] = 36,
                }),
                client,
                CancellationToken.None);

            Assert.Equal(74, resized.GetProperty("cols").GetInt32());
            Assert.Equal(36, resized.GetProperty("rows").GetInt32());
            Assert.Equal((74, 36), factory.LastSize(paneTwo));
            Assert.True(cp.TabGeometry.IsController(client.ConnectionId, tabTwo));
            Assert.True(cp.TabGeometry.IsController(client.ConnectionId, tabId));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Resize_from_another_tab_does_not_claim_the_pane()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var client = new FakeConnection("conn_a");
        try
        {
            await HelloAndActivateAsync(cp, client, "cli_a", columns: 101, rows: 37);
            await FocusAsync(cp, client, "cli_a", tabId, paneId);
            var (tabTwo, paneTwo) = await CreateTabAsync(cp);
            Assert.Equal(tabId, cp.AttachClientViews.Get(client.ConnectionId)!.FocusedTabId());
            Assert.Null(factory.LastSize(paneTwo));

            var resized = await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                Params(new JsonObject
                {
                    ["pane_id"] = paneTwo,
                    ["cols"] = 74,
                    ["rows"] = 36,
                }),
                client,
                CancellationToken.None);

            Assert.NotEqual(74, resized.GetProperty("cols").GetInt32());
            Assert.NotEqual(36, resized.GetProperty("rows").GetInt32());
            Assert.Null(factory.LastSize(paneTwo));
            Assert.False(cp.TabGeometry.IsController(client.ConnectionId, tabTwo));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Overlay_owner_blocks_a_shell_resize_from_another_client()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var client = new FakeConnection("conn_a");
        try
        {
            await HelloAndActivateAsync(cp, client, "cli_a", columns: 101, rows: 37);
            await FocusAsync(cp, client, "cli_a", tabId, paneId);
            var before = factory.LastSize(paneId);
            cp.Overlay.RestoreOwner(new PaneOverlayOwner
            {
                PaneId = new PaneId(paneId),
                AttachClientId = "cli_other",
                Generation = 1,
                InnerCols = 20,
                InnerRows = 10,
            });

            var resized = await cp.DispatchAsync(
                ProtocolMethods.PaneResize,
                Params(new JsonObject
                {
                    ["pane_id"] = paneId,
                    ["cols"] = 74,
                    ["rows"] = 36,
                }),
                client,
                CancellationToken.None);

            Assert.NotEqual(74, resized.GetProperty("cols").GetInt32());
            Assert.NotEqual(36, resized.GetProperty("rows").GetInt32());
            Assert.Equal(before, factory.LastSize(paneId));
            Assert.Equal("cli_other", cp.Overlay.OwnerOf(new PaneId(paneId))!.AttachClientId);

            // The snapshot names the same owner as the declined resize, so the
            // client does not resend the resize on every refresh.
            var snapshot = await cp.DispatchAsync(
                ProtocolMethods.SessionSnapshot,
                Params(new JsonObject()),
                client,
                CancellationToken.None);
            var owner = snapshot.GetProperty("panes").EnumerateArray()
                .Single(p => p.GetProperty("pane_id").GetString() == paneId)
                .GetProperty("geometry_owner").GetString();
            Assert.Equal(resized.GetProperty("geometry_owner").GetString(), owner);
            Assert.Equal("cli_other", owner);
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Different_tabs_keep_independent_sizes()
    {
        var factory = new RecordingPaneFactory();
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        var first = new FakeConnection("conn_a");
        var second = new FakeConnection("conn_b");
        try
        {
            var (tabTwo, paneTwo) = await CreateTabAsync(cp);
            await HelloAndActivateAsync(cp, first, "cli_a", columns: 120, rows: 40);
            await FocusAsync(cp, first, "cli_a", tabId, paneId);
            await HelloAndActivateAsync(cp, second, "cli_b", columns: 80, rows: 24);
            await FocusAsync(cp, second, "cli_b", tabTwo, paneTwo);

            Assert.True(cp.TabGeometry.IsController(first.ConnectionId, tabId));
            Assert.True(cp.TabGeometry.IsController(second.ConnectionId, tabTwo));
            Assert.Equal((120, 40), factory.LastSize(paneId));
            Assert.Equal((80, 24), factory.LastSize(paneTwo));
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Two_socket_connections_share_one_mux_and_one_pane()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix sockets only.");

        var factory = new RecordingPaneFactory();
        var (dir, sock) = NewPrivateSocketPath("shell-share");
        var (cp, paneId, tabId) = await SeedPaneAsync(factory);
        try
        {
            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);
            await using var first = new ControlPlaneClient(sock, connectTimeout: TimeSpan.FromSeconds(2));
            await using var second = new ControlPlaneClient(sock, connectTimeout: TimeSpan.FromSeconds(2));
            await first.ConnectAsync();
            await second.ConnectAsync();

            await HelloAndActivateClientAsync(first, "cli_a", columns: 120, rows: 40);
            await HelloAndActivateClientAsync(second, "cli_b", columns: 80, rows: 24);
            await FocusClientAsync(first, "cli_a", tabId, paneId);
            await FocusClientAsync(second, "cli_b", tabId, paneId);

            await SendKeysClientAsync(first, paneId, "one");
            await SendKeysClientAsync(second, paneId, "two");
            await WaitWritesAsync(factory, 2);
            Assert.Contains("one", factory.Writes);
            Assert.Contains("two", factory.Writes);

            var subA = await SubscribeClientAsync(first);
            var subB = await SubscribeClientAsync(second);
            var idA = subA.GetProperty("attach_client_id").GetString();
            var idB = subB.GetProperty("attach_client_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(idA));
            Assert.False(string.IsNullOrWhiteSpace(idB));
            Assert.NotEqual(idA, idB);

            var pingA = await first.CallAsync(ProtocolMethods.Ping);
            var pingB = await second.CallAsync(ProtocolMethods.Ping);
            Assert.True(pingA.GetProperty("ok").GetBoolean());
            Assert.True(pingB.GetProperty("ok").GetBoolean());
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            TryDeleteTree(dir);
        }
    }

    [Fact]
    public async Task Destination_connect_after_health_expiry_passes_hello_with_new_generation()
    {
        Skip.If(
            !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS(),
            "Unix sockets only.");

        var factory = new RecordingPaneFactory();
        var (dir, sock) = NewPrivateSocketPath("shell-share");
        var (cp, _, _) = await SeedPaneAsync(factory);
        var envelope = new AttachEndpointTransportEnvelope();
        var clock = new ManualClock();
        await using var monitor = new AttachEndpointHealthMonitor(
            (_, _) => new TaskCompletionSource<AttachHealthResult>().Task,
            envelope,
            clock);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            monitor.Poll();
            clock.Advance(TimeSpan.FromSeconds(5));
            monitor.Poll();
            clock.Advance(TimeSpan.FromSeconds(5));
            monitor.Poll();
            Assert.True(envelope.IsInvalidated);

            await using var server = new UnixSocketServer(cp, sock);
            await server.StartAsync(CancellationToken.None);
            var service = new CubesConnectRetargetService(
                new MapCubesConnectEndpointResolver((_, _) =>
                    ValueTask.FromResult<IAttachEndpoint?>(new UnixAttachEndpoint(sock))));
            var outcome = await service.ConnectAsync(
                new CubesConnectRequest
                {
                    Destination = new SidebarCubeItem
                    {
                        Id = "remote",
                        Name = "docker",
                        Kind = SidebarCubeKind.Peer,
                        Reachability = SidebarCubeReachability.Reachable,
                    },
                    TargetTransportEnvelope = envelope,
                },
                CancellationToken.None);

            Assert.False(envelope.IsInvalidated, outcome.Detail);
            Assert.NotEqual("connection generation is invalidated", outcome.Detail);
            if (outcome.Ok)
            {
                Assert.Equal(outcome.TargetConnectionGeneration, envelope.Generation);
                if (outcome.DestClient is not null)
                    await outcome.DestClient.DisposeAsync();
            }
        }
        finally
        {
            await cp.ShutdownAsync(CancellationToken.None);
            TryDeleteTree(dir);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } =
            new(2026, 9, 23, 5, 26, 54, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;

        public void Advance(TimeSpan delta) => UtcNow += delta;
    }

    private static async Task<(ControlPlaneService Cp, string PaneId, string TabId)> SeedPaneAsync(
        IPaneRuntimeFactory factory,
        AppState? state = null)
    {
        state ??= ReadyState();
        var cp = new ControlPlaneService(
            state,
            factory,
            new PaneIntelligencePipeline(),
            new HeuristicAgentDetector(),
            journal: new MemoryJournal(),
            subscriptions: new EventSubscriptionHub());
        var created = await cp.DispatchAsync(
            ProtocolMethods.WorkspaceCreate,
            Params(new JsonObject
            {
                ["cwd"] = Path.GetTempPath(),
                ["create_pane"] = true,
            }),
            CancellationToken.None);
        var snap = await cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
        var paneId = created.GetProperty("pane").GetProperty("pane_id").GetString();
        var tabId = snap.GetProperty("focused_tab_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(paneId));
        Assert.False(string.IsNullOrWhiteSpace(tabId));
        return (cp, paneId!, tabId!);
    }

    private static AppState ReadyState()
    {
        var state = new AppState(SessionId.New("client-shell-attach"));
        state.UpdateSession(s => s with
        {
            LifecycleState = SessionLifecycle.Ready,
            Placement = "local",
        });
        return state;
    }

    private static async Task<(string TabId, string PaneId)> CreateTabAsync(ControlPlaneService cp)
    {
        var snap = await cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot, parameters: null, CancellationToken.None);
        var workspaceId = snap.GetProperty("focused_workspace_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(workspaceId));
        var created = await cp.DispatchAsync(
            ProtocolMethods.TabCreate,
            Params(new JsonObject
            {
                ["workspace_id"] = workspaceId,
                ["create_pane"] = true,
                ["focus"] = false,
            }),
            CancellationToken.None);
        var tabId = created.GetProperty("tab_id").GetString();
        var paneId = created.GetProperty("focused_pane_id").GetString()
            ?? created.GetProperty("pane").GetProperty("pane_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(tabId));
        Assert.False(string.IsNullOrWhiteSpace(paneId));
        return (tabId!, paneId!);
    }

    private static Task<JsonElement> SnapshotAsync(
        ControlPlaneService cp,
        IClientConnection connection) =>
        cp.DispatchAsync(
            ProtocolMethods.SessionSnapshot,
            parameters: null,
            connection,
            CancellationToken.None);

    private static async Task HelloAndActivateAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string clientId,
        ushort columns = 80,
        ushort rows = 24)
    {
        await cp.HandleAttachHelloAsync(
            SampleHello(clientId, columns, rows),
            connection,
            CancellationToken.None);
        await cp.HandleAttachSurfaceInterestAsync(
            new AttachSurfaceInterestRequest
            {
                RequestId = "on",
                ClientId = clientId,
                Active = true,
                GeometryRevision = 1,
            },
            connection,
            CancellationToken.None);
    }

    private static Task<JsonElement> FocusAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string clientId,
        string tabId,
        string paneId) =>
        cp.HandleAttachFocusAsync(
            new AttachFocusRequest
            {
                RequestId = "focus",
                ClientId = clientId,
                Focused = true,
                TabId = tabId,
                PaneId = paneId,
            },
            connection,
            CancellationToken.None);

    private static Task<JsonElement> SubscribeAsync(ControlPlaneService cp, IClientConnection connection) =>
        cp.DispatchAsync(
            ProtocolMethods.EventsSubscribe,
            Params(new JsonObject
            {
                ["from_seq"] = 0,
                ["live"] = true,
            }),
            connection,
            CancellationToken.None);

    private static Task<JsonElement> SendKeysAsync(
        ControlPlaneService cp,
        IClientConnection connection,
        string paneId,
        string text,
        string? leaseId = null)
    {
        var body = new JsonObject
        {
            ["pane_id"] = paneId,
            ["encoding"] = "utf8",
            ["data"] = text,
        };
        if (leaseId is not null)
            body["lease_id"] = leaseId;
        return cp.DispatchAsync(
            ProtocolMethods.PaneSendKeys,
            Params(body),
            connection,
            CancellationToken.None);
    }

    private static async Task HelloAndActivateClientAsync(
        ControlPlaneClient client,
        string clientId,
        ushort columns,
        ushort rows)
    {
        await client.CallAsync(ProtocolMethods.AttachHello, HelloParams(clientId, columns, rows));
        await client.CallAsync(
            ProtocolMethods.AttachSurfaceInterest,
            new JsonObject
            {
                ["request_id"] = "on",
                ["client_id"] = clientId,
                ["active"] = true,
                ["geometry_revision"] = 1,
            });
    }

    private static Task<JsonElement> FocusClientAsync(
        ControlPlaneClient client,
        string clientId,
        string tabId,
        string paneId) =>
        client.CallAsync(
            ProtocolMethods.AttachFocus,
            new JsonObject
            {
                ["request_id"] = "focus",
                ["client_id"] = clientId,
                ["focused"] = true,
                ["tab_id"] = tabId,
                ["pane_id"] = paneId,
            });

    private static Task<JsonElement> SubscribeClientAsync(ControlPlaneClient client) =>
        client.CallAsync(
            ProtocolMethods.EventsSubscribe,
            new JsonObject
            {
                ["from_seq"] = 0,
                ["live"] = true,
            });

    private static Task<JsonElement> SendKeysClientAsync(
        ControlPlaneClient client,
        string paneId,
        string text) =>
        client.CallAsync(
            ProtocolMethods.PaneSendKeys,
            new JsonObject
            {
                ["pane_id"] = paneId,
                ["encoding"] = "utf8",
                ["data"] = text,
            });

    private static JsonObject HelloParams(string clientId, ushort columns, ushort rows)
    {
        var capabilities = new JsonArray();
        foreach (var capability in AttachEndpointProtocol.RequiredCapabilities)
            capabilities.Add(capability);
        return new JsonObject
        {
            ["endpoint_generation"] = AttachEndpointProtocol.EndpointGeneration,
            ["protocol_major"] = 1,
            ["protocol_minor"] = 1,
            ["client_id"] = clientId,
            ["geometry"] = new JsonObject
            {
                ["columns"] = columns,
                ["rows"] = rows,
                ["cell_width_px"] = 8,
                ["cell_height_px"] = 16,
                ["geometry_revision"] = 1,
            },
            ["surface_active"] = false,
            ["snapshot_codecs"] = new JsonArray { AttachEndpointProtocol.SnapshotCodec },
            ["surface_codecs"] = new JsonArray { AttachEndpointProtocol.SurfaceCodec },
            ["input_codecs"] = new JsonArray { AttachEndpointProtocol.InputCodec },
            ["required_capabilities"] = capabilities,
        };
    }

    private static (string Dir, string Sock) NewPrivateSocketPath(string prefix)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine("/tmp", prefix + "-" + id);
        Directory.CreateDirectory(dir);
#pragma warning disable CA1416
        File.SetUnixFileMode(
            dir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        return (dir, Path.Combine(dir, "s.sock"));
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static async Task WaitWritesAsync(RecordingPaneFactory factory, int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (factory.Writes.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(
            factory.Writes.Count >= count,
            "expected " + count + " writes, had " + factory.Writes.Count);
    }

    private static JsonElement Params(JsonObject obj)
    {
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static AttachEndpointHello SampleHello(string clientId, ushort columns, ushort rows) =>
        new()
        {
            EndpointGeneration = AttachEndpointProtocol.EndpointGeneration,
            ProtocolMajor = 1,
            ProtocolMinor = 1,
            ClientId = clientId,
            Geometry = new AttachGeometry
            {
                Columns = columns,
                Rows = rows,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            },
            SurfaceActive = false,
            SnapshotCodecs = [AttachEndpointProtocol.SnapshotCodec],
            SurfaceCodecs = [AttachEndpointProtocol.SurfaceCodec],
            InputCodecs = [AttachEndpointProtocol.InputCodec],
            RequiredCapabilities = AttachEndpointProtocol.RequiredCapabilities.ToArray(),
        };

    private sealed class FakeConnection(string id) : IClientConnection
    {
        public string ConnectionId { get; } = id;

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingPaneFactory : IPaneRuntimeFactory
    {
        private readonly object _gate = new();
        private readonly List<string> _writes = [];
        private readonly Dictionary<string, (int Cols, int Rows)> _sizes = new(StringComparer.Ordinal);

        public IReadOnlyList<string> Writes
        {
            get { lock (_gate) return [.. _writes]; }
        }

        public (int Cols, int Rows)? LastSize(string paneId)
        {
            lock (_gate)
                return _sizes.TryGetValue(paneId, out var size) ? size : null;
        }

        public IPaneRuntime Create(PaneSpawnOptions options) => new RecordingPaneRuntime(this, options.Id);

        internal void RecordWrite(string text)
        {
            lock (_gate)
                _writes.Add(text);
        }

        internal void RecordSize(string paneId, int cols, int rows)
        {
            lock (_gate)
                _sizes[paneId] = (cols, rows);
        }

        private sealed class RecordingPaneRuntime(RecordingPaneFactory owner, PaneId id) : IPaneRuntime
        {
            public PaneId Id { get; } = id;
            public bool IsAlive { get; private set; } = true;
            public int? ExitCode { get; private set; }
            public int? Pid { get; private set; } = 42_012;
#pragma warning disable CS0067
            public event Action<IPaneRuntime, ReadOnlyMemory<byte>>? OutputReceived;
            public event Action<IPaneRuntime, int>? BellReceived { add { } remove { } }
            public event Action<IPaneRuntime, int>? Exited;
#pragma warning restore CS0067
            public Task StartAsync(CancellationToken ct)
            {
                IsAlive = true;
                return Task.CompletedTask;
            }

            public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(!IsAlive, this);
                owner.RecordWrite(Encoding.UTF8.GetString(data.Span));
                return ValueTask.CompletedTask;
            }

            public ValueTask WriteTextAsync(string text, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(!IsAlive, this);
                owner.RecordWrite(text);
                return ValueTask.CompletedTask;
            }

            public ValueTask ResizeAsync(int cols, int rows, CancellationToken ct)
            {
                ObjectDisposedException.ThrowIf(!IsAlive, this);
                owner.RecordSize(Id.Value, cols, rows);
                return ValueTask.CompletedTask;
            }

            public string ReadVisibleText() => "";
            public string ReadRecentText(int maxLines) => "";
            public string ReadRecentUnwrappedText(int maxLines) => "";
            public string ReadDetectionText() => "";
            public ValueTask DisposeAsync()
            {
                IsAlive = false;
                return ValueTask.CompletedTask;
            }
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
