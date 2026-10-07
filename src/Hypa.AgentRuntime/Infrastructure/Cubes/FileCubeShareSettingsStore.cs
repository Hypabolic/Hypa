using System.Text.Json;
using System.Text.Json.Serialization;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.Cubes;

namespace Hypa.AgentRuntime.Infrastructure.Cubes;

/// <summary>
/// <c>share.json</c> beside the mux socket and <c>accept.pfx</c>. Present
/// means share is enabled. A missing or unreadable file means share is off.
/// </summary>
public sealed class FileCubeShareSettingsStore : ICubeShareSettingsStore
{
    public const string FileName = "share.json";

    private readonly string _path;

    public FileCubeShareSettingsStore(string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        _path = Path.Combine(Path.GetFullPath(runtimeDirectory), FileName);
    }

    public string FilePath => _path;

    public CubeShareSettings? LoadEnabled()
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            var document = JsonSerializer.Deserialize(
                File.ReadAllText(_path),
                CubeShareSettingsJsonContext.Default.CubeShareSettingsDocument);
            if (document is not { Enabled: true }
                || string.IsNullOrWhiteSpace(document.Bind)
                || document.Port is < 1 or > 65535)
            {
                return null;
            }

            return new CubeShareSettings { Bind = document.Bind, Port = document.Port };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public bool SaveEnabled(CubeShareSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return TryWrite(new CubeShareSettingsDocument { Enabled = true, Bind = settings.Bind, Port = settings.Port });
    }

    public bool Clear()
    {
        try
        {
            File.Delete(_path);
            return !File.Exists(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file we cannot delete may still be replaceable. Disabled intent
            // is as good as no file.
            return TryWrite(new CubeShareSettingsDocument { Enabled = false });
        }
    }

    private bool TryWrite(CubeShareSettingsDocument document)
    {
        var json = JsonSerializer.Serialize(
            document,
            CubeShareSettingsJsonContext.Default.CubeShareSettingsDocument);
        var temp = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(temp, json);
            }
            else
            {
                using var stream = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                using var writer = new StreamWriter(stream);
                writer.Write(json);
            }

            File.Move(temp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record CubeShareSettingsDocument
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("bind")]
    public string? Bind { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }
}

[JsonSerializable(typeof(CubeShareSettingsDocument))]
internal sealed partial class CubeShareSettingsJsonContext : JsonSerializerContext;
