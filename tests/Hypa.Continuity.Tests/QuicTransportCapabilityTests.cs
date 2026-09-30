using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Hypa.Cli.Commands;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;
using Hypa.Connectivity.Infrastructure;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class QuicTransportCapabilityTests
{
    [Fact]
    public void Probe_reports_runtime_identifier_and_tcp_fallback()
    {
        var report = new QuicTransportCapabilityProbe().Probe();

        Assert.False(string.IsNullOrWhiteSpace(report.RuntimeIdentifier));
        Assert.Equal(BytePathProviders.Tcp, report.FallbackProvider);
        Assert.Equal(BytePathProviders.Quic, report.QuicProvider);
    }

    [Fact]
    public void Quic_client_target_host_does_not_use_an_ip_literal()
    {
        Assert.Equal("localhost", QuicTransportConstants.ClientTargetHost("127.0.0.1", "127.0.0.1"));
        Assert.Equal("localhost", QuicTransportConstants.ClientTargetHost(null, "172.17.0.4"));
        Assert.Equal("mux.example", QuicTransportConstants.ClientTargetHost("mux.example", "172.17.0.4"));
    }

    [Fact]
    public void First_path_disables_zero_rtt()
    {
        var report = new QuicTransportCapabilityProbe().Probe();

        Assert.False(report.ZeroRttEnabled);
        Assert.False(QuicTransportPolicy.ZeroRttEnabled);
    }

    [Fact]
    public void Unsupported_platform_fails_closed_with_tcp_fallback_signal()
    {
        var document = ConnectivityCommand.ToDocument(new QuicTransportCapabilityProbe().Probe());

        if (document.Ok)
        {
            Assert.Null(document.Reason);
            Assert.Null(document.Detail);
            return;
        }

        Assert.False(document.Ok);
        Assert.Equal(BytePathProviders.Tcp, document.FallbackProvider);
        Assert.Equal(BytePathProviders.Quic, document.QuicProvider);
        Assert.Equal(ConnectivityReasons.QuicUnsupported, document.Reason);
        Assert.False(string.IsNullOrWhiteSpace(document.Detail));
    }

    [Fact]
    public void Native_discovery_does_not_throw_when_library_is_absent()
    {
        var native = MsQuicNativeDiscovery.Discover();

        if (native.Found)
            Assert.Equal(MsQuicNativeLocations.AppLocal, native.Location);
        else
            Assert.Equal(MsQuicNativeLocations.Absent, native.Location);
    }

    [Fact]
    public void Invalid_app_local_candidate_fails_closed_without_host_paths()
    {
        var libraryName = OperatingSystem.IsWindows()
            ? "msquic.dll"
            : OperatingSystem.IsLinux()
                ? "libmsquic.so"
                : "libmsquic.dylib";
        var libraryPath = Path.Combine(AppContext.BaseDirectory, libraryName);
        if (File.Exists(libraryPath))
            return;

        File.WriteAllBytes(libraryPath, [0x01]);
        try
        {
            var native = MsQuicNativeDiscovery.Discover();
            var report = new QuicTransportCapabilityProbe().Probe();

            Assert.False(native.Found);
            Assert.Equal(MsQuicNativeLocations.Absent, native.Location);
            Assert.Contains("digest", native.IntegrityDetail!, StringComparison.OrdinalIgnoreCase);
            Assert.False(report.IsSupported);
            Assert.False(report.NativeLibraryFound);
            Assert.Equal(ConnectivityReasons.QuicUnsupported, report.Reason);
            Assert.Equal(BytePathProviders.Tcp, report.FallbackProvider);
        }
        finally
        {
            File.Delete(libraryPath);
        }
    }

    [Fact]
    public void Validated_app_local_library_without_runtime_pin_reports_found_but_unsupported()
    {
        if (MsQuicRuntimePinning.CanPinValidatedLibrary)
            return;

        var libraryName = OperatingSystem.IsLinux() ? "libmsquic.so" : "libmsquic.dylib";
        var libraryPath = Path.Combine(AppContext.BaseDirectory, libraryName);
        if (File.Exists(libraryPath))
            return;

        var payload = new byte[] { 5, 6, 7, 8 };
        File.WriteAllBytes(libraryPath, payload);
        File.WriteAllText(libraryPath + ".sha256", Convert.ToHexString(SHA256.HashData(payload)));
        try
        {
            var report = new QuicTransportCapabilityProbe().Probe();

            Assert.True(report.NativeLibraryFound);
            Assert.False(report.IsSupported);
            Assert.Equal(MsQuicNativeLocations.AppLocal, report.NativeLibraryLocation);
            Assert.Equal(ConnectivityReasons.QuicUnsupported, report.Reason);
            Assert.Contains("pin", report.Detail!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(libraryPath + ".sha256");
            File.Delete(libraryPath);
        }
    }

    [Fact]
    public void World_writable_install_directory_is_rejected()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "hypa-quic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.SetUnixFileMode(
            tempDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        var libraryPath = Path.Combine(tempDir, "libmsquic.so");
        var payload = new byte[] { 1, 2, 3, 4 };
        try
        {
            File.WriteAllBytes(libraryPath, payload);
            File.WriteAllText(
                libraryPath + ".sha256",
                Convert.ToHexString(SHA256.HashData(payload)));

            Assert.False(MsQuicNativeIntegrity.TryValidateAppLocal(libraryPath, out var detail));
            Assert.Contains("directory", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(libraryPath + ".sha256");
            File.Delete(libraryPath);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Digest_sidecar_symlink_is_rejected()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "hypa-quic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var libraryPath = Path.Combine(tempDir, "libmsquic.so");
        var payload = new byte[] { 9, 10, 11, 12 };
        var realSidecar = Path.Combine(tempDir, "real.sha256");
        try
        {
            File.WriteAllBytes(libraryPath, payload);
            File.WriteAllText(realSidecar, Convert.ToHexString(SHA256.HashData(payload)));
            File.CreateSymbolicLink(libraryPath + ".sha256", realSidecar);

            Assert.False(MsQuicNativeIntegrity.TryValidateAppLocal(libraryPath, out var detail));
            Assert.Contains("sidecar", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(libraryPath + ".sha256"))
                File.Delete(libraryPath + ".sha256");
            if (File.Exists(realSidecar))
                File.Delete(realSidecar);
            if (File.Exists(libraryPath))
                File.Delete(libraryPath);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void App_local_library_without_digest_sidecar_is_rejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hypa-quic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var libraryPath = Path.Combine(tempDir, "libmsquic.so");
        try
        {
            File.WriteAllBytes(libraryPath, [0x7f, 0x45, 0x4c, 0x46]);

            Assert.False(MsQuicNativeIntegrity.TryValidateAppLocal(libraryPath, out var detail));
            Assert.Contains("digest", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(libraryPath);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void App_local_library_with_digest_mismatch_is_rejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hypa-quic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var libraryPath = Path.Combine(tempDir, "libmsquic.so");
        try
        {
            File.WriteAllBytes(libraryPath, [1, 2, 3, 4]);
            File.WriteAllText(libraryPath + ".sha256", new string('a', 64));

            Assert.False(MsQuicNativeIntegrity.TryValidateAppLocal(libraryPath, out var detail));
            Assert.Contains("digest", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(libraryPath + ".sha256");
            File.Delete(libraryPath);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Runtime_pinning_uses_windows_switch_or_unix_validated_load()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                MsQuicRuntimePinning.IsAppLocalMsQuicEnabled,
                MsQuicRuntimePinning.CanPinValidatedLibrary);
            return;
        }

        var native = MsQuicNativeDiscovery.Discover();
        if (!native.Found)
        {
            Assert.False(MsQuicRuntimePinning.CanPinValidatedLibrary);
            return;
        }

        var pinned = MsQuicRuntimePinning.TryEnsurePinned(native.LibraryPath, out var detail);
        Assert.Equal(pinned, MsQuicRuntimePinning.CanPinValidatedLibrary);
        if (!System.Runtime.InteropServices.NativeLibrary.TryLoad(native.LibraryPath!, out _))
        {
            // A native dependency does not load on this host. Only a clean failure is valid.
            Assert.False(pinned);
            return;
        }

        Assert.True(pinned, detail);

        var loaded = System.Runtime.InteropServices.NativeLibrary.Load(native.LibraryPath!);
        var runtimeName = OperatingSystem.IsLinux() ? "libmsquic.so.2" : "libmsquic.dylib";
        Assert.True(
            System.Runtime.InteropServices.NativeLibrary.TryLoad(
                runtimeName,
                typeof(System.Net.Quic.QuicConnection).Assembly,
                searchPath: null,
                out var handle));
        Assert.Equal(loaded, handle);
        Assert.True(System.Net.Quic.QuicConnection.IsSupported);
    }

    [Fact]
    public void Resolver_returns_validated_library_for_msquic_names_only()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var native = MsQuicNativeDiscovery.Discover();
        if (!native.Found)
            return;

        var path = native.LibraryPath!;
        var loaded = System.Runtime.InteropServices.NativeLibrary.Load(path);
        Assert.NotEqual(IntPtr.Zero, loaded);
        Assert.Equal(loaded, MsQuicRuntimePinning.ResolveValidatedLibrary("msquic", path));
        Assert.Equal(loaded, MsQuicRuntimePinning.ResolveValidatedLibrary("msquic.2", path));
        Assert.Equal(loaded, MsQuicRuntimePinning.ResolveValidatedLibrary("libmsquic.so.2", path));
        Assert.Equal(loaded, MsQuicRuntimePinning.ResolveValidatedLibrary("libmsquic.so", path));
        Assert.Equal(loaded, MsQuicRuntimePinning.ResolveValidatedLibrary("libmsquic.dylib", path));
        Assert.Equal(loaded, MsQuicRuntimePinning.ResolveValidatedLibrary("libmsquic.dylib.2", path));
        Assert.Equal(IntPtr.Zero, MsQuicRuntimePinning.ResolveValidatedLibrary("libssl.so.3", path));
        Assert.Equal(IntPtr.Zero, MsQuicRuntimePinning.ResolveValidatedLibrary("libc", path));
        Assert.Equal(IntPtr.Zero, MsQuicRuntimePinning.ResolveValidatedLibrary("libcrypto.3.dylib", path));
    }

    [Fact]
    public void Pin_fails_closed_when_dllimport_resolver_is_already_registered()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        const string expected = "a DllImport resolver is already registered for System.Net.Quic";
        var assembly = typeof(QuicTransportCapabilityTests).Assembly;
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
            assembly,
            static (_, _, _) => IntPtr.Zero);

        var native = MsQuicNativeDiscovery.Discover();
        if (!MsQuicRuntimePinning.CanPinValidatedLibrary && native.Found)
        {
            Assert.False(MsQuicRuntimePinning.TryPinUnix(native.LibraryPath, assembly, out var pinDetail));
            Assert.Equal(expected, pinDetail);
            Assert.False(MsQuicRuntimePinning.CanPinValidatedLibrary);
            return;
        }

        Assert.False(MsQuicRuntimePinning.TryRegisterResolver(assembly, out var detail));
        Assert.Equal(expected, detail);
    }

    [Fact]
    public void Unix_pin_is_idempotent()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var missing = Path.Combine(Path.GetTempPath(), "hypa-missing-msquic-" + Guid.NewGuid().ToString("N"));
        var native = MsQuicNativeDiscovery.Discover();
        if (MsQuicRuntimePinning.CanPinValidatedLibrary)
        {
            Assert.True(MsQuicRuntimePinning.TryEnsurePinned(missing, out var alreadyPinned));
            Assert.Null(alreadyPinned);
            if (native.Found)
            {
                Assert.True(MsQuicRuntimePinning.TryEnsurePinned(native.LibraryPath, out var again));
                Assert.Null(again);
            }

            return;
        }

        Assert.False(MsQuicRuntimePinning.TryEnsurePinned(missing, out var missingDetail));
        Assert.False(string.IsNullOrWhiteSpace(missingDetail));
        Assert.False(MsQuicRuntimePinning.TryEnsurePinned(missing, out var missingAgain));
        Assert.False(string.IsNullOrWhiteSpace(missingAgain));
        if (!native.Found)
            return;

        var pinned = MsQuicRuntimePinning.TryEnsurePinned(native.LibraryPath, out var firstDetail);
        if (!NativeLibrary.TryLoad(native.LibraryPath!, out _))
        {
            // The file passed integrity checks, but a native dependency such as
            // libcrypto or libnuma does not load on this host. The pin must fail cleanly.
            Assert.False(pinned);
            Assert.False(string.IsNullOrWhiteSpace(firstDetail));
            Assert.False(MsQuicRuntimePinning.CanPinValidatedLibrary);
            return;
        }

        // The validated library loads here, so the pin must succeed.
        Assert.True(pinned, firstDetail);
        Assert.Null(firstDetail);
        Assert.True(MsQuicRuntimePinning.CanPinValidatedLibrary);
        Assert.True(MsQuicRuntimePinning.TryEnsurePinned(native.LibraryPath, out var secondDetail));
        Assert.Null(secondDetail);
        Assert.True(MsQuicRuntimePinning.TryEnsurePinned(missing, out var ignoredPathDetail));
        Assert.Null(ignoredPathDetail);
    }

    [Fact]
    public void Loaded_image_path_names_the_file_the_loader_mapped()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        var native = MsQuicNativeDiscovery.Discover();
        if (!native.Found || !NativeLibrary.TryLoad(native.LibraryPath!, out var handle))
            return;

        var mapped = LoadedImagePath.Of(handle, "MsQuicOpenVersion");
        Assert.NotNull(mapped);
        Assert.True(LoadedImagePath.SameFile(mapped!, native.LibraryPath!));
        Assert.Null(LoadedImagePath.Of(handle, "no_such_msquic_symbol"));
    }

    [Fact]
    public void Same_file_follows_symlinks_and_rejects_a_different_file()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        var dir = Path.Combine(Path.GetTempPath(), "hypa-same-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var real = Path.Combine(dir, "libmsquic.so.2.6.1");
            var decoy = Path.Combine(dir, "decoy.so");
            var link = Path.Combine(dir, "libmsquic.so.2");
            File.WriteAllBytes(real, [1, 2, 3]);
            File.WriteAllBytes(decoy, [1, 2, 3]);
            File.CreateSymbolicLink(link, real);

            Assert.True(LoadedImagePath.SameFile(link, real));
            Assert.False(LoadedImagePath.SameFile(decoy, real));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Sticky_world_writable_ancestor_does_not_reject_private_install()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        if (!Directory.Exists("/tmp") || !MsQuicNativeIntegrity.IsUnixStickyWorldWritable("/tmp"))
            return;

        // Put the install under testhost so the walk includes /tmp when this tree lives there.
        var tempRoot = Path.Combine(PrivateTestRoot, "hypa-quic-sticky-" + Guid.NewGuid().ToString("N"));
        var installDir = Path.Combine(tempRoot, "app");
        Directory.CreateDirectory(installDir);
        File.SetUnixFileMode(
            installDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var libraryPath = Path.Combine(installDir, "libmsquic.so");
        var payload = new byte[] { 21, 22, 23, 24 };
        try
        {
            File.WriteAllBytes(libraryPath, payload);
            File.SetUnixFileMode(libraryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(
                libraryPath + ".sha256",
                Convert.ToHexString(SHA256.HashData(payload)));
            File.SetUnixFileMode(
                libraryPath + ".sha256",
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.True(MsQuicNativeIntegrity.TryValidateAppLocal(libraryPath, out var detail), detail);
        }
        finally
        {
            if (File.Exists(libraryPath + ".sha256"))
                File.Delete(libraryPath + ".sha256");
            if (File.Exists(libraryPath))
                File.Delete(libraryPath);
            if (Directory.Exists(installDir))
                Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Parent_directory_symlink_in_publish_tree_is_rejected()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var token = Guid.NewGuid().ToString("N");
        var realRoot = Path.Combine(AppContext.BaseDirectory, "hypa-quic-real-" + token);
        var linkRoot = Path.Combine(AppContext.BaseDirectory, "hypa-quic-link-" + token);
        var installDir = Path.Combine(linkRoot, "publish");
        var libraryPath = Path.Combine(installDir, "libmsquic.so");
        var payload = new byte[] { 13, 14, 15, 16 };
        try
        {
            Directory.CreateDirectory(Path.Combine(realRoot, "publish"));
            File.CreateSymbolicLink(linkRoot, realRoot);
            var realLibraryPath = Path.Combine(realRoot, "publish", "libmsquic.so");
            File.WriteAllBytes(realLibraryPath, payload);
            File.WriteAllText(
                realLibraryPath + ".sha256",
                Convert.ToHexString(SHA256.HashData(payload)));

            Assert.False(MsQuicNativeIntegrity.TryValidateAppLocal(libraryPath, out var detail));
            Assert.Contains("symlink", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(Path.Combine(realRoot, "publish", "libmsquic.so.sha256")))
                File.Delete(Path.Combine(realRoot, "publish", "libmsquic.so.sha256"));
            if (File.Exists(Path.Combine(realRoot, "publish", "libmsquic.so")))
                File.Delete(Path.Combine(realRoot, "publish", "libmsquic.so"));
            if (Directory.Exists(Path.Combine(realRoot, "publish")))
                Directory.Delete(Path.Combine(realRoot, "publish"), recursive: true);
            if (Directory.Exists(linkRoot))
                Directory.Delete(linkRoot);
            if (Directory.Exists(realRoot))
                Directory.Delete(realRoot, recursive: true);
        }
    }

    [Fact]
    public void Directory_symlink_in_install_path_is_rejected()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var targetDir = Path.Combine(Path.GetTempPath(), "hypa-quic-target-" + Guid.NewGuid().ToString("N"));
        var linkDir = Path.Combine(Path.GetTempPath(), "hypa-quic-link-" + Guid.NewGuid().ToString("N"));
        var libraryPath = Path.Combine(linkDir, "libmsquic.so");
        Directory.CreateDirectory(targetDir);
        try
        {
            File.CreateSymbolicLink(linkDir, targetDir);
            File.WriteAllBytes(libraryPath, [0x01]);

            Assert.False(MsQuicNativeIntegrity.TryValidateAppLocal(libraryPath, out var detail));
            Assert.Contains("symlink", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(libraryPath))
                File.Delete(libraryPath);
            if (Directory.Exists(linkDir))
                Directory.Delete(linkDir);
            Directory.Delete(targetDir, recursive: true);
        }
    }

    [Fact]
    public void Linux_extended_attribute_probe_accepts_only_enodata_as_absent()
    {
        Assert.True(MsQuicNativeIntegrity.IsLinuxAbsentExtendedAttributeErrno(61));
        Assert.False(MsQuicNativeIntegrity.IsLinuxAbsentExtendedAttributeErrno(93));
        Assert.False(MsQuicNativeIntegrity.IsLinuxAbsentExtendedAttributeErrno(13));
    }

    [Fact]
    public void Mac_extended_acl_probe_accepts_only_success_or_enoent_as_absent()
    {
        Assert.True(MsQuicNativeIntegrity.IsMacAbsentExtendedAclErrno(0));
        Assert.True(MsQuicNativeIntegrity.IsMacAbsentExtendedAclErrno(2));
        Assert.False(MsQuicNativeIntegrity.IsMacAbsentExtendedAclErrno(22));
        Assert.False(MsQuicNativeIntegrity.IsMacAbsentExtendedAclErrno(93));
    }

    [Fact]
    public void Ancestor_with_posix_acl_in_publish_tree_is_rejected()
    {
        if (!OperatingSystem.IsLinux())
            return;

        if (!HasSetfaclOnPath())
            return;

        var token = Guid.NewGuid().ToString("N");
        var parentDir = Path.Combine(AppContext.BaseDirectory, "hypa-quic-parent-" + token);
        var installDir = Path.Combine(parentDir, "publish");
        var libraryPath = Path.Combine(installDir, "libmsquic.so");
        var payload = new byte[] { 17, 18, 19, 20 };
        try
        {
            Directory.CreateDirectory(installDir);
            RunSetfacl(parentDir, "u:nobody:rw");
            File.WriteAllBytes(libraryPath, payload);
            File.WriteAllText(
                libraryPath + ".sha256",
                Convert.ToHexString(SHA256.HashData(payload)));

            Assert.False(MsQuicNativeIntegrity.TryValidateAppLocal(libraryPath, out var detail));
            Assert.Contains("ACL", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(libraryPath + ".sha256"))
                File.Delete(libraryPath + ".sha256");
            if (File.Exists(libraryPath))
                File.Delete(libraryPath);
            if (Directory.Exists(installDir))
                Directory.Delete(installDir, recursive: true);
            if (Directory.Exists(parentDir))
                Directory.Delete(parentDir, recursive: true);
        }
    }

    [Fact]
    public void Publish_hard_link_guard_aborts_when_hard_link_check_cannot_open_path()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var script = FindRepoScript("test-publish-hardlink-guard.ps1");
        if (script is null)
            return;

        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script!}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("pwsh could not start");
        process.WaitForExit();
        var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("hard-link check", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capability_document_serializes_without_raw_native_path()
    {
        var document = ConnectivityCommand.ToDocument(new QuicTransportCapabilityProbe().Probe());
        var json = System.Text.Json.JsonSerializer.Serialize(
            document,
            ConnectivityJsonContext.Default.QuicTransportCapabilityDocument);

        Assert.Contains("\"runtime_identifier\"", json, StringComparison.Ordinal);
        Assert.Contains("\"native_library_location\"", json, StringComparison.Ordinal);
        Assert.Contains("\"quic_provider\":\"quic\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fallback_provider\":\"tcp\"", json, StringComparison.Ordinal);
        Assert.Contains("\"zero_rtt_enabled\":false", json, StringComparison.Ordinal);
        Assert.DoesNotContain("native_library_path", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Bundled_libnuma_requires_a_matching_digest_sidecar()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var installDir = Path.Combine(
            PrivateTestRoot,
            "hypa-numa-valid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDir);
        File.SetUnixFileMode(
            installDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var libraryPath = Path.Combine(installDir, MsQuicRuntimePinning.LibNumaFileName);
        var payload = new byte[] { 31, 32, 33, 34 };
        try
        {
            File.WriteAllBytes(libraryPath, payload);
            File.SetUnixFileMode(libraryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(libraryPath + ".sha256", Convert.ToHexString(SHA256.HashData(payload)));
            File.SetUnixFileMode(libraryPath + ".sha256", UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.True(MsQuicRuntimePinning.TryValidateBundledLibNuma(installDir, out var detail), detail);

            File.WriteAllText(libraryPath + ".sha256", new string('b', 64));
            Assert.False(MsQuicRuntimePinning.TryValidateBundledLibNuma(installDir, out detail));
            Assert.Contains("digest", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
        }
    }

    [Fact]
    public void Bundled_libnuma_is_absent_when_the_file_is_missing()
    {
        var installDir = Path.Combine(Path.GetTempPath(), "hypa-numa-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDir);
        try
        {
            Assert.False(MsQuicRuntimePinning.TryValidateBundledLibNuma(installDir, out var detail));
            Assert.Contains("libnuma", detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(installDir, recursive: true);
        }
    }

    [Fact]
    public void Libnuma_prepare_is_skipped_when_the_host_does_not_require_it()
    {
        var called = false;
        Assert.True(MsQuicRuntimePinning.TryPrepareBundledLibNuma(
            requireLibNuma: false,
            msquicLibraryPath: Path.Combine(Path.GetTempPath(), "libmsquic.so"),
            validate: _ =>
            {
                called = true;
                return (false, "should not run");
            },
            load: _ => throw new InvalidOperationException("should not load"),
            out var detail));
        Assert.False(called);
        Assert.Null(detail);
    }

    [Fact]
    public void Libnuma_prepare_loads_the_validated_image_before_msquic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hypa-numa-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var msquic = Path.Combine(dir, "libmsquic.so.2");
        var numa = Path.Combine(dir, MsQuicRuntimePinning.LibNumaFileName);
        File.WriteAllBytes(msquic, [1]);
        File.WriteAllBytes(numa, [2]);
        try
        {
            string? validated = null;
            string? loaded = null;
            Assert.True(MsQuicRuntimePinning.TryPrepareBundledLibNuma(
                requireLibNuma: true,
                msquic,
                path =>
                {
                    validated = path;
                    return (true, null);
                },
                path =>
                {
                    loaded = path;
                    return new MsQuicRuntimePinning.LoadedNativeImage(new IntPtr(4), path);
                },
                out var detail));
            Assert.Null(detail);
            Assert.Equal(numa, validated);
            Assert.Equal(numa, loaded);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Libnuma_prepare_fails_when_validation_or_the_mapped_image_is_wrong()
    {
        var msquic = Path.Combine(Path.GetTempPath(), "libmsquic.so.2");
        var loaded = false;
        Assert.False(MsQuicRuntimePinning.TryPrepareBundledLibNuma(
            requireLibNuma: true,
            msquic,
            _ => (false, "library digest sidecar is missing"),
            _ =>
            {
                loaded = true;
                return new MsQuicRuntimePinning.LoadedNativeImage(new IntPtr(1), null);
            },
            out var detail));
        Assert.False(loaded);
        Assert.Contains("digest", detail, StringComparison.OrdinalIgnoreCase);

        Assert.False(MsQuicRuntimePinning.TryPrepareBundledLibNuma(
            requireLibNuma: true,
            msquic,
            _ => (true, null),
            path => new MsQuicRuntimePinning.LoadedNativeImage(new IntPtr(4), path + ".other"),
            out detail));
        Assert.Contains("libnuma", detail, StringComparison.OrdinalIgnoreCase);

        Assert.False(MsQuicRuntimePinning.TryPrepareBundledLibNuma(
            requireLibNuma: true,
            msquic,
            _ => (true, null),
            _ => throw new DllNotFoundException("libnuma.so.1"),
            out detail));
        Assert.Contains("libnuma", detail, StringComparison.OrdinalIgnoreCase);

        Assert.False(MsQuicRuntimePinning.TryPrepareBundledLibNuma(
            requireLibNuma: true,
            msquic,
            _ => (true, null),
            path => new MsQuicRuntimePinning.LoadedNativeImage(IntPtr.Zero, path),
            out detail));
        Assert.Contains("libnuma", detail, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindRepoScript(string scriptName)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && current is not null; depth++)
        {
            var candidate = Path.Combine(current.FullName, "scripts", scriptName);
            if (File.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        return null;
    }

    private static bool HasSetfaclOnPath()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "setfacl",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return false;

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A root for install trees that the tests build. The build output dir can
    /// inherit a default ACL on a CI runner, and the guard rejects an install
    /// dir with an extended ACL.
    /// </summary>
    private static string PrivateTestRoot => Path.GetTempPath();

    private static void RunSetfacl(string path, string entry)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "setfacl",
            Arguments = $"-m default:{entry} \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("setfacl could not start");

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var detail = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"setfacl failed: {detail}");
        }
    }
}
