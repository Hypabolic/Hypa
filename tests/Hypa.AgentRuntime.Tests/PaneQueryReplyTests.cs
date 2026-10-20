using System.Text;
using System.Threading.Channels;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.AgentRuntime.Tests.Support;
using Hypa.Terminal;
using Hypa.Terminal.Pty;
using Hypa.Terminal.Vt.Ghostty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// A program in a pane writes a terminal query and gets one reply. Only live
/// child output gets replies. A restore or a replay feed gets none.
/// </summary>
[Trait("Category", "Ghostty")]
[Collection("GhosttyPtyTests")]
public class PaneQueryReplyTests
{
    private const string Da1Reply = "\u001b[?62;22c";
    private static readonly string? LibPath = GhosttyTestRequire.TryResolveNativeLibraryPath();
    private static readonly HostRgb HostBackground = new(10, 20, 30);
    private static readonly HostRgb HostForeground = new(200, 210, 220);

    [SkippableFact]
    public async Task Primary_device_attributes_query_gets_one_reply_on_the_pty()
    {
        RequireLibrary();
        using var vt = new GhosttyVtEngine(40, 8, maxScrollback: 100, libraryPathOverride: LibPath);
        var pty = new RecordingPtyProcess();
        await using var pane = new PaneRuntime(
            Options("p_da1_pty"),
            vt: vt,
            spawnProcess: (_, _) => pty);
        await pane.StartAsync(CancellationToken.None);

        pty.Emit(Encoding.ASCII.GetBytes("\u001b[c"));
        // A cursor position report after DA1 is the end marker. The pane
        // writes replies in stream order, so DA1 is already on the PTY then.
        pty.Emit(Encoding.ASCII.GetBytes("\u001b[2;3H\u001b[6n"));
        await Poll.UntilAsync(
            () => pty.InputText.Contains("\u001b[2;3R", StringComparison.Ordinal),
            "the cursor position report on the PTY");

        Assert.Equal(Da1Reply + "\u001b[2;3R", pty.InputText);
    }

    [SkippableFact]
    public async Task Primary_device_attributes_query_gets_exactly_one_reply()
    {
        await using var pane = NewPane("p_da1");

        var replies = pane.HandleChildBytesForTests("\u001b[c"u8);

        Assert.Equal([Da1Reply], Text(replies));
    }

    [SkippableFact]
    public async Task Background_query_gets_only_the_host_theme_reply()
    {
        await using var pane = NewPane("p_osc11");
        pane.ApplyHostTerminalTheme(new HostTerminalTheme(HostForeground, HostBackground, HostAppearance.Dark));
        // Ghostty holds the host background, so Ghostty alone would answer too.
        var ghosttyAlone = new List<byte[]>();
        ((GhosttyVtEngine)pane.VtEngine).FeedChildOutput("\u001b]11;?\u001b\\"u8, ghosttyAlone);
        Assert.Single(ghosttyAlone);

        var replies = pane.HandleChildBytesForTests("\u001b]11;?\u001b\\"u8);

        Assert.Equal([HostReply(HostDefaultColorKind.Background)], Text(replies));
    }

    [SkippableFact]
    public async Task Foreground_query_with_bel_gets_only_the_host_theme_reply()
    {
        await using var pane = NewPane("p_osc10");
        pane.ApplyHostTerminalTheme(new HostTerminalTheme(HostForeground, HostBackground, HostAppearance.Dark));

        var replies = pane.HandleChildBytesForTests("\u001b]10;?\u0007"u8);

        Assert.Equal([HostReply(HostDefaultColorKind.Foreground)], Text(replies));
    }

    [SkippableFact]
    public async Task Background_query_split_inside_the_terminator_gets_one_reply()
    {
        await using var pane = NewPane("p_osc11_split");
        pane.ApplyHostTerminalTheme(new HostTerminalTheme(HostForeground, HostBackground, HostAppearance.Dark));

        var first = pane.HandleChildBytesForTests("\u001b[6n\u001b]11;?\u001b"u8);
        var second = pane.HandleChildBytesForTests("\\\u001b[c"u8);

        Assert.Equal(
            ["\u001b[1;1R", HostReply(HostDefaultColorKind.Background), Da1Reply],
            Text(first).Concat(Text(second)).ToArray());
    }

    [SkippableFact]
    public async Task Owned_background_query_gets_the_child_colour_from_ghostty()
    {
        await using var pane = NewPane("p_osc11_owned");
        pane.ApplyHostTerminalTheme(new HostTerminalTheme(HostForeground, HostBackground, HostAppearance.Dark));
        _ = pane.HandleChildBytesForTests("\u001b]11;rgb:aa/bb/cc\u001b\\"u8);
        Assert.True(pane.ChildBackgroundOwned);

        var replies = pane.HandleChildBytesForTests("\u001b]11;?\u001b\\"u8);

        Assert.Equal(["\u001b]11;rgb:aaaa/bbbb/cccc\u001b\\"], Text(replies));
    }

