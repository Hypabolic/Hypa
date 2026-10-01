using System.Diagnostics;
using System.Net.Sockets;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.Config;
using Hypa.ControlPlane;
using Hypa.ControlPlane.Unix;

namespace Hypa.Cli.Mux;

public sealed class ProcessMuxSupervisor : IMuxSupervisor
{
    private readonly TimeSpan _readyTimeout;
    private readonly TimeProvider _time;

    public ProcessMuxSupervisor()
        : this(readyTimeout: null, time: null)
    {
    }

    internal ProcessMuxSupervisor(TimeSpan? readyTimeout, TimeProvider? time)
    {
        _readyTimeout = readyTimeout ?? MuxReadyBudget.Timeout;
        _time = time ?? TimeProvider.System;
    }

    internal TimeSpan ReadyTimeout => _readyTimeout;

    /// <summary>
    /// Supervisor spawn only. Prefer <c>setsid(1)</c> when present so the
    /// mux leaves the attach TTY group before exec. Otherwise exec the mux
    /// and let <c>HYPA_MUX_DAEMONIZE=1</c> call <c>setsid(2)</c> inside the
    /// host. Do not use this wrapper for foreground <c>hypa mux serve</c>.
    /// </summary>
    internal const string UnixDaemonizeShell =
        "if command -v setsid >/dev/null 2>&1; then " +
        "exec setsid \"$HYPA_MUX_BIN\" \"$@\" </dev/null >/dev/null 2>&1; " +
        "fi; " +
        "exec \"$HYPA_MUX_BIN\" \"$@\" </dev/null >/dev/null 2>&1";

    internal const string DaemonizeEnvironmentVariable = "HYPA_MUX_DAEMONIZE";
    internal const string LogEnvironmentVariable = "HYPA_MUX_LOG";

