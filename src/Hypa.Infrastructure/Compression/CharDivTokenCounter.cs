using Hypa.Runtime.Application.Ports;

namespace Hypa.Infrastructure.Compression;

public sealed class CharDivTokenCounter : ITokenCounter
{
    public int EstimateTokens(string text) => Math.Max(1, text.Length / 4);
}
