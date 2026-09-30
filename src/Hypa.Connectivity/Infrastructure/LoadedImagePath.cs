using System.Runtime.InteropServices;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Finds the file that the dynamic loader mapped for a loaded library.
/// A handle alone does not prove the file: dyld can take a library from
/// <c>DYLD_LIBRARY_PATH</c> before the absolute path it was given.
/// </summary>
internal static class LoadedImagePath
{
    /// <summary>Returns the path of the image that holds <paramref name="symbol"/>, or null.</summary>
    internal static string? Of(IntPtr libraryHandle, string symbol)
    {
        if (libraryHandle == IntPtr.Zero)
            return null;
        if (!NativeLibrary.TryGetExport(libraryHandle, symbol, out var address))
            return null;
        if (DlAddr(address, out var info) == 0 || info.FileName == IntPtr.Zero)
            return null;
        return Marshal.PtrToStringUTF8(info.FileName);
    }

    /// <summary>True when both paths name the same file after symlinks resolve.</summary>
    internal static bool SameFile(string first, string second) =>
        string.Equals(Canonical(first), Canonical(second), StringComparison.Ordinal);

    private static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var target = new FileInfo(full).ResolveLinkTarget(returnFinalTarget: true);
        return target?.FullName ?? full;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public IntPtr FileName;
        public IntPtr FileBase;
        public IntPtr SymbolName;
        public IntPtr SymbolAddress;
    }

    // Same libc binding style as MsQuicNativeIntegrity. DlInfo is blittable.
    [DllImport("libc", EntryPoint = "dladdr")]
    private static extern int DlAddr(IntPtr address, out DlInfo info);
}
