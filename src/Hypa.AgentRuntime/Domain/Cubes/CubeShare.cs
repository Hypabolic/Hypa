namespace Hypa.AgentRuntime.Domain.Cubes;

/// <summary>Wire names for <see cref="CubeShareStatus.State"/>.</summary>
public static class CubeShareStates
{
    /// <summary>Share is off. No listener runs.</summary>
    public const string Stopped = "stopped";

    /// <summary>The mux is starting the listener.</summary>
    public const string Starting = "starting";

    /// <summary>The listener accepts direct joins.</summary>
    public const string Running = "running";

    /// <summary>The listener exited or failed to start. The mux starts it again.</summary>
    public const string Retrying = "retrying";
}

/// <summary>Where the mux share listener binds.</summary>
public sealed record CubeShareSettings
{
    public const string DefaultBind = "0.0.0.0";
    public const int DefaultPort = 7443;

    public string Bind { get; init; } = DefaultBind;

    public int Port { get; init; } = DefaultPort;
}

/// <summary>What the listener reported once it was ready.</summary>
public sealed record CubeShareListen
{
    public required string Bind { get; init; }

    public required int Port { get; init; }

    public string? CertificateSha256 { get; init; }

    public bool QuicListening { get; init; }

    public string? QuicDetail { get; init; }

    /// <summary>Pairing store directory the listener uses. Invites go in this store.</summary>
    public string? PairingStore { get; init; }
}

/// <summary>
/// Mux share state. <see cref="Enabled"/> is the operator intent and survives
/// a mux restart. <see cref="State"/> is what the listener is doing now.
/// </summary>
public sealed record CubeShareStatus
{
    public bool Enabled { get; init; }

    public string State { get; init; } = CubeShareStates.Stopped;

    public CubeShareSettings? Settings { get; init; }

    public CubeShareListen? Listen { get; init; }

    /// <summary>Why the last start failed or the listener exited.</summary>
    public string? Error { get; init; }

    /// <summary>Listener starts after the first one since share was enabled.</summary>
    public int Restarts { get; init; }

    /// <summary>Why share.json could not be written or removed.</summary>
    public string? PersistError { get; init; }

    public static CubeShareStatus Stopped { get; } = new();
}
