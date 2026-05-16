namespace Hypa.Runtime.Application.Ports;

public interface IVersionProvider
{
    string CurrentVersion { get; }
}
