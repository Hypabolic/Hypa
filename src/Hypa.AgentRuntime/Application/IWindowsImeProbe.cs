namespace Hypa.AgentRuntime.Application;

/// <summary>
// / Windows IME probe for prefix ASCII.
/// <c>src/platform/windows.rs:2554-2683</c>. Tests fake this port.
/// </summary>
public interface IWindowsImeProbe
{
    nint ForegroundWindow();

    uint KeyboardLanguageId(nint hwnd);

    nint DefaultImeWindow(nint hwnd);

    nint? ReadOpenStatus(nint imeHwnd);

    bool SendVkTap(ushort vk);
}

/// <summary>
// / Restore token after a successful Korean IME toggle.
/// <c>src/platform/windows.rs:2634-2638</c> <c>InputSourceRestore</c>.
/// </summary>
public readonly record struct WindowsImeRestore(ushort ToggleVk, nint OriginHwnd);
