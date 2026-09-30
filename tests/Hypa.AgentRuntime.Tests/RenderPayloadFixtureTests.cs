using System.Text;
using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// Live encoder output for one fixed frame equals the published render fixtures.
/// </summary>
public sealed class RenderPayloadFixtureTests
{
    [Fact]
    public void Live_encoder_matches_each_render_fixture()
    {
        var frame = RenderPayloadSamples.Frame();
        var cells = RenderPayloadSamples.CellsJson(frame);
        var snapshot = RenderPayloadSamples.SnapshotJson(frame, "p1");
        var popup = RenderPayloadSamples.PopupSnapshotJson(frame);
        var blit = RenderPayloadSamples.BlitJson(frame);
        var cursor = RenderPayloadSamples.CursorJson(frame);

        Assert.Equal(Load(FixtureCatalog.TerminalRenderCellsPayload), cells);
        Assert.Equal(Load(FixtureCatalog.TerminalRenderSnapshotPayload), snapshot);
        Assert.Equal(Load(FixtureCatalog.TerminalRenderPopupPayload), popup);
        Assert.Equal(Load(FixtureCatalog.TerminalRenderBlitPayload), blit);
        Assert.Equal(Load(FixtureCatalog.TerminalRenderCursorPayload), cursor);

        AssertPayload(FixtureCatalog.EventPath(ProtocolEventTypes.TerminalRender), cells);
        AssertPayload(FixtureCatalog.TerminalRenderSnapshotEvent, snapshot);
        AssertPayload(FixtureCatalog.TerminalRenderPopupEvent, popup);
    }

    [Fact]
    public void Live_event_formatter_matches_each_render_event_fixture()
    {
        var frame = RenderPayloadSamples.Frame();
        AssertEventLine(
            FixtureCatalog.EventPath(ProtocolEventTypes.TerminalRender),
            RenderPayloadSamples.CellsJson(frame),
            "2026-08-12T00:00:02Z");
        AssertEventLine(
            FixtureCatalog.TerminalRenderSnapshotEvent,
            RenderPayloadSamples.SnapshotJson(frame, RenderPayloadSamples.PaneId),
            "2026-08-16T00:00:01Z");
        AssertEventLine(
            FixtureCatalog.TerminalRenderPopupEvent,
            RenderPayloadSamples.PopupSnapshotJson(frame),
            "2026-08-16T00:00:02Z");
    }

    [Fact]
    public void Live_popup_snapshot_omits_pane_id_and_occupant_generation()
    {
        using var doc = JsonDocument.Parse(RenderPayloadSamples.PopupSnapshotJson(RenderPayloadSamples.Frame()));
        var root = doc.RootElement;
        Assert.Equal(ProtocolEventTypes.TerminalRenderTargetPopup, root.GetProperty("target").GetString());
        Assert.False(root.TryGetProperty("pane_id", out _));
        Assert.False(root.TryGetProperty("occupant_generation", out _));
        Assert.False(root.TryGetProperty("patch", out _));
    }

    private static void AssertEventLine(string relativePath, string payloadJson, string occurredAt)
    {
        var record = new RuntimeEventRecord
        {
            Seq = 1,
            Class = EventClass.Render,
            Reliability = EventReliability.Render,
            Type = ProtocolEventTypes.TerminalRender,
            OccurredAt = DateTimeOffset.Parse(occurredAt, System.Globalization.CultureInfo.InvariantCulture),
            PayloadJson = payloadJson,
        };
        var line = Encoding.UTF8.GetString(EventSubscriptionHub.FormatRuntimeEventUtf8(record, "sub_1"));
        Assert.Equal(Load(relativePath), line);
    }

    [Fact]
    public void Schema_fields_match_live_encoder_output()
    {
        var frame = RenderPayloadSamples.Frame();
        var rich = RenderPayloadSamples.RichFrame();
        var samples = new[]
        {
            RenderPayloadSamples.CellsJson(frame),
            RenderPayloadSamples.CellsJson(rich, baseGeneration: 4),
            RenderPayloadSamples.DeltaJson(rich),
            RenderPayloadSamples.CellsJson(RenderPayloadSamples.HiddenCursorFrame()),
            RenderPayloadSamples.SnapshotJson(frame, RenderPayloadSamples.PaneId),
            RenderPayloadSamples.SnapshotJson(frame, ProtocolEventTypes.TerminalRenderTargetPopup),
            RenderPayloadSamples.PatchSnapshotJson(rich),
            RenderPayloadSamples.BlitJson(frame),
            RenderPayloadSamples.BlitOptionalFieldsJson(),
            RenderPayloadSamples.PaneByteFallbackJson(),
            RenderPayloadSamples.PaneByteFallbackWithoutRouteJson(),
            RenderPayloadSamples.PopupByteFallbackJson(),
        };

        RenderSchemaCoverage.AssertCovered(
            ProtocolSchemaCatalog.Create().Render,
            samples);
    }

