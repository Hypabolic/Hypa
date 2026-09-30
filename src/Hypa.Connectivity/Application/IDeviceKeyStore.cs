using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Store one device private key. Use the platform store when it is available.
/// </summary>
public interface IDeviceKeyStore
{
    bool IsPlatformStore { get; }

    string DirectoryPath { get; }

    ValueTask StorePrivateKeyAsync(
        DeviceId deviceId,
        ReadOnlyMemory<byte> pkcs8,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]?> LoadPrivateKeyAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken = default);
}
