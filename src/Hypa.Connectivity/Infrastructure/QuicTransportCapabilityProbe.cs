using System.Net.Quic;
using System.Runtime.InteropServices;
using Hypa.Connectivity.Application;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Infrastructure;

public sealed class QuicTransportCapabilityProbe : IQuicTransportCapabilityProbe
{
    public QuicTransportCapabilityReport Probe()
    {
        try
        {
            var native = MsQuicNativeDiscovery.Discover();
            if (!native.Found)
            {
                return FailClosed(
                    native,
                    isSupported: false,
                    nativeLibraryFound: false,
                    detail: native.IntegrityDetail
                        ?? "validated MsQuic native library was not found beside the app");
            }

            if (!MsQuicRuntimePinning.TryEnsurePinned(native.LibraryPath, out var pinningDetail)
                || !MsQuicRuntimePinning.CanPinValidatedLibrary)
            {
                return FailClosed(
                    native,
                    isSupported: false,
                    nativeLibraryFound: true,
                    detail: pinningDetail
                        ?? "runtime cannot pin MsQuic to the validated app-local library");
            }

            bool supported;
            try
            {
                supported = QuicConnection.IsSupported;
            }
            catch (Exception)
            {
                return FailClosed(
                    native,
                    isSupported: false,
                    nativeLibraryFound: true,
                    detail: "MsQuic runtime capability check failed");
            }

            if (!supported)
            {
                return FailClosed(
                    native,
                    isSupported: false,
                    nativeLibraryFound: true,
                    detail: "QuicConnection.IsSupported is false on this host");
            }

            return new QuicTransportCapabilityReport
            {
                RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                IsSupported = true,
                NativeLibraryFound = true,
                NativeLibraryLocation = native.Location,
                QuicProvider = BytePathProviders.Quic,
                FallbackProvider = BytePathProviders.Tcp,
                ZeroRttEnabled = QuicTransportPolicy.ZeroRttEnabled,
                Reason = null,
                Detail = null,
            };
        }
        catch (Exception)
        {
            return FailClosed(
                new MsQuicNativeDiscovery.Result(false, MsQuicNativeLocations.Absent, null),
                isSupported: false,
                nativeLibraryFound: false,
                detail: "MsQuic capability probe failed");
        }
    }

    private static QuicTransportCapabilityReport FailClosed(
        MsQuicNativeDiscovery.Result native,
        bool isSupported,
        bool nativeLibraryFound,
        string detail) =>
        new()
        {
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            IsSupported = isSupported,
            NativeLibraryFound = nativeLibraryFound,
            NativeLibraryLocation = nativeLibraryFound
                ? native.Location
                : MsQuicNativeLocations.Absent,
            QuicProvider = BytePathProviders.Quic,
            FallbackProvider = BytePathProviders.Tcp,
            ZeroRttEnabled = QuicTransportPolicy.ZeroRttEnabled,
            Reason = ConnectivityReasons.QuicUnsupported,
            Detail = detail,
        };
}
