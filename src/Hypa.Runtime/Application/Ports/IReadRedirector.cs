namespace Hypa.Runtime.Application.Ports;

public interface IReadRedirector
{
    Task<string?> RedirectAsync(string path, CancellationToken ct = default);
}
