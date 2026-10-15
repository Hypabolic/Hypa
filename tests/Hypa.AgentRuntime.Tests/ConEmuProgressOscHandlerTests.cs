using System.Text;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Osc;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ConEmuProgressOscHandlerTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static List<ProgramStatusSignal> Signals(params byte[][] chunks)
    {
        var pipeline = new PaneOscPipeline();
        var signals = new List<ProgramStatusSignal>();
        foreach (var chunk in chunks)
        {
            foreach (var effect in pipeline.Observe(chunk))
            {
                if (effect is ProgramStatusOscEffect status)
                    signals.Add(status.Signal);
            }
        }

        return signals;
    }

    private static ProgramStatusReport Single(string text)
    {
        var signal = Assert.Single(Signals(Bytes(text)));
        Assert.Equal(ProgramStatusSignalKind.Report, signal.Kind);
        Assert.Equal(ProgramStatusReportSource.ConEmuProgress, signal.Source);
        return Assert.IsType<ProgramStatusReport>(signal.Report);
    }

    [Theory]
    [InlineData("\u001b]9;4;1;40\u0007", ProgramStatusState.Working, 40)]
    [InlineData("\u001b]9;4;1;40\u001b\\", ProgramStatusState.Working, 40)]
    [InlineData("\u001b]9;4;1;0\u0007", ProgramStatusState.Working, 0)]
    [InlineData("\u001b]9;4;1;100\u0007", ProgramStatusState.Working, 100)]
    [InlineData("\u001b]9;4;1\u0007", ProgramStatusState.Working, 0)]
    [InlineData("\u001b]9;4;4;75\u0007", ProgramStatusState.Working, 75)]
    [InlineData("\u001b]9;4;4\u0007", ProgramStatusState.Working, null)]
    [InlineData("\u001b]9;4;3\u0007", ProgramStatusState.Working, null)]
    [InlineData("\u001b]9;4;3;50\u0007", ProgramStatusState.Working, null)]
    [InlineData("\u001b]9;4;2\u0007", ProgramStatusState.Error, null)]
    [InlineData("\u001b]9;4;2;80\u0007", ProgramStatusState.Error, null)]
    [InlineData("\u001b]9;4;0\u0007", ProgramStatusState.Clear, null)]
    [InlineData("\u001b]9;4;0;0\u0007", ProgramStatusState.Clear, null)]
    public void Each_state_maps_to_a_root_report_without_text(string text, ProgramStatusState state, int? progress)
    {
        var report = Single(text);
        Assert.Equal(state, report.State);
        Assert.Equal(progress, report.Progress);
        Assert.Null(report.Id);
        Assert.Null(report.App);
        Assert.Null(report.Kind);
        Assert.Null(report.Title);
        Assert.Null(report.Msg);
    }

    [Theory]
    [InlineData("\u001b]9;4;5\u0007")]
    [InlineData("\u001b]9;4;9;10\u0007")]
    [InlineData("\u001b]9;4;-1\u0007")]
    [InlineData("\u001b]9;4;x\u0007")]
    [InlineData("\u001b]9;4;\u0007")]
    [InlineData("\u001b]9;4\u0007")]
    [InlineData("\u001b]9;4;1;101\u0007")]
    [InlineData("\u001b]9;4;1;-5\u0007")]
    [InlineData("\u001b]9;4;1;abc\u0007")]
    [InlineData("\u001b]9;4;1;\u0007")]
    [InlineData("\u001b]9;4;1;5;6\u0007")]
    [InlineData("\u001b]9;4;4;1000\u0007")]
    [InlineData("\u001b]9;4;1 ;5\u0007")]
    [InlineData("\u001b]9;4;1; 5\u0007")]
    public void A_bad_value_gives_no_signal(string text)
    {
        Assert.Empty(Signals(Bytes(text)));
    }

    [Theory]
    [InlineData("\u001b]9;hello\u0007")]
    [InlineData("\u001b]9;4x\u0007")]
    [InlineData("\u001b]9;40;1\u0007")]
    [InlineData("\u001b]9;\u0007")]
    [InlineData("\u001b]9;Build done\u001b\\")]
    [InlineData("\u001b]9;3;1;1\u0007")]
    [InlineData("\u001b]99;4;1;40\u0007")]
    [InlineData("\u001b]7;4;1;40\u0007")]
    public void Other_osc_9_strings_give_no_signal(string text)
    {
        Assert.Empty(Signals(Bytes(text)));
    }

    [Fact]
    public void Split_progress_works_at_every_byte()
    {
        var stream = Bytes("out\u001b]9;4;1;40\u0007x\u001b]9;4;0\u001b\\");
        for (var split = 0; split <= stream.Length; split++)
        {
            var signals = Signals(stream[..split], stream[split..]);
            Assert.Equal(2, signals.Count);
            Assert.Equal(40, signals[0].Report!.Progress);
            Assert.Equal(ProgramStatusState.Clear, signals[1].Report!.State);
        }

        Assert.Equal(2, Signals(stream.Select(b => new[] { b }).ToArray()).Count);
    }

    [Fact]
    public void Signals_keep_stream_order_with_reports_and_marks()
    {
        var stream = Bytes("\u001b]9;4;1;5\u0007\u001b]7501;state=working\u0007\u001b]133;A\u0007\u001b]9;4;2\u0007");
        var kinds = Signals(stream).Select(s => (s.Kind, s.Source)).ToArray();
        Assert.Equal(
            [
                (ProgramStatusSignalKind.Report, ProgramStatusReportSource.ConEmuProgress),
                (ProgramStatusSignalKind.Report, ProgramStatusReportSource.Osc7501),
                (ProgramStatusSignalKind.PromptStart, ProgramStatusReportSource.Osc7501),
                (ProgramStatusSignalKind.Report, ProgramStatusReportSource.ConEmuProgress),
            ],
            kinds);
    }

    [Fact]
    public void The_agent_tracker_still_sees_its_progress_string()
    {
        var pipeline = new PaneOscPipeline();
        pipeline.Observe(Bytes("\u001b]9;4;1;40\u0007"));
        Assert.Equal("4;1;40", pipeline.Agent.LatestProgress);
        pipeline.Observe(Bytes("\u001b]9;hello\u0007"));
        Assert.Equal("hello", pipeline.Agent.LatestProgress);
    }

    [Fact]
    public void The_handler_shares_the_osc_9_policy_of_the_agent_tracker()
    {
        Assert.Equal(AgentOscStateTracker.Policy, ConEmuProgressOscHandler.Policy);
        var builder = new OscHandlerRegistry.Builder();
        new AgentOscStateTracker().RegisterWith(builder);
        new ConEmuProgressOscHandler().RegisterWith(builder);
        var output = new OscOutput();
        new OscSequenceTokenizer(builder.Build()).Observe(Bytes("\u001b]9;4;3\u0007"), output);
        Assert.Single(output.Effects);
    }
}
