namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Switch the host keyboard to ASCII while prefix commands are active.
/// </summary>
public interface IPrefixAsciiInputSource
{
    /// <summary>
    /// Switch to an ASCII-capable input source. No-op when the current source
    /// is already ASCII, the platform does not switch, or the switch fails.
    /// A second call before <see cref="Restore"/> keeps the first saved source.
    /// </summary>
    void SwitchToAscii();

    /// <summary>Restore the source saved by <see cref="SwitchToAscii"/>. No-op when nothing was switched.</summary>
    void Restore();
}

/// <summary>No-op adapter. Disabled keys and unsupported platforms use this.</summary>
public sealed class NoOpPrefixAsciiInputSource : IPrefixAsciiInputSource
{
    public static NoOpPrefixAsciiInputSource Instance { get; } = new();

    public void SwitchToAscii()
    {
    }

    public void Restore()
    {
    }
}
