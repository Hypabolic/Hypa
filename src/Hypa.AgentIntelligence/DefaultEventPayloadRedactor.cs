using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Hypa.AgentRuntime.Application;

namespace Hypa.AgentIntelligence;

/// <summary>
/// Default redaction pack (design §12.4). Cannot be disabled.
/// Redacts env-key-shaped secrets and common API key / bearer / AWS / PEM patterns.
/// </summary>
public sealed partial class DefaultEventPayloadRedactor : IEventPayloadRedactor
{
    public const string Replacement = "[REDACTED]";

    /// <summary>Lookback for prefix fragments. Not a cap on unterminated secrets.</summary>
    internal const int CarryChars = 256;
    internal const int PemCarryMaxChars = 16 * 1024;
    internal const int MaxHoldChars = 16 * 1024;

    private readonly ConcurrentDictionary<string, StreamCarry> _carry = new(StringComparer.Ordinal);

    private sealed class StreamCarry
    {
        public readonly Lock Gate = new();
        public string Text = "";
        public byte[] Utf8Tail = [];
        public bool SuppressUntilTerminator;
        public bool SuppressUntilPemEnd;
        public bool Removed;

        public bool IsIdle =>
            Text.Length == 0
            && Utf8Tail.Length == 0
            && !SuppressUntilTerminator
            && !SuppressUntilPemEnd;
    }

    public string RedactJsonPayload(string eventType, string payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return payloadJson ?? string.Empty;

        // terminal.output: binary secrets are redacted via RedactTerminalBytes before base64.
        // Never run sk-/PASSWORD/AKIA full-text rules over the base64 data field — those
        // patterns match inside standard base64 (+/= word boundaries) and corrupt decode.
        if (string.Equals(eventType, "terminal.output", StringComparison.Ordinal) ||
            string.Equals(eventType, "terminal.render", StringComparison.Ordinal))
            return payloadJson;

        // binding.changed, export.acked, checkpoint.lifecycle: identity / cursor fields only.
        // Full-text sk-/TOKEN=/password rules false-positive on ids and corrupt join metadata.
        if (string.Equals(eventType, "binding.changed", StringComparison.Ordinal) ||
            string.Equals(eventType, "export.acked", StringComparison.Ordinal) ||
            string.Equals(eventType, "checkpoint.lifecycle", StringComparison.Ordinal))
            return payloadJson;

        if (!TryParseJson(payloadJson, out var doc))
            return RedactText(payloadJson);

        using (doc)
        {
            return RewriteJsonStrings(doc.RootElement);
        }
    }

    public ReadOnlyMemory<byte> RedactClosedTerminalBytes(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
            return data;

        var first = RedactTerminalBytes(data);
        if (first.IsEmpty)
            return first;

        var text = Encoding.UTF8.GetString(first.Span);
        var closed = RedactIncompleteSecretPrefixes(text);
        if (closed == text)
            return first;
        return Encoding.UTF8.GetBytes(closed);
    }

    public ReadOnlyMemory<byte> PeekClosedTerminalBytes(string streamKey, ReadOnlyMemory<byte> data)
    {
        if (string.IsNullOrEmpty(streamKey))
            return RedactClosedTerminalBytes(data);

        bool hasState;
        bool suppressPem = false;
        bool suppressTerm = false;
        string hold = "";
        if (_carry.TryGetValue(streamKey, out var peekState) && peekState is not null)
        {
            peekState.Gate.Enter();
            try
            {
                if (peekState.Removed)
                {
                    hasState = false;
                }
                else
                {
                    hasState = true;
                    suppressPem = peekState.SuppressUntilPemEnd;
                    suppressTerm = peekState.SuppressUntilTerminator;
                    hold = peekState.Text ?? "";
                }
            }
            finally
            {
                peekState.Gate.Exit();
            }
        }
        else
        {
            hasState = false;
        }

        if (suppressPem || suppressTerm)
            return Encoding.UTF8.GetBytes(Replacement);

        if (hold.Length > 0)
            return RedactClosedTerminalBytes(Encoding.UTF8.GetBytes(hold));

        // Keyed output was empty and carry is gone: original may be a suppressed
        // tail with no BEGIN/sk- prefix. Do not post original.
        if (!hasState && !data.IsEmpty)
            return Encoding.UTF8.GetBytes(Replacement);

        return ReadOnlyMemory<byte>.Empty;
    }