    private static void AssertPayload(string relativePath, string payloadJson)
    {
        using var doc = JsonDocument.Parse(Load(relativePath));
        var payload = doc.RootElement.GetProperty("params").GetProperty("payload").GetRawText();
        Assert.Equal(payloadJson, payload);
    }

    private static string Load(string relativePath) =>
        FixtureCatalog.Load(relativePath).Trim();
}

internal static class RenderPayloadSamples
{
    public const string PaneId = "p1";
    public const long Generation = 3;
    public const int OccupantGeneration = 2;

    public static VtFrame Frame()
    {
        var link = "https://example.test/compat";
        var row0 = new VtCellView[]
        {
            Cell("H", 1, false, "#FF0000", bold: true, link: null),
            Cell("i", 1, false, null, bold: false, link: null),
            Cell("日", 2, false, null, bold: false, link: null),
            Cell("", 1, true, null, bold: false, link: null),
        };
        var row1 = new VtCellView[]
        {
            Cell("e\u0301", 1, false, null, bold: false, link: null),
            Cell("a", 1, false, null, bold: false, link: link),
            Cell("b", 1, false, null, bold: false, link: link),
            Cell(" ", 1, false, null, bold: false, link: null),
        };
        return new VtFrame(
            PaneId,
            4,
            2,
            [row0, row1],
            new VtFrameCursor(1, 0, true, 2),
            new VtFrameModes(false, true, false, false, "none", false),
            0,
            OccupantGeneration,
            Generation);
    }

    public static string CellsJson(VtFrame frame, long baseGeneration = 0)
    {
        var encoder = new VtCellsEncoder();
        var encoding = encoder.Encode(
            frame,
            baseline: null,
            frame.PaneId,
            frame.Generation,
            baseGeneration,
            frame.OccupantGeneration);
        Assert.NotNull(encoding);
        return Encoding.UTF8.GetString(encoding.Value.PayloadUtf8);
    }

    public static string DeltaJson(VtFrame current)
    {
        var baseline = RichFrame(replaceRow1Col0: "Q");
        var encoder = new VtCellsEncoder();
        var encoding = encoder.Encode(
            current,
            baseline,
            current.PaneId,
            current.Generation,
            baseGeneration: 11,
            current.OccupantGeneration);
        Assert.NotNull(encoding);
        Assert.False(encoding.Value.Payload.Full);
        return Encoding.UTF8.GetString(encoding.Value.PayloadUtf8);
    }

    public static VtFrame RichFrame(string? replaceRow1Col0 = null)
    {
        var styled = new VtCellView(
            "e\u0301",
            1,
            false,
            "#112233",
            "palette:4",
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            "#ABCDEF",
            3,
            Hyperlink: "https://example.test/opt");
        var wide = new VtCellView("日", 2, false, null, null, false, false, false, false, false, false, false);
        var cont = new VtCellView("", 1, true, null, null, false, false, false, false, false, false, false);
        var plain = new VtCellView("Z", 1, false, null, null, false, false, false, false, false, false, false);
        var other = replaceRow1Col0 is null
            ? plain
            : new VtCellView(replaceRow1Col0, 1, false, null, null, false, false, false, false, false, false, false);
        var row0 = new VtCellView[] { styled, wide, cont, plain };
        var row1 = new VtCellView[] { other, plain, plain, plain };
        return new VtFrame(
            PaneId,
            4,
            2,
            [row0, row1],
            new VtFrameCursor(2, 1, true, 4),
            new VtFrameModes(true, true, true, true, "button", true, true, "sgr", true),
            3,
            OccupantGeneration,
            Generation);
    }

    public static VtFrame HiddenCursorFrame()
    {
        var cell = new VtCellView("X", 1, false, null, null, false, false, false, false, false, false, false);
        return new VtFrame(
            PaneId,
            1,
            1,
            [new VtCellView[] { cell }],
            VtFrameCursor.None,
            new VtFrameModes(false, false, false, false, "none", false),
            0,
            OccupantGeneration,
            Generation);
    }

