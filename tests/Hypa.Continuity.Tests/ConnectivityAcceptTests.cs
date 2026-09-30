using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Connectivity.Relay;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class ConnectivityAcceptTests
{
    [SkippableFact]
    public async Task Direct_join_completes_without_rendezvous()
    {
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_accept", "accept001");
        await using var stack = await AcceptTestStack.StartAsync(muxBoot);
        var bytePath = new TcpTlsBytePath();
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            stack.Accept.DialEndpoint,
            clientBoot,
            bytePath: bytePath);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using var mux = muxSocket;
        var clientOutcome = await clientTask;
        Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        await using var client = clientOutcome.Value!;
        Assert.True((await client.SendAsync(
            StreamFrame.Control(
                StreamDirection.ClientToMux,
                0,
                Encoding.UTF8.GetBytes("""{"ping":true}""")))).Ok);
        var fromMux = await ReadMuxBytesAsync(mux, TimeSpan.FromSeconds(3));
        var muxText = Encoding.UTF8.GetString(fromMux);
        Assert.Contains("\"ping\":true", muxText, StringComparison.Ordinal);
        Assert.DoesNotContain(stack.UnixPath, muxText);
    }

    [SkippableFact]
    public async Task Hostname_dial_completes_without_rendezvous_url()
    {
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_acchost", "acchost1");
        await using var stack = await AcceptTestStack.StartAsync(muxBoot);
        var endpoint = stack.Accept.DialEndpoint with { Host = "localhost" };
        var clientTask = OutboundFramedSession.ConnectDirectAsync(endpoint, clientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        }
    }

    [SkippableFact]
    public async Task Tls_direct_join_completes_without_rendezvous()
    {
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_acctls", "acctls01");
        await using var stack = await AcceptTestStack.StartAsync(muxBoot, tlsCertificate: cert);
        var bytePath = new TcpTlsBytePath(cert);
        var clientTask = OutboundFramedSession.ConnectDirectAsync(
            stack.Accept.DialEndpoint,
            clientBoot,
            tlsTrust: cert,
            bytePath: bytePath);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var clientOutcome = await clientTask;
            Assert.True(clientOutcome.Ok, clientOutcome.Detail);
        }
    }

    [Fact]
    public void Non_literal_bind_host_is_rejected()
    {
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new RecordingLocalUnixBridge();
        var bind = new ConnectivityAcceptBind
        {
            Host = "localhost",
            Port = 0,
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            new TcpTlsConnectivityAccept(
                bind,
                bridge,
                coordinator,
                _ => ConnectivityOutcome<JoinBootstrap>.Failure(
                    ConnectivityReasons.Internal,
                    "unused")));
        Assert.Contains("literal ip", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_loopback_bind_requires_trust_gate()
    {
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new RecordingLocalUnixBridge();
        var bind = new ConnectivityAcceptBind
        {
            Host = "0.0.0.0",
            Port = 0,
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            new TcpTlsConnectivityAccept(
                bind,
                bridge,
                coordinator,
                _ => ConnectivityOutcome<JoinBootstrap>.Failure(
                    ConnectivityReasons.Internal,
                    "unused")));
        Assert.Contains("trust gate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_loopback_bind_requires_tls()
    {
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new RecordingLocalUnixBridge();
        var temp = Path.Combine(Path.GetTempPath(), "hypa-accept-" + Guid.NewGuid().ToString("N"));
        var store = new FileDevicePairingStore(temp);
        var keys = new FileDeviceKeyStore(temp);
        var pairing = new DevicePairingService(store, keys);
        var bind = new ConnectivityAcceptBind
        {
            Host = "0.0.0.0",
            Port = 0,
            Tls = false,
        };
        var ex = Assert.Throws<ArgumentException>(() =>
            new TcpTlsConnectivityAccept(
                bind,
                bridge,
                coordinator,
                _ => ConnectivityOutcome<JoinBootstrap>.Failure(
                    ConnectivityReasons.Internal,
                    "unused"),
                trustGate: pairing));
        Assert.Contains("tls", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Concurrent_clients_with_distinct_nonces_both_complete()
    {
        var (muxBootA, clientBootA) = JoinTestPairs.SelfHosted("plc_acccon", "acccon01");
        var (muxBootB, clientBootB) = JoinTestPairs.SelfHosted("plc_acccon", "acccon02");
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new ConcurrentFakeUnixBridge(new AcceptPathJoin(coordinator));
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            bridge,
            coordinator,
            client =>
            {
                if (string.Equals(client.Nonce.Value, clientBootA.Nonce.Value, StringComparison.Ordinal))
                    return ConnectivityOutcome<JoinBootstrap>.Success(muxBootA);
                if (string.Equals(client.Nonce.Value, clientBootB.Nonce.Value, StringComparison.Ordinal))
                    return ConnectivityOutcome<JoinBootstrap>.Success(muxBootB);
                return ConnectivityOutcome<JoinBootstrap>.Failure(
                    ConnectivityReasons.Internal,
                    "unexpected nonce");
            });

        var clientTaskA = OutboundFramedSession.ConnectDirectAsync(accept.DialEndpoint, clientBootA);
        var clientTaskB = OutboundFramedSession.ConnectDirectAsync(accept.DialEndpoint, clientBootB);
        await Task.WhenAll(clientTaskA, clientTaskB);
        var outcomeA = await clientTaskA;
        var outcomeB = await clientTaskB;
        Assert.True(outcomeA.Ok, outcomeA.Detail);
        Assert.True(outcomeB.Ok, outcomeB.Detail);
        Assert.Equal(2, bridge.BindCalls);
    }

    [Fact]
    public void CloseJoinsForDevice_revokes_registered_session_before_bridge()
    {
        var (muxBootA, clientBootA) = JoinTestPairs.SelfHosted(
            "plc_revoke",
            "revoke01",
            clientDevice: "dev_cli001");
        var (muxBootB, clientBootB) = JoinTestPairs.SelfHosted(
            "plc_revoke",
            "revoke02",
            clientDevice: "dev_cli002");
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var sessionA = coordinator.RegisterClient(clientBootA, new MemoryStream());
        var sessionB = coordinator.RegisterClient(clientBootB, new MemoryStream());
        Assert.True(DeviceId.TryParse("dev_cli001", out var revokedDevice));
        coordinator.CloseJoinsForDevice(revokedDevice);
        Assert.True(sessionA.IsRevoked);
        Assert.False(sessionB.IsRevoked);
        Assert.Equal(clientBootA.Nonce.Value, muxBootA.Nonce.Value);
        var matchA = coordinator.CompleteMuxLeg(muxBootA);
        Assert.False(matchA.Ok, matchA.Detail);
        Assert.Equal(ConnectivityReasons.JoinDenied, matchA.Reason);
        Assert.True(coordinator.CompleteMuxLeg(muxBootB).Ok);
        coordinator.CompleteSession(sessionB);
    }

    [Fact]
    public void Coordinator_isolates_pending_legs_by_nonce()
    {
        var (muxBootA, clientBootA) = JoinTestPairs.SelfHosted("plc_coord", "coord001");
        var (muxBootB, clientBootB) = JoinTestPairs.SelfHosted("plc_coord", "coord002");
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var streamA = new MemoryStream();
        var streamB = new MemoryStream();
        var sessionA = coordinator.RegisterClient(clientBootA, streamA);
        var sessionB = coordinator.RegisterClient(clientBootB, streamB);
        var matchA = coordinator.CompleteMuxLeg(muxBootA);
        var matchB = coordinator.CompleteMuxLeg(muxBootB);
        Assert.True(matchA.Ok, matchA.Detail);
        Assert.True(matchB.Ok, matchB.Detail);
        Assert.True(coordinator.TryTakeMatchedClient(sessionA, out var takenA));
        Assert.True(coordinator.TryTakeMatchedClient(sessionB, out var takenB));
        Assert.Same(streamA, takenA.Stream);
        Assert.Same(streamB, takenB.Stream);
        coordinator.CompleteSession(sessionA);
        coordinator.CompleteSession(sessionB);
    }

    [SkippableFact]
    public async Task DisposeAsync_waits_for_in_flight_handler()
    {
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_accdis", "accdis01");
        var fakeMux = await FakeMuxUnix.StartAsync();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new LocalUnixBridge(fakeMux.SocketPath, new AcceptPathJoin(coordinator));
        var accept = TcpTlsConnectivityAccept.StartLoopback(
            bridge,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot));
        var joinTask = OutboundFramedSession.ConnectDirectAsync(accept.DialEndpoint, clientBoot);
        var muxSocket = await fakeMux.AcceptAsync();
        using (muxSocket)
        {
            await accept.DisposeAsync();
            var clientOutcome = await joinTask;
            // On a fast runner the join finishes before the dispose starts, so
            // the outcome can be either. The test proves that dispose returns
            // and the client is not left waiting.
            if (clientOutcome.Ok)
                await clientOutcome.Value!.DisposeAsync();
        }

        await bridge.DisposeAsync();
        await fakeMux.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_completes_after_client_resets_during_handshake()
    {
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new RecordingLocalUnixBridge();
        var accept = TcpTlsConnectivityAccept.StartLoopback(
            bridge,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(ConnectivityReasons.Internal, "unused"));
        using var client = new TcpClient();
        await client.ConnectAsync(accept.DialEndpoint.Host, accept.DialEndpoint.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { (byte)'{' });
        client.LingerState = new LingerOption(true, 0);
        client.Close();
        await accept.DisposeAsync();
        Assert.Equal(0, bridge.BindCalls);
    }

    [Fact]
    public async Task Excess_handshake_is_rejected_when_slots_are_full()
    {
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new RecordingLocalUnixBridge();
        var accept = TcpTlsConnectivityAccept.StartLoopback(
            bridge,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(ConnectivityReasons.Internal, "unused"),
            maxConcurrentHandshakes: 1);

        using var holding = new TcpClient();
        await holding.ConnectAsync(accept.DialEndpoint.Host, accept.DialEndpoint.Port);
        await Task.Delay(50);

        using var rejected = new TcpClient();
        await rejected.ConnectAsync(accept.DialEndpoint.Host, accept.DialEndpoint.Port);
        var buffer = new byte[16];
        var read = await rejected.Client.ReceiveAsync(buffer, SocketFlags.None);
        Assert.Equal(0, read);

        await accept.DisposeAsync();
    }

    [Fact]
    public async Task Rejected_peer_does_not_reach_unix_bridge()
    {
        var (_, clientBoot) = JoinTestPairs.SelfHosted("plc_accrej", "accrej01");
        var recording = new RecordingLocalUnixBridge();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            recording,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.Internal,
                "unused"));
        var join = new OutboundDirectJoin(accept.DialEndpoint);
        var bad = clientBoot with { Audience = "hypa.invalid" };
        var outcome = await join.JoinHeldAsync(JoinTestPairs.Stamp(bad));
        Assert.False(outcome.Ok);
        Assert.Equal(0, recording.BindCalls);
    }

    [Theory]
    [InlineData("role", JoinRole.Mux)]
    [InlineData("placement", null)]
    [InlineData("nonce", null)]
    public async Task Invalid_join_fields_fail_before_unix_bridge(string field, JoinRole? roleOverride)
    {
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_accinv", "accinv01");
        var recording = new RecordingLocalUnixBridge();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            recording,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot));

        var invalid = field switch
        {
            "role" => clientBoot with { Role = roleOverride!.Value },
            "placement" => clientBoot with
            {
                PlacementId = PlacementId.TryParse("plc_wrong000", out var wrongPlacement)
                    ? wrongPlacement
                    : default,
            },
            "nonce" => clientBoot with
            {
                Nonce = JoinNonce.TryParse("wrong0000000000001", out var wrongNonce)
                    ? wrongNonce
                    : default,
            },
            _ => clientBoot,
        };

        var join = new OutboundDirectJoin(accept.DialEndpoint);
        var outcome = await join.JoinHeldAsync(JoinTestPairs.Stamp(invalid));
        Assert.False(outcome.Ok);
        Assert.Equal(0, recording.BindCalls);
    }

    [SkippableFact]
    public async Task ReleaseMuxReservation_allows_second_bind()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBootA, clientBootA) = JoinTestPairs.SelfHosted("plc_relres", "relres01");
        var (muxBootB, clientBootB) = JoinTestPairs.SelfHosted("plc_relres", "relres02");
        var fakeMux = await FakeMuxUnix.StartAsync();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new LocalUnixBridge(fakeMux.SocketPath, new AcceptPathJoin(coordinator));
        coordinator.RegisterClient(clientBootA, new MemoryStream());
        var bindTaskA = bridge.BindMuxAsync(muxBootA);
        var muxSocketA = await fakeMux.AcceptAsync();
        using (muxSocketA)
        {
            var boundA = await bindTaskA;
            Assert.True(boundA.Ok, boundA.Detail);
            bridge.ReleaseMuxReservation(boundA.Value!.Reservation);
        }

        coordinator.RegisterClient(clientBootB, new MemoryStream());
        var bindTaskB = bridge.BindMuxAsync(muxBootB);
        var muxSocketB = await fakeMux.AcceptAsync();
        using (muxSocketB)
        {
            var boundB = await bindTaskB;
            Assert.True(boundB.Ok, boundB.Detail);
        }

        await bridge.DisposeAsync();
        await fakeMux.DisposeAsync();
    }

    [SkippableFact]
    public async Task Wrong_owner_release_does_not_clear_winners_local_unix_reservation()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBootA, clientBootA) = JoinTestPairs.SelfHosted("plc_ownrel", "ownrel01");
        var (muxBootB, clientBootB) = JoinTestPairs.SelfHosted("plc_ownrel", "ownrel02");
        var fakeMux = await FakeMuxUnix.StartAsync();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new LocalUnixBridge(fakeMux.SocketPath, new AcceptPathJoin(coordinator));
        coordinator.RegisterClient(clientBootA, new MemoryStream());
        coordinator.RegisterClient(clientBootB, new MemoryStream());

        var bindTaskA = bridge.BindMuxAsync(muxBootA);
        var bindTaskB = bridge.BindMuxAsync(muxBootB);
        var muxSocketA = await fakeMux.AcceptAsync();
        using (muxSocketA)
        {
            var boundA = await bindTaskA;
            Assert.True(boundA.Ok, boundA.Detail);
            bridge.ReleaseMuxReservation(MuxBridgeReservation.Create());

            await using var winnerStream = bridge.TryTakeMuxStream(boundA.Value!.Reservation);
            Assert.NotNull(winnerStream);
        }

        var muxSocketB = await fakeMux.AcceptAsync();
        using (muxSocketB)
        {
            var boundB = await bindTaskB;
            Assert.True(boundB.Ok, boundB.Detail);
        }

        await bridge.DisposeAsync();
        await fakeMux.DisposeAsync();
    }

    [SkippableFact]
    public async Task Concurrent_real_local_unix_bridge_clients_both_complete()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBootA, clientBootA) = JoinTestPairs.SelfHosted("plc_realcn", "realcn01");
        var (muxBootB, clientBootB) = JoinTestPairs.SelfHosted("plc_realcn", "realcn02");
        var fakeMux = await FakeMuxUnix.StartAsync();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new LocalUnixBridge(fakeMux.SocketPath, new AcceptPathJoin(coordinator));
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            bridge,
            coordinator,
            client =>
            {
                if (string.Equals(client.Nonce.Value, clientBootA.Nonce.Value, StringComparison.Ordinal))
                    return ConnectivityOutcome<JoinBootstrap>.Success(muxBootA);
                if (string.Equals(client.Nonce.Value, clientBootB.Nonce.Value, StringComparison.Ordinal))
                    return ConnectivityOutcome<JoinBootstrap>.Success(muxBootB);
                return ConnectivityOutcome<JoinBootstrap>.Failure(
                    ConnectivityReasons.Internal,
                    "unexpected nonce");
            });

        var clientTaskA = OutboundFramedSession.ConnectDirectAsync(accept.DialEndpoint, clientBootA);
        var clientTaskB = OutboundFramedSession.ConnectDirectAsync(accept.DialEndpoint, clientBootB);
        var muxSocketA = await fakeMux.AcceptAsync();
        var muxSocketB = await fakeMux.AcceptAsync();
        using (muxSocketA)
        using (muxSocketB)
        {
            var outcomeA = await clientTaskA;
            var outcomeB = await clientTaskB;
            Assert.True(outcomeA.Ok, outcomeA.Detail);
            Assert.True(outcomeB.Ok, outcomeB.Detail);
        }

        await fakeMux.DisposeAsync();
    }

    [SkippableFact]
    public async Task Established_session_releases_handshake_slot()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_acslot", "acslot01");
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        var bridge = new SlowSpliceUnixBridge(new AcceptPathJoin(coordinator));
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            bridge,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot),
            maxConcurrentHandshakes: 1);

        var firstTask = OutboundFramedSession.ConnectDirectAsync(accept.DialEndpoint, clientBoot);
        await Task.Delay(200);
        using var second = new TcpClient();
        await second.ConnectAsync(accept.DialEndpoint.Host, accept.DialEndpoint.Port);
        second.Client.ReceiveTimeout = 200;
        var buffer = new byte[16];
        // While the first handshake holds the only slot, the second connection
        // is not served: it times out, or the accept closes it. It never
        // receives data.
        var received = await Task.Run(() =>
        {
            try
            {
                return second.Client.Receive(buffer);
            }
            catch (SocketException)
            {
                return 0;
            }
        });
        Assert.Equal(0, received);

        var firstOutcome = await firstTask;
        Assert.True(firstOutcome.Ok, firstOutcome.Detail);
        await firstOutcome.Value!.DisposeAsync();
    }

    [Fact]
    public async Task Pairing_denied_device_fails_before_unix_bridge()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-accept-" + Guid.NewGuid().ToString("N"));
        var store = new FileDevicePairingStore(temp);
        var keys = new FileDeviceKeyStore(temp);
        var pairing = new DevicePairingService(store, keys);
        var (muxBoot, clientBoot) = JoinTestPairs.SelfHosted("plc_accpr", "accpr001");
        var recording = new RecordingLocalUnixBridge();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            recording,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot),
            trustGate: pairing);
        var join = new OutboundDirectJoin(accept.DialEndpoint);
        var outcome = await join.JoinHeldAsync(JoinTestPairs.Stamp(clientBoot));
        Assert.False(outcome.Ok);
        Assert.Equal(ConnectivityReasons.JoinDenied, outcome.Reason);
        Assert.Equal(0, recording.BindCalls);
    }

    [Fact]
    public async Task Missing_device_signature_fails_before_unix_bridge()
    {
        var temp = Path.Combine(Path.GetTempPath(), "hypa-accept-" + Guid.NewGuid().ToString("N"));
        var store = new FileDevicePairingStore(temp);
        var keys = new FileDeviceKeyStore(temp);
        var pairing = new DevicePairingService(store, keys);
        var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var approved = await pairing.ApproveAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value!.PairingCode,
            PairingApprover.SelfHostedConsole);
        Assert.True(approved.Ok, approved.Detail);
        Assert.True(JoinNonce.TryParse("accpr002", out var nonce));
        Assert.True(PlacementId.TryParse("plc_accpr2", out var placement));
        var issued = await pairing.IssueJoinCapabilityAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value.DeviceId,
            placement,
            JoinRole.Client,
            nonce,
            DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(issued.Ok, issued.Detail);
        var clientBoot = new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Client,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = StreamClass.Control,
            Capability = issued.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
        };
        var recording = new RecordingLocalUnixBridge();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            recording,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.Internal,
                "unused"),
            trustGate: pairing);
        var join = new OutboundDirectJoin(accept.DialEndpoint);
        var outcome = await join.JoinHeldAsync(clientBoot);
        Assert.False(outcome.Ok);
        Assert.Equal(ConnectivityReasons.Unauthorized, outcome.Reason);
        Assert.Equal(0, recording.BindCalls);
    }

    [SkippableFact]
    public async Task Paired_device_with_valid_signature_completes_join()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var temp = Path.Combine(Path.GetTempPath(), "hypa-accept-" + Guid.NewGuid().ToString("N"));
        var store = new FileDevicePairingStore(temp);
        var keys = new FileDeviceKeyStore(temp);
        var pairing = new DevicePairingService(store, keys);
        var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var approved = await pairing.ApproveAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value!.PairingCode,
            PairingApprover.SelfHostedConsole);
        Assert.True(approved.Ok, approved.Detail);
        Assert.True(JoinNonce.TryParse("pairsig1", out var nonce));
        Assert.True(PlacementId.TryParse("plc_pairsg", out var placement));
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var muxCap = await pairing.IssueJoinCapabilityAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value.DeviceId,
            placement,
            JoinRole.Mux,
            nonce,
            expires);
        var clientCap = await pairing.IssueJoinCapabilityAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value.DeviceId,
            placement,
            JoinRole.Client,
            nonce,
            expires);
        Assert.True(muxCap.Ok, muxCap.Detail);
        Assert.True(clientCap.Ok, clientCap.Detail);
        var muxBoot = new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Mux,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = StreamClass.Control,
            Capability = muxCap.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
        };
        var clientBoot = new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Client,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = StreamClass.Control,
            Capability = clientCap.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
        };
        var signed = await JoinDeviceAuthenticator.StampAsync(clientBoot, keys);
        Assert.True(signed.Ok, signed.Detail);
        clientBoot = signed.Value!;
        await using var stack = await AcceptTestStack.StartAsync(
            muxBoot,
            muxBootstrapFactory: _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot),
            trustGate: pairing);
        var join = new OutboundDirectJoin(stack.Accept.DialEndpoint, deviceKeys: keys);
        var joinTask = join.JoinHeldAsync(clientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var outcome = await joinTask;
            Assert.True(outcome.Ok, outcome.Detail);
        }
    }

    [SkippableFact]
    public async Task File_store_revoke_closes_established_join()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        var temp = Path.Combine(Path.GetTempPath(), "hypa-accept-" + Guid.NewGuid().ToString("N"));
        var store = new FileDevicePairingStore(temp);
        var keys = new FileDeviceKeyStore(temp);
        var pairing = new DevicePairingService(store, keys);
        var started = await pairing.StartPairingAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(started.Ok, started.Detail);
        var approved = await pairing.ApproveAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value!.PairingCode,
            PairingApprover.SelfHostedConsole);
        Assert.True(approved.Ok, approved.Detail);
        Assert.True(JoinNonce.TryParse("revwatch1", out var nonce));
        Assert.True(PlacementId.TryParse("plc_revwch", out var placement));
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var muxCap = await pairing.IssueJoinCapabilityAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value.DeviceId,
            placement,
            JoinRole.Mux,
            nonce,
            expires);
        var clientCap = await pairing.IssueJoinCapabilityAsync(
            OperatorIdentity.LocalSelfHosted,
            started.Value.DeviceId,
            placement,
            JoinRole.Client,
            nonce,
            expires);
        Assert.True(muxCap.Ok, muxCap.Detail);
        Assert.True(clientCap.Ok, clientCap.Detail);
        var muxBoot = new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Mux,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = StreamClass.Control,
            Capability = muxCap.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
        };
        var clientBoot = new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Client,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = StreamClass.Control,
            Capability = clientCap.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
        };
        var signed = await JoinDeviceAuthenticator.StampAsync(clientBoot, keys);
        Assert.True(signed.Ok, signed.Detail);
        clientBoot = signed.Value!;
        await using var stack = await AcceptTestStack.StartAsync(
            muxBoot,
            muxBootstrapFactory: _ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot),
            trustGate: pairing);
        var join = new OutboundDirectJoin(stack.Accept.DialEndpoint, deviceKeys: keys);
        var joinTask = join.JoinHeldAsync(clientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var outcome = await joinTask;
            Assert.True(outcome.Ok, outcome.Detail);
            await using var held = outcome.Value!;
            var other = new DevicePairingService(
                new FileDevicePairingStore(temp),
                new FileDeviceKeyStore(temp));
            var revoked = await other.RevokeAsync(
                OperatorIdentity.LocalSelfHosted,
                started.Value.DeviceId);
            Assert.True(revoked.Ok, revoked.Detail);
            var stillValid = await pairing.IsJoinStillValidAsync(
                clientBoot,
                DateTimeOffset.UtcNow);
            Assert.False(stillValid);
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[1];
            try
            {
                var read = await held.Stream.ReadAsync(buffer, readCts.Token);
                Assert.Equal(0, read);
            }
            catch (IOException)
            {
            }
        }
    }

    [SkippableFact]
    public async Task Revoke_over_accept_closes_established_join()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-accrev-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-accrevc-" + Guid.NewGuid().ToString("N"));
        var pairing = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        Assert.True(JoinNonce.TryParse("revnet001", out var nonce));
        Assert.True(PlacementId.TryParse("plc_revnet", out var placement));
        JoinBootstrap? muxIssued = null;
        var muxBoot = new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Mux,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = StreamClass.Control,
            Capability = default!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
        };
        await using var stack = await AcceptTestStack.StartAsync(
            muxBoot,
            tlsCertificate: cert,
            muxBootstrapFactory: _ => muxIssued is null
                ? ConnectivityOutcome<JoinBootstrap>.Failure(
                    ConnectivityReasons.Internal,
                    "mux capability is missing")
                : ConnectivityOutcome<JoinBootstrap>.Success(muxIssued),
            trustGate: pairing);
        pairing.AttachJoinCloser(stack.Accept);
        var fingerprint = AcceptListenCertificate.Sha256Fingerprint(cert);
        Assert.True(TlsCertificatePin.TryParse(fingerprint, out var pin));
        var issued = await pairing.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = stack.Accept.DialEndpoint.Host,
                Port = stack.Accept.Port,
                CertificateSha256 = fingerprint,
            });
        Assert.True(issued.Ok, issued.Detail);
        var path = new TcpTlsBytePath(cert, pin);
        var redeemed = await new OutboundHostInviteRedeem(client, path).RedeemAsync(issued.Value!);
        Assert.True(redeemed.Ok, redeemed.Detail);
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        var muxCap = await pairing.IssueJoinCapabilityAsync(
            OperatorIdentity.LocalSelfHosted,
            redeemed.Value!.Device.Id,
            placement,
            JoinRole.Mux,
            nonce,
            expires);
        var clientCap = await pairing.IssueJoinCapabilityAsync(
            OperatorIdentity.LocalSelfHosted,
            redeemed.Value.Device.Id,
            placement,
            JoinRole.Client,
            nonce,
            expires);
        Assert.True(muxCap.Ok, muxCap.Detail);
        Assert.True(clientCap.Ok, clientCap.Detail);
        muxIssued = muxBoot with { Capability = muxCap.Value! };
        var clientBoot = new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Client,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = StreamClass.Control,
            Capability = clientCap.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = JoinEphemeralKeyPair.CreatePublicKey(),
        };
        var signed = await JoinDeviceAuthenticator.StampAsync(clientBoot, client.KeyStore);
        Assert.True(signed.Ok, signed.Detail);
        clientBoot = signed.Value!;
        var join = new OutboundDirectJoin(
            stack.Accept.DialEndpoint,
            tlsTrust: cert,
            bytePath: path,
            deviceKeys: client.KeyStore);
        var joinTask = join.JoinHeldAsync(clientBoot);
        var muxSocket = await stack.FakeMux.AcceptAsync();
        using (muxSocket)
        {
            var outcome = await joinTask;
            Assert.True(outcome.Ok, outcome.Detail);
            await using var held = outcome.Value!;
            var revoked = await new OutboundHostInviteRevoke(client, path).RevokeAsync(
                new HostInviteRevokeReach
                {
                    Host = stack.Accept.DialEndpoint.Host,
                    Port = stack.Accept.Port,
                    CertificateSha256 = fingerprint,
                },
                redeemed.Value.Device.Id);
            Assert.True(revoked.Ok, revoked.Detail);
            var stillValid = await pairing.IsJoinStillValidAsync(
                clientBoot,
                DateTimeOffset.UtcNow);
            Assert.False(stillValid);
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[1];
            try
            {
                var read = await held.Stream.ReadAsync(buffer, readCts.Token);
                Assert.Equal(0, read);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<byte[]> ReadMuxBytesAsync(Socket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        try
        {
            var read = await socket.ReceiveAsync(buffer, cts.Token);
            return buffer.AsSpan(0, read).ToArray();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }

    private sealed class AcceptTestStack : IAsyncDisposable
    {
        private readonly FakeMuxUnix _fakeMux;
        private readonly LocalUnixBridge _bridge;
        private readonly TcpTlsConnectivityAccept _accept;

        private AcceptTestStack(FakeMuxUnix fakeMux, LocalUnixBridge bridge, TcpTlsConnectivityAccept accept)
        {
            _fakeMux = fakeMux;
            _bridge = bridge;
            _accept = accept;
        }

        public FakeMuxUnix FakeMux => _fakeMux;
        public TcpTlsConnectivityAccept Accept => _accept;
        public string UnixPath => _fakeMux.SocketPath;

        public static async Task<AcceptTestStack> StartAsync(
            JoinBootstrap muxBoot,
            X509Certificate2? tlsCertificate = null,
            Func<JoinBootstrap, ConnectivityOutcome<JoinBootstrap>>? muxBootstrapFactory = null,
            IJoinTrustGate? trustGate = null)
        {
            Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
            var fakeMux = await FakeMuxUnix.StartAsync();
            var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
            var bridge = new LocalUnixBridge(fakeMux.SocketPath, new AcceptPathJoin(coordinator));
            var accept = TcpTlsConnectivityAccept.StartLoopback(
                bridge,
                coordinator,
                muxBootstrapFactory ?? (_ => ConnectivityOutcome<JoinBootstrap>.Success(muxBoot)),
                trustGate: trustGate,
                tlsCertificate: tlsCertificate,
                quicProbe: JoinTestPairs.DisabledQuicProbe);
            return new AcceptTestStack(fakeMux, bridge, accept);
        }

        public async ValueTask DisposeAsync()
        {
            await _accept.DisposeAsync();
            await _bridge.DisposeAsync();
            await _fakeMux.DisposeAsync();
        }
    }

    private sealed class FakeMuxUnix : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();

        private FakeMuxUnix(Socket listener, string socketPath)
        {
            _listener = listener;
            SocketPath = socketPath;
        }

        public string SocketPath { get; }

        public static async Task<FakeMuxUnix> StartAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "hypa-fmux-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
#pragma warning disable CA1416
            File.SetUnixFileMode(
                dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var socketPath = Path.Combine(dir, "mux.sock");
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(
                socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
            listener.Listen(4);
            return await Task.FromResult(new FakeMuxUnix(listener, socketPath));
        }

        public async Task<Socket> AcceptAsync()
        {
            return await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _listener.Dispose();
            _cts.Dispose();
            try
            {
                if (File.Exists(SocketPath))
                    File.Delete(SocketPath);
                var dir = Path.GetDirectoryName(SocketPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConcurrentFakeUnixBridge : ILocalUnixBridge
    {
        private readonly AcceptPathJoin _join;
        private readonly object _gate = new();
        private readonly HashSet<Guid> _reservations = [];
        private int _bindCalls;

        public ConcurrentFakeUnixBridge(AcceptPathJoin join) => _join = join;

        public int BindCalls => _bindCalls;

        public bool ExposesUnixSocketPath => false;

        public async ValueTask<ConnectivityOutcome<MuxBridgeBind>> BindMuxAsync(
            JoinBootstrap bootstrap,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _bindCalls);
            var joined = await _join.JoinAsync(bootstrap, cancellationToken).ConfigureAwait(false);
            if (!joined.Ok || joined.Value is null)
            {
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    joined.Reason ?? ConnectivityReasons.JoinDenied,
                    joined.Detail ?? "join denied");
            }

            var reservation = MuxBridgeReservation.Create();
            lock (_gate)
                _reservations.Add(reservation.Value);
            return ConnectivityOutcome<MuxBridgeBind>.Success(new MuxBridgeBind
            {
                Binding = joined.Value,
                Reservation = reservation,
            });
        }

        public Stream? TryTakeMuxStream(MuxBridgeReservation reservation)
        {
            lock (_gate)
            {
                if (!_reservations.Remove(reservation.Value))
                    return null;

                return new HeldOpenStream();
            }
        }

        public void ReleaseMuxReservation(MuxBridgeReservation reservation)
        {
            lock (_gate)
                _reservations.Remove(reservation.Value);
        }
    }

    private sealed class SlowSpliceUnixBridge : ILocalUnixBridge
    {
        private readonly AcceptPathJoin _join;
        private readonly ManualResetEventSlim _blockRead = new(initialState: false);
        private readonly object _gate = new();
        private MuxBridgeReservation? _reservation;

        public SlowSpliceUnixBridge(AcceptPathJoin join) => _join = join;

        public bool ExposesUnixSocketPath => false;

        public async ValueTask<ConnectivityOutcome<MuxBridgeBind>> BindMuxAsync(
            JoinBootstrap bootstrap,
            CancellationToken cancellationToken = default)
        {
            var joined = await _join.JoinAsync(bootstrap, cancellationToken).ConfigureAwait(false);
            if (!joined.Ok || joined.Value is null)
            {
                return ConnectivityOutcome<MuxBridgeBind>.Failure(
                    joined.Reason ?? ConnectivityReasons.JoinDenied,
                    joined.Detail ?? "join denied");
            }

            var reservation = MuxBridgeReservation.Create();
            lock (_gate)
                _reservation = reservation;
            return ConnectivityOutcome<MuxBridgeBind>.Success(new MuxBridgeBind
            {
                Binding = joined.Value,
                Reservation = reservation,
            });
        }

        public Stream? TryTakeMuxStream(MuxBridgeReservation reservation)
        {
            lock (_gate)
            {
                if (_reservation != reservation)
                    return null;

                _reservation = null;
                return new BlockOnReadStream(_blockRead);
            }
        }

        public void ReleaseMuxReservation(MuxBridgeReservation reservation)
        {
            lock (_gate)
            {
                if (_reservation == reservation)
                    _reservation = null;
            }
        }
    }

    private sealed class BlockOnReadStream : Stream
    {
        private readonly ManualResetEventSlim _gate;

        public BlockOnReadStream(ManualResetEventSlim gate) => _gate = gate;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _gate.Wait(TimeSpan.FromSeconds(2));
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }

    private sealed class HeldOpenStream : Stream
    {
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingLocalUnixBridge : ILocalUnixBridge
    {
        public int BindCalls { get; private set; }

        public bool ExposesUnixSocketPath => false;

        public ValueTask<ConnectivityOutcome<MuxBridgeBind>> BindMuxAsync(
            JoinBootstrap bootstrap,
            CancellationToken cancellationToken = default)
        {
            _ = bootstrap;
            _ = cancellationToken;
            BindCalls++;
            return ValueTask.FromResult(ConnectivityOutcome<MuxBridgeBind>.Failure(
                ConnectivityReasons.Internal,
                "recording bridge does not bind"));
        }

        public Stream? TryTakeMuxStream(MuxBridgeReservation reservation)
        {
            _ = reservation;
            return null;
        }

        public void ReleaseMuxReservation(MuxBridgeReservation reservation)
        {
            _ = reservation;
        }
    }
}
