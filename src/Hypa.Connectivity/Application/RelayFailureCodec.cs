using System.Text.Json;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

public sealed record RelayFailureDocument
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public string? Stage { get; init; }
    public string? AttemptId { get; init; }
    public bool? Retryable { get; init; }
    public string? SourceStatus { get; init; }
    public string? DestConfirmation { get; init; }
}

public sealed record AttachDropDocument
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
    public string? Stage { get; init; }
    public string? AttemptId { get; init; }
    public bool? Retryable { get; init; }
    public bool? MuxRemainsUp { get; init; }
    public bool? ClientDisconnected { get; init; }
    public bool? FreshAttachAllowed { get; init; }
    public string? OperatorCopy { get; init; }
}

/// <summary>Map relay-failure records through source-generated JSON.</summary>
public static class RelayFailureCodec
{
    public static RelayFailureDocument ToDocument(RelayFailure failure) =>
        new()
        {
            Ok = false,
            Reason = failure.Reason,
            Detail = failure.Detail,
            Stage = failure.Stage,
            AttemptId = failure.AttemptId,
            Retryable = failure.Retryable,
            SourceStatus = failure.SourceStatus,
            DestConfirmation = failure.DestConfirmation,
        };

    public static AttachDropDocument ToDocument(AttachDropResult drop) =>
        new()
        {
            Ok = false,
            Reason = drop.Reason,
            Detail = drop.Detail,
            Stage = drop.Stage,
            AttemptId = drop.AttemptId,
            Retryable = drop.Retryable,
            MuxRemainsUp = drop.MuxRemainsUp,
            ClientDisconnected = drop.ClientDisconnected,
            FreshAttachAllowed = drop.FreshAuthorizedAttachAllowed,
            OperatorCopy = drop.OperatorCopy,
        };

    public static string Write(RelayFailure failure) =>
        JsonSerializer.Serialize(ToDocument(failure), ConnectivityJsonContext.Default.RelayFailureDocument);

    public static string Write(AttachDropResult drop) =>
        JsonSerializer.Serialize(ToDocument(drop), ConnectivityJsonContext.Default.AttachDropDocument);
}
