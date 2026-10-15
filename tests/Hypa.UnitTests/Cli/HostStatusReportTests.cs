using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.AgentRuntime.Protocol;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Keys;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The attach reports the session summary to the host terminal (OSC 7501).
/// The mux computes the summary. The attach encodes it and writes it through the host writer.
/// </summary>
public sealed class HostStatusReportTests
{
    private static SessionProgramStatusView Summary(
        ProgramStatusState state = ProgramStatusState.Blocked,
        string msg = "second: Approve deploy?",
        ProgramStatusKind? kind = ProgramStatusKind.Permission,
        int? progress = null) =>
        new()
        {
            State = state,
            Kind = kind,
            Progress = progress,
            App = "deploy",
            PaneId = "p_2",
            Msg = msg,
            Source = new ProgramStatusSourceLabel("work", "build", "second"),
        };

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // ---- encoder ----

    [Fact]
    public void Blocked_encodes_state_kind_app_and_a_base64_message()
    {
        var report = HostStatusReport.Encode(Summary());

        Assert.Equal(
            $"\u001b]7501;state=blocked:kind=permission:app=hypa:msg={B64("second: Approve deploy?")}\u001b\\",
            report);
    }

    [Fact]
    public void Working_encodes_progress_and_no_kind()
    {
        var report = HostStatusReport.Encode(Summary(ProgramStatusState.Working, "build: Compiling", ProgramStatusKind.Auth, 40));

        Assert.Equal($"\u001b]7501;state=working:progress=40:app=hypa:msg={B64("build: Compiling")}\u001b\\", report);
    }

