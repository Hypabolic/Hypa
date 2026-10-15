using System.Text;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.Terminal.Vt;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusOscScannerTests
{
    private const string Brew = "state=working:app=brew:msg=SW5zdGFsbGluZyB1cGRhdGVz";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static List<ProgramStatusSignal> Scan(OscTestFeed feed, params string[] chunks)
    {
        var signals = new List<ProgramStatusSignal>();
        foreach (var chunk in chunks)
            feed.Observe(Bytes(chunk), signals);
        return signals;
    }

    private static OscTestFeed NewFeed() => OscTestFeed.For(new ProgramStatusOscScanner());

    private static List<ProgramStatusSignal> Scan(params string[] chunks) => Scan(NewFeed(), chunks);

    [Fact]
    public void Report_with_string_terminator_is_captured()
    {
        var signal = Assert.Single(Scan($"\u001b]7501;{Brew}\u001b\\"));
        Assert.Equal(ProgramStatusSignalKind.Report, signal.Kind);
        Assert.Equal(ProgramStatusState.Working, signal.Report!.State);
        Assert.Equal("brew", signal.Report.App);
        Assert.Equal("Installing updates", signal.Report.Msg);
    }

    [Fact]
    public void Report_with_bel_terminator_is_captured()
    {
        var signal = Assert.Single(Scan($"\u001b]7501;{Brew}\u0007"));
        Assert.Equal("Installing updates", signal.Report!.Msg);
    }

    [Fact]
    public void Report_split_in_two_chunks_is_captured()
    {
        var signal = Assert.Single(Scan("text\u001b]75", $"01;{Brew}\u001b\\more"));
        Assert.Equal("brew", signal.Report!.App);
    }

    [Fact]
    public void Report_split_between_esc_and_backslash_is_captured()
    {
        var signal = Assert.Single(Scan($"\u001b]7501;{Brew}\u001b", "\\"));
        Assert.Equal("brew", signal.Report!.App);
    }

    [Fact]
    public void Report_split_before_the_introducer_is_captured()
    {
        var signal = Assert.Single(Scan("\u001b", $"]7501;{Brew}\u0007"));
        Assert.Equal("brew", signal.Report!.App);
    }

    [Fact]
    public void Report_split_one_byte_at_a_time_is_captured()
    {
        var scanner = NewFeed();
        var signals = new List<ProgramStatusSignal>();
        foreach (var b in Bytes($"\u001b]7501;{Brew}\u001b\\"))
            scanner.Observe([b], signals);
        Assert.Equal("brew", Assert.Single(signals).Report!.App);
    }

    [Fact]
    public void Query_is_reported_with_either_terminator()
    {
        Assert.Equal(ProgramStatusSignalKind.Query, Assert.Single(Scan("\u001b]7501;?\u001b\\")).Kind);
        Assert.Equal(ProgramStatusSignalKind.Query, Assert.Single(Scan("\u001b]7501;?\u0007")).Kind);
    }

    [Fact]
    public void Query_with_extra_bytes_is_not_a_query_or_a_report()
    {
        Assert.Empty(Scan("\u001b]7501;?x\u001b\\"));
    }

    [Fact]
    public void Ris_is_reported_in_order_with_reports()
    {
        var signals = Scan($"\u001b]7501;state=done\u0007\u001bc\u001b]7501;state=idle\u0007");
        Assert.Equal(
            [ProgramStatusSignalKind.Report, ProgramStatusSignalKind.Reset, ProgramStatusSignalKind.Report],
            signals.Select(s => s.Kind).ToArray());
    }

    [Fact]
    public void Ris_split_over_chunks_is_reported()
    {
        Assert.Equal(ProgramStatusSignalKind.Reset, Assert.Single(Scan("\u001b", "c")).Kind);
    }

    [Fact]
    public void Soft_reset_is_not_a_signal()
    {
        Assert.Empty(Scan("\u001b[!p"));
    }

    [Fact]
    public void Other_osc_sequences_are_ignored()
    {
        Assert.Empty(Scan("\u001b]0;title\u0007\u001b]7;file:///tmp\u001b\\\u001b]75010;x\u0007\u001b]750;x\u0007"));
    }

    [Fact]
    public void Rejected_report_gives_no_signal()
    {
        Assert.Empty(Scan("\u001b]7501;state=bogus\u0007"));
        Assert.Empty(Scan("\u001b]7501;state=done:msg=a-b\u0007"));
    }

    [Fact]
    public void Body_over_the_cap_gives_no_signal_and_the_next_report_works_after_either_terminator()
    {
        var scanner = NewFeed();
        var huge = "state=done:future=" + new string('a', 5000);
        Assert.Empty(Scan(scanner, $"\u001b]7501;{huge}\u0007"));
        Assert.Empty(Scan(scanner, $"\u001b]7501;{huge}\u001b\\"));
        var signal = Assert.Single(Scan(scanner, $"\u001b]7501;{Brew}\u0007"));
        Assert.Equal("brew", signal.Report!.App);
    }

    [Fact]
    public void Report_that_fills_the_4096_byte_sequence_works_and_one_more_byte_does_not()
    {
        var prefix = "state=done:future=";
        var fits = prefix + new string('a', 4096 - 7 - 2 - prefix.Length);
        Assert.Single(Scan($"\u001b]7501;{fits}\u001b\\"));
        Assert.Empty(Scan($"\u001b]7501;{fits}a\u001b\\"));
    }

    [Fact]
    public void Oversize_report_split_over_chunks_gives_no_signal()
    {
        var scanner = NewFeed();
        var huge = "state=done:future=" + new string('a', 5000);
        Assert.Empty(Scan(scanner, "\u001b]7501;" + huge[..2500], huge[2500..] + "\u0007"));
        Assert.Single(Scan(scanner, $"\u001b]7501;{Brew}\u0007"));
    }

    [Fact]
    public void Escape_in_the_body_ends_the_sequence_and_starts_a_new_one()
    {
        var signals = Scan("\u001b]7501;state=working\u001b]7501;state=done\u0007");
        Assert.Equal(ProgramStatusState.Done, Assert.Single(signals).Report!.State);
    }

    [Fact]
    public void Ris_after_an_aborted_report_is_seen()
    {
        Assert.Equal(ProgramStatusSignalKind.Reset, Assert.Single(Scan("\u001b]7501;state=working\u001bc")).Kind);
    }

    [Fact]
    public void Dcs_string_is_skipped_and_the_next_report_works()
    {
        var signal = Assert.Single(Scan("\u001bPqdata\u001b\\", $"\u001b]7501;{Brew}\u0007"));
        Assert.Equal("brew", signal.Report!.App);
    }

    [Fact]
    public void Cancel_byte_drops_a_partial_report()
    {
        Assert.Empty(Scan("\u001b]7501;state=done\u0018"));
    }
}
