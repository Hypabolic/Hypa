using Hypa.AgentRuntime.Domain;

namespace Hypa.AgentRuntime.Application;

/// <summary>
/// Expected persistence failure. Maps to protocol <c>persistence_unavailable</c> (-32011).
/// </summary>
public sealed record RuntimePersistenceError(string Code, string Message)
{
    public const string FutureSchema = "persistence.future_schema";
    public const string DbError = "persistence.db_error";
    public const string IoError = "persistence.io_error";
    public const string AccessDenied = "persistence.access_denied";
    public const string ConflictError = "persistence.conflict";
    public const string BudgetRefused = "persistence.budget_refused";
    public const string CursorExpiredCode = "cursor_expired";

    /// <summary>
    /// Live record for a reliable append that did not stay in HYJR.
    /// The sequence is consumed. A later append must not reuse it.
    /// </summary>
    public RuntimeEventRecord? LiveRecord { get; init; }

    /// <summary>Retention floor when <see cref="Code"/> is <see cref="CursorExpiredCode"/>.</summary>
    public long? FloorSeq { get; init; }

    public static RuntimePersistenceError Future(int detected, int supported) =>
        new(FutureSchema,
            $"Runtime schema version {detected} is newer than this binary supports ({supported}).");

    public static RuntimePersistenceError Db(string message) => new(DbError, message);

    public static RuntimePersistenceError Io(string message) => new(IoError, message);

    public static RuntimePersistenceError Access(string message) => new(AccessDenied, message);

    public static RuntimePersistenceError Conflict(string message) => new(ConflictError, message);

    public static RuntimePersistenceError Budget(string message, RuntimeEventRecord live) =>
        new(BudgetRefused, message) { LiveRecord = live };

    public static RuntimePersistenceError CursorExpired(long floorSeq) =>
        new(CursorExpiredCode, "cursor expired") { FloorSeq = floorSeq };
}
