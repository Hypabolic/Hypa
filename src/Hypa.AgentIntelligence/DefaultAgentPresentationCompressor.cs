using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentIntelligence;

/// <summary>
/// Default agent presentation compressor: ANSI strip, blank collapse, light dedupe,
/// line/byte budgets. Does not mutate VT state. On timeout/exception returns head+tail fallback.
/// </summary>
public sealed partial class DefaultAgentPresentationCompressor : IAgentPresentationCompressor
{
    public const string CompressorId = "default";
    public const int DefaultMaxLines = 200;
    public const int DefaultMaxCompressedBytes = 32 * 1024;

    public PresentationResult Compress(PresentationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();
        var maxLines = request.MaxLines > 0 ? request.MaxLines : DefaultMaxLines;
        var maxBytes = request.MaxCompressedBytes > 0
            ? request.MaxCompressedBytes
            : DefaultMaxCompressedBytes;
        var timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : 250;
        var raw = request.RawText ?? string.Empty;

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var text = CompressCore(raw, maxLines, maxBytes, cts.Token);
            sw.Stop();
            var truncated = !string.Equals(text, StripAnsi(raw), StringComparison.Ordinal)
                && (CountLines(text) >= maxLines || Encoding.UTF8.GetByteCount(text) >= maxBytes);
            // Conservative truncated flag: budgets hit or input was shortened.
            if (Encoding.UTF8.GetByteCount(raw) > maxBytes || CountLines(raw) > maxLines)
                truncated = true;

            return new PresentationResult
            {
                Text = text,
                Truncated = truncated,
                CompressorId = CompressorId,
                ElapsedMs = sw.ElapsedMilliseconds,
                UsedFallback = false,
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return Fallback(raw, maxBytes, sw.ElapsedMilliseconds, usedFallback: true);
        }
        catch (Exception)
        {
            sw.Stop();
            return Fallback(raw, maxBytes, sw.ElapsedMilliseconds, usedFallback: true);
        }
    }

    private static PresentationResult Fallback(
        string raw, int maxBytes, long elapsedMs, bool usedFallback)
    {
        var stripped = StripAnsi(raw);
        var text = HeadTailTruncate(stripped, maxBytes);
        return new PresentationResult
        {
            Text = text,
            Truncated = text.Length < stripped.Length,
            CompressorId = CompressorId,
            ElapsedMs = elapsedMs,
            UsedFallback = usedFallback,
        };
    }

    private static string CompressCore(
        string raw, int maxLines, int maxBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = StripAnsi(raw);
        ct.ThrowIfCancellationRequested();
        text = CollapseBlankLines().Replace(text, "\n\n");
        ct.ThrowIfCancellationRequested();
        text = DeduplicateConsecutiveLines(text);
        ct.ThrowIfCancellationRequested();

        // Line budget (keep head + tail).
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > maxLines)
        {
            var headCount = maxLines * 2 / 3;
            var tailCount = maxLines - headCount - 1;
            if (tailCount < 1)
                tailCount = 1;
            if (headCount < 1)
                headCount = maxLines - tailCount - 1;
            var sb = new StringBuilder();
            for (var i = 0; i < headCount; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(lines[i]);
            }

            sb.Append("\n…[truncated lines]…\n");
            for (var i = lines.Length - tailCount; i < lines.Length; i++)
            {
                if (i > lines.Length - tailCount) sb.Append('\n');
                sb.Append(lines[i]);
            }

            text = sb.ToString();
        }

        ct.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
            text = HeadTailTruncate(text, maxBytes);

        return text;
    }

    private static string HeadTailTruncate(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        var marker = "\n…[truncated]…\n";
        var markerBytes = Encoding.UTF8.GetByteCount(marker);
        var budget = Math.Max(64, maxBytes - markerBytes);
        var headBudget = budget * 2 / 3;
        var tailBudget = budget - headBudget;

        var head = TakeUtf8Prefix(text, headBudget);
        var tail = TakeUtf8Suffix(text, tailBudget);
        return head + marker + tail;
    }

    private static string TakeUtf8Prefix(string text, int maxBytes)
    {
        if (maxBytes <= 0)
            return string.Empty;
        var encoder = Encoding.UTF8;
        var bytes = encoder.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return text;
        // Back up to a valid UTF-8 boundary.
        var len = maxBytes;
        while (len > 0 && (bytes[len] & 0xC0) == 0x80)
            len--;
        return encoder.GetString(bytes, 0, len);
    }

    private static string TakeUtf8Suffix(string text, int maxBytes)
    {
        if (maxBytes <= 0)
            return string.Empty;
        var encoder = Encoding.UTF8;
        var bytes = encoder.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return text;
        var start = bytes.Length - maxBytes;
        while (start < bytes.Length && (bytes[start] & 0xC0) == 0x80)
            start++;
        return encoder.GetString(bytes, start, bytes.Length - start);
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var n = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
                n++;
        }

        return n;
    }

    private static string StripAnsi(string text) => AnsiRegex().Replace(text, string.Empty);

    private static string DeduplicateConsecutiveLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length <= 1)
            return text;

        var sb = new StringBuilder(text.Length);
        string? prev = null;
        var repeat = 0;
        var first = true;

        void Flush()
        {
            if (prev is null)
                return;
            if (!first)
                sb.Append('\n');
            first = false;
            sb.Append(prev);
            if (repeat > 1 && prev.Length > 0)
                sb.Append("  ×").Append(repeat);
        }

        foreach (var line in lines)
        {
            if (line == prev)
            {
                repeat++;
                continue;
            }

            Flush();
            prev = line;
            repeat = 1;
        }

        Flush();
        return sb.ToString();
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\a\x1B]*(?:\a|\x1B\\)|\x1B.", RegexOptions.Compiled)]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled)]
    private static partial Regex CollapseBlankLines();
}
