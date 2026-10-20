using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Infrastructure.Logging;
using Hypa.AgentRuntime.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The queued process-log sink keeps file I/O off the caller thread. A writer
/// that stalls does not delay a caller, a full queue counts its drops, the
/// line order is kept, and exit paths drain the queue.
/// </summary>
public sealed class QueuedProcessLogSinkTests : IDisposable
{
    private static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(250);

    private readonly ITestOutputHelper _output;
    private readonly string _dir;

    public QueuedProcessLogSinkTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "hypa-qlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void A_writer_stalled_for_250_ms_does_not_delay_the_caller()
    {
        var clock = new ManualTimeProvider();
        var inner = new GatedSink();
        using var sink = new QueuedProcessLogSink(inner, timeProvider: clock);

        sink.Write(Line(0));
        Assert.True(inner.Entered.Wait(Poll.HangGuard), "the writer did not take the first line");
        var stall = Stopwatch.StartNew();

        const int calls = 200;
        var durations = new double[calls];
        for (var index = 0; index < calls; index++)
        {
            var started = Stopwatch.GetTimestamp();
            sink.Write(Line(index + 1));
            durations[index] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }

        var callsDoneAt = stall.Elapsed;

        // Each caller returned while the writer was still blocked in the inner sink.
        Assert.False(inner.Gate.IsSet);
        Assert.Empty(inner.Records);

        // The clock of the sink moves through the full stall, and the writer
        // stays blocked. The times are printed, not asserted: the causal
        // check above is the proof.
        clock.Advance(Stall);
        Assert.Empty(inner.Records);
        Array.Sort(durations);
        var p50 = durations[calls / 2];
        var max = durations[^1];
        _output.WriteLine(
            $"enqueue p50 {p50:F1} us, max {max:F1} us, {calls} calls in {callsDoneAt.TotalMilliseconds:F2} ms during a {Stall.TotalMilliseconds} ms stall");

        inner.Gate.Set();
        Assert.True(sink.Flush(Poll.HangGuard));
        Assert.Equal(Enumerable.Range(0, calls + 1).Select(n => (long)n), inner.Records.Select(r => r.Seq!.Value));
    }

