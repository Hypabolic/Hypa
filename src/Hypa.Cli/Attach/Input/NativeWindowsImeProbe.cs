using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Input;

/// <summary>
// / Win32 IME probe.
/// Other RIDs compile this type and never call it.
/// </summary>
internal sealed class NativeWindowsImeProbe : IWindowsImeProbe
{
    public static NativeWindowsImeProbe Instance { get; } = new();

    private const uint WmImeControl = 0x0283;
    private const nuint ImcGetOpenStatus = 0x0005;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ImeStatusReadTimeoutMs = 200;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;

    public nint ForegroundWindow()
    {
        if (!OperatingSystem.IsWindows())
            return 0;
#pragma warning disable CA1416
        return Native.GetForegroundWindow();
#pragma warning restore CA1416
    }

    public uint KeyboardLanguageId(nint hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0)
            return 0;
#pragma warning disable CA1416
        var thread = Native.GetWindowThreadProcessId(hwnd, out _);
        return (uint)((nuint)Native.GetKeyboardLayout(thread) & 0xFFFF);
#pragma warning restore CA1416
    }

    public nint DefaultImeWindow(nint hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0)
            return 0;
#pragma warning disable CA1416
        return Native.ImmGetDefaultIMEWnd(hwnd);
#pragma warning restore CA1416
    }

    public nint? ReadOpenStatus(nint imeHwnd)
    {
        if (!OperatingSystem.IsWindows() || imeHwnd == 0)
            return null;
#pragma warning disable CA1416
        var ret = Native.SendMessageTimeoutW(
            imeHwnd,
            WmImeControl,
            ImcGetOpenStatus,
            0,
            SmtoAbortIfHung,
            ImeStatusReadTimeoutMs,
            out var result);
#pragma warning restore CA1416
        if (ret == 0)
            return null;
        return (nint)result;
    }

    public bool SendVkTap(ushort vk)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        return KoreanImeToggle.QueueToggleTap(vk, count =>
        {
#pragma warning disable CA1416
            return Native.SendKeyboardTap(vk, count);
#pragma warning restore CA1416
        });
    }

    /// <summary>
    // / Win32 <c>INPUT</c> byte size.
    /// passes <c>size_of::&lt;INPUT&gt;()</c> as <c>cbSize</c>. The union must
    /// include mouse input so the record is 40 bytes on 64-bit.
    /// </summary>
    internal static int SendInputRecordSize
    {
        get
        {
#pragma warning disable CA1416
            return Native.RecordSize;
#pragma warning restore CA1416
        }
    }

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern nint GetKeyboardLayout(uint idThread);

        [DllImport("imm32.dll")]
        public static extern nint ImmGetDefaultIMEWnd(nint hWnd);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
        public static extern nint SendMessageTimeoutW(
            nint hWnd,
            uint msg,
            nuint wParam,
            nint lParam,
            uint flags,
            uint timeout,
            out nuint result);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static int RecordSize => Marshal.SizeOf<INPUT>();

        public static uint SendKeyboardTap(ushort vk, int count)
        {
            if (count <= 0)
                return 0;
            if (count == 1)
            {
                var up = new[] { KeyEvent(vk, KeyeventfKeyup) };
                return SendInput(1, up, RecordSize);
            }

            var tap = new[] { KeyEvent(vk, 0), KeyEvent(vk, KeyeventfKeyup) };
            return SendInput(2, tap, RecordSize);
        }

        private static INPUT KeyEvent(ushort vk, uint flags) =>
            new()
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeybdInput
                    {
                        Vk = vk,
                        Scan = 0,
                        Flags = flags,
                        Time = 0,
                        ExtraInfo = 0,
                    },
                },
            };

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mouse;

            [FieldOffset(0)]
            public KeybdInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public nuint ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeybdInput
        {
            public ushort Vk;
            public ushort Scan;
            public uint Flags;
            public uint Time;
            public nuint ExtraInfo;
        }
    }
}
