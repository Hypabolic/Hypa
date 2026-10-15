using Xunit;

namespace Hypa.AgentRuntime.Tests.Support;

/// <summary>
/// Waits for a state that no event announces. The limit is a hang guard
/// only: a test outcome never depends on it.
/// </summary>
internal static class Poll
{
    public static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    public static async Task UntilAsync(Func<bool> ready, string what)
    {
        var guard = new CancellationTokenSource(HangGuard);
        try
        {
            while (!ready())
                await Task.Delay(5, guard.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            Assert.Fail("hang guard expired waiting for " + what);
        }
        finally
        {
            guard.Dispose();
        }
    }

    /// <summary>Await a barrier task. A stalled barrier fails the test after the hang guard.</summary>
    public static async Task GuardAsync(Task barrier, string what)
    {
        try
        {
            await barrier.WaitAsync(HangGuard).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Assert.Fail("hang guard expired waiting for " + what);
        }
    }

    public static Task GuardAsync(ValueTask barrier, string what) =>
        GuardAsync(barrier.AsTask(), what);

    public static async Task<T> UntilAsync<T>(Func<T?> read, string what)
        where T : class
    {
        T? value = null;
        await UntilAsync(() => (value = read()) is not null, what).ConfigureAwait(false);
        return value!;
    }
}
