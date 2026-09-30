using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Input;
using Hypa.Cli.Attach.Keys;
using Hypa.Cli.Attach.Worktrees;
using Xunit;

namespace Hypa.UnitTests.Cli;

public sealed class PrefixAsciiInputAttachTests
{
    [Fact]
    public void Disabled_prefix_switch_does_not_call_the_port()
    {
        var source = new RecordingPrefixAsciiInputSource();
        var live = Live(enabled: false, source);
        foreach (var ev in live.Engine.Feed(KeyChord.Parse("ctrl+b")))
            _ = ev;
        AttachSession.ReconcilePrefixAsciiInput(live);
        Assert.Empty(source.Calls);
        Assert.Equal(AttachClientMode.Prefix, live.Engine.Mode);
    }

    [Fact]
    public void Prefix_mode_switches_to_ascii_and_terminal_restores()
    {
        var source = new RecordingPrefixAsciiInputSource();
        var live = Live(enabled: true, source);
        live.Engine.Feed(KeyChord.Parse("ctrl+b"));
        AttachSession.ReconcilePrefixAsciiInput(live);
        Assert.Equal(["switch"], source.Calls);
        live.Engine.Feed(KeyChord.Parse("esc"));
        AttachSession.ReconcilePrefixAsciiInput(live);
        Assert.Equal(["switch", "restore"], source.Calls);
    }

    [Fact]
    public void Detach_from_prefix_restores_the_input_source()
    {
        var source = new RecordingPrefixAsciiInputSource();
        var live = Live(enabled: true, source);
        live.Engine.Feed(KeyChord.Parse("ctrl+b"));
        AttachSession.ReconcilePrefixAsciiInput(live);
        Assert.Equal(["switch"], source.Calls);
        Assert.Equal(AttachClientMode.Prefix, live.Engine.Mode);

        var events = live.Engine.Feed(KeyChord.Parse("q"));
        Assert.Contains(events, ev => ev.Kind == KeyEngineEventKind.Detach);
        Assert.Equal(AttachClientMode.Prefix, live.Engine.Mode);
        AttachSession.ReconcilePrefixAsciiInput(live);
        Assert.Equal(["switch"], source.Calls);

        AttachSession.RestorePrefixAsciiInput(live);
        Assert.Equal(["switch", "restore"], source.Calls);
        AttachSession.RestorePrefixAsciiInput(null);
    }

    [Fact]
    public void Linux_and_other_platforms_can_use_the_no_op_adapter()
    {
        var live = Live(enabled: true, NoOpPrefixAsciiInputSource.Instance);
        live.Engine.Feed(KeyChord.Parse("ctrl+b"));
        AttachSession.ReconcilePrefixAsciiInput(live);
        live.Engine.Feed(KeyChord.Parse("esc"));
        AttachSession.ReconcilePrefixAsciiInput(live);
        Assert.Equal(AttachClientMode.Terminal, live.Engine.Mode);
    }

    [Fact]
    public void Prefix_and_help_want_ascii_rename_does_not()
    {
        Assert.True(PrefixAsciiNeed.WantsAscii(AttachClientMode.Prefix, WorktreeDialogKind.None));
        Assert.True(PrefixAsciiNeed.WantsAscii(AttachClientMode.KeybindHelp, WorktreeDialogKind.None));
        Assert.True(PrefixAsciiNeed.WantsAscii(AttachClientMode.Terminal, WorktreeDialogKind.Remove));
        Assert.False(PrefixAsciiNeed.WantsAscii(AttachClientMode.Terminal, WorktreeDialogKind.None));
        Assert.False(PrefixAsciiNeed.WantsAscii(AttachClientMode.RenamePane, WorktreeDialogKind.None));
        Assert.False(PrefixAsciiNeed.WantsAscii(AttachClientMode.Settings, WorktreeDialogKind.Create));
    }

    [Fact]
    public void Send_input_record_matches_win32_input_size()
    {
        var expected = nint.Size == 8 ? 40 : 28;
        Assert.Equal(expected, NativeWindowsImeProbe.SendInputRecordSize);
    }

    private static AttachLiveState Live(bool enabled, IPrefixAsciiInputSource source)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        var live = new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = null!,
            AttachConfig = AttachClientConfig.Default with
            {
                Experimental = new AttachExperimentalConfig
                {
                    SwitchAsciiInputSourceInPrefix = enabled,
                },
            },
            PrefixAsciiSource = source,
        };
        live.PrefixAscii.Enabled = enabled;
        return live;
    }

    private sealed class RecordingPrefixAsciiInputSource : IPrefixAsciiInputSource
    {
        public List<string> Calls { get; } = [];

        public void SwitchToAscii() => Calls.Add("switch");

        public void Restore() => Calls.Add("restore");
    }
}
