namespace Hypa.Runtime.Domain.Common;

public readonly record struct Result<T, E>
{
    private readonly T? _value;
    private readonly E? _error;

    public bool IsOk { get; }

    public T Value => IsOk ? _value! : throw new InvalidOperationException("Result is not Ok.");
    public E Error => !IsOk ? _error! : throw new InvalidOperationException("Result is not Fail.");

    private Result(T value) { IsOk = true; _value = value; _error = default; }
    private Result(E error, bool _) { IsOk = false; _value = default; _error = error; }

    public static Result<T, E> Ok(T value) => new(value);
    public static Result<T, E> Fail(E error) => new(error, false);

    public Result<U, E> Map<U>(Func<T, U> f) =>
        IsOk ? Result<U, E>.Ok(f(Value)) : Result<U, E>.Fail(Error);

    public Result<T, E2> MapError<E2>(Func<E, E2> f) =>
        IsOk ? Result<T, E2>.Ok(Value) : Result<T, E2>.Fail(f(Error));

    public void Deconstruct(out bool isOk, out T? value, out E? error)
    {
        isOk = IsOk;
        value = _value;
        error = _error;
    }
}
