using Hypa.Runtime.Application.Ports;
using Microsoft.ML.Tokenizers;

namespace Hypa.Infrastructure.Compression;

/// <summary>
/// Counts tokens with the o200k_base vocabulary. The vocabulary is about
/// 40 MB of heap, so the counter builds it on the first count. A command
/// that never counts tokens, such as the accept bridge, does not build it.
/// </summary>
public sealed class TiktokenTokenCounter : ITokenCounter
{
    private const string EncodingName = "o200k_base";

    private readonly Lazy<TiktokenTokenizer?> _tokenizer = new(CreateTokenizer, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly ITokenCounter _fallback = new CharDivTokenCounter();

    /// <summary>True when the vocabulary is built. False before the first count and when it failed to load.</summary>
    public bool IsLoaded => _tokenizer.IsValueCreated && _tokenizer.Value is not null;

    public int EstimateTokens(string text)
    {
        var tokenizer = _tokenizer.Value;
        if (tokenizer is null)
            return _fallback.EstimateTokens(text);

        try
        {
            return Math.Max(1, tokenizer.CountTokens(text, considerPreTokenization: true, considerNormalization: true));
        }
        catch
        {
            return _fallback.EstimateTokens(text);
        }
    }

    private static TiktokenTokenizer? CreateTokenizer()
    {
        try
        {
            return TiktokenTokenizer.CreateForEncoding(EncodingName, null, null);
        }
        catch
        {
            return null;
        }
    }
}
