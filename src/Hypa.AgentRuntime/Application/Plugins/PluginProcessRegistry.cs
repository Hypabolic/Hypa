namespace Hypa.AgentRuntime.Application.Plugins;

/// <summary>
/// Live PIDs of plugin commands spawned by the host launcher.
/// Used to attribute Unix socket peers without caller-supplied tokens.
/// </summary>
public interface IPluginProcessRegistry
{
    void Register(int pid);

    void Unregister(int pid);

    bool IsLive(int pid);
}

public sealed class PluginProcessRegistry : IPluginProcessRegistry
{
    private readonly object _gate = new();
    private readonly HashSet<int> _live = [];

    public void Register(int pid)
    {
        if (pid <= 0)
            return;
        lock (_gate)
            _live.Add(pid);
    }

    public void Unregister(int pid)
    {
        if (pid <= 0)
            return;
        lock (_gate)
            _live.Remove(pid);
    }

    public bool IsLive(int pid)
    {
        if (pid <= 0)
            return false;
        lock (_gate)
            return _live.Contains(pid);
    }
}