    public ReadOnlyMemory<byte> RedactTerminalBytes(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
            return data;

        if (TrySplitCompleteUtf8(data.Span, out var complete, out var remainder))
        {
            if (complete.IsEmpty)
                return remainder.ToArray();

            var text = Encoding.UTF8.GetString(complete);
            var redacted = RedactText(text);
            if ((ReferenceEquals(redacted, text) || redacted == text) && remainder.IsEmpty)
                return data;

            return ConcatUtf8(redacted, remainder);
        }

        // Interior invalid UTF-8: redact ASCII runs in place. Do not re-encode
        // the whole span (replacement decode would rewrite binary PTY).
        return RedactAsciiRuns(data);
    }

    public ReadOnlyMemory<byte> RedactTerminalBytes(string streamKey, ReadOnlyMemory<byte> data)
    {
        if (string.IsNullOrEmpty(streamKey))
            return RedactTerminalBytes(data);
        if (data.IsEmpty)
            return data;

        if (_carry.TryGetValue(streamKey, out var existing) && existing is not null)
        {
            existing.Gate.Enter();
            try
            {
                if (!existing.Removed && !existing.IsIdle)
                    return RedactKeyedTerminalBytes(streamKey, existing, data);
            }
            finally
            {
                existing.Gate.Exit();
            }
        }

        var span = data.Span;
        if (!TerminalSecretAnchors.MayStartHold(span)
            && !TerminalSecretAnchors.ContainsAnchor(span))
            return data;

        var state = EnterCarry(streamKey);
        try
        {
            return RedactKeyedTerminalBytes(streamKey, state, data);
        }
        finally
        {
            state.Gate.Exit();
        }
    }

    public ReadOnlyMemory<byte> FlushTerminalStream(string streamKey)
    {
        if (string.IsNullOrEmpty(streamKey))
            return ReadOnlyMemory<byte>.Empty;

        if (!_carry.TryGetValue(streamKey, out var state) || state is null)
            return ReadOnlyMemory<byte>.Empty;

        state.Gate.Enter();
        try
        {
            if (state.Removed)
                return ReadOnlyMemory<byte>.Empty;
            return FlushCarryUnlocked(streamKey, state);
        }
        finally
        {
            state.Gate.Exit();
        }
    }

    /// <summary>
    /// Enter the per-stream gate. Tests use this to prove two keys do not share
    /// one lock. No static mutable state.
    /// </summary>
    internal IDisposable LockStream(string streamKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(streamKey);
        return new StreamGateHold(EnterCarry(streamKey));
    }

    private ReadOnlyMemory<byte> FlushCarryUnlocked(string streamKey, StreamCarry state)
    {
        state.Removed = true;
        _carry.TryRemove(KeyValuePair.Create(streamKey, state));

        state.SuppressUntilTerminator = false;
        state.SuppressUntilPemEnd = false;
        var leftover = state.Text ?? "";
        var redacted = leftover.Length == 0
            ? ""
            : RedactIncompleteSecretPrefixes(RedactText(leftover));
        var tail = state.Utf8Tail;
        if (string.IsNullOrEmpty(redacted) && (tail is null || tail.Length == 0))
            return ReadOnlyMemory<byte>.Empty;

        return ConcatUtf8(redacted, tail);
    }

    private StreamCarry EnterCarry(string streamKey)
    {
        while (true)
        {
            var state = _carry.GetOrAdd(streamKey, static _ => new StreamCarry());
            state.Gate.Enter();
            if (!state.Removed)
                return state;
            state.Gate.Exit();
        }
    }

    private void DropIfIdle(string streamKey, StreamCarry state)
    {
        if (!state.IsIdle)
            return;

        state.Removed = true;
        _carry.TryRemove(KeyValuePair.Create(streamKey, state));
    }

    private sealed class StreamGateHold : IDisposable
    {
        private StreamCarry? _state;

        public StreamGateHold(StreamCarry state) => _state = state;

