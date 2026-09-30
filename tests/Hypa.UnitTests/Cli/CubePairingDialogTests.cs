using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.ControlPlane;
using Hypa.AgentRuntime.Application.Sidebar;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Chrome;
using Hypa.Cli.Attach.Copy;
using Hypa.Cli.Attach.Cubes;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Mouse;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Application;
using Hypa.Placement.Domain;
using Hypa.Placement.Infrastructure;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class CubePairingDialogTests
{
    [Fact]
    public void Add_dialog_types_invite_and_moves_focus()
    {
        var dialog = new CubePairingDialogModel();
        dialog.OpenAdd();
        Assert.True(dialog.IsOpen);
        Assert.Equal(nameof(CubeAddState.Invite), dialog.FocusedField());
        dialog.TypeIntoFocus("hypa-invite:abc");
        dialog.MoveFocus(1);
        dialog.TypeIntoFocus("Edge");
        Assert.Equal("hypa-invite:abc", dialog.Add!.Invite);
        Assert.Equal("Edge", dialog.Add.Label);
        Assert.True(dialog.Cancel());
        Assert.False(dialog.IsOpen);
    }

    [Fact]
    public void Share_dialog_restores_running_helper_fields()
    {
        var dialog = new CubePairingDialogModel();
        dialog.OpenShare(
            enabled: true,
            new CubeShareState
            {
                Running = true,
                BindHost = "0.0.0.0",
                Port = "7443",
                Invite = "hypa-invite:share",
            });
        Assert.True(dialog.Share!.Running);
        Assert.Equal("hypa-invite:share", dialog.Share.Invite);
        Assert.Equal(CubePairingDialogKind.Share, dialog.Kind);
    }

    [Fact]
    public void Painter_stamps_share_and_add_titles()
    {
        var share = new CubePairingDialogModel();
        share.OpenShare(true);
        var sharePaint = CubePairingDialogPainter.Paint(share, 80, 24);
        Assert.Contains(CubePairingDialogPainter.ShareTitle, sharePaint, StringComparison.Ordinal);
        Assert.DoesNotContain("share this mux", sharePaint, StringComparison.Ordinal);
        Assert.DoesNotContain("bind:", sharePaint, StringComparison.Ordinal);
        Assert.DoesNotContain("host:", sharePaint, StringComparison.Ordinal);
        Assert.DoesNotContain("port:", sharePaint, StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.StartHint, sharePaint, StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.AdvancedLabel, sharePaint, StringComparison.Ordinal);
        Assert.Equal("", share.Share!.AdvertiseHost);

        var add = new CubePairingDialogModel();
        add.OpenAdd();
        var addPaint = CubePairingDialogPainter.Paint(add, 80, 24);
        Assert.Contains(CubePairingDialogPainter.AddTitle, addPaint, StringComparison.Ordinal);
        Assert.Contains("hypa-invite:", addPaint, StringComparison.Ordinal);
    }

    [Fact]
    public void Share_painter_keeps_full_invite_and_copy_button()
    {
        var invite = "hypa-invite:" + new string('A', 180);
        var share = new CubePairingDialogModel();
        share.OpenShare(
            enabled: true,
            new CubeShareState
            {
                Running = true,
                AdvertiseHost = "127.0.0.1",
                Invite = invite,
            });
        var paint = CubePairingDialogPainter.Paint(share, 80, 24);
        Assert.Contains(invite, HostInviteFormat.Compact(paint), StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.CopyLabel, paint, StringComparison.Ordinal);
        Assert.DoesNotContain(CubePairingDialogPainter.CopiedLabel, paint, StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.CodeHint, paint, StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.ReachPrefix + "127.0.0.1:7443", paint, StringComparison.Ordinal);
        Assert.DoesNotContain("bind:", paint, StringComparison.Ordinal);
        var layout = CubePairingDialogPainter.Measure(share, 80, 24);
        Assert.True(layout.Copy.Cols > 0);
        Assert.True(layout.Invite.Rows >= 2);
    }

    [Fact]
    public void Cleared_advertise_field_names_no_address_even_when_the_environment_sets_one()
    {
        var model = new CubePairingDialogModel();
        model.OpenShare(enabled: true, restore: null, advertiseEnvironment: "192.0.2.10");
        Assert.Equal("192.0.2.10", model.Share!.AdvertiseHost);
        Assert.Equal("192.0.2.10", AttachSession.NamedAdvertiseHost(model.Share));

        model.Share.AdvertiseHost = "";
        Assert.Null(AttachSession.NamedAdvertiseHost(model.Share));
        model.Share.AdvertiseHost = "  198.51.100.4  ";
        Assert.Equal("198.51.100.4", AttachSession.NamedAdvertiseHost(model.Share));
    }

    [Fact]
    public void Advanced_share_pages_through_every_address_at_80_by_24()
    {
        var addresses = Enumerable.Range(1, 30).Select(i => "10.20.0." + i).ToList();
        var model = new CubePairingDialogModel();
        model.OpenShare(
            enabled: true,
            new CubeShareState { Advanced = true, Running = false },
            advertiseEnvironment: null,
            interfaceAddresses: addresses);

        var seen = new HashSet<string>();
        for (var page = 0; page < 30; page++)
        {
            var paint = CubePairingDialogPainter.Paint(model, 80, 24);
            foreach (var address in addresses)
            {
                if (paint.Contains(CubePairingDialogPainter.ReachPrefix + address + ":", StringComparison.Ordinal))
                    seen.Add(address);
            }

            Assert.Contains(CubePairingDialogPainter.PagingHint, paint, StringComparison.Ordinal);
            model.Share!.ReachScroll += CubePairingDialogPainter.ReachPageRows;
        }

        Assert.Equal(addresses.Count, seen.Count);
    }

    [Fact]
    public void Clearing_the_host_field_recomputes_the_address_preview()
    {
        var addresses = new[] { "192.168.1.10", "10.0.0.5" };
        var model = new CubePairingDialogModel();
        model.OpenShare(
            enabled: true,
            new CubeShareState { Advanced = true, Focus = 1 },
            advertiseEnvironment: "192.168.1.10",
            interfaceAddresses: addresses);
        Assert.Equal(new[] { "192.168.1.10" }, model.Share!.AdvertisedHosts);

        while (!string.IsNullOrEmpty(model.Share.AdvertiseHost))
            model.BackspaceFocus();

        Assert.Equal(addresses, model.Share.AdvertisedHosts);
    }

    [Fact]
    public void Invalid_advertise_environment_stays_in_the_field()
    {
        var model = new CubePairingDialogModel();
        model.OpenShare(enabled: true, restore: null, advertiseEnvironment: "not a host!");
        Assert.Equal("not a host!", model.Share!.AdvertiseHost);
        Assert.False(HostInviteReach.TryValidate(model.Share.AdvertiseHost, 7443, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task Clicking_share_invite_yanks_full_code()
    {
        var invite = "hypa-invite:" + new string('B', 80);
        var live = Live();
        AttachSession.OpenShareMuxDialog(live);
        live.CubesPairing.Share!.Running = true;
        live.CubesPairing.Share.Invite = invite;
        var layout = CubePairingDialogPainter.Measure(live.CubesPairing, 80, 24);
        live.CubesPairing.Layout = layout;
        await AttachSession.TryHandleCubePairingDialogMouseAsync(
            tty: null,
            live,
            [new MouseEvent(MouseButton.Left, MouseAction.Press, layout.Copy.Col, layout.Copy.Row)],
            new SilentPort(),
            CancellationToken.None);
        Assert.True(live.CubesPairing.Share.Copied);
        Assert.NotNull(live.LastOsc52);
        Assert.Equal(Osc52Yank.Encode(invite), live.LastOsc52);
    }

    [Fact]
    public async Task Advanced_toggle_reveals_and_hides_share_fields()
    {
        var live = Live();
        AttachSession.OpenShareMuxDialog(live);
        var simple = CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24);
        Assert.DoesNotContain("bind:", simple, StringComparison.Ordinal);
        var layout = live.CubesPairing.Layout
            ?? CubePairingDialogPainter.Measure(live.CubesPairing, 80, 24);
        await AttachSession.TryHandleCubePairingDialogMouseAsync(
            tty: null,
            live,
            [new MouseEvent(MouseButton.Left, MouseAction.Press, layout.Advanced.Col, layout.Advanced.Row)],
            new SilentPort(),
            CancellationToken.None);
        Assert.True(live.CubesPairing.Share!.Advanced);
        var advanced = CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24);
        Assert.Contains("bind:", advanced, StringComparison.Ordinal);
        Assert.Contains("host:", advanced, StringComparison.Ordinal);
        Assert.Contains("port:", advanced, StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.SimpleLabel, advanced, StringComparison.Ordinal);
        layout = live.CubesPairing.Layout
            ?? CubePairingDialogPainter.Measure(live.CubesPairing, 80, 24);
        await AttachSession.TryHandleCubePairingDialogMouseAsync(
            tty: null,
            live,
            [new MouseEvent(MouseButton.Left, MouseAction.Press, layout.Advanced.Col, layout.Advanced.Row)],
            new SilentPort(),
            CancellationToken.None);
        Assert.False(live.CubesPairing.Share.Advanced);
        var hidden = CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24);
        Assert.DoesNotContain("bind:", hidden, StringComparison.Ordinal);
    }

    [Fact]
    public void Share_drag_extracts_wrapped_invite()
    {
        var invite = "hypa-invite:" + new string('C', 180);
        var share = new CubePairingDialogModel();
        share.OpenShare(
            enabled: true,
            new CubeShareState
            {
                Running = true,
                AdvertiseHost = "127.0.0.1",
                Invite = invite,
            });
        CubePairingDialogPainter.Paint(share, 80, 24);
        var layout = share.Layout ?? CubePairingDialogPainter.Measure(share, 80, 24);
        Assert.True(layout.Invite.Rows >= 2);
        share.Selection.Begin(layout.Invite.Col, layout.Invite.Row);
        share.Selection.Extend(layout.Invite.EndCol - 1, layout.Invite.EndRow - 1);
        var extracted = HostInviteFormat.Compact(share.ExtractSelection());
        Assert.Contains(invite, extracted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drag_select_in_share_dialog_yanks_selection()
    {
        var invite = "hypa-invite:" + new string('D', 80);
        var live = Live();
        AttachSession.OpenShareMuxDialog(live);
        live.CubesPairing.Share!.Running = true;
        live.CubesPairing.Share.Invite = invite;
        CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24);
        var layout = live.CubesPairing.Layout
            ?? CubePairingDialogPainter.Measure(live.CubesPairing, 80, 24);
        live.CubesPairing.Layout = layout;
        var startCol = layout.Invite.Col;
        var startRow = layout.Invite.Row;
        var endCol = layout.Invite.EndCol - 1;
        Assert.True(await AttachSession.TryHandleCubePairingDialogMouseAsync(
            tty: null,
            live,
            [
                new MouseEvent(MouseButton.Left, MouseAction.Press, startCol, startRow),
                new MouseEvent(MouseButton.Left, MouseAction.Drag, endCol, startRow),
                new MouseEvent(MouseButton.Left, MouseAction.Release, endCol, startRow),
            ],
            new SilentPort(),
            CancellationToken.None));
        Assert.False(live.Mouse.Selection.HostRange);
        Assert.NotNull(live.LastOsc52);
        var extracted = live.CubesPairing.ExtractSelection();
        Assert.Contains("hypa-invite:", HostInviteFormat.Compact(extracted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clicking_invite_does_not_yank()
    {
        var invite = "hypa-invite:" + new string('F', 40);
        var live = Live();
        AttachSession.OpenShareMuxDialog(live);
        live.CubesPairing.Share!.Running = true;
        live.CubesPairing.Share.Invite = invite;
        CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24);
        var layout = live.CubesPairing.Layout
            ?? CubePairingDialogPainter.Measure(live.CubesPairing, 80, 24);
        live.CubesPairing.Layout = layout;
        Assert.True(await AttachSession.TryHandleCubePairingDialogMouseAsync(
            tty: null,
            live,
            [
                new MouseEvent(MouseButton.Left, MouseAction.Press, layout.Invite.Col, layout.Invite.Row),
                new MouseEvent(MouseButton.Left, MouseAction.Release, layout.Invite.Col, layout.Invite.Row),
            ],
            new SilentPort(),
            CancellationToken.None));
        Assert.False(live.CubesPairing.Share.Copied);
        Assert.Null(live.LastOsc52);
        Assert.False(live.Mouse.Selection.HostRange);
        Assert.False(live.CubesPairing.Selection.Active);
    }

    [Fact]
    public async Task Start_release_on_copy_does_not_yank()
    {
        var invite = "hypa-invite:" + new string('G', 40);
        var live = Live();
        AttachSession.OpenShareMuxDialog(live);
        live.CubesPairing.Share!.Running = true;
        live.CubesPairing.Share.Invite = invite;
        CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24);
        var layout = live.CubesPairing.Layout
            ?? CubePairingDialogPainter.Measure(live.CubesPairing, 80, 24);
        live.CubesPairing.Layout = layout;
        live.CubesPairing.IgnoreNextRelease = true;
        await AttachSession.TryHandleCubePairingDialogMouseAsync(
            tty: null,
            live,
            [new MouseEvent(MouseButton.Left, MouseAction.Release, layout.Copy.Col, layout.Copy.Row)],
            new SilentPort(),
            CancellationToken.None);
        Assert.False(live.CubesPairing.Share.Copied);
        Assert.Null(live.LastOsc52);
        Assert.False(live.CubesPairing.IgnoreNextRelease);
    }

    [Fact]
    public void Share_painter_wraps_full_start_error()
    {
        var error = AcceptHelperProcess.FormatStartError(
            "Unhandled exception: System.Net.Sockets.SocketException (98): Address already in use",
            7443);
        Assert.Equal("port 7443 is already in use", error);
        var share = new CubePairingDialogModel();
        share.OpenShare(true, new CubeShareState { Error = error + ". Open advanced and pick another port." });
        var paint = CubePairingDialogPainter.Paint(share, 80, 24);
        Assert.Contains("port 7443 is already in use", paint, StringComparison.Ordinal);
        Assert.Contains("Open advanced and pick another port.", paint, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", paint, StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_start_info_does_not_inherit_stdin()
    {
        var start = AcceptHelperProcess.CreateStartInfo(
            "/opt/hypa/hypa",
            "default",
            "0.0.0.0",
            7443,
            ["192.168.1.10", "203.0.113.8"]);
        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.False(start.UseShellExecute);
        Assert.Equal("connectivity", start.ArgumentList[0]);
        Assert.Equal("accept", start.ArgumentList[1]);
        var args = start.ArgumentList.ToArray();
        var first = Array.IndexOf(args, "--advertise-host");
        var second = Array.IndexOf(args, "--advertise-host", first + 1);
        Assert.Equal("192.168.1.10", args[first + 1]);
        Assert.Equal("203.0.113.8", args[second + 1]);
    }

    [Fact]
    public void Share_dialog_lists_every_address_and_shows_all_addresses()
    {
        var dialog = new CubePairingDialogModel();
        dialog.OpenShare(
            enabled: true,
            new CubeShareState
            {
                Running = true,
                Advanced = true,
                BindHost = "0.0.0.0",
                AdvertiseHost = "",
                Port = "7443",
                Invite = "hypa-invite:reach",
                AdvertisedHosts = ["192.168.1.10", "203.0.113.8"],
            });
        var paint = CubePairingDialogPainter.Paint(dialog, 80, 24);
        Assert.Equal(24, paint.Split('\n').Length);
        Assert.Equal(80, paint.Split('\n')[0].Length);
        Assert.Contains("host: " + CubePairingDialogPainter.AllAddressesText, paint, StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.ReachPrefix + "192.168.1.10:7443", paint, StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.ReachPrefix + "203.0.113.8:7443", paint, StringComparison.Ordinal);
        Assert.Equal("", dialog.Share!.AdvertiseHost);

        dialog.Share.Focus = 1;
        dialog.TypeIntoFocus("10.0.0.8");
        var limited = CubePairingDialogPainter.Paint(dialog, 80, 24);
        Assert.Equal(24, limited.Split('\n').Length);
        Assert.Contains("host: 10.0.0.8", limited, StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.LimitedReachText, limited, StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.ReachPrefix + "10.0.0.8:7443", limited, StringComparison.Ordinal);
        Assert.DoesNotContain(CubePairingDialogPainter.ReachPrefix + "192.168.1.10:7443", limited, StringComparison.Ordinal);
    }

    [Fact]
    public void Occupied_listener_does_not_start_a_second_helper()
    {
        var occupied = ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
            ConnectivityReasons.BootstrapInvalid,
            "port 7443 is already in use");
        Assert.False(ExistingAcceptShare.ShouldStartHelper(occupied));

        var missing = ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
            ConnectivityReasons.BootstrapInvalid,
            "accept certificate is missing");
        Assert.True(ExistingAcceptShare.ShouldStartHelper(missing));

        var idle = ConnectivityOutcome<ConnectivityAcceptListenDocument>.Failure(
            ConnectivityReasons.PeerUnavailable,
            ExistingAcceptShare.NotListeningDetail);
        Assert.True(ExistingAcceptShare.ShouldStartHelper(idle));
    }

    [Fact]
    public void Connection_refused_is_not_an_occupied_port()
    {
        var refused = new System.Net.Sockets.SocketException(
            (int)System.Net.Sockets.SocketError.ConnectionRefused);
        Assert.True(ExistingAcceptShare.IsNotListeningSocket(refused));
        var inUse = new System.Net.Sockets.SocketException(
            (int)System.Net.Sockets.SocketError.AddressAlreadyInUse);
        Assert.False(ExistingAcceptShare.IsNotListeningSocket(inUse));
    }

    [Fact]
    public async Task Closed_loopback_port_refuses_fingerprint_read()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var ex = await Assert.ThrowsAsync<SocketException>(
            () => ExistingAcceptShare.ReadListenerFingerprintAsync(port, CancellationToken.None));
        Assert.True(ExistingAcceptShare.IsNotListeningSocket(ex));
    }

    [Fact]
    public async Task Fingerprint_read_matches_live_accept_certificate()
    {
        using var cert = AcceptListenCertificate.CreateSelfSigned(IPAddress.Loopback);
        var expected = AcceptListenCertificate.Sha256Fingerprint(cert);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                await using var stream = accepted.GetStream();
                var wrapped = await RendezvousTls.WrapServerAsync(
                    stream,
                    cert,
                    CancellationToken.None);
                if (wrapped.Value is { } ssl)
                    await ssl.DisposeAsync();
            });
            var presented = await ExistingAcceptShare.ReadListenerFingerprintAsync(
                port,
                CancellationToken.None);
            Assert.Equal(expected, presented);
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Listener_bind_matches_only_the_address_that_owns_the_port()
    {
        using var wildcard = Listen(IPAddress.Any);
        using var loopback = Listen(IPAddress.Loopback);
        Assert.True(ExistingAcceptShare.ListenerBindMatches("0.0.0.0", PortOf(wildcard)));
        Assert.False(ExistingAcceptShare.ListenerBindMatches("127.0.0.1", PortOf(wildcard)));
        Assert.True(ExistingAcceptShare.ListenerBindMatches("127.0.0.1", PortOf(loopback)));
        Assert.False(ExistingAcceptShare.ListenerBindMatches("0.0.0.0", PortOf(loopback)));
    }

    [Fact]
    public async Task Wildcard_listener_is_not_reused_for_a_loopback_bind()
    {
        var session = "bind" + Guid.NewGuid().ToString("N")[..12];
        var socketPath = UnixSocketServer.ResolveSocketPath(session, honorEnvironment: false);
        var sessionDir = Path.GetDirectoryName(socketPath)!;
        var pairing = Path.Combine(Path.GetTempPath(), "hypa-bind-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable(DevicePairingPaths.PairingStoreVariable);
        Directory.CreateDirectory(pairing);
        Environment.SetEnvironmentVariable(DevicePairingPaths.PairingStoreVariable, pairing);
        var pfx = AcceptListenCertificate.CreateSelfSignedPfx(IPAddress.Any);
        using var cert = X509CertificateLoader.LoadPkcs12(
            pfx,
            AcceptListenCertificate.DefaultPfxPassword,
            X509KeyStorageFlags.Exportable);
        AcceptListenCertificate.WritePfx(Path.Combine(sessionDir, "accept.pfx"), pfx);
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                await using var stream = accepted.GetStream();
                var wrapped = await RendezvousTls.WrapServerAsync(
                    stream,
                    cert,
                    CancellationToken.None);
                if (wrapped.Value is { } ssl)
                    await ssl.DisposeAsync();
            });
            var reused = await ExistingAcceptShare.TryIssueAsync(
                session,
                "127.0.0.1",
                port,
                ["192.0.2.10"],
                CancellationToken.None);
            Assert.False(reused.Ok);
            Assert.Equal("listener bind differs", reused.Detail);
            Assert.False(ExistingAcceptShare.ShouldStartHelper(reused));
            Assert.False(File.Exists(Path.Combine(pairing, DevicePairingPaths.StoreFileName)));
            await server;
        }
        finally
        {
            listener.Stop();
            Environment.SetEnvironmentVariable(DevicePairingPaths.PairingStoreVariable, previous);
            try { Directory.Delete(sessionDir, recursive: true); }
            catch (IOException) { }
            try { Directory.Delete(pairing, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Share_open_uses_the_supplied_reach_catalog()
    {
        var live = Live();
        live.HostReach = new FixedHostReachCatalog(["203.0.113.9", "192.0.2.8"]);
        AttachSession.OpenShareMuxDialog(live);
        Assert.Equal(
            ["203.0.113.9", "192.0.2.8"],
            live.CubesPairing.Share!.AdvertisedHosts);
    }

    [Fact]
    public async Task Drag_select_error_text_does_not_mark_copied()
    {
        var live = Live();
        AttachSession.OpenShareMuxDialog(live);
        live.CubesPairing.Share!.Error = "port is invalid";
        CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24);
        var layout = live.CubesPairing.Layout
            ?? CubePairingDialogPainter.Measure(live.CubesPairing, 80, 24);
        live.CubesPairing.Layout = layout;
        var row = layout.Panel.Row + 3;
        var startCol = layout.Panel.Col + 2;
        Assert.True(await AttachSession.TryHandleCubePairingDialogMouseAsync(
            tty: null,
            live,
            [
                new MouseEvent(MouseButton.Left, MouseAction.Press, startCol, row),
                new MouseEvent(MouseButton.Left, MouseAction.Drag, startCol + 12, row),
                new MouseEvent(MouseButton.Left, MouseAction.Release, startCol + 12, row),
            ],
            new SilentPort(),
            CancellationToken.None));
        Assert.False(live.Mouse.Selection.HostRange);
        Assert.False(live.CubesPairing.Share.Copied);
        Assert.NotNull(live.LastOsc52);
        Assert.Contains("port", live.CubesPairing.ExtractSelection(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drag_starting_outside_share_does_not_host_select()
    {
        var invite = "hypa-invite:" + new string('H', 40);
        var live = Live();
        AttachSession.OpenShareMuxDialog(live);
        live.CubesPairing.Share!.Running = true;
        live.CubesPairing.Share.Invite = invite;
        CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24);
        var layout = live.CubesPairing.Layout
            ?? CubePairingDialogPainter.Measure(live.CubesPairing, 80, 24);
        live.CubesPairing.Layout = layout;
        Assert.True(await AttachSession.TryHandleCubePairingDialogMouseAsync(
            tty: null,
            live,
            [
                new MouseEvent(MouseButton.Left, MouseAction.Press, 0, 0),
                new MouseEvent(MouseButton.Left, MouseAction.Drag, layout.Invite.Col, layout.Invite.Row),
                new MouseEvent(MouseButton.Left, MouseAction.Release, layout.Invite.Col, layout.Invite.Row),
            ],
            new SilentPort(),
            CancellationToken.None));
        Assert.False(live.Mouse.Selection.Active);
        Assert.False(live.Mouse.Selection.HostRange);
        Assert.False(live.CubesPairing.Selection.Active);
    }

    [Fact]
    public async Task Drag_from_invite_stays_inside_panel()
    {
        var invite = "hypa-invite:" + new string('I', 40);
        var live = Live();
        AttachSession.OpenShareMuxDialog(live);
        live.CubesPairing.Share!.Running = true;
        live.CubesPairing.Share.Invite = invite;
        CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24);
        var layout = live.CubesPairing.Layout
            ?? CubePairingDialogPainter.Measure(live.CubesPairing, 80, 24);
        live.CubesPairing.Layout = layout;
        Assert.True(await AttachSession.TryHandleCubePairingDialogMouseAsync(
            tty: null,
            live,
            [
                new MouseEvent(MouseButton.Left, MouseAction.Press, layout.Invite.Col, layout.Invite.Row),
                new MouseEvent(MouseButton.Left, MouseAction.Drag, 0, 0),
                new MouseEvent(MouseButton.Left, MouseAction.Release, 0, 0),
            ],
            new SilentPort(),
            CancellationToken.None));
        Assert.False(live.Mouse.Selection.HostRange);
        Assert.True(live.CubesPairing.Selection.Active);
        Assert.True(layout.Panel.Contains(
            live.CubesPairing.Selection.EndCol,
            live.CubesPairing.Selection.EndRow));
    }

    [Fact]
    public void Paired_share_has_no_copy_hit()
    {
        var share = new CubePairingDialogModel();
        share.OpenShare(
            enabled: true,
            new CubeShareState
            {
                Running = true,
                Invite = "hypa-invite:pair",
                Paired = true,
            });
        var layout = CubePairingDialogPainter.Measure(share, 80, 24);
        Assert.Equal(0, layout.Copy.Cols);
        Assert.Equal(0, layout.Primary.Cols);
    }

    [Fact]
    public async Task Drag_select_in_add_dialog_yanks_code_field()
    {
        var live = Live();
        AttachSession.OpenAddCubeDialog(live);
        live.CubesPairing.TypeIntoFocus("hypa-invite:" + new string('E', 40));
        CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24);
        var layout = live.CubesPairing.Layout
            ?? CubePairingDialogPainter.Measure(live.CubesPairing, 80, 24);
        live.CubesPairing.Layout = layout;
        var row = layout.Panel.Row + 3;
        var startCol = layout.Panel.Col + 2;
        Assert.True(await AttachSession.TryHandleCubePairingDialogMouseAsync(
            tty: null,
            live,
            [
                new MouseEvent(MouseButton.Left, MouseAction.Press, startCol, row),
                new MouseEvent(MouseButton.Left, MouseAction.Drag, startCol + 20, row),
                new MouseEvent(MouseButton.Left, MouseAction.Release, startCol + 20, row),
            ],
            new SilentPort(),
            CancellationToken.None));
        Assert.False(live.Mouse.Selection.HostRange);
        Assert.NotNull(live.LastOsc52);
        var extracted = live.CubesPairing.ExtractSelection();
        Assert.Contains("hypa-invite:", HostInviteFormat.Compact(extracted), StringComparison.Ordinal);
    }

    [Fact]
    public void Share_painter_shows_paired_and_hides_invite()
    {
        var invite = "hypa-invite:" + new string('P', 40);
        var share = new CubePairingDialogModel();
        share.OpenShare(
            enabled: true,
            new CubeShareState
            {
                Running = true,
                Invite = invite,
                Paired = true,
            });
        var paint = CubePairingDialogPainter.Paint(share, 80, 24);
        Assert.Contains(CubePairingDialogPainter.PairedLabel, paint, StringComparison.Ordinal);
        Assert.Contains(CubePairingDialogPainter.PairedHint, paint, StringComparison.Ordinal);
        Assert.DoesNotContain(invite, paint, StringComparison.Ordinal);
        Assert.DoesNotContain(CubePairingDialogPainter.CopyLabel, paint, StringComparison.Ordinal);
        Assert.DoesNotContain(CubePairingDialogPainter.StartLabel, paint, StringComparison.Ordinal);
        Assert.DoesNotContain(CubePairingDialogPainter.StopLabel, paint, StringComparison.Ordinal);
    }

    [Fact]
    public void Share_watch_marks_paired_then_dismisses()
    {
        var share = new CubeShareState { Running = true };
        var now = DateTimeOffset.Parse("2026-09-14T06:00:00Z");
        Assert.True(CubeSharePairingWatch.TryMarkPaired(share, now));
        Assert.True(share.Paired);
        Assert.False(CubeSharePairingWatch.ShouldDismiss(share, now));
        Assert.True(CubeSharePairingWatch.ShouldDismiss(share, now + CubeSharePairingWatch.DismissAfter));
    }

    [Fact]
    public async Task Share_flush_shows_paired_then_closes_dialog()
    {
        var live = Live();
        AttachSession.OpenShareMuxDialog(live);
        live.CubesPairing.Share!.Running = true;
        live.CubesPairing.Share.Invite = "hypa-invite:pair";
        var now = DateTimeOffset.Parse("2026-09-14T06:00:00Z");
        live.CubesPairing.Share.Paired = true;
        live.CubesPairing.Share.PairedAt = now;
        Assert.True(
            await AttachSession.TryFlushSharePairingAsync(
                live,
                tty: null,
                controlGate: null,
                CancellationToken.None,
                now));
        Assert.True(live.CubesPairing.IsOpen);
        Assert.True(live.CubesPairing.Share!.PairedShown);
        Assert.True(
            await AttachSession.TryFlushSharePairingAsync(
                live,
                tty: null,
                controlGate: null,
                CancellationToken.None,
                now + CubeSharePairingWatch.DismissAfter));
        Assert.False(live.CubesPairing.IsOpen);
        Assert.False(live.Engine.ExclusiveSurfaceOpen);
    }

    [Fact]
    public void Chrome_hit_apply_opens_add_and_share()
    {
        var add = ChromeHitApply.Apply(
            new ChromeHit(ChromeHitKind.SidebarAddCube),
            overflowOffset: 0,
            maxOverflowOffset: 0);
        Assert.True(add.OpenAddCube);
        var share = ChromeHitApply.Apply(
            new ChromeHit(ChromeHitKind.SidebarShareMux),
            overflowOffset: 0,
            maxOverflowOffset: 0);
        Assert.True(share.OpenShareMux);
    }

    [Fact]
    public void Cube_card_hangs_reachability_under_name()
    {
        var view = new CubesChromeSectionStrategy().Compose(
            new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                Expanded = true,
                RequestedWidth = 26,
                Cubes =
                [
                    new SidebarCubeItem
                    {
                        Id = "plc_intel",
                        Name = "Intel Mac",
                        Kind = SidebarCubeKind.Local,
                        Reachability = SidebarCubeReachability.Local,
                    },
                ],
            },
            new ResolvedSidebarSection
            {
                Id = SidebarTokenGrammar.CubesId,
                Title = "Cubes",
                Order = 0,
                Collapsed = false,
                Config = AttachSidebarSectionConfig.CubesDefault,
            },
            collapsed: false,
            visible: true,
            width: 26,
            SidebarCollapseDisplay.Expanded);
        var rows = view.Rows.Where(row => row.Id == "plc_intel").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Contains("Intel Mac", rows[0].Label, StringComparison.Ordinal);
        Assert.Contains("local", rows[1].Label, StringComparison.Ordinal);
        Assert.Equal(0, rows[0].CardRowIndex);
        Assert.Equal(1, rows[1].CardRowIndex);
        Assert.True(rows[1].NameColumn > rows[0].IndentCols);
        Assert.Equal(rows[1].NameColumn, rows[1].IndentCols);
        Assert.Equal(
            rows[0].IndentCols + SidebarSectionComposer.CardHangingIndentCols,
            rows[1].NameColumn);
    }

    [Fact]
    public void Cubes_header_paints_add_and_share_actions()
    {
        var view = new CubesChromeSectionStrategy().Compose(
            new SidebarComposeInput
            {
                Ui = AttachUiConfig.Default,
                Expanded = true,
                RequestedWidth = 28,
            },
            new ResolvedSidebarSection
            {
                Id = SidebarTokenGrammar.CubesId,
                Title = "cubes",
                Order = 0,
                Collapsed = false,
                Config = new AttachSidebarSectionConfig(),
            },
            collapsed: false,
            visible: true,
            width: 28,
            SidebarCollapseDisplay.Expanded);
        Assert.Contains(view.Actions, action => action.Id == "add_cube");
        Assert.Contains(view.Actions, action => action.Id == "share_mux");
    }

    [Fact]
    public void Move_work_is_disabled_when_continuity_is_off()
    {
        var cube = new SidebarCubeItem
        {
            Id = "plc_peer001",
            Name = "Peer",
            Kind = SidebarCubeKind.Peer,
            Reachability = SidebarCubeReachability.Reachable,
        };
        var actions = SidebarCubeRowActions.ForCube(cube, continuityEnabled: false);
        Assert.Contains(
            actions,
            action => action.Id == SidebarCubeRowActions.MoveWork && !action.Enabled);
        Assert.Contains(
            actions,
            action => action.Id == SidebarCubeRowActions.Connect && action.Enabled);
    }

    [Fact]
    public void Cubes_header_actions_hit_add_and_share()
    {
        var pane = new SidebarPaneView
        {
            Id = SidebarTokenGrammar.CubesId,
            Slot = SidebarPaneSlot.Cubes,
            Header = CubesChromeSectionStrategy.HeaderText,
            Visible = true,
            Actions =
            [
                new SidebarActionHit("add_cube", " add", SidebarActionAlign.Left, 5),
                new SidebarActionHit("share_mux", " share", SidebarActionAlign.Right, 7),
            ],
        };
        var frame = new SidebarFrame
        {
            Width = 26,
            Display = SidebarCollapseDisplay.Expanded,
            Panes = [pane],
        };
        var sidebar = new CellRect(0, 0, 26, 24);
        var layout = SidebarTwoPaneLayoutPolicy.Expanded(sidebar, resourceSectionIds: [SidebarTokenGrammar.CubesId]);
        var hits = SidebarHitModel.Hits(sidebar, layout, frame, spacesScroll: 0, agentsScroll: 0);
        var add = Assert.Single(hits, hit => hit.Kind is SidebarStubKind.AddCube);
        var share = Assert.Single(hits, hit => hit.Kind is SidebarStubKind.ShareMux);
        var cubes = layout.RectFor(SidebarPaneSlot.Cubes);
        Assert.Equal(cubes.EndRow - 1, add.Rect.Row);
        Assert.Equal(cubes.EndRow - 1, share.Rect.Row);
        Assert.True(add.Rect.Row > cubes.Row);
    }

    [Fact]
    public async Task Failed_add_revokes_local_enrollment()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-add-rev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pairing = new DevicePairingService(
                new FileDevicePairingStore(dir),
                new FileDeviceKeyStore(dir));
            var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
            Assert.True(started.Ok);
            Assert.True(InviteId.TryParse("inv_failedadd", out var inviteId));
            var invite = new HostInvite
            {
                InviteId = inviteId,
                Secret = "secret",
                Host = "127.0.0.1",
                Port = 7443,
                CertificateSha256 = "not-a-fingerprint",
                ExpiresAt = DateTimeOffset.UtcNow,
                TransferValue = "hypa-invite:failed",
            };
            await AttachSession.RevokeRedeemedEnrollmentAsync(
                pairing,
                invite,
                started.Value!.DeviceId,
                CancellationToken.None);
            var listed = await pairing.ListAsync(OperatorIdentity.LocalSelfHosted);
            Assert.True(listed.Ok);
            var device = Assert.Single(listed.Value!, row => row.Id == started.Value.DeviceId);
            Assert.Equal(DeviceTrustStatus.Revoked, device.Status);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Remove_unenrolled_cube_drops_catalog_row()
    {
        var root = Path.Combine(Path.GetTempPath(), "hypa-rm-cube-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", root);
        try
        {
            Assert.True(ProcessLocalOperatorIdentity.TryResolve(out var owner));
            var store = new FilePlacementDirectoryStore(PlacementStatePaths.ResolveFromEnvironment());
            var directory = new PlacementDirectoryService(store);
            var registered = await directory.RegisterAsync(new PlacementRegistration
            {
                Owner = owner,
                DisplayName = "Arm Mac",
                Kind = PlacementDirectoryKind.Local,
                MuxIdentity = MuxIdentity.Parse("mux_armmac1"),
            });
            Assert.True(registered.Ok, registered.Detail);
            var live = Live();
            live.Cubes =
            [
                new SidebarCubeItem
                {
                    Id = registered.Value!.Id.Value,
                    Name = "Arm Mac",
                    Kind = SidebarCubeKind.Local,
                    Reachability = SidebarCubeReachability.Local,
                },
            ];
            live.StatusError = "device is not enrolled";
            await AttachSession.RevokeCubeDeviceAsync(
                registered.Value.Id.Value,
                live,
                tty: null,
                CancellationToken.None);
            Assert.Null(live.StatusError);
            Assert.DoesNotContain(live.Cubes, cube => cube.Id == registered.Value.Id.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", previous);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Input_clears_stuck_status_error()
    {
        var live = Live();
        live.StatusError = "Received unexpected EOF or 0 bytes from the transport stream.";
        Assert.True(AttachSession.DismissStatusErrorOnInput(live));
        Assert.Null(live.StatusError);
        Assert.False(AttachSession.DismissStatusErrorOnInput(live));
    }

    [Fact]
    public void Open_add_marks_exclusive_surface()
    {
        var live = Live();
        AttachSession.OpenAddCubeDialog(live);
        Assert.True(live.CubesPairing.IsOpen);
        Assert.True(live.Engine.ExclusiveSurfaceOpen);
        Assert.Equal(AttachClientModePublication.CubesPairingToken, live.Dispatcher.ClientMode);
    }

    [Fact]
    public void Open_share_stays_enabled_without_peer_paint()
    {
        var live = Live();
        AttachSession.OpenShareMuxDialog(live);
        Assert.True(live.CubesPairing.Share!.ShareEnabled);
        Assert.True(AttachClientModePublication.HumanExclusiveSurfaceOpen(live));
    }

    private static TcpListener Listen(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        return listener;
    }

    private static int PortOf(TcpListener listener) =>
        ((IPEndPoint)listener.LocalEndpoint).Port;

    private sealed class FixedHostReachCatalog : IHostReachCatalog
    {
        private readonly IReadOnlyList<string> _addresses;

        public FixedHostReachCatalog(IReadOnlyList<string> addresses) => _addresses = addresses;

        public IReadOnlyList<string> ListInterfaceAddresses() => _addresses;
    }

    private static AttachLiveState Live()
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table, chrome: AttachChromePolicy.FromUi(AttachUiConfig.Default)),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new SilentPort(), "ws-1", "t1", "p1", "lease-r"),
            Renew = new LeaseRenewLoop((_, _, _) => Task.CompletedTask),
            WorkspaceId = "ws-1",
            TabId = "t1",
            PaneId = "p1",
            InputLease = "lease-i",
            ChromeEnabled = true,
            Chrome = MouseTestGeom.Split(),
        };
    }

    private static async Task YankHostDragAsync(
        AttachLiveState live,
        int startCol,
        int startRow,
        int endCol,
        int endRow,
        bool drag = true)
    {
        live.Host.Resize(80, 24);
        CubePairingDialogPainter.Stamp(
            new HostFrameCellSink(live.Host),
            live.CubesPairing,
            80,
            24);
        var ctx = AttachSession.MouseFeedContextFor(live);
        using var linked = new CancellationTokenSource();
        MouseEvent[] events = drag
            ?
            [
                new MouseEvent(MouseButton.Left, MouseAction.Press, startCol, startRow),
                new MouseEvent(MouseButton.Left, MouseAction.Drag, endCol, endRow),
                new MouseEvent(MouseButton.Left, MouseAction.Release, endCol, endRow),
            ]
            :
            [
                new MouseEvent(MouseButton.Left, MouseAction.Press, startCol, startRow),
                new MouseEvent(MouseButton.Left, MouseAction.Release, endCol, endRow),
            ];
        foreach (var ev in events)
        {
            foreach (var result in live.Mouse.Feed(ev, ctx))
            {
                _ = await AttachSession.ApplyMouseResultAsync(
                    result,
                    live,
                    new SilentPort(),
                    tty: null,
                    linked,
                    CancellationToken.None);
            }
        }
    }

    private sealed class SilentPort : IAttachCommandPort
    {
        public Task<System.Text.Json.JsonElement> CallAsync(
            string method,
            System.Text.Json.Nodes.JsonObject? @params,
            CancellationToken cancellationToken) =>
            Task.FromResult(default(System.Text.Json.JsonElement));
    }
}
