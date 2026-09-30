namespace Hypa.AgentRuntime.Protocol;

/// <summary>
/// Error catalog: code → stable symbol. Messages never include tokens,
/// env values, or raw terminal bytes (Appendix C).
/// Wire shape: <c>{code,message,data:{retryable,request_id}}</c>.
/// </summary>
public static class ProtocolErrors
{
    public const string ParseError = "parse_error";
    public const string InvalidRequest = "invalid_request";
    public const string MethodNotFound = "method_not_found";
    public const string InvalidParams = "invalid_params";
    public const string InternalError = "internal_error";
    public const string ServerShuttingDown = "server_shutting_down";
    public const string NotFound = "not_found";
    public const string InvalidState = "invalid_state";
    public const string LeaseRequired = "lease_required";
    public const string LeaseExpired = "lease_expired";
    public const string OccupantReplaced = "occupant_replaced";
    public const string CapabilityInvalid = "capability_invalid";
    public const string PaneStartFailed = "pane_start_failed";
    public const string PersistenceUnavailable = "persistence_unavailable";
    public const string ReplayIncomplete = "replay_incomplete";
    public const string CheckpointConflict = "checkpoint_conflict";
    public const string TransferIncomplete = "transfer_incomplete";
    public const string RemoteUnavailable = "remote_unavailable";
    public const string PolicyUnavailable = "policy_unavailable";
    public const string Fenced = "fenced";
    public const string CapabilityMissing = "capability_missing";
    public const string CursorExpired = "cursor_expired";

    /// <summary>Complete Appendix C catalog ordered by code (ascending magnitude of negative codes).</summary>
    public static IReadOnlyList<ProtocolErrorEntry> Catalog { get; } =
    [
        new(ProtocolErrorCodes.ParseError, ParseError, "request line is not valid JSON/NDJSON"),
        new(ProtocolErrorCodes.InvalidRequest, InvalidRequest, "malformed NDJSON/RPC envelope"),
        new(ProtocolErrorCodes.MethodNotFound, MethodNotFound, "method not in negotiated capability set"),
        new(ProtocolErrorCodes.InvalidParams, InvalidParams, "missing/invalid field"),
        new(ProtocolErrorCodes.InternalError, InternalError, "handler failed"),
        new(ProtocolErrorCodes.ServerShuttingDown, ServerShuttingDown, "admission closed"),
        new(ProtocolErrorCodes.NotFound, NotFound, "session/workspace/pane/occupant missing"),
        new(ProtocolErrorCodes.InvalidState, InvalidState, "operation invalid for lifecycle state"),
        new(ProtocolErrorCodes.LeaseRequired, LeaseRequired, "input/resize/admin lease absent"),
        new(ProtocolErrorCodes.LeaseExpired, LeaseExpired, "lease expired before admission"),
        new(ProtocolErrorCodes.OccupantReplaced, OccupantReplaced, "pinned wait target no longer exists"),
        new(ProtocolErrorCodes.CapabilityInvalid, CapabilityInvalid, "signature/audience/tenant/run/session/nonce failure"),
        new(ProtocolErrorCodes.PaneStartFailed, PaneStartFailed, "child/provider start failure"),
        new(ProtocolErrorCodes.PersistenceUnavailable, PersistenceUnavailable, "migration/journal/disk failure"),
        new(ProtocolErrorCodes.ReplayIncomplete, ReplayIncomplete, "journal ended before valid footer"),
        new(ProtocolErrorCodes.CheckpointConflict, CheckpointConflict, "writes occurred after quiescent barrier"),
        new(ProtocolErrorCodes.TransferIncomplete, TransferIncomplete, "file/submodule/LFS transfer cannot be reproduced"),
        new(ProtocolErrorCodes.RemoteUnavailable, RemoteUnavailable, "gateway/provider/runtime unavailable"),
        new(ProtocolErrorCodes.PolicyUnavailable, PolicyUnavailable, "governed fail-closed policy could not be evaluated"),
        new(ProtocolErrorCodes.Fenced, Fenced, "stale Work generation is not entitled to mutate"),
    ];

    private static readonly Dictionary<int, ProtocolErrorEntry> ByCode =
        Catalog.ToDictionary(e => e.Code);

    private static readonly Dictionary<string, ProtocolErrorEntry> BySymbol =
        Catalog.ToDictionary(e => e.Symbol, StringComparer.Ordinal);

    public static bool TryGet(int code, out ProtocolErrorEntry? entry) =>
        ByCode.TryGetValue(code, out entry);

    public static bool TryGetBySymbol(string symbol, out ProtocolErrorEntry? entry) =>
        BySymbol.TryGetValue(symbol, out entry);

    public static string? SymbolOf(int code) =>
        ByCode.TryGetValue(code, out var e) ? e.Symbol : null;

    /// <summary>Catalog meaning for a code, or <see cref="InternalError"/> when unknown.</summary>
    public static string MeaningOf(int code) =>
        ByCode.TryGetValue(code, out var e) ? e.Meaning : "handler failed";

    /// <summary>Appendix C retryable policy. Handler failures are not retryable.</summary>
    public static bool IsRetryable(int code) => false;
}

/// <summary>One row of the Appendix C error catalog.</summary>
public sealed record ProtocolErrorEntry(int Code, string Symbol, string Meaning);
