using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Connections;
using Hypa.Cli.Mux;
using Xunit;
using Hypa.AgentRuntime.Domain.Cubes.Connections;

namespace Hypa.UnitTests.Cli;

public sealed class EndpointConnectionTests
{
    [Fact]
    public void LosingALiveConnectionMovesToLostAndEmitsTheLoss()
    {
        var clock = new FakeClock();
        var connection = Live(clock);
        var lostAt = clock.GetTimestamp();

        var result = connection.Lose("connection was lost");

        Assert.True(result.IsOk);
        Assert.Equal(new ConnectionLost("cube", "connection was lost"), result.Value);
        Assert.Equal(EndpointConnectionState.Lost, connection.State);
        Assert.Equal(lostAt, connection.LostAt);
        Assert.True(connection.IsArmed);
    }

    [Theory]
    [InlineData("Connecting")]
    [InlineData("Lost")]
    [InlineData("Reconnecting")]
    [InlineData("Failed")]
    public void LosingFromAnyOtherStateIsRejected(string stateName)
    {
        var state = Enum.Parse<EndpointConnectionState>(stateName);
        var clock = new FakeClock();
        var connection = Reach(state, clock);

        var result = connection.Lose("connection was lost");

        Assert.False(result.IsOk);
        Assert.Equal(state, connection.State);
    }

    [Fact]
    public void LiveFromConnectingCommitsWithoutAnEvent()
    {
        var connection = new EndpointConnection("cube", new FakeClock());

        var result = connection.Live(new ConnectionId("cube", 3), "boot-a");

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
        Assert.Equal(EndpointConnectionState.Live, connection.State);
        Assert.Equal(new ConnectionId("cube", 3), connection.Connection);
        Assert.Equal("boot-a", connection.BootId);
    }

    [Theory]
    [InlineData("Lost")]
    [InlineData("Reconnecting")]
    public void LiveFromAnArmedStateEmitsARestore(string stateName)
    {
        var state = Enum.Parse<EndpointConnectionState>(stateName);
        var clock = new FakeClock();
        var connection = Reach(state, clock);

        var result = connection.Live(new ConnectionId("cube", 8), "boot-b");

        Assert.True(result.IsOk);
        Assert.Equal(new ConnectionRestored("cube", new ConnectionId("cube", 8)), result.Value);
        Assert.Equal(EndpointConnectionState.Live, connection.State);
        Assert.Equal(0, connection.Attempts);
        Assert.Null(connection.LastFailureCode);
        Assert.False(connection.IsArmed);
    }

    [Fact]
    public void LiveFromFailedCommitsWithoutAnEvent()
    {
        var clock = new FakeClock();
        var connection = Reach(EndpointConnectionState.Failed, clock);

        var result = connection.Live(new ConnectionId("cube", 9), "boot-c");

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
        Assert.Equal(EndpointConnectionState.Live, connection.State);
    }

    [Fact]
    public void LiveFromLiveRefreshesTheIdentityWithoutAnEvent()
    {
        var connection = Live(new FakeClock());

        var result = connection.Live(new ConnectionId("cube", 12), "boot-d");

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
        Assert.Equal(new ConnectionId("cube", 12), connection.Connection);
        Assert.Equal("boot-d", connection.BootId);
    }

    [Fact]
    public void LiveRejectsAnotherEndpointAndAnUnknownGeneration()
    {
        var connection = new EndpointConnection("cube", new FakeClock());

        Assert.False(connection.Live(new ConnectionId("other", 3), "boot").IsOk);
        Assert.False(connection.Live(new ConnectionId("cube", 0), "boot").IsOk);
        Assert.Equal(EndpointConnectionState.Connecting, connection.State);
    }

    [Fact]
    public void ADueRedialCountsOneAttemptAndSchedulesTheNextOne()
    {
        var clock = new FakeClock();
        var connection = Live(clock);
        connection.Lose("connection was lost");

        var result = connection.BeginRedial();

        Assert.True(result.IsOk);
        Assert.Equal(EndpointConnectionState.Reconnecting, result.Value);
        Assert.Equal(1, connection.Attempts);
        Assert.Equal(clock.GetTimestamp() + Ticks(0.5), connection.NextAttemptAt);
    }

    [Fact]
    public void ARedialBeforeItIsDueIsRejected()
    {
        var clock = new FakeClock();
        var connection = Live(clock);
        connection.Lose("connection was lost");
        connection.BeginRedial();

        clock.Advance(TimeSpan.FromMilliseconds(400));
        var result = connection.BeginRedial();

        Assert.False(result.IsOk);
        Assert.False(connection.IsDue);
        Assert.Equal(1, connection.Attempts);
    }

    [Theory]
    [InlineData("Connecting")]
    [InlineData("Live")]
    [InlineData("Failed")]
    public void ARedialFromANonArmedStateIsRejected(string stateName)
    {
        var state = Enum.Parse<EndpointConnectionState>(stateName);
        var connection = Reach(state, new FakeClock());

        Assert.False(connection.BeginRedial().IsOk);
        Assert.Equal(state, connection.State);
    }

    [Fact]
    public void AnImmediateRedialSkipsTheWaitAndKeepsTheSchedule()
    {
        var clock = new FakeClock();
        var connection = Live(clock);
        connection.Lose("connection was lost");
        connection.BeginRedial();
        var scheduled = connection.NextAttemptAt;

        var result = connection.BeginRedial(immediate: true);

        Assert.True(result.IsOk);
        Assert.Equal(EndpointConnectionState.Reconnecting, connection.State);
        Assert.Equal(1, connection.Attempts);
        Assert.Equal(scheduled, connection.NextAttemptAt);
    }

