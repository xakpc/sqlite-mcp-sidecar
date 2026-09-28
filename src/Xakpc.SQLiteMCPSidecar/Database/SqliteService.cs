using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xakpc.SQLiteMCPSidecar.Configuration;
using Xakpc.SQLiteMCPSidecar.Exceptions;

namespace Xakpc.SQLiteMCPSidecar.Database;

/// <summary>The outcome of a committed structured write.</summary>
/// <param name="RowsAffected">The rows of the target table that the statement changed.</param>
/// <param name="RowId">The rowid of an inserted row. It is 0 for an update and a delete.</param>
/// <param name="RowsChanged">
/// Every row that the statement changed, including the rows that an <c>ON DELETE CASCADE</c> removed
/// and the rows that a trigger wrote. This is the number that <c>maxRows</c> bounds and the number
/// that the write budget counts.
/// </param>
public sealed record StructuredWriteResult(int RowsAffected, long RowId, int RowsChanged);

/// <summary>The fixed diagnostic value set. Read-only, and never extended with a caller-chosen pragma.</summary>
/// <param name="SqliteVersion">The SQLite library version of this sidecar, not of the owning application.</param>
/// <param name="JournalMode">The journal mode of the database file. The owning application owns this setting.</param>
/// <param name="PageSize">The page size in bytes.</param>
/// <param name="PageCount">The number of pages. PageSize * PageCount is the database size.</param>
/// <param name="QuickCheck">The <c>PRAGMA quick_check</c> result. <c>ok</c> means no structural damage was found.</param>
public sealed record DiagnosticsResult(
    string SqliteVersion,
    string JournalMode,
    long PageSize,
    long PageCount,
    string QuickCheck);

/// <summary>
/// Opens connections to the one database of the deployment and runs the server-authored
/// statements. The sidecar opens a connection with the least privilege that the operation needs.
/// </summary>
public sealed class SqliteService : IDisposable
{
    private readonly SidecarOptions _options;
    private readonly SemaphoreSlim _requestSlots;
    private readonly SemaphoreSlim _writeSlot;
    private readonly SemaphoreSlim _backupSlot;

