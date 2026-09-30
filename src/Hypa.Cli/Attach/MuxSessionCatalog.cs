using System.Text.Json;
using Hypa.Cli.Mux;
using Hypa.ControlPlane;

namespace Hypa.Cli.Attach;

public sealed record MuxSessionListItem(
    string Name,
    string Socket,
    int Pid,
    bool Alive);

public sealed record MuxAttachClientState(
    bool Attached,
    string Session,
    string? PaneId,
    string? InputLeaseId,
    string? ResizeLeaseId,
    int Connections);

/// <summary>
/// Mux sessions live at <c>~/.config/hypa/runtime/&lt;name&gt;/</c>.
/// </summary>
public sealed class MuxSessionCatalog
{
    public const string AttachStateFileName = "attach.status.json";

    private readonly string _runtimeRoot;

    public MuxSessionCatalog(string? runtimeRoot = null)
    {
        _runtimeRoot = runtimeRoot ?? DefaultRuntimeRoot();
    }

    public string RuntimeRoot => _runtimeRoot;

    public static string DefaultRuntimeRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = "/tmp";
        return Path.Combine(home, ".config", "hypa", "runtime");
    }

    public static string SessionDirectory(string sessionName)
    {
        var socket = UnixSocketServer.ResolveSocketPath(sessionName, honorEnvironment: false);
        return Path.GetDirectoryName(socket) ?? DefaultRuntimeRoot();
    }

    /// <summary>
    /// Directory beside the socket <c>hypa status</c> will ping. Honours
    /// <c>HYPA_RUNTIME_SOCKET</c> so client attach state is not split from
    /// <c>runtime.status.json</c>.
    /// </summary>
    public static string AttachStateDirectory(string sessionName)
    {
        var socket = UnixSocketServer.ResolveSocketPath(sessionName);
        return Path.GetDirectoryName(socket) ?? DefaultRuntimeRoot();
    }

    public IReadOnlyList<string> DiscoverSessionDirs()
    {
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(_runtimeRoot))
        {
            foreach (var d in Directory.GetDirectories(_runtimeRoot))
                dirs.Add(d);
        }

        var envSocket = Environment.GetEnvironmentVariable("HYPA_RUNTIME_SOCKET");
        if (!string.IsNullOrWhiteSpace(envSocket))
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(envSocket));
            if (parent is not null)
                dirs.Add(parent);
        }

        var stateDir = Environment.GetEnvironmentVariable("HYPA_RUNTIME_STATE_DIR");
        if (!string.IsNullOrWhiteSpace(stateDir))
            dirs.Add(Path.GetFullPath(stateDir));

        return dirs.ToList();
    }

    public async Task<IReadOnlyList<MuxSessionListItem>> ListAsync(CancellationToken ct)
    {
        var items = new List<MuxSessionListItem>();
        foreach (var dir in DiscoverSessionDirs())
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dir);
            var socket = Path.Combine(dir, "hypa.sock");
            var statusPath = Path.Combine(dir, "runtime.status.json");
            var pid = 0;
            if (File.Exists(statusPath))
            {
                try
                {
                    await using var stream = File.OpenRead(statusPath);
                    var status = await JsonSerializer.DeserializeAsync(
                            stream, MuxJsonContext.Default.RuntimeStatusFile, ct)
                        .ConfigureAwait(false);
                    if (status is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(status.Session))
                            name = status.Session;
                        if (!string.IsNullOrWhiteSpace(status.Socket))
                            socket = status.Socket;
                        pid = status.Pid;
                    }
                }
                catch
                {
                    // keep directory name
                }
            }

            var ping = await MuxControlPlane.TryPingAsync(socket, ct).ConfigureAwait(false);
            items.Add(new MuxSessionListItem(name, socket, pid, ping is not null));
        }

        return items
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<RuntimeStatusFile?> TryReadStatusAsync(string session, CancellationToken ct)
    {
        string socket;
        try
        {
            socket = UnixSocketServer.ResolveSocketPath(session);
        }
        catch
        {
            return null;
        }

        var statusPath = Path.Combine(Path.GetDirectoryName(socket) ?? ".", "runtime.status.json");
        if (!File.Exists(statusPath))
            return null;

        try
        {
            await using var stream = File.OpenRead(statusPath);
            return await JsonSerializer.DeserializeAsync(
                    stream, MuxJsonContext.Default.RuntimeStatusFile, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public void WriteAttachState(string session, MuxAttachClientState state)
    {
        var dir = AttachStateDirectory(session);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, AttachStateFileName);
        var json = JsonSerializer.Serialize(state, MuxJsonContext.Default.MuxAttachClientState);
        File.WriteAllText(path, json);
    }

    public void ClearAttachState(string session)
    {
        var path = Path.Combine(AttachStateDirectory(session), AttachStateFileName);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort
        }
    }

    public MuxAttachClientState? TryReadAttachState(string session)
    {
        var path = Path.Combine(AttachStateDirectory(session), AttachStateFileName);
        if (!File.Exists(path))
            return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, MuxJsonContext.Default.MuxAttachClientState);
        }
        catch
        {
            return null;
        }
    }

    public bool TryDeleteSessionDir(string session, out string error)
    {
        error = string.Empty;
        string dir;
        try
        {
            dir = Path.Combine(_runtimeRoot, session);
            if (!Directory.Exists(dir))
                dir = SessionDirectory(session);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (!Directory.Exists(dir))
        {
            error = $"No session directory for '{session}'.";
            return false;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
