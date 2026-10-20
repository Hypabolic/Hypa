namespace Hypa.AgentRuntime.Tests.Support;

/// <summary>
/// The pace of a lab model, for example a pane that writes a line every
/// 4 ms. The time is an input of the model. It is not a wait for a result.
/// A timer of the clock completes the task at the due time.
/// </summary>
internal static class Pace
{
    public static Task For(TimeSpan due, TimeProvider? clock = null)
    {
        if (due <= TimeSpan.Zero)
            return Task.CompletedTask;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = (clock ?? TimeProvider.System).CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            done,
            due,
            Timeout.InfiniteTimeSpan);

        // The task holds the timer until the timer fires.
        _ = done.Task.ContinueWith(
            static (_, state) => ((ITimer)state!).Dispose(),
            timer,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return done.Task;
    }

    public static Task For(int milliseconds, TimeProvider? clock = null) =>
        For(TimeSpan.FromMilliseconds(milliseconds), clock);
}