    [Fact]
    public void AFailedRedialStaysArmedAndKeepsTheReason()
    {
        var clock = new FakeClock();
        var connection = Live(clock);
        connection.Lose("connection was lost");
        connection.BeginRedial();

        var result = connection.RedialFailed(new ConnectError("dial_failed", "timeout"));

        Assert.True(result.IsOk);
        Assert.Equal(EndpointConnectionState.Reconnecting, connection.State);
        Assert.Equal("dial_failed", connection.LastFailureCode);
        Assert.True(connection.IsArmed);
    }

    [Fact]
    public void ARedialFailureFromLiveIsRejected()
    {
        var connection = Live(new FakeClock());

        Assert.False(connection.RedialFailed(new ConnectError("dial_failed")).IsOk);
        Assert.Equal(EndpointConnectionState.Live, connection.State);
    }

    [Theory]
    [InlineData("Lost")]
    [InlineData("Reconnecting")]
    public void CancelFromAnArmedStateFailsAndEmitsAFailure(string stateName)
    {
        var state = Enum.Parse<EndpointConnectionState>(stateName);
        var connection = Reach(state, new FakeClock());

        var result = connection.Cancel();

        Assert.True(result.IsOk);
        Assert.Equal(new ConnectionFailed("cube", "cancelled by selection"), result.Value);
        Assert.Equal(EndpointConnectionState.Failed, connection.State);
        Assert.False(connection.IsArmed);
    }

    [Theory]
    [InlineData("Connecting")]
    [InlineData("Live")]
    [InlineData("Failed")]
    public void CancelFromANonArmedStateIsRejected(string stateName)
    {
        var state = Enum.Parse<EndpointConnectionState>(stateName);
        var connection = Reach(state, new FakeClock());

        Assert.False(connection.Cancel().IsOk);
        Assert.Equal(state, connection.State);
    }

    [Fact]
    public void BackoffIsImmediateThenHalfSecondDoublingThenFiveSeconds()
    {
        Assert.Equal(
            [0, 500, 1000, 2000, 4000, 5000, 5000],
            Enumerable.Range(1, 7).Select(n => (int)EndpointConnection.DelayBefore(n).TotalMilliseconds));
    }

    [Fact]
    public void TheScheduleFollowsTheBackoffTableAcrossRedials()
    {
        var clock = new FakeClock();
        var connection = Live(clock);
        connection.Lose("connection was lost");
        var gaps = new List<int>();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            clock.Advance(TimeSpan.FromTicks(Math.Max(0, connection.NextAttemptAt - clock.GetTimestamp())));
            Assert.True(connection.BeginRedial().IsOk);
            var gapTicks = connection.NextAttemptAt - clock.GetTimestamp();
            gaps.Add((int)(gapTicks * 1000 / clock.TimestampFrequency));
        }

        Assert.Equal([500, 1000, 2000, 4000, 5000, 5000], gaps);
    }

    [Fact]
    public void QuietStatusAppearsAtTwoSecondsAndGraceAtThirty()
    {
        var clock = new FakeClock();
        var connection = Live(clock);
        connection.Lose("connection was lost");

        clock.Advance(TimeSpan.FromMilliseconds(1900));
        Assert.False(connection.QuietElapsed);
        Assert.False(connection.GraceExpired);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(connection.QuietElapsed);
        Assert.False(connection.GraceExpired);

        clock.Advance(TimeSpan.FromSeconds(27.9));
        Assert.False(connection.GraceExpired);

        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(connection.GraceExpired);
    }

    [Fact]
    public void TheFirstRedialIsDueAtOnceAfterTheLoss()
    {
        var connection = Live(new FakeClock());

        connection.Lose("connection was lost");

        Assert.True(connection.IsDue);
    }

    [Fact]
    public void TheRegistryHoldsOneEntityPerEndpointAndFindsTheArmedOne()
    {
        var clock = new FakeClock();
        var registry = new AttachEndpointConnections { Time = clock };
        Assert.Null(registry.Armed);
        Assert.False(registry.TryGetEntity("cube", out _));

        var first = registry.Entity("cube");
        var second = registry.Entity("lab");

        Assert.Same(first, registry.Entity("cube"));
        Assert.NotSame(first, second);
        Assert.True(registry.TryGetEntity("cube", out var found));
        Assert.Same(first, found);

        first.Live(new ConnectionId("cube", 4), "boot");
        Assert.Null(registry.Armed);
        first.Lose("connection was lost");
        Assert.Same(first, registry.Armed);
    }

    private static EndpointConnection Live(TimeProvider clock)
    {
        var connection = new EndpointConnection("cube", clock);
        connection.Live(new ConnectionId("cube", 7), "boot-a");
        return connection;
    }

    private static EndpointConnection Reach(EndpointConnectionState state, FakeClock clock)
    {
        var connection = new EndpointConnection("cube", clock);
        if (state is EndpointConnectionState.Connecting)
            return connection;

        connection.Live(new ConnectionId("cube", 7), "boot-a");
        if (state is EndpointConnectionState.Live)
            return connection;

        connection.Lose("connection was lost");
        if (state is EndpointConnectionState.Lost)
            return connection;

        if (state is EndpointConnectionState.Reconnecting)
        {
            connection.BeginRedial();
            return connection;
        }

        connection.Cancel();
        return connection;
    }

    private static long Ticks(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 10, 8, 4, 0, 0, TimeSpan.Zero);
        private long _ticks;

        public override DateTimeOffset GetUtcNow() => _utc;

        public override long GetTimestamp() => _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta)
        {
            _utc += delta;
            _ticks += delta.Ticks;
        }
    }
}
