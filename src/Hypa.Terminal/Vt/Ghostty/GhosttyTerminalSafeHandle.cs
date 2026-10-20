using System.Runtime.InteropServices;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// Owns a libghostty-vt terminal pointer and the <see cref="GCHandle"/> of its
/// callback userdata. <see cref="ReleaseHandle"/> frees native memory and must
/// not take a managed lock.
/// </summary>
internal sealed class GhosttyTerminalSafeHandle : SafeHandle
{
    private GCHandle _callbacks;

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

    /// <summary>
    /// Take ownership of the callback userdata handle. The handle is freed
    /// after the terminal, so Ghostty cannot call back on a freed handle.
    /// </summary>
    internal void AttachCallbacks(GCHandle callbacks) => _callbacks = callbacks;

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
            GhosttyNative.ghostty_terminal_free(handle);
        if (_callbacks.IsAllocated)
            _callbacks.Free();
        return true;
    }
}
