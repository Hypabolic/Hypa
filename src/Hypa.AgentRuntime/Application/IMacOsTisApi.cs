namespace Hypa.AgentRuntime.Application;

/// <summary>
// / macOS Text Input Source calls.
/// Tests fake this port. Production loads Carbon and CoreFoundation at run time.
/// </summary>
public interface IMacOsTisApi
{
    /// <summary>
    // / Drain pending input-source notifications.
    /// <c>src/platform/macos.rs:135-146</c> <c>pump_input_source_runloop</c>.
    /// </summary>
    void PumpRunLoop();

    nint CopyCurrentKeyboardInputSource();

    nint CopyAsciiCapableKeyboardLayoutInputSource();

    bool SameInputSourceId(nint left, nint right);

    int SelectInputSource(nint source);

    void Release(nint source);
}
