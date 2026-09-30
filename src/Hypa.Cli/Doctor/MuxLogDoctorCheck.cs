using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.Logging;
using Hypa.ControlPlane;
using Hypa.Runtime.Application.Ports;

namespace Hypa.Cli.Doctor;

/// <summary>
/// Reports mux log path, Unix mode, and sink-disabled state.
/// </summary>
public sealed class MuxLogDoctorCheck : IDoctorCheck
{
    private readonly string? _pathOverride;

    public MuxLogDoctorCheck()
        : this(null)
    {
    }

    public MuxLogDoctorCheck(string? pathOverride)
    {
        _pathOverride = pathOverride;
    }

    public string Category => "Mux";

    public DoctorCheckResult Run()
    {
        var path = ResolvePath();
        var marker = ProcessLogPaths.DisabledMarkerPath(path);
        if (File.Exists(marker))
        {
            return new DoctorCheckResult(
                "Mux log",
                path,
                DoctorStatus.Warn,
                "process log sink is disabled",
                "The sink latched after a write or rotation error. Product calls still succeed.");
        }

        if (!File.Exists(path))
        {
            if (IsUnusableParent(path))
            {
                return new DoctorCheckResult(
                    "Mux log",
                    path,
                    DoctorStatus.Warn,
                    "process log parent is unusable (read-only permissions); sink is disabled",
                    "The sink cannot create a log file or disabled marker. Product calls still succeed.");
            }

            return new DoctorCheckResult(
                "Mux log",
                path,
                DoctorStatus.Warn,
                "log file is not present");
        }

        var detail = path;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (UnixLogPathSecurity.IsSymlink(path))
            {
                return new DoctorCheckResult(
                    "Mux log",
                    path,
                    DoctorStatus.Warn,
                    "log path is a symlink");
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                if (UnixLogPathSecurity.IsSymlink(parent))
                {
                    return new DoctorCheckResult(
                        "Mux log",
                        path,
                        DoctorStatus.Warn,
                        "log parent is a symlink");
                }

                if (UnixLogPathSecurity.HasSharedWrite(parent))
                {
                    return new DoctorCheckResult(
                        "Mux log",
                        path,
                        DoctorStatus.Warn,
                        "log parent is writable by group or others");
                }
            }

            var mode = File.GetUnixFileMode(path);
            var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if ((mode & ~privateMode) != 0)
            {
                return new DoctorCheckResult(
                    "Mux log",
                    path,
                    DoctorStatus.Warn,
                    $"mode is {(int)mode:o}, want 0600");
            }

            detail = path + " mode=0600";
        }

        return new DoctorCheckResult("Mux log", path, DoctorStatus.Ok, detail);
    }

    private string ResolvePath()
    {
        if (!string.IsNullOrWhiteSpace(_pathOverride))
            return ProcessLogPaths.NormalizeLogPath(_pathOverride);
        var env = Environment.GetEnvironmentVariable(ProcessLogPaths.MuxLogVariable);
        if (!string.IsNullOrWhiteSpace(env))
            return ProcessLogPaths.NormalizeLogPath(env);
        try
        {
            var socket = UnixSocketServer.ResolveSocketPath("default");
            return ProcessLogPaths.NormalizeLogPath(ProcessLogPaths.DefaultMuxPath(socket));
        }
        catch
        {
            return ProcessLogPaths.NormalizeLogPath(ProcessLogPaths.DefaultMuxFileName);
        }
    }

    /// <summary>
    // A private
    /// read-only parent latches the sink without a marker because
    /// WriteDisabledMarker cannot create a file there.
    /// </summary>
    private static bool IsUnusableParent(string path)
    {
        if (!UnixLogPathSecurity.IsUnix)
            return false;
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            return false;
        if (UnixLogPathSecurity.IsSymlink(parent))
            return false;
        return !UnixLogPathSecurity.HasOwnerWrite(parent);
    }
}
