using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Infrastructure.Config;

/// <summary>
/// One lock. One immutable Current. Bind uses <see cref="IAttachConfigLoader"/>.
/// Fail closed: invalid TOML keeps Current.
/// </summary>
public sealed class LiveAttachConfigRuntime : IAttachConfigRuntime
{
    private readonly IAttachConfigLoader _loader;
    private readonly object _gate = new();
    private AttachClientConfig _current;

    public LiveAttachConfigRuntime(IAttachConfigLoader loader, AttachClientConfig? initial = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _current = initial ?? AttachClientConfig.Default;
    }

    public AttachClientConfig Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public AttachConfigReloadReport ReloadFromDisk()
    {
        lock (_gate)
        {
            var loaded = _loader.Load();
            if (!loaded.IsOk)
            {
                return AttachConfigReloadReport.Failed(
                    loaded.Errors.Select(static e => e.ToString()).ToArray());
            }

            _current = loaded.Value;
            return AttachConfigReloadReport.Applied();
        }
    }
}
