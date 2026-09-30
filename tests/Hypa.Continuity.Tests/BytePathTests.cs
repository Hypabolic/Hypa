using System.Net;
using System.Net.Sockets;
using System.Text;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Connectivity.Relay;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class BytePathTests
{
    [Fact]
    public async Task Hostname_endpoint_opens_without_rendezvous_url()
    {
        await using var relay = RendezvousRelayServer.StartSelfHostedLoopback();
        var path = new TcpTlsBytePath();
        var opened = await path.OpenAsync(
            new BytePathRequest
            {
                Provider = BytePathProviders.Tcp,
                Endpoint = new BytePathEndpoint
                {
                    Host = "127.0.0.1",
                    Port = relay.Port,
                },
            });
        Assert.True(opened.Ok, opened.Detail);
        await path.CloseAsync(opened.Value!);
    }

    [Fact]
    public async Task Tcp_relay_join_completes_through_byte_path()
    {
        await using var relay = RendezvousRelayServer.StartSelfHostedLoopback();
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_bpath", "bpath001");
        var bytePath = new TcpTlsBytePath();
        Assert.True(RendezvousUrl.TryParse(relay.BoundUrl, out var url));
        var muxTask = OutboundFramedSession.ConnectAsync(url, muxBoot, bytePath: bytePath);
        var clientTask = OutboundFramedSession.ConnectAsync(url, clientBoot, bytePath: bytePath);
        var muxOutcome = await muxTask;
        var clientOutcome = await clientTask;
        Assert.True(muxOutcome.Ok, muxOutcome.Detail);
        Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        await using var mux = muxOutcome.Value!;
        await using var client = clientOutcome.Value!;

        Assert.True((await mux.SendAsync(
            StreamFrame.Control(
                StreamDirection.MuxToClient,
                0,
                Encoding.UTF8.GetBytes("""{"ok":true}""")))).Ok);
        var received = await client.ReceiveAsync();
        Assert.True(received.Ok, received.Detail);
        Assert.Equal(StreamDirection.MuxToClient, received.Value!.Direction);
    }

    [Fact]
    public async Task Tls_relay_join_completes_through_byte_path()
    {
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        await using var relay = RendezvousRelayServer.StartSelfHostedLoopbackTls(cert);
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_bptls", "bptls001");
        var bytePath = new TcpTlsBytePath(cert);
        Assert.True(RendezvousUrl.TryParse(relay.BoundUrl, out var url));
        var muxTask = OutboundFramedSession.ConnectAsync(url, muxBoot, tlsTrust: cert, bytePath: bytePath);
        var clientTask = OutboundFramedSession.ConnectAsync(url, clientBoot, tlsTrust: cert, bytePath: bytePath);
        var muxOutcome = await muxTask;
        var clientOutcome = await clientTask;
        Assert.True(muxOutcome.Ok, muxOutcome.Detail);
        Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        await using var mux = muxOutcome.Value!;
        await using var client = clientOutcome.Value!;
        Assert.True((await mux.SendAsync(
            StreamFrame.Binary(
                StreamDirection.MuxToClient,
                1,
                0,
                Encoding.UTF8.GetBytes("screen")))).Ok);
        var received = await client.ReceiveAsync();
        Assert.True(received.Ok, received.Detail);
        Assert.True(received.Value!.CountsAsBinary);
    }

    [Fact]
    public async Task Path_failure_returns_outcome_and_releases_path()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var path = new TcpTlsBytePath();
        var opened = await path.OpenAsync(
            new BytePathRequest
            {
                Provider = BytePathProviders.Tcp,
                Endpoint = new BytePathEndpoint
                {
                    Host = "127.0.0.1",
                    Port = port,
                },
            });
        Assert.False(opened.Ok);
        Assert.Equal(ConnectivityReasons.PeerUnavailable, opened.Reason);
        Assert.Null(opened.Value);
    }

    [Fact]
    public async Task CloseAsync_disposes_path_when_caller_token_is_cancelled()
    {
        await using var relay = RendezvousRelayServer.StartSelfHostedLoopback();
        var path = new TcpTlsBytePath();
        var opened = await path.OpenAsync(
            new BytePathRequest
            {
                Provider = BytePathProviders.Tcp,
                Endpoint = new BytePathEndpoint
                {
                    Host = "127.0.0.1",
                    Port = relay.Port,
                },
            });
        Assert.True(opened.Ok, opened.Detail);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await path.CloseAsync(opened.Value!, cts.Token);
    }

    [Fact]
    public async Task Unsupported_provider_is_rejected()
    {
        var path = new TcpTlsBytePath();
        var opened = await path.OpenAsync(
            new BytePathRequest
            {
                Provider = "quic",
                Endpoint = new BytePathEndpoint
                {
                    Host = "127.0.0.1",
                    Port = 1,
                },
            });
        Assert.False(opened.Ok);
        Assert.Equal(ConnectivityReasons.Internal, opened.Reason);
        Assert.Null(opened.Value);
    }

    [Fact]
    public async Task Join_denied_releases_path_after_relay_rejects_nonce()
    {
        await using var relay = RendezvousRelayServer.StartSelfHostedLoopback();
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_bpfail", "bpfail01");
        Assert.True(RendezvousUrl.TryParse(relay.BoundUrl, out var relayUrl));
        var join = new OutboundRendezvousJoin(relayUrl);
        var muxTask = join.JoinHeldAsync(muxBoot);
        var clientTask = join.JoinHeldAsync(clientBoot);
        var muxHeld = await muxTask;
        var clientHeld = await clientTask;
        Assert.True(muxHeld.Ok, muxHeld.Detail);
        Assert.True(clientHeld.Ok, clientHeld.Detail);
        await muxHeld.Value!.DisposeAsync();
        await clientHeld.Value!.DisposeAsync();

        var replay = await join.JoinHeldAsync(muxBoot);
        Assert.False(replay.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, replay.Reason);
    }
}
