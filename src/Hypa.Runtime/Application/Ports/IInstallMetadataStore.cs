using Hypa.Runtime.Domain.Updates;

namespace Hypa.Runtime.Application.Ports;

public interface IInstallMetadataStore
{
    Task<InstallMetadata> GetAsync(CancellationToken ct);
    Task SaveAsync(InstallMetadata metadata, CancellationToken ct);
}
