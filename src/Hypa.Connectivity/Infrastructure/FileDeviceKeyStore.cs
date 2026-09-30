using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>File private-key store. Used when the platform store is not available.</summary>
public sealed class FileDeviceKeyStore : IDeviceKeyStore
{
    private readonly string _directory;

    public FileDeviceKeyStore(string directory, bool isPlatformStore = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        RestrictDirectory(_directory);
        IsPlatformStore = isPlatformStore;
    }

    public bool IsPlatformStore { get; }

    public string DirectoryPath => _directory;

    public async ValueTask StorePrivateKeyAsync(
        DeviceId deviceId,
        ReadOnlyMemory<byte> pkcs8,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(deviceId.Value))
            throw new ArgumentException("device id is required", nameof(deviceId));
        if (pkcs8.Length == 0)
            throw new ArgumentException("private key is required", nameof(pkcs8));

        var path = KeyPath(deviceId);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, pkcs8.ToArray(), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        RestrictFile(path);
    }

    public async ValueTask<byte[]?> LoadPrivateKeyAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken = default)
    {
        var path = KeyPath(deviceId);
        if (!File.Exists(path))
            return null;
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private string KeyPath(DeviceId deviceId) =>
        Path.Combine(_directory, deviceId.Value + ".pkcs8");

    private static void RestrictDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>
/// Platform key store when the OS user-private directory is available.
/// macOS: Application Support/Hypa/connectivity/device-keys.
/// </summary>
public sealed class PlatformDeviceKeyStore
{
    public static IDeviceKeyStore? TryCreateFromOs()
    {
        var root = DevicePairingStatePaths.ResolvePlatformKeyRoot();
        if (string.IsNullOrWhiteSpace(root))
            return null;
        return new FileDeviceKeyStore(root, isPlatformStore: true);
    }

    public static IDeviceKeyStore Create(string directory) =>
        new FileDeviceKeyStore(directory, isPlatformStore: true);

    public static IDeviceKeyStore CreateOrFallback(string fallbackDirectory)
    {
        return TryCreateFromOs() ?? new FileDeviceKeyStore(fallbackDirectory);
    }
}
