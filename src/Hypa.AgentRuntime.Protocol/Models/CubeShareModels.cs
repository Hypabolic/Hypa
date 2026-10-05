using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Models;

public sealed record CubeShareStartParams
{
    /// <summary>Listener bind address. Default <c>0.0.0.0</c>.</summary>
    [JsonPropertyName("bind")]
    public string? Bind { get; init; }

    /// <summary>Listener port. Default 7443.</summary>
    [JsonPropertyName("port")]
    public int? Port { get; init; }
}

public sealed record CubeShareListenInfo
{
    [JsonPropertyName("bind")]
    public string Bind { get; init; } = "";

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("certificate_sha256")]
    public string? CertificateSha256 { get; init; }

    [JsonPropertyName("quic_listening")]
    public bool QuicListening { get; init; }

    [JsonPropertyName("quic_detail")]
    public string? QuicDetail { get; init; }
}

/// <summary>Result of <c>cube.share.status</c>, <c>cube.share.start</c>, and <c>cube.share.stop</c>.</summary>
public sealed record CubeShareStatusResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;

    /// <summary>Operator intent. Survives a mux restart.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary><c>stopped</c>, <c>starting</c>, <c>running</c>, or <c>retrying</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = "stopped";

    [JsonPropertyName("bind")]
    public string? Bind { get; init; }

    [JsonPropertyName("port")]
    public int? Port { get; init; }

    /// <summary>Present while <c>state</c> is <c>running</c>.</summary>
    [JsonPropertyName("listen")]
    public CubeShareListenInfo? Listen { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("restarts")]
    public int Restarts { get; init; }
}
