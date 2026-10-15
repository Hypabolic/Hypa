using System.Text;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.ProgramStatus;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProgramStatusParserTests
{
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static Result<ProgramStatusReport, ProgramStatusRejection> Parse(string pairs, int terminatorBytes = 2) =>
        ProgramStatusParser.Parse(Encoding.UTF8.GetBytes(pairs), terminatorBytes);

    private static ProgramStatusReport Ok(string pairs)
    {
        var result = Parse(pairs);
        Assert.True(result.IsOk, result.IsOk ? "" : result.Error.ToString());
        return result.Value;
    }

    private static void Rejected(string pairs, ProgramStatusRejection expected)
    {
        var result = Parse(pairs);
        Assert.False(result.IsOk);
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public void Full_report_is_parsed()
    {
        var report = Ok($"state=blocked:kind=auth:progress=40:app=brew:title={B64("T")}:msg={B64("Installing updates")}");
        Assert.Equal(ProgramStatusState.Blocked, report.State);
        Assert.Equal(ProgramStatusKind.Auth, report.Kind);
        Assert.Equal(40, report.Progress);
        Assert.Equal("brew", report.App);
        Assert.Equal("T", report.Title);
        Assert.Equal("Installing updates", report.Msg);
        Assert.Null(report.Id);
    }

    [Theory]
    [InlineData("idle", ProgramStatusState.Idle)]
    [InlineData("working", ProgramStatusState.Working)]
    [InlineData("done", ProgramStatusState.Done)]
    [InlineData("blocked", ProgramStatusState.Blocked)]
    [InlineData("error", ProgramStatusState.Error)]
    [InlineData("clear", ProgramStatusState.Clear)]
    public void Every_state_is_known(string wire, ProgramStatusState expected) =>
        Assert.Equal(expected, Ok($"state={wire}").State);

    [Fact]
    public void Missing_state_ignores_the_report() =>
        Rejected("app=brew", ProgramStatusRejection.MissingState);

    [Fact]
    public void Unknown_state_ignores_the_report() =>
        Rejected("state=paused", ProgramStatusRejection.UnknownState);

    [Fact]
    public void Last_state_wins_when_a_key_repeats() =>
        Assert.Equal(ProgramStatusState.Done, Ok("state=bogus:state=done").State);

    [Fact]
    public void Whitespace_around_keys_and_values_is_trimmed()
    {
        var report = Ok(" state = working : app = brew ");
        Assert.Equal(ProgramStatusState.Working, report.State);
        Assert.Equal("brew", report.App);
    }

    [Theory]
    [InlineData("state=done:novalue")]
    [InlineData("state=done:=x")]
    [InlineData("state=done::app=brew")]
    [InlineData("state=done:APP=brew")]
    [InlineData("state=done:app=br ew")]
    [InlineData("state=done:app=bréw")]
    public void Malformed_pair_is_skipped_and_the_rest_is_processed(string pairs) =>
        Assert.Equal(ProgramStatusState.Done, Ok(pairs).State);

    [Fact]
    public void Malformed_app_pair_leaves_app_absent() =>
        Assert.Null(Ok("state=done:app=br ew").App);

    [Fact]
    public void Unknown_key_is_ignored() =>
        Assert.Equal(ProgramStatusState.Done, Ok("state=done:future=whatever").State);

    [Fact]
    public void Kind_applies_only_with_blocked()
    {
        Assert.Equal(ProgramStatusKind.Question, Ok("state=blocked:kind=question").Kind);
        Assert.Null(Ok("state=working:kind=question").Kind);
        Assert.Null(Ok("state=done:kind=question").Kind);
    }

    [Fact]
    public void Unknown_kind_is_absent() =>
        Assert.Null(Ok("state=blocked:kind=other").Kind);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    [InlineData("42", 42)]
    public void Progress_applies_with_working_and_blocked(string value, int expected)
    {
        Assert.Equal(expected, Ok($"state=working:progress={value}").Progress);
        Assert.Equal(expected, Ok($"state=blocked:progress={value}").Progress);
    }

    [Theory]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("4.5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("99999999999")]
    public void Bad_progress_is_absent(string value) =>
        Assert.Null(Ok($"state=working:progress={value}").Progress);

    [Theory]
    [InlineData("idle")]
    [InlineData("done")]
    [InlineData("error")]
    public void Progress_is_absent_for_other_states(string state) =>
        Assert.Null(Ok($"state={state}:progress=50").Progress);

    [Theory]
    [InlineData("claude-code", "claude-code")]
    [InlineData("a.b_c+d", "a.b_c+d")]
    [InlineData("a,b", null)]
    [InlineData("a/b", null)]
    public void App_follows_its_character_set(string value, string? expected) =>
        Assert.Equal(expected, Ok($"state=done:app={value}").App);

    [Fact]
    public void App_of_33_characters_is_absent_and_of_32_is_kept()
    {
        Assert.Equal(new string('a', 32), Ok($"state=done:app={new string('a', 32)}").App);
        Assert.Null(Ok($"state=done:app={new string('a', 33)}").App);
    }

    [Fact]
    public void Base64_padding_is_optional()
    {
        var padded = B64("ab");
        Assert.EndsWith("=", padded);
        Assert.Equal("ab", Ok($"state=done:msg={padded}").Msg);
        Assert.Equal("ab", Ok($"state=done:msg={padded.TrimEnd('=')}").Msg);
    }

    [Fact]
    public void Utf8_text_is_decoded() =>
        Assert.Equal("café ✓", Ok($"state=done:msg={B64("café ✓")}").Msg);

    [Fact]
    public void Last_msg_wins() =>
        Assert.Equal("two", Ok($"state=done:msg={B64("one")}:msg={B64("two")}").Msg);

    [Fact]
    public void Id_is_kept_for_the_store() =>
        Assert.Equal("build/test", Ok("state=done:id=build/test").Id);

    [Fact]
    public void Id_with_bad_characters_ignores_the_report() =>
        Rejected("state=done:id=bad id", ProgramStatusRejection.InvalidId);

    // Discard rules. Each one rejects the whole report.

    [Fact]
    public void Sequence_over_4096_bytes_is_discarded()
    {
        // ESC ] 7501 ; (7) + pairs + ST (2) must stay at 4096 bytes or less.
        var prefix = "state=done:future=";
        var fits = prefix + new string('a', 4096 - 7 - 2 - prefix.Length);
        Assert.True(Parse(fits).IsOk);
        Rejected(fits + "a", ProgramStatusRejection.SequenceTooLong);
    }

    [Fact]
    public void Bel_terminator_gives_one_more_byte()
    {
        var prefix = "state=done:future=";
        var fits = prefix + new string('a', 4096 - 7 - 1 - prefix.Length);
        Assert.True(Parse(fits, terminatorBytes: 1).IsOk);
        Assert.False(Parse(fits + "a", terminatorBytes: 1).IsOk);
    }

    [Fact]
    public void Key_over_16_bytes_is_discarded()
    {
        Assert.True(Parse($"state=done:{new string('a', 16)}=x").IsOk);
        Rejected($"state=done:{new string('a', 17)}=x", ProgramStatusRejection.KeyTooLong);
    }

    [Fact]
    public void Msg_over_2732_encoded_bytes_is_discarded_before_decoding()
    {
        // Not valid base64 at all. The size check must fire first.
        Rejected($"state=done:msg={new string('A', 2733)}", ProgramStatusRejection.MsgTooLong);
    }

    [Fact]
    public void Msg_over_2048_decoded_bytes_is_discarded()
    {
        Assert.Equal(2048, Ok($"state=done:msg={B64(new string('x', 2048))}").Msg!.Length);
        Rejected($"state=done:msg={B64(new string('x', 2049))}", ProgramStatusRejection.MsgTooLong);
    }

    [Fact]
    public void Title_over_256_encoded_bytes_is_discarded() =>
        Rejected($"state=done:title={new string('A', 257)}", ProgramStatusRejection.TitleTooLong);

    [Fact]
    public void Title_over_192_decoded_bytes_is_discarded()
    {
        Assert.Equal(192, Ok($"state=done:title={B64(new string('x', 192))}").Title!.Length);
        Rejected($"state=done:title={B64(new string('x', 193))}", ProgramStatusRejection.TitleTooLong);
    }

    [Theory]
    [InlineData("msg=a-b")]
    [InlineData("msg=a.b_")]
    [InlineData("msg=abcde")]
    [InlineData("title=ab=c")]
    [InlineData("title=a===")]
    public void Base64_that_does_not_decode_is_discarded(string pair) =>
        Rejected($"state=done:{pair}", ProgramStatusRejection.InvalidBase64);

    [Fact]
    public void Decoded_text_that_is_not_utf8_is_discarded() =>
        Rejected($"state=done:msg={Convert.ToBase64String([0xff, 0xfe])}", ProgramStatusRejection.InvalidText);

    [Theory]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    [InlineData("a\u001bb")]
    [InlineData("a\u007fb")]
    [InlineData("a\u0080b")]
    [InlineData("a\u009fb")]
    public void Control_character_in_msg_or_title_is_discarded(string text)
    {
        Rejected($"state=done:msg={B64(text)}", ProgramStatusRejection.ControlCharacter);
        Rejected($"state=done:title={B64(text)}", ProgramStatusRejection.ControlCharacter);
    }

    [Fact]
    public void Text_just_outside_the_control_ranges_is_kept() =>
        Assert.Equal("a b c", Ok($"state=done:msg={B64("a b c")}").Msg);

    [Fact]
    public void Bad_msg_pair_discards_even_when_a_later_msg_is_good() =>
        Rejected($"state=done:msg={B64("a\nb")}:msg={B64("ok")}", ProgramStatusRejection.ControlCharacter);

    [Theory]
    [InlineData("a")]
    [InlineData("a/b/c/d/e/f/g/h")]
    [InlineData("A_b.c+d-e")]
    public void Valid_ids_are_kept(string id) => Assert.Equal(id, Ok($"state=done:id={id}").Id);

    [Fact]
    public void Id_of_32_byte_segment_is_valid_and_33_is_not()
    {
        Assert.NotNull(Ok($"state=done:id={new string('a', 32)}").Id);
        Rejected($"state=done:id={new string('a', 33)}", ProgramStatusRejection.InvalidId);
    }

    [Fact]
    public void Id_with_nine_levels_is_rejected() =>
        Rejected("state=done:id=a/b/c/d/e/f/g/h/i", ProgramStatusRejection.InvalidId);

    [Fact]
    public void Id_over_128_bytes_is_rejected()
    {
        var id = string.Join("/", Enumerable.Repeat(new string('a', 17), 7)); // 7*17+6 = 125
        Assert.NotNull(Ok($"state=done:id={id}").Id);
        Rejected($"state=done:id={id}aaaa", ProgramStatusRejection.InvalidId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("a,b")]
    [InlineData("a=b")]
    [InlineData("a b")]
    public void Bad_ids_are_rejected_and_never_fall_back_to_the_root(string id) =>
        Rejected($"state=done:id={id}", ProgramStatusRejection.InvalidId);

    [Fact]
    public void Bad_clear_id_is_rejected_and_does_not_clear_everything() =>
        Rejected("state=clear:id=a//b", ProgramStatusRejection.InvalidId);
}
