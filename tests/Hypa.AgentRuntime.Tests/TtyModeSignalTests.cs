using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.Terminal.Pty;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

/// <summary>
/// The PTY echo mode signal without a native helper: the TtyMode frame, the
/// order of a mode change against the output bytes, the minor gate, and the
/// optional <c>echo</c> field of a live cells body.
/// </summary>
public sealed class TtyModeSignalTests
{
    [Theory]
    [InlineData(true, true, 0x03)]
    [InlineData(false, true, 0x02)]
    [InlineData(true, false, 0x01)]
    [InlineData(false, false, 0x00)]
    public void TtyMode_frame_carries_echo_in_bit_0_and_icanon_in_bit_1(bool echo, bool canonical, byte flags)
    {
        var frame = PtyHostFrameCodec.CreateTtyMode(new PtyTtyMode(echo, canonical));
        var wire = PtyHostFrameCodec.Encode(frame);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 17, flags }, wire);

        var (decoded, consumed) = PtyHostFrameCodec.Decode(wire);
        Assert.Equal(wire.Length, consumed);
        Assert.Equal(PtyHostFrameType.TtyMode, decoded.Type);
        Assert.Equal(new PtyTtyMode(echo, canonical), PtyHostFrameCodec.ParseTtyMode(decoded.Payload));
    }

    [Fact]
    public void Mode_change_applies_when_the_reader_has_read_the_bytes_before_it()
    {
        var tracker = new PtyTtyModeTracker();
        var raised = 0;
        tracker.Changed += () => raised++;

        // The helper sends the first mode after Spawned, before any output.
        tracker.Record(new PtyTtyMode(Echo: true, Canonical: true));
        Assert.Equal(new PtyTtyMode(true, true), tracker.Current);
        Assert.Equal(1, raised);

        // "PROMPT" is in the channel. The child turns echo off and prints
        // "Password: ". The helper sends the change before that output.
        tracker.NoteEnqueued(6);
        tracker.Record(new PtyTtyMode(Echo: false, Canonical: true));
        tracker.NoteEnqueued(10);
        Assert.Equal(1, raised);

        // The reader has read part of "PROMPT": the bytes are still echo on.
        tracker.NoteRead(4);
        Assert.True(tracker.Current!.Value.Echo);

        // The reader has read every byte before the change.
        tracker.NoteRead(2);
        Assert.False(tracker.Current!.Value.Echo);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Mode_change_after_bytes_already_read_raises_changed_at_once()
    {
        var tracker = new PtyTtyModeTracker();
        tracker.Record(new PtyTtyMode(true, true));
        tracker.NoteEnqueued(8);
        tracker.NoteRead(8);
        var raised = 0;
        tracker.Changed += () => raised++;

        // A silent stty -echo that the helper finds at an input write.
        tracker.Record(new PtyTtyMode(false, true));

        Assert.Equal(1, raised);
        Assert.False(tracker.Current!.Value.Echo);
    }

    [Fact]
    public void Helper_below_minor_one_leaves_the_mode_unknown()
    {
        var tracker = new PtyTtyModeTracker();
        var frame = PtyHostFrameCodec.CreateTtyMode(new PtyTtyMode(false, true));

        Assert.False(PtyHostProcess.TryRecordTtyMode(0, tracker, frame.Payload));
        Assert.Null(tracker.Current);

        Assert.True(PtyHostProcess.TryRecordTtyMode(PtyHostFrameCodec.TtyModeMinor, tracker, frame.Payload));
        Assert.Equal(new PtyTtyMode(false, true), tracker.Current);
    }

    [Fact]
    public void Server_hello_minor_is_one_and_a_minor_zero_hello_still_parses()
    {
        Assert.True(PtyHostFrameCodec.ProtocolMinor >= PtyHostFrameCodec.TtyModeMinor);
        var old = PtyHostFrameCodec.CreateHello(minor: 0);
        Assert.True(PtyHostFrameCodec.TryParseHello(old.Payload, out var minor, out var error), error);
        Assert.Equal(0, minor);
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, null, null)]
    [InlineData(false, null, null)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Cells_body_has_echo_only_for_an_echo_mode_peer_with_a_known_mode(
        bool echoMode,
        bool? frameEcho,
        bool? wireEcho)
    {
        // A known mode carries both bits. Canonical is the opposite of echo
        // here, so the test sees each field on its own.
        bool? frameCanonical = frameEcho is { } e ? !e : null;
        var frame = Frame('a', frameEcho, frameCanonical);
        var encoding = new VtCellsEncoder().Encode(frame, baseline: null, "p1", 1, 0, 1, echoMode: echoMode);

        Assert.NotNull(encoding);
        using var doc = JsonDocument.Parse(encoding.Value.PayloadUtf8);
        var has = doc.RootElement.TryGetProperty("echo", out var echo);
        Assert.Equal(wireEcho is not null, has);
        Assert.Equal(wireEcho is not null, doc.RootElement.TryGetProperty("canonical", out var canonical));
        if (wireEcho is { } expected)
        {
            Assert.Equal(expected, echo.GetBoolean());
            Assert.Equal(!expected, canonical.GetBoolean());
        }

        var parsed = JsonSerializer.Deserialize(encoding.Value.PayloadUtf8, ProtocolJsonContext.Default.TerminalRenderCellsPayload);
        Assert.Equal(wireEcho, parsed!.Echo);
        Assert.Equal(wireEcho is { } w ? !w : null, parsed.Canonical);
    }

    [Fact]
    public void Echo_only_change_posts_a_body_with_no_rows()
    {
        var before = Frame('a', echo: true);
        var after = Frame('a', echo: false) with { Generation = 2 };
        var encoding = new VtCellsEncoder().Encode(after, before, "p1", 2, 1, 1, echoMode: true);

        Assert.NotNull(encoding);
        Assert.False(encoding.Value.Payload.Full);
        Assert.Empty(encoding.Value.Payload.Rows ?? []);
        Assert.False(encoding.Value.Payload.Echo);
        Assert.Contains("\"echo\":false", Encoding.UTF8.GetString(encoding.Value.PayloadUtf8), StringComparison.Ordinal);
    }

    private static VtFrame Frame(char ch, bool? echo, bool? canonical = true)
    {
        const int cols = 4;
        const int rows = 2;
        var cells = new IReadOnlyList<VtCellView>[rows];
        for (var r = 0; r < rows; r++)
        {
            var row = new VtCellView[cols];
            for (var c = 0; c < cols; c++)
                row[c] = new VtCellView(ch.ToString(), 1, false, null, null, false, false, false, false, false, false, false);
            cells[r] = row;
        }

        return new VtFrame(
            "p1",
            cols,
            rows,
            cells,
            new VtFrameCursor(0, 0, true, 0),
            new VtFrameModes(false, true, false, false, "none", false, Echo: echo, Canonical: echo is null ? null : canonical),
            0,
            1,
            1);
    }
}
