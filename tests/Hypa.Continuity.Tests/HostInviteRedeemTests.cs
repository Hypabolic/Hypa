using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class HostInviteRedeemTests
{
    [SkippableFact]
    public async Task Redeem_over_accept_does_not_bind_unix_bridge()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-invh-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-invc-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        var recording = new RecordingBridge();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        DeviceRecord? paired = null;
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            recording,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.Internal,
                "join must not run"),
            trustGate: host,
            tlsCertificate: cert,
            quicProbe: JoinTestPairs.DisabledQuicProbe,
            onInviteRedeemed: device => paired = device);
        var fingerprint = AcceptListenCertificate.Sha256Fingerprint(cert);
        var issued = await host.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = accept.DialEndpoint.Host,
                Port = accept.Port,
                CertificateSha256 = fingerprint,
            });
        Assert.True(issued.Ok, issued.Detail);
        var redeem = new OutboundHostInviteRedeem(
            client,
            new TcpTlsBytePath(cert, TlsCertificatePin.TryParse(fingerprint, out var pin) ? pin : default));
        var outcome = await redeem.RedeemAsync(issued.Value!);
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.NotNull(paired);
        Assert.Equal(outcome.Value!.Device.Id, paired.Id);
        Assert.Equal(accept.DialEndpoint.Host, outcome.Value.Address);
        Assert.Equal(0, recording.BindCalls);
        var listed = await host.ListAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(listed.Ok);
        Assert.Contains(listed.Value!, d => d.Status == DeviceTrustStatus.Trusted);
    }

    [SkippableFact]
    public async Task Wrong_certificate_pin_never_sends_secret()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-invp-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-invpc-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        var recording = new RecordingBridge();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            recording,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.Internal,
                "join must not run"),
            trustGate: host,
            tlsCertificate: cert,
            quicProbe: JoinTestPairs.DisabledQuicProbe);
        var issued = await host.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = accept.DialEndpoint.Host,
                Port = accept.Port,
                CertificateSha256 = new string('a', 64),
            });
        Assert.True(issued.Ok, issued.Detail);
        Assert.True(TlsCertificatePin.TryParse(new string('a', 64), out var wrongPin));
        var redeem = new OutboundHostInviteRedeem(client, new TcpTlsBytePath(certificatePin: wrongPin));
        var outcome = await redeem.RedeemAsync(issued.Value!);
        Assert.False(outcome.Ok);
        Assert.Equal(ConnectivityReasons.PeerUnavailable, outcome.Reason);
        var listed = await host.ListAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(listed.Ok);
        Assert.DoesNotContain(listed.Value!, d => d.Status == DeviceTrustStatus.Trusted);
        var loaded = await new FileDevicePairingStore(hostDir).LoadAsync();
        Assert.Contains(loaded.HostInvites, i => !i.Consumed);
        Assert.Equal(0, recording.BindCalls);
    }

    [SkippableFact]
    public async Task Revoke_over_accept_does_not_bind_unix_bridge()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-invr-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-invrc-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        var recording = new RecordingBridge();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            recording,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.Internal,
                "join must not run"),
            trustGate: host,
            tlsCertificate: cert,
            quicProbe: JoinTestPairs.DisabledQuicProbe);
        var fingerprint = AcceptListenCertificate.Sha256Fingerprint(cert);
        Assert.True(TlsCertificatePin.TryParse(fingerprint, out var pin));
        var issued = await host.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = accept.DialEndpoint.Host,
                Port = accept.Port,
                CertificateSha256 = fingerprint,
            });
        Assert.True(issued.Ok, issued.Detail);
        var path = new TcpTlsBytePath(cert, pin);
        var redeem = await new OutboundHostInviteRedeem(client, path).RedeemAsync(issued.Value!);
        Assert.True(redeem.Ok, redeem.Detail);
        Assert.Equal(accept.DialEndpoint.Host, redeem.Value!.Address);
        var revoked = await new OutboundHostInviteRevoke(client, path).RevokeAsync(
            new HostInviteRevokeReach
            {
                Host = accept.DialEndpoint.Host,
                Port = accept.Port,
                CertificateSha256 = fingerprint,
            },
            redeem.Value!.Device.Id);
        Assert.True(revoked.Ok, revoked.Detail);
        Assert.Equal(0, recording.BindCalls);
        var listed = await host.ListAsync(OperatorIdentity.LocalSelfHosted);
        Assert.True(listed.Ok);
        Assert.Contains(listed.Value!, d =>
            d.Id.Value == redeem.Value.Device.Id.Value && d.Status == DeviceTrustStatus.Revoked);
    }

    [SkippableFact]
    public async Task Redeem_uses_the_second_address_when_the_first_does_not_answer()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        Skip.IfNot(CanBindLoopbackAlias(), "IPv6 loopback is not available");
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-inv2-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-inv2c-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        await using var accept = StartAliasAccept(host, cert);
        var fingerprint = AcceptListenCertificate.Sha256Fingerprint(cert);
        var issued = await host.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = "127.0.0.1",
                Hosts = ["127.0.0.1", "::1"],
                Port = accept.Port,
                CertificateSha256 = fingerprint,
            });
        Assert.True(issued.Ok, issued.Detail);
        Assert.True(TlsCertificatePin.TryParse(fingerprint, out var pin));
        var outcome = await new OutboundHostInviteRedeem(client, new TcpTlsBytePath(certificatePin: pin))
            .RedeemAsync(issued.Value!);
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal("::1", outcome.Value!.Address);
        Assert.Equal(DeviceTrustStatus.Trusted, outcome.Value.Device.Status);
    }

    [SkippableFact]
    public async Task Old_single_host_invite_still_redeems()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-invo1-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-invo1c-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        var recording = new RecordingBridge();
        var coordinator = new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0);
        await using var accept = TcpTlsConnectivityAccept.StartLoopback(
            recording,
            coordinator,
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.Internal,
                "join must not run"),
            trustGate: host,
            tlsCertificate: cert,
            quicProbe: JoinTestPairs.DisabledQuicProbe);
        var fingerprint = AcceptListenCertificate.Sha256Fingerprint(cert);
        var issued = await host.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = accept.DialEndpoint.Host,
                Port = accept.Port,
                CertificateSha256 = fingerprint,
                Session = "default",
            });
        Assert.True(issued.Ok, issued.Detail);
        Assert.True(HostInviteFormat.TryUnwrap(issued.Value!.TransferValue, out var bytes, out _));
        var document = JsonNode.Parse(bytes)!.AsObject();
        document.Remove("hosts");
        var legacy = HostInviteCodec.Decode(HostInviteFormat.Wrap(document.ToJsonString()));
        Assert.True(legacy.Ok, legacy.Detail);
        Assert.Equal([accept.DialEndpoint.Host], legacy.Value!.Hosts);
        Assert.True(TlsCertificatePin.TryParse(fingerprint, out var pin));
        var outcome = await new OutboundHostInviteRedeem(client, new TcpTlsBytePath(certificatePin: pin))
            .RedeemAsync(legacy.Value);
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal(accept.DialEndpoint.Host, outcome.Value!.Address);
        Assert.Equal(0, recording.BindCalls);
    }

    [SkippableFact]
    public async Task Wrong_pin_on_one_address_never_sends_the_secret()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        Skip.IfNot(CanBindLoopbackAlias(), "IPv6 loopback is not available");
        using var goodCert = JoinTestPairs.CreateLoopbackCertificate();
        using var wrongCert = JoinTestPairs.CreateLoopbackCertificate();
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-invw-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-invwc-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        await using var accept = StartAliasAccept(host, goodCert);
        await using var wrong = TlsApplicationCapture.Start(IPAddress.Parse("127.0.0.1"), accept.Port, wrongCert);
        var fingerprint = AcceptListenCertificate.Sha256Fingerprint(goodCert);
        var issued = await host.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = "127.0.0.1",
                Hosts = ["127.0.0.1", "::1"],
                Port = accept.Port,
                CertificateSha256 = fingerprint,
            });
        Assert.True(issued.Ok, issued.Detail);
        Assert.True(TlsCertificatePin.TryParse(fingerprint, out var pin));
        var outcome = await new OutboundHostInviteRedeem(client, new TcpTlsBytePath(certificatePin: pin))
            .RedeemAsync(issued.Value!);
        await wrong.Completed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal("::1", outcome.Value!.Address);
        Assert.Equal(1, wrong.Accepted);
        Assert.True(string.IsNullOrEmpty(wrong.Line) || !wrong.Line.Contains(issued.Value!.Secret, StringComparison.Ordinal));
        var loaded = await new FileDevicePairingStore(hostDir).LoadAsync();
        Assert.Contains(loaded.HostInvites, invite => invite.Consumed && invite.FailedAttempts == 0);
    }

    [SkippableFact]
    public async Task Redeem_uses_the_live_address_when_the_first_address_does_not_answer()
    {
        Skip.IfNot(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());
        Skip.IfNot(CanBindLoopbackAlias(), "IPv6 loopback is not available");
        using var cert = JoinTestPairs.CreateLoopbackCertificate();
        var hostDir = Path.Combine(Path.GetTempPath(), "hypa-invd-" + Guid.NewGuid().ToString("N"));
        var clientDir = Path.Combine(Path.GetTempPath(), "hypa-invdc-" + Guid.NewGuid().ToString("N"));
        var host = new DevicePairingService(
            new FileDevicePairingStore(hostDir),
            new FileDeviceKeyStore(hostDir));
        var client = new DevicePairingService(
            new FileDevicePairingStore(clientDir),
            new FileDeviceKeyStore(clientDir));
        await using var accept = StartAliasAccept(host, cert);
        var fingerprint = AcceptListenCertificate.Sha256Fingerprint(cert);
        var issued = await host.IssueHostInviteAsync(
            OperatorIdentity.LocalSelfHosted,
            new HostInviteIssueRequest
            {
                Host = "203.0.113.8",
                Hosts = ["203.0.113.8", "::1"],
                Port = accept.Port,
                CertificateSha256 = fingerprint,
            });
        Assert.True(issued.Ok, issued.Detail);
        Assert.True(TlsCertificatePin.TryParse(fingerprint, out var pin));
        var path = new HoldUntilCancelPath(
            new TcpTlsBytePath(certificatePin: pin),
            "203.0.113.8");
        using var outer = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var started = Stopwatch.StartNew();
        var outcome = await new OutboundHostInviteRedeem(client, path)
            .RedeemAsync(issued.Value!, outer.Token);
        started.Stop();
        Assert.True(outcome.Ok, outcome.Detail);
        Assert.Equal("::1", outcome.Value!.Address);
        Assert.Equal(DeviceTrustStatus.Trusted, outcome.Value.Device.Status);
        Assert.InRange(
            started.Elapsed,
            OutboundHostInviteRedeem.AddressAttemptTimeout - TimeSpan.FromSeconds(1),
            OutboundHostInviteRedeem.AddressAttemptTimeout + TimeSpan.FromSeconds(8));
    }

    private static bool CanBindLoopbackAlias()
    {
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static TcpTlsConnectivityAccept StartAliasAccept(DevicePairingService host, X509Certificate2 cert)
    {
        var accept = new TcpTlsConnectivityAccept(
            new ConnectivityAcceptBind
            {
                Host = IPAddress.IPv6Loopback.ToString(),
                Port = 0,
                Tls = true,
                TlsServerName = "localhost",
            },
            new RecordingBridge(),
            new AcceptJoinCoordinator(RendezvousRelayIdentity.SelfHostedV0),
            _ => ConnectivityOutcome<JoinBootstrap>.Failure(
                ConnectivityReasons.Internal,
                "join must not run"),
            trustGate: host,
            tlsCertificate: cert,
            quicProbe: JoinTestPairs.DisabledQuicProbe);
        accept.Run();
        return accept;
    }

    private sealed class HoldUntilCancelPath : IBytePath
    {
        private readonly IBytePath _live;
        private readonly string _holdHost;

        public HoldUntilCancelPath(IBytePath live, string holdHost)
        {
            _live = live;
            _holdHost = holdHost;
        }

        public string Provider => _live.Provider;

        public async ValueTask<ConnectivityOutcome<BytePathHandle>> OpenAsync(
            BytePathRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(request.Endpoint?.Host, _holdHost, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return ConnectivityOutcome<BytePathHandle>.Failure(
                    ConnectivityReasons.PeerUnavailable,
                    "address did not answer");
            }

            return await _live.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask CloseAsync(BytePathHandle handle, CancellationToken cancellationToken = default) =>
            _live.CloseAsync(handle, cancellationToken);
    }

    private sealed class TlsApplicationCapture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly X509Certificate2 _certificate;

        private TlsApplicationCapture(IPAddress address, int port, X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener = new TcpListener(address, port);
            _listener.Start();
            Completed = AcceptLoopAsync();
        }

        public Task Completed { get; }

        public string? Line { get; private set; }

        public int Accepted { get; private set; }

        public static TlsApplicationCapture Start(IPAddress address, int port, X509Certificate2 certificate) =>
            new(address, port, certificate);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await Completed.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Accepted++;
                await using var stream = client.GetStream();
                using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    },
                    _cts.Token);
                var line = await BoundedJoinLine.ReadAsync(ssl, _cts.Token);
                if (line.Ok)
                    Line = line.Value;
            }
            catch (Exception)
            {
            }
        }
    }

    private sealed class RecordingBridge : ILocalUnixBridge
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
