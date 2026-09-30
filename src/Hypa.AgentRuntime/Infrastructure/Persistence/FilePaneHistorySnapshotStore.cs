using System.Text.Json;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentRuntime.Infrastructure.Persistence;

/// <summary>
// History lives in
/// <c>session-history.json</c> next to the session graph, not inside it.
/// </summary>
public sealed class FilePaneHistorySnapshotStore : IPaneHistorySnapshotStore
{
    private readonly string _path;

    public FilePaneHistorySnapshotStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _path = Path.Combine(stateDirectory, RuntimeStatePaths.HistoryFileName);
    }

    public string FilePath => _path;

    public SessionHistorySnapshot? Load()
    {
        if (!File.Exists(_path))
            return null;

        string content;
        try
        {
            content = File.ReadAllText(_path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return Parse(content);
    }

    public RuntimeResult<RuntimeUnit> Save(SessionHistorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(snapshot, SessionHistoryJsonContext.Default.SessionHistorySnapshot);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            try
            {
                File.Move(tmp, _path, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (IOException)
                {
                }

                throw;
            }

            return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
    }

    public RuntimeResult<RuntimeUnit> Clear()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
            return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
        }
        catch (FileNotFoundException)
        {
            return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
        }
        catch (DirectoryNotFoundException)
        {
            return RuntimeResult<RuntimeUnit>.Ok(RuntimeUnit.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RuntimeResult<RuntimeUnit>.Fail(RuntimePersistenceError.Io(ex.Message));
        }
    }

    internal static SessionHistorySnapshot? Parse(string content)
    {
        try
        {
            var snapshot = JsonSerializer.Deserialize(
                content,
                SessionHistoryJsonContext.Default.SessionHistorySnapshot);
            if (snapshot is null)
                return null;
            if (snapshot.Version > SessionHistorySnapshot.FormatVersion)
                return null;
            return snapshot;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
