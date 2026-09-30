namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Application Strategy — no Terminal dependency.
/// </summary>
public interface IPaneKeyComboEncoder
{
    /// <summary>
    /// Encode <paramref name="keys"/> to VT bytes. Rejects <c>prefix+</c> and unknown tokens.
    /// </summary>
    bool TryEncode(IReadOnlyList<string> keys, out byte[] bytes, out string? error);
}
