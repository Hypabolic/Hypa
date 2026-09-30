using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>Persist operator pairing state. Private keys are not this store.</summary>
public interface IDevicePairingStore
{
    string FilePath { get; }

    ValueTask<DevicePairingSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(DevicePairingSnapshot snapshot, CancellationToken cancellationToken = default);
}
