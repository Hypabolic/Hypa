namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Restore token for the previous macOS input source.
/// <c>src/platform/macos.rs:181-196</c> <c>InputSourceRestore</c>.
/// </summary>
public sealed class MacOsInputSourceRestore : IDisposable
{
    private readonly IMacOsTisApi _api;
    private nint _previous;
    private bool _disposed;

    public MacOsInputSourceRestore(IMacOsTisApi api, nint previous)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _previous = previous;
    }

    public nint PreviousSource => _previous;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_previous == 0)
            return;
        _api.SelectInputSource(_previous);
        _api.Release(_previous);
        _previous = 0;
    }
}

/// <summary>
// / Switch to the ASCII-capable macOS layout.
/// <c>src/platform/macos.rs:198-219</c> <c>switch_to_ascii_input_source</c>.
/// </summary>
public static class MacOsAsciiInputSwitch
{
    public static MacOsInputSourceRestore? SwitchToAscii(IMacOsTisApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var current = api.CopyCurrentKeyboardInputSource();
        if (current == 0)
            return null;
        var ascii = api.CopyAsciiCapableKeyboardLayoutInputSource();
        if (ascii == 0)
        {
            api.Release(current);
            return null;
        }

        if (api.SameInputSourceId(current, ascii))
        {
            api.Release(current);
            api.Release(ascii);
            return null;
        }

        var status = api.SelectInputSource(ascii);
        api.Release(ascii);
        if (status != 0)
        {
            api.Release(current);
            return null;
        }

        return new MacOsInputSourceRestore(api, current);
    }
}
