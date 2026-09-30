using System.Net.Quic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Hypa.Connectivity.Infrastructure;

/// <summary>
/// Locks <c>System.Net.Quic</c> onto a validated app-local MsQuic file.
/// Windows uses the AppLocalMsQuic host switch.
/// Linux loads a validated <c>libnuma.so.1</c> by absolute path before
/// MsQuic. libmsquic needs that library. The pack ships it.
/// Linux and macOS load MsQuic by its absolute path. The names
/// <c>MsQuicApi</c> passes to <c>NativeLibrary.TryLoad</c> must return the
/// same handle. NativeAOT does not raise
/// <c>AssemblyLoadContext.ResolvingUnmanagedDll</c> for that call, and
/// <c>NativeLibrary.TryLoad</c> does not call a <c>DllImport</c> resolver.
/// The resolver still returns the validated file for MsQuic names so a
/// <c>DllImport</c> from the Quic assembly cannot bind a different build.
/// </summary>
public static class MsQuicRuntimePinning
{
    // .NET 10 MsQuicApi.s_minMsQuicVersion is 2.2.2. The Unix load name is
    // Interop.Libraries.MsQuic plus "." plus that major.
    private const int MinimumMsQuicMajor = 2;

    // Every MsQuic 2.x build exports this entry point.
    private const string MsQuicEntryPoint = "MsQuicOpenVersion";

    internal const string LibNumaFileName = "libnuma.so.1";

    // libnuma exports this entry point. The mapped image must be our file.
    private const string LibNumaEntryPoint = "numa_available";

    private const string ResolverConflictDetail =
        "a DllImport resolver is already registered for System.Net.Quic";

    private static readonly object Gate = new();
    private static int _unixPinned;
    private static string? _unixLibraryPath;
    private static bool _resolverInstalled;

    public static bool IsAppLocalMsQuicEnabled =>
        AppContext.TryGetSwitch("System.Net.Quic.AppLocalMsQuic", out var enabled) && enabled;

    public static bool CanPinValidatedLibrary =>
        OperatingSystem.IsWindows()
            ? IsAppLocalMsQuicEnabled
            : Volatile.Read(ref _unixPinned) == 1;

    public static bool TryEnsurePinned(string? validatedLibraryPath, out string? detail)
    {
        if (OperatingSystem.IsWindows())
        {
            if (IsAppLocalMsQuicEnabled)
            {
                detail = null;
                return true;
            }

            detail = "System.Net.Quic.AppLocalMsQuic is not enabled for this process";
            return false;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            detail = "runtime cannot pin MsQuic to the validated app-local library on this platform";
            return false;
        }

        return TryPinUnix(validatedLibraryPath, typeof(QuicConnection).Assembly, out detail);
    }

    internal static bool TryPinUnix(string? validatedLibraryPath, Assembly quicAssembly, out string? detail)
    {
        if (Volatile.Read(ref _unixPinned) == 1)
        {
            detail = null;
            return true;
        }

        lock (Gate)
        {
            if (_unixPinned == 1)
            {
                detail = null;
                return true;
            }

            if (string.IsNullOrWhiteSpace(validatedLibraryPath) || !File.Exists(validatedLibraryPath))
            {
                detail = "validated MsQuic native library was not found beside the app";
                return false;
            }

            if (!EnsureResolver(quicAssembly, out detail))
                return false;

            if (!TryPrepareBundledLibNuma(
                    OperatingSystem.IsLinux(),
                    validatedLibraryPath,
                    static path =>
                    {
                        var ok = MsQuicNativeIntegrity.TryValidateAppLocal(path, out var failure);
                        return (ok, failure);
                    },
                    static path =>
                    {
                        var handle = NativeLibrary.Load(path);
                        return new LoadedNativeImage(handle, LoadedImagePath.Of(handle, LibNumaEntryPoint));
                    },
                    out detail))
                return false;

            try
            {
                _unixLibraryPath = validatedLibraryPath;
                var pinnedHandle = NativeLibrary.Load(validatedLibraryPath);
                if (!RuntimeNameReturnsPinnedHandle(pinnedHandle))
                {
                    detail = "failed to pin the validated app-local MsQuic library";
                    return false;
                }

                // The handle can belong to a library dyld found first on DYLD_LIBRARY_PATH.
                // Require that the mapped image is the validated file.
                var mapped = LoadedImagePath.Of(pinnedHandle, MsQuicEntryPoint);
                if (mapped is null || !LoadedImagePath.SameFile(mapped, validatedLibraryPath))
                {
                    detail = "the loaded MsQuic image is not the validated app-local library";
                    return false;
                }

                _unixPinned = 1;
                detail = null;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                detail = "failed to pin the validated app-local MsQuic library";
                return false;
            }
        }
    }

