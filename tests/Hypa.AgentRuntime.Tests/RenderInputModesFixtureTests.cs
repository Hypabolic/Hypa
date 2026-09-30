using System.Text;
using System.Text.Json;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Terminal.Vt;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class RenderInputModesFixtureTests
{
    [Fact]
    public void Snapshot_with_both_flags_round_trips_through_parse()
    {
        var parsed = AttachSnapshotPacker.ParseSnapshotJson("pane_1", BothFlagsSnapshotJson());
        Assert.True(parsed.Frame.Modes.ApplicationCursor);
        Assert.True(parsed.Frame.Modes.BracketedPaste);
    }

    [Fact]
    public void Schema_lists_the_input_mode_fields()
    {
        var document = ProtocolSchemaCatalog.Create();
        Assert.NotNull(document.Render);
        var cells = Assert.Single(document.Render.Payloads, p => p.Kind == TerminalRenderCellsPayload.KindCells);
        var modes = Assert.Single(document.Render.Payloads, p => p.Name == "modes");
        AssertCatalogOrder(cells.Fields, "mouse_encoding", "bracketed_paste", "application_cursor");
        AssertCatalogOrder(modes.Fields, "sync", "application_cursor");

        foreach (var sample in new[] { FullCellsJson(), ModeDeltaJson() })
        {
            using var doc = JsonDocument.Parse(sample);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                Assert.Contains(cells.Fields, field => field.Name == prop.Name);
            }

            Assert.True(doc.RootElement.GetProperty("application_cursor").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("bracketed_paste").GetBoolean());
        }

        using var snapshot = JsonDocument.Parse(SnapshotWireJson());
        var modeObject = snapshot.RootElement.GetProperty("snapshot").GetProperty("modes");
        foreach (var prop in modeObject.EnumerateObject())
            Assert.Contains(modes.Fields, field => field.Name == prop.Name);
        Assert.True(modeObject.GetProperty("application_cursor").GetBoolean());
        Assert.True(modeObject.GetProperty("bracketed_paste").GetBoolean());
    }

    [Fact]
    public void Live_encoder_matches_each_input_mode_fixture()
    {
        AssertFixture("render/cells-input-modes.json", FullCellsJson());
        AssertFixture("render/cells-mode-delta.json", ModeDeltaJson());
        AssertFixture("render/snapshot-input-modes.json", SnapshotWireJson());
    }

    private static void AssertCatalogOrder(
        IReadOnlyList<ProtocolSchemaRenderFieldEntry> fields,
        params string[] names)
    {
        var index = -1;
        foreach (var name in names)
        {
            var next = fields.ToList().FindIndex(field => field.Name == name);
            Assert.True(next > index, name);
            index = next;
        }
    }

    private static void AssertFixture(string relative, string actual)
    {
        var dump = Path.Combine(Path.GetTempPath(), relative.Replace('/', '-'));
        string expected;
        try
        {
            expected = FixtureCatalog.Load(relative);
        }
        catch (FileNotFoundException)
        {
            File.WriteAllText(dump, actual);
            throw new FileNotFoundException($"Embedded fixture not found: '{relative}'. Wrote {dump}.");
        }

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            File.WriteAllText(dump, actual);
            Assert.Fail($"Fixture {relative} differs from the live encoder. Wrote {dump}.");
        }
    }

    private static string FullCellsJson()
    {
        var encoding = new VtCellsEncoder().Encode(BothFlagsFrame(), baseline: null, "pane_1", 1, 0, 0);
        Assert.NotNull(encoding);
        return Encoding.UTF8.GetString(encoding.Value.PayloadUtf8!);
    }

    private static string ModeDeltaJson()
    {
        var current = BothFlagsFrame();
        var baseline = current with { Modes = current.Modes with { BracketedPaste = false, ApplicationCursor = false } };
        var encoding = new VtCellsEncoder().Encode(current, baseline, "pane_1", 2, 1, 0);
        Assert.NotNull(encoding);
        return Encoding.UTF8.GetString(encoding.Value.PayloadUtf8!);
    }

    private static string SnapshotWireJson()
    {
        var parsed = AttachSnapshotPacker.ParseSnapshotJson("pane_1", BothFlagsSnapshotJson());
        var parts = AttachSnapshotPacker.Pack("pane_1", parsed, new DefaultEventPayloadRedactor(), generation: 1);
        var part = Assert.Single(parts);
        return Encoding.UTF8.GetString(part);
    }

    private static string BothFlagsSnapshotJson()
    {
        var snapshot = new VtStructuredSnapshot
        {
            SchemaVersion = 1,
            Provider = "ghostty",
            Cols = 1,
            Rows = 1,
            Cursor = new VtCursorSnapshot { Col = 0, Row = 0, Visible = true },
            ActiveScreen = "main",
            ScrollRegion = new VtScrollRegionSnapshot { Top = 0, Bottom = 0 },
            Modes = new VtModesSnapshot
            {
                Origin = false,
                AutoWrap = true,
                Insert = false,
                BracketedPaste = true,
                Mouse = "none",
                FocusReporting = false,
                Sync = false,
                ApplicationCursor = true,
            },
            Cells =
            [
                [
                    new VtCellSnapshot
                    {
                        Text = "A",
                        Width = 1,
                        IsContinuation = false,
                        Style = VtCellStyleSnapshot.Default,
                    },
                ],
            ],
        };
        return JsonSerializer.Serialize(snapshot, VtSnapshotWireJsonContext.Default.VtStructuredSnapshot);
    }

    private static VtFrame BothFlagsFrame()
    {
        var cell = new VtCellView("A", 1, false, null, null, false, false, false, false, false, false, false);
        var modes = new VtFrameModes(
            false,
            true,
            false,
            true,
            "none",
            false,
            false,
            null,
            true);
        return new VtFrame(
            "pane_1",
            1,
            1,
            [[cell]],
            new VtFrameCursor(0, 0, true, 0),
            modes,
            0,
            0,
            1);
    }
}
