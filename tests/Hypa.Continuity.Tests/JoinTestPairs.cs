using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.Continuity.Tests;

internal static class JoinTestPairs
{
    public static (JoinBootstrap Mux, JoinBootstrap Client) SelfHosted(
        string placementId,
        string nonceValue,
        StreamClass streamClass = StreamClass.Control,
        string muxDevice = "dev_mux001",
        string clientDevice = "dev_cli001",
        JoinSecret? secret = null,
        DateTimeOffset? utcNow = null)
    {
        Assert.True(JoinNonce.TryParse(nonceValue, out var nonce));
        Assert.True(DeviceId.TryParse(muxDevice, out var muxDeviceId));
        Assert.True(DeviceId.TryParse(clientDevice, out var clientDeviceId));
        Assert.True(PlacementId.TryParse(placementId, out var placement));
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(1);
        var joinSecret = secret ?? JoinSecret.Create();
        var issuer = new DeveloperJoinCapabilityIssuer();
        var muxCap = issuer.Issue(placement, muxDeviceId, JoinRole.Mux, nonce, expires, now, joinSecret);
        var clientCap = issuer.Issue(placement, clientDeviceId, JoinRole.Client, nonce, expires, now, joinSecret);
        Assert.True(muxCap.Ok, muxCap.Detail);
        Assert.True(clientCap.Ok, clientCap.Detail);
        return (Stamp(new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Mux,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = streamClass,
            Capability = muxCap.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = "",
        }), Stamp(new JoinBootstrap
        {
            ProtocolVersion = ConnectivityProtocolVersion.V0,
            Role = JoinRole.Client,
            PlacementId = placement,
            Nonce = nonce,
            StreamClass = streamClass,
            Capability = clientCap.Value!,
            Audience = RendezvousAudiences.Rendezvous,
            TenantScope = RendezvousTenants.Local,
            EphPublicKey = "",
        }));
    }

    public static JoinBootstrap Stamp(JoinBootstrap bootstrap)
    {
        var key = string.IsNullOrWhiteSpace(bootstrap.EphPublicKey)
            ? JoinEphemeralKeyPair.CreatePublicKey()
            : bootstrap.EphPublicKey;
        var mac = JoinEphAuthenticator.CreateMac(bootstrap.Capability.Secret, key);
        Assert.True(mac.Ok, mac.Detail);
        return bootstrap with
        {
            EphPublicKey = key,
            EphPublicMac = mac.Value!,
        };
    }

    public static X509Certificate2 CreateLoopbackCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=127.0.0.1",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName("localhost");
        san.AddDnsName("127.0.0.1");
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(7));
        // macOS refuses to export or load a PKCS#12 with an empty password.
        const string password = "hypa-test-cert";
        return X509CertificateLoader.LoadPkcs12(
            AcceptListenCertificate.BuildPfx(created, key, password),
            password,
            X509KeyStorageFlags.Exportable);
    }

    public static IQuicTransportCapabilityProbe DisabledQuicProbe { get; } = new DisabledProbe();

    private sealed class DisabledProbe : IQuicTransportCapabilityProbe
    {
        public QuicTransportCapabilityReport Probe() =>
            new()
            {
                RuntimeIdentifier = "test",
                IsSupported = false,
                NativeLibraryFound = false,
                NativeLibraryLocation = MsQuicNativeLocations.Absent,
                QuicProvider = BytePathProviders.Quic,
                FallbackProvider = BytePathProviders.Tcp,
                ZeroRttEnabled = false,
                Reason = ConnectivityReasons.QuicUnsupported,
                Detail = "test accept stack disables quic",
            };
    }
}
