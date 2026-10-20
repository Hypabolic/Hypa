using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Tests.Support;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Hub delivery waits. A shutdown token ends the wait as success.
/// A test hang guard stays outside <see cref="IEventSubscriptionHub.WhenQueuedDeliveredAsync"/>.
/// </summary>
public sealed class HubDeliveryWaitTests
{
    [Fact]
    public async Task Shutdown_cancel_ends_a_blocked_delivery_wait()
    {
        await using var hub = new EventSubscriptionHub();
        var sink = new HoldSink();
        Register(hub, sink);
        Assert.True(hub.PostLive(RenderRecord()));
        await Poll.GuardAsync(sink.Entered, "the blocked write");

        using var shutdown = new CancellationTokenSource();
        var wait = hub.WhenQueuedDeliveredOrShutdownAsync(shutdown.Token).AsTask();
        Assert.False(wait.IsCompleted);
        shutdown.Cancel();
        await Poll.GuardAsync(wait, "shutdown delivery wait");
        sink.Release();
    }

    [Fact]
    public async Task Blocked_delivery_wait_stays_open_until_the_write_finishes()
    {
        await using var hub = new EventSubscriptionHub();
        var sink = new HoldSink();
        Register(hub, sink);
        Assert.True(hub.PostLive(RenderRecord()));
        await Poll.GuardAsync(sink.Entered, "the blocked write");

        var wait = hub.WhenQueuedDeliveredAsync().AsTask();
        Assert.False(wait.IsCompleted);
        sink.Release();
        await Poll.GuardAsync(wait, "hub delivery");
    }

    private static void Register(EventSubscriptionHub hub, HoldSink sink)
    {
        var sub = hub.Register(new EventSubscriptionRequest
        {
            SubscriptionId = "sub_hold",
            ConnectionId = sink.ConnectionId,
            FromSeq = 0,
            Classes = new HashSet<EventClass> { EventClass.Render },
            ReplayBudget = 0,
            Live = true,
            Sink = sink,
            HasClassTokens = true,
        });
        sub.AttachPane("p1");
        sub.EnableLive();
    }

    private static RuntimeEventRecord RenderRecord() => new()
    {
        Seq = 1,
        Class = EventClass.Render,
        Reliability = EventReliability.Render,
        Type = ProtocolEventTypes.TerminalRender,
        OccurredAt = DateTimeOffset.UtcNow,
        PayloadJson = """{"pane_id":"p1"}""",
        PaneKey = "p1",
    };

    private sealed class HoldSink : IEventPushSink
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ConnectionId => "c_hold";

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async Task WriteLineAsync(string jsonLine, CancellationToken ct = default)
        {
            _ = jsonLine;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
