namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Lightweight Result for agent-runtime expected errors (no dependency on Hypa.Runtime).
/// </summary>
public readonly record struct RuntimeResult<T>
{
    private readonly T? _value;
    private readonly RuntimePersistenceError? _error;

    public bool IsOk { get; }

    public T Value => IsOk
        ? _value!
        : throw new InvalidOperationException("Result is not Ok.");

    public RuntimePersistenceError Error => !IsOk
        ? _error!
        : throw new InvalidOperationException("Result is not Fail.");

    private RuntimeResult(T value)
    {
        IsOk = true;
        _value = value;
        _error = null;
    }

    private RuntimeResult(RuntimePersistenceError error)
    {
        IsOk = false;
        _value = default;
        _error = error;
    }

    public static RuntimeResult<T> Ok(T value) => new(value);

    public static RuntimeResult<T> Fail(RuntimePersistenceError error) => new(error);

    public RuntimeResult<U> Map<U>(Func<T, U> f) =>
        IsOk ? RuntimeResult<U>.Ok(f(Value)) : RuntimeResult<U>.Fail(Error);
}

/// <summary>Unit success token for void-like Result operations.</summary>
public readonly struct RuntimeUnit
{
    public static RuntimeUnit Value => default;
}
