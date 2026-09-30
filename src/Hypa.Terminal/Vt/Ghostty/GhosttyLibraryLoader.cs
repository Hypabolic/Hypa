using System.Runtime.InteropServices;
using System.Text;
using Hypa.Terminal.Pty;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// Resolves and loads <c>libghostty-vt</c> for the current RID.
/// Search order: absolute <c>HYPA_GHOSTTY_VT</c> / override → beside apphost →
/// pinned RID layout under <c>AppContext.BaseDirectory</c> only.
/// </summary>
public static class GhosttyLibraryLoader
{
    public const string LibraryBaseName = "ghostty-vt";
    /// <summary>Hypa pin token in <c>abi-manifest.json</c>. Not the Ghostty product major.</summary>
    public const string AbiVersion = "1";
    public const string PinnedCommit = "c5a21edfcbc2d5b46540ad91b7980aca31f5f1f3";
    /// <summary>
    /// Live <c>ghostty_build_info</c> VERSION_STRING family at this pin
    /// (<c>0.1.0-dev</c>). Other product families fail closed.
    /// </summary>
    internal const string PinnedProductVersion = "0.1";

    /// <summary>
    /// Every LibraryImport symbol on the Ghostty native surface.
    /// Load fails closed if any export is missing.
    /// </summary>
    public static readonly string[] RequiredExports =
    [
        "ghostty_build_info",
        "ghostty_terminal_new",
        "ghostty_terminal_free",
        "ghostty_terminal_vt_write",
        "ghostty_terminal_reset",
        "ghostty_terminal_resize",
        "ghostty_terminal_get",
        "ghostty_terminal_set",
        "ghostty_terminal_mode_get",
        "ghostty_terminal_scroll_viewport",
        "ghostty_terminal_grid_ref",
        "ghostty_terminal_grid_ref_track",
        "ghostty_tracked_grid_ref_free",
        "ghostty_tracked_grid_ref_has_value",
        "ghostty_tracked_grid_ref_point",
        "ghostty_grid_ref_cell",
        "ghostty_grid_ref_graphemes",
        "ghostty_grid_ref_style",
        "ghostty_cell_get",
        "ghostty_style_default",
        "ghostty_formatter_terminal_new",
        "ghostty_formatter_format_alloc",
        "ghostty_formatter_free",
        "ghostty_free",
        "ghostty_color_palette_default",
        "ghostty_render_state_new",
        "ghostty_render_state_free",
        "ghostty_render_state_update",
        "ghostty_render_state_get",
        "ghostty_render_state_set",
        "ghostty_render_state_colors_get",
        "ghostty_render_state_row_iterator_new",
        "ghostty_render_state_row_iterator_free",
        "ghostty_render_state_row_iterator_next",
        "ghostty_render_state_row_get",
        "ghostty_render_state_row_set",
        "ghostty_render_state_row_cells_new",
        "ghostty_render_state_row_cells_next",
        "ghostty_render_state_row_cells_select",
        "ghostty_render_state_row_cells_get",
        "ghostty_render_state_row_cells_get_multi",
        "ghostty_render_state_row_cells_free",
    ];

    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static string? _loadedPath;
    private static bool _resolverRegistered;

    public static bool IsLoaded
    {
        get { lock (Gate) return _handle != IntPtr.Zero; }
    }

    public static string? LoadedPath
    {
        get { lock (Gate) return _loadedPath; }
    }

    /// <summary>
    /// File name for Linux or macOS (<c>libghostty-vt.so</c> / <c>.dylib</c>).
    /// Discovery APIs must use <see cref="TryGetLibraryFileName"/> so Windows
    /// testhost can skip. Explicit callers still get
    /// <see cref="PlatformNotSupportedException"/>.
    /// </summary>
    public static string LibraryFileName
    {
        get
        {
            if (TryGetLibraryFileName(out var name))
                return name;
            throw new PlatformNotSupportedException(
                "libghostty-vt supports Linux and macOS only. Windows is not a Ghostty-only F1 mux RID.");
        }
    }

    /// <summary>
    /// True when this OS has a Ghostty native file name. False on Windows.
    /// </summary>
    public static bool TryGetLibraryFileName(out string fileName)
    {
        if (OperatingSystem.IsMacOS())
        {
            fileName = "libghostty-vt.dylib";
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            fileName = "libghostty-vt.so";
            return true;
        }

        fileName = "";
        return false;
    }

