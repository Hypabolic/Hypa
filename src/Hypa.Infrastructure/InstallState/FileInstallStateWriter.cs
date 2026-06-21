using System.Text.Json;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain;

namespace Hypa.Infrastructure.InstallState;

public sealed class FileInstallStateWriter : IInstallStateWriter
{
    public void Write(HypaInstallState state)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var hypaDir = Path.Combine(home, ".hypa");
        Directory.CreateDirectory(hypaDir);

        var path = Path.Combine(hypaDir, "install-state.json");
        var json = JsonSerializer.Serialize(state, InstallStateJsonContext.Default.HypaInstallState);
        File.WriteAllText(path, json);
    }
}