    public async Task<MuxReadyInfo> EnsureReadyAsync(
        string session,
        string? cwd,
        string? socketOverride,
        CancellationToken ct)
    {
        string socketPath;
        try
        {
            socketPath = socketOverride ?? UnixSocketServer.ResolveSocketPath(session);
        }
        catch (Exception ex)
        {
            throw new MuxAttachException(ex.Message, ex);
        }

        var ping = await MuxControlPlane.TryPingAsync(socketPath, ct).ConfigureAwait(false);
        if (ping is not null)
            return new MuxReadyInfo(session, socketPath, ping);

        Process? child = null;
        try
        {
            var launch = ResolveLaunch();
            var logDir = Path.GetDirectoryName(socketPath);
            if (!string.IsNullOrEmpty(logDir))
                EnsurePrivateSocketDirectory(logDir, session);

            var logPath = Path.Combine(logDir ?? ".", "mux.log");

            var processCwd = string.IsNullOrWhiteSpace(cwd)
                ? Environment.CurrentDirectory
                : cwd;
            var serveArgs = new List<string>(launch.Prefix)
            {
                "--session",
                session,
            };
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                serveArgs.Add("--cwd");
                serveArgs.Add(cwd);
            }

            var psi = CreateServeStartInfo(launch.FileName, serveArgs, processCwd, logPath);
            ApplySupervisorDaemonizeEnvironment(psi, logPath);
            if (!string.IsNullOrWhiteSpace(socketOverride))
                psi.Environment["HYPA_RUNTIME_SOCKET"] = socketPath;

            child = Process.Start(psi);
            if (child is null)
                throw new MuxAttachException($"Failed to start mux server: {launch.FileName}");

            if (psi.RedirectStandardInput)
                child.StandardInput.Close();
            // exec setsid replaces this process. An exit before the socket is ready is a failed start.
            ping = await WaitForReadyAsync(socketPath, logPath, ct, child, session).ConfigureAwait(false);
            return new MuxReadyInfo(session, socketPath, ping);
        }
        catch (SocketException ex) when (OperatingSystem.IsWindows())
        {
            throw new MuxAttachException(
                "Mux attach requires a Unix domain socket. This Windows host cannot attach. " +
                "Compression commands (hypa -c, hypa git, hypa doctor) still work.",
                ex);
        }
        catch (MuxAttachException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MuxAttachException(ex.Message, ex);
        }
        finally
        {
            if (child is { HasExited: false })
            {
                // Detach: server stays. Do not dispose the live process.
            }
            else
            {
                child?.Dispose();
            }
        }
    }

    /// <summary>
    /// Create the socket parent with the server's 0700 guard before spawn.
    /// A plain create follows the umask (0775 under umask 002). The server then
    /// refuses the parent, and the mux.log sink refuses it too, so the failure
    /// leaves no log. An existing shared-write parent is refused here, where
    /// the attach client can still show the error.
    /// </summary>
    internal static void EnsurePrivateSocketDirectory(string directory, string session)
    {
        try
        {
            new UnixSocketOwnerGuard().EnsurePrivateDirectory(directory);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MuxAttachException(
                ex.Message + PrivateDirectoryFixHint(directory, DefaultSocketDirectory(session)), ex);
        }
    }

    /// <summary>
    /// Suggest chmod only for Hypa's own runtime directory. A custom
    /// HYPA_RUNTIME_SOCKET parent may be shared, such as /tmp.
    /// </summary>
    internal static string PrivateDirectoryFixHint(string directory, string? defaultDirectory)
    {
        if (string.IsNullOrEmpty(defaultDirectory))
            return "";

        return string.Equals(
            Path.GetFullPath(directory),
            Path.GetFullPath(defaultDirectory),
            StringComparison.Ordinal)
            ? $" Fix it with: chmod 700 '{directory}'"
            : "";
    }

    private static string? DefaultSocketDirectory(string session)
    {
        try
        {
            return Path.GetDirectoryName(
                UnixSocketServer.ResolveSocketPath(session, honorEnvironment: false));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal async Task<string> WaitForReadyAsync(
        string socketPath,
        string logPath,
        CancellationToken ct,
        Process? child = null,
        string? session = null)
    {
        var deadline = _time.GetUtcNow() + _readyTimeout;
        while (_time.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var ping = await MuxControlPlane.TryPingAsync(socketPath, ct).ConfigureAwait(false);
            if (ping is not null)
                return ping;

            // A ping cancelled by ct returns not-ready. Report the cancel, not a failed start.
            ct.ThrowIfCancellationRequested();
            if (child is { HasExited: true })
                break;

            await Task.Delay(MuxReadyBudget.PollInterval, _time, ct).ConfigureAwait(false);
        }

        var last = await MuxControlPlane.TryPingAsync(socketPath, ct).ConfigureAwait(false);
        if (last is not null)
            return last;

        throw new MuxAttachException(NotReadyMessage(socketPath, logPath, session));
    }

    /// <summary>
    /// The server writes errors before its log sink opens to stderr, which the
    /// daemonized spawn discards. Do not point at a log that was never written.
    /// </summary>
    internal static string NotReadyMessage(string socketPath, string logPath, string? session)
    {
        if (File.Exists(logPath))
            return $"Mux server did not become ready at {socketPath}. See {logPath}.";

        var serve = string.IsNullOrEmpty(session)
            ? "hypa mux serve"
            : $"hypa mux serve --session {session}";
        return $"Mux server did not become ready at {socketPath}. " +
            $"It wrote no log at {logPath}. Run '{serve}' to see the startup error.";
    }

    internal static MuxLaunch ResolveLaunch()
    {
        var overridePath = Environment.GetEnvironmentVariable("HYPA_MUX_SERVER");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return LaunchFromPath(overridePath);

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var name = Path.GetFileNameWithoutExtension(processPath);
            if (name.Equals("hypa", StringComparison.OrdinalIgnoreCase))
                return new MuxLaunch(processPath, ["mux", "serve"]);
        }

        var searchDirs = new List<string>();
        if (!string.IsNullOrEmpty(processPath))
        {
            var processDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(processDir))
                searchDirs.Add(processDir);
        }

        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
            searchDirs.Add(baseDir);

        foreach (var dir in searchDirs.Distinct(StringComparer.Ordinal))
        {
            var sibling = Path.Combine(dir, OperatingSystem.IsWindows() ? "hypa.exe" : "hypa");
            if (File.Exists(sibling))
                return new MuxLaunch(sibling, ["mux", "serve"]);

            var dll = Path.Combine(dir, "hypa.dll");
            if (File.Exists(dll))
                return new MuxLaunch(ResolveDotnetHost(), ["exec", dll, "mux", "serve"]);
        }

        return new MuxLaunch("hypa-runtime", ["mux", "serve"]);
    }

    internal static ProcessStartInfo CreateServeStartInfo(
        string fileName,
        IReadOnlyList<string> serveArgs,
        string cwd,
        string logPath)
    {
        ProcessStartInfo psi;
        if (!OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
                WorkingDirectory = cwd,
            };
            psi.Environment["HYPA_MUX_BIN"] = fileName;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(UnixDaemonizeShell);
            psi.ArgumentList.Add("hypa-mux");
            foreach (var arg in serveArgs)
                psi.ArgumentList.Add(arg);
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
                WorkingDirectory = cwd,
            };
            foreach (var arg in serveArgs)
                psi.ArgumentList.Add(arg);
        }

        // Child WorkingDirectory is --cwd. Relative HYPA_CONFIG_PATH must stay
        // bound to the client process that resolved it.
        FileAttachConfigLoader.PinResolvedPaths(psi.Environment);
        return psi;
    }

    private static MuxLaunch LaunchFromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("hypa", StringComparison.OrdinalIgnoreCase))
            return new MuxLaunch(path, ["mux", "serve"]);
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return new MuxLaunch(ResolveDotnetHost(), ["exec", path, "mux", "serve"]);
        return new MuxLaunch(path, ["mux", "serve"]);
    }

    private static string ResolveDotnetHost()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(host))
            return host;

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
            return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        return "dotnet";
    }

    internal static void ApplySupervisorDaemonizeEnvironment(ProcessStartInfo psi, string logPath)
    {
        psi.Environment[LogEnvironmentVariable] = logPath;
        psi.Environment[DaemonizeEnvironmentVariable] = "1";
    }

    internal sealed record MuxLaunch(string FileName, string[] Prefix);
}

public sealed class MuxAttachException : Exception
{
    public MuxAttachException(string message) : base(message)
    {
    }

    public MuxAttachException(string message, Exception inner) : base(message, inner)
    {
    }
}