    /// <summary>
    /// Resolve candidate paths without loading. Cwd and parent walks are not used.
    /// </summary>
    public static IReadOnlyList<string> ResolveCandidatePaths(string? pathOverride = null)
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p))
                return;
            var full = Path.GetFullPath(p);
            if (!list.Contains(full, StringComparer.Ordinal))
                list.Add(full);
        }

        var overridePath = NativeAssetResolver.RequireAbsoluteOverride(
            pathOverride,
            Environment.GetEnvironmentVariable("HYPA_GHOSTTY_VT"),
            "HYPA_GHOSTTY_VT");
        Add(overridePath);

        if (TryGetLibraryFileName(out var fileName))
        {
            foreach (var candidate in NativeAssetResolver.EnumerateSearchPaths(fileName))
                Add(candidate);
        }

        return list;
    }

    public static string? FindExistingLibraryPath(string? pathOverride = null)
    {
        // Explicit override is exclusive: do not fall through to search paths.
        if (!string.IsNullOrWhiteSpace(pathOverride))
        {
            var absolute = NativeAssetResolver.RequireAbsoluteOverride(
                pathOverride,
                envValue: null,
                "HYPA_GHOSTTY_VT");
            if (absolute is null)
                return null;
            return File.Exists(absolute) ? absolute : null;
        }

        if (!TryGetLibraryFileName(out _))
            return null;

        foreach (var path in ResolveCandidatePaths(pathOverride: null))
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Load the shared library. Returns false when not found.
    /// </summary>
    public static bool TryLoad(string? pathOverride, out string? path, out string? error)
    {
        lock (Gate)
        {
            // If already loaded and no exclusive override, reuse.
            // Exclusive override to a different path is a test/fail path — do not pretend success.
            if (_handle != IntPtr.Zero
                && (string.IsNullOrWhiteSpace(pathOverride)
                    || string.Equals(
                        _loadedPath,
                        Path.GetFullPath(pathOverride),
                        StringComparison.Ordinal)))
            {
                path = _loadedPath;
                error = null;
                return true;
            }

            if (string.IsNullOrWhiteSpace(pathOverride) && !TryGetLibraryFileName(out _))
            {
                path = null;
                error =
                    "libghostty-vt supports Linux and macOS only. "
                    + "Windows is not a Ghostty-only F1 mux RID.";
                return false;
            }

            path = FindExistingLibraryPath(pathOverride);
            if (path is null)
            {
                var searched = !string.IsNullOrWhiteSpace(pathOverride)
                    ? Path.GetFullPath(pathOverride)
                    : string.Join(", ", ResolveCandidatePaths(pathOverride: null));
                error =
                    $"libghostty-vt not found. Searched: {searched}. "
                    + "Build with scripts/build-libghostty-vt.sh or set HYPA_GHOSTTY_VT.";
                return false;
            }

            var previous = _handle;
            var previousPath = _loadedPath;
            var candidate = IntPtr.Zero;
            var swapped = false;
            try
            {
                EnsureDllImportResolver();
                if (!NativeLibrary.TryLoad(path, out candidate) || candidate == IntPtr.Zero)
                {
                    error = $"NativeLibrary.TryLoad failed for {path}";
                    candidate = IntPtr.Zero;
                    path = null;
                    return false;
                }

                if (!TryVerifyRequiredExports(candidate, out var missing))
                {
                    NativeLibrary.Free(candidate);
                    candidate = IntPtr.Zero;
                    error =
                        $"libghostty-vt ABI mismatch: missing export '{missing}'. " +
                        $"Pinned commit {PinnedCommit}, abi {AbiVersion}.";
                    path = null;
                    return false;
                }

                _handle = candidate;
                _loadedPath = path;
                swapped = true;

                // Required symbols plus a live version query. Empty or a
                // product family other than PinnedProductVersion fails closed.
                // Do not require the native string to equal PinnedCommit (not a SHA).
                var version = TryQueryBuildInfo(GhosttyNative.BuildInfoVersionString);
                if (!MatchesPinnedAbi(version))
                {
                    NativeLibrary.Free(candidate);
                    candidate = IntPtr.Zero;
                    _handle = previous;
                    _loadedPath = previousPath;
                    error =
                        $"libghostty-vt ABI mismatch: version '{version ?? "<null>"}' is not product {PinnedProductVersion}. " +
                        $"Pinned commit {PinnedCommit}, abi {AbiVersion}.";
                    path = null;
                    return false;
                }

                if (previous != IntPtr.Zero && previous != candidate)
                    NativeLibrary.Free(previous);

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                if (candidate != IntPtr.Zero)
                {
                    NativeLibrary.Free(candidate);
                    candidate = IntPtr.Zero;
                }

                if (swapped)
                {
                    _handle = previous;
                    _loadedPath = previousPath;
                }

                error = ex.Message;
                path = null;
                return false;
            }
        }
    }

    /// <summary>
    /// Load or throw with a clear message (fail-closed path).
    /// </summary>
    public static string LoadRequired(string? pathOverride = null)
    {
        if (TryLoad(pathOverride, out var path, out var error))
            return path!;

        throw new InvalidOperationException(
            "HYPA_VT_REQUIRED=ghostty (or HYPA_VT_PROVIDER=ghostty): " + (error ?? "load failed"));
    }

    public static string GetPortableRid() => NativeAssetResolver.PortableRid();

    /// <summary>
    /// Accept a live <c>ghostty_build_info</c> VERSION_STRING for this pin.
    /// The vendored lib reports <c>0.1.0-dev</c>. Empty and other product
    /// families fail closed. <see cref="AbiVersion"/> is the Hypa pin token,
    /// not the Ghostty product major.
    /// </summary>
    internal static bool MatchesPinnedAbi(string? version)
    {
        if (string.IsNullOrEmpty(version))
            return false;

        var s = version;
        if (s[0] is 'v' or 'V')
            s = s[1..];
        if (s.Length == 0)
            return false;

        if (s.Equals(PinnedProductVersion, StringComparison.Ordinal))
            return true;

        if (s.Length > PinnedProductVersion.Length
            && s.StartsWith(PinnedProductVersion, StringComparison.Ordinal))
        {
            var next = s[PinnedProductVersion.Length];
            return next is '.' or '-' or '+';
        }

        return false;
    }

    internal static bool TryVerifyRequiredExports(IntPtr handle, out string? missing)
    {
        foreach (var name in RequiredExports)
        {
            if (!NativeLibrary.TryGetExport(handle, name, out _))
            {
                missing = name;
                return false;
            }
        }

        missing = null;
        return true;
    }

    /// <summary>
    /// Accept a live <c>ghostty_build_info</c> version. Null, empty, and a
    /// product family other than <see cref="PinnedProductVersion"/> fail closed.
    /// </summary>
    internal static bool TryAcceptQueriedBuildInfo(string? queried, out string? error)
    {
        if (queried is null)
        {
            error = "ghostty_build_info failed";
            return false;
        }

        if (string.IsNullOrWhiteSpace(queried))
        {
            error = "ghostty_build_info returned empty version";
            return false;
        }

        if (!MatchesPinnedAbi(queried))
        {
            error = $"version '{queried}' is not product {PinnedProductVersion}";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Query <c>ghostty_build_info</c> (same path as <see cref="GhosttyVtProbe"/>).
    /// Returns null when the native call fails. Empty string means the field
    /// is present but blank.
    /// </summary>
    internal static unsafe string? TryQueryBuildInfo(int buildInfoField)
    {
        var gs = new GhosttyNative.GhosttyString();
        var rc = GhosttyNative.ghostty_build_info(buildInfoField, (IntPtr)(&gs));
        if (rc != GhosttyNative.Success)
            return null;

        if (gs.Ptr == IntPtr.Zero || gs.Len == 0)
            return string.Empty;

        var len = checked((int)gs.Len);
        var buffer = new byte[len];
        Marshal.Copy(gs.Ptr, buffer, 0, len);
        return Encoding.UTF8.GetString(buffer);
    }

    private static void EnsureDllImportResolver()
    {
        if (_resolverRegistered)
            return;

        NativeLibrary.SetDllImportResolver(
            typeof(GhosttyLibraryLoader).Assembly,
            static (name, _, _) =>
            {
                if (!name.Equals(LibraryBaseName, StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("libghostty-vt", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("ghostty-vt", StringComparison.OrdinalIgnoreCase))
                {
                    return IntPtr.Zero;
                }

                lock (Gate)
                {
                    if (_handle != IntPtr.Zero)
                        return _handle;
                }

                if (TryLoad(pathOverride: null, out _, out _))
                {
                    lock (Gate)
                        return _handle;
                }

                return IntPtr.Zero;
            });

        _resolverRegistered = true;
    }
}
