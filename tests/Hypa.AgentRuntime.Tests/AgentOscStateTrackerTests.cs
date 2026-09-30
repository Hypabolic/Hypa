using System.Text;
using Hypa.Terminal.Vt;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class AgentOscStateTrackerTests
{
    [Fact]
    public void Osc_zero_title_is_retained_and_empty_clears()
    {
        var tracker = new AgentOscStateTracker();
        tracker.Observe(Encoding.UTF8.GetBytes("\u001b]0;grok\u0007"));
        Assert.Equal("grok", tracker.LatestTitle);
        tracker.Observe(Encoding.UTF8.GetBytes("\u001b]0;\u0007"));
        Assert.Equal("", tracker.LatestTitle);
    }

    [Fact]
    public void Osc_nine_progress_keeps_payload_after_command()
    {
        var tracker = new AgentOscStateTracker();
        tracker.Observe(Encoding.UTF8.GetBytes("\u001b]9;4;1;-1\u0007"));
        Assert.Equal("4;1;-1", tracker.LatestProgress);
    }

    [Fact]
    public void Clear_retained_drops_title_and_progress()
    {
        var tracker = new AgentOscStateTracker();
        tracker.Observe(Encoding.UTF8.GetBytes("\u001b]0;grok\u0007\u001b]9;4;0;0\u0007"));
        tracker.ClearRetained();
        Assert.Equal("", tracker.LatestTitle);
        Assert.Equal("", tracker.LatestProgress);
    }

    [Fact]
    public void Oversized_body_bel_returns_to_ground_for_the_next_title()
    {
        var tracker = new AgentOscStateTracker();
        tracker.Observe(new byte[] { 0x1b, (byte)']' });
        var body = new byte[AgentOscStateTracker.MaxBodyBytes];
        Array.Fill(body, (byte)'x');
        tracker.Observe(body);
        tracker.Observe(new byte[] { 0x1b, 0x07 });
        tracker.Observe(Encoding.UTF8.GetBytes("\u001b]0;kept\u0007"));
        Assert.Equal("kept", tracker.LatestTitle);
    }

    [Fact]
    public void Sanitize_counts_unicode_scalars_not_utf16_units()
    {
        var tracker = new AgentOscStateTracker();
        var emoji = new StringBuilder();
        for (var i = 0; i < 200; i++)
            emoji.Append("😀");
        tracker.Observe(Encoding.UTF8.GetBytes("\u001b]0;" + emoji + "\u0007"));
        var scalars = 0;
        foreach (var _ in tracker.LatestTitle.EnumerateRunes())
            scalars++;
        Assert.Equal(200, scalars);
    }
}
