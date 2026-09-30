using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Hypa.AgentIntelligence;
using Hypa.AgentRuntime.Tests.Support;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class RedactorFastPathTests
{
    [Fact]
    public void Build_shaped_chunk_without_anchor_bytes_returns_the_input_memory()
    {
        var data = CleanChunk(8192, (byte)'C');
        Assert.False(TerminalSecretAnchors.ContainsLeadByte(data));
        var redactor = new DefaultEventPayloadRedactor();
        var result = redactor.RedactTerminalBytes("pane_clean", data);
        Assert.True(SameMemory(data, result));
    }

    [Fact]
    public void Build_shaped_chunk_without_anchor_bytes_allocates_no_bytes()
    {
        var data = CleanChunk(8192, (byte)'C');
        var redactor = new DefaultEventPayloadRedactor();
        Assert.Equal(0, AllocatedBytes(() => redactor.RedactTerminalBytes("pane_alloc", data)));
    }

    [Fact]
    public void Fill_output_chunk_returns_the_input_memory()
    {
        var data = YFill(8192);
        Assert.False(TerminalSecretAnchors.ContainsLeadByte(data));
        var redactor = new DefaultEventPayloadRedactor();
        var result = redactor.RedactTerminalBytes("pane_fill", data);
        Assert.True(SameMemory(data, result));
        Assert.Equal(0, AllocatedBytes(() => redactor.RedactTerminalBytes("pane_fill", data)));
    }

    [Fact]
    public void Every_rule_fixture_leaves_the_fast_path()
    {
        var redactor = new DefaultEventPayloadRedactor();
        Assert.NotEmpty(RedactionRuleFixtures.All);
        foreach (var fixture in RedactionRuleFixtures.All)
        {
            var data = Encoding.UTF8.GetBytes(fixture.Input);
            var result = redactor.RedactTerminalBytes("pane_fix_" + fixture.RuleName, data);
            Assert.False(
                SameMemory(data, result),
                $"{fixture.RuleName} took the fast path for {fixture.Input}");
            redactor.FlushTerminalStream("pane_fix_" + fixture.RuleName);
        }
    }

    [Fact]
    public void Every_rule_fixture_still_redacts_the_secret()
    {
        var redactor = new DefaultEventPayloadRedactor();
        foreach (var fixture in RedactionRuleFixtures.All)
        {
            var key = "pane_secret_" + fixture.RuleName + "_" + fixture.Input.Length;
            var data = Encoding.UTF8.GetBytes(fixture.Input);
            var first = redactor.RedactTerminalBytes(key, data);
            var flush = redactor.FlushTerminalStream(key);
            var joined = Encoding.UTF8.GetString(first.Span) + Encoding.UTF8.GetString(flush.Span);
            if (fixture.Secret.Length > 0)
            {
                Assert.DoesNotContain(fixture.Secret, joined);
            }

            if (fixture.ExpectReplacement)
            {
                Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
            }
        }
    }

    [Fact]
    public void Fixture_list_covers_every_generated_rule()
    {
        var regexMethods = typeof(DefaultEventPayloadRedactor)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(Regex) && m.GetParameters().Length == 0)
            .Select(m => m.Name)
            .ToArray();
        Assert.Equal(RedactionRuleFixtures.RuleNames.Length, regexMethods.Length);
        foreach (var name in regexMethods)
        {
            Assert.Contains(name, RedactionRuleFixtures.RuleNames);
            Assert.Contains(RedactionRuleFixtures.All, f => f.RuleName == name);
        }
    }

    [Fact]
    public void Chunk_with_an_anchor_byte_but_no_anchor_word_returns_the_input_memory()
    {
        var data = Encoding.UTF8.GetBytes("packages restored to /tmp/store\n");
        Assert.True(TerminalSecretAnchors.ContainsLeadByte(data));
        Assert.False(TerminalSecretAnchors.ContainsAnchor(data));
        var redactor = new DefaultEventPayloadRedactor();
        var result = redactor.RedactTerminalBytes("pane_stage_b", data);
        Assert.True(SameMemory(data, result));
    }

    [Fact]
    public void Chunk_with_an_anchor_word_but_no_rule_match_returns_equal_bytes()
    {
        var data = Encoding.UTF8.GetBytes("token bucket refill\n");
        Assert.True(TerminalSecretAnchors.ContainsAnchor(data));
        var redactor = new DefaultEventPayloadRedactor();
        var result = redactor.RedactTerminalBytes("pane_word", data);
        Assert.False(SameMemory(data, result));
        Assert.True(data.AsSpan().SequenceEqual(result.Span));
    }

    [Fact]
    public void Secret_split_across_two_reads_is_still_redacted()
    {
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123";
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes("pane_split", Encoding.UTF8.GetBytes(token[..13]));
        var second = redactor.RedactTerminalBytes("pane_split", Encoding.UTF8.GetBytes(token[13..] + "\n"));
        var flush = redactor.FlushTerminalStream("pane_split");
        var joined = Encoding.UTF8.GetString(first.Span)
            + Encoding.UTF8.GetString(second.Span)
            + Encoding.UTF8.GetString(flush.Span);
        Assert.DoesNotContain(token, joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, Encoding.UTF8.GetString(second.Span)
            + Encoding.UTF8.GetString(flush.Span));
    }

    [Fact]
    public void Assignment_split_at_the_equals_sign_is_still_held()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var first = redactor.RedactTerminalBytes("pane_eq", Encoding.UTF8.GetBytes("API_KEY"));
        var second = redactor.RedactTerminalBytes("pane_eq", Encoding.UTF8.GetBytes("=hunter2 "));
        var flush = redactor.FlushTerminalStream("pane_eq");
        var joined = Encoding.UTF8.GetString(first.Span)
            + Encoding.UTF8.GetString(second.Span)
            + Encoding.UTF8.GetString(flush.Span);
        Assert.DoesNotContain("hunter2", joined);
    }

    [Fact]
    public void Trailing_identifier_fragment_leaves_the_fast_path()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var firstBytes = Encoding.UTF8.GetBytes("cccc MY_");
        Assert.False(TerminalSecretAnchors.ContainsAnchor(firstBytes));
        var first = redactor.RedactTerminalBytes("pane_frag", firstBytes);
        Assert.False(SameMemory(firstBytes, first));

        var second = redactor.RedactTerminalBytes("pane_frag", Encoding.UTF8.GetBytes("TOKEN=hunter2\n"));
        var flush = redactor.FlushTerminalStream("pane_frag");
        var joined = Encoding.UTF8.GetString(first.Span)
            + Encoding.UTF8.GetString(second.Span)
            + Encoding.UTF8.GetString(flush.Span);
        Assert.DoesNotContain("hunter2", joined);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, joined);
    }

    [Fact]
    public void Trailing_export_fragment_leaves_the_fast_path()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var firstBytes = Encoding.UTF8.GetBytes("cccc export ");
        Assert.False(TerminalSecretAnchors.ContainsAnchor(firstBytes));
        var first = redactor.RedactTerminalBytes("pane_export", firstBytes);
        Assert.False(SameMemory(firstBytes, first));

        var second = redactor.RedactTerminalBytes(
            "pane_export", Encoding.UTF8.GetBytes("API_KEY=hunter2\n"));
        var flush = redactor.FlushTerminalStream("pane_export");
        var joined = Encoding.UTF8.GetString(first.Span)
            + Encoding.UTF8.GetString(second.Span)
            + Encoding.UTF8.GetString(flush.Span);
        Assert.DoesNotContain("hunter2", joined);
    }

    [Fact]
    public void Trailing_incomplete_utf8_leaves_the_fast_path()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var firstBytes = new byte[] { (byte)'C', (byte)'C', (byte)'C', 0xC3 };
        Assert.False(TerminalSecretAnchors.ContainsAnchor(firstBytes));
        var first = redactor.RedactTerminalBytes("pane_utf8", firstBytes);
        Assert.False(SameMemory(firstBytes, first));
        Assert.True(first.Length < firstBytes.Length);

        var second = redactor.RedactTerminalBytes("pane_utf8", new byte[] { 0xA9, (byte)'\n' });
        var flush = redactor.FlushTerminalStream("pane_utf8");
        var joined = first.ToArray().Concat(second.ToArray()).Concat(flush.ToArray()).ToArray();
        Assert.Equal("CCCé\n", Encoding.UTF8.GetString(joined));
    }

    [Fact]
    public void Invalid_utf8_with_a_secret_still_redacts()
    {
        var secret = "sk-abcdefghijklmnopqrstuvwx"u8;
        var data = new byte[1 + secret.Length + 1];
        data[0] = 0xFF;
        secret.CopyTo(data.AsSpan(1));
        data[^1] = (byte)'\n';
        var redactor = new DefaultEventPayloadRedactor();
        var result = redactor.RedactTerminalBytes("pane_bin", data);
        var text = Encoding.UTF8.GetString(result.Span);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwx", text);
        Assert.Equal(0xFF, result.Span[0]);
        Assert.Contains(DefaultEventPayloadRedactor.Replacement, text);
    }

    [Fact]
    public void Invalid_utf8_without_an_anchor_returns_the_input_memory()
    {
        var data = new byte[] { (byte)'C', 0xFF, (byte)'C', (byte)'\n' };
        Assert.False(TerminalSecretAnchors.ContainsAnchor(data));
        Assert.False(TerminalSecretAnchors.MayStartHold(data));
        var redactor = new DefaultEventPayloadRedactor();
        var result = redactor.RedactTerminalBytes("pane_bin_clean", data);
        Assert.True(SameMemory(data, result));
    }

    [Fact]
    public void Two_stream_keys_do_not_share_one_lock()
    {
        var redactor = new DefaultEventPayloadRedactor();
        var secret = Encoding.UTF8.GetBytes("sk-abcdefghijklmnopqrstuvwxyz0123\n");
        using var hold = redactor.LockStream("pane_a");
        var started = new ManualResetEventSlim(false);
        var done = new ManualResetEventSlim(false);
        ReadOnlyMemory<byte> blocked = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                started.Set();
                blocked = redactor.RedactTerminalBytes("pane_a", secret);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });
        thread.IsBackground = true;
        thread.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(50);
        Assert.False(done.IsSet);

        var sw = Stopwatch.StartNew();
        var other = redactor.RedactTerminalBytes("pane_b", secret);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 200, $"pane_b took {sw.ElapsedMilliseconds}ms");
        Assert.Contains(
            DefaultEventPayloadRedactor.Replacement,
            Encoding.UTF8.GetString(other.Span));

        hold.Dispose();
        Assert.True(done.Wait(TimeSpan.FromSeconds(2)));
        Assert.Null(error);
        Assert.Contains(
            DefaultEventPayloadRedactor.Replacement,
            Encoding.UTF8.GetString(blocked.Span));
    }

    [Fact]
    public void Carry_state_survives_a_concurrent_drop_and_recreate()
    {
        var redactor = new DefaultEventPayloadRedactor();
        const string key = "pane_race";
        const string token = "sk-abcdefghijklmnopqrstuvwxyz0123";
        var first = Encoding.UTF8.GetBytes(token[..13]);
        var second = Encoding.UTF8.GetBytes(token[13..] + "\n");
        var leaks = 0;
        Parallel.For(0, 200, _ =>
        {
            var a = Encoding.UTF8.GetString(redactor.RedactTerminalBytes(key, first).Span);
            var b = Encoding.UTF8.GetString(redactor.RedactTerminalBytes(key, second).Span);
            if (a.Contains(token, StringComparison.Ordinal)
                || b.Contains(token, StringComparison.Ordinal))
                Interlocked.Increment(ref leaks);
        });
        var flush = Encoding.UTF8.GetString(redactor.FlushTerminalStream(key).Span);
        Assert.Equal(0, leaks);
        Assert.DoesNotContain(token, flush);
    }

    [Fact]
    public void Eight_kib_of_lead_bytes_completes_inside_a_bound()
    {
        var data = new byte[8192];
        Array.Fill(data, (byte)'s');
        data[^1] = (byte)'\n';
        var redactor = new DefaultEventPayloadRedactor();
        var sw = Stopwatch.StartNew();
        var result = redactor.RedactTerminalBytes("pane_adv", data);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"adversarial chunk took {sw.ElapsedMilliseconds}ms");
        Assert.True(data.AsSpan().SequenceEqual(result.Span));
    }

    private static byte[] CleanChunk(int length, byte fill)
    {
        var data = new byte[length];
        Array.Fill(data, fill);
        data[^1] = (byte)'\n';
        return data;
    }

    private static byte[] YFill(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = (i & 1) == 0 ? (byte)'y' : (byte)'\n';
        data[^1] = (byte)'\n';
        return data;
    }

    private static bool SameMemory(byte[] input, ReadOnlyMemory<byte> result)
    {
        if (!MemoryMarshal.TryGetArray(result, out var segment) || segment.Array is null)
            return false;
        return ReferenceEquals(segment.Array, input)
            && segment.Offset == 0
            && segment.Count == input.Length;
    }

    private static long AllocatedBytes(Action action)
    {
        for (var i = 0; i < 200; i++)
            action();

        var allocated = long.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return allocated;
    }
}
