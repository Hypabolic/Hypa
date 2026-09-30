using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.Config;

namespace Hypa.Cli.Attach.ReleaseNotes;

/// <summary>
/// Persists the last pack-notes version the user dismissed.
/// Path follows attach config under Hypa dirs.
/// </summary>
public sealed class PackNotesSeenStore
{
    public const string FileName = "pack-notes-seen.json";

    private readonly IAttachConfigEnvironment _env;
    private readonly Func<string, bool>? _fileExists;
    private readonly Func<string, string>? _readAllText;
    private readonly Action<string, string>? _writeAtomic;

    public PackNotesSeenStore(
        IAttachConfigEnvironment? env = null,
        Func<string, bool>? fileExists = null,
        Func<string, string>? readAllText = null,
        Action<string, string>? writeAtomic = null)
    {
        _env = env ?? new SystemAttachConfigEnvironment();
        _fileExists = fileExists;
        _readAllText = readAllText;
        _writeAtomic = writeAtomic;
    }

    public string ResolvePath()
    {
        var explicitConfig = _env.GetVariable(FileAttachConfigLoader.ConfigPathVariable);
        if (!string.IsNullOrWhiteSpace(explicitConfig))
        {
            var directory = Path.GetDirectoryName(explicitConfig.Trim());
            if (string.IsNullOrWhiteSpace(directory))
                directory = ".";
            return Path.GetFullPath(Path.Combine(directory, FileName));
        }

        var xdg = _env.GetVariable(FileAttachConfigLoader.XdgConfigHomeVariable);
        if (!string.IsNullOrWhiteSpace(xdg))
            return Path.GetFullPath(Path.Combine(xdg.Trim(), "hypa", FileName));

        if (_env.IsWindows && !string.IsNullOrWhiteSpace(_env.AppData))
            return Path.GetFullPath(Path.Combine(_env.AppData, "hypa", FileName));

        return Path.GetFullPath(Path.Combine(_env.UserHome, ".config", "hypa", FileName));
    }

    public string? TryLoadSeenVersion()
    {
        var path = ResolvePath();
        if (!Exists(path))
            return null;

        string text;
        try
        {
            text = Read(path);
        }
        catch (Exception)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            var loaded = JsonSerializer.Deserialize(text, PackNotesSeenJsonContext.Default.PackNotesSeenState);
            return string.IsNullOrWhiteSpace(loaded?.Version) ? null : loaded.Version.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool TryMarkSeen(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var path = ResolvePath();
        var json = JsonSerializer.Serialize(
            new PackNotesSeenState(version.Trim()),
            PackNotesSeenJsonContext.Default.PackNotesSeenState);
        try
        {
            WriteAtomic(path, json);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool Exists(string path) => _fileExists?.Invoke(path) ?? File.Exists(path);

    private string Read(string path) => _readAllText?.Invoke(path) ?? File.ReadAllText(path);

    private void WriteAtomic(string path, string json)
    {
        if (_writeAtomic is not null)
        {
            _writeAtomic(path, json);
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        File.WriteAllText(temp, json);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }
}