    internal readonly record struct LoadedNativeImage(IntPtr Handle, string? MappedPath);

    internal static bool TryValidateBundledLibNuma(string baseDir, out string? detail)
    {
        var path = Path.Combine(baseDir, LibNumaFileName);
        if (!File.Exists(path))
        {
            detail = "validated libnuma.so.1 was not found beside the app";
            return false;
        }

        if (!MsQuicNativeIntegrity.TryValidateAppLocal(path, out detail))
        {
            detail ??= "validated libnuma.so.1 was not found beside the app";
            return false;
        }

        detail = null;
        return true;
    }

    /// <summary>
    /// Linux loads the validated libnuma file before MsQuic.
    /// Other hosts skip this step.
    /// </summary>
    internal static bool TryPrepareBundledLibNuma(
        bool requireLibNuma,
        string msquicLibraryPath,
        Func<string, (bool Ok, string? Detail)> validate,
        Func<string, LoadedNativeImage> load,
        out string? detail)
    {
        if (!requireLibNuma)
        {
            detail = null;
            return true;
        }

        var directory = Path.GetDirectoryName(msquicLibraryPath);
        if (string.IsNullOrEmpty(directory))
        {
            detail = "validated libnuma.so.1 was not found beside the app";
            return false;
        }

        var numaPath = Path.Combine(directory, LibNumaFileName);
        var validated = validate(numaPath);
        if (!validated.Ok)
        {
            detail = validated.Detail ?? "validated libnuma.so.1 was not found beside the app";
            return false;
        }

        LoadedNativeImage loaded;
        try
        {
            loaded = load(numaPath);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            detail = "failed to pin the validated app-local libnuma library";
            return false;
        }

        var sameImage = false;
        if (loaded.Handle != IntPtr.Zero && loaded.MappedPath is not null)
        {
            try
            {
                sameImage = LoadedImagePath.SameFile(loaded.MappedPath, numaPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sameImage = false;
            }
        }

        if (!sameImage)
        {
            detail = "the loaded libnuma image is not the validated app-local library";
            return false;
        }

        detail = null;
        return true;
    }

    private static bool EnsureResolver(Assembly quicAssembly, out string? detail)
    {
        if (_resolverInstalled)
        {
            detail = null;
            return true;
        }

        if (!TryRegisterResolver(quicAssembly, out detail))
            return false;

        _resolverInstalled = true;
        detail = null;
        return true;
    }

    internal static bool TryRegisterResolver(Assembly assembly, out string? detail)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(
                assembly,
                static (libraryName, _, _) => ResolveValidatedLibrary(libraryName, _unixLibraryPath));
        }
        catch (InvalidOperationException)
        {
            detail = ResolverConflictDetail;
            return false;
        }

        detail = null;
        return true;
    }

    internal static IntPtr ResolveValidatedLibrary(string libraryName, string? validatedLibraryPath)
    {
        if (!IsMsQuicLibraryName(libraryName))
            return IntPtr.Zero;

        if (string.IsNullOrWhiteSpace(validatedLibraryPath) || !File.Exists(validatedLibraryPath))
            return IntPtr.Zero;

        return NativeLibrary.Load(validatedLibraryPath);
    }

    private static bool RuntimeNameReturnsPinnedHandle(IntPtr pinnedHandle)
    {
        var assembly = typeof(QuicConnection).Assembly;
        foreach (var name in RuntimeLibraryNames())
        {
            if (!NativeLibrary.TryLoad(name, assembly, searchPath: null, out var handle))
                continue;

            return handle == pinnedHandle;
        }

        return false;
    }

    private static IEnumerable<string> RuntimeLibraryNames()
    {
        if (OperatingSystem.IsLinux())
        {
            yield return "libmsquic.so." + MinimumMsQuicMajor;
            yield return "libmsquic.so";
            yield break;
        }

        yield return "libmsquic.dylib." + MinimumMsQuicMajor;
        yield return "libmsquic.dylib";
    }

    private static bool IsMsQuicLibraryName(string name) =>
        name.Equals("msquic", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("msquic.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("libmsquic", StringComparison.OrdinalIgnoreCase);
}
