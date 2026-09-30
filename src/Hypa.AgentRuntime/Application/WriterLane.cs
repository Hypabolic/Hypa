namespace Hypa.AgentRuntime.Application;

// Additive snake_case.</summary>
public static class WriterLaneNames
{
    public const string Control = "control";
    public const string Ordered = "ordered";
    public const string Render = "render";
}

/// <summary>Admission result for a per-connection writer lane.</summary>
public enum WriterLaneStatus : byte
{
    Ok = 0,
    Full = 1,
    Closed = 2,
    TooLarge = 3,
}

/// <summary>Typed result for reliable/render/ordered writer admission.</summary>
public readonly record struct WriterLaneResult(WriterLaneStatus Status)
{
    public static WriterLaneResult Ok { get; } = new(WriterLaneStatus.Ok);

    public static WriterLaneResult Full { get; } = new(WriterLaneStatus.Full);

    public static WriterLaneResult Closed { get; } = new(WriterLaneStatus.Closed);

    public static WriterLaneResult TooLarge { get; } = new(WriterLaneStatus.TooLarge);

    public bool IsOk => Status == WriterLaneStatus.Ok;

    public bool IsFull => Status == WriterLaneStatus.Full;

    public bool IsClosed => Status == WriterLaneStatus.Closed;

    public bool IsTooLarge => Status == WriterLaneStatus.TooLarge;
}
