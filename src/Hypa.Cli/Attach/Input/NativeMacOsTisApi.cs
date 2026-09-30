using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Input;

/// <summary>
// / Carbon TIS adapter.
/// Loads frameworks only on macOS. Other RIDs compile this type and never call it.
/// </summary>
internal sealed class NativeMacOsTisApi : IMacOsTisApi
{
    public static NativeMacOsTisApi Instance { get; } = new();

    private const string CarbonPath = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string CoreFoundationPath =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private readonly object _gate = new();
    private nint _inputSourceIdKey;
    private nint _runLoopDefaultMode;
    private bool _constantsResolved;

    public void PumpRunLoop()
    {
        if (!OperatingSystem.IsMacOS() || !TryConstants())
            return;
#pragma warning disable CA1416
        _ = Native.CFRunLoopRunInMode(_runLoopDefaultMode, 0.0, 0);
#pragma warning restore CA1416
    }

    public nint CopyCurrentKeyboardInputSource()
    {
        if (!OperatingSystem.IsMacOS())
            return 0;
#pragma warning disable CA1416
        return Native.TISCopyCurrentKeyboardInputSource();
#pragma warning restore CA1416
    }

    public nint CopyAsciiCapableKeyboardLayoutInputSource()
    {
        if (!OperatingSystem.IsMacOS())
            return 0;
#pragma warning disable CA1416
        return Native.TISCopyCurrentASCIICapableKeyboardLayoutInputSource();
#pragma warning restore CA1416
    }

    public bool SameInputSourceId(nint left, nint right)
    {
        if (!OperatingSystem.IsMacOS() || left == 0 || right == 0 || !TryConstants())
            return false;
#pragma warning disable CA1416
        var leftId = Native.TISGetInputSourceProperty(left, _inputSourceIdKey);
        var rightId = Native.TISGetInputSourceProperty(right, _inputSourceIdKey);
        return leftId != 0 && rightId != 0 && Native.CFEqual(leftId, rightId) != 0;
#pragma warning restore CA1416
    }

    public int SelectInputSource(nint source)
    {
        if (!OperatingSystem.IsMacOS() || source == 0)
            return -1;
#pragma warning disable CA1416
        return Native.TISSelectInputSource(source);
#pragma warning restore CA1416
    }

    public void Release(nint source)
    {
        if (!OperatingSystem.IsMacOS() || source == 0)
            return;
#pragma warning disable CA1416
        Native.CFRelease(source);
#pragma warning restore CA1416
    }

    private bool TryConstants()
    {
        if (_constantsResolved)
            return _inputSourceIdKey != 0 && _runLoopDefaultMode != 0;
        lock (_gate)
        {
            if (_constantsResolved)
                return _inputSourceIdKey != 0 && _runLoopDefaultMode != 0;
            _constantsResolved = true;
            if (!OperatingSystem.IsMacOS())
                return false;
            if (!NativeLibrary.TryLoad(CarbonPath, out var carbon)
                || !NativeLibrary.TryLoad(CoreFoundationPath, out var coreFoundation))
            {
                return false;
            }

            if (!TryReadPointer(carbon, "kTISPropertyInputSourceID", out _inputSourceIdKey)
                || !TryReadPointer(coreFoundation, "kCFRunLoopDefaultMode", out _runLoopDefaultMode))
            {
                return false;
            }

            return true;
        }
    }

    private static bool TryReadPointer(nint library, string name, out nint value)
    {
        try
        {
            var export = NativeLibrary.GetExport(library, name);
            value = Marshal.ReadIntPtr(export);
            return value != 0;
        }
        catch (EntryPointNotFoundException)
        {
            value = 0;
            return false;
        }
    }

    [SupportedOSPlatform("macos")]
    private static class Native
    {
        [DllImport(CarbonPath, EntryPoint = "TISCopyCurrentKeyboardInputSource")]
        public static extern nint TISCopyCurrentKeyboardInputSource();

        [DllImport(CarbonPath, EntryPoint = "TISCopyCurrentASCIICapableKeyboardLayoutInputSource")]
        public static extern nint TISCopyCurrentASCIICapableKeyboardLayoutInputSource();

        [DllImport(CarbonPath, EntryPoint = "TISGetInputSourceProperty")]
        public static extern nint TISGetInputSourceProperty(nint source, nint key);

        [DllImport(CarbonPath, EntryPoint = "TISSelectInputSource")]
        public static extern int TISSelectInputSource(nint source);

        [DllImport(CoreFoundationPath, EntryPoint = "CFRelease")]
        public static extern void CFRelease(nint value);

        [DllImport(CoreFoundationPath, EntryPoint = "CFEqual")]
        public static extern byte CFEqual(nint left, nint right);

        [DllImport(CoreFoundationPath, EntryPoint = "CFRunLoopRunInMode")]
        public static extern int CFRunLoopRunInMode(nint mode, double seconds, byte returnAfterSourceHandled);
    }
}
