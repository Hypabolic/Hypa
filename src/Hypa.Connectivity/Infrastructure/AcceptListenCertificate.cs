using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Self-signed listen certificate for the accept helper.
/// </summary>
public static class AcceptListenCertificate
{
    // Not a secret. The file mode (0600) protects the key. macOS refuses to
    // export or load a PKCS#12 with an empty password on some runners, so the
    // files Hypa writes carry this password. A file with an empty password
    // still loads.
    private const string PfxPassword = "hypa-accept-pfx";

    public const string DefaultPfxPassword = PfxPassword;

    public static X509Certificate2 CreateSelfSigned(IPAddress bindAddress) =>
        X509CertificateLoader.LoadPkcs12(
            CreateSelfSignedPfx(bindAddress, PfxPassword),
            PfxPassword,
            X509KeyStorageFlags.Exportable);

    /// <summary>
    /// Creates a self-signed certificate and returns it as PKCS#12 bytes.
    /// </summary>
    public static byte[] CreateSelfSignedPfx(IPAddress bindAddress, string password = PfxPassword)
    {
        ArgumentNullException.ThrowIfNull(bindAddress);
        // ECDSA, not RSA: the macOS RSA key export uses a legacy keychain path
        // that fails on runners with a locked keychain.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=hypa-accept",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        if (!bindAddress.Equals(IPAddress.Any)
            && !bindAddress.Equals(IPAddress.IPv6Any)
            && !IPAddress.IsLoopback(bindAddress))
        {
            san.AddIpAddress(bindAddress);
        }

        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        return BuildPfx(created, key, password);
    }

    /// <summary>
    /// Builds a PKCS#12 in managed code from the key object that made the
    /// certificate. A key read back through an <c>X509Certificate2</c> lives in
    /// a macOS keychain, and the runner keychain refuses to export it.
    /// </summary>
    public static byte[] BuildPfx(X509Certificate2 certificate, AsymmetricAlgorithm key, string password)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(password);
        // A public-only copy: the certificate object holds a keychain key on macOS.
        using var publicCertificate = X509CertificateLoader.LoadCertificate(certificate.RawData);
        var certificates = new Pkcs12SafeContents();
        certificates.AddCertificate(publicCertificate);
        var keys = new Pkcs12SafeContents();
        keys.AddShroudedKey(
            key,
            password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));
        var builder = new Pkcs12Builder();
        builder.AddSafeContentsUnencrypted(certificates);
        builder.AddSafeContentsUnencrypted(keys);
        builder.SealWithMac(password, HashAlgorithmName.SHA256, 100_000);
        return builder.Encode();
    }

    public static string Sha256Fingerprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
    }

    public static ConnectivityOutcome<X509Certificate2> LoadPfx(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ConnectivityOutcome<X509Certificate2>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "certificate path is required");
        }

        if (!File.Exists(path))
        {
            return ConnectivityOutcome<X509Certificate2>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "certificate file is missing");
        }

        try
        {
            return ConnectivityOutcome<X509Certificate2>.Success(LoadFile(path));
        }
        catch (CryptographicException)
        {
            return ConnectivityOutcome<X509Certificate2>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "certificate file is invalid");
        }
        catch (IOException)
        {
            return ConnectivityOutcome<X509Certificate2>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "certificate file is unreadable");
        }
    }

    private static X509Certificate2 LoadFile(string path)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                path,
                PfxPassword,
                X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            // A file from an earlier version has an empty password.
            return X509CertificateLoader.LoadPkcs12FromFile(
                path,
                password: null,
                X509KeyStorageFlags.Exportable);
        }
    }

    public static void WritePfx(string path, byte[] pfx)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(pfx);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(path, pfx);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
#pragma warning disable CA1416
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
        }
    }
}
