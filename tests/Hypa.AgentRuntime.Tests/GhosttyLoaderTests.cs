using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// path resolution, required fail-closed, optional selection.
/// </summary>
public class GhosttyLoaderTests
{
    [Fact]
    public void FromEnvironment_default_is_ghostty_required()
    {
        using var _ = new EnvScope(
            ("HYPA_VT_PROVIDER", null),
            ("HYPA_VT_REQUIRED", null),
            ("HYPA_GHOSTTY_VT", null));

        var sel = VtProviderSelection.FromEnvironment();
        Assert.Equal(VtProviderKind.Ghostty, sel.Provider);
        Assert.True(sel.RequireGhostty);
        Assert.Equal("ghostty", sel.ProviderWireName);
        Assert.Null(sel.LibraryPathOverride);
    }

    [Fact]
    public void FromEnvironment_provider_ghostty_requires()
    {
        using var _ = new EnvScope(
            ("HYPA_VT_PROVIDER", "ghostty"),
            ("HYPA_VT_REQUIRED", null),
            ("HYPA_GHOSTTY_VT", null));

        var sel = VtProviderSelection.FromEnvironment();
        Assert.Equal(VtProviderKind.Ghostty, sel.Provider);
        Assert.True(sel.RequireGhostty);
        Assert.Equal("ghostty", sel.ProviderWireName);
    }

    [Fact]
    public void FromEnvironment_required_ghostty_keeps_ghostty()
    {
        using var _ = new EnvScope(
            ("HYPA_VT_PROVIDER", "ghostty"),
            ("HYPA_VT_REQUIRED", "ghostty"),
            ("HYPA_GHOSTTY_VT", "/tmp/libghostty-vt-test.so"));

        var sel = VtProviderSelection.FromEnvironment();
        Assert.Equal(VtProviderKind.Ghostty, sel.Provider);
        Assert.True(sel.RequireGhostty);
        Assert.Equal("/tmp/libghostty-vt-test.so", sel.LibraryPathOverride);
    }

