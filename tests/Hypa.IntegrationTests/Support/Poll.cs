using System.Diagnostics;
using Xunit;

namespace Hypa.IntegrationTests;

/// <summary>
/// Waits for a state that no event announces. The limit is
/// <see cref="HangGuard"/>: it only stops a hung run, and a test outcome
/// never depends on it.
/// </summary>
internal static class Poll
{
    public static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    // After this many spins, each check sleeps for one scheduler tick. A
    // long wait then does not take a full core from the other tests.
    private const int SpinsBeforeSleep = 10;

    public static async Task UntilAsync(Func<bool> ready, string what)
    {
        if (ready())
            return;
        // The check loop has its own thread. It does not take a pool thread
        // from the code under test, which runs its continuations there.
        var reached = await Task.Factory.StartNew(
                () => SpinUntil(ready),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .ConfigureAwait(false);
        Assert.True(reached, "hang guard expired waiting for " + what);
    }

    private static bool SpinUntil(Func<bool> ready)
    {
        var start = Stopwatch.GetTimestamp();
        var spinner = new SpinWait();
        while (!ready())
        {
            if (Stopwatch.GetElapsedTime(start) >= HangGuard)
                return false;
            spinner.SpinOnce(SpinsBeforeSleep);
        }

        return true;
    }
}
