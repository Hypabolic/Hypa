using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class AttachSurfaceIdentityStampTests
{
    [Fact]
    public void Stamp_keeps_packed_cell_rows_and_adds_identity()
    {
        var payload = """{"pane_id":"p1","kind":"cells","full":true,"grid_cols":10,"grid_rows":1,"generation":1,"rows":[{"i":0,"t":"hello     "}]}""";
        var record = new RuntimeEventRecord
        {
            Seq = 1,
            Class = EventClass.Render,
            Reliability = EventReliability.Render,
            Type = ProtocolEventTypes.TerminalRender,
            OccurredAt = DateTimeOffset.UnixEpoch,
            PayloadJson = payload,
            AttachEmitBootId = "boot-live",
            AttachEmitProjectionRevision = 4,
            AttachEmitSurfaceRevision = 5,
            AttachEmitColumns = 80,
            AttachEmitRows = 24,
        };

        Assert.True(EventSubscriptionHub.TryStampAttachSurfaceIdentity(
            Encoding.UTF8.GetBytes(payload),
            record,
            out var stamped));

        using var doc = JsonDocument.Parse(stamped);
        var root = doc.RootElement;
        Assert.Equal("boot-live", root.GetProperty("boot_id").GetString());
        Assert.Equal(4UL, root.GetProperty("projection_revision").GetUInt64());
        Assert.Equal(5UL, root.GetProperty("surface_revision").GetUInt64());
        Assert.Equal(80, root.GetProperty("columns").GetInt32());
        Assert.Equal(10, root.GetProperty("grid_cols").GetInt32());
        Assert.Equal(1, root.GetProperty("grid_rows").GetInt32());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("rows").ValueKind);
        Assert.Equal("hello     ", root.GetProperty("rows")[0].GetProperty("t").GetString());

        var cells = root.Deserialize(ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        Assert.NotNull(cells);
        Assert.Equal(10, cells!.GridCols);
        Assert.Single(cells.Rows!);
    }
}