    [Fact]
    public void Working_without_progress_is_indeterminate()
    {
        var report = HostStatusReport.Encode(Summary(ProgramStatusState.Working, "build", null));

        Assert.DoesNotContain("progress", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_is_clamped_to_the_spec_range()
    {
        Assert.Contains(":progress=100:", HostStatusReport.Encode(Summary(ProgramStatusState.Working, "a", null, 250)), StringComparison.Ordinal);
        Assert.Contains(":progress=0:", HostStatusReport.Encode(Summary(ProgramStatusState.Working, "a", null, -5)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProgramStatusState.Done, "done")]
    [InlineData(ProgramStatusState.Error, "error")]
    [InlineData(ProgramStatusState.Idle, "idle")]
    public void Other_states_carry_neither_kind_nor_progress(ProgramStatusState state, string wire)
    {
        var report = HostStatusReport.Encode(Summary(state, "x: y", ProgramStatusKind.Question, 50));

        Assert.StartsWith($"\u001b]7501;state={wire}:app=hypa:msg=", report, StringComparison.Ordinal);
    }

    [Fact]
    public void No_summary_encodes_clear() =>
        Assert.Equal("\u001b]7501;state=clear\u001b\\", HostStatusReport.Encode(null));

    [Fact]
    public void The_message_decodes_to_utf8_text_without_control_characters()
    {
        var report = HostStatusReport.Encode(Summary(msg: "päne: 日本語 \u001b[31m\u009b done"));

        var b64 = report.Split("msg=")[1].TrimEnd('\\', '\u001b');
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Equal("päne: 日本語 [31m done", decoded);
        Assert.DoesNotContain(decoded, c => char.IsControl(c));
    }

    [Fact]
    public void A_long_message_is_cut_at_the_decoded_limit_on_a_character_boundary()
    {
        var report = HostStatusReport.Encode(Summary(msg: new string('語', 2000)));

        var b64 = report.Split("msg=")[1].TrimEnd('\\', '\u001b');
        var bytes = Convert.FromBase64String(b64);
        Assert.True(bytes.Length <= HostStatusReport.MaxDecodedMsgBytes);
        Assert.Equal(0, bytes.Length % 3);
        Assert.True(report.Length < 3300);
    }

    [Theory]
    [InlineData("\u001b]7501;?\u001b\\", true)]
    [InlineData("\u001b]7501;?\u0007", true)]
    [InlineData("\u001b]7501;?;future=1\u001b\\", true)]
    [InlineData("\u001b]7501;state=working\u001b\\", false)]
    [InlineData("\u001b]7502;?\u001b\\", false)]
    [InlineData("\u001b]7501;?x\u001b\\", false)]
    [InlineData("\u001b]7501;?", false)]
    public void The_detection_reply_is_recognised(string sequence, bool expected) =>
        Assert.Equal(expected, HostStatusReport.IsDetectionReply(Encoding.ASCII.GetBytes(sequence)));

    // ---- setting and detection ----

    [Fact]
    public void Auto_without_a_host_reply_sends_no_report()
    {
        var reporter = new HostStatusReporter();

        Assert.Equal(HostStatusReport.Query, reporter.TakeQuery(HostStatusMode.Auto));
        Assert.Equal(HostStatusReport.Clear, reporter.Sequence(HostStatusMode.Auto, Summary()));
    }

    [Fact]
    public void Auto_with_a_host_reply_sends_reports()
    {
        var reporter = new HostStatusReporter();
        _ = reporter.TakeQuery(HostStatusMode.Auto);
        reporter.NoteHostReply();

        Assert.Equal(HostStatusReport.Encode(Summary()), reporter.Sequence(HostStatusMode.Auto, Summary()));
        Assert.Equal(HostStatusReport.Clear, reporter.Sequence(HostStatusMode.Auto, null));
    }

    [Fact]
    public void Auto_sends_the_query_once_for_each_attach()
    {
        var reporter = new HostStatusReporter();

        Assert.NotNull(reporter.TakeQuery(HostStatusMode.Auto));
        Assert.Null(reporter.TakeQuery(HostStatusMode.Auto));
    }

    [Fact]
    public void On_sends_reports_without_a_query()
    {
        var reporter = new HostStatusReporter();

        Assert.Null(reporter.TakeQuery(HostStatusMode.On));
        Assert.Equal(HostStatusReport.Encode(Summary()), reporter.Sequence(HostStatusMode.On, Summary()));
    }

    [Fact]
    public void Off_sends_no_query_and_never_a_report_even_after_a_reply()
    {
        var reporter = new HostStatusReporter();
        reporter.NoteHostReply();

        Assert.Null(reporter.TakeQuery(HostStatusMode.Off));
        Assert.Equal(HostStatusReport.Clear, reporter.Sequence(HostStatusMode.Off, Summary()));
        Assert.Equal(HostStatusReport.Clear, reporter.Sequence(HostStatusMode.Off, null));
    }

    // ---- host reply on the input path ----

    [Fact]
    public void The_host_reply_is_read_from_stdin_and_not_passed_as_keys()
    {
        var csi = new List<byte>();
        var raw = Encoding.ASCII.GetBytes("a\u001b]7501;?\u001b\\b");

        var keys = AttachSession.DecodeInput(
            raw, csi, out _, out _, out _, out _, out var theme);

        Assert.True(theme.HostStatusReply);
        Assert.Equal("ab", Encoding.ASCII.GetString(keys.ToArray()));
    }

    [Fact]
    public void The_host_reply_can_arrive_in_two_reads()
    {
        var csi = new List<byte>();
        AttachSession.DecodeInput(Encoding.ASCII.GetBytes("\u001b]75"), csi, out _, out _, out _, out _, out var first);
        AttachSession.DecodeInput(Encoding.ASCII.GetBytes("01;?\u001b\\"), csi, out _, out _, out _, out _, out var second);

        Assert.False(first.HostStatusReply);
        Assert.True(second.HostStatusReply);
    }

    [Fact]
    public void A_theme_reply_is_not_a_status_reply()
    {
        var csi = new List<byte>();
        AttachSession.DecodeInput(
            Encoding.ASCII.GetBytes("\u001b]11;rgb:0000/0000/0000\u001b\\"), csi, out _, out _, out _, out _, out var theme);

        Assert.False(theme.HostStatusReply);
    }

    // ---- host writer ----

    private static (UnixRawTerminal Tty, MemoryStream Out) Terminal()
    {
        var capture = new MemoryStream();
        return (new UnixRawTerminal(capture, 80, 24), capture);
    }

    private static string Written(MemoryStream capture) => Encoding.UTF8.GetString(capture.ToArray());

    [Fact]
    public void A_summary_that_does_not_change_sends_no_second_report()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        var report = HostStatusReport.Encode(Summary());

        tty.WriteHostStatus(report);
        tty.WriteHostStatus(report);
        tty.WriteHostStatus(report);

        Assert.Equal(report, Written(capture));
    }

    [Fact]
    public void A_changed_summary_sends_a_new_report()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        var first = HostStatusReport.Encode(Summary());
        var second = HostStatusReport.Encode(Summary(ProgramStatusState.Done, "second: Done", null));

        tty.WriteHostStatus(first);
        tty.WriteHostStatus(second);
        tty.WriteHostStatus(first);

        Assert.Equal(first + second + first, Written(capture));
    }

    [Fact]
    public void A_clear_with_no_report_before_it_is_not_sent()
    {
        var (tty, capture) = Terminal();
        using var term = tty;

        tty.WriteHostStatus(HostStatusReport.Clear);

        Assert.Equal("", Written(capture));
    }

    [Fact]
    public void Clear_after_a_report_is_sent_once_and_the_next_report_is_sent_again()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        var report = HostStatusReport.Encode(Summary());

        tty.WriteHostStatus(report);
        tty.WriteHostStatus(HostStatusReport.Clear);
        tty.WriteHostStatus(HostStatusReport.Clear);
        tty.WriteHostStatus(report);

        Assert.Equal(report + HostStatusReport.Clear + report, Written(capture));
    }