    [Fact]
    public void A_full_queue_counts_the_drops_and_writes_one_drop_record_at_the_gap()
    {
        const int capacity = 8;
        const int burst = 20;
        var inner = new GatedSink();
        using var sink = new QueuedProcessLogSink(inner, capacity);

        sink.Write(Line(0));
        Assert.True(inner.Entered.Wait(Poll.HangGuard));
        for (var index = 1; index <= burst; index++)
            sink.Write(Line(index));

        Assert.Equal(burst - capacity, sink.DroppedCount);

        inner.Gate.Set();
        Assert.True(sink.Flush(Poll.HangGuard));
        sink.Write(Line(100));
        Assert.True(sink.Flush(Poll.HangGuard));

        var records = inner.Records;
        var lines = records.Where(r => r.Event == TestEvent).Select(r => r.Seq!.Value).ToArray();
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 100], lines);
        var drop = Assert.Single(records, r => r.Event == ProcessLogEvents.LogSinkDropped);
        Assert.Equal(burst - capacity, drop.DropCount);
        Assert.Equal(capacity, drop.QueueDepth);
        Assert.Equal(ProcessLogLevel.Warning, drop.Level);
        Assert.Equal(ProcessLogEvents.SubsystemLog, drop.Subsystem);

        // The drop record sits where the lines were lost: after line 8, before line 100.
        var order = records.Select(r => r.Event == TestEvent ? r.Seq!.Value.ToString() : "drop").ToArray();
        Assert.Equal(["0", "1", "2", "3", "4", "5", "6", "7", "8", "drop", "100"], order);
        Assert.Equal(burst + 2, lines.Length + drop.DropCount!.Value);
    }

    [Fact]
    public async Task Lines_from_many_threads_keep_the_order_of_each_thread()
    {
        var inner = new GatedSink();
        inner.Gate.Set();
        using var sink = new QueuedProcessLogSink(inner, capacity: 100_000);
        const int threads = 4;
        const int perThread = 2_000;

        await Task.WhenAll(Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
        {
            for (var index = 0; index < perThread; index++)
                sink.Write(Line(thread * 1_000_000 + index));
        })));
        Assert.True(sink.Flush(Poll.HangGuard));

        var seqs = inner.Records.Select(r => r.Seq!.Value).ToArray();
        Assert.Equal(threads * perThread, seqs.Length);
        for (var thread = 0; thread < threads; thread++)
        {
            var mine = seqs.Where(s => s / 1_000_000 == thread).ToArray();
            Assert.Equal(Enumerable.Range(thread * 1_000_000, perThread).Select(n => (long)n), mine);
        }
    }

    [Fact]
    public void A_file_sink_behind_the_queue_writes_the_lines_in_order()
    {
        var path = Path.Combine(_dir, "attach.1.log");
        var sink = new QueuedProcessLogSink(new JsonLinesFileLogSink(path, ProcessLogLevel.Debug));
        for (var index = 0; index < 1_000; index++)
            sink.Write(Line(index));
        sink.Dispose();

        var seqs = File.ReadAllLines(path)
            .Select(line =>
            {
                using var doc = JsonDocument.Parse(line);
                return doc.RootElement.GetProperty("seq").GetInt64();
            })
            .ToArray();
        Assert.Equal(Enumerable.Range(0, 1_000).Select(n => (long)n), seqs);
    }

    [Fact]
    public async Task Dispose_drains_the_queue_then_disposes_the_inner_sink()
    {
        var clock = new ManualTimeProvider();
        var inner = new GatedSink();
        var sink = new QueuedProcessLogSink(inner, timeProvider: clock, drainTimeout: Poll.HangGuard);
        sink.Write(Line(0));
        Assert.True(inner.Entered.Wait(Poll.HangGuard));
        for (var index = 1; index <= 50; index++)
            sink.Write(Line(index));

        // The drain deadline is armed, so Dispose waits for the writer. The
        // clock does not move, so only the writer can end that wait.
        var dispose = Task.Run(sink.Dispose);
        await Poll.UntilAsync(() => clock.NextDue is not null, "the drain deadline");
        Assert.False(dispose.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref inner.DisposeCount));

        inner.Gate.Set();
        await dispose.WaitAsync(Poll.HangGuard);

        Assert.Equal(Enumerable.Range(0, 51).Select(n => (long)n), inner.Records.Select(r => r.Seq!.Value));
        Assert.Equal(1, Volatile.Read(ref inner.DisposeCount));
        Assert.False(sink.IsEnabled(ProcessLogLevel.Warning));
        sink.Write(Line(99));
        sink.Dispose();
        Assert.Equal(51, inner.Records.Count);
    }

    [Fact]
    public async Task Dispose_stops_at_the_drain_timeout_and_the_exit_signal_flushes_the_rest()
    {
        var drain = TimeSpan.FromMilliseconds(50);
        var clock = new ManualTimeProvider();
        var inner = new GatedSink();
        var exit = new FakeExitSignal();
        var sink = new QueuedProcessLogSink(inner, timeProvider: clock, exitSignal: exit, drainTimeout: drain);
        try
        {
            sink.Write(Line(0));
            Assert.True(inner.Entered.Wait(Poll.HangGuard));
            sink.Write(Line(1));

            // Dispose waits for the writer until the clock reaches the drain timeout.
            var dispose = Task.Run(sink.Dispose);
            await Poll.UntilAsync(() => clock.NextDue is not null, "the drain deadline");
            Assert.False(dispose.IsCompleted);
            clock.Advance(drain);
            await dispose.WaitAsync(Poll.HangGuard);
            Assert.False(inner.Gate.IsSet);
            Assert.Equal(0, Volatile.Read(ref inner.DisposeCount));

            // The writer is not done, so the exit callback stays. The clock does
            // not move again, so the exit flush waits for the writer.
            Assert.Equal(1, exit.Registered);
            Assert.Null(clock.NextDue);
            var exiting = Task.Run(exit.Raise);
            await Poll.UntilAsync(() => clock.NextDue is not null, "the exit flush deadline");
            Assert.False(exiting.IsCompleted);
            inner.Gate.Set();
            await exiting.WaitAsync(Poll.HangGuard);
            Assert.Equal([0L, 1L], inner.Records.Select(r => r.Seq!.Value));
        }
        finally
        {
            inner.Gate.Set();
        }

        // The writer closes the inner sink and then removes the callback.
        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref inner.DisposeCount) == 1 && exit.Registered == 0,
            Poll.HangGuard));
    }

    [Fact]
    public async Task The_exit_flush_gives_up_at_its_timeout_when_the_writer_is_stalled()
    {
        var clock = new ManualTimeProvider();
        var inner = new GatedSink();
        var exit = new FakeExitSignal();
        var sink = new QueuedProcessLogSink(inner, timeProvider: clock, exitSignal: exit, drainTimeout: TimeSpan.Zero);
        try
        {
            sink.Write(Line(0));
            Assert.True(inner.Entered.Wait(Poll.HangGuard));
            sink.Write(Line(1));

            var exiting = Task.Run(exit.Raise);
            await Poll.UntilAsync(() => clock.NextDue is not null, "the exit flush deadline");
            Assert.False(exiting.IsCompleted);
            clock.Advance(QueuedProcessLogSink.ExitFlushTimeout);

            // The exit path ends while the writer is still blocked.
            await exiting.WaitAsync(Poll.HangGuard);
            Assert.False(inner.Gate.IsSet);
            Assert.Empty(inner.Records);
        }
        finally
        {
            inner.Gate.Set();
            sink.Dispose();
        }
    }

    [Fact]
    public void Flush_returns_false_when_the_inner_sink_is_disabled_by_a_write()
    {
        var inner = new FailingSink();
        using var sink = new QueuedProcessLogSink(inner);
        sink.Write(Line(0));
        Assert.False(sink.Flush(Poll.HangGuard));
        Assert.True(sink.Disabled);
        Assert.False(sink.IsEnabled(ProcessLogLevel.Warning));
    }

    [Fact]
    public async Task Flush_returns_false_while_the_writer_is_stalled_and_true_after()
    {
        var clock = new ManualTimeProvider();
        var inner = new GatedSink();
        using var sink = new QueuedProcessLogSink(inner, timeProvider: clock);
        try
        {
            Assert.True(sink.Flush(TimeSpan.Zero));

            sink.Write(Line(0));
            Assert.True(inner.Entered.Wait(Poll.HangGuard));
            Assert.False(sink.Flush(TimeSpan.Zero));

            var timeout = TimeSpan.FromMilliseconds(30);
            var flush = Task.Run(() => sink.Flush(timeout));
            await Poll.UntilAsync(() => clock.NextDue is not null, "the flush deadline");
            clock.Advance(timeout);
            Assert.False(await flush.WaitAsync(Poll.HangGuard));
        }
        finally
        {
            inner.Gate.Set();
        }

        Assert.True(sink.Flush(Poll.HangGuard));
        Assert.Single(inner.Records);
    }

    [Fact]
    public void Flush_returns_false_after_the_inner_sink_threw()
    {
        var inner = new ThrowingSink { ThrowOn = 1 };
        using var sink = new QueuedProcessLogSink(inner);
        sink.Write(Line(0));
        Assert.True(sink.Flush(Poll.HangGuard));

        sink.Write(Line(1));
        sink.Write(Line(2));
        Assert.False(sink.Flush(Poll.HangGuard));

        // The writer goes on, and only the line that threw is lost.
        sink.Write(Line(3));
        Assert.False(sink.Flush(Poll.HangGuard));
        Assert.Equal([0L, 2L, 3L], inner.Records.Select(r => r.Seq!.Value));
        Assert.True(sink.IsEnabled(ProcessLogLevel.Information));
    }

    [Fact]
    public void Flush_returns_false_after_the_batch_sink_lost_a_record()
    {
        var inner = new LossyBatchSink { LoseOn = 1 };
        using var sink = new QueuedProcessLogSink(inner);
        sink.Write(Line(0));
        Assert.True(sink.Flush(Poll.HangGuard));

        sink.Write(Line(1));
        sink.Write(Line(2));
        Assert.False(sink.Flush(Poll.HangGuard));
        Assert.Equal([0L, 2L], inner.Records.Select(r => r.Seq!.Value));
        Assert.True(sink.IsEnabled(ProcessLogLevel.Information));
    }

    [Fact]
    public void The_process_exit_signal_writes_the_last_lines_to_the_file()
    {
        var path = Path.Combine(_dir, "mux.log");
        var exit = new FakeExitSignal();
        var sink = new QueuedProcessLogSink(
            new JsonLinesFileLogSink(path, ProcessLogLevel.Information),
            exitSignal: exit);
        try
        {
            for (var index = 0; index < 10; index++)
                sink.Write(Line(index));

            exit.Raise();

            var lines = File.ReadAllLines(path);
            Assert.Equal(10, lines.Length);
            using var last = JsonDocument.Parse(lines[^1]);
            Assert.Equal(9, last.RootElement.GetProperty("seq").GetInt64());
        }
        finally
        {
            sink.Dispose();
        }

        Assert.Equal(0, exit.Registered);
    }

    [Fact]
    public void Lines_below_the_filter_are_not_queued()
    {
        var inner = new GatedSink { Filter = ProcessLogLevel.Information };
        inner.Gate.Set();
        using var sink = new QueuedProcessLogSink(inner);
        Assert.False(sink.IsEnabled(ProcessLogLevel.Debug));
        sink.Write(Line(0) with { Level = ProcessLogLevel.Debug });
        sink.Write(Line(1));
        Assert.True(sink.Flush(Poll.HangGuard));
        Assert.Equal([1L], inner.Records.Select(r => r.Seq!.Value));
    }

    [Fact]
    public async Task The_file_sink_level_check_does_not_wait_for_the_file_lock()
    {
        var path = Path.Combine(_dir, "lock.log");
        using var sink = new JsonLinesFileLogSink(path, ProcessLogLevel.Information);
        var gate = typeof(JsonLinesFileLogSink)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(sink)!;
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                held.Set();
                release.Wait(Poll.HangGuard);
            }
        });
        holder.Start();
        try
        {
            Assert.True(held.Wait(Poll.HangGuard));
            var check = Task.Run(() => (sink.IsEnabled(ProcessLogLevel.Information), sink.Disabled));
            var result = await check.WaitAsync(Poll.HangGuard);
            // The check finished while another thread still held the file lock.
            Assert.False(release.IsSet);
            Assert.True(result.Item1);
            Assert.False(result.Item2);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    private const string TestEvent = "test.line";

    private static ProcessLogRecord Line(int seq) =>
        new()
        {
            Event = TestEvent,
            Subsystem = "test",
            Outcome = ProcessLogEvents.OutcomeOk,
            Ts = DateTimeOffset.UnixEpoch,
            Pid = 1,
            Level = ProcessLogLevel.Information,
            Seq = seq,
        };

    private sealed class GatedSink : IProcessLogSink, IDisposable
    {
        private readonly ConcurrentQueue<ProcessLogRecord> _records = new();

        public int DisposeCount;

        public ManualResetEventSlim Gate { get; } = new(false);

        public ManualResetEventSlim Entered { get; } = new(false);

        public ProcessLogLevel Filter { get; init; } = ProcessLogLevel.Debug;

        public IReadOnlyList<ProcessLogRecord> Records => _records.ToArray();

        public bool Disabled => false;

        public string? Path => null;

        public bool IsEnabled(ProcessLogLevel level) => ProcessLogFilter.Allows(Filter, level);

        public void Write(ProcessLogRecord record)
        {
            Entered.Set();
            Gate.Wait(Poll.HangGuard);
            _records.Enqueue(record);
        }

        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    /// <summary>Throws on one sequence number and stays enabled.</summary>
    private sealed class ThrowingSink : IProcessLogSink
    {
        private readonly ConcurrentQueue<ProcessLogRecord> _records = new();

        public long ThrowOn { get; init; }

        public IReadOnlyList<ProcessLogRecord> Records => _records.ToArray();

        public bool Disabled => false;

        public string? Path => null;

        public bool IsEnabled(ProcessLogLevel level) => true;

        public void Write(ProcessLogRecord record)
        {
            if (record.Seq == ThrowOn)
                throw new IOException("write failed");
            _records.Enqueue(record);
        }
    }

    /// <summary>Loses one sequence number of a batch, as a record that does not serialize.</summary>
    private sealed class LossyBatchSink : IProcessLogBatchSink
    {
        private readonly ConcurrentQueue<ProcessLogRecord> _records = new();

        public long LoseOn { get; init; }

        public IReadOnlyList<ProcessLogRecord> Records => _records.ToArray();

        public bool Disabled => false;

        public string? Path => null;

        public bool IsEnabled(ProcessLogLevel level) => true;

        public void Write(ProcessLogRecord record) => WriteBatch([record]);

        public int WriteBatch(IReadOnlyList<ProcessLogRecord> records)
        {
            var lost = 0;
            foreach (var record in records)
            {
                if (record.Seq == LoseOn)
                    lost++;
                else
                    _records.Enqueue(record);
            }

            return lost;
        }
    }

    private sealed class FailingSink : IProcessLogSink
    {
        private int _disabled;

        public bool Disabled => Volatile.Read(ref _disabled) == 1;

        public string? Path => null;

        public bool IsEnabled(ProcessLogLevel level) => !Disabled;

        public void Write(ProcessLogRecord record) => Volatile.Write(ref _disabled, 1);
    }

    private sealed class FakeExitSignal : IProcessExitSignal
    {
        private volatile Action? _onExit;

        public int Registered => _onExit is null ? 0 : 1;

        public IDisposable Register(Action onExit)
        {
            _onExit = onExit;
            return new Unregister(this);
        }

        public void Raise() => _onExit?.Invoke();

        private sealed class Unregister(FakeExitSignal owner) : IDisposable
        {
            public void Dispose() => owner._onExit = null;
        }
    }
}
