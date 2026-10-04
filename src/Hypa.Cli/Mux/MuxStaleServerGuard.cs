namespace Hypa.Cli.Mux;

/// <summary>
/// Before attach, offers to restart a mux left running by an older or
/// removed install. Restarting closes every pane, so it always asks first.
/// </summary>
public sealed class MuxStaleServerGuard
{
    internal const string RestartHint = "Run `hypa mux stop`, then `hypa attach`, to restart it.";
    internal const string RestartImpact = "Restarting the mux closes every running pane in this session.";
    internal const string KeepCopy =
        "Keeping the running mux. Run `hypa mux stop`, then `hypa attach`, when you are ready.";
    internal const string StopFailedCopy = "Could not stop the mux. Attaching to the running one.";

    private readonly TextReader _input;
    private readonly TextWriter _error;
    private readonly bool _interactive;
    private readonly string? _clientVersion;
    private readonly Func<string, string, Task<bool>> _stop;

    public MuxStaleServerGuard()
        : this(
            Console.In,
            Console.Error,
            !Console.IsInputRedirected && !Console.IsOutputRedirected,
            MuxServerVersionCheck.CurrentClientVersion(),
            async (session, socket) =>
                await MuxServerStopper.StopAsync(session, socket, Console.Error, Console.Error)
                    .ConfigureAwait(false) == 0)
    {
    }

    internal MuxStaleServerGuard(
        TextReader input,
        TextWriter error,
        bool interactive,
        string? clientVersion,
        Func<string, string, Task<bool>> stop)
    {
        _input = input;
        _error = error;
        _interactive = interactive;
        _clientVersion = clientVersion;
        _stop = stop;
    }

    /// <summary>
    /// True when the stale mux was stopped and the caller should start a
    /// fresh one. False to attach to the mux that is running.
    /// </summary>
    public async Task<bool> TryRestartAsync(MuxReadyInfo ready, bool once)
    {
        ArgumentNullException.ThrowIfNull(ready);
        var check = MuxServerVersionCheck.FromPing(ready.PingJson, _clientVersion);
        if (check is null || !check.IsStale)
            return false;

        var detail = check.Describe(ready.Session);
        if (once || !_interactive)
        {
            await _error.WriteLineAsync($"hypa attach: {detail} {RestartHint}").ConfigureAwait(false);
            return false;
        }

        if (!RemoteRestartConsent.TryPrompt(_input, _error, $"{detail}\n{RestartImpact}"))
        {
            await _error.WriteLineAsync(KeepCopy).ConfigureAwait(false);
            return false;
        }

        if (await _stop(ready.Session, ready.SocketPath).ConfigureAwait(false))
            return true;

        await _error.WriteLineAsync(StopFailedCopy).ConfigureAwait(false);
        return false;
    }
}
