namespace Hypa.Placement.Infrastructure;

public interface ISshStdioBridge : IAsyncDisposable
{
    string LocalSocketPath { get; }
}

public sealed record SshStdioBridgeStartRequest
{
    public required string Target { get; init; }
    public required string Session { get; init; }
    public required string LocalSocketPath { get; init; }
    public string? ConfigPath { get; init; }
    public string? ControlPath { get; init; }
    public bool BatchMode { get; init; }
    public bool FreshDirectory { get; init; } = true;
}

public interface ISshStdioBridgeFactory
{
    ISshStdioBridge Start(SshStdioBridgeStartRequest request);
}

public sealed class SshStdioBridgeFactory : ISshStdioBridgeFactory
{
    public ISshStdioBridge Start(SshStdioBridgeStartRequest request) =>
        SshStdioBridge.Start(request);
}
