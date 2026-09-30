namespace Hypa.AgentRuntime.Domain;

/// <summary>
/// Reliability class for journal records and live fanout policy (design §10.2).
/// </summary>
public enum EventReliability : byte
{
    /// <summary>Durable journal; never drop on live fanout (control, lifecycle).</summary>
    Reliable = 1,

    /// <summary>Output class: may journal under budget; live is droppable/coalesceable.</summary>
    Output = 2,

    /// <summary>Live-only attach stream: never journaled; droppable/coalesceable like Output.</summary>
    Render = 3,
}

/// <summary>Wire helpers for <see cref="EventReliability"/>.</summary>
public static class EventReliabilityMap
{
    public const string Reliable = "reliable";
    public const string Output = "output";
    public const string Render = "render";

    public static string ToWire(EventReliability r) => r switch
    {
        EventReliability.Reliable => Reliable,
        EventReliability.Output => Output,
        EventReliability.Render => Render,
        _ => throw new ArgumentOutOfRangeException(nameof(r), r, "Unknown reliability"),
    };

    public static bool TryParse(string? token, out EventReliability r)
    {
        r = default;
        if (string.IsNullOrWhiteSpace(token))
            return false;
        switch (token.Trim().ToLowerInvariant())
        {
            case Reliable:
                r = EventReliability.Reliable;
                return true;
            case Output:
                r = EventReliability.Output;
                return true;
            case Render:
                r = EventReliability.Render;
                return true;
            default:
                return false;
        }
    }

    public static EventReliability ForClass(EventClass c) => c switch
    {
        EventClass.Control or EventClass.Lifecycle => EventReliability.Reliable,
        EventClass.Output => EventReliability.Output,
        EventClass.Render => EventReliability.Render,
        _ => EventReliability.Reliable,
    };
}
