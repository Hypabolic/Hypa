using System.Text.Json;
using Hypa.Connectivity.Domain;

namespace Hypa.Connectivity.Application;

/// <summary>
/// Map attach reconnect request and offer through source-generated JSON.
/// </summary>
public static class AttachReconnectCodec
{
    public const string RequestType = "attach.reconnect";
    public const string OfferType = "attach.reconnect.offer";

    public static AttachReconnectRequestDocument ToDocument(AttachReconnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var last = new ChannelCursorDocument[request.LastReceived.Count];
        for (var i = 0; i < request.LastReceived.Count; i++)
        {
            var cursor = request.LastReceived[i];
            last[i] = new ChannelCursorDocument
            {
                ChannelId = cursor.ChannelId,
                LastReceivedSequence = cursor.LastReceivedSequence,
                Unavailable = cursor.Unavailable,
                AttemptId = cursor.AttemptId,
                PlacementId = cursor.PlacementId,
                DeviceId = cursor.DeviceId,
                Role = cursor.Role,
            };
        }

        return new AttachReconnectRequestDocument
        {
            Type = RequestType,
            PreviousAttemptId = request.PreviousAttemptId.Value,
            AttemptId = request.AttemptId.Value,
            Capability = JoinBootstrapCodec.ToDocument(request.Capability),
            LastReceived = last,
        };
    }

    public static AttachReconnectOfferDocument ToDocument(AttachReconnectOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return new AttachReconnectOfferDocument
        {
            Type = OfferType,
            AttemptId = offer.AttemptId.Value,
            SnapshotPaint = offer.SnapshotPaint,
            InputLeaseRequired = offer.InputLeaseRequired,
            ReplayFrameCount = offer.ReplayFrames.Count,
        };
    }

    public static string Write(AttachReconnectRequest request) =>
        JsonSerializer.Serialize(
            ToDocument(request),
            ConnectivityJsonContext.Default.AttachReconnectRequestDocument);

    public static string Write(AttachReconnectOffer offer) =>
        JsonSerializer.Serialize(
            ToDocument(offer),
            ConnectivityJsonContext.Default.AttachReconnectOfferDocument);

    public static ConnectivityOutcome<AttachReconnectRequest> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ConnectivityOutcome<AttachReconnectRequest>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "reconnect json is required");
        }

        AttachReconnectRequestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(
                json,
                ConnectivityJsonContext.Default.AttachReconnectRequestDocument);
        }
        catch (JsonException)
        {
            return ConnectivityOutcome<AttachReconnectRequest>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "reconnect json is invalid");
        }

        if (document is null)
        {
            return ConnectivityOutcome<AttachReconnectRequest>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "reconnect json is required");
        }

        return FromDocument(document);
    }

    public static ConnectivityOutcome<AttachReconnectOffer> ReadOffer(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "reconnect offer json is required");
        }

        AttachReconnectOfferDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(
                json,
                ConnectivityJsonContext.Default.AttachReconnectOfferDocument);
        }
        catch (JsonException)
        {
            return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "reconnect offer json is invalid");
        }

        if (document is null
            || !string.Equals(document.Type, OfferType, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(document.AttemptId))
        {
            return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "reconnect offer json is invalid");
        }

        if (!AttachAttemptId.TryParse(document.AttemptId, out var attemptId))
        {
            return ConnectivityOutcome<AttachReconnectOffer>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "attach attempt id is invalid");
        }

        var replayCount = Math.Max(0, document.ReplayFrameCount);
        var placeholders = new StreamFrame[replayCount];
        for (var i = 0; i < placeholders.Length; i++)
        {
            placeholders[i] = StreamFrame.Control(
                StreamDirection.MuxToClient,
                0,
                ReadOnlyMemory<byte>.Empty);
        }

        return ConnectivityOutcome<AttachReconnectOffer>.Success(new AttachReconnectOffer
        {
            AttemptId = attemptId,
            ReplayFrames = placeholders,
            SnapshotPaint = document.SnapshotPaint,
            InputLeaseRequired = document.InputLeaseRequired,
        });
    }

    public static ConnectivityOutcome<AttachReconnectRequest> FromDocument(
        AttachReconnectRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(document.Type, RequestType, StringComparison.Ordinal))
        {
            return ConnectivityOutcome<AttachReconnectRequest>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "reconnect type is invalid");
        }

        if (!AttachAttemptId.TryParse(document.PreviousAttemptId, out var previous))
        {
            return ConnectivityOutcome<AttachReconnectRequest>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "previous attach attempt id is invalid");
        }

        if (!AttachAttemptId.TryParse(document.AttemptId, out var attempt))
        {
            return ConnectivityOutcome<AttachReconnectRequest>.Failure(
                ConnectivityReasons.BootstrapInvalid,
                "attach attempt id is invalid");
        }

        var capability = JoinBootstrapCodec.FromCapabilityDocument(document.Capability);
        if (!capability.Ok || capability.Value is null)
        {
            return ConnectivityOutcome<AttachReconnectRequest>.Failure(
                capability.Reason ?? ConnectivityReasons.BootstrapInvalid,
                capability.Detail ?? "capability is required");
        }

        var last = new ChannelCursor[document.LastReceived.Length];
        for (var i = 0; i < document.LastReceived.Length; i++)
        {
            var cursor = document.LastReceived[i];
            last[i] = new ChannelCursor
            {
                ChannelId = cursor.ChannelId,
                LastReceivedSequence = cursor.LastReceivedSequence,
                Unavailable = cursor.Unavailable,
                AttemptId = cursor.AttemptId ?? "",
                PlacementId = cursor.PlacementId ?? "",
                DeviceId = cursor.DeviceId ?? "",
                Role = cursor.Role,
            };
        }

        return ConnectivityOutcome<AttachReconnectRequest>.Success(new AttachReconnectRequest
        {
            PreviousAttemptId = previous,
            AttemptId = attempt,
            Capability = capability.Value,
            LastReceived = last,
        });
    }
}