    [Fact]
    public void FromEnvironment_explicit_basic_throws()
    {
        using var _ = new EnvScope(
            ("HYPA_VT_PROVIDER", "basic"),
            ("HYPA_VT_REQUIRED", null),
            ("HYPA_GHOSTTY_VT", null));

        var ex = Assert.Throws<ArgumentException>(() => VtProviderSelection.FromEnvironment());
        Assert.Contains("rejected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromEnvironment_explicit_f1_throws()
    {
        using var _ = new EnvScope(
            ("HYPA_VT_PROVIDER", "f1"),
            ("HYPA_VT_REQUIRED", null),
            ("HYPA_GHOSTTY_VT", null));

        var ex = Assert.Throws<ArgumentException>(() => VtProviderSelection.FromEnvironment());
        Assert.Contains("rejected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromEnvironment_channel_f2_selects_ghostty_when_provider_unset()
    {
        using var _ = new EnvScope(
            ("HYPA_VT_PROVIDER", null),
            ("HYPA_VT_REQUIRED", null),
            ("HYPA_GHOSTTY_VT", null));

        var sel = VtProviderSelection.FromEnvironment("f2");
        Assert.Equal(VtProviderKind.Ghostty, sel.Provider);
        Assert.True(sel.RequireGhostty);
    }

    [Fact]
    public void FromEnvironment_reads_planted_channel_marker_beside_base_directory()
    {
        using var _ = new EnvScope(
            ("HYPA_VT_PROVIDER", null),
            ("HYPA_VT_REQUIRED", null),
            ("HYPA_GHOSTTY_VT", null));

        var path = Path.Combine(AppContext.BaseDirectory, NativeAssetResolver.ChannelMarkerFileName);
        var hadPrevious = File.Exists(path);
        var previous = hadPrevious ? File.ReadAllText(path) : null;
        try
        {
            File.WriteAllText(path, "f2\n");
            Assert.Equal("f2", NativeAssetResolver.ReadChannelMarker());
            var sel = VtProviderSelection.FromEnvironment();
            Assert.Equal(VtProviderKind.Ghostty, sel.Provider);
            Assert.True(sel.RequireGhostty);
        }
        finally
        {
            if (hadPrevious)
                File.WriteAllText(path, previous!);
            else
            {
                try { File.Delete(path); }
                catch { /* cleanup */ }
            }
        }
    }

    [Fact]
    public void FromEnvironment_explicit_basic_does_not_override_f2_channel()
    {
        using var _ = new EnvScope(
            ("HYPA_VT_PROVIDER", "basic"),
            ("HYPA_VT_REQUIRED", null),
            ("HYPA_GHOSTTY_VT", null));

        Assert.Throws<ArgumentException>(() => VtProviderSelection.FromEnvironment("f2"));
    }

    [Fact]
    public void ParseProvider_rejects_unknown()
    {
        Assert.Throws<ArgumentException>(() => VtProviderSelection.ParseProvider("vte"));
    }

    [Fact]
    public void ResolveCandidatePaths_includes_override_and_base()
    {
        if (OperatingSystem.IsWindows())
            return; // spike is Unix-only

        var paths = GhosttyLibraryLoader.ResolveCandidatePaths("/tmp/custom-ghostty-lib");
        Assert.Contains(paths, p => p.Contains("custom-ghostty-lib", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith(GhosttyLibraryLoader.LibraryFileName, StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveCandidatePaths_excludes_cwd_and_parent_walk()
    {
        if (OperatingSystem.IsWindows())
            return;

        var parent = Path.Combine(Path.GetTempPath(), "hypa-ghostty-walk-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(parent, "cwd");
        Directory.CreateDirectory(cwd);
        var previous = Directory.GetCurrentDirectory();
        try
        {
            var planted = Path.Combine(
                cwd,
                "native",
                "runtimes",
                GhosttyLibraryLoader.GetPortableRid(),
                "native",
                GhosttyLibraryLoader.LibraryFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
            File.WriteAllText(planted, "planted");
            Directory.SetCurrentDirectory(cwd);

            var paths = GhosttyLibraryLoader.ResolveCandidatePaths();
            Assert.DoesNotContain(Path.GetFullPath(planted), paths);
            Assert.DoesNotContain(paths, p => p.StartsWith(parent, StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.SetCurrentDirectory(previous); }
            catch { /* restore */ }
            try { Directory.Delete(parent, recursive: true); }
            catch { /* cleanup */ }
        }
    }

    [Fact]
    public void FindExistingLibraryPath_returns_null_on_unsupported_os()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Null(GhosttyLibraryLoader.FindExistingLibraryPath());
        Assert.False(GhosttyLibraryLoader.TryGetLibraryFileName(out _));
        Assert.Empty(GhosttyLibraryLoader.ResolveCandidatePaths());
        Assert.False(GhosttyLibraryLoader.TryLoad(pathOverride: null, out var path, out var error));
        Assert.Null(path);
        Assert.Contains("Windows is not a Ghostty-only F1 mux RID", error, StringComparison.Ordinal);
        Assert.Throws<PlatformNotSupportedException>(() => _ = GhosttyLibraryLoader.LibraryFileName);
    }

    [Fact]
    public void LoadRequired_missing_asset_throws()
    {
        if (OperatingSystem.IsWindows())
            return;

        var missing = Path.Combine(Path.GetTempPath(), "hypa-missing-ghostty-" + Guid.NewGuid().ToString("N"), "nope.so");
        var ex = Assert.Throws<InvalidOperationException>(() => GhosttyLibraryLoader.LoadRequired(missing));
        Assert.Contains("ghostty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Required_ghostty_engine_create_does_not_silent_basic()
    {
        if (OperatingSystem.IsWindows())
            return;

        var missing = Path.Combine(
            Path.GetTempPath(),
            "hypa-missing-ghostty-req-" + Guid.NewGuid().ToString("N"),
            "nope.so");
        var factory = new VtEngineFactory(new VtProviderSelection
        {
            Provider = VtProviderKind.Ghostty,
            RequireGhostty = true,
            LibraryPathOverride = missing,
        });

        // Fail closed: never returns BasicVtEngine when Ghostty is required.
        Assert.ThrowsAny<Exception>(() => factory.Create(20, 6));
    }

    [Fact]
    public void TryLoad_missing_returns_false_with_error()
    {
        if (OperatingSystem.IsWindows())
            return;

        var missing = Path.Combine(Path.GetTempPath(), "hypa-missing-ghostty-" + Guid.NewGuid().ToString("N"), "nope.so");
        var ok = GhosttyLibraryLoader.TryLoad(missing, out var path, out var error);
        Assert.False(ok);
        Assert.Null(path);
        Assert.False(string.IsNullOrEmpty(error));
    }



    [Fact]
    public void TryLoad_missing_terminal_set_export_rejects_and_releases_handle()
    {
        if (OperatingSystem.IsWindows())
            return;

        var lib = CompileLibraryMissingTerminalSet(out var compileError);
        Assert.False(
            string.IsNullOrEmpty(lib),
            compileError ?? "cc did not produce a library missing ghostty_terminal_set");

        try
        {
            Assert.True(NativeLibrary.TryLoad(lib, out var probe) && probe != IntPtr.Zero, lib);
            try
            {
                Assert.False(GhosttyLibraryLoader.TryVerifyRequiredExports(probe, out var missing));
                Assert.Equal("ghostty_terminal_set", missing);
            }
            finally
            {
                NativeLibrary.Free(probe);
            }

            var wasLoaded = GhosttyLibraryLoader.IsLoaded;
            var previousPath = GhosttyLibraryLoader.LoadedPath;
            var ok = GhosttyLibraryLoader.TryLoad(lib, out var loadedPath, out var error);
            Assert.False(ok);
            Assert.Null(loadedPath);
            Assert.False(string.IsNullOrEmpty(error));
            Assert.Contains("ghostty_terminal_set", error, StringComparison.Ordinal);
            Assert.Contains("missing export", error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(wasLoaded, GhosttyLibraryLoader.IsLoaded);
            Assert.Equal(previousPath, GhosttyLibraryLoader.LoadedPath);
        }
        finally
        {
            try
            {
                var dir = Path.GetDirectoryName(lib);
                if (dir is not null)
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // temp cleanup
            }
        }
    }

    private static string? CompileLibraryMissingTerminalSet(out string? error)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            "hypa-ghostty-missing-set-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "stub.c");
        var sb = new StringBuilder();
        foreach (var name in GhosttyLibraryLoader.RequiredExports)
        {
            if (name == "ghostty_terminal_set")
                continue;
            sb.Append("void ").Append(name).Append("(void) {}\n");
        }

        File.WriteAllText(src, sb.ToString());
        var lib = Path.Combine(
            dir,
            OperatingSystem.IsMacOS() ? "libmissing-set.dylib" : "libmissing-set.so");
        using var compile = new Process();
        compile.StartInfo.FileName = "cc";
        compile.StartInfo.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-dynamiclib" : "-shared");
        if (!OperatingSystem.IsMacOS())
            compile.StartInfo.ArgumentList.Add("-fPIC");
        compile.StartInfo.ArgumentList.Add("-o");
        compile.StartInfo.ArgumentList.Add(lib);
        compile.StartInfo.ArgumentList.Add(src);
        compile.StartInfo.RedirectStandardOutput = true;
        compile.StartInfo.RedirectStandardError = true;
        compile.StartInfo.UseShellExecute = false;
        try
        {
            compile.Start();
        }
        catch (Exception ex)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* temp cleanup */ }
            error = "cc is required to compile a Ghostty stub: " + ex.Message;
            return null;
        }

        if (!compile.WaitForExit(30_000))
        {
            try { compile.Kill(entireProcessTree: true); }
            catch { /* teardown */ }
            try { Directory.Delete(dir, recursive: true); }
            catch { /* temp cleanup */ }
            error = "cc timed out compiling Ghostty stub";
            return null;
        }

        var stderr = compile.StandardError.ReadToEnd();
        if (compile.ExitCode != 0 || !File.Exists(lib))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* temp cleanup */ }
            error = "cc failed to compile Ghostty stub: " + stderr;
            return null;
        }

        error = null;
        return lib;
    }



    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("2.0.0", false)]
    [InlineData("1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("abc", false)]
    [InlineData("v2.1.0", false)]
    [InlineData("v1.1.4-main+c5a21ed", false)]
    [InlineData("0.2.0", false)]
    [InlineData("0.10.0", false)]
    [InlineData("0.1", true)]
    [InlineData("0.1.0", true)]
    [InlineData("0.1.0-dev", true)]
    [InlineData("v0.1.0-dev", true)]
    public void MatchesPinnedAbi_rejects_empty_and_other_majors(string? version, bool ok)
    {
        Assert.Equal(ok, GhosttyLibraryLoader.MatchesPinnedAbi(version));
        Assert.Equal("1", GhosttyLibraryLoader.AbiVersion);
        Assert.Equal("0.1", GhosttyLibraryLoader.PinnedProductVersion);
    }

    /// <summary>Scoped env mutation for tests (restores previous values).</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly (string Key, string? Previous)[] _prev;

        public EnvScope(params (string Key, string? Value)[] pairs)
        {
            _prev = new (string, string?)[pairs.Length];
            for (var i = 0; i < pairs.Length; i++)
            {
                _prev[i] = (pairs[i].Key, Environment.GetEnvironmentVariable(pairs[i].Key));
                Environment.SetEnvironmentVariable(pairs[i].Key, pairs[i].Value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, previous) in _prev)
                Environment.SetEnvironmentVariable(key, previous);
        }
    }

}
