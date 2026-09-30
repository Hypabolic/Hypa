namespace Hypa.Connectivity.Infrastructure;

/// <summary>OpenSSH process port. Only the process adapter starts <c>ssh</c>.</summary>
public interface IOpenSshCommandRunner
{
    Task<OpenSshCaptureResult> CaptureAsync(
        OpenSshRunRequest request,
        CancellationToken cancellationToken = default);

    Task<OpenSshStdioSession> StartStdioAsync(
        OpenSshRunRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OpenSshRunRequest
{
    public required string Target { get; init; }

    public required string RemoteCommand { get; init; }

    public string? ConfigPath { get; init; }

    public string? ControlPath { get; init; }

    public bool BatchMode { get; init; } = true;
}

public sealed record OpenSshCaptureResult
{
    public required int ExitCode { get; init; }

    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }
}

public sealed class OpenSshStdioSession : IAsyncDisposable
{
    public required Stream Stream { get; init; }

    public required IAsyncDisposable Lifetime { get; init; }

    public async ValueTask DisposeAsync() =>
        await Lifetime.DisposeAsync().ConfigureAwait(false);
}
