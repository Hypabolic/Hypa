using System.Runtime.InteropServices;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// Owns a libghostty-vt render-state pointer. <see cref="ReleaseHandle"/>
/// frees native memory and must not take a managed lock.
/// </summary>
internal sealed class GhosttyRenderStateSafeHandle : SafeHandle
{
    public GhosttyRenderStateSafeHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public GhosttyRenderStateSafeHandle(IntPtr existing)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(existing);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
            GhosttyNative.ghostty_render_state_free(handle);
        return true;
    }
}