    public static string PatchSnapshotJson(VtFrame frame)
    {
        var snapshot = new VtAttachSnapshot(frame, new VtScrollRegion(1, 3), 1);
        var parts = AttachSnapshotPacker.Pack(
            frame.PaneId,
            snapshot,
            new DefaultEventPayloadRedactor(),
            generation: frame.Generation,
            dirtyRows: [0],
            occupantGeneration: frame.OccupantGeneration);
        var part = Assert.Single(parts);
        return Encoding.UTF8.GetString(part);
    }

    public static string SnapshotJson(VtFrame frame, string paneId)
    {
        var snapshot = new VtAttachSnapshot(frame, new VtScrollRegion(0, 1), 1);
        var parts = AttachSnapshotPacker.Pack(
            paneId,
            snapshot,
            new DefaultEventPayloadRedactor(),
            generation: Generation,
            occupantGeneration: OccupantGeneration);
        var part = Assert.Single(parts);
        return Encoding.UTF8.GetString(part);
    }

    /// <summary>
    /// Same call as the live popup snapshot path: no occupant generation and no dirty rows.
    /// </summary>
    public static string PopupSnapshotJson(VtFrame frame)
    {
        var snapshot = new VtAttachSnapshot(frame, new VtScrollRegion(0, 1), 1);
        var parts = AttachSnapshotPacker.Pack(
            ProtocolEventTypes.TerminalRenderTargetPopup,
            snapshot,
            new DefaultEventPayloadRedactor(),
            generation: Generation);
        var part = Assert.Single(parts);
        return Encoding.UTF8.GetString(part);
    }

    public static string BlitJson(VtFrame frame)
    {
        var encoded = VtBlitEncoder.Encode(frame, baseline: null);
        var payload = new TerminalRenderBlitPayload
        {
            PaneId = PaneId,
            Kind = TerminalRenderBlitPayload.KindBlit,
            Ansi = encoded.Ansi,
            Full = encoded.Full,
            GridCols = frame.Cols,
            GridRows = frame.Rows,
            Generation = Generation,
            OccupantGeneration = OccupantGeneration,
            Reanchor = encoded.Full,
            ChangedCells = encoded.ChangedCells,
        };
        return JsonSerializer.Serialize(payload, ProtocolJsonContext.Default.TerminalRenderBlitPayload);
    }

    public static string BlitOptionalFieldsJson()
    {
        var payload = new TerminalRenderBlitPayload
        {
            PaneId = PaneId,
            Kind = TerminalRenderBlitPayload.KindBlit,
            Ansi = "\u001b[0m",
            Full = true,
            GridCols = 4,
            GridRows = 2,
            Generation = Generation,
            BaseGeneration = 4,
            OccupantGeneration = OccupantGeneration,
            Reanchor = true,
            ChangedCells = 3,
            WireBytes = 12,
        };
        return JsonSerializer.Serialize(payload, ProtocolJsonContext.Default.TerminalRenderBlitPayload);
    }

    public static string PaneByteFallbackJson() =>
        RuntimeEventPayloadJson.WriteTerminalRender(
            PaneId,
            "YQ==",
            1,
            AttachPathTrace.RouteByteFallback);

    public static string PaneByteFallbackWithoutRouteJson() =>
        RuntimeEventPayloadJson.WriteTerminalRender(PaneId, "YQ==", 1);

    public static string PopupByteFallbackJson() =>
        RuntimeEventPayloadJson.WritePopupTerminalRender("YQ==", 1);

