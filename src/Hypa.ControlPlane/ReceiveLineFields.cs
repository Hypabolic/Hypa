using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;

namespace Hypa.ControlPlane;

/// <summary>The <c>params.payload.kind</c> values that select a receive queue.</summary>
internal enum ReceivePayloadKind : byte
{
    /// <summary>No kind, or a kind that is not a string.</summary>
    None,
    Other,
    Cells,
    Blit,
    Snapshot,
}

/// <summary>
/// The fields of one received NDJSON line that select its receive queue.
/// <see cref="Read"/> reads them with one <see cref="Utf8JsonReader"/> pass
/// over the line bytes. It does not make a <see cref="JsonDocument"/>.
/// <para>
/// The order of the properties has no effect. The reader reads the top-level
/// <c>event</c>, <c>id</c> and <c>params</c>, then <c>params.lane</c>,
/// <c>params.reliability</c>, <c>params.type</c> and <c>params.payload</c>,
/// then <c>pane_id</c>, <c>kind</c>, <c>full</c>, <c>reanchor</c>,
/// <c>base_generation</c>, <c>complete</c> and <c>patch</c> in the payload.
/// When a property occurs two times, the last one wins, as for
/// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>.
/// </para>
/// <para>
/// The reader always reads to the end of the line, also when
/// <c>params.lane</c> is present. Thus a line that is not valid JSON throws
/// <see cref="JsonException"/>, as <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/>
/// did, and the connection fails closed. The pane fields are also necessary
/// for the attach receive policy.
/// </para>
/// </summary>
internal struct ReceiveLineFields
{
    /// <summary>The root value is a JSON object.</summary>
    public bool IsObject { get; private set; }

    /// <summary>The root has an <c>event</c> property.</summary>
    public bool HasEvent { get; private set; }

    /// <summary>
    /// The root <c>id</c>: the text of a string id, or the raw text of a
    /// number id. Null when there is no id or the id is another kind.
    /// </summary>
    public string? Id { get; private set; }

    /// <summary><c>params</c> is an object.</summary>
    public bool HasParams { get; private set; }

    /// <summary><c>params.lane</c> when it is one of the <see cref="WriterLaneNames"/>. Else null.</summary>
    public string? LaneField { get; private set; }

    /// <summary><c>params.reliability</c> when it is one of the <see cref="EventReliabilityMap"/> tokens. Else null.</summary>
    public string? Reliability { get; private set; }

    /// <summary>
    /// <c>params.type</c> when it is <see cref="ProtocolEventTypes.TerminalRender"/>
    /// or a pane state type of <see cref="ClientEventQueue.PaneStateEventTypes"/>. Else null.
    /// </summary>
    public string? Type { get; private set; }

    /// <summary><c>params.payload</c> is an object.</summary>
    public bool HasPayload { get; private set; }

    /// <summary>A <c>params.payload.pane_id</c> string that is not empty. Else null.</summary>
    public string? PaneId { get; private set; }

    public ReceivePayloadKind Kind { get; private set; }

    /// <summary><c>full</c> is <c>true</c>.</summary>
    public bool Full { get; private set; }

    /// <summary><c>reanchor</c> is <c>true</c>.</summary>
    public bool Reanchor { get; private set; }

    /// <summary><c>base_generation</c> is an integer above zero.</summary>
    public bool Chained { get; private set; }

    /// <summary><c>complete</c> is <c>false</c>.</summary>
    public bool Incomplete { get; private set; }

    /// <summary><c>patch</c> is <c>true</c>.</summary>
    public bool Patch { get; private set; }

    private static readonly string[] KnownTypes =
        [ProtocolEventTypes.TerminalRender, .. ClientEventQueue.PaneStateEventTypes];

    /// <summary>
    /// The receive queue of the line, as a <see cref="WriterLaneNames"/> value.
    /// <c>params.lane</c> selects it when present. Else a render line (by
    /// reliability or type) goes to the ordered lane when its payload is a
    /// full, reanchor, chained or non-final frame, and to the latest-wins
    /// render slot when it is not. Every other line is reliable.
    /// </summary>
    public readonly string Lane
    {
        get
        {
            if (!HasParams)
                return WriterLaneNames.Control;
            if (LaneField is { } lane)
                return lane;
            if (Reliability is EventReliabilityMap.Reliable or EventReliabilityMap.Output)
                return WriterLaneNames.Control;

            var isRender = Reliability is EventReliabilityMap.Render
                || Type is ProtocolEventTypes.TerminalRender;
            if (!isRender)
                return WriterLaneNames.Control;
            if (!HasPayload)
                return WriterLaneNames.Ordered;
            return IsOrderedPayload ? WriterLaneNames.Ordered : WriterLaneNames.Render;
        }
    }

