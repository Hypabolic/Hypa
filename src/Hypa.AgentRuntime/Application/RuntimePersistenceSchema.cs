namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Normative SQLite schema version for the agent runtime session graph (design Appendix A).
/// Separate from compression-product <c>hypa.db</c> schema_metadata.
/// </summary>
public static class RuntimePersistenceSchema
{
    public const int Version = 1;

    public const string SchemaVersionKey = "schema_version";
    public const string RuntimeSessionIdKey = "runtime_session_id";
    public const string EventSeqKey = "event_seq";
}
