using System.Text;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The host kitty keyboard mode, the legacy ctrl+alt chords, and the Escape
/// wait. The attach pushes kitty flag 1 when the host answers <c>CSI ? u</c>,
/// sends legacy bytes to panes, and pops the flag on every exit.
/// </summary>
public sealed class HostKittyKeyboardTests
{
    private static KeyEngine DefaultEngine() =>
        new(KeyBindingTable.CompileOrThrow(KeysConfig.Default()));

    private static KeyEngine FamilyEngine() =>
        new(KeyBindingTable.CompileOrThrow(KeysConfig.Default().WithPrefixFreeFamily()));

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public void Decoder_reads_escape_and_backspace_byte_as_ctrl_alt_h()
    {
        var one = Assert.Single(KeyEventDecoder.Decode([0x1B, 0x08]));
        Assert.Equal(new KeyChord(false, true, true, false, "h"), one.Chord);
        Assert.Equal([0x1B, 0x08], one.Raw);

        var del = Assert.Single(KeyEventDecoder.Decode([0x1B, 0x7F]));
        Assert.Equal(new KeyChord(false, false, true, false, "backspace"), del.Chord);
    }

    [Fact]
    public void Unbound_ctrl_alt_h_goes_to_the_pane_with_the_default_keys()
    {
        var events = DefaultEngine().Feed([0x1B, 0x08]);

        var send = Assert.Single(events);
        Assert.Equal(KeyEngineEventKind.SendPaneBytes, send.Kind);
        Assert.Equal([0x1B, 0x08], send.Bytes);
    }

    [Theory]
    [InlineData(0x08, KeyActionId.FocusPaneLeft)]
    [InlineData(0x0A, KeyActionId.FocusPaneDown)]
    [InlineData(0x0B, KeyActionId.FocusPaneUp)]
    [InlineData(0x0C, KeyActionId.FocusPaneRight)]
    public void Prefix_free_family_dispatches_legacy_ctrl_alt_focus_chords(byte control, KeyActionId action)
    {
        var events = FamilyEngine().Feed([0x1B, control]);

        Assert.Contains(events, e => e.Kind == KeyEngineEventKind.Dispatch && e.Action == action);
    }

    [Fact]
    public void Escape_escape_in_one_read_dispatches_ctrl_alt_bracket_on_the_stdin_path()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        var keys = Decode(framer, [0x1B, 0x1B]);
        keys.AddRange(AttachSession.FlushPendingCsi(framer.Pending, framer));

