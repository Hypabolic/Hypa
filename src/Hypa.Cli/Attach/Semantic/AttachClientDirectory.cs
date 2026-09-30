using System.Text.Json;

namespace Hypa.Cli.Attach;

/// <summary>
/// Maps an attach client id to its action socket.
/// The socket file is <c>c{pid}.sock</c> beside the mux socket.
/// </summary>
internal static class AttachClientDirectory
{
    public const string IndexFileName = "attach-clients.json";

    public static string SocketPath(string sessionDir, int pid) =>
        Path.Combine(sessionDir, $"c{pid}.sock");

    public static void Register(string sessionDir, AttachClientRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDir);
        ArgumentNullException.ThrowIfNull(record);
        WithLock(sessionDir, path =>
        {
            var index = Read(path);
            var clients = (index.Clients ?? [])
                .Where(client => !SameClient(client, record))
                .ToList();
            clients.Add(record);
            Write(path, new AttachClientIndex { Clients = clients });
        });
    }

    public static void Unregister(string sessionDir, string attachClientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDir);
        if (string.IsNullOrWhiteSpace(attachClientId) || !Directory.Exists(sessionDir))
            return;

        WithLock(sessionDir, path =>
        {
            var index = Read(path);
            var clients = (index.Clients ?? [])
                .Where(client => !string.Equals(client.AttachClientId, attachClientId, StringComparison.Ordinal))
                .ToList();
            Write(path, new AttachClientIndex { Clients = clients });
        });
    }

    public static IReadOnlyList<AttachClientRecord> List(string sessionDir)
    {
        if (string.IsNullOrWhiteSpace(sessionDir) || !Directory.Exists(sessionDir))
            return [];

        var path = Path.Combine(sessionDir, IndexFileName);
        return WithLock(sessionDir, _ => Read(path).Clients ?? []);
    }

    public static IReadOnlyList<AttachClientRecord> ListLive(string sessionDir)
    {
        var live = new List<AttachClientRecord>();
        foreach (var client in List(sessionDir))
        {
            if (IsProcessAlive(client.Pid))
            {
                live.Add(client);
                continue;
            }

            Unregister(sessionDir, client.AttachClientId);
        }

        return live;
    }

    private static bool SameClient(AttachClientRecord client, AttachClientRecord record) =>
        string.Equals(client.AttachClientId, record.AttachClientId, StringComparison.Ordinal)
        || client.Pid == record.Pid;

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void WithLock(string sessionDir, Action<string> action)
    {
        WithLock(sessionDir, path =>
        {
            action(path);
            return 0;
        });
    }

    private static T WithLock<T>(string sessionDir, Func<string, T> action)
    {
        Directory.CreateDirectory(sessionDir);
        var indexPath = Path.Combine(sessionDir, IndexFileName);
        var lockPath = Path.Combine(sessionDir, "attach-clients.lock");
        using var stream = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        return action(indexPath);
    }

    private static AttachClientIndex Read(string path)
    {
        if (!File.Exists(path))
            return new AttachClientIndex();

        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new AttachClientIndex();

        try
        {
            return JsonSerializer.Deserialize(text, AttachSemanticJsonContext.Default.AttachClientIndex)
                ?? new AttachClientIndex();
        }
        catch (JsonException)
        {
            return new AttachClientIndex();
        }
    }

    private static void Write(string path, AttachClientIndex index)
    {
        var json = JsonSerializer.Serialize(index, AttachSemanticJsonContext.Default.AttachClientIndex);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
