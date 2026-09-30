namespace Hypa.AgentRuntime.Application;

/// <summary>
/// One JSON Lines process-log record. Not a <c>runtime.event</c>.
/// Written with a hand-written <c>Utf8JsonWriter</c>. Keep this type out of
/// <c>ProtocolJsonContext</c>.
/// </summary>
public sealed record ProcessLogRecord
{
    public required string Event { get; init; }

    public required string Subsystem { get; init; }

    public required string Outcome { get; init; }

    public DateTimeOffset Ts { get; init; }

    public int Pid { get; init; }

    public ProcessLogLevel Level { get; init; } = ProcessLogLevel.Information;

    public string? SessionId { get; init; }

    public string? RequestId { get; init; }

    public string? Method { get; init; }

    public bool? ChangesUi { get; init; }

    public string? AttachClientId { get; init; }

    public string? SubscriptionId { get; init; }

    public string? Err { get; init; }

    public long? Seq { get; init; }

    public string? PaneId { get; init; }

    public string? TabId { get; init; }

    public string? WorkspaceId { get; init; }

    public string? PreviousTabId { get; init; }

    public string? CurrentTabId { get; init; }

    public string? Surface { get; init; }

    public string? SettingKey { get; init; }

    public string? SettingValue { get; init; }

    public string? ValueKind { get; init; }

    public string? Digest { get; init; }

    public long? DropCount { get; init; }

    public int? QueueDepth { get; init; }

    public string? DisconnectReason { get; init; }

    public string? Action { get; init; }

    public string? Source { get; init; }

    public string? Stage { get; init; }

    public long? ElapsedMs { get; init; }

    public long? StageMs { get; init; }

    public string? EndpointId { get; init; }

    public string? Transport { get; init; }

    public string? EventType { get; init; }

    public string? Agent { get; init; }

    public bool AgentSpecified { get; init; }

    public string? AgentStatus { get; init; }

    public string? PreviousStatus { get; init; }

    public string? Reason { get; init; }
}