        public void Dispose()
        {
            var state = Interlocked.Exchange(ref _state, null);
            state?.Gate.Exit();
        }
    }

    private static bool TrySealOversizedOpenPem(string text, out string sealedText)
    {
        sealedText = text;
        var pemStart = text.LastIndexOf("-----BEGIN", StringComparison.Ordinal);
        if (pemStart < 0)
            return false;
        if (text.IndexOf("-----END", pemStart, StringComparison.Ordinal) >= 0)
            return false;
        if (text.Length - pemStart <= PemCarryMaxChars)
            return false;

        sealedText = text[..pemStart] + RedactIncompleteSecretPrefixes(text[pemStart..]);
        return true;
    }

    private ReadOnlyMemory<byte> RedactKeyedTerminalBytes(
        string streamKey, StreamCarry state, ReadOnlyMemory<byte> data)
    {
        var incoming = PrependUtf8Tail(state, data);

        string chunk;
        if (TrySplitCompleteUtf8(incoming.Span, out var complete, out var remainder))
        {
            state.Utf8Tail = remainder.IsEmpty ? [] : remainder.ToArray();
            if (complete.IsEmpty)
            {
                DropIfIdle(streamKey, state);
                return ReadOnlyMemory<byte>.Empty;
            }

            chunk = Encoding.UTF8.GetString(complete);
        }
        else
        {
            // Interior invalid UTF-8 is a terminator only after the held
            // assignment/token tail is merged into the leading ASCII run.
            var mixed = RedactInvalidUtf8WithHold(state, incoming);
            DropIfIdle(streamKey, state);
            return mixed;
        }

        if (state.SuppressUntilPemEnd)
        {
            var end = chunk.IndexOf("-----END", StringComparison.Ordinal);
            if (end < 0)
            {
                DropIfIdle(streamKey, state);
                return ReadOnlyMemory<byte>.Empty;
            }

            state.SuppressUntilPemEnd = false;
            var nl = chunk.IndexOf('\n', end);
            chunk = nl >= 0 ? chunk[(nl + 1)..] : "";
            if (chunk.Length == 0)
            {
                DropIfIdle(streamKey, state);
                return ReadOnlyMemory<byte>.Empty;
            }
        }

        if (state.SuppressUntilTerminator)
        {
            var term = IndexOfWhitespace(chunk);
            if (term < 0)
            {
                DropIfIdle(streamKey, state);
                return ReadOnlyMemory<byte>.Empty;
            }

            state.SuppressUntilTerminator = false;
            chunk = chunk[(term + 1)..];
            if (chunk.Length == 0)
            {
                DropIfIdle(streamKey, state);
                return ReadOnlyMemory<byte>.Empty;
            }
        }

        var combined = state.Text.Length == 0 ? chunk : state.Text + chunk;
        if (TrySealOversizedOpenPem(combined, out var sealedWindow))
        {
            state.Text = "";
            state.SuppressUntilPemEnd = true;
            DropIfIdle(streamKey, state);
            return Encoding.UTF8.GetBytes(
                RedactIncompleteSecretPrefixes(RedactText(sealedWindow)));
        }

        var cut = FindHoldStart(combined);
        var emitRaw = combined[..cut];
        var holdRaw = combined[cut..];

        if (holdRaw.Length > MaxHoldChars)
        {
            emitRaw = combined;
            holdRaw = "";
            state.SuppressUntilTerminator = !EndsWithWhitespace(combined);
        }

        state.Text = holdRaw;
        DropIfIdle(streamKey, state);

        if (string.IsNullOrEmpty(emitRaw))
            return ReadOnlyMemory<byte>.Empty;

        return Encoding.UTF8.GetBytes(RedactText(emitRaw));
    }

    private static ReadOnlyMemory<byte> PrependUtf8Tail(
        StreamCarry state, ReadOnlyMemory<byte> data)
    {
        if (state.Utf8Tail.Length == 0)
            return data;

        var merged = new byte[state.Utf8Tail.Length + data.Length];
        state.Utf8Tail.CopyTo(merged, 0);
        data.Span.CopyTo(merged.AsSpan(state.Utf8Tail.Length));
        state.Utf8Tail = [];
        return merged;
    }

