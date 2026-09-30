namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Production <see cref="IPrefixAsciiInputSource"/>.
/// <c>src/platform/mod.rs:458-477</c> <c>RealPrefixInputSource</c>.
/// A second switch before restore keeps the first saved source.
/// </summary>
public sealed class PrefixAsciiInputAdapter : IPrefixAsciiInputSource
{
    private readonly Action _pump;
    private readonly Func<IDisposable?> _switchToAscii;
    private IDisposable? _restore;

    public PrefixAsciiInputAdapter(Func<IDisposable?> switchToAscii, Action? pump = null)
    {
        ArgumentNullException.ThrowIfNull(switchToAscii);
        _switchToAscii = switchToAscii;
        _pump = pump ?? Noop;
    }

    public void SwitchToAscii()
    {
        if (_restore is not null)
            return;
        _pump();
        _restore = _switchToAscii();
    }

    /// <summary>
    /// Restore the source saved by <see cref="SwitchToAscii"/>. No-op when
    // / nothing was switched. A second call is a no-op.
    /// <c>src/platform/mod.rs:474-476</c> <c>RealPrefixInputSource::restore</c>.
    /// </summary>
    public void Restore()
    {
        _restore?.Dispose();
        _restore = null;
    }

    private static void Noop()
    {
    }
}
