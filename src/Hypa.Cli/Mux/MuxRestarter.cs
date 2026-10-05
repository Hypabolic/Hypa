using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure;
using Hypa.ControlPlane;

namespace Hypa.Cli.Mux;

/// <summary>
/// Restarts a mux so it runs the installed Hypa. <c>hypa mux restart</c>
/// stops and starts it here. The sidebar row ends attach, stops the mux and
/// execs the installed <c>hypa attach</c> through <see cref="TryExecAttach"/>.
/// A future auto-updater runs the same steps after it installs.
/// </summary>
internal static class MuxRestarter
{
    // Linux reports a deleted executable as "<path> (deleted)".
    private const string DeletedSuffix = " (deleted)";

    internal const string InsideMuxCopy =
        "hypa mux restart: this shell runs inside the mux it would restart. " +
        "Click " + MuxRestartCommands.SidebarAction + " in the sidebar, or run it from a terminal outside Hypa.";

    /// <summary>
    /// Stopping the mux kills a shell that runs inside it before the new mux
    /// starts. Pane shells carry the mux socket in HYPA_RUNTIME_SOCKET.
    /// </summary>
    internal static bool RunsInsideCurrentProcess(string socketPath) =>
        RunsInside(socketPath, Environment.GetEnvironmentVariable(CustomCommandEnvironment.RuntimeSocket));

    /// <summary>Stops the mux when one is live, then starts a fresh one from this <c>hypa</c>.</summary>
    internal static async Task<int> RestartAsync(
        string session,
        string socketPath,
        string? socketOverride,
        IMuxSupervisor supervisor,
        TextWriter output,
        TextWriter error,
        CancellationToken ct)
    {
        var ping = await MuxControlPlane.TryPingAsync(socketPath, ct).ConfigureAwait(false);
        if (ping is null)
        {
            await output.WriteLineAsync($"No mux was running for session '{session}'. Starting one.")
                .ConfigureAwait(false);
        }
        else
        {
            var stopped = await MuxServerStopper.StopAsync(session, socketPath, output, error)
                .ConfigureAwait(false);
            if (stopped != 0)
                return stopped;
        }

        try
        {
            await supervisor.EnsureReadyAsync(session, cwd: null, socketOverride, ct).ConfigureAwait(false);
        }
        catch (MuxAttachException ex)
        {
            await error.WriteLineAsync($"hypa mux restart: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync(
                $"Started mux session={session}. Run `{MuxRestartCommands.Attach(session)}` to attach.")
            .ConfigureAwait(false);
        return 0;
    }

    /// <summary>True when <paramref name="envSocket"/> names the mux at <paramref name="socketPath"/>.</summary>
    internal static bool RunsInside(string socketPath, string? envSocket)
    {
        if (string.IsNullOrWhiteSpace(envSocket))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(envSocket),
                Path.GetFullPath(socketPath),
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The installed <c>hypa</c> to exec after an in-app restart, so attach
    /// and mux both come up on the installed version. Prefers the binary
    /// beside this one, then <c>PATH</c> (an upgrade may have deleted this
    /// install's directory). Null for a source build or test host, which
    /// re-attach in process instead.
    /// </summary>
    internal static string? ResolveRelaunchBinary(string? processPath, string? pathEnvironment)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return null;
        if (processPath.EndsWith(DeletedSuffix, StringComparison.Ordinal))
            processPath = processPath[..^DeletedSuffix.Length];

        var name = Path.GetFileNameWithoutExtension(processPath);
        if (!name.Equals(MuxInvocation.ProductFileName, StringComparison.OrdinalIgnoreCase)
            && !name.Equals(MuxInvocation.LeanAttachFileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // The SDK apphost is also named hypa and sits beside hypa.dll.
        var dir = Path.GetDirectoryName(processPath);
        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "hypa.dll")))
            return null;

        var sibling = HypaCliPathResolver.Resolve(processPath);
        if (sibling is not null && File.Exists(sibling))
            return sibling;

        if (string.IsNullOrWhiteSpace(pathEnvironment))
            return null;
        var fileName = OperatingSystem.IsWindows() ? "hypa.exe" : "hypa";
        foreach (var entry in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Replaces this attach process with <c>hypa attach</c> from the installed
    /// Hypa. Returns only when exec was not possible.
    /// </summary>
    internal static bool TryExecAttach(string session, TextWriter error)
    {
        if (OperatingSystem.IsWindows())
            return false;
        var binary = ResolveRelaunchBinary(
            Environment.ProcessPath,
            Environment.GetEnvironmentVariable("PATH"));
        if (binary is null)
            return false;
        if (UnixProcessReplace.TryExec(binary, ["attach", "--session", session], out var errno))
            return true;
        error.WriteLine(UnixProcessReplace.FormatExecFailure(binary, errno));
        return false;
    }
}
