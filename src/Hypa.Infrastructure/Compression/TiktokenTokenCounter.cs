using Hypa.Runtime.Application.Ports;
using Microsoft.ML.Tokenizers;

namespace Hypa.Infrastructure.Compression;

public sealed class TiktokenTokenCounter : ITokenCounter
{
    private const string EncodingName = "o200k_base";

    private readonly TiktokenTokenizer? _tokenizer;
    private readonly ITokenCounter _fallback = new CharDivTokenCounter();

    public TiktokenTokenCounter()
    {
        try
        {
            _tokenizer = TiktokenTokenizer.CreateForEncoding(EncodingName, null, null);
        }
        catch
        {
            _tokenizer = null;
        }
    }

    public int EstimateTokens(string text)
    {
        if (_tokenizer is null)
            return _fallback.EstimateTokens(text);

        try
        {
            return Math.Max(1, _tokenizer.CountTokens(text, considerPreTokenization: true, considerNormalization: true));
        }
        catch
        {
            return _fallback.EstimateTokens(text);
        }
    }
}
