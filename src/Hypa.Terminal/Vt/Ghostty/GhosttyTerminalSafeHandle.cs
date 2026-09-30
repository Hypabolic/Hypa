using System.Runtime.InteropServices;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// Owns a libghostty-vt terminal pointer. <see cref="ReleaseHandle"/> frees
/// native memory and must not take a managed lock.
/// </summary>
internal sealed class GhosttyTerminalSafeHandle : SafeHandle
{
    public GhosttyTerminalSafeHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public GhosttyTerminalSafeHandle(IntPtr existing)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(existing);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
            GhosttyNative.ghostty_terminal_free(handle);
        return true;
    }
}
