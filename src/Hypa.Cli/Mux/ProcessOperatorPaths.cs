using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Hypa.Placement.Application;
using Hypa.Placement.Infrastructure;

namespace Hypa.Cli.Mux;

/// <summary>
/// Placement and pairing directories for this process.
/// The login home resolver runs once, at construction.
/// </summary>
public sealed class ProcessOperatorPaths
{
    private ProcessOperatorPaths(string? placementDirectory, string? pairingDirectory)
    {
        PlacementDirectory = placementDirectory;
        PairingDirectory = pairingDirectory;
    }

    public string? PlacementDirectory { get; }

    public string? PairingDirectory { get; }

    public static ProcessOperatorPaths Resolve(Func<string?>? loginHome = null)
    {
        var home = loginHome is null
            ? PlacementStatePaths.ResolveLoginHome()
            : loginHome();
        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var operatorHome = Environment.GetEnvironmentVariable(PlacementStatePaths.OperatorHomeVariable);
        return new ProcessOperatorPaths(
            TryResolve(() => PlacementStatePaths.Resolve(stateHome, operatorHome, home)),
            TryResolve(() => DevicePairingStatePaths.Resolve(
                Environment.GetEnvironmentVariable(DevicePairingPaths.PairingStoreVariable),
                stateHome,
                Environment.GetEnvironmentVariable(DevicePairingPaths.OperatorHomeVariable),
                home)));
    }

    public IPlacementDirectory OpenPlacementDirectory()
    {
        var root = PlacementDirectory ?? PlacementStatePaths.ResolveFromEnvironment();
        return new PlacementDirectoryService(new FilePlacementDirectoryStore(root));
    }

    public DevicePairingService? TryCreatePairing()
    {
        if (string.IsNullOrWhiteSpace(PairingDirectory))
            return null;

        try
        {
            Directory.CreateDirectory(PairingDirectory);
            var keys = PlatformDeviceKeyStore.CreateOrFallback(
                DevicePairingStatePaths.FallbackKeyDirectory(PairingDirectory));
            return new DevicePairingService(new FileDevicePairingStore(PairingDirectory), keys);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryResolve(Func<string> resolve)
    {
        try
        {
            return resolve();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
