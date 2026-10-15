using System.Text;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Osc;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ShellPromptOscHandlerTests
{
    private const string Working = "\u001b]7501;state=working:app=brew\u0007";

    private static List<ProgramStatusSignalKind> Kinds(params byte[][] chunks)
    {
        var pipeline = new PaneOscPipeline();
        var kinds = new List<ProgramStatusSignalKind>();
        foreach (var chunk in chunks)
        {
            foreach (var effect in pipeline.Observe(chunk))
            {
                if (effect is ProgramStatusOscEffect status)
                    kinds.Add(status.Signal.Kind);
            }
        }

        return kinds;
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData("\u001b]133;A\u0007")]
    [InlineData("\u001b]133;A\u001b\\")]
    [InlineData("\u001b]133;A;cl=m\u0007")]
    [InlineData("\u001b]133;A;aid=42;cl=m;k=i\u001b\\")]
    [InlineData("\u001b]133;A;\u0007")]
    public void Prompt_start_gives_one_signal(string text)
    {
        Assert.Equal([ProgramStatusSignalKind.PromptStart], Kinds(Bytes(text)));
    }

    [Theory]
    [InlineData("\u001b]133;B\u0007")]
    [InlineData("\u001b]133;C\u0007")]
    [InlineData("\u001b]133;D\u0007")]
    [InlineData("\u001b]133;D;0\u0007")]
    [InlineData("\u001b]133;AB\u0007")]
    [InlineData("\u001b]133;a\u0007")]
    [InlineData("\u001b]133;\u0007")]
    [InlineData("\u001b]133\u0007")]
    [InlineData("\u001b]1330;A\u0007")]
    public void Other_marks_give_no_signal(string text)
    {
        Assert.Empty(Kinds(Bytes(text)));
    }

    [Fact]
    public void A_cancelled_or_aborted_mark_gives_no_signal()
    {
        Assert.Empty(Kinds(Bytes("\u001b]133;A\u0018")));
        Assert.Empty(Kinds(Bytes("\u001b]133;A\u001bx")));
    }

    [Fact]
    public void A_mark_over_the_body_cap_gives_no_signal_and_the_next_mark_works()
    {
        var long_ = "\u001b]133;A;" + new string('x', 400) + "\u0007";
        Assert.Equal([ProgramStatusSignalKind.PromptStart], Kinds(Bytes(long_ + "\u001b]133;A\u0007")));
        Assert.Equal([ProgramStatusSignalKind.PromptStart], Kinds(Bytes("\u001b]133;A;" + new string('x', 200) + "\u0007")));
        Assert.Empty(Kinds(Bytes(long_)));
    }

    [Fact]
    public void Split_mark_works_at_every_byte()
    {
        var stream = Bytes("out\u001b]133;D;0\u0007\u001b]133;A;cl=m\u001b\\\u001b]133;B\u0007$ ");
        for (var split = 0; split <= stream.Length; split++)
            Assert.Equal([ProgramStatusSignalKind.PromptStart], Kinds(stream[..split], stream[split..]));

        Assert.Equal(
            [ProgramStatusSignalKind.PromptStart],
            Kinds(stream.Select(b => new[] { b }).ToArray()));
    }

    [Fact]
    public void Signals_keep_stream_order_with_reports()
    {
        var stream = Bytes(Working + "\u001b]133;A\u0007" + Working);
        var expected = new[]
        {
            ProgramStatusSignalKind.Report,
            ProgramStatusSignalKind.PromptStart,
            ProgramStatusSignalKind.Report,
        };
        Assert.Equal(expected, Kinds(stream));
        for (var split = 0; split <= stream.Length; split++)
            Assert.Equal(expected, Kinds(stream[..split], stream[split..]));
    }

    [Fact]
    public void The_handler_works_with_only_its_own_registration()
    {
        var builder = new OscHandlerRegistry.Builder();
        new ShellPromptOscHandler().RegisterWith(builder);
        var output = new OscOutput();
        new OscSequenceTokenizer(builder.Build()).Observe(Bytes("\u001b]133;A\u0007"), output);
        Assert.Single(output.Effects);
    }
}
