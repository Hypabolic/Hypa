using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Hypa.Cli.Attach;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The attach client side of the PTY echo mode signal: the hello offers
/// the capability, and each cells body states the echo flag of its frame.
/// </summary>
public sealed class EchoModeClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Attach_hello_offers_echo_mode_on_every_link(bool renderCredit)
    {
        var hello = AttachEndpointPreflight.BuildHello(
            "client_1",
            new AttachGeometry
            {
                Columns = 80,
                Rows = 24,
                CellWidthPx = 8,
                CellHeightPx = 16,
                GeometryRevision = 1,
            },
            surfaceActive: true,
            renderCredit: renderCredit);

        var wire = JsonSerializer.Serialize(hello, ProtocolJsonContext.Default.AttachEndpointHello);
        var parsed = JsonSerializer.Deserialize(wire, ProtocolJsonContext.Default.AttachEndpointHello)!;
        Assert.Contains(AttachEndpointProtocol.EchoModeCapability, parsed.OptionalCapabilities!);
        Assert.Equal(
            renderCredit,
            parsed.OptionalCapabilities!.Contains(AttachEndpointProtocol.RenderCreditCapability));
    }

    [Fact]
    public void Each_cells_body_states_the_echo_flag_and_an_absent_flag_is_unknown()
    {
        var on = Body() with { Echo = true, Canonical = true };
        var first = VtCellUnpacker.Apply(Parse(on), previous: null);
        Assert.True(first.Echo);
        Assert.True(first.Canonical);

        var off = Body() with { Echo = false, Canonical = false, Full = false, BaseGeneration = 1, Generation = 2 };
        var second = VtCellUnpacker.Apply(Parse(off), first);
        Assert.False(second.Echo);
        Assert.False(second.Canonical);

        // A body with no flag does not keep the false of the body before it.
        var unknown = Body() with { Full = false, BaseGeneration = 2, Generation = 3 };
        var third = VtCellUnpacker.Apply(Parse(unknown), second);
        Assert.Null(third.Echo);
        Assert.Null(third.Canonical);
    }

    private static TerminalRenderCellsPayload Body() => new()
    {
        Kind = TerminalRenderCellsPayload.KindCells,
        PaneId = "p1",
        Full = true,
        GridCols = 1,
        GridRows = 1,
        Generation = 1,
        Rows = [],
    };

    /// <summary>The body as the client reads it from the wire.</summary>
    private static TerminalRenderCellsPayload Parse(TerminalRenderCellsPayload body) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(body, ProtocolJsonContext.Default.TerminalRenderCellsPayload),
            ProtocolJsonContext.Default.TerminalRenderCellsPayload)!;
}
