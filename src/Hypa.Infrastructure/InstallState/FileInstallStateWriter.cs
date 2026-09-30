using System.Text.Json;
using Hypa.Infrastructure.Storage;
using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain;

namespace Hypa.Infrastructure.InstallState;

public sealed class FileInstallStateWriter : IInstallStateWriter
{
    private readonly HypaDataOptions _dataOptions;

    // Back-compat public parameterless ctor: routes through the same default HypaDataOptions
    // (DataDirectory defaults to ~/.hypa via the record initializer — no hardcoded literal here).
    public FileInstallStateWriter() : this(new HypaDataOptions()) { }

    public FileInstallStateWriter(HypaDataOptions dataOptions)
    {
        _dataOptions = dataOptions;
    }

    public void Write(HypaInstallState state)
    {
        var hypaDir = _dataOptions.DataDirectory;
        Directory.CreateDirectory(hypaDir);

        var finalPath = Path.Combine(hypaDir, "install-state.json");
        var tempPath = Path.Combine(hypaDir, $".install-state-{Guid.NewGuid():N}.tmp");

        var json = JsonSerializer.Serialize(state, InstallStateJsonContext.Default.HypaInstallState);

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, finalPath, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup of temp file on failure.
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }
}
