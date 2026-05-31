namespace Hypa.Runtime.Application.Ports;

public interface IGitFileStateProvider
{
    Task<IReadOnlyDictionary<string, string>?> GetCleanBlobOidsAsync(
        string projectRoot, CancellationToken ct);

    Task<string?> GetCleanBlobOidAsync(
        string absolutePath, string projectRoot, CancellationToken ct);
}
