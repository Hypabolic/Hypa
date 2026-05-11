namespace Hypa.Runtime.Application.Ports;

public interface ITokenCounter
{
    int EstimateTokens(string text);
}
