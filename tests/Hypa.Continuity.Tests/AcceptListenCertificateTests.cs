using System.Net;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class AcceptListenCertificateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hypa-cert-" + Guid.NewGuid().ToString("N"));

    public AcceptListenCertificateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void A_written_certificate_loads_with_the_same_key_and_fingerprint()
    {
        var pfx = AcceptListenCertificate.CreateSelfSignedPfx(IPAddress.Loopback);
        using var created = X509CertificateLoader.LoadPkcs12(
            pfx,
            AcceptListenCertificate.DefaultPfxPassword,
            X509KeyStorageFlags.Exportable);
        var path = Path.Combine(_dir, "accept.pfx");
        AcceptListenCertificate.WritePfx(path, pfx);

        var loaded = AcceptListenCertificate.LoadPfx(path);

        Assert.True(loaded.Ok, loaded.Detail);
        using var certificate = loaded.Value!;
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(
            AcceptListenCertificate.Sha256Fingerprint(created),
            AcceptListenCertificate.Sha256Fingerprint(certificate));
    }

    [Fact]
    public void A_certificate_file_with_an_empty_password_still_loads()
    {
        var path = Path.Combine(_dir, "old.pfx");
        File.WriteAllBytes(path, AcceptListenCertificate.CreateSelfSignedPfx(IPAddress.Loopback, string.Empty));

        var loaded = AcceptListenCertificate.LoadPfx(path);

        Assert.True(loaded.Ok, loaded.Detail);
        loaded.Value!.Dispose();
    }
}
