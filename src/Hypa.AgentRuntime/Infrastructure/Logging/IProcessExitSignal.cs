namespace Hypa.AgentRuntime.Infrastructure.Logging;

/// <summary>
/// Tells a subscriber that the process is about to end. A queued log sink
/// uses it to flush its last lines.
/// </summary>
public interface IProcessExitSignal
{
    /// <summary>
    /// Calls <paramref name="onExit"/> when the process ends. Dispose the
    /// result to remove the callback.
    /// </summary>
    IDisposable Register(Action onExit);
}

/// <summary>
/// Production signal. <see cref="AppDomain.ProcessExit"/> covers a normal
/// exit and SIGTERM. <see cref="AppDomain.UnhandledException"/> covers a
/// crash, because the runtime does not raise <c>ProcessExit</c> for an
/// unhandled exception.
/// </summary>
public sealed class AppDomainProcessExitSignal : IProcessExitSignal
{
    public IDisposable Register(Action onExit)
    {
        ArgumentNullException.ThrowIfNull(onExit);
        EventHandler exit = (_, _) => onExit();
        UnhandledExceptionEventHandler crash = (_, _) => onExit();
        AppDomain.CurrentDomain.ProcessExit += exit;
        AppDomain.CurrentDomain.UnhandledException += crash;
        return new Registration(exit, crash);
    }

    private sealed class Registration(EventHandler exit, UnhandledExceptionEventHandler crash) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            AppDomain.CurrentDomain.ProcessExit -= exit;
            AppDomain.CurrentDomain.UnhandledException -= crash;
        }
    }
}
