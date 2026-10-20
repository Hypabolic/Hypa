using System.Text;
using Hypa.Cli.Attach;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// .NET puts the host terminal in application cursor and keypad mode when
/// it opens the console. The attach restore must put the host back in
/// normal mode. A Ghostty engine plays the host terminal.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public class HostRestoreKeypadGhosttyTests
{
    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();

    private const string DotNetKeypadXmit = "\u001b[?1h\u001b=";
    private const string QueryCursorAndKeypadModes = "\u001b[?1$p\u001b[?66$p";

    [SkippableFact]
    public void Restore_after_an_overlay_attach_leaves_normal_cursor_and_keypad_modes()
    {
        using var host = NewEngine();
        Assert.Equal(["\u001b[?1;1$y", "\u001b[?66;1$y"], Feed(host, DotNetKeypadXmit + QueryCursorAndKeypadModes));

        using var capture = new MemoryStream();
        using (var tty = new UnixRawTerminal(capture, 80, 24))
        {
            tty.EnterRaw();
            tty.EnterClientOverlay();
            tty.Restore();
        }

        Feed(host, Encoding.UTF8.GetString(capture.ToArray()));
        Assert.Equal(["\u001b[?1;2$y", "\u001b[?66;2$y"], Feed(host, QueryCursorAndKeypadModes));
    }

    [SkippableFact]
    public void Restore_without_the_overlay_also_leaves_normal_cursor_and_keypad_modes()
    {
        using var host = NewEngine();
        Feed(host, DotNetKeypadXmit);

        using var capture = new MemoryStream();
        using (var tty = new UnixRawTerminal(capture, 80, 24))
        {
            tty.EnterRaw();
            tty.Restore();
        }

        Feed(host, Encoding.UTF8.GetString(capture.ToArray()));
        Assert.Equal(["\u001b[?1;2$y", "\u001b[?66;2$y"], Feed(host, QueryCursorAndKeypadModes));
    }

    private static GhosttyVtEngine NewEngine()
    {
        GhosttyTestRequire.RequireNativeLibrary(LibPath);
        return new GhosttyVtEngine(80, 24, libraryPathOverride: LibPath);
    }

    private static string[] Feed(GhosttyVtEngine vt, string bytes)
    {
        var replies = new List<byte[]>();
        vt.FeedChildOutput(Encoding.UTF8.GetBytes(bytes), replies);
        return replies.Select(r => Encoding.ASCII.GetString(r)).ToArray();
    }
}
