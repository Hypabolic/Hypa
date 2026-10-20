using System.Text;
using Hypa.Cli.Attach;
using Hypa.Cli.Attach.Latency;
using Hypa.Cli.Attach.Prediction;
using Xunit;

namespace Hypa.UnitTests.Cli;

/// <summary>
/// The echo predictor on synthetic frames and a manual clock. Each test
/// types keys, binds their seq as the input sender does, and applies frames
/// as the cells apply does.
/// </summary>
public sealed class EchoPredictorTests
{
    private const string Pane = "p1";
    private const int Cols = 20;

    private static readonly AssembledStyle Green = AssembledStyle.Default with { Fg = 0x00ff00 };

    private readonly ManualTimeProvider _time = new();
    private long _seq;

    [Fact]
    public void A_glyph_prediction_confirms_and_its_epoch_shows_later_keys()
    {
        var predictor = Always();
        var prompt = Frame("$ ", cursor: 2);
        Type(predictor, "a", prompt);
        Assert.Null(predictor.View(Pane, prompt));

        var echo = Frame("$ a", cursor: 3);
        predictor.Reconcile(Pane, echo, 1, fullOrReanchor: false);
        Assert.Equal(1, predictor.Stats().ConfirmedByGlyph);

        Type(predictor, "b", echo);
        var view = Assert.IsType<PredictionView>(predictor.View(Pane, echo));
        var cell = Assert.Single(view.Cells);
        Assert.Equal((3, 0, "b"), (cell.Col, cell.Row, cell.Cell.Text));
        Assert.Equal(new PredictedCursor(4, 0), view.Cursor);
    }

    [Fact]
    public void A_mismatch_deletes_the_epoch_and_counts_the_shown_cells()
    {
        var meter = new AttachLatencyMeter(_time);
        var predictor = new EchoPredictor(_time, new AlwaysEchoDisplayPolicy(), meter);
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        Type(predictor, "b", echo);
        var view = predictor.View(Pane, echo)!;
        predictor.NoteShown(view);

        // The application echoed another glyph.
        var other = Frame("$ aX", cursor: 4);
        predictor.Reconcile(Pane, other, 2, fullOrReanchor: false);
        Assert.NotNull(predictor.View(Pane, other));
        _time.Advance(EchoPredictor.JudgeDelay);
        predictor.Reconcile(Pane, other, 2, fullOrReanchor: false);

        Assert.Null(predictor.View(Pane, other));
        var stats = predictor.Stats();
        Assert.Equal(1, stats.Mismatches);
        Assert.Equal(1, stats.PredictedShown);
        Assert.Equal(1, stats.MispredictedShown);
        Assert.Equal(0, stats.Pending);
        var report = meter.Report();
        Assert.Equal(1, report.PredictedCellsShown);
        Assert.Equal(1, report.MispredictedCellsShown);
    }

    [Fact]
    public void A_tentative_epoch_stays_hidden_until_one_prediction_confirms()
    {
        var predictor = Always();
        var prompt = Frame("$ ", cursor: 2);
        Type(predictor, "a", prompt);
        Type(predictor, "b", prompt);
        Assert.Null(predictor.View(Pane, prompt));

        // A frame without the ack does not confirm, even with the glyph.
        var early = Frame("$ a", cursor: 3);
        predictor.Reconcile(Pane, early, null, fullOrReanchor: false);
        Assert.Null(predictor.View(Pane, early));

        predictor.Reconcile(Pane, early, 1, fullOrReanchor: false);
        var view = Assert.IsType<PredictionView>(predictor.View(Pane, early));
        Assert.Equal("b", Assert.Single(view.Cells).Cell.Text);
    }

    [Fact]
    public void A_space_over_a_blank_confirms_by_the_cursor_and_not_by_the_glyph()
    {
        var predictor = Always();
        var prompt = Frame("> ab", cursor: 4);
        Type(predictor, " ", prompt);

        // The blank cell already matches before the application replies.
        predictor.Reconcile(Pane, prompt, 1, fullOrReanchor: false);
        Assert.Equal(0, predictor.Stats().ConfirmedByCursor);
        Assert.Null(predictor.View(Pane, prompt));

        // The application moves the cursor only.
        var moved = Frame("> ab", cursor: 5);
        predictor.Reconcile(Pane, moved, 1, fullOrReanchor: false);
        Assert.Equal(1, predictor.Stats().ConfirmedByCursor);
        Assert.Equal(0, predictor.Stats().Pending);
    }