    /// <summary>Apply the default rule pack to arbitrary text.</summary>
    public static string RedactText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = text;

        // Authorization: Bearer <token> before KEY=value. Assignment takes one
        // word, so AUTHORIZATION: Bearer <jwt> would otherwise leave the jwt.
        result = AuthorizationHeader().Replace(result, m =>
            m.Groups["key"].Value + m.Groups["sep"].Value + "Bearer " + Replacement);

        result = BearerToken().Replace(result, "Bearer " + Replacement);

        // KEY=value and "KEY":"value" for secret-shaped keys.
        result = EnvAssignment().Replace(result, m =>
            m.Groups["key"].Value + m.Groups["sep"].Value + QuoteIf(m) + Replacement + QuoteIfEnd(m));

        result = JsonSecretField().Replace(result, m =>
            m.Groups["prefix"].Value + Replacement + m.Groups["suffix"].Value);

        // OpenAI / generic sk- keys
        result = SkToken().Replace(result, Replacement);

        // GitHub tokens
        result = GhToken().Replace(result, Replacement);

        // AWS access key id
        result = AwsAccessKey().Replace(result, Replacement);

        // AWS secret-looking 40-char base64-ish after aws_secret_access_key
        result = AwsSecretPair().Replace(result, m =>
            m.Groups["prefix"].Value + Replacement);

        // PEM blocks
        result = PemBlock().Replace(result, "-----BEGIN REDACTED-----\n" + Replacement + "\n-----END REDACTED-----");

        // Password / token / secret inline assignments without key shape already caught
        result = PasswordInline().Replace(result, m =>
            m.Groups["label"].Value + m.Groups["sep"].Value + Replacement);

