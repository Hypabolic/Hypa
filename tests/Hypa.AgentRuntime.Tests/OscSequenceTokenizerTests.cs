using System.Text;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Hypa.AgentRuntime.Domain.Theme;
using Hypa.Terminal.Vt;
using Hypa.Terminal.Vt.Osc;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class OscSequenceTokenizerTests
{
    private sealed record SeenSequence(int Number, string Body, int TerminatorBytes, int EndOffset);

    private sealed class RecordingHandler : IOscHandler, IControlSequenceObserver
    {
        public List<SeenSequence> Seen { get; } = [];

        public List<string> Resets { get; } = [];

        public void Handle(in OscSequence sequence, OscOutput output) =>
            Seen.Add(new SeenSequence(
                sequence.Number,
                Encoding.UTF8.GetString(sequence.Body),
                sequence.TerminatorBytes,
                sequence.EndOffset));

        public void OnFullReset(int endOffset, OscOutput output) => Resets.Add("ris");

        public void OnSoftReset(int endOffset, OscOutput output) => Resets.Add("decstr");
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static OscSequenceTokenizer Tokenizer(
        RecordingHandler handler,
        OscStringPolicy policy,
        params int[] numbers)
    {
        var builder = new OscHandlerRegistry.Builder();
        foreach (var number in numbers)
            builder.Add(number, policy, handler);
        builder.AddObserver(handler);
        return new OscSequenceTokenizer(builder.Build());
    }

    private static readonly OscStringPolicy Strict = new(64, OscEscapeInBody.AbortAndRestart, CancelAborts: true);

    private static void Feed(OscSequenceTokenizer tokenizer, params string[] chunks)
    {
        var output = new OscOutput();
        foreach (var chunk in chunks)
            tokenizer.Observe(Bytes(chunk), output);
    }

    [Fact]
    public void Mixed_stream_split_at_every_byte_reaches_each_handler_once_and_in_order()
    {
        var directory = Path.GetFullPath(Path.GetTempPath()).TrimEnd('/');
        var stream = Bytes(
            "before\u001b]2;my title\u0007"
            + "\u001b]7;" + new Uri(directory).AbsoluteUri + "\u001b\\"
            + "\u001b]10;?\u001b\\"
            + "\u001b]7501;state=working:app=brew\u0007"
            + "\u001b]7501;?\u001b\\"
            + "\u001bc"
            + "after");

        for (var split = 0; split <= stream.Length; split++)
            AssertStream(stream, directory, [stream[..split], stream[split..]]);

        // One byte at a time.
        AssertStream(stream, directory, stream.Select(b => new[] { b }).ToArray());
    }

    private static void AssertStream(byte[] stream, string directory, byte[][] chunks)
    {
        _ = stream;
        var pipeline = new PaneOscPipeline();
        var effects = new List<OscEffect>();
        foreach (var chunk in chunks)
            effects.AddRange(pipeline.Observe(chunk));

        Assert.Equal("my title", pipeline.Agent.LatestTitle);
        Assert.Equal(directory, pipeline.Agent.WorkingDirectory);
        Assert.Collection(
            effects,
            e =>
            {
                var color = Assert.IsType<DefaultColorOscEffect>(e);
                Assert.Equal(DefaultColorOscKind.Query, color.Event.Kind);
                Assert.Equal(HostDefaultColorKind.Foreground, color.Event.Channel);
            },
            e =>
            {
                var signal = Assert.IsType<ProgramStatusOscEffect>(e).Signal;
                Assert.Equal(ProgramStatusSignalKind.Report, signal.Kind);
                Assert.Equal("brew", signal.Report!.App);
            },
            e => Assert.Equal(ProgramStatusSignalKind.Query, Assert.IsType<ProgramStatusOscEffect>(e).Signal.Kind),
            e => Assert.Equal(ProgramStatusSignalKind.Reset, Assert.IsType<ProgramStatusOscEffect>(e).Signal.Kind));
    }

    [Fact]
    public void Title_change_is_reported_once_across_chunks()
    {
        var tracker = new AgentOscStateTracker();
        var feed = OscTestFeed.For(tracker);
        bool Changed(string chunk)
        {
            var before = tracker.TerminalTitle;
            feed.Observe(Bytes(chunk));
            return !string.Equals(before, tracker.TerminalTitle, StringComparison.Ordinal);
        }

        Assert.False(Changed("\u001b]0;ab"));
        Assert.True(Changed("c\u0007"));
        Assert.False(Changed("\u001b]0;abc\u0007"));
    }

    [Fact]
    public void Osc_number_with_no_handler_allocates_nothing()
    {
        var handler = new RecordingHandler();
        var tokenizer = Tokenizer(handler, Strict, 2);
        var output = new OscOutput();
        var unhandled = Bytes("text\u001b]52;c;" + new string('A', 3000) + "\u0007more text");
        var split = new[] { Bytes("\u001b]99;abc"), Bytes(new string('x', 5000)), Bytes("\u001b\\tail") };
        var plain = Bytes(new string('p', 8000));

        void Run()
        {
            tokenizer.Observe(unhandled, output);
            foreach (var chunk in split)
                tokenizer.Observe(chunk, output);
            tokenizer.Observe(plain, output);
        }

        for (var i = 0; i < 20; i++)
            Run();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
            Run();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public void Handler_gets_the_whole_body_and_the_terminator_width()
    {
        var handler = new RecordingHandler();
        var tokenizer = Tokenizer(handler, Strict, 133);
        Feed(tokenizer, "x\u001b]133;A;cl=m\u0007y\u001b]133;B\u001b\\");
        Assert.Equal(
            [
                new SeenSequence(133, "133;A;cl=m", 1, 14),
                new SeenSequence(133, "133;B", 2, 24),
            ],
            handler.Seen);
    }

    [Fact]
    public void Number_with_no_semicolon_is_routed_by_its_digits()
    {
        var handler = new RecordingHandler();
        var tokenizer = Tokenizer(handler, Strict, 111);
        Feed(tokenizer, "\u001b]111\u0007");
        Assert.Equal("111", Assert.Single(handler.Seen).Body);
    }

    [Theory]
    [InlineData("\u001b]02;x\u0007")]
    [InlineData("\u001b]2x;x\u0007")]
    [InlineData("\u001b]+2;x\u0007")]
    [InlineData("\u001b]22;x\u0007")]
    [InlineData("\u001b]123456;x\u0007")]
    [InlineData("\u001b];x\u0007")]
    public void Only_the_canonical_decimal_text_selects_a_handler(string input)
    {
        var handler = new RecordingHandler();
        var tokenizer = Tokenizer(handler, Strict, 2);
        Feed(tokenizer, input);
        Assert.Empty(handler.Seen);
        Feed(tokenizer, "\u001b]2;ok\u0007");
        Assert.Equal("2;ok", Assert.Single(handler.Seen).Body);
    }

    [Fact]
    public void Two_handlers_can_share_a_number_and_a_different_policy_is_refused()
    {
        var first = new RecordingHandler();
        var second = new RecordingHandler();
        var builder = new OscHandlerRegistry.Builder().Add(9, Strict, first).Add(9, Strict, second);
        var tokenizer = new OscSequenceTokenizer(builder.Build());
        Feed(tokenizer, "\u001b]9;4;1\u0007");
        Assert.Single(first.Seen);
        Assert.Single(second.Seen);
        Assert.Throws<ArgumentException>(() =>
            builder.Add(9, Strict with { MaxBodyBytes = 5 }, first));
    }

    [Fact]
    public void Body_cap_is_set_by_the_handler_policy()
    {
        var small = new RecordingHandler();
        var large = new RecordingHandler();
        var builder = new OscHandlerRegistry.Builder()
            .Add(10, new OscStringPolicy(20, OscEscapeInBody.Keep, CancelAborts: false), small)
            .Add(2, new OscStringPolicy(40, OscEscapeInBody.Keep, CancelAborts: false), large);
        var tokenizer = new OscSequenceTokenizer(builder.Build());
        var body = new string('a', 30);
        Feed(tokenizer, $"\u001b]10;{body}\u0007\u001b]2;{body}\u0007");
        Assert.Empty(small.Seen);
        Assert.Single(large.Seen);
    }

    [Fact]
    public void Body_that_fills_the_cap_is_kept_and_one_more_byte_is_not()
    {
        var handler = new RecordingHandler();
        var tokenizer = Tokenizer(handler, new OscStringPolicy(10, OscEscapeInBody.Keep, false), 2);
        Feed(tokenizer, "\u001b]2;" + new string('a', 8) + "\u0007");
        Assert.Single(handler.Seen);
        Feed(tokenizer, "\u001b]2;" + new string('a', 9) + "\u0007");
        Assert.Single(handler.Seen);
        Feed(tokenizer, "\u001b]2;ok\u0007");
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public void Oversized_string_is_skipped_across_chunks_until_either_terminator()
    {
        var handler = new RecordingHandler();
        var tokenizer = Tokenizer(handler, Strict, 2);
        Feed(tokenizer, "\u001b]2;" + new string('a', 100), new string('b', 100) + "\u0007");
        Feed(tokenizer, "\u001b]2;" + new string('a', 200) + "\u001b", "\\");
        Assert.Empty(handler.Seen);
        Feed(tokenizer, "\u001b]2;ok\u0007");
        Assert.Single(handler.Seen);
    }

    [Fact]
    public void Strict_policy_aborts_on_escape_and_cancel_and_restarts_on_the_next_byte()
    {
        var handler = new RecordingHandler();
        var tokenizer = Tokenizer(handler, Strict, 2);
        Feed(tokenizer, "\u001b]2;one\u001b]2;two\u0007");
        Assert.Equal("2;two", Assert.Single(handler.Seen).Body);
        Feed(tokenizer, "\u001b]2;three\u0018four\u0007");
        Assert.Single(handler.Seen);
    }

    [Fact]
    public void Lenient_policies_keep_escape_pairs_in_the_body()
    {
        var keepEndOnBel = new RecordingHandler();
        var keep = new RecordingHandler();
        var builder = new OscHandlerRegistry.Builder()
            .Add(2, new OscStringPolicy(64, OscEscapeInBody.KeepAndEndOnBel, false), keepEndOnBel)
            .Add(10, new OscStringPolicy(64, OscEscapeInBody.Keep, false), keep);
        var tokenizer = new OscSequenceTokenizer(builder.Build());

        Feed(tokenizer, "\u001b]2;a\u001bxb\u0007\u001b]2;c\u001b\u0007");
        Assert.Equal(["2;a\u001bxb", "2;c\u001b"], keepEndOnBel.Seen.Select(s => s.Body));

        Feed(tokenizer, "\u001b]10;a\u001bxb\u0007\u001b]10;c\u001b\u0007d\u0007");
        Assert.Equal(["10;a\u001bxb", "10;c\u001b\u0007d"], keep.Seen.Select(s => s.Body));
    }

    [Fact]
    public void Dcs_apc_pm_and_sos_strings_hide_osc_look_alikes_and_end_with_st_only()
    {
        var handler = new RecordingHandler();
        var tokenizer = Tokenizer(handler, Strict, 2);
        foreach (var introducer in new[] { "P", "_", "^", "X" })
        {
            Feed(tokenizer, $"\u001b{introducer}data\u0007still\u001b\\");
            Feed(tokenizer, "\u001b]2;after\u0007");
        }

        Assert.Equal(4, handler.Seen.Count);
        Assert.Empty(handler.Resets);
    }

    [Fact]
    public void Control_sequence_observers_see_ris_and_decstr_in_order()
    {
        var handler = new RecordingHandler();
        var tokenizer = Tokenizer(handler, Strict, 2);
        Feed(tokenizer, "\u001bc", "\u001b[", "!", "p", "\u001b[1;2r", "\u001b[?!p", "\u001b[2!p", "\u001b[!p");
        Assert.Equal(["ris", "decstr", "decstr"], handler.Resets);
    }

    [Fact]
    public void Ris_inside_an_osc_string_is_not_a_reset()
    {
        var handler = new RecordingHandler();
        var tokenizer = Tokenizer(handler, new OscStringPolicy(64, OscEscapeInBody.Keep, false), 2);
        Feed(tokenizer, "\u001b]2;a\u001bcb\u0007");
        Assert.Empty(handler.Resets);
        Assert.Equal("2;a\u001bcb", Assert.Single(handler.Seen).Body);
    }
}