    public SqliteService(SidecarOptions options)
    {
        _options = options;
        _requestSlots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        _writeSlot = new SemaphoreSlim(1, 1);
        _backupSlot = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Bounds the number of concurrent MCP operations. A backup does not take one: it runs outside the
    /// request that started it. See <see cref="TryAcquireBackupSlot"/>.
    /// </summary>
    public async ValueTask<IDisposable> AcquireRequestSlotAsync(CancellationToken cancellationToken)
    {
        await _requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Slot(_requestSlots);
    }

    /// <summary>
    /// Takes the one write slot, or returns <c>null</c> when the busy timeout expires first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One writer at a time keeps the row-limit rollback meaningful and it lowers lock contention with
    /// the owning application. Structured writes and <c>execute_write_sql</c> share this slot.
    /// </para>
    /// <para>
    /// <b>Invariant.</b> The wait is bounded. A write takes a request slot first and then this slot,
    /// thus a write that waits holds a request slot. An unbounded wait would let
    /// <c>MAX_CONCURRENCY</c> queued writes occupy every request slot and starve each read. A caller
    /// that gets <c>null</c> returns <c>DatabaseBusy</c>, which is the honest code: another writer
    /// holds the lock, thus the agent retries later.
    /// </para>
    /// </remarks>
    public async ValueTask<IDisposable?> TryAcquireWriteSlotAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.BusyTimeoutSeconds);
        return await _writeSlot.WaitAsync(timeout, cancellationToken).ConfigureAwait(false)
            ? new Slot(_writeSlot)
            : null;
    }

    /// <summary>
    /// The read-only connection string. Pooling is off: a pooled handle keeps the authorizer and
    /// the limits of the last operation, which is a privilege-escalation path.
    /// </summary>
    public string ReadOnlyConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = _options.BusyTimeoutSeconds,
        }.ConnectionString;

    /// <summary>
    /// Opens a read-only connection. The connection opens read-only also when the deployment has
    /// write permissions: read access never becomes write access through a shared connection.
    /// </summary>
    public async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ReadOnlyConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Defensive mode, trusted schema off, the runtime limits and no attached databases.
            // The baseline belongs on every connection, and it applies after every Open: these
            // settings live on the handle and not on the process.
            SqliteSecurity.ApplyBaseline(connection, _options);

            // A second, independent block next to the read-only open mode. The two mechanisms fail
            // in different ways, thus both stay.
            //
            // This is a server-authored statement, thus it runs before any authorizer is installed.
            // Each policy rejects PRAGMA. The caller of this method installs the authorizer.
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA query_only=ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The read-write connection string. Pooling is off for the same reason as the read-only one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ReadWrite</c> and never <c>ReadWriteCreate</c>. The database must exist already: creation
    /// hides a wrong <c>SQLITE_SIDECAR_DB</c> path and makes an empty database next to the correct one.
    /// </para>
    /// <para>
    /// <b>Invariant.</b> <c>ForeignKeys = true</c>. The setting belongs to the connection and not to
    /// the database file, thus it does not conflict with the rule that the owning application owns the
    /// persistent settings. A write that breaks referential integrity is the damage class that this
    /// product must limit, thus the sidecar does not accept the SQLite default of off.
    /// </para>
    /// </remarks>
    public string ReadWriteConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = _options.BusyTimeoutSeconds,
            ForeignKeys = true,
        }.ConnectionString;

    /// <summary>
    /// Opens a read-write connection. Only a write operation calls it.
    /// </summary>
    /// <remarks>
    /// It runs no <c>PRAGMA query_only</c>, which is the one difference from
    /// <see cref="OpenReadOnlyAsync"/>. The baseline applies here too: those settings live on the
    /// handle, thus every connection needs them.
    /// </remarks>
    public async Task<SqliteConnection> OpenReadWriteAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ReadWriteConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            SqliteSecurity.ApplyBaseline(connection, _options);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Adds one row to a table. The server builds the statement; the caller sends no SQL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order of the steps is the security contract. Identifier validation reads the live schema
    /// with a <c>PRAGMA</c>, and <c>BEGIN IMMEDIATE</c> is transaction control. Every authorizer policy
    /// denies both, thus the authorizer goes on after them and comes off before the commit.
    /// </para>
    /// <para>
    /// <c>BEGIN IMMEDIATE</c> and not <c>BEGIN</c>, also for an insert. A deferred transaction must
    /// upgrade a read lock, and SQLite does not call the busy handler for a lock upgrade, thus the busy
    /// timeout would have no effect.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidWriteException">The table or a column does not exist, or a value has no SQLite equivalent.</exception>
    public async Task<StructuredWriteResult> InsertAsync(
        string table,
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(values);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.QueryTimeoutSeconds));
        var token = timeout.Token;

        await using var connection = await OpenReadWriteAsync(token).ConfigureAwait(false);
        using var interrupt = SqliteSecurity.RegisterInterrupt(connection, token);

        var schema = await StructuredWriteBuilder.ReadTableSchemaAsync(connection, table, token).ConfigureAwait(false);
        var statement = StructuredWriteBuilder.BuildInsert(schema, values);

        await RunServerStatementAsync(connection, "BEGIN IMMEDIATE;", token).ConfigureAwait(false);
        var authorizer = SqliteSecurity.InstallAuthorizer(connection, AuthorizerPolicy.Write);
        try
        {
            int rowsAffected;

            // The budget counts every row that the statement wrote, thus a trigger on the table is
            // charged too. An insert has no maxRows, so this count bounds nothing here.
            var changesBefore = SqliteSecurity.TotalChanges(connection);
            await using (var command = connection.CreateCommand())
            {
                StructuredWriteBuilder.Bind(command, statement);
                rowsAffected = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            var rowsChanged = SqliteSecurity.TotalChanges(connection) - changesBefore;
            var rowId = SqliteSecurity.LastInsertRowId(connection);

            // Off before the commit: the policy denies transaction control, thus the server would
            // otherwise reject its own COMMIT.
            authorizer.Dispose();
            await RunServerStatementAsync(connection, "COMMIT;", token).ConfigureAwait(false);

            return new StructuredWriteResult(rowsAffected, rowId, rowsChanged);
        }
        catch
        {
            authorizer.Dispose();
            await RollbackQuietlyAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Changes the values of the rows that a filter selects. The server builds the statement.
    /// </summary>
    /// <exception cref="InvalidWriteException">The request shape, a name or a value is wrong.</exception>
    /// <exception cref="WriteLimitExceededException">The write passed the effective limit.</exception>
    public Task<StructuredWriteResult> UpdateAsync(
        string table,
        IReadOnlyDictionary<string, JsonElement> values,
        IReadOnlyList<WriteCondition> where,
        int maxRows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);

        return MutateAsync(
            table,
            where,
            maxRows,
            schema => StructuredWriteBuilder.BuildUpdate(schema, values, where),
            cancellationToken);
    }

    /// <summary>
    /// Removes the rows that a filter selects. The server builds the statement.
    /// </summary>
    /// <exception cref="InvalidWriteException">The request shape, a name or a value is wrong.</exception>
    /// <exception cref="WriteLimitExceededException">The write passed the effective limit.</exception>
    public Task<StructuredWriteResult> DeleteAsync(
        string table,
        IReadOnlyList<WriteCondition> where,
        int maxRows,
        CancellationToken cancellationToken)
        => MutateAsync(
            table,
            where,
            maxRows,
            schema => StructuredWriteBuilder.BuildDelete(schema, where),
            cancellationToken);

    /// <summary>
    /// Runs one bounded structured <c>UPDATE</c> or <c>DELETE</c> inside one transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The step order is the same security contract as <see cref="InsertAsync"/>: the identifier
    /// validation runs a <c>PRAGMA</c> and <c>BEGIN IMMEDIATE</c> is transaction control, thus both
    /// run before the authorizer goes on, and the authorizer comes off before the commit.
    /// </para>
    /// <para>
    /// <b>Invariant.</b> Two independent checks bound the write, and both are necessary. The
    /// pre-count protects the availability of the owning application: it rejects a broad filter
    /// before the statement writes anything, holds the write lock or inflates the WAL. The
    /// post-execution check protects data integrity: the owning application writes too, thus the row
    /// count can change between the count and the write. See
    /// <c>.lode/database/structured-writes.md</c>.
    /// </para>
    /// <para>
    /// <b>Invariant.</b> A <c>LIMIT</c> on the write statement is not a substitute for either check.
    /// A <c>LIMIT</c> writes a partial result silently, which is worse for an agent than a clean
    /// rejection.
    /// </para>
    /// <para>
    /// The pre-count is a <c>SELECT</c> and it runs with the authorizer installed.
    /// <see cref="AuthorizerPolicy.Write"/> permits <c>SQLITE_SELECT</c> and <c>SQLITE_READ</c> for
    /// exactly this reason.
    /// </para>
    /// </remarks>
    private async Task<StructuredWriteResult> MutateAsync(
        string table,
        IReadOnlyList<WriteCondition> where,
        int maxRows,
        Func<TableSchema, StructuredStatement> build,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(where);

        // The deployment cap always wins. A caller cannot raise its own bound.
        var effectiveLimit = Math.Min(maxRows, _options.MaxWriteRows);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.QueryTimeoutSeconds));
        var token = timeout.Token;

        await using var connection = await OpenReadWriteAsync(token).ConfigureAwait(false);
        using var interrupt = SqliteSecurity.RegisterInterrupt(connection, token);

        var schema = await StructuredWriteBuilder.ReadTableSchemaAsync(connection, table, token).ConfigureAwait(false);
        var statement = build(schema);
        var preCount = StructuredWriteBuilder.BuildPreCount(schema, where, effectiveLimit + 1);

        await RunServerStatementAsync(connection, "BEGIN IMMEDIATE;", token).ConfigureAwait(false);
        var authorizer = SqliteSecurity.InstallAuthorizer(connection, AuthorizerPolicy.Write);
        try
        {
            long matched;
            await using (var command = connection.CreateCommand())
            {
                StructuredWriteBuilder.Bind(command, preCount);
                matched = Convert.ToInt64(
                    await command.ExecuteScalarAsync(token).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            if (matched > effectiveLimit)
            {
                throw new WriteLimitExceededException(WriteLimitExceededException.PreCheck, effectiveLimit);
            }

            int rowsAffected;
            var changesBefore = SqliteSecurity.TotalChanges(connection);
            await using (var command = connection.CreateCommand())
            {
                StructuredWriteBuilder.Bind(command, statement);
                rowsAffected = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            // Not the ExecuteNonQuery count: a cascade and a trigger also change rows, and those rows
            // are what the limit must bound.
            var rowsChanged = SqliteSecurity.TotalChanges(connection) - changesBefore;
            if (rowsChanged > effectiveLimit)
            {
                throw new WriteLimitExceededException(WriteLimitExceededException.PostCheck, effectiveLimit);
            }

            authorizer.Dispose();
            await RunServerStatementAsync(connection, "COMMIT;", token).ConfigureAwait(false);

            return new StructuredWriteResult(rowsAffected, 0, rowsChanged);
        }
        catch
        {
            authorizer.Dispose();
            await RollbackQuietlyAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs one statement that the server authored. No authorizer may be installed when it runs.
    /// </summary>
    private static async Task RunServerStatementAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls the transaction back on a failure path.
    /// </summary>
    /// <remarks>
    /// It swallows its own failure on purpose, and it passes no cancellation token. The original
    /// exception is what the caller must see: a failure here would replace a useful error with a
    /// confusing one. Closing the connection would also roll back, and the explicit statement makes
    /// the intent visible and releases the write lock sooner.
    /// </remarks>
    private static async Task RollbackQuietlyAsync(SqliteConnection connection)
    {
        try
        {
            await RunServerStatementAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // No transaction was open, or the handle is already unusable.
        }
    }

    /// <summary>
    /// Runs one caller-supplied read statement inside the sandbox and returns a bounded result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order matters. The baseline applies on <c>Open</c>, the server statement runs next, and
    /// the authorizer is installed only after that. The disposal order is the reverse, thus the
    /// authorizer and the progress handler are gone before the handle closes.
    /// </para>
    /// <para>
    /// The timeout interrupts SQLite itself. A command timeout would only abandon the caller and
    /// leave the statement running against the owning application.
    /// </para>
    /// </remarks>
    /// <exception cref="StatementRejectedException">
    /// The text is not exactly one statement, or the authorizer rejected it.
    /// </exception>
    public async Task<QueryResult> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.QueryTimeoutSeconds));
        var token = timeout.Token;

        await using var connection = await OpenReadOnlyAsync(token).ConfigureAwait(false);
        using var interrupt = SqliteSecurity.RegisterInterrupt(connection, token);
        using var authorizer = SqliteSecurity.InstallAuthorizer(connection, AuthorizerPolicy.Read);

        var check = SqliteSecurity.ValidateSingleStatement(connection, sql);
        if (check != StatementCheck.Ok)
        {
            throw new StatementRejectedException(check);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await QueryResult.ReadAsync(reader, _options, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the journal mode. The sidecar never writes this value: the owning application owns it.
    /// </summary>
    public async Task<string> ReadJournalModeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string ?? "unknown";
    }

    /// <summary>
    /// Reads the DDL of every table, index, trigger and view. The statement is server-authored and
    /// takes no caller input.
    /// </summary>
    public async Task<string> ReadSchemaDdlAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sql FROM sqlite_master
            WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'
            ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'view' THEN 1 WHEN 'index' THEN 2 ELSE 3 END, name;
            """;

        var builder = new System.Text.StringBuilder();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            builder.Append(reader.GetString(0).TrimEnd()).Append(';').Append('\n');
        }

        return builder.ToString();
    }


    /// <summary>
    /// Takes the one backup slot, or returns <c>null</c> when a backup already runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Invariant.</b> The wait is zero. This is the one semaphore of the three that never waits,
    /// and it must not copy the bounded wait of <see cref="TryAcquireWriteSlotAsync"/>. A backup runs
    /// for as long as the database is large, thus a waiting caller would hold a connection for the
    /// whole copy. A second request is answered at once with <c>BackupFailed</c>.
    /// </para>
    /// <para>
    /// Do not queue the second request either. A queue lets an agent plan unbounded disk use, and this
    /// product has no scheduler.
    /// </para>
    /// </remarks>
    public IDisposable? TryAcquireBackupSlot() =>
        _backupSlot.Wait(0) ? new Slot(_backupSlot) : null;

    /// <summary>
    /// The connection string of a backup destination. The path is server-built and never comes from a
    /// caller.
    /// </summary>
    /// <remarks>
    /// <c>ReadWriteCreate</c> is correct here and it is the one place in the sidecar that creates a
    /// file: the destination of a backup does not exist yet. The rule that the live database is never
    /// created is about <see cref="ReadWriteConnectionString"/>, which stays <c>ReadWrite</c>.
    /// </remarks>
    public static string BackupDestinationConnectionString(string partialPath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = partialPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ConnectionString;

    /// <summary>
    /// Opens the destination of a backup. The sandbox baseline does not apply: no caller SQL ever
    /// reaches this handle, and the only writer is the Online Backup API itself.
    /// </summary>
    public static async Task<SqliteConnection> OpenBackupDestinationAsync(
        string partialPath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(BackupDestinationConnectionString(partialPath));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reads the fixed diagnostic value set over a read-only connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Invariant.</b> The value set is fixed and the statements are server-authored. Never accept a
    /// caller-supplied <c>PRAGMA</c> name: a caller-chosen pragma is a write primitive and an
    /// information leak.
    /// </para>
    /// <para>
    /// <c>quick_check</c> and not <c>integrity_check</c>. <c>quick_check</c> does not verify index
    /// content, thus it does not stall the owning application. It still reads every page, thus the
    /// query timeout and the interrupt bound it: a large database must not hang the tool.
    /// </para>
    /// </remarks>
    public async Task<DiagnosticsResult> ReadDiagnosticsAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.QueryTimeoutSeconds));
        var token = timeout.Token;

        await using var connection = await OpenReadOnlyAsync(token).ConfigureAwait(false);
        using var interrupt = SqliteSecurity.RegisterInterrupt(connection, token);

        // No authorizer is installed. Every policy denies PRAGMA, and these statements are the
        // server's own. This is the same shape as ReadJournalModeAsync.
        var version = await ReadScalarAsync(connection, "SELECT sqlite_version();", token).ConfigureAwait(false);
        var journalMode = await ReadScalarAsync(connection, "PRAGMA journal_mode;", token).ConfigureAwait(false);
        var pageSize = await ReadScalarAsync(connection, "PRAGMA page_size;", token).ConfigureAwait(false);
        var pageCount = await ReadScalarAsync(connection, "PRAGMA page_count;", token).ConfigureAwait(false);
        var quickCheck = await ReadScalarAsync(connection, "PRAGMA quick_check;", token).ConfigureAwait(false);

        return new DiagnosticsResult(
            version ?? "unknown",
            journalMode ?? "unknown",
            ParseLong(pageSize),
            ParseLong(pageCount),
            quickCheck ?? "unknown");
    }

    private static async Task<string?> ReadScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value switch
        {
            null or DBNull => null,
            string text => text,
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    public void Dispose()
    {
        _requestSlots.Dispose();
        _writeSlot.Dispose();
        _backupSlot.Dispose();
    }

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
