using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// The native callbacks of one Ghostty terminal: <c>write_pty</c>, device
/// attributes (DA1, DA2, DA3) and XTVERSION.
/// </summary>
/// <remarks>
/// <para>
/// One instance is the userdata of one terminal, through a
/// <see cref="GCHandle"/>. The terminal safe handle owns that handle and frees
/// it after <c>ghostty_terminal_free</c>, so no callback can run on a freed
/// handle.
/// </para>
/// <para>
/// Ghostty calls back inside <c>ghostty_terminal_vt_write</c>, on the thread
/// that feeds, under the engine gate. A callback never writes to the PTY.
/// <c>write_pty</c> copies the reply into the reply list of the current live
/// feed. A feed without a reply list drops the reply, so a restore or a
/// replay cannot send a reply to the program.
/// </para>
/// <para>
/// A callback catches every exception. An exception must not go into native
/// code.
/// </para>
/// </remarks>
internal sealed class GhosttyTerminalCallbacks
{
    // The XTVERSION name. A u8 literal is static image data, so the address
    // does not move and stays valid after the callback returns.
    private static ReadOnlySpan<byte> XtversionName => "hypa"u8;

    private List<byte[]>? _replies;

    /// <summary>
    /// The reply list of the live feed that runs now, or null. Set and clear it
    /// only under the engine gate, around one <c>vt_write</c>.
    /// </summary>
    internal List<byte[]>? Replies
    {
        get => _replies;
        set => _replies = value;
    }

    /// <summary>
    /// Set the userdata and the callbacks on a new terminal. Returns the first
    /// non-success code, or <see cref="GhosttyNative.Success"/>.
    /// </summary>
    internal static unsafe int Install(IntPtr terminal, GCHandle handle)
    {
        var rc = GhosttyNative.ghostty_terminal_set(
            terminal,
            GhosttyNative.TerminalOptUserdata,
            GCHandle.ToIntPtr(handle));
        if (rc != GhosttyNative.Success)
            return rc;

        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, nuint, void> writePty = &OnWritePty;
        rc = GhosttyNative.ghostty_terminal_set(
            terminal,
            GhosttyNative.TerminalOptWritePty,
            (IntPtr)writePty);
        if (rc != GhosttyNative.Success)
            return rc;

        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, GhosttyNative.GhosttyDeviceAttributes*, byte> deviceAttributes =
            &OnDeviceAttributes;
        rc = GhosttyNative.ghostty_terminal_set(
            terminal,
            GhosttyNative.TerminalOptDeviceAttributes,
            (IntPtr)deviceAttributes);
        if (rc != GhosttyNative.Success)
            return rc;

        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, GhosttyNative.GhosttyString> xtversion = &OnXtversion;
        return GhosttyNative.ghostty_terminal_set(
            terminal,
            GhosttyNative.TerminalOptXtversion,
            (IntPtr)xtversion);
    }

    /// <summary>
    /// Fill the device attributes that Hypa reports. DA1 is
    /// <c>CSI ? 62 ; 22 c</c> (VT220, ANSI colour), the same as the Ghostty
    /// default. DA2 is <c>CSI &gt; 1 ; 0 ; 0 c</c>. DA3 is unit id 0.
    /// </summary>
    internal static unsafe void FillDeviceAttributes(GhosttyNative.GhosttyDeviceAttributes* attrs)
    {
        *attrs = default;
        attrs->Primary.ConformanceLevel = GhosttyNative.DaConformanceVt220;
        attrs->Primary.Features[0] = GhosttyNative.DaFeatureAnsiColor;
        attrs->Primary.NumFeatures = 1;
        attrs->Secondary.DeviceType = GhosttyNative.DaDeviceTypeVt220;
        attrs->Secondary.FirmwareVersion = 0;
        attrs->Secondary.RomCartridge = 0;
        attrs->Tertiary.UnitId = 0;
    }

    private static GhosttyTerminalCallbacks? FromUserdata(IntPtr userdata)
    {
        if (userdata == IntPtr.Zero)
            return null;
        var handle = GCHandle.FromIntPtr(userdata);
        return handle.IsAllocated ? handle.Target as GhosttyTerminalCallbacks : null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnWritePty(IntPtr terminal, IntPtr userdata, byte* data, nuint len)
    {
        try
        {
            if (data is null || len == 0 || len > int.MaxValue)
                return;
            var replies = FromUserdata(userdata)?._replies;
            if (replies is null)
                return;

            // The data is valid only during the call. Copy it.
            replies.Add(new ReadOnlySpan<byte>(data, (int)len).ToArray());
        }
        catch
        {
            // Do not let an exception go into native code. The reply is lost.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte OnDeviceAttributes(
        IntPtr terminal,
        IntPtr userdata,
        GhosttyNative.GhosttyDeviceAttributes* outAttrs)
    {
        try
        {
            if (outAttrs is null)
                return 0;
            FillDeviceAttributes(outAttrs);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe GhosttyNative.GhosttyString OnXtversion(IntPtr terminal, IntPtr userdata)
    {
        try
        {
            fixed (byte* name = XtversionName)
            {
                return new GhosttyNative.GhosttyString
                {
                    Ptr = (IntPtr)name,
                    Len = (nuint)XtversionName.Length,
                };
            }
        }
        catch
        {
            return default;
        }
    }
}
