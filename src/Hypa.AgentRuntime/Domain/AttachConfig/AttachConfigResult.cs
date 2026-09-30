namespace Hypa.AgentRuntime.Domain.AttachConfig;

/// <summary>Expected-error result for attach TOML. Do not use exceptions for bad user config.</summary>
public readonly struct AttachConfigResult<T>
{
    private readonly T? _value;
    private readonly IReadOnlyList<AttachConfigError> _errors;

    public bool IsOk { get; }

    public T Value => IsOk
        ? _value!
        : throw new InvalidOperationException("Attach config result is not Ok.");

    public IReadOnlyList<AttachConfigError> Errors => _errors;

    public AttachConfigError Error => _errors.Count > 0
        ? _errors[0]
        : throw new InvalidOperationException("Attach config result has no errors.");

    private AttachConfigResult(T value)
    {
        IsOk = true;
        _value = value;
        _errors = [];
    }

    private AttachConfigResult(IReadOnlyList<AttachConfigError> errors)
    {
        IsOk = false;
        _value = default;
        _errors = errors.Count == 0
            ? [AttachConfigError.Toml("Invalid attach config.")]
            : errors;
    }

    public static AttachConfigResult<T> Ok(T value) => new(value);

    public static AttachConfigResult<T> Fail(AttachConfigError error) => new([error]);

    public static AttachConfigResult<T> Fail(IReadOnlyList<AttachConfigError> errors) => new(errors);

    public AttachConfigResult<TOut> Map<TOut>(Func<T, TOut> map) =>
        IsOk ? AttachConfigResult<TOut>.Ok(map(Value)) : AttachConfigResult<TOut>.Fail(_errors);
}
