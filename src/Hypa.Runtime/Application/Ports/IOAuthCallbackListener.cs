namespace Hypa.Runtime.Application.Ports;

public interface IOAuthCallbackListener
{
    Task StartAsync(CancellationToken ct);
    Uri GetRedirectUri();
    Task<OAuthCallbackResult> WaitForCallbackAsync(TimeSpan timeout, CancellationToken ct);
    Task StopAsync();
}

public sealed record OAuthCallbackResult(string? Code, string? Error, string? State);
