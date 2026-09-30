using System.Text.Json.Serialization;

namespace Hypa.AgentRuntime.Protocol.Envelopes;

/// <summary>
/// Wire error object: <c>{code,message,data:{retryable,request_id}}</c>.
/// Messages never include tokens, env values, or raw terminal bytes.
/// </summary>
public sealed record RpcError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public RpcErrorData? Data { get; init; }
}

/// <summary>Optional error data per Appendix C.</summary>
public sealed record RpcErrorData
{
    [JsonPropertyName("retryable")]
    public bool? Retryable { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    /// <summary>Retention floor when <c>error_code</c> is <c>cursor_expired</c>.</summary>
    [JsonPropertyName("floor_seq")]
    public long? FloorSeq { get; init; }
}
