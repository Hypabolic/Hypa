using System.Text;

namespace Hypa.Terminal.Vt.Ghostty;

/// <summary>
/// Thin ABI probe for: load library, query build_info, create terminal,
/// feed bytes, free. Not a full <see cref="IVtEngine"/>.
/// </summary>
public static class GhosttyVtProbe
{
    /// <summary>
    /// Load (if needed) and query version/build via <c>ghostty_build_info</c>.
    /// </summary>
    public static GhosttyProviderInfo? TryProbe(string? pathOverride, out string? error)
    {
        if (!GhosttyLibraryLoader.TryLoad(pathOverride, out var path, out error))
            return null;

        try
        {
            var version = GhosttyLibraryLoader.TryQueryBuildInfo(GhosttyNative.BuildInfoVersionString) ?? "unknown";
            var build = GhosttyLibraryLoader.TryQueryBuildInfo(GhosttyNative.BuildInfoVersionBuild);

            // Prove create / write / free on a tiny terminal. SafeHandle owns
            // native lifetime (same class as GhosttyVtEngine).
            var options = new GhosttyNative.GhosttyTerminalOptions
            {
                Cols = 80,
                Rows = 24,
                MaxScrollback = 100,
            };
            var rc = GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out var raw, options);
            if (rc != GhosttyNative.Success || raw == IntPtr.Zero)
            {
                error = $"ghostty_terminal_new failed with code {rc}";
                return null;
            }

            using var terminal = new GhosttyTerminalSafeHandle(raw);
            var added = false;
            try
            {
                terminal.DangerousAddRef(ref added);
                var term = terminal.DangerousGetHandle();
                var bytes = Encoding.UTF8.GetBytes("hypa-h13-probe\r\n");
                unsafe
                {
                    fixed (byte* p = bytes)
                    {
                        GhosttyNative.ghostty_terminal_vt_write(term, (IntPtr)p, (nuint)bytes.Length);
                    }
                }
            }
            finally
            {
                if (added)
                    terminal.DangerousRelease();
            }

            error = null;
            return new GhosttyProviderInfo
            {
                Version = version,
                Build = string.IsNullOrEmpty(build) ? null : build,
                Abi = GhosttyLibraryLoader.AbiVersion,
                UpstreamCommit = GhosttyLibraryLoader.PinnedCommit,
                LibraryPath = path,
                LinkMode = "dynamic",
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Fail-closed probe used at process startup when Ghostty is required.</summary>
    public static GhosttyProviderInfo ProbeRequired(string? pathOverride = null)
    {
        GhosttyLibraryLoader.LoadRequired(pathOverride);
        var info = TryProbe(pathOverride, out var error);
        if (info is null)
        {
            throw new InvalidOperationException(
                "Ghostty VT probe failed after library load: " + (error ?? "unknown"));
        }

        return info;
    }
}
