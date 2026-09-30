using Hypa.AgentRuntime.Application;
using Hypa.ControlPlane;
using Xunit;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Live counters stay off the journal high-water when they share a subscription
/// with journaled events.
/// </summary>
public sealed class LiveSequenceDomainTests
{
    [Fact]
    public void Live_counters_stay_off_the_journal_cursor()
    {
        var sub = OpenSubscription();
        Assert.Equal(0, sub.LastEmittedSeq);

        var config = ConfigChanged(2);
        var undeliverable = Undeliverable("p1", 3);
        Assert.True(sub.TryEnqueueLiveBatch([config, undeliverable], out _));
        var first = sub.DrainLiveQueue();
        Assert.Equal(2, first.Count);
        foreach (var record in first)
            sub.MarkEmitted(record);

        Assert.Equal(0, sub.LastEmittedSeq);
        Assert.False(sub.HasAlreadyEmitted(Journal(1)));

        var journal = Journal(1);
        var shown = Shown("p1", 2);
        Assert.True(sub.TryEnqueueLiveBatch([journal, shown], out _));
        var second = sub.DrainLiveQueue();
        Assert.Contains(second, record => record.Type == ProtocolEventTypes.PaneLifecycle && record.Seq == 1);
        Assert.Contains(second, record => record.Type == ProtocolEventTypes.NotificationShown && record.Seq == 2);
        foreach (var record in second)
            sub.MarkEmitted(record);

        Assert.Equal(2, sub.LastEmittedSeq);

        sub.MarkEmitted(Journal(20));
        Assert.Equal(20, sub.LastEmittedSeq);
        Assert.False(sub.HasAlreadyEmitted(ConfigChanged(4)));
        Assert.False(sub.HasAlreadyEmitted(Undeliverable("p1", 4)));
        Assert.False(sub.HasAlreadyEmitted(Undeliverable("p2", 1)));
        Assert.True(sub.HasAlreadyEmitted(ConfigChanged(2)));
        Assert.True(sub.HasAlreadyEmitted(Undeliverable("p1", 3)));
        Assert.True(sub.HasAlreadyEmitted(Shown("p1", 15)));

        var laterJournal = Journal(21);
        var laterConfig = ConfigChanged(5);
        var laterNotice = Undeliverable("p2", 1);
        var laterStill = Journal(22);
        Assert.True(sub.TryEnqueueLiveBatch(
            [laterJournal, laterConfig, laterNotice, laterStill],
            out _));
        var third = sub.DrainLiveQueue();
        Assert.Equal(4, third.Count);
        foreach (var record in third)
            sub.MarkEmitted(record);

        Assert.Equal(22, sub.LastEmittedSeq);
        Assert.True(sub.HasAlreadyEmitted(ConfigChanged(5)));
        Assert.False(sub.HasAlreadyEmitted(Journal(23)));
    }

    [Fact]
    public void Incomplete_notifications_stay_on_the_journal_cursor()
    {
        var sub = OpenSubscription();
        sub.MarkEmitted(Journal(20));
        Assert.Equal(20, sub.LastEmittedSeq);

        Assert.True(sub.HasAlreadyEmitted(Notice(MissingReasonJson(), 15)));
        Assert.True(sub.HasAlreadyEmitted(Notice(MissingPaneJson(), 15)));
        Assert.True(sub.HasAlreadyEmitted(Notice(EmptyPaneJson(), 15)));

        Deliver(sub, Notice(MissingReasonJson(), 21));
        Assert.Equal(21, sub.LastEmittedSeq);
        Deliver(sub, Notice(MissingPaneJson(), 22));
        Assert.Equal(22, sub.LastEmittedSeq);
        Deliver(sub, Notice(EmptyPaneJson(), 23));
        Assert.Equal(23, sub.LastEmittedSeq);

        Assert.True(sub.HasAlreadyEmitted(Notice(MissingReasonJson(), 21)));
        Assert.True(sub.HasAlreadyEmitted(Notice(MissingPaneJson(), 22)));
        Assert.True(sub.HasAlreadyEmitted(Notice(EmptyPaneJson(), 23)));
        Assert.True(sub.TryEnqueueLiveBatch([Notice(MissingReasonJson(), 21)], out _));
        Assert.Empty(sub.DrainLiveQueue());
        Assert.False(sub.HasAlreadyEmitted(Journal(24)));

        var live = Undeliverable("p1", 1);
        Assert.False(sub.HasAlreadyEmitted(live));
        Deliver(sub, live);
        Assert.Equal(23, sub.LastEmittedSeq);
        Assert.True(sub.HasAlreadyEmitted(live));
    }

    [Fact]
    public async Task Overlapped_config_changes_are_not_dropped()
    {
        var sub = OpenSubscription();
        var gate = new SemaphoreSlim(1, 1);
        var next = 0L;
        var published = new List<long>();
        var firstAllocated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var first = ControlPlaneService.EmitInAllocationOrderAsync(
                gate,
                () => Interlocked.Increment(ref next),
                async (seq, _) =>
                {
                    firstAllocated.TrySetResult();
                    await releaseFirst.Task.ConfigureAwait(false);
                    Assert.False(sub.HasAlreadyEmitted(ConfigChanged(seq)));
                    Deliver(sub, ConfigChanged(seq));
                    lock (published)
                        published.Add(seq);
                },
                CancellationToken.None);

            await firstAllocated.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var second = ControlPlaneService.EmitInAllocationOrderAsync(
                gate,
                () => Interlocked.Increment(ref next),
                (seq, _) =>
                {
                    Assert.False(sub.HasAlreadyEmitted(ConfigChanged(seq)));
                    Deliver(sub, ConfigChanged(seq));
                    lock (published)
                        published.Add(seq);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            await Task.Delay(50);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref next));

            releaseFirst.TrySetResult();
            var both = Task.WhenAll(first, second);
            var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(both, finished);
            await both;
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        lock (published)
            Assert.Equal(new long[] { 1, 2 }, published);
        Assert.True(sub.HasAlreadyEmitted(ConfigChanged(1)));
        Assert.True(sub.HasAlreadyEmitted(ConfigChanged(2)));
    }

