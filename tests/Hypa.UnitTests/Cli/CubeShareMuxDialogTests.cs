using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Cubes;
using Hypa.Cli.Attach.Keys;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>The share dialog drives the home mux listener. The attach owns no listener.</summary>
public sealed class CubeShareMuxDialogTests
{
    private const string Fingerprint = "8cafa922781c6292214a611e02e6eebe3263cd60034d1376438ee50f26cfee01";

    [Fact]
    public async Task Start_enables_mux_share_and_shows_an_invite()
    {
        var mux = new FakeMuxShare { StartReply = Running(7443) };
        var invites = new FakeInvites();
        var live = Live(mux, invites);
        AttachSession.OpenShareMuxDialog(live);

        await PressEnterAsync(live);

        Assert.Equal([("0.0.0.0", 7443)], mux.Starts);
        var share = live.CubesPairing.Share!;
        Assert.True(share.Running);
        Assert.False(share.Starting);
        Assert.Null(share.Error);
        Assert.Equal("hypa-invite:1", share.Invite);
        Assert.Equal(["192.0.2.10"], share.AdvertisedHosts);
        var issued = Assert.Single(invites.Calls);
        Assert.Equal(("home", 7443, Fingerprint), (issued.Session, issued.Port, issued.Fingerprint));
        Assert.Equal("/mux/pairing", Assert.Single(invites.Stores));
    }

    [Fact]
    public async Task Share_shows_when_its_intent_was_not_saved()
    {
        var mux = new FakeMuxShare
        {
            StartReply = Running(7443) with { PersistError = "share.json could not be written" },
        };
        var live = Live(mux, new FakeInvites());
        AttachSession.OpenShareMuxDialog(live);

        await PressEnterAsync(live);

        Assert.True(live.CubesPairing.Share!.Running);
        Assert.Equal("hypa-invite:1", live.CubesPairing.Share.Invite);
        Assert.Equal("share.json could not be written", live.CubesPairing.Share.Error);
    }

    [Fact]
    public async Task Lost_reach_clears_the_old_invite()
    {
        var mux = new FakeMuxShare { StatusReply = Running(7443) };
        var live = Live(mux, new FakeInvites());
        AttachSession.OpenShareMuxDialog(live);
        await AttachSession.RefreshShareFromMuxAsync(live, tty: null, CancellationToken.None);
        Assert.Equal("hypa-invite:1", live.CubesPairing.Share!.Invite);

        live.HostReach = new FixedHostReach([]);
        await AttachSession.RefreshShareFromMuxAsync(live, tty: null, CancellationToken.None);

        Assert.True(live.CubesPairing.Share!.Running);
        Assert.Equal("", live.CubesPairing.Share.Invite);
        Assert.NotNull(live.CubesPairing.Share.Error);
        Assert.Null(live.LastMuxShare);
    }

