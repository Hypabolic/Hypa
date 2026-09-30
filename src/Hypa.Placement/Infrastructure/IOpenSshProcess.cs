namespace Hypa.Placement.Infrastructure;

/// <summary>
/// OpenSSH process seam. The application port is <c>IRemoteMuxPath</c>.
/// Unit tests replace this collaborator. OpenSSH owns credentials.
/// </summary>
public interface IOpenSshProcess
{
    Task<OpenSshProcessResult> RunAsync(
        OpenSshProcessRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record OpenSshProcessRequest
{
    public required string Target { get; init; }
    public required string RemoteCommand { get; init; }
    public string? ConfigPath { get; init; }
    public string? ControlPath { get; init; }
    public bool BatchMode { get; init; }
    public string? StdinText { get; init; }
}

public sealed record OpenSshProcessResult
{
    public required int ExitCode { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";

    public bool Success => ExitCode == 0;
}
