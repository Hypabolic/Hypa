namespace Hypa.Connectivity.Domain;

/// <summary>Wire names for relay-failure stages. Lowercase snake strings.</summary>
public static class RelayFailureStageWire
{
    public const string Join = "join";
    public const string Attach = "attach";
    public const string Pulling = "pulling";
    public const string Applying = "applying";
    public const string Starting = "starting";
    public const string Probing = "probing";
    public const string Fencing = "fencing";
}

/// <summary>Dest confirmation seen by the caller after a relay loss.</summary>
public static class DestConfirmationWire
{
    public const string Unconfirmed = "unconfirmed";
    public const string Unknown = "unknown";
    public const string Confirmed = "confirmed";
    public const string SplitBrain = "split_brain";
}

/// <summary>Source Run status after a relay loss.</summary>
public static class SourceStatusWire
{
    public const string Active = "active";
    public const string Fencing = "fencing";
    public const string Dead = "dead";
}

/// <summary>Join and attach attempt ids. Distinct from Work id and join nonce.</summary>
public static class AttemptIds
{
    public static string New() => "att_" + Guid.NewGuid().ToString("N");

    public static string FromJoinNonce(JoinNonce nonce) => "att_" + nonce.Value;
}

/// <summary>
/// Relay-failure report. Unknown dest confirmation is never success.
/// </summary>
public sealed record RelayFailure
{
    public required bool Ok { get; init; }
    public required string Reason { get; init; }
    public required string Detail { get; init; }
    public required string Stage { get; init; }
    public required string AttemptId { get; init; }
    public required bool Retryable { get; init; }
    public string? SourceStatus { get; init; }
    public string? DestConfirmation { get; init; }
}

/// <summary>
/// Attach drop after a relay loss. The destination mux stays up.
/// </summary>
public sealed record AttachDropResult
{
    public required bool Ok { get; init; }
    public required string Reason { get; init; }
    public required string Detail { get; init; }
    public required string Stage { get; init; }
    public required string AttemptId { get; init; }
    public required bool Retryable { get; init; }
    public required bool MuxRemainsUp { get; init; }
    public required bool ClientDisconnected { get; init; }
    public required bool FreshAuthorizedAttachAllowed { get; init; }
    public required string OperatorCopy { get; init; }
}
