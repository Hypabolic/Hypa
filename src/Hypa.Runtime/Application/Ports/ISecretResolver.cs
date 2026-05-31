namespace Hypa.Runtime.Application.Ports;

public interface ISecretResolver
{
    ValueTask<string?> ResolveAsync(string reference, CancellationToken ct);
}
