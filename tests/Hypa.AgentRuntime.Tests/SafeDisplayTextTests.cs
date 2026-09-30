using System.Text;
using Hypa.AgentRuntime.Application;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class SafeDisplayTextTests
{
    [Fact]
    public void Encode_strips_csi_and_keeps_printable_payload()
    {
        Assert.Equal("[31mred[0m", SafeDisplayText.Encode("\u001b[31mred\u001b[0m"));
        Assert.DoesNotContain('\u001b', SafeDisplayText.Encode("\u001b[2Jkeep"));
        Assert.Contains("keep", SafeDisplayText.Encode("\u001b[2Jkeep"), StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_strips_osc_and_bel_terminator()
    {
        Assert.Equal("]0;eviltitle", SafeDisplayText.Encode("\u001b]0;evil\u0007title"));
        Assert.DoesNotContain('\u001b', SafeDisplayText.Encode("\u001b]52;c;evil\u0007box"));
        Assert.DoesNotContain('\u0007', SafeDisplayText.Encode("\u001b]0;evil\u0007title"));
        Assert.Contains("box", SafeDisplayText.Encode("\u001b]52;c;evil\u0007box"), StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_strips_c1_and_del()
    {
        Assert.Equal("a2Jb", SafeDisplayText.Encode("a\u009b2Jb"));
        Assert.Equal("ab", SafeDisplayText.Encode("a\u007fb"));
        Assert.Equal("cd", SafeDisplayText.Encode("c\u009dd"));
        Assert.DoesNotContain('\u009b', SafeDisplayText.Encode("x\u009b2Jy"));
        Assert.DoesNotContain('\u007f', SafeDisplayText.Encode("x\u007fy"));
    }

    [Fact]
    public void Contains_unsafe_control_detects_csi_osc_c1_and_del()
    {
        Assert.True(SafeDisplayText.ContainsUnsafeControl("\u001b[31m"));
        Assert.True(SafeDisplayText.ContainsUnsafeControl("\u001b]0;evil\u0007"));
        Assert.True(SafeDisplayText.ContainsUnsafeControl("a\u009b2J"));
        Assert.True(SafeDisplayText.ContainsUnsafeControl("a\u007f"));
        Assert.True(SafeDisplayText.ContainsUnsafeControl("\u0007"));
        Assert.False(SafeDisplayText.ContainsUnsafeControl("ok"));
        Assert.False(SafeDisplayText.ContainsUnsafeControl(""));
        Assert.False(SafeDisplayText.ContainsUnsafeControl(null));
    }

    [Fact]
    public void Clip_drops_split_controls_after_a_long_prefix()
    {
        var prefix = new string('X', 31);
        var split = prefix + "\u001b[31mHACK";
        var clipped = SafeDisplayText.Clip(split, 31);
        Assert.Equal(prefix, clipped);
        Assert.DoesNotContain('\u001b', clipped);
        Assert.Equal(31, SafeDisplayText.Width(clipped));

        var afterCut = new string('Y', 32) + "\u001b]52;c;evil\u0007";
        Assert.Equal(new string('Y', 32), SafeDisplayText.Clip(afterCut, 32));
        Assert.DoesNotContain('\u001b', SafeDisplayText.Clip(afterCut, 32));
        Assert.DoesNotContain('\u0007', SafeDisplayText.Clip(afterCut, 32));
    }

    [Fact]
    public void Clip_and_encode_never_emit_c0_c1_esc_or_del()
    {
        var raw = "ok\u001b[31m\u009b2J\u007f\u0007more";
        foreach (var text in new[]
                 {
                     SafeDisplayText.Encode(raw),
                     SafeDisplayText.Clip(raw, 8),
                     SafeDisplayText.PadRight(raw, 12),
                 })
        {
            Assert.False(SafeDisplayText.ContainsUnsafeControl(text));
            foreach (var rune in text.EnumerateRunes())
                Assert.False(SafeDisplayText.IsUnsafeControl(rune));
        }
    }

    [Fact]
    public void Width_counts_wide_combining_and_emoji_columns()
    {
        Assert.Equal(2, SafeDisplayText.Width("日"));
        Assert.Equal(2, SafeDisplayText.Width("😀"));
        Assert.Equal(1, SafeDisplayText.Width("e\u0301"));
        Assert.Equal(0, SafeDisplayText.Width("\u001b"));
        Assert.Equal(0, SafeDisplayText.Width("\u007f"));
        Assert.Equal(0, SafeDisplayText.Width("\u009b"));
        Assert.Equal(8, SafeDisplayText.Width("日本語タ"));
        Assert.Equal(2, SafeDisplayText.Width(new Rune(0x65E5)));
        Assert.Equal(0, SafeDisplayText.Width(new Rune(0x0301)));
        Assert.Equal(0, SafeDisplayText.Width(new Rune(0x1B)));
    }

    [Fact]
    public void Clip_and_pad_use_display_columns_not_utf16()
    {
        Assert.Equal("", SafeDisplayText.Clip("日", 1));
        Assert.Equal("日", SafeDisplayText.Clip("日", 2));
        Assert.Equal("日x", SafeDisplayText.Clip("日x😀", 3));
        Assert.Equal(4, SafeDisplayText.Width(SafeDisplayText.PadRight("日", 4)));
        Assert.Equal("日  ", SafeDisplayText.PadRight("日", 4));
        Assert.Equal("日", SafeDisplayText.PadRight("日本語", 2));
        Assert.Equal("日 ", SafeDisplayText.PadRight("日😀", 3));
        Assert.Equal(3, SafeDisplayText.Width(SafeDisplayText.PadRight("日😀", 3)));
        Assert.Equal(1, SafeDisplayText.Width(SafeDisplayText.Clip("e\u0301x", 1)));
        Assert.Equal("e\u0301", SafeDisplayText.Clip("e\u0301x", 1));
    }

    [Fact]
    public void Truncate_end_keeps_ellipsis_at_one_column()
    {
        Assert.Equal("…", SafeDisplayText.TruncateEnd("日", 1));
        Assert.Equal("he…", SafeDisplayText.TruncateEnd("hello", 3));
        Assert.Equal("hi", SafeDisplayText.TruncateEnd("hi", 10));
        Assert.Equal("", SafeDisplayText.TruncateEnd("hello", 0));
    }

    [Fact]
    public void Encode_drops_lone_controls_and_zero_width_marks()
    {
        Assert.Equal("", SafeDisplayText.Encode("\u001b\u0007\u007f\u009b"));
        Assert.Equal("", SafeDisplayText.Encode("\u0301"));
        Assert.Equal("e\u0301", SafeDisplayText.Encode("e\u0301"));
        Assert.Equal("", SafeDisplayText.Encode(null));
        Assert.Equal(0, SafeDisplayText.Width(null));
        Assert.Equal("", SafeDisplayText.Clip(null, 4));
        Assert.Equal("    ", SafeDisplayText.PadRight(null, 4));
    }
}