        return result;
    }

    internal static string RedactIncompleteSecretPrefixes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = IncompletePem().Replace(
            text, "-----BEGIN REDACTED-----\n" + Replacement + "\n-----END REDACTED-----");
        result = IncompleteSk().Replace(result, Replacement);
        result = IncompleteGh().Replace(result, Replacement);
        result = IncompleteAkia().Replace(result, Replacement);
        result = IncompleteBearer().Replace(result, "Bearer " + Replacement);
        return result;
    }

    /// <summary>
    /// Decide emit vs hold on the unredacted window. Variable-length tokens and
    /// KEY=/password= values that touch EOS stay in carry until whitespace or flush.
    /// </summary>
    private static int FindHoldStart(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var hold = text.Length;

        var pemStart = text.LastIndexOf("-----BEGIN", StringComparison.Ordinal);
        if (pemStart >= 0 && text.IndexOf("-----END", pemStart, StringComparison.Ordinal) < 0)
            hold = Math.Min(hold, pemStart);

        var assignment = IncompleteAssignmentAtEnd().Match(text);
        if (assignment.Success)
            hold = Math.Min(hold, assignment.Index);

        var token = IncompleteSecretAtEnd().Match(text);
        if (token.Success)
            hold = Math.Min(hold, token.Index);

        var prefix = TrailingPrefixFragmentLength(text);
        if (prefix > 0)
            hold = Math.Min(hold, text.Length - Math.Min(prefix, CarryChars));

        return hold;
    }

    private static int LongestSuffixOfPrefix(string text, string token, StringComparison comparison) =>
        LongestSuffixOfPrefix(text.AsSpan(), token.AsSpan(), comparison);

    internal static bool TailWindowStartsHold(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return false;

        foreach (var token in ExactPrefixTokens)
        {
            if (LongestSuffixOfPrefix(text, token, StringComparison.Ordinal) > 0)
                return true;
        }

        foreach (var token in IgnoreCasePrefixTokens)
        {
            if (LongestSuffixOfPrefix(text, token, StringComparison.OrdinalIgnoreCase) > 0)
                return true;
        }

        if (LongestSuffixOfPrefix(text, "Bearer ", StringComparison.OrdinalIgnoreCase) > 0)
            return true;
        if (LongestSuffixOfPrefix(text, "Bearer", StringComparison.OrdinalIgnoreCase) > 0)
            return true;

        return IncompleteKeyPrefixAtEnd().IsMatch(text);
    }

    private static int LongestSuffixOfPrefix(
        ReadOnlySpan<char> text, ReadOnlySpan<char> token, StringComparison comparison)
    {
        var max = Math.Min(text.Length, token.Length);
        for (var n = max; n > 0; n--)
        {
            if (!text[(text.Length - n)..].Equals(token[..n], comparison))
                continue;
            if (n < text.Length && char.IsLetterOrDigit(text[text.Length - n - 1]))
                continue;
            return n;
        }

        return 0;
    }

    private static readonly string[] ExactPrefixTokens =
    [
        "-----BEGIN",
        "sk-",
        "AKIA",
        "ghp_",
        "gho_",
        "ghu_",
        "ghs_",
        "ghr_",
    ];

    private static readonly string[] IgnoreCasePrefixTokens =
    [
        "PASSWORD",
        "AUTHORIZATION",
        "OPENAI_API_KEY",
        "ANTHROPIC_API_KEY",
        "GITHUB_TOKEN",
        "GH_TOKEN",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_ACCESS_KEY_ID",
        "API_TOKEN",
        "API_KEY",
        "ACCESS_TOKEN",
        "SECRET_KEY",
        "CLIENT_SECRET",
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "aws_secret_access_key",
        "export ",
        "set ",
    ];

    private static int TrailingPrefixFragmentLength(string text)
    {
        var max = 0;
        foreach (var token in ExactPrefixTokens)
        {
            if (token == "-----BEGIN" && TrailingDashesAreClosedPemEnd(text))
                continue;
            var n = LongestSuffixOfPrefix(text, token, StringComparison.Ordinal);
            if (n > max)
                max = n;
        }

        foreach (var token in IgnoreCasePrefixTokens)
        {
            var n = LongestSuffixOfPrefix(text, token, StringComparison.OrdinalIgnoreCase);
            if (n > max)
                max = n;
        }

        var bearer = LongestSuffixOfPrefix(text, "Bearer ", StringComparison.OrdinalIgnoreCase);
        if (bearer > max)
            max = bearer;
        bearer = LongestSuffixOfPrefix(text, "Bearer", StringComparison.OrdinalIgnoreCase);
        if (bearer > max)
            max = bearer;

        var generic = IncompleteKeyPrefixAtEnd().Match(text);
        if (generic.Success && generic.Length > max)
            max = generic.Length;

        return max;
    }

    private static bool TrySplitCompleteUtf8(
        ReadOnlySpan<byte> data,
        out ReadOnlySpan<byte> complete,
        out ReadOnlySpan<byte> remainder)
    {
        complete = data;
        remainder = default;
        if (data.IsEmpty || Utf8.IsValid(data))
            return true;

        var maxBack = Math.Min(3, data.Length);
        for (var n = 1; n <= maxBack; n++)
        {
            var prefix = data[..^n];
            var tail = data[^n..];
            if (!IsIncompleteUtf8Sequence(tail))
                continue;
            if (prefix.Length > 0 && !Utf8.IsValid(prefix))
                continue;

            complete = prefix;
            remainder = tail;
            return true;
        }

        return false;
    }

    private static bool IsIncompleteUtf8Sequence(ReadOnlySpan<byte> tail)
    {
        if (tail.IsEmpty || tail.Length > 3)
            return false;

        var lead = tail[0];
        int expected;
        if ((lead & 0b1110_0000) == 0b1100_0000)
            expected = 2;
        else if ((lead & 0b1111_0000) == 0b1110_0000)
            expected = 3;
        else if ((lead & 0b1111_1000) == 0b1111_0000)
            expected = 4;
        else
            return false;

        if (tail.Length >= expected)
            return false;

        for (var i = 1; i < tail.Length; i++)
        {
            if ((tail[i] & 0b1100_0000) != 0b1000_0000)
                return false;
        }

        return true;
    }

    private static ReadOnlyMemory<byte> ConcatUtf8(string text, ReadOnlySpan<byte> tail)
    {
        if (string.IsNullOrEmpty(text) && tail.IsEmpty)
            return ReadOnlyMemory<byte>.Empty;

        if (string.IsNullOrEmpty(text))
            return tail.ToArray();

        var encoded = Encoding.UTF8.GetBytes(text);
        if (tail.IsEmpty)
            return encoded;

        var merged = new byte[encoded.Length + tail.Length];
        encoded.CopyTo(merged, 0);
        tail.CopyTo(merged.AsSpan(encoded.Length));
        return merged;
    }

    private static ReadOnlyMemory<byte> ConcatUtf8(string text, byte[]? tail) =>
        ConcatUtf8(text, tail is null ? ReadOnlySpan<byte>.Empty : tail);

    private static int IndexOfWhitespace(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
                return i;
        }

        return -1;
    }

    private static bool EndsWithWhitespace(string text) =>
        text.Length > 0 && char.IsWhiteSpace(text[^1]);

    /// <summary>
    /// Redact secret-shaped ASCII runs inside a mixed/invalid UTF-8 span.
    /// High bytes stay as-is so non-text PTY is not rewritten.
    /// </summary>
    private static ReadOnlyMemory<byte> RedactAsciiRuns(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        List<(int Start, int Length, byte[] Replacement)>? edits = null;
        var i = 0;
        while (i < span.Length)
        {
            if (span[i] >= 0x80)
            {
                i++;
                continue;
            }

            var start = i;
            while (i < span.Length && span[i] < 0x80)
                i++;

            var run = span[start..i];
            var text = Encoding.ASCII.GetString(run);
            var redacted = RedactText(text);
            if (redacted == text)
                continue;

            edits ??= [];
            edits.Add((start, i - start, Encoding.ASCII.GetBytes(redacted)));
        }

        if (edits is null)
            return data;

        var extra = 0;
        foreach (var edit in edits)
            extra += edit.Replacement.Length - edit.Length;

        var result = new byte[span.Length + extra];
        var src = 0;
        var dst = 0;
        foreach (var edit in edits)
        {
            var before = edit.Start - src;
            if (before > 0)
            {
                span.Slice(src, before).CopyTo(result.AsSpan(dst));
                dst += before;
            }

            edit.Replacement.CopyTo(result.AsSpan(dst));
            dst += edit.Replacement.Length;
            src = edit.Start + edit.Length;
        }

        if (src < span.Length)
            span[src..].CopyTo(result.AsSpan(dst));

        return result;
    }

    /// <summary>
    /// True when a trailing <c>-----</c> is the closer of a completed
    /// <c>-----END …-----</c> line and no extra dashes follow that line.
    /// Extra dashes after the closer are a following <c>-----BEGIN</c> prefix.
    /// </summary>
    private static bool TrailingDashesAreClosedPemEnd(string text)
    {
        var end = text.LastIndexOf("-----END", StringComparison.Ordinal);
        if (end < 0)
            return false;

        var i = end + "-----END".Length;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == ' ' || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
            {
                i++;
                continue;
            }

            break;
        }

        if (i + 5 > text.Length || !text.AsSpan(i, 5).SequenceEqual("-----"))
            return false;

        var rest = text.AsSpan(i + 5);
        for (var j = 0; j < rest.Length; j++)
        {
            if (rest[j] == '-')
                return false;
            if (!char.IsWhiteSpace(rest[j]))
                return false;
        }

        return true;
    }

    private static ReadOnlyMemory<byte> RedactInvalidUtf8WithHold(
        StreamCarry state, ReadOnlyMemory<byte> incoming)
    {
        state.Utf8Tail = [];
        var span = incoming.Span;
        var hold = state.Text ?? "";
        state.Text = "";

        using var output = new MemoryStream(hold.Length + incoming.Length + 16);
        var holdPending = hold.Length > 0;
        var i = 0;
        while (i < span.Length)
        {
            if (span[i] >= 0x80)
            {
                if (holdPending)
                {
                    WriteUtf8(output, RedactIncompleteSecretPrefixes(RedactText(hold)));
                    holdPending = false;
                }

                output.WriteByte(span[i]);
                i++;
                continue;
            }

            var start = i;
            while (i < span.Length && span[i] < 0x80)
                i++;

            var run = Encoding.ASCII.GetString(span[start..i]);
            if (holdPending)
            {
                run = hold + run;
                holdPending = false;
            }

            if (i < span.Length)
            {
                WriteUtf8(output, RedactIncompleteSecretPrefixes(RedactText(run)));
                continue;
            }

            var cut = FindHoldStart(run);
            WriteUtf8(output, RedactText(run[..cut]));
            state.Text = run[cut..];
        }

        if (holdPending)
            WriteUtf8(output, RedactIncompleteSecretPrefixes(RedactText(hold)));

        return output.ToArray();
    }

    private static void WriteUtf8(MemoryStream output, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var bytes = Encoding.UTF8.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
    }

    private static bool TryParseJson(string payloadJson, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(payloadJson);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static string RewriteJsonStrings(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        }))
        {
            WriteRedactedElement(writer, root);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRedactedElement(
        Utf8JsonWriter writer, JsonElement element, string? propertyName = null)
    {
        if (propertyName is not null && IsSecretPropertyName(propertyName))
        {
            writer.WriteStringValue(Replacement);
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    WriteRedactedElement(writer, prop.Value, prop.Name);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRedactedElement(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(RedactText(element.GetString() ?? string.Empty));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    internal static bool IsSecretPropertyName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        if (name.Equals("password", StringComparison.OrdinalIgnoreCase)
            || name.Equals("passwd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pwd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("secret", StringComparison.OrdinalIgnoreCase)
            || name.Equals("token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("api_key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("access_token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("secret_key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("client_secret", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteIf(Match m) =>
        m.Groups["q"].Success ? m.Groups["q"].Value : string.Empty;

    private static string QuoteIfEnd(Match m) =>
        m.Groups["q"].Success ? m.Groups["q"].Value : string.Empty;

    // Derived anchors for TerminalSecretAnchors. A rule cannot fire if none of
    // its substrings are present. Case-insensitive means ASCII case folding.
    //
    // AuthorizationHeader  authorization (ci)
    // EnvAssignment        _token, _key, _secret, password (ci)
    // JsonSecretField      _token, _key, _secret, password, authorization (ci)
    // BearerToken          bearer (ci)
    // SkToken              sk- (cs)
    // GhToken              ghp_ gho_ ghu_ ghs_ ghr_ (cs)
    // AwsAccessKey         AKIA (cs)
    // AwsSecretPair        secret (ci)
    // PemBlock             -----BEGIN (cs)
    // PasswordInline       password, passwd, pwd, secret, token (ci)
    // IncompletePem        -----BEGIN (cs)
    // IncompleteSk         sk- (cs)
    // IncompleteGh         gh[pousr]_ (cs)
    // IncompleteAkia       AKIA (cs)
    // IncompleteBearer     bearer (ci)
    // IncompleteSecretAtEnd            sk-, gh[pousr]_, AKIA (cs), bearer (ci)
    // IncompleteAssignmentAtEnd        authorization, password, passwd, pwd,
    //                                  secret, token, _key (ci)
    // IncompleteKeyPrefixAtEnd         tail guard only (no whole-chunk anchor)
    //
    // Whole-chunk set: sk-, AKIA, gh[pousr]_, -----BEGIN (cs);
    // authorization, bearer, password, passwd, pwd, secret, token, _key (ci).
    // _token sits inside token. _secret sits inside secret.
    // '=' and '"' are not lead bytes: no rule fires on them alone.
    // The fixture list in RedactionRuleFixtures keeps this table honest.

    // Authorization: Bearer <token> / AUTHORIZATION=Bearer <token> / AUTHORIZATION=<token>
    [GeneratedRegex(
        @"(?i)(?<key>\bAuthorization)\b(?<sep>\s*[=:]\s*)(?:Bearer\s+)?(?<val>[^\s'""&,;]+)",
        RegexOptions.Compiled)]
    private static partial Regex AuthorizationHeader();

    // API_KEY=secret or API_KEY="secret" or API_TOKEN: secret
    [GeneratedRegex(
        @"(?i)(?<key>\b(?:[A-Z0-9_]+_(?:TOKEN|KEY|SECRET)|PASSWORD|AWS_SECRET_ACCESS_KEY|AWS_ACCESS_KEY_ID|OPENAI_API_KEY|ANTHROPIC_API_KEY|GITHUB_TOKEN|GH_TOKEN))\b(?<sep>\s*[=:]\s*)(?<q>['""])?(?<val>[^\s'""&,;]+)(\k<q>)?",
        RegexOptions.Compiled)]
    private static partial Regex EnvAssignment();

    // JSON "api_key":"value" / "password": "value"
    [GeneratedRegex(
        @"(?i)(?<prefix>""(?:[A-Za-z0-9_]+_(?:TOKEN|KEY|SECRET)|password|api_key|access_token|secret_key|client_secret|authorization)""\s*:\s*"")(?<val>[^""]*)(?<suffix>"")",
        RegexOptions.Compiled)]
    private static partial Regex JsonSecretField();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.Compiled)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9]{20,}\b", RegexOptions.Compiled)]
    private static partial Regex SkToken();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", RegexOptions.Compiled)]
    private static partial Regex GhToken();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(
        @"(?i)(?<prefix>aws_secret_access_key\s*[=:]\s*)([A-Za-z0-9/+=]{30,})",
        RegexOptions.Compiled)]
    private static partial Regex AwsSecretPair();

    [GeneratedRegex(
        @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----",
        RegexOptions.Compiled)]
    private static partial Regex PemBlock();

    [GeneratedRegex(
        @"(?i)(?<label>\b(?:password|passwd|pwd|secret|token)\b)(?<sep>\s*[=:]\s*)(?<val>\S+)",
        RegexOptions.Compiled)]
    private static partial Regex PasswordInline();

    [GeneratedRegex(@"-----BEGIN(?! REDACTED)[\s\S]*", RegexOptions.Compiled)]
    private static partial Regex IncompletePem();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9]*", RegexOptions.Compiled)]
    private static partial Regex IncompleteSk();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]*", RegexOptions.Compiled)]
    private static partial Regex IncompleteGh();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]*", RegexOptions.Compiled)]
    private static partial Regex IncompleteAkia();

    [GeneratedRegex(@"(?i)\bBearer(?:\s+[A-Za-z0-9\-._~+/=]*)?", RegexOptions.Compiled)]
    private static partial Regex IncompleteBearer();

    [GeneratedRegex(
        @"(?:\bsk-[A-Za-z0-9]*|\bgh[pousr]_[A-Za-z0-9]*|\bAKIA[0-9A-Z]*|(?i)\bBearer(?:\s+[A-Za-z0-9\-._~+/=]*)?)\z",
        RegexOptions.Compiled)]
    private static partial Regex IncompleteSecretAtEnd();

    // KEY= / password= / "api_key":"value tails that have not seen whitespace (or a close quote).
    // \z (not $): a trailing newline is a terminator, not an incomplete-at-EOS match.
    [GeneratedRegex(
        @"(?i)(?:\bAuthorization\b\s*[=:]\s*(?:Bearer\s+)?['""]?[^\s'""&,;]*|\b(?:[A-Z0-9_]+_(?:TOKEN|KEY|SECRET)|PASSWORD|AWS_SECRET_ACCESS_KEY|AWS_ACCESS_KEY_ID|OPENAI_API_KEY|ANTHROPIC_API_KEY|GITHUB_TOKEN|GH_TOKEN|password|passwd|pwd|secret|token)\b\s*[=:]\s*['""]?[^\s'""&,;]*|(?:aws_secret_access_key\s*[=:]\s*[A-Za-z0-9/+=]*)|""(?:[A-Za-z0-9_]+_(?:TOKEN|KEY|SECRET)|password|api_key|access_token|secret_key|client_secret|authorization)""\s*:\s*""[^""]*)\z",
        RegexOptions.Compiled)]
    private static partial Regex IncompleteAssignmentAtEnd();

    [GeneratedRegex(
        @"(?i)(?:\b(?:export|set)\s+)?[A-Za-z_][A-Za-z0-9_]*_(?:T(?:O(?:K(?:E(?:N)?)?)?)?|K(?:E(?:Y)?)?|S(?:E(?:C(?:R(?:E(?:T)?)?)?)?)?)?\z",
        RegexOptions.Compiled)]
    private static partial Regex IncompleteKeyPrefixAtEnd();
}