        Assert.Equal([0x1B, 0x1B], keys.ToArray());
        var events = FamilyEngine().Feed(keys.ToArray());
        var dispatch = Assert.Single(events, e => e.Kind == KeyEngineEventKind.Dispatch);
        Assert.Equal(KeyActionId.PreviousTab, dispatch.Action);
    }

    [Fact]
    public void Unbound_escape_escape_reaches_the_pane_as_the_same_bytes()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        var keys = Decode(framer, [0x1B, 0x1B]);
        keys.AddRange(AttachSession.FlushPendingCsi(framer.Pending, framer));

        var bytes = PaneBytes(DefaultEngine().Feed(keys.ToArray()));
        Assert.Equal([0x1B, 0x1B], bytes);
    }

    [Fact]
    public void Escape_then_escape_and_a_letter_in_one_read_is_one_chord_and_the_letter()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        var keys = Decode(framer, [0x1B, 0x1B, (byte)'x']);

        Assert.Equal([0x1B, 0x1B, (byte)'x'], keys.ToArray());
        Assert.Empty(framer.Pending);
    }

    [Fact]
    public void Escape_before_a_complete_kitty_report_is_two_keys()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        var keys = Decode(framer, Ascii("\u001b\u001b[27u"));

        Assert.Empty(framer.Pending);
        var decoded = KeyEventDecoder.Decode(keys.ToArray());
        Assert.Equal(2, decoded.Count);
        Assert.All(decoded, d => Assert.True(d.Chord.IsEscape));
        Assert.Equal([0x1B], decoded[0].Raw);
        Assert.Equal(Ascii("\u001b[27u"), decoded[1].Raw);
    }

    [Fact]
    public void Escape_before_a_cursor_key_is_two_keys()
    {
        var decoded = KeyEventDecoder.Decode(Ascii("\u001b\u001b[A"));

        Assert.Equal(2, decoded.Count);
        Assert.True(decoded[0].Chord.IsEscape);
        Assert.Equal("up", decoded[1].Chord.Key);
    }

    [Fact]
    public void Escape_escape_waiting_for_host_replies_sends_one_escape_and_holds_the_other()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        framer.NoteHostColorQuerySent();
        Decode(framer, [0x1B, 0x1B]);

        Assert.Equal([0x1B], AttachSession.FlushPendingCsi(framer.Pending, framer).ToArray());
        Assert.Equal([0x1B], framer.Pending.ToArray());
        // The held ESC stitches with the rest of a split reply.
        Assert.Empty(Decode(framer, Ascii("]11;rgb:2424/2727/3a3a\u001b\\")));
        Assert.Empty(framer.Pending);
    }

    [Theory]
    [InlineData("\u001b[27u", "esc")]
    [InlineData("\u001b[13u", "enter")]
    [InlineData("\u001b[9u", "tab")]
    [InlineData("\u001b[127u", "backspace")]
    [InlineData("\u001b[32;5u", "space")]
    [InlineData("\u001b[57414u", "enter")]
    [InlineData("\u001b[57400u", "1")]
    [InlineData("\u001b[P", "f1")]
    [InlineData("\u001b[13~", "f3")]
    public void Decoder_reads_kitty_functional_codes_as_named_keys(string raw, string key)
    {
        var one = Assert.Single(KeyEventDecoder.Decode(Ascii(raw)));
        Assert.Equal(key, one.Chord.Key);
    }

    [Fact]
    public void Decoder_ignores_lock_bits_and_alternate_key_fields()
    {
        // ctrl with caps lock (1 + 4 + 64), and a shifted alternate key.
        var locked = Assert.Single(KeyEventDecoder.Decode(Ascii("\u001b[104;69u")));
        Assert.Equal(new KeyChord(false, true, false, false, "h"), locked.Chord);

        var alternate = Assert.Single(KeyEventDecoder.Decode(Ascii("\u001b[104:72;7u")));
        Assert.Equal(new KeyChord(false, true, true, false, "h"), alternate.Chord);
    }

    [Fact]
    public void Kitty_escape_on_the_stdin_path_is_a_key_at_once_and_reaches_the_pane_as_esc()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        var keys = Decode(framer, Ascii("\u001b[27u"));

        Assert.Empty(framer.Pending);
        var engine = DefaultEngine();
        engine.HostKittyKeyboard = true;
        var send = Assert.Single(engine.Feed(keys.ToArray()));
        Assert.Equal(KeyEngineEventKind.SendPaneBytes, send.Kind);
        Assert.Equal([0x1B], send.Bytes);
    }

    [Fact]
    public void Unbound_kitty_ctrl_alt_h_reaches_the_pane_as_legacy_bytes()
    {
        var engine = DefaultEngine();
        engine.HostKittyKeyboard = true;

        Assert.Equal([0x1B, 0x08], PaneBytes(engine.Feed(Ascii("\u001b[104;7u"))));
    }

    [Fact]
    public void Bound_kitty_ctrl_alt_h_dispatches()
    {
        var engine = FamilyEngine();
        engine.HostKittyKeyboard = true;

        var events = engine.Feed(Ascii("\u001b[104;7u"));
        Assert.Contains(events, e => e.Kind == KeyEngineEventKind.Dispatch && e.Action == KeyActionId.FocusPaneLeft);
    }

    [Fact]
    public void Kitty_ctrl_alt_i_and_m_are_distinct_chords()
    {
        var i = Assert.Single(KeyEventDecoder.Decode(Ascii("\u001b[105;7u")));
        var m = Assert.Single(KeyEventDecoder.Decode(Ascii("\u001b[109;7u")));

        Assert.Equal(new KeyChord(false, true, true, false, "i"), i.Chord);
        Assert.Equal(new KeyChord(false, true, true, false, "m"), m.Chord);
    }

    [Theory]
    [InlineData("\u001b[99;5u", "\u0003")]
    [InlineData("\u001b[97;6u", "\u0001")]
    [InlineData("\u001b[120;3u", "\u001bx")]
    [InlineData("\u001b[120;4u", "\u001bX")]
    [InlineData("\u001b[49;4u", "\u001b!")]
    [InlineData("\u001b[49:33;4u", "\u001b!")]
    [InlineData("\u001b[27u", "\u001b")]
    [InlineData("\u001b[27;3u", "\u001b\u001b")]
    [InlineData("\u001b[13;2u", "\r")]
    [InlineData("\u001b[13;3u", "\u001b\r")]
    [InlineData("\u001b[9;2u", "\u001b[Z")]
    [InlineData("\u001b[127;5u", "\b")]
    [InlineData("\u001b[127;3u", "\u001b\u007f")]
    [InlineData("\u001b[32;5u", "\0")]
    [InlineData("\u001b[91;5u", "\u001b")]
    [InlineData("\u001b[99;69u", "\u0003")]
    [InlineData("\u001b[57400u", "1")]
    [InlineData("\u001b[57414u", "\r")]
    [InlineData("\u001b[57417;5u", "\u001b[1;5D")]
    [InlineData("\u001b[57419u", "\u001b[A")]
    [InlineData("\u001b[57421u", "\u001b[5~")]
    [InlineData("\u001b[P", "\u001bOP")]
    [InlineData("\u001b[13~", "\u001bOR")]
    [InlineData("\u001b[13;2~", "\u001b[1;2R")]
    public void Kitty_reports_convert_to_legacy_bytes(string kitty, string legacy)
    {
        Assert.True(KittyKeyLegacyEncoder.TryEncodeLegacy(Ascii(kitty), out var bytes));
        Assert.Equal(Encoding.UTF8.GetBytes(legacy), bytes);
    }

    [Theory]
    [InlineData("\u001b[A")]
    [InlineData("\u001b[1;5C")]
    [InlineData("\u001b[3~")]
    [InlineData("\u001b[200~")]
    [InlineData("\u001b[1;2P")]
    public void Legacy_forms_are_not_converted(string raw)
    {
        Assert.False(KittyKeyLegacyEncoder.TryEncodeLegacy(Ascii(raw), out _));
    }

    [Fact]
    public void Kitty_key_with_no_legacy_encoding_sends_nothing()
    {
        Assert.True(KittyKeyLegacyEncoder.TryEncodeLegacy(Ascii("\u001b[57428u"), out var bytes));
        Assert.Empty(bytes);

        var engine = DefaultEngine();
        engine.HostKittyKeyboard = true;
        Assert.Empty(engine.Feed(Ascii("\u001b[57428u")));
    }

    [Fact]
    public void Kitty_reports_pass_unchanged_when_the_host_is_in_legacy_mode()
    {
        // A host keybind can send CSI u text on a legacy host. Keep it.
        var engine = DefaultEngine();
        Assert.Equal(Ascii("\u001b[13;2u"), PaneBytes(engine.Feed(Ascii("\u001b[13;2u"))));
    }

    [Fact]
    public void Kitty_flags_reply_gives_no_keys_and_marks_the_host()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        var keys = Decode(framer, Ascii("\u001b[?0u\u001b[?62;22c"));

        Assert.Empty(keys);
        Assert.Empty(framer.Pending);
        Assert.True(framer.KittyKeyboardReplied);
        Assert.Equal(0, framer.HostKittyKeyboardFlags);
        Assert.True(framer.TakeKittyKeyboardReply());
        Assert.False(framer.TakeKittyKeyboardReply());
    }

    [Fact]
    public void Device_attributes_reply_alone_leaves_the_host_in_legacy_mode()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        var keys = Decode(framer, Ascii("\u001b[?1;2c"));

        Assert.Empty(keys);
        Assert.True(framer.PrimaryDeviceAttributesReplied);
        Assert.False(framer.KittyKeyboardReplied);
        Assert.False(framer.TakeKittyKeyboardReply());

        // A flags report after DA1 does not answer the probe.
        Assert.Empty(Decode(framer, Ascii("\u001b[?1u")));
        Assert.False(framer.TakeKittyKeyboardReply());
    }

    [Fact]
    public void Activation_pushes_once_and_restore_pops_once()
    {
        var output = new MemoryStream();
        var tty = new UnixRawTerminal(output, 80, 24);
        var guard = new RecordingGuard();
        tty.ModeGuardFactory = () => guard;
        tty.EnterRaw();
        tty.EnterClientOverlay();
        var live = NewLive(new AttachUiConfig());

        Assert.True(AttachSession.ActivateHostKittyKeyboard(tty, live));
        Assert.False(AttachSession.ActivateHostKittyKeyboard(tty, live));
        Assert.True(live.Engine.HostKittyKeyboard);
        Assert.Equal(1, guard.Armed);
        Assert.Equal(1, guard.Pushing);

        tty.Restore();
        tty.Restore();

        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal(1, Count(text, HostKeyboardMode.PushSequence));
        Assert.Equal(1, Count(text, HostKeyboardMode.PopSequence));
        // The pop goes to the alternate screen, where the push went.
        Assert.True(text.IndexOf(HostKeyboardMode.PopSequence, StringComparison.Ordinal)
            < text.LastIndexOf(SnapshotPainter.LeaveAltScreen, StringComparison.Ordinal));
        Assert.Equal(1, guard.Disarmed);
    }

    [Fact]
    public void Refresh_after_focus_or_resize_does_not_grow_the_host_stack()
    {
        var output = new MemoryStream();
        var tty = new UnixRawTerminal(output, 80, 24);
        tty.ModeGuardFactory = () => new RecordingGuard();
        tty.EnterRaw();
        Assert.True(tty.PushKittyKeyboard());

        tty.RefreshKittyKeyboard();
        tty.RefreshKittyKeyboard();
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal(1, Count(text, HostKeyboardMode.PushSequence) - Count(text, HostKeyboardMode.PopSequence));

        tty.Restore();
        text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Equal(0, Count(text, HostKeyboardMode.PushSequence) - Count(text, HostKeyboardMode.PopSequence));
    }

    [Fact]
    public void No_push_without_an_armed_host_mode_guard()
    {
        foreach (var factory in new Func<IHostModeGuard?>[] { () => new RecordingGuard { CanArm = false }, () => null })
        {
            var output = new MemoryStream();
            var tty = new UnixRawTerminal(output, 80, 24);
            tty.ModeGuardFactory = factory;
            tty.EnterRaw();
            var live = NewLive(new AttachUiConfig());

            Assert.False(tty.ModeGuardArmed);
            Assert.False(AttachSession.ActivateHostKittyKeyboard(tty, live));

            Assert.False(live.Engine.HostKittyKeyboard);
            Assert.False(tty.KittyKeyboardPushed);
            Assert.DoesNotContain(HostKeyboardMode.PushSequence, Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_push_when_the_guard_process_is_gone()
    {
        var output = new MemoryStream();
        var tty = new UnixRawTerminal(output, 80, 24);
        var guard = new RecordingGuard { CanNotePushing = false };
        tty.ModeGuardFactory = () => guard;
        tty.EnterRaw();

        Assert.False(tty.PushKittyKeyboard());

        Assert.False(tty.KittyKeyboardPushed);
        Assert.DoesNotContain(HostKeyboardMode.PushSequence, Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_push_keeps_the_guard_armed_with_no_pop()
    {
        var tty = new UnixRawTerminal(() => (-1, 5));
        var guard = new RecordingGuard();
        tty.ModeGuardFactory = () => guard;
        tty.EnterRaw();

        Assert.False(tty.PushKittyKeyboard());

        Assert.Equal(1, guard.Pushing);
        Assert.Equal(1, guard.Popped);
        Assert.Equal(0, guard.Disarmed);
        Assert.True(tty.ModeGuardArmed);
    }

    [Fact]
    public void Failed_pop_keeps_the_crash_guard_armed()
    {
        var calls = 0;
        var tty = new UnixRawTerminal(() =>
        {
            calls++;
            // The push goes out. The pop fails: the host TTY is gone (EIO).
            return calls == 1 ? (HostKeyboardMode.PushSequence.Length, 0) : (-1, 5);
        });
        var guard = new RecordingGuard();
        tty.ModeGuardFactory = () => guard;
        tty.EnterRaw();
        Assert.True(tty.PushKittyKeyboard());

        tty.Restore();

        Assert.Equal(0, guard.Disarmed);
        Assert.Equal(0, guard.Popped);
        Assert.True(tty.ModeGuardArmed);
    }

    [Fact]
    public void No_push_after_a_restore_started()
    {
        var output = new MemoryStream();
        var tty = new UnixRawTerminal(output, 80, 24);
        var guard = new RecordingGuard();
        tty.ModeGuardFactory = () => guard;
        tty.EnterRaw();
        tty.Restore();

        Assert.False(tty.PushKittyKeyboard());
        Assert.Equal(0, guard.Pushing);
        Assert.Equal(1, guard.Disarmed);
    }

    [Fact]
    public void Refresh_after_a_failed_restore_pop_writes_nothing()
    {
        var calls = 0;
        var writes = 0;
        var tty = new UnixRawTerminal(() =>
        {
            calls++;
            if (calls == 1)
                return (HostKeyboardMode.PushSequence.Length, 0);
            writes++;
            return (-1, 5);
        });
        tty.ModeGuardFactory = () => new RecordingGuard();
        tty.EnterRaw();
        Assert.True(tty.PushKittyKeyboard());
        tty.Restore();
        var after = writes;

        tty.RefreshKittyKeyboard();

        Assert.Equal(after, writes);
    }

    [Fact]
    public void Refresh_without_a_push_writes_nothing()
    {
        var output = new MemoryStream();
        var tty = new UnixRawTerminal(output, 80, 24);
        tty.EnterRaw();
        var before = output.Length;

        tty.RefreshKittyKeyboard();

        Assert.Equal(before, output.Length);
        tty.Restore();
        Assert.DoesNotContain(HostKeyboardMode.PopSequence, Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Config_off_keeps_the_host_in_legacy_mode()
    {
        var output = new MemoryStream();
        var tty = new UnixRawTerminal(output, 80, 24);
        tty.ModeGuardFactory = () => new RecordingGuard();
        tty.EnterRaw();
        var live = NewLive(new AttachUiConfig { KittyKeyboard = false });

        Assert.False(AttachSession.ActivateHostKittyKeyboard(tty, live));
        Assert.False(live.Engine.HostKittyKeyboard);
        Assert.DoesNotContain(HostKeyboardMode.PushSequence, Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void Lone_escape_waits_ten_ms_with_mouse_capture_off_and_fifty_with_it_on()
    {
        var csi = new List<byte> { 0x1B };

        Assert.Equal(10, AttachSession.StdinPollTimeoutMs(csi, mouseCapture: false));
        Assert.Equal(50, AttachSession.StdinPollTimeoutMs(csi, mouseCapture: true));
    }

    [Fact]
    public void Lone_escape_keeps_the_full_wait_while_host_replies_are_due()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        framer.NoteHostColorQuerySent();

        Assert.Equal(
            50,
            AttachSession.StdinPollTimeoutMs([0x1B], mouseCapture: false, framer: framer));
    }

    [Fact]
    public void Lone_escape_keeps_the_full_wait_until_the_keyboard_probe_ends()
    {
        var framer = new AttachHostInputFramer(new ManualTimeProvider());
        framer.NoteKeyboardProbeSent();
        Assert.Equal(50, AttachSession.StdinPollTimeoutMs([0x1B], mouseCapture: false, framer: framer));

        Decode(framer, Ascii("\u001b[?62c"));

        Assert.Equal(10, AttachSession.StdinPollTimeoutMs([0x1B], mouseCapture: false, framer: framer));
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(part, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += part.Length;
        }

        return count;
    }

    private static byte[] PaneBytes(IReadOnlyList<KeyEngineEvent> events)
    {
        var bytes = new List<byte>();
        foreach (var e in events)
        {
            Assert.Equal(KeyEngineEventKind.SendPaneBytes, e.Kind);
            bytes.AddRange(e.Bytes ?? []);
        }

        return [.. bytes];
    }

    private static List<byte> Decode(AttachHostInputFramer framer, ReadOnlySpan<byte> raw) =>
        AttachSession.DecodeInput(
            raw,
            framer.Pending,
            out _,
            out _,
            out _,
            out _,
            out _,
            mouseReports: null,
            framer);

    [Fact]
    public void Kitty_probe_goes_before_the_colour_query_burst()
    {
        var capture = new MemoryStream();
        var tty = new UnixRawTerminal(capture, 80, 24);
        var live = NewLive(new AttachUiConfig { KittyKeyboard = true });

        AttachSession.WriteHostStartQueries(tty, live);

        var written = Encoding.UTF8.GetString(capture.ToArray());
        var probe = written.IndexOf(HostKeyboardMode.ProbeSequence, StringComparison.Ordinal);
        var palette = written.IndexOf("\u001b]4;", StringComparison.Ordinal);
        Assert.True(probe >= 0, "no kitty probe");
        Assert.True(palette > probe, "the probe must go before the OSC 4 queries");
        Assert.True(live.HostInput.KeyboardProbeOutstanding);
    }

    [Fact]
    public void No_kitty_probe_when_the_mode_is_off()
    {
        var capture = new MemoryStream();
        var tty = new UnixRawTerminal(capture, 80, 24);
        var live = NewLive(new AttachUiConfig { KittyKeyboard = false });

        AttachSession.WriteHostStartQueries(tty, live);

        Assert.DoesNotContain(HostKeyboardMode.ProbeSequence, Encoding.UTF8.GetString(capture.ToArray()), StringComparison.Ordinal);
        Assert.False(live.HostInput.KeyboardProbeOutstanding);
    }

    private static AttachLiveState NewLive(AttachUiConfig ui) => new()
    {
        Engine = DefaultEngine(),
        Table = KeyBindingTable.CompileOrThrow(KeysConfig.Default()),
        Dispatcher = null!,
        Theme = Hypa.AgentRuntime.Domain.Theme.ThemeRuntime.Default,
        Ui = ui,
    };

    internal sealed class RecordingGuard : IHostModeGuard
    {
        public int Armed { get; private set; }

        public int Disarmed { get; private set; }

        public bool CanArm { get; init; } = true;

        public bool CanNotePushing { get; init; } = true;

        public int Pushing { get; private set; }

        public int Popped { get; private set; }

        /// <summary>Runs at each arm, before the guard reports armed.</summary>
        public Action? OnArm { get; init; }

        public bool NotePushing()
        {
            if (!CanNotePushing)
                return false;
            Pushing++;
            return Armed > 0;
        }

        public void NotePopped() => Popped++;

        public bool Arm()
        {
            OnArm?.Invoke();
            if (!CanArm)
                return false;
            Armed++;
            return true;
        }

        public void Disarm() => Disarmed++;

        public void Dispose()
        {
        }
    }
}