    [Fact]
    public async Task Retrying_mux_share_shows_the_listener_error_and_stays_stoppable()
    {
        var mux = new FakeMuxShare
        {
            StartReply = new CubeShareStatusResult
            {
                Enabled = true,
                State = "retrying",
                Bind = "0.0.0.0",
                Port = 7443,
                Error = "port 7443 is already in use",
                Restarts = 1,
            },
        };
        var invites = new FakeInvites();
        var live = Live(mux, invites);
        AttachSession.OpenShareMuxDialog(live);

        await PressEnterAsync(live);

        var share = live.CubesPairing.Share!;
        Assert.True(share.Running);
        Assert.Equal("", share.Invite);
        Assert.StartsWith("port 7443 is already in use", share.Error, StringComparison.Ordinal);
        Assert.Contains("retrying", share.Error, StringComparison.Ordinal);
        Assert.Empty(invites.Calls);
        Assert.Contains(
            CubePairingDialogPainter.StopLabel,
            CubePairingDialogPainter.Paint(live.CubesPairing, 80, 24),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_disables_mux_share()
    {
        var mux = new FakeMuxShare { StartReply = Running(7443) };
        var live = Live(mux, new FakeInvites());
        AttachSession.OpenShareMuxDialog(live);
        await PressEnterAsync(live);

        await PressEnterAsync(live);

        Assert.Equal(1, mux.Stops);
        var share = live.CubesPairing.Share!;
        Assert.False(share.Running);
        Assert.Equal("", share.Invite);
        Assert.Null(live.LastMuxShare);
    }

    [Fact]
    public async Task Old_mux_without_share_methods_reports_a_restart()
    {
        var mux = new FakeMuxShare { StartError = SocketMuxCubeSharePort.OldMuxDetail };
        var live = Live(mux, new FakeInvites());
        AttachSession.OpenShareMuxDialog(live);

        await PressEnterAsync(live);

        var share = live.CubesPairing.Share!;
        Assert.False(share.Running);
        Assert.Equal(SocketMuxCubeSharePort.OldMuxDetail, share.Error);
    }

    [Fact]
    public async Task Opening_the_dialog_shows_a_share_another_attach_started()
    {
        var mux = new FakeMuxShare { StatusReply = Running(7444) };
        var invites = new FakeInvites();
        var live = Live(mux, invites);
        AttachSession.OpenShareMuxDialog(live);
        Assert.False(live.CubesPairing.Share!.Running);

        await AttachSession.RefreshShareFromMuxAsync(live, tty: null, CancellationToken.None);

        var share = live.CubesPairing.Share!;
        Assert.True(share.Running);
        Assert.Equal("7444", share.Port);
        Assert.Equal("hypa-invite:1", share.Invite);
        Assert.Empty(mux.Starts);
    }

    [Fact]
    public async Task Reopening_the_dialog_reuses_the_invite_for_the_same_listener()
    {
        var mux = new FakeMuxShare { StatusReply = Running(7443) };
        var invites = new FakeInvites();
        var live = Live(mux, invites);
        AttachSession.OpenShareMuxDialog(live);
        await AttachSession.RefreshShareFromMuxAsync(live, tty: null, CancellationToken.None);
        live.CubesPairing.Cancel();

        AttachSession.OpenShareMuxDialog(live);
        Assert.True(live.CubesPairing.Share!.Running);
        Assert.Equal("hypa-invite:1", live.CubesPairing.Share.Invite);
        await AttachSession.RefreshShareFromMuxAsync(live, tty: null, CancellationToken.None);

        Assert.Single(invites.Calls);
        Assert.Equal("hypa-invite:1", live.CubesPairing.Share!.Invite);
    }

    [Fact]
    public async Task A_new_listener_certificate_mints_a_new_invite()
    {
        var mux = new FakeMuxShare { StatusReply = Running(7443) };
        var invites = new FakeInvites();
        var live = Live(mux, invites);
        AttachSession.OpenShareMuxDialog(live);
        await AttachSession.RefreshShareFromMuxAsync(live, tty: null, CancellationToken.None);

        mux.StatusReply = Running(7443, new string('a', 64));
        await AttachSession.RefreshShareFromMuxAsync(live, tty: null, CancellationToken.None);

        Assert.Equal(2, invites.Calls.Count);
        Assert.Equal("hypa-invite:2", live.CubesPairing.Share!.Invite);
    }

    [Fact]
    public async Task Refresh_clears_a_share_the_mux_no_longer_runs()
    {
        var mux = new FakeMuxShare { StatusReply = Running(7443) };
        var live = Live(mux, new FakeInvites());
        AttachSession.OpenShareMuxDialog(live);
        await AttachSession.RefreshShareFromMuxAsync(live, tty: null, CancellationToken.None);

        mux.StatusReply = new CubeShareStatusResult { Enabled = false, State = "stopped" };
        await AttachSession.RefreshShareFromMuxAsync(live, tty: null, CancellationToken.None);

        Assert.False(live.CubesPairing.Share!.Running);
        Assert.Equal("", live.CubesPairing.Share.Invite);
        Assert.Null(live.LastMuxShare);
    }

    private static CubeShareStatusResult Running(int port, string fingerprint = Fingerprint) =>
        new()
        {
            Enabled = true,
            State = "running",
            Bind = "0.0.0.0",
            Port = port,
            Listen = new CubeShareListenInfo
            {
                Bind = "0.0.0.0",
                Port = port,
                CertificateSha256 = fingerprint,
                QuicListening = true,
                PairingStore = "/mux/pairing",
            },
        };

    private static Task PressEnterAsync(AttachLiveState live) =>
        AttachSession.RouteCubePairingDialogKeyAsync(
            KeyChord.Parse("enter"),
            live,
            new SilentPort(),
            tty: null,
            CancellationToken.None);

    private static AttachLiveState Live(IMuxCubeSharePort mux, IShareInviteIssuer invites)
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
            HomeSessionName = "home",
            HostReach = new FixedHostReach(["192.0.2.10"]),
            MuxShare = mux,
            ShareInvites = invites,
        };
    }

    private sealed class FakeMuxShare : IMuxCubeSharePort
    {
        public CubeShareStatusResult StatusReply { get; set; } = new() { State = "stopped" };

        public CubeShareStatusResult StartReply { get; set; } = new() { State = "stopped" };

        public string? StartError { get; set; }

        public List<(string Bind, int Port)> Starts { get; } = [];

        public int Stops { get; private set; }

        public Task<MuxCubeShareCall> StatusAsync(CancellationToken ct) =>
            Task.FromResult(MuxCubeShareCall.Ok(StatusReply));

        public Task<MuxCubeShareCall> StartAsync(string bind, int port, CancellationToken ct)
        {
            Starts.Add((bind, port));
            return Task.FromResult(StartError is { } error
                ? MuxCubeShareCall.Fail(error)
                : MuxCubeShareCall.Ok(StartReply));
        }

        public Task<MuxCubeShareCall> StopAsync(CancellationToken ct)
        {
            Stops++;
            return Task.FromResult(MuxCubeShareCall.Ok(new CubeShareStatusResult { State = "stopped" }));
        }
    }

    private sealed class FakeInvites : IShareInviteIssuer
    {
        public List<(string Session, int Port, string? Fingerprint)> Calls { get; } = [];

        public List<string?> Stores { get; } = [];

        public Task<ConnectivityOutcome<string>> IssueAsync(
            string session,
            int port,
            string? certificateSha256,
            string? pairingStore,
            IReadOnlyList<string> advertiseHosts,
            CancellationToken ct)
        {
            Calls.Add((session, port, certificateSha256));
            Stores.Add(pairingStore);
            return Task.FromResult(ConnectivityOutcome<string>.Success("hypa-invite:" + Calls.Count));
        }
    }

    private sealed class FixedHostReach(IReadOnlyList<string> addresses) : IHostReachCatalog
    {
        public IReadOnlyList<string> ListInterfaceAddresses() => addresses;
    }

    private sealed class SilentPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? @params, CancellationToken cancellationToken) =>
            Task.FromResult(default(JsonElement));
    }
}
