using Microsoft.Win32.SafeHandles;

namespace Hypa.Terminal.Pty;

/// <summary>
/// Opaque ownership of a master PTY file descriptor for H2 handoff.
/// Not a managed <see cref="Stream"/> fiction — adopts via helper SCM_RIGHTS only.
/// </summary>
public sealed class PtyHandoffHandle : IDisposable
{
    private SafeFileHandle? _handle;
    private int _disposed;

    public PtyHandoffHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid)
            throw new ArgumentException("Handoff handle is invalid.", nameof(handle));
        _handle = handle;
    }

    /// <summary>True when this instance still owns a live descriptor.</summary>
    public bool OwnsHandle => _disposed == 0 && _handle is { IsInvalid: false, IsClosed: false };

    /// <summary>
    /// Dangerous raw FD for one-shot SCM_RIGHTS send. Caller must not close while owned here.
    /// </summary>
    public int DangerousGetFileDescriptor()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0 || _handle is null, this);
        return _handle.DangerousGetHandle().ToInt32();
    }

    /// <summary>
    /// Transfer ownership of the underlying <see cref="SafeFileHandle"/> to the caller.
    /// This instance no longer closes the FD on dispose.
    /// </summary>
    public SafeFileHandle Detach()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var h = _handle ?? throw new ObjectDisposedException(nameof(PtyHandoffHandle));
        _handle = null;
        Interlocked.Exchange(ref _disposed, 1);
        return h;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _handle?.Dispose();
        _handle = null;
    }
}
