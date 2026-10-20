using Xunit;

namespace Hypa.Continuity.Tests.Support;

/// <summary>
/// Waits for a state or for a task. The limit is a hang guard only: a test
/// outcome never depends on it.
/// </summary>
internal static class Poll
{
    public static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Waits for a state that no event announces. A dedicated thread checks
    /// the state with <see cref="SpinWait"/>, so the wait uses no clock delay.
    /// </summary>
    public static async Task UntilAsync(Func<bool> ready, string what)
    {
        if (ready())
            return;
        var met = await Task.Factory.StartNew(
                () => SpinWait.SpinUntil(ready, HangGuard),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .ConfigureAwait(false);
        if (!met)
            Assert.Fail("hang guard expired waiting for " + what);
    }

    /// <summary>Awaits a barrier task. A stalled barrier fails the test after the hang guard.</summary>
    public static async Task GuardAsync(Task barrier, string what)
    {
        try
        {
            await barrier.WaitAsync(HangGuard).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!barrier.IsCompleted)
        {
            Assert.Fail("hang guard expired waiting for " + what);
        }
    }

    public static Task GuardAsync(ValueTask barrier, string what) =>
        GuardAsync(barrier.AsTask(), what);

    /// <summary>
    /// Waits until the task ends, with any outcome. Use it for a background
    /// loop at shutdown. A loop that does not end fails the test after the
    /// hang guard.
    /// </summary>
    public static async Task EndAsync(Task task, string what)
    {
        try
        {
            await task.WaitAsync(HangGuard).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            Assert.Fail("hang guard expired waiting for " + what);
        }
        catch (Exception) when (task.IsCompleted)
        {
            // The outcome of the loop is not the result of this wait.
        }
    }
}
