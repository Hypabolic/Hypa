using System.Text.Json;
using Hypa.Infrastructure.InstallState;
using Hypa.Infrastructure.Storage;

namespace Hypa.Infrastructure.Doctor;

internal static class InstallStateReader
{
    internal static string DefaultPath(HypaDataOptions dataOptions) =>
        Path.Combine(dataOptions.DataDirectory, "install-state.json");

    public static bool ReadInitWithMcp(HypaDataOptions dataOptions) =>
        ReadInitWithMcp(DefaultPath(dataOptions));

    public static bool ReadInitWithMcp(string stateFilePath)
    {
        if (!File.Exists(stateFilePath))
            return false;

        try
        {
            var content = File.ReadAllText(stateFilePath);
            var state = JsonSerializer.Deserialize(content, InstallStateJsonContext.Default.HypaInstallState);
            return state?.InitWithMcp ?? false;
        }
        catch
        {
            return false;
        }
    }
}
