using Hypa.AgentRuntime.Domain.Theme;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class HostThemeParserTests
{
    [Fact]
    public void Color_scheme_997_reports()
    {
        Assert.True(HostThemeParser.TryParseColorSchemeReport(HostThemeParser.DarkReport, out var dark));
        Assert.Equal(HostAppearance.Dark, dark);
        Assert.True(HostThemeParser.TryParseColorSchemeReport(HostThemeParser.LightReport, out var light));
        Assert.Equal(HostAppearance.Light, light);
        Assert.False(HostThemeParser.TryParseColorSchemeReport("\u001b[?997;0n", out _));
        Assert.False(HostThemeParser.TryParseColorSchemeReport("\u001b[?996n", out _));
    }

    [Fact]
    public void Osc_11_rgb_and_hex()
    {
        Assert.True(HostThemeParser.TryParseOscDefaultColor(
            "\u001b]11;rgb:cccc/dddd/eeee\u001b\\",
            out var kind,
            out var color));
        Assert.Equal(HostDefaultColorKind.Background, kind);
        Assert.Equal(new HostRgb(0xcc, 0xdd, 0xee), color);

        Assert.True(HostThemeParser.TryParseOscDefaultColor(
            "\u001b]11;#123456\u0007",
            out kind,
            out color));
        Assert.Equal(HostDefaultColorKind.Background, kind);
        Assert.Equal(new HostRgb(0x12, 0x34, 0x56), color);

        Assert.True(HostThemeParser.TryParseOscDefaultColor(
            "\u001b]10;rgb:ffff/ffff/ffff\u0007",
            out kind,
            out _));
        Assert.Equal(HostDefaultColorKind.Foreground, kind);
    }

    [Fact]
    public void Luminance_split_at_128000()
    {
        Assert.Equal(HostAppearance.Light, HostThemeParser.InferAppearance(255, 255, 255));
        Assert.Equal(HostAppearance.Light, new HostRgb(128, 128, 128).InferredAppearance());
        Assert.Equal(HostAppearance.Dark, HostThemeParser.InferAppearance(0, 0, 0));
        Assert.Equal(HostAppearance.Dark, new HostRgb(127, 127, 127).InferredAppearance());
    }

    [Fact]
    public void Incomplete_osc_is_not_a_report()
    {
        Assert.False(HostThemeParser.TryParseOscDefaultColor("\u001b]11;rgb:ffff/ffff/ffff", out _, out _));
        Assert.False(HostThemeParser.IsOscTerminated("\u001b]11;rgb:ffff/ffff/ffff"u8));
        Assert.True(HostThemeParser.IsOscTerminated("\u001b]11;#000000\u0007"u8));
    }

    [Fact]
    public void Osc_set_and_reset_sequences_match_herdr()
    {
        Assert.Equal(
            "\u001b]10;rgb:cc/dd/ee\u001b\\",
            HostThemeParser.OscSetDefaultColorSequence(
                HostDefaultColorKind.Foreground,
                new HostRgb(0xcc, 0xdd, 0xee)));
        Assert.Equal(
            "\u001b]11;rgb:12/34/56\u001b\\",
            HostThemeParser.OscSetDefaultColorSequence(
                HostDefaultColorKind.Background,
                new HostRgb(0x12, 0x34, 0x56)));
        Assert.Equal("\u001b]110\u001b\\", HostThemeParser.OscResetDefaultColorSequence(HostDefaultColorKind.Foreground));
        Assert.Equal("\u001b]111\u001b\\", HostThemeParser.OscResetDefaultColorSequence(HostDefaultColorKind.Background));
    }

    [Fact]
    public void Osc_rgb_response_scales_byte_times_257()
    {
        var bytes = HostThemeParser.OscRgbResponse(
            HostDefaultColorKind.Background,
            new HostRgb(0x12, 0x34, 0x56));
        Assert.Equal("\u001b]11;rgb:1212/3434/5656\u001b\\", System.Text.Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Attach_start_sequence_queries_osc4_0_through_255()
    {
        var sequence = HostThemeParser.AttachStartSequence;
        Assert.StartsWith(
            HostThemeParser.EnableReports + HostThemeParser.QueryColorScheme + HostThemeParser.Osc10Query + HostThemeParser.Osc11Query,
            sequence,
            StringComparison.Ordinal);
        Assert.Contains(HostThemeParser.Osc10Query, sequence, StringComparison.Ordinal);
        Assert.Contains(HostThemeParser.Osc11Query, sequence, StringComparison.Ordinal);
        var palette = HostThemeParser.HostTerminalThemeQuerySequence(includePalette: true);
        Assert.Contains("\u001b]4;0;?\u001b\\", palette, StringComparison.Ordinal);
        Assert.EndsWith("\u001b]4;255;?\u001b\\", palette, StringComparison.Ordinal);
        for (var index = 0; index <= 255; index++)
            Assert.Contains($"\u001b]4;{index};?\u001b\\", palette, StringComparison.Ordinal);
        Assert.DoesNotContain("]4;", HostThemeParser.HostTerminalThemeQuerySequence(includePalette: false), StringComparison.Ordinal);
        Assert.Contains(palette, sequence, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_osc4_st_and_bel_rgb_and_hash()
    {
        Assert.True(HostThemeParser.TryParseOscPaletteColor(
            "\u001b]4;0;rgb:1111/2222/3333\u001b\\",
            out var index,
            out var color));
        Assert.Equal((byte)0, index);
        Assert.Equal(new HostRgb(0x11, 0x22, 0x33), color);

        Assert.True(HostThemeParser.TryParseOscPaletteColor(
            "\u001b]4;255;#aabbcc\u0007",
            out index,
            out color));
        Assert.Equal((byte)255, index);
        Assert.Equal(new HostRgb(0xaa, 0xbb, 0xcc), color);
    }

    [Fact]
    public void Rejects_malformed_rgb_unterminated_and_index_out_of_range()
    {
        Assert.False(HostThemeParser.TryParseOscPaletteColor(
            "\u001b]4;1;rgb:zzzz/0000/0000\u001b\\", out _, out _));
        Assert.False(HostThemeParser.TryParseOscPaletteColor(
            "\u001b]4;1;rgb:1111/2222/3333", out _, out _));
        Assert.False(HostThemeParser.TryParseOscPaletteColor(
            "\u001b]4;256;rgb:1111/2222/3333\u001b\\", out _, out _));
        Assert.False(HostThemeParser.TryParseOscPaletteColor(
            "\u001b]4;-1;rgb:1111/2222/3333\u001b\\", out _, out _));
        Assert.False(HostThemeParser.TryParseOscPaletteColor(
            "\u001b]4;1;\u001b\\", out _, out _));
    }

    [Fact]
    public void Hex_components_scale()
    {
        Assert.True(HostThemeParser.TryParseHexComponent("f", out var nibble));
        Assert.Equal(255, nibble);
        Assert.True(HostThemeParser.TryParseHexComponent("80", out var eight));
        Assert.Equal(128, eight);
        Assert.True(HostThemeParser.TryParseHexComponent("800", out var twelve));
        Assert.Equal(128, twelve);
        Assert.True(HostThemeParser.TryParseHexComponent("8000", out var sixteen));
        Assert.Equal(128, sixteen);
    }
}
