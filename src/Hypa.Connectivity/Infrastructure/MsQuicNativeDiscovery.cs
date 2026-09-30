namespace Hypa.Connectivity.Infrastructure;

/// <summary>Locates a validated MsQuic native library beside the app only.</summary>
public static class MsQuicNativeDiscovery
{
    public sealed record Result(
        bool Found,
        string Location,
        string? IntegrityDetail,
        string? LibraryPath = null);

    public static Result Discover()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            if (!MsQuicNativeIntegrity.TryValidateInstallDirectory(baseDir, out var directoryDetail))
            {
                return new Result(
                    false,
                    Domain.MsQuicNativeLocations.Absent,
                    directoryDetail ?? "application directory failed integrity checks");
            }

            foreach (var name in CandidateNames())
            {
                var besideApp = Path.Combine(baseDir, name);
                if (!File.Exists(besideApp))
                    continue;

                if (MsQuicNativeIntegrity.TryValidateAppLocal(besideApp, out var failureDetail))
                {
                    if (OperatingSystem.IsLinux()
                        && !MsQuicRuntimePinning.TryValidateBundledLibNuma(baseDir, out var numaDetail))
                    {
                        return new Result(
                            false,
                            Domain.MsQuicNativeLocations.Absent,
                            numaDetail ?? "validated libnuma.so.1 was not found beside the app");
                    }

                    return new Result(true, Domain.MsQuicNativeLocations.AppLocal, null, besideApp);
                }

                return new Result(
                    false,
                    Domain.MsQuicNativeLocations.Absent,
                    failureDetail ?? "app-local MsQuic library failed integrity checks");
            }

            return new Result(false, Domain.MsQuicNativeLocations.Absent, null);
        }
        catch (IOException)
        {
            return new Result(false, Domain.MsQuicNativeLocations.Absent, "native library discovery failed");
        }
        catch (UnauthorizedAccessException)
        {
            return new Result(false, Domain.MsQuicNativeLocations.Absent, "native library discovery failed");
        }
    }

    private static IEnumerable<string> CandidateNames()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "msquic.dll";
            yield break;
        }

        if (OperatingSystem.IsLinux())
        {
            // MsQuicApi opens libmsquic.so.<major> before libmsquic.so.
            yield return "libmsquic.so.2";
            yield return "libmsquic.so";
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "libmsquic.dylib";
            yield return "libmsquic.2.dylib";
        }
    }
}
