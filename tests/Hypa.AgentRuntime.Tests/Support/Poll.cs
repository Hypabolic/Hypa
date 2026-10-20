using Xunit;

namespace Hypa.AgentRuntime.Tests.Support;

/// <summary>
/// Waits for a state that no event announces. The limit is a hang guard
/// only: a test outcome never depends on it.
/// </summary>
internal static class Poll
{
    public static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    public static Task UntilAsync(Func<bool> ready, string what) =>
        RunAsync(() => ValueTask.FromResult(ready()), () => what);

    /// <summary>
    /// The same wait. <paramref name="what"/> gives the failure text when the
    /// hang guard expires, so the text can show the last state.
    /// </summary>
    public static Task UntilAsync(Func<bool> ready, Func<string> what) =>
        RunAsync(() => ValueTask.FromResult(ready()), what);

    /// <summary>
    /// Runs one step until the step returns true. A step can paint or flush
    /// before it checks the state.
    /// </summary>
    public static Task UntilAsync(Func<Task<bool>> step, string what) =>
        RunAsync(() => new ValueTask<bool>(step()), () => what);

    /// <inheritdoc cref="UntilAsync(Func{Task{bool}}, string)"/>
    public static Task UntilAsync(Func<Task<bool>> step, Func<string> what) =>
        RunAsync(() => new ValueTask<bool>(step()), what);

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

    private static async Task RunAsync(Func<ValueTask<bool>> ready, Func<string> what)
    {
        var guard = new CancellationTokenSource(HangGuard);
        try
        {
            while (!await ready().AsTask().WaitAsync(guard.Token).ConfigureAwait(false))
                await Task.Delay(5, guard.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (guard.IsCancellationRequested)
        {
            Assert.Fail("hang guard expired waiting for " + what());
        }
        finally
        {
            guard.Dispose();
        }
    }
}
