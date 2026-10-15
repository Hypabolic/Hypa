using Xunit;

namespace Hypa.UnitTests;

/// <summary>
/// The limit for a test wait. A test waits for a signal, never for a clock.
/// This limit only stops a hung run. No outcome depends on it.
/// </summary>
internal static class TestWait
{
    internal static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Returns when the thread blocks on a lock, or when the task ends. Use it before
    /// an assertion that the task is still running: a call that did not block
    /// ends the task, and the assertion then fails. Any wait of the thread
    /// passes this check, also a short wait on another lock. Use a busy fact
    /// of the gate when the product has one.
    /// </summary>
    internal static void UntilBlockedOrDone(Func<Thread?> thread, Task task) =>
        UntilBlockedOrDone(thread, () => task.IsCompleted);

    internal static void UntilBlockedOrDone(Func<Thread?> thread, Func<bool> done) =>
        Assert.True(
            SpinWait.SpinUntil(
                () => done() || (thread() is { } t && (t.ThreadState & ThreadState.WaitSleepJoin) != 0),
                HangGuard));
}
