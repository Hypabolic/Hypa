using System.Text.Json;
using Hypa.Infrastructure.InstallState;

namespace Hypa.Infrastructure.Doctor;

internal static class InstallStateReader
{
    private static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".hypa", "install-state.json");
    }

    public static bool ReadInitWithMcp() => ReadInitWithMcp(DefaultPath());

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
