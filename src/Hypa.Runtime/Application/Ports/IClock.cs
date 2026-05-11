namespace Hypa.Runtime.Application.Ports;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
