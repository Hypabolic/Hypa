namespace Hypa.AgentRuntime.Application.Integrations;

public sealed record IntegrationError(string Code, string Message)
{
    public const string UnknownTarget = "integration.unknown_target";
    public const string InstallFailed = "integration.install_failed";
    public const string UninstallFailed = "integration.uninstall_failed";

    public static IntegrationError Unknown(string target) =>
        new(UnknownTarget, "unknown integration target: " + target);

    public static IntegrationError Install(string message) =>
        new(InstallFailed, message);

    public static IntegrationError Uninstall(string message) =>
        new(UninstallFailed, message);
}

public readonly record struct IntegrationResult<T>
{
    private readonly T? _value;
    private readonly IntegrationError? _error;

    public bool IsOk { get; }

    public T Value => IsOk
        ? _value!
        : throw new InvalidOperationException("Result is not Ok.");

    public IntegrationError Error => !IsOk
        ? _error!
        : throw new InvalidOperationException("Result is not Fail.");

    private IntegrationResult(T value)
    {
        IsOk = true;
        _value = value;
        _error = null;
    }

    private IntegrationResult(IntegrationError error)
    {
        IsOk = false;
        _value = default;
        _error = error;
    }

    public static IntegrationResult<T> Ok(T value) => new(value);

    public static IntegrationResult<T> Fail(IntegrationError error) => new(error);
}