    [Fact]
    public async Task Overlapped_input_undeliverable_notices_are_not_dropped()
    {
        var reversed = OpenSubscription();
        Deliver(reversed, Undeliverable("p1", 2));
        Assert.True(reversed.HasAlreadyEmitted(Undeliverable("p1", 1)));

        var sub = OpenSubscription();
        var gate = new object();
        var next = 0L;
        var published = new List<long>();
        using var firstAllocated = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();

        var first = Task.Run(() => ControlPlaneService.EmitInAllocationOrder(
            gate,
            () => Interlocked.Increment(ref next),
            seq =>
            {
                firstAllocated.Set();
                releaseFirst.Wait(TimeSpan.FromSeconds(5));
                Assert.False(sub.HasAlreadyEmitted(Undeliverable("p1", seq)));
                Deliver(sub, Undeliverable("p1", seq));
                lock (published)
                    published.Add(seq);
            }));

        Assert.True(firstAllocated.Wait(TimeSpan.FromSeconds(2)));

        var second = Task.Run(() => ControlPlaneService.EmitInAllocationOrder(
            gate,
            () => Interlocked.Increment(ref next),
            seq =>
            {
                Assert.False(sub.HasAlreadyEmitted(Undeliverable("p1", seq)));
                Deliver(sub, Undeliverable("p1", seq));
                lock (published)
                    published.Add(seq);
            }));

        try
        {
            await Task.Delay(50);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref next));
        }
        finally
        {
            releaseFirst.Set();
        }

        var both = Task.WhenAll(first, second);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(both, finished);
        await both;

        lock (published)
            Assert.Equal(new long[] { 1, 2 }, published);
        Assert.True(sub.HasAlreadyEmitted(Undeliverable("p1", 1)));
        Assert.True(sub.HasAlreadyEmitted(Undeliverable("p1", 2)));
    }

    private static EventSubscription OpenSubscription()
    {
        var sub = new EventSubscription(new EventSubscriptionRequest
        {
            SubscriptionId = "sub_live",
            ConnectionId = "conn_live",
            FromSeq = 0,
            Classes = new HashSet<EventClass> { EventClass.Control, EventClass.Lifecycle },
            ReplayBudget = 0,
            Live = true,
            Sink = new QuietSink(),
        });
        sub.EnableLive();
        return sub;
    }

    private static void Deliver(EventSubscription sub, RuntimeEventRecord record)
    {
        Assert.True(sub.TryEnqueueLiveBatch([record], out _));
        var drained = sub.DrainLiveQueue();
        var delivered = Assert.Single(drained);
        Assert.Equal(record.Seq, delivered.Seq);
        Assert.Equal(record.Type, delivered.Type);
        sub.MarkEmitted(delivered);
    }

    private static RuntimeEventRecord Journal(long seq) =>
        new()
        {
            Seq = seq,
            Class = EventClass.Lifecycle,
            Reliability = EventReliability.Reliable,
            Type = ProtocolEventTypes.PaneLifecycle,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = """{"pane_id":"p1","state":"running"}""",
        };

    private static RuntimeEventRecord ConfigChanged(long seq) =>
        new()
        {
            Seq = seq,
            Class = EventClass.Lifecycle,
            Reliability = EventReliability.Reliable,
            Type = ProtocolEventTypes.ConfigChanged,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = RuntimeEventPayloadJson.WriteConfigChanged("plugin", "theme", "ink"),
        };

    private static RuntimeEventRecord Undeliverable(string paneId, long seq) =>
        new()
        {
            Seq = seq,
            Class = EventClass.Control,
            Reliability = EventReliability.Reliable,
            Type = ProtocolEventTypes.NotificationShown,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = RuntimeEventPayloadJson.WriteNotificationShown(
                "Input undeliverable",
                "1 bytes",
                "pane.input",
                "none",
                paneId,
                "input_undeliverable"),
        };

    private static RuntimeEventRecord Notice(string payloadJson, long seq) =>
        new()
        {
            Seq = seq,
            Class = EventClass.Control,
            Reliability = EventReliability.Reliable,
            Type = ProtocolEventTypes.NotificationShown,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = payloadJson,
        };

    private static string MissingReasonJson() =>
        """{"title":"Saved","body":null,"source":"app","sound":"none","pane_id":"p1"}""";

    private static string MissingPaneJson() =>
        """{"title":"Input undeliverable","body":"1 bytes","source":"pane.input","sound":"none","reason":"input_undeliverable"}""";

    private static string EmptyPaneJson() =>
        RuntimeEventPayloadJson.WriteNotificationShown(
            "Input undeliverable",
            "1 bytes",
            "pane.input",
            "none",
            "",
            "input_undeliverable");

    private static RuntimeEventRecord Shown(string paneId, long seq) =>
        new()
        {
            Seq = seq,
            Class = EventClass.Control,
            Reliability = EventReliability.Reliable,
            Type = ProtocolEventTypes.NotificationShown,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = RuntimeEventPayloadJson.WriteNotificationShown(
                "Saved",
                null,
                "app",
                "none",
                paneId,
                "shown"),
        };

    private sealed class QuietSink : IEventPushSink
    {
        public string ConnectionId => "conn_live";

        public Task WriteLineAsync(string jsonLine, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
