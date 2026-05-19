using Hypa.Runtime.Domain.Common;
using Microsoft.Data.Sqlite;

namespace Hypa.Infrastructure.Storage;

internal static class StorageFailure
{
    public static bool IsExpected(Exception ex) =>
        ex is SqliteException or IOException or UnauthorizedAccessException;

    public static Error ToError(Exception ex) => ex switch
    {
        SqliteException => new Error("storage.db_error", ex.Message),
        IOException => new Error("storage.io_error", ex.Message),
        UnauthorizedAccessException => new Error("storage.access_denied", ex.Message),
        _ => new Error("storage.error", ex.Message),
    };

    public static Error ToError(Error error)
    {
        var code = error.Code.StartsWith("schema.", StringComparison.Ordinal)
            ? "storage." + error.Code["schema.".Length..]
            : error.Code;
        return new Error(code, error.Message);
    }
}
