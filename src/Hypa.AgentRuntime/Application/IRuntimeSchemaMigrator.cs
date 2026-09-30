namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Applies normative runtime SQLite DDL and stamps <c>runtime_meta.schema_version</c>.
/// </summary>
public interface IRuntimeSchemaMigrator
{
    /// <summary>
    /// Open/create the database under the state root, apply v1 DDL if needed,
    /// seed <c>runtime_session_id</c> once, and fail closed on future schema versions.
    /// </summary>
    Task<RuntimeResult<RuntimeUnit>> MigrateAsync(CancellationToken ct = default);
}