    [SkippableFact]
    public async Task Palette_query_gets_one_reply_with_the_host_palette()
    {
        await using var pane = NewPane("p_osc4");
        pane.ApplyHostTerminalTheme(
            new HostTerminalTheme(HostForeground, HostBackground, HostAppearance.Dark)
                .WithPaletteColor(1, new HostRgb(0x12, 0x34, 0x56)));

        var replies = pane.HandleChildBytesForTests("\u001b]4;1;?\u001b\\"u8);

        Assert.Equal(["\u001b]4;1;rgb:1212/3434/5656\u001b\\"], Text(replies));
    }

    [SkippableFact]
    public async Task Kitty_keyboard_query_gets_no_reply()
    {
        await using var pane = NewPane("p_kitty");

        Assert.Empty(pane.HandleChildBytesForTests("\u001b[?u"u8));

        // A pushed flag set must not make the pane claim kitty keys either.
        Assert.Empty(pane.HandleChildBytesForTests("\u001b[>1u\u001b[?u"u8));
    }

    [SkippableFact]
    public async Task Kitty_keyboard_probe_before_device_attributes_gets_only_device_attributes()
    {
        await using var pane = NewPane("p_kitty_probe");

        var replies = pane.HandleChildBytesForTests("\u001b[?u\u001b[c"u8);

        Assert.Equal([Da1Reply], Text(replies));
    }

    [SkippableFact]
    public async Task Replayed_history_sends_no_reply()
    {
        await using var pane = NewPane("p_replay");
        const string queries = "\u001b[c\u001b[6n\u001b[>q\u001b[5n";

        pane.SeedHistoryAnsi(queries);
        pane.FeedVtForTests(queries);
        var live = pane.HandleChildBytesForTests("plain output"u8);

        Assert.Empty(live);
    }

    [SkippableFact]
    public async Task Program_status_query_reply_keeps_stream_order_with_vt_replies()
    {
        await using var pane = NewPane("p_7501");

        var replies = pane.HandleChildBytesForTests("\u001b[c\u001b]7501;?\u001b\\\u001b[5n"u8);

        Assert.Equal([Da1Reply, "\u001b]7501;?\u001b\\", "\u001b[0n"], Text(replies));
    }

    [SkippableFact]
    public async Task Host_colour_reply_keeps_stream_order_with_vt_replies()
    {
        await using var pane = NewPane("p_order");
        pane.ApplyHostTerminalTheme(new HostTerminalTheme(HostForeground, HostBackground, HostAppearance.Dark));

        var replies = pane.HandleChildBytesForTests("\u001b[6n\u001b]11;?\u001b\\\u001b[c"u8);

        Assert.Equal(
            ["\u001b[1;1R", HostReply(HostDefaultColorKind.Background), Da1Reply],
            Text(replies));
    }

    private static PaneSpawnOptions Options(string id) => new()
    {
        Id = new PaneId(id),
        Cwd = Path.GetTempPath(),
        Command = "/bin/true",
        Args = [],
        Cols = 40,
        Rows = 8,
    };

    private static PaneRuntime NewPane(string id)
    {
        RequireLibrary();
        return new PaneRuntime(
            Options(id),
            new GhosttyVtEngine(40, 8, maxScrollback: 100, libraryPathOverride: LibPath));
    }

    private static string HostReply(HostDefaultColorKind kind) =>
        Encoding.ASCII.GetString(HostThemeParser.OscRgbResponse(
            kind,
            kind is HostDefaultColorKind.Foreground ? HostForeground : HostBackground));

    private static string[] Text(List<byte[]> replies) =>
        replies.Select(r => Encoding.ASCII.GetString(r)).ToArray();

    private static void RequireLibrary() => GhosttyTestRequire.RequireNativeLibrary(LibPath);

    /// <summary>A PTY that emits scripted child output and records what the pane writes to it.</summary>
    private sealed class RecordingPtyProcess : IPtyProcess
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly RecordingStream _input = new();
        private readonly ChannelReadStream _outputStream;

        public RecordingPtyProcess() => _outputStream = new ChannelReadStream(_output.Reader);

        public int Pid => 4243;
        public bool IsRunning => true;
        public int? ExitCode => null;
        public Stream StandardInput => _input;
        public Stream StandardOutput => _outputStream;

        public string InputText => _input.Text;

        public void Emit(byte[] chunk) => _output.Writer.TryWrite(chunk);

        public void Resize(int cols, int rows)
        {
        }

        public Task WaitForExitAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

        public ValueTask DisposeAsync()
        {
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingStream : Stream
    {
        private readonly Lock _gate = new();
        private readonly MemoryStream _bytes = new();

        public string Text
        {
            get
            {
                lock (_gate)
                    return Encoding.ASCII.GetString(_bytes.ToArray());
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
                _bytes.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_gate)
                _bytes.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ChannelReadStream(ChannelReader<byte[]> reader) : Stream
    {
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;
                if (reader.TryRead(out var next))
                {
                    _current = next;
                    _offset = 0;
                }
            }

            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }
    }
}
