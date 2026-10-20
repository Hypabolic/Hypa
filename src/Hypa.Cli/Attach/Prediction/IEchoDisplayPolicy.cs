using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.Cli.Attach.Prediction;

/// <summary>
/// Decides whether the client shows the predictions of a confirmed epoch
/// (ADR 0018, decision 1). The policy never shows a tentative epoch: the
/// predictor asks it only about epochs that have a confirmed prediction.
/// </summary>
internal interface IEchoDisplayPolicy
{
    /// <summary>False when the predictor must not record or show predictions.</summary>
    bool Predicts { get; }

    /// <param name="srtt">The smoothed time from a seq send to its ack. Null before the first sample.</param>
    /// <param name="oldestPending">The age of the oldest pending prediction of a confirmed epoch.</param>
    bool ShouldDisplay(TimeSpan? srtt, TimeSpan oldestPending);

    /// <summary>The keys now go to another pane. The next RTT sample is of a new route.</summary>
    void NewRoute()
    {
    }
}

/// <summary>
/// Shows predictions when the SRTT is more than 30 ms and stops when it is
/// 20 ms or less. Between the two values the last decision stays. A pending
/// prediction older than 250 ms shows at any SRTT.
/// </summary>
internal sealed class AdaptiveEchoDisplayPolicy : IEchoDisplayPolicy
{
    public static readonly TimeSpan ShowAbove = TimeSpan.FromMilliseconds(30);

    public static readonly TimeSpan HideAtOrBelow = TimeSpan.FromMilliseconds(20);

    public static readonly TimeSpan ShowAnywayAfter = TimeSpan.FromMilliseconds(250);

    /// <summary>The first sample of a new route decides at this value, with no hysteresis.</summary>
    public static readonly TimeSpan NewRouteThreshold = TimeSpan.FromMilliseconds(25);

    private bool _showing;
    private bool _newRoute;

    public bool Predicts => true;

    /// <summary>The SRTT decision of the last call, without the 250 ms rule.</summary>
    public bool Showing => _showing;

    public bool ShouldDisplay(TimeSpan? srtt, TimeSpan oldestPending)
    {
        if (srtt is not { } value)
        {
            if (_newRoute)
                return oldestPending >= ShowAnywayAfter;
        }
        else if (_newRoute)
        {
            // The state of the old route does not apply to this one.
            _showing = value > NewRouteThreshold;
            _newRoute = false;
        }
        else if (value > ShowAbove)
        {
            _showing = true;
        }
        else if (value <= HideAtOrBelow)
        {
            _showing = false;
        }

        return _showing || oldestPending >= ShowAnywayAfter;
    }

    public void NewRoute() => _newRoute = true;
}

/// <summary>Shows every confirmed epoch.</summary>
internal sealed class AlwaysEchoDisplayPolicy : IEchoDisplayPolicy
{
    public bool Predicts => true;

    public bool ShouldDisplay(TimeSpan? srtt, TimeSpan oldestPending) => true;
}

/// <summary>Records and shows nothing.</summary>
internal sealed class NeverEchoDisplayPolicy : IEchoDisplayPolicy
{
    public bool Predicts => false;

    public bool ShouldDisplay(TimeSpan? srtt, TimeSpan oldestPending) => false;
}

/// <summary>Builds the policy of a config value. Each call returns a new instance.</summary>
internal static class EchoDisplayPolicies
{
    public static IEchoDisplayPolicy For(PredictiveEchoMode mode) => mode switch
    {
        PredictiveEchoMode.Always => new AlwaysEchoDisplayPolicy(),
        PredictiveEchoMode.Never => new NeverEchoDisplayPolicy(),
        _ => new AdaptiveEchoDisplayPolicy(),
    };
}
