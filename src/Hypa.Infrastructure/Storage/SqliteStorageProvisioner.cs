using Hypa.Runtime.Application.Ports;
using Hypa.Runtime.Domain.Common;

namespace Hypa.Infrastructure.Storage;

public sealed class SqliteStorageProvisioner(
    HypaDataOptions options,
    SqliteSchemaInitializer schemaInitializer) : IStorageProvisioner
{
    public async Task<Result<Unit, Error>> ProvisionAsync(CancellationToken ct)
    {
        var schemaResult = await schemaInitializer.InitAsync(ct);
        if (!schemaResult.IsOk)
            return schemaResult;

        try
        {
            Directory.CreateDirectory(options.ArtifactsDirectory);
        }
        catch (IOException ex)
        {
            return Result<Unit, Error>.Fail(new Error("storage.io_error", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<Unit, Error>.Fail(new Error("storage.access_denied", ex.Message));
        }

        return Result<Unit, Error>.Ok(Unit.Value);
    }
}