    [Fact]
    public void Detach_sends_clear_after_a_report()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        tty.WriteHostStatus(HostStatusReport.Encode(Summary()));

        tty.Restore();

        Assert.EndsWith(HostStatusReport.Clear, Written(capture), StringComparison.Ordinal);
    }

    [Fact]
    public void Detach_sends_no_clear_when_there_was_no_report()
    {
        var (tty, capture) = Terminal();
        using var term = tty;

        tty.Restore();

        Assert.DoesNotContain("7501", Written(capture), StringComparison.Ordinal);
    }

    [Fact]
    public void Detach_sends_no_second_clear_after_the_summary_was_cleared()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        tty.WriteHostStatus(HostStatusReport.Encode(Summary()));
        tty.WriteHostStatus(HostStatusReport.Clear);

        tty.Restore();

        var text = Written(capture);
        Assert.Equal(1, text.Split(HostStatusReport.Clear).Length - 1);
    }

    // ---- attach state ----

    private static AttachLiveState Live(HostStatusMode mode)
    {
        var table = KeyBindingTable.CompileOrThrow(KeysConfig.Default());
        return new AttachLiveState
        {
            Engine = new KeyEngine(table),
            Table = table,
            Dispatcher = new AttachCommandDispatcher(new SilentPort(), "w1", "t1", "p1", "lease"),
            ChromeEnabled = false,
            PaneId = "p1",
            Ui = AttachUiConfig.Default with { HostStatus = mode },
        };
    }

    private static JsonDocument StatusChanged(SessionProgramStatusView? session, int occupantGeneration = 1)
    {
        var payload = new JsonObject
        {
            ["pane_id"] = "p_2",
            ["occupant_generation"] = occupantGeneration,
            ["tab_id"] = "t1",
            ["workspace_id"] = "w1",
            ["program_status"] = new JsonArray(),
            ["view"] = null,
            ["tab_view"] = null,
            ["session_view"] = ProgramStatusViewWire.ToNode(session),
        };
        return JsonDocument.Parse(new JsonObject
        {
            ["event"] = ProtocolEventTypes.RuntimeEvent,
            ["params"] = new JsonObject
            {
                ["type"] = ProtocolEventTypes.PaneProgramStatusChanged,
                ["payload"] = payload,
            },
        }.ToJsonString());
    }

    [Fact]
    public void An_event_after_a_host_reply_reports_once_and_a_repeat_sends_nothing()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        var live = Live(HostStatusMode.Auto);
        _ = live.HostStatus.TakeQuery(live.Ui.HostStatus);

        using var before = StatusChanged(Summary());
        AttachSession.ApplyNotifyEvent(live, before.RootElement, tty);
        Assert.Equal("", Written(capture));

        live.HostStatus.NoteHostReply();
        AttachSession.ApplyHostStatus(tty, live);
        AttachSession.ApplyNotifyEvent(live, before.RootElement, tty);

        Assert.Equal(HostStatusReport.Encode(Summary()), Written(capture));
    }

    [Fact]
    public void Off_writes_nothing_for_an_event()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        var live = Live(HostStatusMode.Off);

        using var doc = StatusChanged(Summary());
        AttachSession.ApplyNotifyEvent(live, doc.RootElement, tty);

        Assert.Equal("", Written(capture));
        Assert.NotNull(live.SessionStatus);
    }

    [Fact]
    public void A_change_to_off_at_run_time_sends_one_clear_when_a_report_is_outstanding()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        var live = Live(HostStatusMode.On);

        using var blocked = StatusChanged(Summary());
        AttachSession.ApplyNotifyEvent(live, blocked.RootElement, tty);
        live.Ui = live.Ui with { HostStatus = HostStatusMode.Off };
        AttachSession.ApplyHostStatus(tty, live);
        AttachSession.ApplyHostStatus(tty, live);
        using var other = StatusChanged(Summary(msg: "second: Approve again?"));
        AttachSession.ApplyNotifyEvent(live, other.RootElement, tty);

        Assert.Equal(HostStatusReport.Encode(Summary()) + HostStatusReport.Clear, Written(capture));
    }

    [Fact]
    public void A_change_to_off_sends_no_clear_when_no_report_was_sent()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        var live = Live(HostStatusMode.Off);

        AttachSession.ApplyHostStatus(tty, live);

        Assert.Equal("", Written(capture));
    }

    [Fact]
    public void On_reports_an_event_and_clears_when_the_last_record_goes()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        var live = Live(HostStatusMode.On);

        using var blocked = StatusChanged(Summary());
        using var gone = StatusChanged(null);
        AttachSession.ApplyNotifyEvent(live, blocked.RootElement, tty);
        AttachSession.ApplyNotifyEvent(live, gone.RootElement, tty);

        Assert.Equal(HostStatusReport.Encode(Summary()) + HostStatusReport.Clear, Written(capture));
        Assert.Null(live.SessionStatus);
    }

    [Fact]
    public void A_status_event_of_an_older_occupant_changes_nothing()
    {
        var (tty, capture) = Terminal();
        using var term = tty;
        var live = Live(HostStatusMode.On);

        using var current = StatusChanged(Summary(), occupantGeneration: 2);
        using var stale = StatusChanged(null, occupantGeneration: 1);
        AttachSession.ApplyNotifyEvent(live, current.RootElement, tty);
        var written = Written(capture);
        AttachSession.ApplyNotifyEvent(live, stale.RootElement, tty);

        Assert.Equal(written, Written(capture));
        Assert.Equal(Summary(), live.SessionStatus);
    }

    [Fact]
    public void A_snapshot_replaces_the_summary_so_a_new_mux_or_another_cube_is_followed()
    {
        var withSession = JsonDocument.Parse(new JsonObject
        {
            ["agent_view"] = new JsonObject
            {
                ["active"] = false,
                ["session"] = ProgramStatusViewWire.ToNode(Summary()),
            },
        }.ToJsonString());
        var without = JsonDocument.Parse("""{"agent_view":{"active":false}}""");

        Assert.Equal(Summary(), Hypa.Cli.Attach.Sidebar.SidebarLiveModel.ReadSessionStatus(withSession.RootElement));
        Assert.Null(Hypa.Cli.Attach.Sidebar.SidebarLiveModel.ReadSessionStatus(without.RootElement));
        Assert.Null(Hypa.Cli.Attach.Sidebar.SidebarLiveModel.ReadSessionStatus(JsonDocument.Parse("{}").RootElement));
    }

    private sealed class SilentPort : IAttachCommandPort
    {
        public Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
            Task.FromResult(default(JsonElement));
    }
}
