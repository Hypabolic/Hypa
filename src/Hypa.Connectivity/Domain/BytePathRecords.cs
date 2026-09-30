namespace Hypa.Connectivity.Domain;

/// <summary>Provider-owned dial target. Does not require a rendezvous URL.</summary>
public sealed record BytePathEndpoint
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public bool Tls { get; init; }

    public string? TlsServerName { get; init; }

    /// <summary>
    /// When false, the accept helper does not listen for QUIC.
    /// When null, the client does not know and may attempt QUIC when TLS is on.
    /// </summary>
    public bool? QuicListening { get; init; }
}

/// <summary>
/// Provider-owned byte path request. Open a path without a rendezvous URL.
/// QUIC uses <see cref="Endpoint"/>. SSH reach uses target and session.
/// </summary>
public sealed record BytePathRequest
{
    public required string Provider { get; init; }

    public BytePathEndpoint? Endpoint { get; init; }

    public string Target { get; init; } = "";

    public string Session { get; init; } = "";

    public bool ManageSshConfig { get; init; } = true;
}

public sealed record BytePathHandle
{
    public required Stream Stream { get; init; }

    public required IAsyncDisposable Lifetime { get; init; }

    public string Provider { get; init; } = "";

    public PeerConnectionGeneration Generation { get; init; }

    public string Session { get; init; } = "";
}

public sealed record PeerReachRequest
{
    public required PeerProfile Profile { get; init; }

    public bool RequestLiveHandoff { get; init; }

    public bool ManageSshConfig { get; init; } = true;
}

public enum RemoteKeybindingMode
{
    Local,
    Server,
}

public enum RemoteRestartDecision
{
    KeepRunning,
    LiveHandoff,
    StopRequired,
    FailClosed,
}

public sealed record RemoteRestartDecisionResult
{
    public required RemoteRestartDecision Decision { get; init; }

    public string? Reason { get; init; }
}

/// <summary>
/// Destructive restart needs consent. Live handoff is Unix-only.
/// </summary>
public static class RemoteRestartPolicy
{
    public static RemoteRestartDecisionResult Decide(
        bool compatible,
        bool liveHandoffRequested,
        bool liveHandoffEnabled,
        bool consentGranted)
    {
        if (compatible)
            return new RemoteRestartDecisionResult { Decision = RemoteRestartDecision.KeepRunning };

        if (liveHandoffRequested && liveHandoffEnabled)
            return new RemoteRestartDecisionResult { Decision = RemoteRestartDecision.LiveHandoff };

        if (!consentGranted)
        {
            return new RemoteRestartDecisionResult
            {
                Decision = RemoteRestartDecision.FailClosed,
                Reason = PeerReachReasons.ConsentRequired,
            };
        }

        return new RemoteRestartDecisionResult { Decision = RemoteRestartDecision.StopRequired };
    }

    public static bool LiveHandoffSupportedOnHost() =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();
}

public static class BytePathProviders
{
    public const string Tcp = "tcp";

    public const string Quic = "quic";
}

public static class BytePathEndpointRules
{
    public static bool TryValidate(BytePathEndpoint? endpoint, out string detail)
    {
        detail = "";
        if (endpoint is null)
        {
            detail = "byte path endpoint is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(endpoint.Host))
        {
            detail = "byte path host is required";
            return false;
        }

        if (endpoint.Port is <= 0 or > 65535)
        {
            detail = "byte path port is out of range";
            return false;
        }

        if (endpoint.Tls
            && endpoint.TlsServerName is not null
            && string.IsNullOrWhiteSpace(endpoint.TlsServerName))
        {
            detail = "tls server name is required";
            return false;
        }

        return true;
    }

    public static bool TryFromRendezvousUrl(
        RendezvousUrl url,
        out BytePathEndpoint endpoint,
        out string detail)
    {
        endpoint = null!;
        detail = "";
        if (!Uri.TryCreate(url.Value, UriKind.Absolute, out var uri))
        {
            detail = "rendezvous url must be an outbound http, https, or websocket url";
            return false;
        }

        if (uri.Scheme is "wss")
        {
            detail = "wss requires websocket tls; outbound join does not send cleartext";
            return false;
        }

        if (uri.Scheme is not "http" and not "ws" and not "https")
        {
            detail = "rendezvous url must be an outbound http, https, or websocket url";
            return false;
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            detail = "rendezvous url must be an outbound http, https, or websocket url";
            return false;
        }

        var tls = uri.Scheme is "https";
        var port = uri.Port > 0 ? uri.Port : tls ? 443 : 80;
        endpoint = new BytePathEndpoint
        {
            Host = uri.Host,
            Port = port,
            Tls = tls,
            TlsServerName = uri.Host,
        };
        return true;
    }
}
