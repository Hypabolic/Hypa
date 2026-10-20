namespace Hypa.Continuity.Tests.Support;

/// <summary>
/// A gap of a lab latency model: the key pace of a bench, the chunk gaps of
/// a scripted pane, or a warm-up of a probe. The gap is an input of the
/// model. It is not a wait for a result. A timer of the clock completes the
/// gap at its due time.
/// </summary>
internal static class Pacing
{
    public static Task GapAsync(TimeSpan gap, CancellationToken cancellationToken = default) =>
        GapAsync(gap, TimeProvider.System, cancellationToken);

    public static async Task GapAsync(TimeSpan gap, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            await Task.FromCanceled(cancellationToken).ConfigureAwait(false);
        if (gap <= TimeSpan.Zero)
            return;
        var due = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = clock.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            due,
            gap,
            Timeout.InfiniteTimeSpan);
        using var stop = cancellationToken.Register(() => due.TrySetCanceled(cancellationToken));
        await due.Task.ConfigureAwait(false);
    }
}
