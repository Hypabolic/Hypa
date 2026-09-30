using Hypa.AgentRuntime.Application;

namespace Hypa.Cli.Attach.Input;

/// <summary>
/// Host prefix ASCII switch. macOS uses TIS. Windows uses Korean IME only.
// / Other platforms are a no-op.
/// </summary>
public static class PlatformPrefixAsciiInputSource
{
    public static IPrefixAsciiInputSource Create()
    {
        if (OperatingSystem.IsMacOS())
        {
            var api = NativeMacOsTisApi.Instance;
            return new PrefixAsciiInputAdapter(
                () => MacOsAsciiInputSwitch.SwitchToAscii(api),
                api.PumpRunLoop);
        }

        if (OperatingSystem.IsWindows())
        {
            var probe = NativeWindowsImeProbe.Instance;
            return new PrefixAsciiInputAdapter(
                () =>
                {
                    var token = WindowsKoreanImeSwitch.SwitchToAscii(probe);
                    return token is { } restore
                        ? new WindowsImeRestoreHandle(restore, probe)
                        : null;
                });
        }

        return NoOpPrefixAsciiInputSource.Instance;
    }

    private sealed class WindowsImeRestoreHandle : IDisposable
    {
        private readonly WindowsImeRestore _token;
        private readonly IWindowsImeProbe _probe;
        private bool _disposed;

        public WindowsImeRestoreHandle(WindowsImeRestore token, IWindowsImeProbe probe)
        {
            _token = token;
            _probe = probe;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            WindowsKoreanImeSwitch.Restore(_token, _probe);
        }
    }
}
