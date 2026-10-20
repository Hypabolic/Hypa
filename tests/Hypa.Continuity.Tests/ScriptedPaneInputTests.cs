using System.Text;
using Hypa.Continuity.Tests.RemoteTyping;
using Xunit;

namespace Hypa.Continuity.Tests;

public sealed class ScriptedPaneInputTests
{
    [Theory]
    [InlineData("\u001b[?62;22c", "")]
    [InlineData("\u001b[12;40R", "")]
    [InlineData("\u001b]11;rgb:0000/0000/0000\u001b\\", "")]
    [InlineData("\u001b]10;rgb:ffff/ffff/ffff\u0007a", "a")]
    [InlineData("\u001b[?2004;1$y\u007f", "\u007f")]
    [InlineData("x\u001b[1;2c\u001b[D", "x\u001b[D")]
    public void Terminal_replies_on_the_pane_stdin_are_removed_and_keys_stay(string stdin, string keys)
    {
        var pending = Encoding.UTF8.GetBytes(stdin).ToList();
        ScriptedPaneFactory.StripTerminalReplies(pending);
        Assert.Equal(keys, Encoding.UTF8.GetString(pending.ToArray()));
    }

    [Fact]
    public void Cursor_keys_and_an_incomplete_reply_stay_until_the_reply_ends()
    {
        var pending = Encoding.UTF8.GetBytes("\u001b[C\u001bOD\u001b[?6").ToList();
        ScriptedPaneFactory.StripTerminalReplies(pending);
        Assert.Equal("\u001b[C\u001bOD\u001b[?6", Encoding.UTF8.GetString(pending.ToArray()));

        pending.AddRange("2c"u8.ToArray());
        ScriptedPaneFactory.StripTerminalReplies(pending);
        Assert.Equal("\u001b[C\u001bOD", Encoding.UTF8.GetString(pending.ToArray()));
    }
}
