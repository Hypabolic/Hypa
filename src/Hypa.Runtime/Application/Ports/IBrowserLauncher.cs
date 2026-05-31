namespace Hypa.Runtime.Application.Ports;

public interface IBrowserLauncher
{
    bool TryOpen(string url);
}
