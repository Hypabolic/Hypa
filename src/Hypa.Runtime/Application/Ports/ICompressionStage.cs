namespace Hypa.Runtime.Application.Ports;

public interface ICompressionStage
{
    string Id { get; }
    string Apply(string text);
}
