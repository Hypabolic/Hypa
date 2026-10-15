using Hypa.AgentRuntime.Application.Cubes.Connections;
using Hypa.AgentRuntime.Domain.Cubes.Connections;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The pure reconnect policy. Each decision is checked for each connection
/// state and each time since the loss. The clock is a fake, so no test waits.
/// </summary>
public sealed class EndpointReconnectPolicyTests
{
    private static readonly ReconnectFacts Ready = new(
        DetachRequested: false,
        AttemptRunning: false,
        AttemptTargetsEndpoint: false,
        CommittedEndpointLive: false,
        EndpointInCatalog: true,
        CanRedial: true);

    [Fact]
    public void NoArmedConnectionHasNoStep()
    {
        Assert.Equal(ReconnectStep.Idle, EndpointReconnectPolicy.Decide(null, Ready));
    }

    [Fact]
    public void DetachingHasNoStepEvenWhenTheRedialIsDue()
    {
        var clock = new FakeClock();
        var connection = Lost(clock);

        var facts = Ready with { DetachRequested = true };

        Assert.Equal(ReconnectStep.Idle, EndpointReconnectPolicy.Decide(connection, facts));
    }

    [Fact]
    public void FirstRedialIsImmediateAfterTheLoss()
    {
        var clock = new FakeClock();
        var connection = Lost(clock);

        Assert.Equal(ReconnectStep.Redial, EndpointReconnectPolicy.Decide(connection, Ready));
    }

    [Fact]
    public void ScheduledRedialWaitsForTheBackoff()
    {
        var clock = new FakeClock();
        var connection = Lost(clock);
        _ = connection.BeginRedial();
        _ = connection.RedialFailed(new ConnectError("dial_failed"));

        Assert.Equal(ReconnectStep.KeepStatus, EndpointReconnectPolicy.Decide(connection, Ready));

        clock.Advance(TimeSpan.FromSeconds(0.5));

        Assert.Equal(ReconnectStep.Redial, EndpointReconnectPolicy.Decide(connection, Ready));
    }

    [Fact]
    public void RedialWaitsWhenThePresentationCannotRedial()
    {
        var clock = new FakeClock();
        var connection = Lost(clock);

        var facts = Ready with { CanRedial = false };

        Assert.Equal(ReconnectStep.KeepStatus, EndpointReconnectPolicy.Decide(connection, facts));
    }

    [Fact]
    public void CommittedLiveClientCommitsTheConnection()
    {
        var clock = new FakeClock();
        var connection = Lost(clock);

        var facts = Ready with { CommittedEndpointLive = true };

        Assert.Equal(ReconnectStep.CommitLive, EndpointReconnectPolicy.Decide(connection, facts));
    }

    [Fact]
    public void InFlightAttemptForThisEndpointKeepsTheStatusWithoutANewAttempt()
    {
        var clock = new FakeClock();
        var connection = Lost(clock);

        var facts = Ready with { AttemptRunning = true, AttemptTargetsEndpoint = true };

        Assert.Equal(ReconnectStep.KeepStatus, EndpointReconnectPolicy.Decide(connection, facts));
    }

    [Fact]
    public void InFlightAttemptForAnotherEndpointHasNoStep()
    {
        var clock = new FakeClock();
        var connection = Lost(clock);

        var facts = Ready with { AttemptRunning = true, AttemptTargetsEndpoint = false };

        Assert.Equal(ReconnectStep.Idle, EndpointReconnectPolicy.Decide(connection, facts));
    }

    [Fact]
    public void EndpointThatLeftTheCatalogCancelsTheReconnect()
    {
        var clock = new FakeClock();
        var connection = Lost(clock);

        var facts = Ready with { EndpointInCatalog = false };

        Assert.Equal(ReconnectStep.CancelReconnect, EndpointReconnectPolicy.Decide(connection, facts));
    }

    [Fact]
    public void ConnectingEndpointHasNoStep()
    {
        var connection = new EndpointConnection("cube", new FakeClock());

        Assert.Equal(ReconnectStep.Idle, EndpointReconnectPolicy.Decide(connection, Ready));
    }

    [Fact]
    public void LiveEndpointHasNoStep()
    {
        var connection = new EndpointConnection("cube", new FakeClock());
        _ = connection.Live(new ConnectionId("cube", 7), "boot-a");

        Assert.Equal(ReconnectStep.Idle, EndpointReconnectPolicy.Decide(connection, Ready));
    }

    [Fact]
    public void CancelledReconnectHasNoStep()
    {
        var clock = new FakeClock();
        var connection = Lost(clock);
        _ = connection.Cancel();

        Assert.Equal(ReconnectStep.Idle, EndpointReconnectPolicy.Decide(connection, Ready));
    }

    [Theory]
    [InlineData(0, ReconnectStatusLevel.Hidden)]
    [InlineData(1.999, ReconnectStatusLevel.Hidden)]
    [InlineData(2, ReconnectStatusLevel.Notice)]
    [InlineData(29.999, ReconnectStatusLevel.Notice)]
    [InlineData(30, ReconnectStatusLevel.Unreachable)]
    [InlineData(120, ReconnectStatusLevel.Unreachable)]
    public void StatusLevelFollowsTheTimeSinceTheLoss(double seconds, ReconnectStatusLevel expected)
    {
        var clock = new FakeClock();
        var connection = Lost(clock);
        clock.Advance(TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, EndpointReconnectPolicy.StatusFor(connection));
    }

    private static EndpointConnection Lost(FakeClock clock)
    {
        var connection = new EndpointConnection("cube", clock);
        _ = connection.Live(new ConnectionId("cube", 7), "boot-a");
        _ = connection.Lose("connection was lost");
        return connection;
    }

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
