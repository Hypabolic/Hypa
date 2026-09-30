namespace Hypa.AgentRuntime.Application;

/// <summary>Expected history-store failure. Decode and miss are not thrown.</summary>
public readonly record struct PaneHistoryError(string Code, string Message)
{
    public static PaneHistoryError DecodeFailed { get; } =
        new("decode_failed", "History block decode failed.");

    public static PaneHistoryError CompressFailed { get; } =
        new("compress_failed", "History block compress failed.");

    public static PaneHistoryError NotFound { get; } =
        new("not_found", "History row is not in the store.");
}

/// <summary>
/// Lightweight Result for pane history. AgentRuntime does not reference
/// <c>Hypa.Runtime</c>.
/// </summary>
public readonly record struct PaneHistoryResult<T>
{
    private readonly T? _value;
    private readonly PaneHistoryError _error;

    public bool IsOk { get; }

    public T Value => IsOk
        ? _value!
        : throw new InvalidOperationException("Result is not Ok.");

    public PaneHistoryError Error => !IsOk
        ? _error
        : throw new InvalidOperationException("Result is not Fail.");

    private PaneHistoryResult(T value)
    {
        IsOk = true;
        _value = value;
        _error = default;
    }

    private PaneHistoryResult(PaneHistoryError error)
    {
        IsOk = false;
        _value = default;
        _error = error;
    }

    public static PaneHistoryResult<T> Ok(T value) => new(value);

    public static PaneHistoryResult<T> Fail(PaneHistoryError error) => new(error);
}