    public static string CursorJson(VtFrame frame)
    {
        var cells = JsonSerializer.Deserialize(
            CellsJson(frame),
            ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        Assert.NotNull(cells);
        Assert.NotNull(cells.Cursor);
        return JsonSerializer.Serialize(cells.Cursor, ProtocolJsonContext.Default.TerminalRenderCursorPayload);
    }

    private static VtCellView Cell(string text, int width, bool continuation, string? fg, bool bold, string? link) =>
        new(text, width, continuation, fg, null, bold, false, false, false, false, false, false, Hyperlink: link);
}

internal static class RenderSchemaCoverage
{
    public static void AssertCovered(ProtocolSchemaRenderSection schema, IReadOnlyList<string> samples)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var payload in schema.Payloads)
            seen[payload.Name] = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sample in samples)
        {
            using var doc = JsonDocument.Parse(sample);
            var payload = MatchPayload(doc.RootElement, schema.Payloads);
            Walk(doc.RootElement, payload, schema.Payloads, seen);
        }

        foreach (var payload in schema.Payloads)
        {
            foreach (var field in payload.Fields)
            {
                Assert.Contains(field.Name, seen[payload.Name]);
            }
        }
    }

    private static void Walk(
        JsonElement obj,
        ProtocolSchemaRenderPayloadEntry payload,
        IReadOnlyList<ProtocolSchemaRenderPayloadEntry> payloads,
        Dictionary<string, HashSet<string>> seen)
    {
        Assert.Equal(JsonValueKind.Object, obj.ValueKind);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in obj.EnumerateObject())
        {
            var field = Assert.Single(payload.Fields, f => f.Name == prop.Name);
            present.Add(prop.Name);
            seen[payload.Name].Add(prop.Name);
            Assert.True(
                KindMatches(field.Type, prop.Value.ValueKind),
                $"{payload.Name}.{field.Name} kind {prop.Value.ValueKind} is not {field.Type}");
            Assert.False(
                OmitConditionHolds(field.OmitWhen, prop.Value),
                $"{payload.Name}.{field.Name} is present while omit_when is {field.OmitWhen}");
            var nested = NestedPayload(field.Type, payloads);
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                Assert.NotNull(nested);
                Walk(prop.Value, nested, payloads, seen);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                WalkArray(prop.Value, field.Type, nested, payloads, seen);
            }
        }

        foreach (var field in payload.Fields)
        {
            if (present.Contains(field.Name))
                continue;

            Assert.False(
                string.IsNullOrEmpty(field.OmitWhen),
                $"{payload.Name}.{field.Name} is absent and has no omit_when");
        }
    }

    private static void WalkArray(
        JsonElement array,
        string type,
        ProtocolSchemaRenderPayloadEntry? nested,
        IReadOnlyList<ProtocolSchemaRenderPayloadEntry> payloads,
        Dictionary<string, HashSet<string>> seen)
    {
        if (type.EndsWith("[][]", StringComparison.Ordinal))
        {
            Assert.NotNull(nested);
            foreach (var row in array.EnumerateArray())
            {
                Assert.Equal(JsonValueKind.Array, row.ValueKind);
                foreach (var cell in row.EnumerateArray())
                {
                    Assert.Equal(JsonValueKind.Object, cell.ValueKind);
                    Walk(cell, nested, payloads, seen);
                }
            }

            return;
        }

        var elementType = type[..^2];
        foreach (var item in array.EnumerateArray())
        {
            Assert.True(
                KindMatches(elementType, item.ValueKind),
                $"{elementType} element kind {item.ValueKind} is not {elementType}");
            if (item.ValueKind == JsonValueKind.Object)
            {
                Assert.NotNull(nested);
                Walk(item, nested, payloads, seen);
            }
        }
    }

    private static bool KindMatches(string type, JsonValueKind kind)
    {
        if (type is "string")
            return kind == JsonValueKind.String;
        if (type is "bool")
            return kind is JsonValueKind.True or JsonValueKind.False;
        if (type is "int" or "uint")
            return kind == JsonValueKind.Number;
        if (type.EndsWith("[]", StringComparison.Ordinal))
            return kind == JsonValueKind.Array;

        return kind == JsonValueKind.Object;
    }

    private static bool OmitConditionHolds(string? omitWhen, JsonElement value) =>
        omitWhen switch
        {
            "null" => value.ValueKind == JsonValueKind.Null,
            "false" => value.ValueKind == JsonValueKind.False,
            "0" => value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var number)
                && number == 0,
            _ => false,
        };

    private static ProtocolSchemaRenderPayloadEntry MatchPayload(
        JsonElement root,
        IReadOnlyList<ProtocolSchemaRenderPayloadEntry> payloads)
    {
        if (root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
            return Assert.Single(payloads, p => p.Kind == kind.GetString());

        if (root.TryGetProperty("target", out var target)
            && target.ValueKind == JsonValueKind.String
            && target.GetString() == ProtocolEventTypes.TerminalRenderTargetPopup)
        {
            return Assert.Single(payloads, p => p.Name == "popup_byte_fallback");
        }

        return Assert.Single(payloads, p => p.Name == "byte_fallback");
    }

    private static ProtocolSchemaRenderPayloadEntry? NestedPayload(
        string type,
        IReadOnlyList<ProtocolSchemaRenderPayloadEntry> payloads)
    {
        var name = type;
        if (name.EndsWith("[][]", StringComparison.Ordinal))
            name = name[..^4];
        else if (name.EndsWith("[]", StringComparison.Ordinal))
            name = name[..^2];

        foreach (var payload in payloads)
        {
            if (string.Equals(payload.Name, name, StringComparison.Ordinal))
                return payload;
        }

        return null;
    }
}
