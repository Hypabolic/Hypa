using Hypa.AgentRuntime.Application.Metadata;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class MetadataTokenStoreTests
{
    [Fact]
    public void Patch_normalises_values_and_empty_values_clear()
    {
        using var store = new MetadataTokenStore();
        var set = store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "  ready\n  ",
            ["empty"] = " \t",
        });

        Assert.True(set.Accepted);
        Assert.Equal("ready", set.Values["status"]);
        Assert.DoesNotContain("empty", set.Values.Keys);

        var clear = store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = null,
        });
        Assert.True(clear.Accepted);
        Assert.Empty(store.Get("workspace", "w1"));
    }

    [Fact]
    public void Stale_sequence_is_successful_noop_and_ttl_expires_only_patched_key()
    {
        var now = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);
        using var store = new MetadataTokenStore(new FrozenTimeProvider(now));
        Assert.True(store.ApplyPatch("workspace", "w1", "source", new Dictionary<string, string?>
        {
            ["short"] = "one",
            ["long"] = "two",
        }, sequence: 2, now: now).Accepted);
        Assert.True(store.ApplyPatch("workspace", "w1", "source", new Dictionary<string, string?>
        {
            ["short"] = "fresh",
        }, sequence: 1, ttlMs: 100, now: now).StaleSequence);
        Assert.Equal("one", store.Get("workspace", "w1")["short"]);

        Assert.True(store.ApplyPatch("workspace", "w1", "source", new Dictionary<string, string?>
        {
            ["short"] = "fresh",
        }, sequence: 3, ttlMs: 100, now: now).Changed);
        var expired = store.ExpireDue(now.AddMilliseconds(100));
        Assert.Single(expired);
        Assert.DoesNotContain("short", expired[0].Values.Keys);
        Assert.Equal("two", expired[0].Values["long"]);
        var values = store.Get("workspace", "w1");
        Assert.DoesNotContain("short", values.Keys);
        Assert.Equal("two", values["long"]);
    }

    [Fact]
    public void Later_patch_without_ttl_cancels_key_expiry()
    {
        var now = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);
        using var store = new MetadataTokenStore(new FrozenTimeProvider(now));
        Assert.True(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "ready",
        }, ttlMs: 100, now: now).Accepted);
        Assert.True(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "ready",
        }, now: now).Accepted);
        store.ExpireDue(now.AddMilliseconds(100));
        Assert.Equal("ready", store.Get("workspace", "w1")["status"]);
    }

    [Fact]
    public void Invalid_patch_fails_closed_and_preserves_last_good_map()
    {
        using var store = new MetadataTokenStore();
        Assert.True(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "ready",
        }).Accepted);

        var badSource = store.ApplyPatch("workspace", "w1", "bad source!", new Dictionary<string, string?>
        {
            ["next"] = "value",
        });
        Assert.False(badSource.Accepted);
        Assert.Equal("token key is invalid", store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["bad key!"] = "value",
        }).Error);
        Assert.False(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["has.dot"] = "value",
        }).Accepted);
        Assert.False(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            [new string('a', 33)] = "value",
        }).Accepted);
        Assert.Equal("ttl_ms is out of range", store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "expired",
        }, ttlMs: 0).Error);
        Assert.False(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "expired",
        }, ttlMs: -1).Accepted);
        Assert.False(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "expired",
        }, ttlMs: MetadataTokenLimits.MaxTtlMilliseconds + 1).Accepted);

        Assert.Equal("ready", store.Get("workspace", "w1")["status"]);
        Assert.DoesNotContain("next", store.Get("workspace", "w1").Keys);
        Assert.DoesNotContain("bad key!", store.Get("workspace", "w1").Keys);
        Assert.DoesNotContain("has.dot", store.Get("workspace", "w1").Keys);
    }

    [Fact]
    public void Caps_and_sequenced_source_bound_fail_closed()
    {
        using var store = new MetadataTokenStore();
        var tooMany = Enumerable.Range(0, 17).ToDictionary(i => $"k{i}", i => (string?)$"v{i}");
        Assert.False(store.ApplyPatch("workspace", "w1", "git", tooMany).Accepted);

        var first = new Dictionary<string, string?>();
        for (var i = 0; i < 16; i++)
            first[$"a{i}"] = "x";
        Assert.True(store.ApplyPatch("workspace", "w1", "git", first).Accepted);
        var second = new Dictionary<string, string?>();
        for (var i = 0; i < 16; i++)
            second[$"b{i}"] = "y";
        Assert.True(store.ApplyPatch("workspace", "w1", "git", second).Accepted);
        Assert.False(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["overflow"] = "z",
        }).Accepted);
        Assert.Equal(32, store.Get("workspace", "w1").Count);

        using var seq = new MetadataTokenStore();
        for (var i = 0; i < 32; i++)
        {
            Assert.True(seq.ApplyPatch("workspace", "w2", $"src{i}", new Dictionary<string, string?>
            {
                ["k"] = "v",
            }, sequence: 1).Accepted);
        }
        Assert.False(seq.ApplyPatch("workspace", "w2", "src32", new Dictionary<string, string?>
        {
            ["k"] = "v",
        }, sequence: 1).Accepted);
    }

    [Fact]
    public void ApplyPatch_does_not_expire_due_keys()
    {
        var now = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);
        using var store = new MetadataTokenStore(new FrozenTimeProvider(now));
        Assert.True(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "ready",
        }, ttlMs: 100, now: now).Accepted);
        Assert.True(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["other"] = "kept",
        }, now: now.AddMilliseconds(100)).Accepted);

        var values = store.Get("workspace", "w1");
        Assert.Equal("ready", values["status"]);
        Assert.Equal("kept", values["other"]);
        store.ExpireDue(now.AddMilliseconds(100));
        values = store.Get("workspace", "w1");
        Assert.DoesNotContain("status", values.Keys);
        Assert.Equal("kept", values["other"]);
    }

    [Fact]
    public void SweepRequested_suppresses_Expired_on_ExpireDue()
    {
        var now = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);
        using var store = new MetadataTokenStore(new FrozenTimeProvider(now));
        var expired = 0;
        var sweep = 0;
        store.Expired += _ => expired++;
        store.SweepRequested += () =>
        {
            sweep++;
            return Task.CompletedTask;
        };
        Assert.True(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "ready",
        }, ttlMs: 100, now: now).Accepted);

        var removed = store.ExpireDue(now.AddMilliseconds(100));
        Assert.Single(removed);
        Assert.Equal(0, expired);
        Assert.Equal(0, sweep);
        Assert.Empty(store.Get("workspace", "w1"));
    }

    [Fact]
    public void Timer_tick_requests_sweep_once_and_does_not_raise_Expired()
    {
        var time = new SteppingTimeProvider(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero));
        using var store = new MetadataTokenStore(time);
        var expired = 0;
        var sweep = 0;
        store.Expired += _ => expired++;
        store.SweepRequested += () =>
        {
            sweep++;
            return Task.CompletedTask;
        };
        Assert.True(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "ready",
        }, ttlMs: 100, now: time.GetUtcNow()).Accepted);
        Assert.Equal(1, time.CreatedTimerCount);

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, sweep);
        Assert.Equal(0, expired);
        Assert.Equal("ready", store.Get("workspace", "w1")["status"]);
    }

    [Fact]
    public void Timer_tick_expires_when_SweepRequested_is_absent()
    {
        var time = new SteppingTimeProvider(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero));
        using var store = new MetadataTokenStore(time);
        var expired = 0;
        store.Expired += _ => expired++;
        Assert.True(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "ready",
        }, ttlMs: 100, now: time.GetUtcNow()).Accepted);

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, expired);
        Assert.Empty(store.Get("workspace", "w1"));
    }

    [Fact]
    public void Timer_tick_awaits_SweepRequested_before_returning()
    {
        var time = new SteppingTimeProvider(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero));
        using var store = new MetadataTokenStore(time);
        var completed = 0;
        store.SweepRequested += async () =>
        {
            await Task.Delay(50);
            Interlocked.Increment(ref completed);
        };
        Assert.True(store.ApplyPatch("workspace", "w1", "git", new Dictionary<string, string?>
        {
            ["status"] = "ready",
        }, ttlMs: 100, now: time.GetUtcNow()).Accepted);

        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, completed);
        Assert.Equal("ready", store.Get("workspace", "w1")["status"]);
    }

    [Fact]
    public void ApplyPatch_pane_kind_shares_caps_ttl_seq()
    {
        var now = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);
        using var store = new MetadataTokenStore(new FrozenTimeProvider(now));
        Assert.True(store.ApplyPatch("pane", "p1", "git", new Dictionary<string, string?>
        {
            ["name"] = "claude",
        }, sequence: 2, ttlMs: 100, now: now).Accepted);
        Assert.True(store.ApplyPatch("pane", "p1", "git", new Dictionary<string, string?>
        {
            ["name"] = "other",
        }, sequence: 1, now: now).StaleSequence);
        Assert.Equal("claude", store.Get("pane", "p1")["name"]);

        var tooMany = Enumerable.Range(0, 17).ToDictionary(i => $"k{i}", i => (string?)$"v{i}");
        Assert.False(store.ApplyPatch("pane", "p1", "git", tooMany).Accepted);

        Assert.True(store.ApplyPatch("pane", "p1", "git", new Dictionary<string, string?>
        {
            ["name"] = "claude",
        }, now: now).Accepted);
        store.ExpireDue(now.AddMilliseconds(100));
        Assert.Equal("claude", store.Get("pane", "p1")["name"]);
    }

    private sealed class FrozenTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FrozenTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) =>
            throw new NotSupportedException();
    }

    private sealed class SteppingTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        private readonly List<StepTimer> _timers = [];

        public SteppingTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public int CreatedTimerCount { get; private set; }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            CreatedTimerCount++;
            var timer = new StepTimer(callback, state, dueTime, period, _utcNow);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            _utcNow += delta;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue(_utcNow);
        }

        private sealed class StepTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _due;
            private readonly TimeSpan _period;
            private DateTimeOffset _dueAt;
            private bool _disposed;

            public StepTimer(
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period,
                DateTimeOffset now)
            {
                _callback = callback;
                _state = state;
                _due = dueTime;
                _period = period;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + dueTime;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime;
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + dueTime;
                return true;
            }

            public void FireIfDue(DateTimeOffset now)
            {
                if (_disposed || _due == Timeout.InfiniteTimeSpan)
                    return;
                while (_dueAt <= now)
                {
                    _callback(_state);
                    if (_period <= TimeSpan.Zero || _period == Timeout.InfiniteTimeSpan)
                    {
                        _dueAt = DateTimeOffset.MaxValue;
                        break;
                    }

                    _dueAt += _period;
                }
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
