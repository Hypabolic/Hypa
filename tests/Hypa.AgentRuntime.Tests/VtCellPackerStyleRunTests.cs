using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol.Json;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The style runs of packed rows. Seeded frames with a mix of colours, style
/// bits, underlines, hyperlinks and wide glyphs must pack to the same rows and
/// hyperlink table as before the packer stopped making a run object for each
/// cell. The golden hashes were taken from the earlier packer.
/// </summary>
public sealed class VtCellPackerStyleRunTests
{
    private const string GoldenInline = "3B13A5A4E7DDF046B34CED3F75DACC3C";
    private const string GoldenTable = "4963C7BE86EB4DA7DE862C4A32A97BEF";

    [Fact]
    public void Seeded_styled_rows_pack_to_the_golden_runs_with_inline_links() =>
        Assert.Equal(GoldenInline, PackHash(emitHyperlinkTable: false));

    [Fact]
    public void Seeded_styled_rows_pack_to_the_golden_runs_with_a_link_table() =>
        Assert.Equal(GoldenTable, PackHash(emitHyperlinkTable: true));

    private static string PackHash(bool emitHyperlinkTable)
    {
        var sb = new StringBuilder();
        for (var seed = 0; seed < 40; seed++)
        {
            var packed = VtCellPacker.Pack(SeededFrame(new Random(4040 + seed)), emitHyperlinkTable);
            var json = JsonSerializer.Serialize(packed.Rows, ProtocolJsonContext.Default.IReadOnlyListTerminalRenderCellRow);
            sb.Append(json).Append('\n').AppendJoin('\n', packed.HyperlinkTable ?? []).Append('\n');
        }

        // The fixture must reach the packer with links, style runs and wide glyphs.
        var all = sb.ToString();
        Assert.Contains(emitHyperlinkTable ? "\"hi\":" : "example.test", all, StringComparison.Ordinal);
        Assert.Contains("\"s\":", all, StringComparison.Ordinal);
        Assert.Contains("\"w\":", all, StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all)))[..32];
    }

    private static VtFrame SeededFrame(Random random)
    {
        const int cols = 40;
        const int rows = 6;
        string?[] links = [null, null, null, "https://example.test/a", "https://example.test/b", ""];
        string?[] colours = [null, null, "#112233", "palette:4", "palette:208"];
        string[] glyphs = ["a", "b", " ", " ", "é", "日", "🙂"];
        var cells = new List<IReadOnlyList<VtCellView>>();
        for (var r = 0; r < rows; r++)
        {
            var row = new List<VtCellView>();
            while (row.Count < cols)
            {
                // A style holds for a few cells, so that runs merge and split.
                var fg = colours[random.Next(colours.Length)];
                var bg = colours[random.Next(colours.Length)];
                var bits = random.Next(8) == 0 ? random.Next(512) : 0;
                var underlineColor = random.Next(10) == 0 ? "#ABCDEF" : null;
                var underlineStyle = random.Next(10) == 0 ? random.Next(1, 4) : 0;
                var link = links[random.Next(links.Length)];
                var span = random.Next(1, 7);
                for (var i = 0; i < span && row.Count < cols; i++)
                {
                    var glyph = glyphs[random.Next(glyphs.Length)];
                    var wide = glyph is "日" or "🙂";
                    if (wide && row.Count == cols - 1)
                        glyph = "x";
                    wide = glyph is "日" or "🙂";
                    row.Add(new VtCellView(
                        glyph,
                        wide ? 2 : 1,
                        false,
                        fg,
                        bg,
                        (bits & 1) != 0,
                        (bits & 2) != 0,
                        (bits & 4) != 0,
                        (bits & 8) != 0,
                        (bits & 16) != 0,
                        (bits & 32) != 0,
                        (bits & 64) != 0,
                        (bits & 128) != 0,
                        (bits & 256) != 0,
                        underlineColor,
                        underlineStyle,
                        Hyperlink: link));
                    if (wide)
                        row.Add(new VtCellView("", 1, true, fg, bg, false, false, false, false, false, false, false, Hyperlink: link));
                }
            }

            cells.Add(row);
        }

        return new VtFrame(
            "pane_golden",
            cols,
            rows,
            cells,
            VtFrameCursor.None,
            new VtFrameModes(false, false, false, false, "none", false),
            0,
            1,
            1);
    }
}
