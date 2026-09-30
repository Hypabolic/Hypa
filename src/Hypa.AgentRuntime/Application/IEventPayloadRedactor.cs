namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Redacts secrets from journal payloads before durable write and live fanout.
/// Live path and durable path share the same redaction decision (design §12.4).
/// Default pack cannot be disabled.
/// </summary>
public interface IEventPayloadRedactor
{
    /// <summary>Redact a JSON payload string for the given event type.</summary>
    string RedactJsonPayload(string eventType, string payloadJson);

    /// <summary>
    /// Stateless redact of raw terminal bytes. Used by <c>agent.read</c> on already-joined VT text.
    /// Incomplete UTF-8 at the tail is not a redaction exemption: the valid prefix is redacted.
    /// </summary>
    ReadOnlyMemory<byte> RedactTerminalBytes(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Stateless redact including incomplete secret prefixes. Does not mutate keyed carry.
    /// Live snapshot failure uses this so a hold chunk can still paint without secrets.
    /// </summary>
    ReadOnlyMemory<byte> RedactClosedTerminalBytes(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Non-mutating closed redact of the current keyed hold for <paramref name="streamKey"/>.
    /// Does not call <see cref="FlushTerminalStream"/>. A PEM or terminator continuation
    /// that keyed redact suppressed returns Replacement, not the original tail.
    /// </summary>
    ReadOnlyMemory<byte> PeekClosedTerminalBytes(string streamKey, ReadOnlyMemory<byte> data);

    /// <summary>
    /// Stream-keyed redact. Holds unredacted assignment, token, and PEM tails.
    /// Holds a 1–3 byte UTF-8 remainder so a split secret is not emitted.
    /// The returned memory may alias <paramref name="data"/>.
    /// Do not keep the result after this call returns.
    /// Do not write the input while the result is live.
    /// </summary>
    ReadOnlyMemory<byte> RedactTerminalBytes(string streamKey, ReadOnlyMemory<byte> data);

    /// <summary>
    /// Emit leftover carry for <paramref name="streamKey"/> and drop the key.
    /// Incomplete secret prefixes and assignment tails are treated as secrets.
    /// </summary>
    ReadOnlyMemory<byte> FlushTerminalStream(string streamKey);
}