    /// <summary>
    /// True when the render payload must stay in arrival order. A cells
    /// delta, a blit and a complete snapshot patch can go to the latest-wins
    /// slot. Another kind stays ordered.
    /// </summary>
    public readonly bool IsOrderedPayload => Kind switch
    {
        ReceivePayloadKind.Cells => Full || Reanchor || Chained,
        ReceivePayloadKind.Blit => Full || Reanchor,
        // Non-final slices and complete Full batches stay ordered.
        ReceivePayloadKind.Snapshot => Incomplete || !Patch,
        _ => true,
    };

    /// <summary>A pane state event of <see cref="PaneId"/> (scroll, status, metadata, match or bell).</summary>
    public readonly bool IsPaneState =>
        Type is not null and not ProtocolEventTypes.TerminalRender
        && PaneId is not null;

    /// <summary>
    /// Reads the fields of one line. Throws <see cref="JsonException"/> when
    /// the line is not one valid JSON value.
    /// </summary>
    public static ReceiveLineFields Read(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, isFinalBlock: true, state: default);
        var fields = default(ReceiveLineFields);
        reader.Read();
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            fields.IsObject = true;
            fields.ReadRoot(ref reader);
        }
        else
        {
            reader.Skip();
        }

        // A second value after the root throws in Read.
        if (reader.Read())
            throw new JsonException("The line has more than one JSON value.");
        return fields;
    }

    private void ReadRoot(ref Utf8JsonReader reader)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("event"u8))
            {
                HasEvent = true;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                Id = reader.TokenType switch
                {
                    JsonTokenType.String => reader.GetString(),
                    JsonTokenType.Number => Encoding.UTF8.GetString(reader.ValueSpan),
                    _ => null,
                };
                reader.Skip();
            }
            else if (reader.ValueTextEquals("params"u8))
            {
                reader.Read();
                ResetParams();
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    HasParams = true;
                    ReadParams(ref reader);
                }
                else
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private void ReadParams(ref Utf8JsonReader reader)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("lane"u8))
            {
                reader.Read();
                LaneField = reader.TokenType == JsonTokenType.String
                    ? Match(ref reader, WriterLaneNames.Ordered, WriterLaneNames.Render, WriterLaneNames.Control)
                    : null;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("reliability"u8))
            {
                reader.Read();
                Reliability = reader.TokenType == JsonTokenType.String
                    ? Match(ref reader, EventReliabilityMap.Reliable, EventReliabilityMap.Output, EventReliabilityMap.Render)
                    : null;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                Type = reader.TokenType == JsonTokenType.String ? MatchKnownType(ref reader) : null;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("payload"u8))
            {
                reader.Read();
                ResetPayload();
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    HasPayload = true;
                    ReadPayload(ref reader);
                }
                else
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private void ReadPayload(ref Utf8JsonReader reader)
    {
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("pane_id"u8))
            {
                reader.Read();
                PaneId = reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } pane
                    ? pane
                    : null;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("kind"u8))
            {
                reader.Read();
                Kind = reader.TokenType != JsonTokenType.String
                    ? ReceivePayloadKind.None
                    : reader.ValueTextEquals(TerminalRenderCellsPayload.KindCells)
                        ? ReceivePayloadKind.Cells
                        : reader.ValueTextEquals(TerminalRenderBlitPayload.KindBlit)
                            ? ReceivePayloadKind.Blit
                            : reader.ValueTextEquals(TerminalRenderSnapshotPayload.KindSnapshot)
                                ? ReceivePayloadKind.Snapshot
                                : ReceivePayloadKind.Other;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("full"u8))
            {
                reader.Read();
                Full = reader.TokenType == JsonTokenType.True;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("reanchor"u8))
            {
                reader.Read();
                Reanchor = reader.TokenType == JsonTokenType.True;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("base_generation"u8))
            {
                reader.Read();
                Chained = reader.TokenType == JsonTokenType.Number
                    && reader.TryGetInt64(out var baseGeneration)
                    && baseGeneration > 0;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("complete"u8))
            {
                reader.Read();
                Incomplete = reader.TokenType == JsonTokenType.False;
                reader.Skip();
            }
            else if (reader.ValueTextEquals("patch"u8))
            {
                reader.Read();
                Patch = reader.TokenType == JsonTokenType.True;
                reader.Skip();
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private void ResetParams()
    {
        HasParams = false;
        LaneField = null;
        Reliability = null;
        Type = null;
        ResetPayload();
    }

    private void ResetPayload()
    {
        HasPayload = false;
        PaneId = null;
        Kind = ReceivePayloadKind.None;
        Full = false;
        Reanchor = false;
        Chained = false;
        Incomplete = false;
        Patch = false;
    }

    private static string? Match(ref Utf8JsonReader reader, string first, string second, string third)
    {
        if (reader.ValueTextEquals(first))
            return first;
        if (reader.ValueTextEquals(second))
            return second;
        return reader.ValueTextEquals(third) ? third : null;
    }

    private static string? MatchKnownType(ref Utf8JsonReader reader)
    {
        foreach (var type in KnownTypes)
        {
            if (reader.ValueTextEquals(type))
                return type;
        }

        return null;
    }
}
