using System.Net;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Connectivity.Relay;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class RelayReusableNonceTests
{
    [Fact]
    public async Task Nonce_is_rejected_on_reuse_by_default()
    {
        await using var relay = RendezvousRelayServer.StartSelfHostedLoopback();
        Assert.True(RendezvousUrl.TryParse(relay.BoundUrl, out var url));
        var join = new OutboundRendezvousJoin(url);

        await JoinPairAsync(join, "plc_once01", "once0001");
        var replay = await ReplayAsync(join, "plc_once01", "once0001");

        Assert.False(replay.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, replay.Reason);
        Assert.Equal("join nonce already used", replay.Detail);
    }

    [Fact]
    public async Task Configured_reusable_nonce_may_be_reused()
    {
        await using var relay = Start("reuse001");
        Assert.True(RendezvousUrl.TryParse(relay.BoundUrl, out var url));
        var join = new OutboundRendezvousJoin(url);

        await JoinPairAsync(join, "plc_reuse1", "reuse001");
        await JoinPairAsync(join, "plc_reuse1", "reuse001");
    }

    [Fact]
    public async Task Other_nonce_stays_single_use_when_reusable_nonce_is_configured()
    {
        await using var relay = Start("reuse001");
        Assert.True(RendezvousUrl.TryParse(relay.BoundUrl, out var url));
        var join = new OutboundRendezvousJoin(url);

        await JoinPairAsync(join, "plc_other1", "other0001");
        var replay = await ReplayAsync(join, "plc_other1", "other0001");
        Assert.False(replay.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, replay.Reason);
        Assert.Equal("join nonce already used", replay.Detail);

        await JoinPairAsync(join, "plc_reuse1", "reuse001");
    }

    [Fact]
    public void Parse_accepts_reusable_nonce()
    {
        var ok = Hypa.Connectivity.Relay.Program.TryParseArgs(
            ["--listen", "127.0.0.1:9", "--reusable-nonce", "reuse001"],
            out var address,
            out var port,
            out _,
            out var pairingStore,
            out var reusableNonce,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(IPAddress.Parse("127.0.0.1"), address);
        Assert.Equal(9, port);
        Assert.Null(pairingStore);
        Assert.Equal("reuse001", reusableNonce);
        Assert.Equal("", error);
    }

    [Fact]
    public void Parse_rejects_reusable_nonce_without_a_value()
    {
        var missing = Hypa.Connectivity.Relay.Program.TryParseArgs(
            ["--listen", "127.0.0.1:9", "--reusable-nonce"],
            out _,
            out _,
            out _,
            out _,
            out var missingNonce,
            out var missingError);
        Assert.False(missing);
        Assert.Null(missingNonce);
        Assert.Equal("--reusable-nonce requires a value", missingError);

        var flag = Hypa.Connectivity.Relay.Program.TryParseArgs(
            ["--reusable-nonce", "--listen", "127.0.0.1:9"],
            out _,
            out _,
            out _,
            out _,
            out var flagNonce,
            out var flagError);
        Assert.False(flag);
        Assert.Null(flagNonce);
        Assert.Equal("--reusable-nonce requires a value", flagError);
    }

    [Fact]
    public void Parse_omits_reusable_nonce_when_the_flag_is_absent()
    {
        var ok = Hypa.Connectivity.Relay.Program.TryParseArgs(
            ["--listen", "127.0.0.1:9"],
            out _,
            out _,
            out _,
            out _,
            out var reusableNonce,
            out var error);

        Assert.True(ok, error);
        Assert.Null(reusableNonce);
    }

    private static RendezvousRelayServer Start(string reusableNonce)
    {
        var relay = new RendezvousRelayServer(
            RendezvousRelayIdentity.SelfHostedV0,
            IPAddress.Loopback,
            reusableNonce: reusableNonce);
        relay.Run();
        return relay;
    }

    private static async Task JoinPairAsync(OutboundRendezvousJoin join, string placement, string nonce)
    {
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted(placement, nonce);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var muxTask = join.JoinHeldAsync(muxBoot, timeout.Token);
        var clientTask = join.JoinHeldAsync(clientBoot, timeout.Token);
        var muxHeld = await muxTask;
        var clientHeld = await clientTask;
        Assert.True(muxHeld.Ok, muxHeld.Detail);
        Assert.True(clientHeld.Ok, clientHeld.Detail);
        await muxHeld.Value!.DisposeAsync();
        await clientHeld.Value!.DisposeAsync();
    }

    private static async Task<ConnectivityOutcome<HeldJoinSession>> ReplayAsync(
        OutboundRendezvousJoin join,
        string placement,
        string nonce)
    {
        var (muxBoot, _) = JoinTestPairs.SelfHosted(placement, nonce);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var replay = await join.JoinHeldAsync(muxBoot, timeout.Token);
        if (replay.Value is not null)
            await replay.Value.DisposeAsync();
        return replay;
    }
}