    [Fact]
    public void A_space_typed_in_a_shown_epoch_moves_the_predicted_cursor_with_no_cell()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "> a", cursor: 3);
        Type(predictor, " ", echo);
        var view = Assert.IsType<PredictionView>(predictor.View(Pane, echo));
        Assert.Empty(view.Cells);
        Assert.Equal(new PredictedCursor(4, 0), view.Cursor);
    }

    [Fact]
    public void An_erase_over_a_space_confirms_by_the_cursor_position()
    {
        var predictor = Always();
        var line = Frame("$ foo  ", cursor: 7);
        Type(predictor, "\x7f", line);
        var back = Frame("$ foo  ", cursor: 6);
        predictor.Reconcile(Pane, back, 1, fullOrReanchor: false);
        var stats = predictor.Stats();
        Assert.Equal(1, stats.ConfirmedByCursor);
        Assert.Equal(0, stats.Mismatches);
    }

    [Fact]
    public void An_erase_over_a_glyph_confirms_by_the_blank_glyph()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ ab", cursor: 4);
        Type(predictor, "\x7f", echo);
        var view = Assert.IsType<PredictionView>(predictor.View(Pane, echo));
        var cell = Assert.Single(view.Cells);
        Assert.Equal((3, " "), (cell.Col, cell.Cell.Text));
        Assert.Equal(new PredictedCursor(3, 0), view.Cursor);

        predictor.Reconcile(Pane, Frame("$ a", cursor: 3), 2, fullOrReanchor: false);
        Assert.Equal(2, predictor.Stats().ConfirmedByGlyph);
    }

    [Fact]
    public void An_erase_inside_the_text_is_not_predicted()
    {
        var predictor = Always();
        Confirmed(predictor, "$ a", cursor: 3);
        // The cursor is on the b of "abc": zsh moves the rest of the line left.
        var inside = Frame("$ abc", cursor: 3);
        predictor.Reconcile(Pane, inside, _seq, fullOrReanchor: false);
        Type(predictor, "\x7f", inside);
        Assert.Null(predictor.View(Pane, inside));
        Assert.Equal(0, predictor.Stats().Pending);
    }

    [Fact]
    public void An_ack_that_comes_with_a_stale_frame_deletes_nothing_for_fifty_ms()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        Type(predictor, "b", echo);

        // On a cube the ack can come before the body with the echo.
        predictor.Reconcile(Pane, echo, 2, fullOrReanchor: false);
        _time.Advance(EchoPredictor.JudgeDelay - TimeSpan.FromMilliseconds(1));
        predictor.Reconcile(Pane, echo, 2, fullOrReanchor: false);
        Assert.NotNull(predictor.View(Pane, echo));
        Assert.Equal(0, predictor.Stats().Mismatches);

        predictor.Reconcile(Pane, Frame("$ ab", cursor: 4), 2, fullOrReanchor: false);
        Assert.Equal(0, predictor.Stats().Mismatches);
        Assert.Equal(0, predictor.Stats().Pending);
    }

    [Fact]
    public void The_judge_timer_deletes_a_stale_epoch_with_no_new_frame()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        Type(predictor, "b", echo);
        predictor.Reconcile(Pane, echo, 2, fullOrReanchor: false);
        Assert.Equal(EchoPredictor.JudgeDelay, predictor.NextWake());

        _time.Advance(EchoPredictor.JudgeDelay);
        Assert.True(predictor.Tick(Pane, echo));
        Assert.Null(predictor.View(Pane, echo));
        Assert.Equal(1, predictor.Stats().Mismatches);
        Assert.Null(predictor.NextWake());
    }

    [Fact]
    public void A_prediction_bound_after_its_ack_is_judged_and_the_timer_stops()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        // The seq of "b" goes out, and its echo and ack arrive before the
        // stdin thread records the prediction.
        var mark = predictor.ReserveMark();
        predictor.OnSeqAssigned(Pane, ++_seq, mark);
        var echoB = Frame("$ ab", cursor: 4);
        predictor.Reconcile(Pane, echoB, _seq, fullOrReanchor: false);
        // The stdin thread gives the frame from before the send.
        predictor.Observe(Pane, Encoding.UTF8.GetBytes("b"), mark, echo, paste: false, allowed: true);

        // The prediction sits on the cell of the real echo, not one cell to
        // its right, and the judge confirms it by the glyph.
        var view = predictor.View(Pane, echoB);
        if (view is not null)
            Assert.All(view.Cells, cell => Assert.Equal(3, cell.Col));
        Assert.Equal(EchoPredictor.JudgeDelay, predictor.NextWake());
        var confirmed = predictor.Stats().ConfirmedByGlyph;
        _time.Advance(EchoPredictor.JudgeDelay);
        predictor.Tick(Pane, echoB);
        Assert.Equal(confirmed + 1, predictor.Stats().ConfirmedByGlyph);
        Assert.Null(predictor.NextWake());
    }

    [Fact]
    public void A_paste_acked_before_it_is_observed_still_ends_the_epoch()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        Type(predictor, "b", echo);
        Assert.NotNull(predictor.View(Pane, echo));

        // The paste goes out and its ack comes before the stdin thread
        // records it.
        var mark = predictor.ReserveMark();
        predictor.OnSeqAssigned(Pane, ++_seq, mark);
        predictor.NoteAck(Pane, _seq, Frame("$ abpasted", cursor: 10));
        predictor.Observe(Pane, Encoding.UTF8.GetBytes("pasted"), mark, echo, paste: true, allowed: true);

        Assert.Null(predictor.View(Pane, Frame("$ abpasted", cursor: 10)));
    }

    [Fact]
    public void Keys_typed_on_an_unknown_frame_start_no_prediction_until_their_ack()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        var mark = predictor.ReserveMark();
        predictor.OnSeqAssigned(Pane, ++_seq, mark);
        // A frame came during the send, so the base of "b" is unknown.
        predictor.ObserveUnknownBase(Pane, mark, paste: false);
        Type(predictor, "c", echo);
        Assert.Null(predictor.View(Pane, echo));

        // After the ack of every unpredicted key, the next key predicts from
        // the new frame, on the right cell, and confirms by its glyph.
        var echoBc = Frame("$ abc", cursor: 5);
        predictor.Reconcile(Pane, echoBc, _seq, fullOrReanchor: false);
        var confirmed = predictor.Stats().ConfirmedByGlyph;
        Type(predictor, "d", echoBc);
        predictor.Reconcile(Pane, Frame("$ abcd", cursor: 6), _seq, fullOrReanchor: false);
        Assert.Equal(confirmed + 1, predictor.Stats().ConfirmedByGlyph);
        Assert.Equal(0, predictor.Stats().Mismatches);
    }

    [Fact]
    public void Keys_that_the_sender_sends_to_another_pane_end_their_predictions()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        var mark = predictor.ReserveMark(Pane);
        predictor.Observe(Pane, Encoding.UTF8.GetBytes("b"), mark, echo, paste: false, allowed: true);
        Assert.NotNull(predictor.View(Pane, echo));

        // The focus moved before the sender took the keys: they went to
        // another pane.
        predictor.OnSeqAssigned("other-pane", ++_seq, mark);

        Assert.Null(predictor.View(Pane, echo));
    }

    [Fact]
    public void Keys_sent_to_another_pane_before_their_observe_get_no_prediction()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        var mark = predictor.ReserveMark(Pane);
        // The keys were typed on this pane, but the sender took them while
        // the focus was on another pane, before the stdin thread recorded them.
        predictor.OnSeqAssigned("other-pane", ++_seq, mark);
        predictor.Observe(Pane, Encoding.UTF8.GetBytes("b"), mark, echo, paste: false, allowed: true);

        Assert.Null(predictor.View(Pane, echo));
    }

    [Fact]
    public void Keys_on_a_newly_focused_pane_sent_before_their_observe_still_bind()
    {
        const string other = "pane-b";
        var predictor = Always();
        Confirmed(predictor, "$ a", cursor: 3);

        // The focus moved to pane B. Its first key goes out before its observe.
        var onB = Frame("> ", cursor: 2);
        var mark = predictor.ReserveMark(other);
        predictor.OnSeqAssigned(other, ++_seq, mark);
        predictor.Observe(other, Encoding.UTF8.GetBytes("x"), mark, onB, paste: false, allowed: true);

        // The key is not foreign: its echo confirms it.
        predictor.Reconcile(other, Frame("> x", cursor: 3), _seq, fullOrReanchor: false);
        Assert.True(predictor.Stats().ConfirmedByGlyph >= 2);
    }

    [Fact]
    public void A_confirmed_erase_retires_the_earlier_prediction_on_its_cell()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        // "b" and then an erase over it, both before any ack.
        Type(predictor, "b", echo);
        Type(predictor, "\u007f", echo);
        // The echo of both keys: the cell is blank again.
        predictor.Reconcile(Pane, Frame("$ a", cursor: 3), _seq, fullOrReanchor: false);

        var view = predictor.View(Pane, Frame("$ a", cursor: 3));
        Assert.True(view is null || view.Cells.All(cell => cell.Cell.Text != "b"), "the old b came back");
    }

    [Fact]
    public void A_batch_with_one_foreign_key_still_predicts_the_key_typed_on_its_pane()
    {
        const string other = "pane-b";
        var predictor = Always();
        Confirmed(predictor, "$ a", cursor: 3);

        // One key typed on this pane, then the focus moves to pane B and a key
        // is typed there. The sender sends both to pane B in one batch.
        var first = predictor.ReserveMark(Pane);
        var second = predictor.ReserveMark(other);
        predictor.OnSeqAssigned(other, ++_seq, second);
        var onB = Frame("> ", cursor: 2);
        Assert.False(predictor.Observe(Pane, Encoding.UTF8.GetBytes("z"), first, Frame("$ a", cursor: 3), paste: false, allowed: true));
        predictor.Observe(other, Encoding.UTF8.GetBytes("x"), second, onB, paste: false, allowed: true);

        // The key typed on pane B keeps its seq, so its echo confirms it.
        var confirmed = predictor.Stats().ConfirmedByGlyph;
        predictor.Reconcile(other, Frame("> x", cursor: 3), _seq, fullOrReanchor: false);
        Assert.Equal(confirmed + 1, predictor.Stats().ConfirmedByGlyph);
    }

    [Fact]
    public void A_key_typed_elsewhere_and_sent_into_the_predicted_pane_ends_its_predictions()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        var shown = predictor.ReserveMark(Pane);
        predictor.Observe(Pane, Encoding.UTF8.GetBytes("b"), shown, echo, paste: false, allowed: true);
        Assert.NotNull(predictor.View(Pane, echo));

        // A key typed while another pane had the focus goes into this pane.
        var foreign = predictor.ReserveMark("pane-b");
        predictor.OnSeqAssigned(Pane, ++_seq, foreign);

        Assert.Null(predictor.View(Pane, echo));
    }

    [Fact]
    public void A_shown_prediction_with_no_ack_expires_and_counts_as_mispredicted()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        Type(predictor, "b", echo);
        predictor.NoteShown(predictor.View(Pane, echo)!);
        Assert.Equal(EchoPredictor.AckExpiry, predictor.NextWake());

        _time.Advance(EchoPredictor.AckExpiry);
        Assert.True(predictor.Tick(Pane, echo));
        Assert.Null(predictor.View(Pane, echo));
        Assert.Equal(1, predictor.Stats().Expired);
        Assert.Equal(1, predictor.Stats().MispredictedShown);
    }

    [Fact]
    public void One_frame_with_a_space_and_b_typed_ahead_confirms_all_three()
    {
        var predictor = Always();
        var prompt = Frame("> ", cursor: 2);
        Type(predictor, "a", prompt);
        Type(predictor, " ", prompt);
        Type(predictor, "b", prompt);

        predictor.Reconcile(Pane, Frame("> a b", cursor: 5), 3, fullOrReanchor: false);
        var stats = predictor.Stats();
        Assert.Equal(2, stats.ConfirmedByGlyph);
        Assert.Equal(1, stats.ConfirmedByCursor);
        Assert.Equal(0, stats.Pending);
        Assert.Equal(0, stats.Mismatches);
    }

    [Fact]
    public void A_change_on_a_row_with_no_prediction_does_not_delete_the_epoch()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "> a", cursor: 3);
        Type(predictor, "b", echo);

        // Claude Code clears a hint row after a key.
        var hint = Frame(["> a", "", "", "hint text"], cursor: 3);
        var cleared = Frame(["> a", "", "", ""], cursor: 3);
        predictor.Reconcile(Pane, hint, null, fullOrReanchor: false);
        predictor.Reconcile(Pane, cleared, null, fullOrReanchor: false);
        Assert.NotNull(predictor.View(Pane, cleared));
        Assert.Equal(0, predictor.Stats().Mismatches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Echo_off_in_canonical_mode_turns_prediction_off(bool agent)
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "> a", cursor: 3);
        Type(predictor, "b", echo);
        Assert.NotNull(predictor.View(Pane, echo with { Echo = true, Canonical = true }));

        // A password prompt: echo off, canonical mode on.
        var prompt = Frame("Password: ", cursor: 10) with { Echo = false, Canonical = true };
        predictor.Reconcile(Pane, prompt, null, fullOrReanchor: false, agentPane: agent);
        Assert.Null(predictor.View(Pane, prompt, agentPane: agent));
        Assert.Equal(0, predictor.Stats().Pending);
        Assert.True(predictor.Stats().ResetCalls[PredictionResetReason.EchoOff] >= 1);

        // A key typed at the prompt makes no prediction.
        var made = predictor.Stats().Made;
        Type(predictor, "s", prompt, agent: agent);
        Assert.Equal(made, predictor.Stats().Made);
        Assert.Equal(0, predictor.Stats().Pending);
        Assert.Null(predictor.View(Pane, prompt, agentPane: agent));
    }

    [Theory]
    [InlineData("main", false)]
    [InlineData("alternate", true)]
    public void Line_editor_with_echo_and_canonical_mode_off_still_predicts(string screen, bool agent)
    {
        // readline, zle, fish and an agent in raw mode turn echo and
        // canonical mode off and draw their own echo.
        var predictor = Always();
        var prompt = Frame("$ ", cursor: 2, screen: screen) with { Echo = false, Canonical = false };
        Type(predictor, "a", prompt, agent: agent);
        var echo = Frame("$ a", cursor: 3, screen: screen) with { Echo = false, Canonical = false };
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false, agentPane: agent);
        Assert.Equal(1, predictor.Stats().ConfirmedByGlyph);

        Type(predictor, "b", echo, agent: agent);
        var view = Assert.IsType<PredictionView>(predictor.View(Pane, echo, agentPane: agent));
        Assert.Equal("b", Assert.Single(view.Cells).Cell.Text);
        Assert.Equal(0, predictor.Stats().ResetCalls[PredictionResetReason.EchoOff]);
    }

    [Fact]
    public void Raw_mode_password_prompt_after_enter_never_shows_a_typed_key()
    {
        // A raw-mode password prompt (echo and canonical mode off, as a line
        // editor) that draws nothing for each key. The echo flag cannot tell
        // it from a line editor, so epoch confirmation is the guard.
        var predictor = Always();
        var shell = Frame("$ ", cursor: 2) with { Echo = false, Canonical = false };
        Type(predictor, "a", shell);
        var echo = Frame("$ a", cursor: 3) with { Echo = false, Canonical = false };
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false);
        Assert.Equal(1, predictor.Stats().ConfirmedByGlyph);

        // Enter is an unpredicted key: it ends the confirmed epoch.
        Type(predictor, "\r", echo);
        var prompt = Frame(["$ a", "Password: "], cursor: 10, cursorRow: 1) with { Echo = false, Canonical = false };
        predictor.Reconcile(Pane, prompt, _seq, fullOrReanchor: false);

        foreach (var key in new[] { "s", "e", "c" })
        {
            Type(predictor, key, prompt);
            Assert.Null(predictor.View(Pane, prompt));
        }

        Assert.Equal(4, predictor.Stats().Made);
        predictor.Reconcile(Pane, prompt, _seq, fullOrReanchor: false);
        Assert.Null(predictor.View(Pane, prompt));
        _time.Advance(EchoPredictor.JudgeDelay);
        predictor.Tick(Pane, prompt);
        Assert.Null(predictor.View(Pane, prompt));
        Assert.Equal(1, predictor.Stats().Mismatches);
        Assert.Equal(0, predictor.Stats().Pending);

        // A key after the mismatch starts a new tentative epoch: still hidden.
        Type(predictor, "r", prompt);
        Assert.Null(predictor.View(Pane, prompt));
        Assert.Equal(0, predictor.Stats().PredictedShown);
    }

    [Fact]
    public void Raw_mode_masked_password_prompt_shows_no_typed_key()
    {
        // A raw-mode prompt that draws '*' for each key. The '*' does not
        // confirm the glyph prediction, so the epoch stays tentative and is
        // deleted at the judge.
        var predictor = Always();
        var prompt = Frame("Password: ", cursor: 10) with { Echo = false, Canonical = false };
        Type(predictor, "s", prompt);
        Type(predictor, "e", prompt);
        Assert.Null(predictor.View(Pane, prompt));

        var masked = Frame("Password: **", cursor: 12) with { Echo = false, Canonical = false };
        predictor.Reconcile(Pane, masked, _seq, fullOrReanchor: false);
        Assert.Null(predictor.View(Pane, masked));
        _time.Advance(EchoPredictor.JudgeDelay);
        predictor.Tick(Pane, masked);
        Assert.Equal(0, predictor.Stats().ConfirmedByGlyph);
        Assert.Equal(1, predictor.Stats().Mismatches);

        Type(predictor, "c", masked);
        Assert.Null(predictor.View(Pane, masked));
        Assert.Equal(0, predictor.Stats().PredictedShown);
    }

    [Fact]
    public void Unknown_echo_mode_leaves_epoch_confirmation_as_the_only_guard()
    {
        var predictor = Always();
        // An older mux or a process-io pane sends no echo flag.
        var prompt = Frame("Password: ", cursor: 10);
        Assert.Null(prompt.Echo);

        Type(predictor, "s", prompt);
        Assert.Equal(1, predictor.Stats().Made);
        // A tentative epoch shows nothing.
        Assert.Null(predictor.View(Pane, prompt));

        // The ack comes with a frame that has no echo. After the judge delay
        // the prediction is a mismatch and the epoch is deleted unshown.
        predictor.Reconcile(Pane, prompt, _seq, fullOrReanchor: false);
        _time.Advance(EchoPredictor.JudgeDelay);
        predictor.Tick(Pane, prompt);
        Assert.Equal(1, predictor.Stats().Mismatches);
        Assert.Equal(0, predictor.Stats().Pending);
        Assert.Equal(0, predictor.Stats().PredictedShown);
        Assert.Equal(0, predictor.Stats().ResetCalls[PredictionResetReason.EchoOff]);
    }

    [Fact]
    public void Echo_on_predicts_and_shows_as_with_no_flag()
    {
        var predictor = Always();
        var prompt = Frame("$ ", cursor: 2) with { Echo = true, Canonical = true };
        Type(predictor, "a", prompt);
        var echo = Frame("$ a", cursor: 3) with { Echo = true, Canonical = true };
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false);
        Type(predictor, "b", echo);

        var view = Assert.IsType<PredictionView>(predictor.View(Pane, echo));
        Assert.Equal("b", Assert.Single(view.Cells).Cell.Text);
        Assert.Equal(0, predictor.Stats().ResetCalls[PredictionResetReason.EchoOff]);
    }

    [Fact]
    public void The_alternate_screen_turns_prediction_off_for_a_pane_with_no_agent()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "> a", cursor: 3);
        Type(predictor, "b", echo);
        var alt = Frame("> a", cursor: 3, screen: "alternate");
        predictor.Reconcile(Pane, alt, null, fullOrReanchor: false);
        Assert.Null(predictor.View(Pane, alt));
        Assert.Equal(0, predictor.Stats().Pending);

        var made = predictor.Stats().Made;
        Type(predictor, "c", alt);
        Assert.Equal(0, predictor.Stats().Pending);
        Assert.Equal(made, predictor.Stats().Made);
    }

    [Fact]
    public void On_the_alternate_screen_a_pane_with_a_detected_agent_gets_predictions_that_confirm_and_show()
    {
        var predictor = Always();
        var prompt = Frame("> ", cursor: 2, screen: "alternate");
        Type(predictor, "a", prompt, agent: true);
        var echo = Frame("> a", cursor: 3, screen: "alternate");
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false, agentPane: true);
        Assert.Equal(1, predictor.Stats().ConfirmedByGlyph);

        Type(predictor, "b", echo, agent: true);
        var view = Assert.IsType<PredictionView>(predictor.View(Pane, echo, agentPane: true));
        var cell = Assert.Single(view.Cells);
        Assert.Equal((3, 0, "b"), (cell.Col, cell.Row, cell.Cell.Text));
        Assert.Equal(new PredictedCursor(4, 0), view.Cursor);

        var next = Frame("> ab", cursor: 4, screen: "alternate");
        predictor.Reconcile(Pane, next, _seq, fullOrReanchor: false, agentPane: true);
        Assert.Equal(2, predictor.Stats().ConfirmedByGlyph);
        Assert.Equal(0, predictor.Stats().Mismatches);
        Assert.Equal(0, predictor.Stats().ResetCalls[PredictionResetReason.AlternateScreen]);
    }

    [Fact]
    public void On_the_alternate_screen_an_agent_pane_with_a_hidden_cursor_shows_no_prediction()
    {
        var predictor = Always();
        var echo = ConfirmedOnAgentAlternateScreen(predictor, "> a", cursor: 3);
        Type(predictor, "b", echo, agent: true);
        Assert.NotNull(predictor.View(Pane, echo, agentPane: true));

        // The agent hides the cursor while it draws (ESC[?25l).
        var drawing = Frame("> a", cursor: 3, visible: false, screen: "alternate");
        predictor.Reconcile(Pane, drawing, null, fullOrReanchor: false, agentPane: true);
        Assert.Null(predictor.View(Pane, drawing, agentPane: true));

        // The hidden cursor suspends the display only: the cursor shows
        // again (ESC[?25h) and the prediction shows again.
        predictor.Reconcile(Pane, echo, null, fullOrReanchor: false, agentPane: true);
        Assert.NotNull(predictor.View(Pane, echo, agentPane: true));
        Assert.Equal(0, predictor.Stats().Mismatches);
    }

    [Fact]
    public void On_the_alternate_screen_an_agent_pane_in_synchronized_output_shows_no_prediction()
    {
        var predictor = Always();
        var echo = ConfirmedOnAgentAlternateScreen(predictor, "> a", cursor: 3);
        Type(predictor, "b", echo, agent: true);
        var sync = Frame("> a", cursor: 3, screen: "alternate", sync: true);
        Assert.Null(predictor.View(Pane, sync, agentPane: true));
        predictor.Reconcile(Pane, sync, null, fullOrReanchor: false, agentPane: true);
        Assert.Equal(0, predictor.Stats().Pending);
        Assert.Equal(1, predictor.Stats().ResetCalls[PredictionResetReason.SynchronizedOutput]);
    }

    [Fact]
    public void An_agent_pane_that_loses_its_agent_on_the_alternate_screen_resets_the_predictor()
    {
        var predictor = Always();
        var echo = ConfirmedOnAgentAlternateScreen(predictor, "> a", cursor: 3);
        Type(predictor, "b", echo, agent: true);
        Assert.NotNull(predictor.View(Pane, echo, agentPane: true));

        // The agent field is now none: the program that stays is not an agent.
        Assert.Null(predictor.View(Pane, echo, agentPane: false));
        predictor.Reconcile(Pane, echo, null, fullOrReanchor: false, agentPane: false);
        Assert.Equal(0, predictor.Stats().Pending);
        Assert.Equal(1, predictor.Stats().ResetCalls[PredictionResetReason.AlternateScreen]);
    }

    [Fact]
    public void Synchronized_output_turns_prediction_off()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "> a", cursor: 3);
        Type(predictor, "b", echo);
        var sync = Frame("> a", cursor: 3, sync: true);
        Assert.Null(predictor.View(Pane, sync));
        predictor.Reconcile(Pane, sync, null, fullOrReanchor: false);
        Assert.Equal(0, predictor.Stats().Pending);
    }

    [Fact]
    public void A_paste_payload_turns_prediction_off_until_its_ack()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "> a", cursor: 3);
        Type(predictor, "b", echo);
        Type(predictor, "pasted", echo, paste: true);
        Assert.Equal(0, predictor.Stats().Pending);

        // A key after the paste waits for the frame that acks the paste.
        Type(predictor, "c", echo);
        Assert.Equal(0, predictor.Stats().Pending);
        var pasted = Frame("> abpasted", cursor: 10);
        predictor.Reconcile(Pane, pasted, _seq, fullOrReanchor: false);
        Type(predictor, "d", pasted);
        Assert.Equal(1, predictor.Stats().Pending);
    }

    [Fact]
    public void An_unpredicted_key_ends_the_epoch_and_the_next_key_starts_a_tentative_one()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        Type(predictor, "\r", echo);
        Type(predictor, "l", echo);
        Assert.Equal(0, predictor.Stats().Pending);

        var next = Frame(["$ a", "$ l"], cursor: 3, cursorRow: 1);
        predictor.Reconcile(Pane, next, _seq, fullOrReanchor: false);
        Type(predictor, "s", next);
        // The new epoch is tentative.
        Assert.Null(predictor.View(Pane, next));
        predictor.Reconcile(Pane, Frame(["$ a", "$ ls"], cursor: 4, cursorRow: 1), _seq, fullOrReanchor: false);
        Assert.Equal(0, predictor.Stats().Mismatches);
        Assert.Equal(2, predictor.Stats().ConfirmedByGlyph);
    }

    [Fact]
    public void A_prediction_acked_together_with_a_later_unpredicted_key_is_retired_not_mismatched()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        Type(predictor, "b", echo);
        predictor.NoteShown(predictor.View(Pane, echo)!);
        Type(predictor, "\r", echo);

        // The Enter scrolled the screen, so b is not where it was predicted.
        var moved = Frame(["x", "$ ab", "$ "], cursor: 2, cursorRow: 2);
        predictor.Reconcile(Pane, moved, _seq, fullOrReanchor: false);
        _time.Advance(EchoPredictor.JudgeDelay);
        predictor.Reconcile(Pane, moved, _seq, fullOrReanchor: false);
        var stats = predictor.Stats();
        Assert.Equal(1, stats.Retired);
        Assert.Equal(0, stats.Mismatches);
        Assert.Equal(0, stats.MispredictedShown);
        Assert.Null(predictor.View(Pane, moved));
    }

    [Fact]
    public void A_hidden_cursor_frame_suspends_the_display_and_keeps_the_epoch()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "> a", cursor: 3);
        Type(predictor, " ", echo);

        // Claude Code hides the cursor while it draws the echo. A capture
        // between ESC[?25l and ESC[?25h has the cursor at a draw position.
        var drawing = Frame(["> a", "", "", ""], cursor: 0, cursorRow: 3, visible: false);
        predictor.Reconcile(Pane, drawing, 2, fullOrReanchor: false);
        Assert.Null(predictor.View(Pane, drawing));
        _time.Advance(EchoPredictor.JudgeDelay * 2);
        predictor.Reconcile(Pane, drawing, 2, fullOrReanchor: false);
        Assert.Equal(0, predictor.Stats().Mismatches);
        Assert.Equal(1, predictor.Stats().Pending);

        // ESC[?25h: the display continues and the space confirms.
        var shown = Frame(["> a", "", "", ""], cursor: 4);
        Assert.NotNull(predictor.View(Pane, shown));
        predictor.Reconcile(Pane, shown, 2, fullOrReanchor: false);
        Assert.Equal(1, predictor.Stats().ConfirmedByCursor);
        Assert.Equal(0, predictor.Stats().Mismatches);
    }

    [Fact]
    public void A_suspended_judge_does_not_rearm_the_timer_until_a_frame_comes()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "> a", cursor: 3);
        Type(predictor, " ", echo);
        var drawing = Frame(["> a", "", "", ""], cursor: 0, cursorRow: 3, visible: false);
        predictor.Reconcile(Pane, drawing, _seq, fullOrReanchor: false);
        _time.Advance(EchoPredictor.JudgeDelay);
        predictor.Tick(Pane, drawing);
        Assert.Equal(1, predictor.Stats().Pending);
        // Only the 250 ms show rule waits. The judge does not run again.
        var wake = Assert.IsType<TimeSpan>(predictor.NextWake());
        Assert.True(wake > TimeSpan.Zero);
        _time.Advance(wake);
        predictor.Tick(Pane, drawing);
        Assert.Null(predictor.NextWake());
        Assert.Equal(1, predictor.Stats().Pending);
    }

    [Fact]
    public void Keys_typed_while_an_unpredicted_key_waits_do_not_hide_its_ack_from_the_retire_rule()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        Type(predictor, "b", echo);
        predictor.NoteShown(predictor.View(Pane, echo)!);
        Type(predictor, "\r", echo);
        var enter = _seq;
        Type(predictor, "l", echo);

        // The frame acks the Enter only. The screen scrolled.
        var moved = Frame(["x", "$ ab", "$ "], cursor: 2, cursorRow: 2);
        predictor.Reconcile(Pane, moved, enter, fullOrReanchor: false);
        _time.Advance(EchoPredictor.JudgeDelay);
        predictor.Reconcile(Pane, moved, enter, fullOrReanchor: false);
        Assert.Equal(1, predictor.Stats().Retired);
        Assert.Equal(0, predictor.Stats().Mismatches);

        // The l is still unacked, so a new key waits too.
        Type(predictor, "s", moved);
        Assert.Equal(0, predictor.Stats().Pending);
    }

    [Fact]
    public void A_no_echo_prompt_never_shows_a_prediction()
    {
        var predictor = Always();
        var prompt = Frame("Password: ", cursor: 10);
        foreach (var key in "secret")
        {
            Type(predictor, key.ToString(), prompt);
            predictor.Reconcile(Pane, prompt, _seq, fullOrReanchor: false);
            Assert.Null(predictor.View(Pane, prompt));
            _time.Advance(TimeSpan.FromMilliseconds(120));
            predictor.Tick(Pane, prompt);
            Assert.Null(predictor.View(Pane, prompt));
        }

        Assert.Equal(0, predictor.Stats().PredictedShown);
        Assert.Equal(0, predictor.Stats().ConfirmedByGlyph);
    }

    [Fact]
    public void A_peer_without_input_ack_gets_no_prediction()
    {
        var predictor = Always();
        var prompt = Frame("$ ", cursor: 2);
        var mark = predictor.ReserveMark();
        predictor.Observe(Pane, "a"u8, mark, prompt, paste: false, allowed: true);
        predictor.OnSeqAssigned(Pane, seq: null, mark);
        Assert.Equal(0, predictor.Stats().Pending);
        mark = predictor.ReserveMark();
        predictor.Observe(Pane, "b"u8, mark, prompt, paste: false, allowed: true);
        Assert.Equal(0, predictor.Stats().Pending);
    }

    [Fact]
    public void A_seq_bound_before_the_observe_still_reaches_the_prediction()
    {
        var predictor = Always();
        var prompt = Frame("$ ", cursor: 2);
        var mark = predictor.ReserveMark();
        // The sender can put the batch on the wire before the stdin thread
        // records the prediction.
        predictor.OnSeqAssigned(Pane, 7, mark);
        predictor.Observe(Pane, "a"u8, mark, prompt, paste: false, allowed: true);
        predictor.Reconcile(Pane, Frame("$ a", cursor: 3), 7, fullOrReanchor: false);
        Assert.Equal(1, predictor.Stats().ConfirmedByGlyph);
    }

    [Fact]
    public void A_predicted_cell_has_the_style_of_the_cell_that_confirms_it()
    {
        var predictor = Always();
        var prompt = Frame("$ ", cursor: 2);
        Type(predictor, "a", prompt);
        // zsh highlighting colors the typed word.
        var echo = Frame("$ a", cursor: 3, style: (2, Green));
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false);
        Type(predictor, "b", echo);
        var predicted = Assert.Single(predictor.View(Pane, echo)!.Cells).Cell;

        var confirmed = Frame("$ ab", cursor: 4, style: (2, Green));
        Assert.Equal(confirmed.CellAt(3, 0), predicted);
        Assert.Equal(Green, predicted.Style);
        Assert.False(predicted.Style.Underline);
        Assert.False(predicted.Style.Dim);
    }

    [Fact]
    public void Arrows_move_the_predicted_cursor_and_confirm_by_the_cursor()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ ab", cursor: 4);
        Type(predictor, "\x1b[D", echo);
        Assert.Equal(new PredictedCursor(3, 0), predictor.View(Pane, echo)!.Cursor);
        var left = Frame("$ ab", cursor: 3);
        predictor.Reconcile(Pane, left, _seq, fullOrReanchor: false);
        Type(predictor, "\x1b[C", left);
        Assert.Equal(new PredictedCursor(4, 0), predictor.View(Pane, left)!.Cursor);
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false);
        Assert.Equal(2, predictor.Stats().ConfirmedByCursor);

        // With DECCKM set, only the SS3 form is an arrow.
        var app = Frame("$ ab", cursor: 4, applicationCursor: true);
        predictor.Reconcile(Pane, app, _seq, fullOrReanchor: false);
        Type(predictor, "\x1b[D", app);
        Assert.Equal(0, predictor.Stats().Pending);
    }

    [Fact]
    public void The_zsh_erase_and_space_sequence_confirms_every_key()
    {
        var predictor = Always();
        // $ foo  with the cursor at column 7. zsh echoes DEL over a
        // trailing space as a bare \b, and a space as a space.
        var frame = Frame("$ foo  ", cursor: 7);
        (string Key, int Cursor, string Text)[] steps =
        [
            ("\x7f", 6, "$ foo  "),
            (" ", 7, "$ foo  "),
            ("\x7f", 6, "$ foo  "),
            (" ", 7, "$ foo  "),
            ("b", 8, "$ foo  b"),
        ];
        foreach (var (key, cursor, text) in steps)
        {
            Type(predictor, key, frame);
            if (predictor.View(Pane, frame) is { } view)
                Assert.Equal(new PredictedCursor(cursor, 0), view.Cursor);
            frame = Frame(text, cursor);
            predictor.Reconcile(Pane, frame, _seq, fullOrReanchor: false);
            Assert.Equal(0, predictor.Stats().Pending);
        }

        var stats = predictor.Stats();
        Assert.Equal(4, stats.ConfirmedByCursor);
        Assert.Equal(1, stats.ConfirmedByGlyph);
        Assert.Equal(0, stats.Mismatches);
    }

    [Fact]
    public void The_last_column_and_a_wide_cell_end_the_epoch()
    {
        var predictor = Always();
        var edge = Confirmed(predictor, "$ " + new string('x', Cols - 3), cursor: Cols - 1);
        Type(predictor, "y", edge);
        Assert.Equal(0, predictor.Stats().Pending);

        var wide = Frame("$ ", cursor: 2) with
        {
            Cells =
            [
                [Cell("$"), Cell(" "), new AssembledCell("中", 2, false, AssembledStyle.Default), new AssembledCell("", 1, true, AssembledStyle.Default)],
            ],
        };
        predictor.Reconcile(Pane, wide, _seq, fullOrReanchor: false);
        Type(predictor, "z", wide);
        Assert.Equal(0, predictor.Stats().Pending);
    }

    [Fact]
    public void A_resize_a_reanchor_and_a_focus_change_delete_the_epoch()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        Type(predictor, "b", echo);
        predictor.Reconcile(Pane, Frame("$ a", cursor: 3, cols: Cols + 1), null, fullOrReanchor: false);
        Assert.Equal(0, predictor.Stats().Pending);

        echo = Frame("$ a", cursor: 3);
        Type(predictor, "b", echo);
        predictor.Reconcile(Pane, echo, null, fullOrReanchor: true);
        Assert.Equal(0, predictor.Stats().Pending);

        Type(predictor, "b", echo);
        Assert.Equal(1, predictor.Stats().Pending);
        var mark = predictor.ReserveMark();
        predictor.Observe("p2", "c"u8, mark, echo, paste: false, allowed: true);
        Assert.Equal("p2", predictor.PaneId);
        Assert.Null(predictor.View(Pane, echo));
    }

    [Fact]
    public void Prediction_is_off_while_the_overlay_owns_the_screen_or_the_pane_is_scrolled_back()
    {
        var predictor = Always();
        var echo = Confirmed(predictor, "$ a", cursor: 3);
        Type(predictor, "b", echo);
        var mark = predictor.ReserveMark();
        predictor.Observe(Pane, "c"u8, mark, echo, paste: false, allowed: false);
        Assert.Equal(0, predictor.Stats().Pending);
    }

    [Fact]
    public void Never_records_nothing()
    {
        var predictor = new EchoPredictor(_time, new NeverEchoDisplayPolicy());
        Assert.False(predictor.Predicts);
        var prompt = Frame("$ ", cursor: 2);
        Type(predictor, "a", prompt);
        predictor.Reconcile(Pane, Frame("$ a", cursor: 3), 1, fullOrReanchor: false);
        Type(predictor, "b", prompt);
        Assert.Null(predictor.View(Pane, prompt));
        Assert.Equal(0, predictor.Stats().Pending);
        Assert.Null(predictor.NextWake());
    }

    [Fact]
    public void Adaptive_shows_above_thirty_ms_and_hides_at_twenty_ms_or_less()
    {
        var policy = new AdaptiveEchoDisplayPolicy();
        var young = TimeSpan.Zero;
        Assert.False(policy.ShouldDisplay(null, young));
        Assert.False(policy.ShouldDisplay(TimeSpan.FromMilliseconds(30), young));
        Assert.True(policy.ShouldDisplay(TimeSpan.FromMilliseconds(31), young));
        // Between the two values the last decision stays.
        Assert.True(policy.ShouldDisplay(TimeSpan.FromMilliseconds(25), young));
        Assert.True(policy.ShouldDisplay(TimeSpan.FromMilliseconds(21), young));
        Assert.False(policy.ShouldDisplay(TimeSpan.FromMilliseconds(20), young));
        Assert.False(policy.ShouldDisplay(TimeSpan.FromMilliseconds(25), young));
        Assert.False(policy.ShouldDisplay(TimeSpan.FromMilliseconds(30), young));
        Assert.True(policy.ShouldDisplay(TimeSpan.FromMilliseconds(40), young));
    }

    [Fact]
    public void Adaptive_shows_a_pending_prediction_older_than_250_ms_at_a_small_rtt()
    {
        var policy = new AdaptiveEchoDisplayPolicy();
        var small = TimeSpan.FromMilliseconds(5);
        Assert.False(policy.ShouldDisplay(small, TimeSpan.FromMilliseconds(249)));
        Assert.True(policy.ShouldDisplay(small, TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public void Adaptive_measures_the_rtt_from_the_seq_send_to_the_ack()
    {
        var predictor = new EchoPredictor(_time, new AdaptiveEchoDisplayPolicy());
        var prompt = Frame("$ ", cursor: 2);
        Type(predictor, "a", prompt);
        _time.Advance(TimeSpan.FromMilliseconds(80));
        var echo = Frame("$ a", cursor: 3);
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false);
        Assert.Equal(TimeSpan.FromMilliseconds(80), predictor.Stats().Srtt);
        Type(predictor, "b", echo);
        Assert.NotNull(predictor.View(Pane, echo));
    }

    [Fact]
    public void Adaptive_shows_nothing_for_a_local_pane()
    {
        var predictor = new EchoPredictor(_time, new AdaptiveEchoDisplayPolicy());
        var text = "$ ";
        var frame = Frame(text, cursor: 2);
        foreach (var key in "hello")
        {
            Type(predictor, key.ToString(), frame);
            _time.Advance(TimeSpan.FromMilliseconds(2));
            text += key;
            frame = Frame(text, cursor: text.Length);
            Assert.Null(predictor.View(Pane, frame));
            predictor.Reconcile(Pane, frame, _seq, fullOrReanchor: false);
        }

        Assert.Equal(0, predictor.Stats().PredictedShown);
        Assert.True(predictor.Stats().Srtt <= TimeSpan.FromMilliseconds(2));
    }

    [Fact]
    public void The_key_reader_counts_keys_as_the_latency_meter_does()
    {
        var bytes = Encoding.UTF8.GetBytes("a\x1b[D\x1bOC\x1b[1;5Cé\x7f\r");
        var keys = EchoKeyReader.Read(bytes, applicationCursor: false);
        Assert.Equal(AttachLatencyMeter.CountKeys(bytes), keys.Count);
        Assert.Equal(
            [EchoKeyKind.Printable, EchoKeyKind.Left, EchoKeyKind.Other, EchoKeyKind.Other, EchoKeyKind.Other, EchoKeyKind.Erase, EchoKeyKind.Other],
            keys.Select(k => k.Kind));
        Assert.Equal(
            EchoKeyKind.Right,
            Assert.Single(EchoKeyReader.Read("\x1bOC"u8, applicationCursor: true)).Kind);
    }

    [Fact]
    public void A_synchronized_output_block_older_than_the_mux_hold_no_longer_stops_prediction()
    {
        var predictor = Always();
        var held = Frame("$ ", cursor: 2, sync: true);
        Type(predictor, "a", held);
        Assert.Equal(0, predictor.Stats().Made);

        // The program ended inside the block. The mux paints it after 1 s.
        _time.Advance(EchoPredictor.SynchronizedOutputStale);
        held = Frame("$ a", cursor: 3, sync: true);
        predictor.Reconcile(Pane, held, _seq, fullOrReanchor: false);
        Type(predictor, "a", held);
        Assert.Equal(1, predictor.Stats().Made);
        var echo = Frame("$ aa", cursor: 4, sync: true);
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false);
        Type(predictor, "b", echo);
        Assert.NotNull(predictor.View(Pane, echo));

        // A new block after the mode closed stops prediction again.
        predictor.Reconcile(Pane, Frame("$ aab", cursor: 5), _seq, fullOrReanchor: false);
        Type(predictor, "c", Frame("$ aab", cursor: 5, sync: true));
        Assert.Equal(2, predictor.Stats().Made);
    }

    [Fact]
    public void An_agent_pane_on_the_alternate_screen_predicts_again_when_its_block_is_older_than_the_mux_hold()
    {
        var predictor = Always();
        var held = Frame("> ", cursor: 2, screen: "alternate", sync: true);
        Type(predictor, "a", held, agent: true);
        Assert.Equal(0, predictor.Stats().Made);

        // The agent ended a draw inside the block. The mux paints it after 1 s.
        _time.Advance(EchoPredictor.SynchronizedOutputStale);
        held = Frame("> a", cursor: 3, screen: "alternate", sync: true);
        predictor.Reconcile(Pane, held, _seq, fullOrReanchor: false, agentPane: true);
        Type(predictor, "b", held, agent: true);
        Assert.Equal(1, predictor.Stats().Made);
    }

    [Fact]
    public void Adaptive_forgets_the_rtt_of_the_previous_pane()
    {
        var predictor = new EchoPredictor(_time, new AdaptiveEchoDisplayPolicy());
        var prompt = Frame("$ ", cursor: 2);
        Type(predictor, "a", prompt);
        _time.Advance(TimeSpan.FromMilliseconds(80));
        var echo = Frame("$ a", cursor: 3);
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false);
        Type(predictor, "b", echo);
        Assert.NotNull(predictor.View(Pane, echo));

        // The keys move to a local pane. Its echo comes in 2 ms.
        const string local = "local-pane";
        var mark = predictor.ReserveMark(local);
        predictor.Observe(local, "x"u8, mark, prompt, paste: false, allowed: true);
        predictor.OnSeqAssigned(local, ++_seq, mark);
        _time.Advance(TimeSpan.FromMilliseconds(2));
        var localEcho = Frame("$ x", cursor: 3);
        predictor.Reconcile(local, localEcho, _seq, fullOrReanchor: false);
        mark = predictor.ReserveMark(local);
        predictor.Observe(local, "y"u8, mark, localEcho, paste: false, allowed: true);
        predictor.OnSeqAssigned(local, ++_seq, mark);
        Assert.Null(predictor.View(local, localEcho));
        Assert.Equal(TimeSpan.FromMilliseconds(2), predictor.Stats().Srtt);
    }

    [Fact]
    public void Adaptive_keeps_showing_on_another_pane_of_the_same_cube()
    {
        var predictor = new EchoPredictor(_time, new AdaptiveEchoDisplayPolicy());
        var prompt = Frame("$ ", cursor: 2);
        Type(predictor, "a", prompt);
        _time.Advance(TimeSpan.FromMilliseconds(80));
        var echo = Frame("$ a", cursor: 3);
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false);
        Type(predictor, "b", echo);
        Assert.NotNull(predictor.View(Pane, echo));

        // The second pane has an RTT between the hide and show thresholds,
        // above the new-route threshold.
        const string second = "second-pane";
        var mark = predictor.ReserveMark(second);
        predictor.OnSeqAssigned(second, ++_seq, mark);
        predictor.Observe(second, "x"u8, mark, prompt, paste: false, allowed: true);
        _time.Advance(TimeSpan.FromMilliseconds(28));
        var secondEcho = Frame("$ x", cursor: 3);
        predictor.Reconcile(second, secondEcho, _seq, fullOrReanchor: false);
        Assert.Equal(TimeSpan.FromMilliseconds(28), predictor.Stats().Srtt);
        mark = predictor.ReserveMark(second);
        predictor.Observe(second, "y"u8, mark, secondEcho, paste: false, allowed: true);
        predictor.OnSeqAssigned(second, ++_seq, mark);
        Assert.NotNull(predictor.View(second, secondEcho));
    }

    [Fact]
    public void Adaptive_decides_a_new_route_without_the_state_of_the_old_route()
    {
        var policy = new AdaptiveEchoDisplayPolicy();
        Assert.True(policy.ShouldDisplay(TimeSpan.FromMilliseconds(80), TimeSpan.Zero));
        // Inside the hysteresis band the old decision stays on the same route.
        Assert.True(policy.ShouldDisplay(TimeSpan.FromMilliseconds(22), TimeSpan.Zero));

        policy.NewRoute();
        Assert.False(policy.ShouldDisplay(null, TimeSpan.Zero));
        Assert.False(policy.ShouldDisplay(TimeSpan.FromMilliseconds(22), TimeSpan.Zero));
        Assert.False(policy.ShouldDisplay(TimeSpan.FromMilliseconds(28), TimeSpan.Zero));
        Assert.True(policy.ShouldDisplay(TimeSpan.FromMilliseconds(31), TimeSpan.Zero));

        policy.NewRoute();
        Assert.True(policy.ShouldDisplay(TimeSpan.FromMilliseconds(28), TimeSpan.Zero));
    }

    [Fact]
    public void The_report_counters_name_each_made_prediction_and_each_reset_reason()
    {
        var predictor = Always();
        var prompt = Frame("$ ", cursor: 2);
        Type(predictor, "ab", prompt);
        Type(predictor, "x", prompt, paste: true);
        predictor.Reconcile(Pane, Frame("$ ", cursor: 2, screen: "alternate"), _seq, fullOrReanchor: false);

        var counters = AttachSession.PredictionCounters(predictor.Stats());
        Assert.Equal(2, counters["made"]);
        Assert.Equal(1, counters["reset.paste"]);
        Assert.Equal(1, counters["reset.alternate_screen"]);
        Assert.Equal(0, counters["reset.no_ack"]);
        // The alternate screen frame ends the reconcile before its ack, so no RTT sample exists.
        Assert.Equal(-1, counters["srtt_us"]);
    }

    private EchoPredictor Always() => new(_time, new AlwaysEchoDisplayPolicy());

    /// <summary>Types "a" at the cursor of <paramref name="text"/> and confirms it.</summary>
    private AssembledSnapshot Confirmed(EchoPredictor predictor, string text, int cursor)
    {
        var before = Frame(text[..^1], cursor - 1);
        Type(predictor, text[^1].ToString(), before);
        var echo = Frame(text, cursor);
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false);
        Assert.True(predictor.Stats().ConfirmedByGlyph >= 1);
        return echo;
    }

    /// <summary>Types "a" on the alternate screen of an agent pane and confirms it.</summary>
    private AssembledSnapshot ConfirmedOnAgentAlternateScreen(EchoPredictor predictor, string text, int cursor)
    {
        var before = Frame(text[..^1], cursor - 1, screen: "alternate");
        Type(predictor, text[^1].ToString(), before, agent: true);
        var echo = Frame(text, cursor, screen: "alternate");
        predictor.Reconcile(Pane, echo, _seq, fullOrReanchor: false, agentPane: true);
        Assert.True(predictor.Stats().ConfirmedByGlyph >= 1);
        return echo;
    }

    /// <summary>The stdin thread and the input sender steps for one payload.</summary>
    private void Type(EchoPredictor predictor, string text, AssembledSnapshot frame, bool paste = false, bool agent = false)
    {
        var mark = predictor.ReserveMark();
        predictor.Observe(Pane, Encoding.UTF8.GetBytes(text), mark, frame, paste, allowed: true, agentPane: agent);
        predictor.OnSeqAssigned(Pane, ++_seq, mark);
    }

    private static AssembledCell Cell(string text) => new(text, 1, false, AssembledStyle.Default);

    private static AssembledSnapshot Frame(
        string text,
        int cursor,
        bool visible = true,
        string screen = "main",
        bool sync = false,
        int cols = Cols,
        bool applicationCursor = false,
        (int From, AssembledStyle Style)? style = null) =>
        Frame([text], cursor, 0, visible, screen, sync, cols, applicationCursor, style);

    private static AssembledSnapshot Frame(
        string[] lines,
        int cursor,
        int cursorRow = 0,
        bool visible = true,
        string screen = "main",
        bool sync = false,
        int cols = Cols,
        bool applicationCursor = false,
        (int From, AssembledStyle Style)? style = null)
    {
        var rows = new List<IReadOnlyList<AssembledCell>>();
        for (var r = 0; r < Math.Max(4, lines.Length); r++)
        {
            var line = r < lines.Length ? lines[r] : "";
            var row = new List<AssembledCell>(cols);
            for (var c = 0; c < cols; c++)
            {
                var text = c < line.Length ? line[c].ToString() : " ";
                var cellStyle = r == 0 && style is { } s && c >= s.From && c < line.Length ? s.Style : AssembledStyle.Default;
                row.Add(new AssembledCell(text, 1, false, cellStyle));
            }

            rows.Add(row);
        }

        return new AssembledSnapshot(
            Pane,
            cols,
            rows.Count,
            "ghostty",
            screen,
            rows,
            default,
            new AssembledCursor(cursor, cursorRow, visible),
            SynchronizedOutput: sync,
            ApplicationCursor: applicationCursor);
    }
}
