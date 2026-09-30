namespace Hypa.Connectivity.Domain;

/// <summary>Stable Connectivity reasons. Wire as lowercase snake strings.</summary>
public static class ConnectivityReasons
{
    public const string JoinDenied = "join_denied";
    public const string JoinExpired = "join_expired";
    public const string PeerUnavailable = "peer_unavailable";
    public const string StreamReset = "stream_reset";
    public const string TransferIncomplete = "transfer_incomplete";
    public const string UnknownCommit = "unknown_commit";
    public const string BootstrapInvalid = "bootstrap_invalid";
    public const string RendezvousUrlInvalid = "rendezvous_url_invalid";
    public const string Unauthorized = "unauthorized";
    public const string ApplicationEncryptionRequired = "application_encryption_required";
    public const string Internal = "internal";
    public const string QuicUnsupported = "quic_unsupported";
    public const string InviteInvalid = "invite_invalid";
    public const string InviteExpired = "invite_expired";
    public const string InviteConsumed = "invite_consumed";
    public const string InviteRateLimited = "invite_rate_limited";
}

/// <summary>Expected Connectivity result. Exceptions stay for programmer errors.</summary>
public sealed record ConnectivityOutcome
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public string? Stage { get; init; }
    public string? AttemptId { get; init; }
    public bool? Retryable { get; init; }

    public static ConnectivityOutcome Success() => new() { Ok = true };

    public static ConnectivityOutcome Failure(
        string reason,
        string detail,
        string? stage = null,
        string? attemptId = null,
        bool? retryable = null) =>
        new()
        {
            Ok = false,
            Reason = reason,
            Detail = detail,
            Stage = stage,
            AttemptId = attemptId,
            Retryable = retryable,
        };
}

public sealed record ConnectivityOutcome<T>
{
    public required bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public string? Stage { get; init; }
    public string? AttemptId { get; init; }
    public bool? Retryable { get; init; }
    public T? Value { get; init; }

    public static ConnectivityOutcome<T> Success(T value) => new() { Ok = true, Value = value };

    public static ConnectivityOutcome<T> Failure(
        string reason,
        string detail,
        string? stage = null,
        string? attemptId = null,
        bool? retryable = null) =>
        new()
        {
            Ok = false,
            Reason = reason,
            Detail = detail,
            Stage = stage,
            AttemptId = attemptId,
            Retryable = retryable,
        };

    public ConnectivityOutcome WithoutValue() =>
        Ok
            ? ConnectivityOutcome.Success()
            : new ConnectivityOutcome
            {
                Ok = false,
                Reason = Reason ?? ConnectivityReasons.Internal,
                Detail = Detail ?? "",
                Stage = Stage,
                AttemptId = AttemptId,
                Retryable = Retryable,
            };
}
