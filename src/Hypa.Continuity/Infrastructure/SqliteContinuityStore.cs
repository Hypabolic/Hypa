using Hypa.Continuity.Application;
using Hypa.Continuity.Domain;
using Microsoft.Data.Sqlite;

namespace Hypa.Continuity.Infrastructure;

/// <summary>
/// Continuity SQLite store for Work / Run generation fence (C-01).
/// Not mux HYJR.
/// </summary>
public sealed class SqliteContinuityStore : IContinuityStore, IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _created;

    public SqliteContinuityStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _dbPath = Path.Combine(Path.GetFullPath(directory), "continuity.db");
    }

    public async ValueTask EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        if (_created)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_created)
                return;

            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS works (
                  work_id TEXT PRIMARY KEY NOT NULL,
                  harness_adapter_id TEXT NOT NULL,
                  created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS runs (
                  work_id TEXT NOT NULL,
                  generation INTEGER NOT NULL,
                  placement_id TEXT NOT NULL,
                  mux_endpoint TEXT NOT NULL,
                  status TEXT NOT NULL,
                  resume_evidence INTEGER NOT NULL DEFAULT 0,
                  home TEXT,
                  cwd TEXT,
                  attempt_id TEXT,
                  workpack_sha256 TEXT,
                  PRIMARY KEY (work_id, generation),
                  FOREIGN KEY (work_id) REFERENCES works(work_id)
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_runs_one_active
                  ON runs(work_id) WHERE status = 'active';
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await EnsureResumeEvidenceColumnAsync(conn, cancellationToken).ConfigureAwait(false);
            await EnsureRunPathColumnsAsync(conn, cancellationToken).ConfigureAwait(false);
            await EnsureHandoffIdentityColumnsAsync(conn, cancellationToken).ConfigureAwait(false);
            _created = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<WorkRecord?> GetWorkAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT work_id, harness_adapter_id, created_at
                FROM works WHERE work_id = @id LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("@id", workId.Value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            return new WorkRecord
            {
                Id = WorkId.Parse(reader.GetString(0)),
                HarnessAdapterId = reader.GetString(1),
                CreatedAt = DateTimeOffset.Parse(
                    reader.GetString(2),
                    null,
                    System.Globalization.DateTimeStyles.RoundtripKind),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RunRecord?> GetRunAsync(
        WorkId workId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ReadRunAsync(conn, workId.Value, generation, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RunRecord?> GetActiveRunAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT work_id, generation, placement_id, mux_endpoint, status, resume_evidence, home, cwd, attempt_id, workpack_sha256
                FROM runs
                WHERE work_id = @id AND status = 'active'
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("@id", workId.Value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            return ReadRun(reader);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ActiveWorkRun>> ListActiveWorksAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT w.work_id, w.harness_adapter_id, w.created_at,
                       r.generation, r.placement_id, r.mux_endpoint, r.status,
                       r.resume_evidence, r.home, r.cwd, r.attempt_id, r.workpack_sha256
                FROM runs r
                JOIN works w ON w.work_id = r.work_id
                WHERE r.status = 'active'
                ORDER BY w.created_at ASC, r.generation ASC;
                """;
            var list = new List<ActiveWorkRun>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var work = new WorkRecord
                {
                    Id = WorkId.Parse(reader.GetString(0)),
                    HarnessAdapterId = reader.GetString(1),
                    CreatedAt = DateTimeOffset.Parse(
                        reader.GetString(2),
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                };
                var run = new RunRecord
                {
                    WorkId = work.Id,
                    Generation = reader.GetInt64(3),
                    PlacementId = reader.GetString(4),
                    MuxEndpoint = reader.GetString(5),
                    Status = FromStatus(reader.GetString(6)),
                    ResumeEvidence = FromResumeEvidence(reader.GetInt64(7)),
                    Home = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Cwd = reader.IsDBNull(9) ? null : reader.GetString(9),
                    AttemptId = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : null,
                    WorkPackSha256 = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null,
                };
                list.Add(new ActiveWorkRun { Work = work, Run = run });
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<RunRecord>> ListRunsAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT work_id, generation, placement_id, mux_endpoint, status, resume_evidence, home, cwd, attempt_id, workpack_sha256
                FROM runs
                WHERE work_id = @id
                ORDER BY generation ASC;
                """;
            cmd.Parameters.AddWithValue("@id", workId.Value);
            var list = new List<RunRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                list.Add(ReadRun(reader));
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveNewWorkAsync(
        WorkRecord work,
        RunRecord firstRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(firstRun);
        if (firstRun.WorkId != work.Id)
            throw new InvalidOperationException("run WorkId mismatch");
        if (firstRun.Status != RunStatus.Active)
            throw new InvalidOperationException("first Run must be active");

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (var w = conn.CreateCommand())
            {
                w.Transaction = tx;
                w.CommandText = """
                    INSERT INTO works (work_id, harness_adapter_id, created_at)
                    VALUES (@id, @adapter, @created);
                    """;
                w.Parameters.AddWithValue("@id", work.Id.Value);
                w.Parameters.AddWithValue("@adapter", work.HarnessAdapterId);
                w.Parameters.AddWithValue("@created", work.CreatedAt.UtcDateTime.ToString("O"));
                await w.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertRunAsync(conn, tx, firstRun, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveImportedWorkAsync(
        WorkRecord work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO works (work_id, harness_adapter_id, created_at)
                VALUES (@id, @adapter, @created);
                """;
            cmd.Parameters.AddWithValue("@id", work.Id.Value);
            cmd.Parameters.AddWithValue("@adapter", work.HarnessAdapterId);
            cmd.Parameters.AddWithValue("@created", work.CreatedAt.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask InsertActiveRunAsync(
        RunRecord destRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destRun);
        if (destRun.Status != RunStatus.Active)
            throw new InvalidOperationException("dest Run must be active");

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await InsertRunAsync(conn, tx, destRun, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask FlipGenerationAsync(
        WorkId workId,
        long sourceGeneration,
        RunRecord destRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destRun);
        if (destRun.WorkId != workId)
            throw new InvalidOperationException("dest WorkId mismatch");
        if (destRun.Generation != sourceGeneration + 1)
            throw new InvalidOperationException("dest generation must be source+1");
        if (destRun.Status != RunStatus.Active)
            throw new InvalidOperationException("dest Run must be active");

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var source = await ReadRunAsync(conn, workId.Value, sourceGeneration, cancellationToken, tx)
                .ConfigureAwait(false);
            if (source is null || source.Status != RunStatus.Active)
                throw new InvalidOperationException("source Run is not active");

            // Order: write dest active first, then mark source dead (dest wins on crash).
            // Unique index on one active requires source leave active first OR use fencing.
            // Spec: probe before flip; then dest active then source dead.
            // Unique index blocks two actives — mark source fencing, insert dest active, mark source dead.
            await using (var fence = conn.CreateCommand())
            {
                fence.Transaction = tx;
                fence.CommandText = """
                    UPDATE runs SET status = 'fencing'
                    WHERE work_id = @id AND generation = @gen AND status = 'active';
                    """;
                fence.Parameters.AddWithValue("@id", workId.Value);
                fence.Parameters.AddWithValue("@gen", sourceGeneration);
                var n = await fence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (n != 1)
                    throw new InvalidOperationException("failed to fence source Run");
            }

            await InsertRunAsync(conn, tx, destRun, cancellationToken).ConfigureAwait(false);

            await using (var dead = conn.CreateCommand())
            {
                dead.Transaction = tx;
                dead.CommandText = """
                    UPDATE runs SET status = 'dead'
                    WHERE work_id = @id AND generation = @gen AND status = 'fencing';
                    """;
                dead.Parameters.AddWithValue("@id", workId.Value);
                dead.Parameters.AddWithValue("@gen", sourceGeneration);
                await dead.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RecoverActiveInvariantAsync(
        WorkId workId,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Dest wins: highest generation that is active or fencing becomes sole active;
            // older active/fencing rows become dead.
            long? winnerGen = null;
            await using (var find = conn.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = """
                    SELECT generation FROM runs
                    WHERE work_id = @id AND status IN ('active', 'fencing')
                    ORDER BY generation DESC
                    LIMIT 1;
                    """;
                find.Parameters.AddWithValue("@id", workId.Value);
                var obj = await find.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (obj is long l)
                    winnerGen = l;
                else if (obj is int i)
                    winnerGen = i;
            }

            if (winnerGen is null)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await using (var promote = conn.CreateCommand())
            {
                promote.Transaction = tx;
                promote.CommandText = """
                    UPDATE runs SET status = 'active'
                    WHERE work_id = @id AND generation = @gen;
                    """;
                promote.Parameters.AddWithValue("@id", workId.Value);
                promote.Parameters.AddWithValue("@gen", winnerGen.Value);
                await promote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var kill = conn.CreateCommand())
            {
                kill.Transaction = tx;
                kill.CommandText = """
                    UPDATE runs SET status = 'dead'
                    WHERE work_id = @id
                      AND generation < @gen
                      AND status IN ('active', 'fencing');
                    """;
                kill.Parameters.AddWithValue("@id", workId.Value);
                kill.Parameters.AddWithValue("@gen", winnerGen.Value);
                await kill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask UpdateRunStatusAsync(
        WorkId workId,
        long generation,
        RunStatus status,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE runs SET status = @status
                WHERE work_id = @id AND generation = @gen;
                """;
            cmd.Parameters.AddWithValue("@status", ToStatus(status));
            cmd.Parameters.AddWithValue("@id", workId.Value);
            cmd.Parameters.AddWithValue("@gen", generation);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Test helper: simulate crash after dest active insert and before source dead.
    /// Leaves source in fencing and dest active.
    /// </summary>
    internal async ValueTask SimulateCrashAfterDestActiveAsync(
        WorkId workId,
        long sourceGeneration,
        RunRecord destRun,
        CancellationToken cancellationToken = default)
    {
        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (var fence = conn.CreateCommand())
            {
                fence.Transaction = tx;
                fence.CommandText = """
                    UPDATE runs SET status = 'fencing'
                    WHERE work_id = @id AND generation = @gen AND status = 'active';
                    """;
                fence.Parameters.AddWithValue("@id", workId.Value);
                fence.Parameters.AddWithValue("@gen", sourceGeneration);
                await fence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertRunAsync(conn, tx, destRun, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Test helper: plant two active Runs by dropping the one-active unique index.
    /// </summary>
    internal async ValueTask SimulateTwoActiveRunsAsync(
        WorkId workId,
        RunRecord extraActive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extraActive);
        if (extraActive.WorkId != workId)
            throw new InvalidOperationException("extra WorkId mismatch");
        if (extraActive.Status != RunStatus.Active)
            throw new InvalidOperationException("extra Run must be active");

        await EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (var drop = conn.CreateCommand())
            {
                drop.Transaction = tx;
                drop.CommandText = "DROP INDEX IF EXISTS ux_runs_one_active;";
                await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertRunAsync(conn, tx, extraActive, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString());

    private static async ValueTask InsertRunAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        RunRecord run,
        CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO runs (work_id, generation, placement_id, mux_endpoint, status, resume_evidence, home, cwd, attempt_id, workpack_sha256)
            VALUES (@id, @gen, @placement, @mux, @status, @evidence, @home, @cwd, @attempt, @pack);
            """;
        cmd.Parameters.AddWithValue("@id", run.WorkId.Value);
        cmd.Parameters.AddWithValue("@gen", run.Generation);
        cmd.Parameters.AddWithValue("@placement", run.PlacementId);
        cmd.Parameters.AddWithValue("@mux", run.MuxEndpoint);
        cmd.Parameters.AddWithValue("@status", ToStatus(run.Status));
        cmd.Parameters.AddWithValue("@evidence", (int)run.ResumeEvidence);
        cmd.Parameters.AddWithValue("@home", (object?)run.Home ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cwd", (object?)run.Cwd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@attempt", (object?)run.AttemptId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pack", (object?)run.WorkPackSha256 ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<RunRecord?> ReadRunAsync(
        SqliteConnection conn,
        string workId,
        long generation,
        CancellationToken cancellationToken,
        SqliteTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null)
            cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT work_id, generation, placement_id, mux_endpoint, status, resume_evidence, home, cwd, attempt_id, workpack_sha256
            FROM runs
            WHERE work_id = @id AND generation = @gen
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@id", workId);
        cmd.Parameters.AddWithValue("@gen", generation);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return ReadRun(reader);
    }

    private static RunRecord ReadRun(SqliteDataReader reader) =>
        new()
        {
            WorkId = WorkId.Parse(reader.GetString(0)),
            Generation = reader.GetInt64(1),
            PlacementId = reader.GetString(2),
            MuxEndpoint = reader.GetString(3),
            Status = FromStatus(reader.GetString(4)),
            ResumeEvidence = FromResumeEvidence(reader.GetInt64(5)),
            Home = reader.IsDBNull(6) ? null : reader.GetString(6),
            Cwd = reader.IsDBNull(7) ? null : reader.GetString(7),
            AttemptId = reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null,
            WorkPackSha256 = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null,
        };

    private static async Task EnsureResumeEvidenceColumnAsync(
        SqliteConnection conn,
        CancellationToken cancellationToken)
    {
        var hasColumn = false;
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(runs);";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "resume_evidence", StringComparison.Ordinal))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn)
            return;

        await using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE runs ADD COLUMN resume_evidence INTEGER NOT NULL DEFAULT 0;";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureRunPathColumnsAsync(
        SqliteConnection conn,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(runs);";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                columns.Add(reader.GetString(1));
        }

        if (!columns.Contains("home"))
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE runs ADD COLUMN home TEXT;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!columns.Contains("cwd"))
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE runs ADD COLUMN cwd TEXT;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureHandoffIdentityColumnsAsync(
        SqliteConnection conn,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(runs);";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                columns.Add(reader.GetString(1));
        }

        if (!columns.Contains("attempt_id"))
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE runs ADD COLUMN attempt_id TEXT;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!columns.Contains("workpack_sha256"))
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE runs ADD COLUMN workpack_sha256 TEXT;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static ResumeEvidence FromResumeEvidence(long value)
    {
        var mapped = (int)value;
        return Enum.IsDefined(typeof(ResumeEvidence), mapped)
            ? (ResumeEvidence)mapped
            : ResumeEvidence.None;
    }

    private static string ToStatus(RunStatus status) => status switch
    {
        RunStatus.Active => "active",
        RunStatus.Fencing => "fencing",
        RunStatus.Dead => "dead",
        RunStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static RunStatus FromStatus(string status) => status switch
    {
        "active" => RunStatus.Active,
        "fencing" => RunStatus.Fencing,
        "dead" => RunStatus.Dead,
        "failed" => RunStatus.Failed,
        _ => throw new InvalidOperationException($"unknown run status: {status}"),
    };
}
