using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xakpc.SQLiteMCPSidecar.Database;
using Xakpc.SQLiteMCPSidecar.Exceptions;
using Xakpc.SQLiteMCPSidecar.Security;

namespace Xakpc.SQLiteMCPSidecar.Mcp;

/// <summary>
/// The MCP tools of the sidecar. The deployment permission set decides which of them a caller
/// sees and can call.
/// </summary>
/// <remarks>
/// <para>
/// Gating has two layers and both come from the <c>[Authorize]</c> attribute on each tool.
/// <c>AddAuthorizationFilters()</c> removes an unauthorized tool from <c>tools/list</c>, which is
/// the primary control, and it rejects a direct <c>tools/call</c> of that name, which is the
/// backstop.
/// </para>
/// <para>
/// Every tool and every parameter needs a <see cref="DescriptionAttribute"/>. That text is the
/// only description the agent reads, and a vague description is the main reason an agent misuses
/// a tool.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed partial class SqliteTools(
    SqliteService database,
    WriteBudget writeBudget,
    WriteDeduplication deduplication,
    BackupService backups,
    ILogger<SqliteTools> logger)
{
    [McpServerTool(Name = "schema")]
    [Description("Return the DDL of every table, view and index in the database. "
               + "The output is SQL text, not table rows: one CREATE statement states the columns, "
               + "the types, the nullability, the defaults, the primary key, the foreign keys and "
               + "the constraints. Read this before any query or write.")]
    [Authorize(Policy = "perm:schema")]
    public async Task<CallToolResult> SchemaAsync(CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetTimestamp();
        using var slot = await database.AcquireRequestSlotAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var ddl = await database.ReadSchemaDdlAsync(cancellationToken).ConfigureAwait(false);
            LogCompleted(logger, "schema", Elapsed(started), "ok");
            return SidecarErrors.Success(ddl);
        }
        catch (OperationCanceledException)
        {
            LogCompleted(logger, "schema", Elapsed(started), nameof(SidecarError.QueryTimedOut));
            return SidecarErrors.Failure(SidecarError.QueryTimedOut, "The schema read was cancelled.");
        }
        catch (Exception exception)
        {
            // The detail reaches the log only. The caller gets the code and a fixed explanation.
            LogFailed(logger, "schema", nameof(SidecarError.DatabaseError), exception);
            return SidecarErrors.Failure(SidecarError.DatabaseError, "The schema could not be read.");
        }
    }

    [McpServerTool(Name = "query")]
    [Description("Run one read-only SQL statement and return the rows as TOON. "
               + "Read the schema first. The statement must be exactly one SELECT: a second "
               + "statement, any write, any DDL, ATTACH, VACUUM and any PRAGMA are rejected. "
               + "The result is bounded by a row limit, a byte limit and a timeout, and the "
               + "'truncated' flag states whether the sidecar stopped at a limit.")]
    [Authorize(Policy = "perm:read")]
    public async Task<CallToolResult> QueryAsync(
        [Description("One read-only SQL statement, as a string, with no trailing second statement. "
                   + "Write literal values into the statement; there is no parameter list.")]
        string? sql = null,
        CancellationToken cancellationToken = default)
    {
        var started = TimeProvider.System.GetTimestamp();

        // The `= null` lesson applies here too, and it was missing: with no default the binder throws
        // for an absent argument and the SDK masks the message, thus an agent that forgot `sql` got
        // no error code at all. Shape validation runs before the request slot, because a malformed
        // request must not occupy one.
        if (string.IsNullOrWhiteSpace(sql))
        {
            LogCompleted(logger, "query", Elapsed(started), nameof(SidecarError.InvalidQuery));
            return SidecarErrors.Failure(
                SidecarError.InvalidQuery,
                "The sql value is required. Send exactly one read-only SQL statement as a string.");
        }

        using var slot = await database.AcquireRequestSlotAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await database.QueryAsync(sql, cancellationToken).ConfigureAwait(false);
            if (result.RowTooLarge)
            {
                LogQueryCompleted(logger, "query", Elapsed(started), 0, 0, nameof(SidecarError.ResultTooLarge));
                return SidecarErrors.Failure(
                    SidecarError.ResultTooLarge,
                    "One single row is larger than the result byte budget. Select fewer columns.");
            }

            // The SQL text and the returned values never reach the log.
            LogQueryCompleted(logger, "query", Elapsed(started), result.RowCount, result.ByteCount, "ok");
            return SidecarErrors.Success(result.Text);
        }
        catch (StatementRejectedException exception)
        {
            var (code, explanation) = exception.Check switch
            {
                StatementCheck.MultipleStatements => (SidecarError.InvalidQuery,
                    "The request holds more than one statement. Send exactly one."),
                StatementCheck.Empty => (SidecarError.InvalidQuery,
                    "The request holds no statement. Send one SELECT; whitespace and a comment alone "
                  + "are not a query."),
                StatementCheck.Rejected => (SidecarError.QueryRejected,
                    "The requested action is not permitted. Only read statements are available."),
                // The statement is correct and the database is locked. Same code that the execution
                // path returns for SQLITE_BUSY, thus one condition gives one code wherever it
                // appears, and the agent retries instead of rewriting the statement.
                StatementCheck.Busy => (SidecarError.DatabaseBusy,
                    "Another writer holds the lock. Retry the query later."),
                _ => (SidecarError.InvalidQuery,
                    "The statement did not compile. Check the syntax and the table and column names."),
            };

            LogCompleted(logger, "query", Elapsed(started), code.ToString());
            return SidecarErrors.Failure(code, explanation);
        }
        catch (SqliteException exception)
        {
            // A SQLite message often names the database file, thus it reaches the log only.
            var code = exception.SqliteErrorCode switch
            {
                SqliteAuthorizationDenied => SidecarError.QueryRejected,
                SqliteInterrupt => SidecarError.QueryTimedOut,
                SqliteBusy or SqliteLocked => SidecarError.DatabaseBusy,
                SqliteError or SqliteTooBig => SidecarError.InvalidQuery,
                _ => SidecarError.DatabaseError,
            };

            LogFailed(logger, "query", code.ToString(), exception);
            return SidecarErrors.Failure(code, ExplanationFor(code));
        }
        catch (OperationCanceledException)
        {
            LogCompleted(logger, "query", Elapsed(started), nameof(SidecarError.QueryTimedOut));
            return SidecarErrors.Failure(
                SidecarError.QueryTimedOut,
                "The query passed the time limit and was stopped. Make the query smaller.");
        }
    }

    [McpServerTool(Name = "insert")]
    [Description("Add exactly one row to one table. You do not write SQL: name the table and give a "
               + "column-to-value object, and the server builds a parameterized INSERT. "
               + "Read the schema first, because every column name is checked against it. "
               + "Each value is a literal, never a SQL expression: to get a computed default, leave "
               + "the column out and let the column default apply. "
               + "There is no conflict clause, so a duplicate key fails instead of updating the row. "
               + "One call adds one row; call it again for another row.")]
    [Authorize(Policy = "perm:write")]
    public async Task<CallToolResult> InsertAsync(
        [Description("A unique identifier for this write, as a string, at most 128 characters. Send "
                   + "the SAME value when you retry a call that failed or timed out: the row is then "
                   + "added one time only. Use a NEW value for new work.")]
        string? requestId = null,
        [Description("The table to add the row to, as a string. It must be a table from the schema "
                   + "tool, not a view.")]
        string? table = null,
        [Description("The row, as an object of column name to value. A value is a string, a number, a "
                   + "boolean or null. Omit a column to accept its default.")]
        Dictionary<string, JsonElement>? values = null,
        CancellationToken cancellationToken = default)
    {
        var started = TimeProvider.System.GetTimestamp();

        // Shape validation before any database work, and before the request slot: a malformed request
        // must not occupy a slot or reach the database.
        if (ValidateWriteRequest(requestId, table, out var shapeFailure) is not { } key)
        {
            LogWriteCompleted(logger, "insert", Elapsed(started), table ?? "-", 0, false, shapeFailure!);
            return SidecarErrors.Failure(SidecarError.InvalidWrite, ExplanationForWrite(shapeFailure!));
        }

        if (values is null || values.Count == 0)
        {
            LogWriteCompleted(logger, "insert", Elapsed(started), table!, 0, false, nameof(SidecarError.InvalidWrite));
            return SidecarErrors.Failure(
                SidecarError.InvalidWrite,
                "The values object is absent or empty. Name at least one column and its value.");
        }

        return await RunWriteAsync(
            "insert",
            started,
            key,
            StructuredWriteBuilder.CanonicalInsertPayload(table!, values),
            (ms, rows, replayed, outcome) => LogWriteCompleted(logger, "insert", ms, table!, rows, replayed, outcome),
            token => database.InsertAsync(table!, values, token),
            result => $"rowsAffected: {result.RowsAffected}\nrowid: {result.RowId}",
            cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "update")]
    [Description("Change columns of the rows that a filter selects. You do not write SQL: name the "
               + "table, give a column-to-value object, give a where filter and give maxRows. "
               + "Read the schema first, because every column name is checked against it. "
               + "Each value is a literal, never a SQL expression. "
               + "The filter is a flat list and every condition is joined with AND; there is no OR "
               + "and no nesting, so express an either-or as two calls. "
               + "The filter is required: there is no way to change every row of a table.")]
    [Authorize(Policy = "perm:write")]
    public async Task<CallToolResult> UpdateAsync(
        [Description("A unique identifier for this write, as a string, at most 128 characters. Send "
                   + "the SAME value when you retry a call that failed or timed out: the change is "
                   + "then applied one time only. Use a NEW value for new work.")]
        string? requestId = null,
        [Description("The table to change, as a string. It must be a table from the schema tool, not "
                   + "a view.")]
        string? table = null,
        [Description("The new values, as an object of column name to value. A value is a string, a "
                   + "number, a boolean or null. Only the named columns change.")]
        Dictionary<string, JsonElement>? values = null,
        [Description("The filter, as a LIST of condition objects joined with AND, never a SQL string. "
                   + "Each condition has a column, an operator and, for a comparison, a value. The "
                   + "operators are eq, ne, lt, lte, gt, gte, is-null and is-not-null; the last two "
                   + "take no value. At least one condition is required.")]
        List<WriteCondition>? where = null,
        [Description("The most rows this call may change, as a whole number. The call is rejected and "
                   + "nothing changes if the filter matches more. This counts EVERY row the write "
                   + "touches, including rows a trigger writes, so it can be larger than the number "
                   + "of rows the filter selects. The deployment also sets its own cap, and the lower "
                   + "of the two wins.")]
        int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        var started = TimeProvider.System.GetTimestamp();

        if (ValidateWriteRequest(requestId, table, out var shapeFailure) is not { } key)
        {
            LogWriteCompleted(logger, "update", Elapsed(started), table ?? "-", 0, false, shapeFailure!);
            return SidecarErrors.Failure(SidecarError.InvalidWrite, ExplanationForWrite(shapeFailure!));
        }

        if (values is null || values.Count == 0)
        {
            LogWriteCompleted(logger, "update", Elapsed(started), table!, 0, false, nameof(SidecarError.InvalidWrite));
            return SidecarErrors.Failure(
                SidecarError.InvalidWrite,
                "The values object is absent or empty. Name at least one column and its new value.");
        }

        if (ValidateFilter(where, maxRows, out var filterFailure) is not ({ } filter, { } limit))
        {
            LogWriteCompleted(logger, "update", Elapsed(started), table!, 0, false, nameof(SidecarError.InvalidWrite));
            return SidecarErrors.Failure(SidecarError.InvalidWrite, filterFailure!);
        }

        return await RunWriteAsync(
            "update",
            started,
            key,
            StructuredWriteBuilder.CanonicalUpdatePayload(table!, values, filter, limit),
            (ms, rows, replayed, outcome) => LogWriteCompleted(logger, "update", ms, table!, rows, replayed, outcome),
            token => database.UpdateAsync(table!, values, filter, limit, token),
            result => $"rowsAffected: {result.RowsAffected}\nrowsChanged: {result.RowsChanged}",
            cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "delete")]
    [Description("Remove the rows that a filter selects. You do not write SQL: name the table, give "
               + "a where filter and give maxRows. "
               + "Read the schema first, because every column name is checked against it. "
               + "The filter is a flat list and every condition is joined with AND; there is no OR "
               + "and no nesting, so express an either-or as two calls. "
               + "The filter is required: there is no way to remove every row of a table. "
               + "A delete cannot be undone, and it can remove rows in OTHER tables through "
               + "ON DELETE CASCADE, so read the schema and count those rows into maxRows.")]
    [Authorize(Policy = "perm:write")]
    public async Task<CallToolResult> DeleteAsync(
        [Description("A unique identifier for this write, as a string, at most 128 characters. Send "
                   + "the SAME value when you retry a call that failed or timed out: the rows are "
                   + "then removed one time only. Use a NEW value for new work.")]
        string? requestId = null,
        [Description("The table to remove rows from, as a string. It must be a table from the schema "
                   + "tool, not a view.")]
        string? table = null,
        [Description("The filter, as a LIST of condition objects joined with AND, never a SQL string. "
                   + "Each condition has a column, an operator and, for a comparison, a value. The "
                   + "operators are eq, ne, lt, lte, gt, gte, is-null and is-not-null; the last two "
                   + "take no value. At least one condition is required.")]
        List<WriteCondition>? where = null,
        [Description("The most rows this call may remove, as a whole number. The call is rejected and "
                   + "nothing changes if the filter matches more. This counts EVERY row the write "
                   + "removes, including rows removed in other tables by ON DELETE CASCADE, so "
                   + "deleting one row that has three cascading children needs a maxRows of at least "
                   + "4. The deployment also sets its own cap, and the lower of the two wins.")]
        int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        var started = TimeProvider.System.GetTimestamp();

        if (ValidateWriteRequest(requestId, table, out var shapeFailure) is not { } key)
        {
            LogWriteCompleted(logger, "delete", Elapsed(started), table ?? "-", 0, false, shapeFailure!);
            return SidecarErrors.Failure(SidecarError.InvalidWrite, ExplanationForWrite(shapeFailure!));
        }

        if (ValidateFilter(where, maxRows, out var filterFailure) is not ({ } filter, { } limit))
        {
            LogWriteCompleted(logger, "delete", Elapsed(started), table!, 0, false, nameof(SidecarError.InvalidWrite));
            return SidecarErrors.Failure(SidecarError.InvalidWrite, filterFailure!);
        }

        return await RunWriteAsync(
            "delete",
            started,
            key,
            StructuredWriteBuilder.CanonicalDeletePayload(table!, filter, limit),
            (ms, rows, replayed, outcome) => LogWriteCompleted(logger, "delete", ms, table!, rows, replayed, outcome),
            token => database.DeleteAsync(table!, filter, limit, token),
            result => $"rowsAffected: {result.RowsAffected}\nrowsChanged: {result.RowsChanged}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The registered name of the raw write tool.</summary>
    public const string RawWriteToolName = "execute_write_sql";

    [McpServerTool(Name = RawWriteToolName)]
    [Description("Run one caller-written INSERT, UPDATE or DELETE statement. This is the escape hatch "
               + "for work that the structured insert, update and delete tools cannot express, and it "
               + "gives up their protections: there is no row limit, and a WHERE clause is not "
               + "required, so 'DELETE FROM jobs' is accepted and removes every row. A write cannot be "
               + "undone: take a backup first when you are not sure. "
               + "Read the schema first and write literal values into the statement; there is no "
               + "parameter list. "
               + "It must be exactly one statement: a second statement, any SELECT-only work that "
               + "belongs in the query tool, DDL, ATTACH, VACUUM, any PRAGMA and BEGIN or COMMIT are "
               + "all rejected, and so is a write to an sqlite_ internal table. "
               + "Add a RETURNING clause to get the changed rows back as TOON, bounded by the same row "
               + "limit and byte limit as a query.")]
    [Authorize(Policy = "perm:danger-raw-write")]
    public async Task<CallToolResult> ExecuteWriteSqlAsync(
        [Description("A unique identifier for this write, as a string, at most 128 characters. Send "
                   + "the SAME value when you retry a call that failed or timed out: the statement "
                   + "then runs one time only. Use a NEW value for new work.")]
        string? requestId = null,
        [Description("One INSERT, UPDATE or DELETE statement, as a string, with no trailing second "
                   + "statement. Write literal values into the statement. CTEs, subqueries, conflict "
                   + "clauses and RETURNING are available.")]
        string? sql = null,
        CancellationToken cancellationToken = default)
    {
        var started = TimeProvider.System.GetTimestamp();

        // Shape validation before any database work, and before the request slot.
        if (ValidateRequestId(requestId, out var shapeFailure) is not { } key)
        {
            LogRawWriteCompleted(logger, RawWriteToolName, Elapsed(started), "-", 0, false, shapeFailure!);
            return SidecarErrors.Failure(SidecarError.InvalidWrite, ExplanationForWrite(shapeFailure!));
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            LogRawWriteCompleted(logger, RawWriteToolName, Elapsed(started), "-", 0, false, nameof(SidecarError.InvalidWrite));
            return SidecarErrors.Failure(
                SidecarError.InvalidWrite,
                "The sql value is required. Send exactly one INSERT, UPDATE or DELETE statement.");
        }

        // The statement itself is the canonical payload: there is no argument map whose key order
        // would need normalizing. The hash of it is also the log identifier, thus the statement text
        // never reaches a log line.
        var canonical = $"{RawWriteToolName}\n{sql.Trim()}";
        var sqlHash = WriteDeduplication.HashPayload(canonical);

        return await RunWriteAsync(
            RawWriteToolName,
            started,
            key,
            canonical,
            (ms, rows, replayed, outcome) =>
                LogRawWriteCompleted(logger, RawWriteToolName, ms, sqlHash, rows, replayed, outcome),
            token => database.ExecuteWriteSqlAsync(sql!, token),
            FormatRawWrite,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the answer of a raw write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Invariant.</b> The result size never fails a committed raw write. The statement has already
    /// run and committed when this method runs, thus <see cref="SidecarError.ResultTooLarge"/> is
    /// unreachable here although the read path returns it for the same condition. A truncated
    /// <c>RETURNING</c> result reports <c>truncated: true</c>, and a row that no budget can hold reports
    /// that the rows were not returned. Both answers are honest about the write, which is the number the
    /// agent must act on. See
    /// <c>.lode/decisions/0008-a-committed-raw-write-never-fails-on-result-size.md</c>.
    /// </para>
    /// <para>
    /// <c>rowsAffected</c> only, and never <c>rowsChanged</c>. A raw write has no <c>maxRows</c> to
    /// bound a cascade against, thus the second number would be a fact with no action attached to it.
    /// The write budget still counts it.
    /// </para>
    /// </remarks>
    private static string FormatRawWrite(RawWriteResult result)
    {
        if (result.Rows is null)
        {
            return $"rowsAffected: {result.RowsAffected}";
        }

        if (result.Rows.RowTooLarge)
        {
            return $"rowsAffected: {result.RowsAffected}\n\n"
                 + "rows: not returned, because one single row is larger than the result byte budget. "
                 + "The write was applied. Name fewer columns in RETURNING to see the rows.";
        }

        return $"rowsAffected: {result.RowsAffected}\n\n{result.Rows.Text}";
    }

    /// <summary>
    /// The registered name of the backup tool. <c>Program</c> reads it to mark this one tool, and only
    /// this one, as task-mode.
    /// </summary>
    public const string BackupToolName = "backup";

    [McpServerTool(Name = BackupToolName, ReadOnly = true)]
    [Description("Make a consistent copy of the database into the server's backup directory. "
               + "The copy is safe to take while the application that owns the database keeps writing, "
               + "and it never blocks that application for the length of the copy. "
               + "This call answers immediately with a task and the copy continues in the background: "
               + "poll the task to learn the outcome and the file name. "
               + "You cannot choose where the file goes and you cannot read it back through this server; "
               + "you give an optional label and the server builds the name. "
               + "Only one backup runs at a time. "
               + "Take a backup before a delete you are not sure about, because a delete cannot be undone.")]
    [Authorize(Policy = "perm:backup")]
    public async Task<CallToolResult> BackupAsync(
        [Description("An optional short label for the file name, at most 40 characters, for example "
                   + "'before-cleanup'. Letters, digits, dots, underscores and hyphens are kept and "
                   + "anything else is removed. It is a label and NOT a path or a file name: the server "
                   + "adds the database name and a UTC timestamp, so a repeated label never overwrites "
                   + "an earlier backup.")]
        string? label = null,
        CancellationToken cancellationToken = default)
    {
        var started = TimeProvider.System.GetTimestamp();

        // INVARIANT: a backup takes no request slot. It runs outside the request that started it and it
        // lasts as long as the database is large, thus holding one of MAX_CONCURRENCY slots for that
        // time would starve every read. The backup slot alone serialises it.
        using var backupSlot = database.TryAcquireBackupSlot();
        if (backupSlot is null)
        {
            LogCompleted(logger, BackupToolName, Elapsed(started), nameof(SidecarError.BackupFailed));
            return SidecarErrors.Failure(
                SidecarError.BackupFailed,
                "A backup is already in progress. Wait for it to finish, then start another one.");
        }

        try
        {
            var result = await backups.RunAsync(label, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is BackupOutcome.AbandonedAfterRestarts)
            {
                LogCompleted(logger, BackupToolName, Elapsed(started), nameof(SidecarError.BackupFailed));
                return SidecarErrors.Failure(
                    SidecarError.BackupFailed,
                    "The backup restarted too many times, because the database is written to faster than "
                  + "it can be copied. Nothing was left behind. Retry when the database is quieter.");
            }

            LogCompleted(logger, BackupToolName, Elapsed(started), "ok");
            return SidecarErrors.Success(
                $"name: {result.FileName}\nbytes: {result.Bytes}\nrestarts: {result.Restarts}");
        }
        catch (OperationCanceledException)
        {
            // The sidecar is stopping. Nothing usable was left behind: BackupService removes the partial
            // file on every abandon path.
            LogCompleted(logger, BackupToolName, Elapsed(started), nameof(SidecarError.BackupFailed));
            return SidecarErrors.Failure(
                SidecarError.BackupFailed,
                "The backup was stopped before it finished. Nothing was left behind. Start another one.");
        }
        catch (Exception exception)
        {
            // A SQLite message and a filesystem message both name paths, thus the detail reaches the log
            // only and the agent gets fixed text.
            LogFailed(logger, BackupToolName, nameof(SidecarError.BackupFailed), exception);
            return SidecarErrors.Failure(
                SidecarError.BackupFailed,
                "The backup did not complete. The operator must check the sidecar log.");
        }
    }

    [McpServerTool(Name = "diagnostics", ReadOnly = true)]
    [Description("Report a small fixed set of health values for the database: the SQLite version, the "
               + "journal mode, the page size, the page count and a quick integrity check. "
               + "Page size multiplied by page count is the size of the database in bytes. "
               + "A journal mode of 'wal' means a sidecar write does not block the readers of the owning "
               + "application; any other mode means it does. "
               + "A quick_check of 'ok' means no structural damage was found. "
               + "The value set is fixed: this tool runs no query and takes no arguments.")]
    [Authorize(Policy = "perm:diagnostics")]
    public async Task<CallToolResult> DiagnosticsAsync(CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetTimestamp();
        using var slot = await database.AcquireRequestSlotAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await database.ReadDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            LogCompleted(logger, "diagnostics", Elapsed(started), "ok");
            return SidecarErrors.Success(
                $"sqliteVersion: {result.SqliteVersion}\n"
              + $"journalMode: {result.JournalMode}\n"
              + $"pageSize: {result.PageSize}\n"
              + $"pageCount: {result.PageCount}\n"
              + $"quickCheck: {result.QuickCheck}");
        }
        catch (OperationCanceledException)
        {
            // quick_check reads every page, thus a large database can reach the query timeout.
            LogCompleted(logger, "diagnostics", Elapsed(started), nameof(SidecarError.QueryTimedOut));
            return SidecarErrors.Failure(
                SidecarError.QueryTimedOut,
                "The integrity check passed the time limit and was stopped. The database is too large "
              + "for this check at the configured timeout.");
        }
        catch (Exception exception)
        {
            LogFailed(logger, "diagnostics", nameof(SidecarError.DatabaseError), exception);
            return SidecarErrors.Failure(SidecarError.DatabaseError, "The diagnostics could not be read.");
        }
    }

    /// <summary>
    /// Runs the part that every write shares: the idempotency check, the budget gate, the two slots,
    /// the execution and the mapping of a failure onto an error code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three structured tools and <c>execute_write_sql</c> all run through this one method. The
    /// order of the controls is a security contract, thus it exists one time and not two times. The
    /// tools differ in three values only: the canonical payload, the execution and the log line, and
    /// <paramref name="logCompleted"/> carries the last one because a structured write logs its table
    /// while a raw write logs a hash of its statement.
    /// </para>
    /// <para>
    /// <b>Invariant.</b> The order is deduplication, then the budget gate, then the request slot, then
    /// the write slot. A replayed response writes no row, thus it consumes no budget and needs no
    /// slot. Shape validation already ran in the tool method, before any of this.
    /// </para>
    /// <para>
    /// <b>Invariant.</b> The budget is charged and the response is cached only after the transaction
    /// committed. A cached failure would make the instruction "retry later" of
    /// <see cref="SidecarError.DatabaseBusy"/> and <see cref="SidecarError.WriteBudgetExceeded"/>
    /// impossible to follow for the whole window.
    /// </para>
    /// </remarks>
    private async Task<CallToolResult> RunWriteAsync<TResult>(
        string tool,
        long started,
        string key,
        string canonicalPayload,
        WriteCompletedLog logCompleted,
        Func<CancellationToken, Task<TResult>> execute,
        Func<TResult, string> format,
        CancellationToken cancellationToken)
        where TResult : IWriteOutcome
    {
        // Deduplication runs before the budget check. A replayed response writes no row, thus it
        // consumes no budget.
        var payloadHash = WriteDeduplication.HashPayload(canonicalPayload);
        switch (deduplication.Check(key, payloadHash, out var stored))
        {
            case DeduplicationOutcome.Replay:
                // Byte-identical to the original answer: a retry must look like the first call.
                logCompleted(Elapsed(started), 0, true, "ok");
                return SidecarErrors.Success(stored!);

            case DeduplicationOutcome.PayloadConflict:
                logCompleted(Elapsed(started), 0, false, nameof(SidecarError.InvalidWrite));
                return SidecarErrors.Failure(
                    SidecarError.InvalidWrite,
                    "This requestId was used for different work. Use a new requestId for new work.");

            case DeduplicationOutcome.InFlight:
                // The same request is already running. Executing it here would apply the write a
                // second time, which is the failure the requestId exists to prevent. DatabaseBusy is
                // the honest code: the work is under way and the retry it asks for is correct.
                logCompleted(Elapsed(started), 0, false, nameof(SidecarError.DatabaseBusy));
                return SidecarErrors.Failure(
                    SidecarError.DatabaseBusy,
                    "This requestId is already running. Wait, then retry with the same requestId.");
        }

        // INVARIANT: the Check above reserved the identifier, thus this method owns it from here and
        // every path that does not commit must release it. A leaked reservation would answer
        // DatabaseBusy for the whole five minute window and make its own "retry" instruction
        // impossible to follow.
        var committed = false;
        try
        {
            if (!writeBudget.HasCapacity())
            {
                logCompleted(Elapsed(started), 0, false, nameof(SidecarError.WriteBudgetExceeded));
                return SidecarErrors.Failure(
                    SidecarError.WriteBudgetExceeded,
                    "The write budget for this minute is used up. Wait, then retry with the same requestId.");
            }

            using var slot = await database.AcquireRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            using var writeSlot = await database.TryAcquireWriteSlotAsync(cancellationToken).ConfigureAwait(false);
            if (writeSlot is null)
            {
                logCompleted(Elapsed(started), 0, false, nameof(SidecarError.DatabaseBusy));
                return SidecarErrors.Failure(SidecarError.DatabaseBusy, ExplanationFor(SidecarError.DatabaseBusy));
            }

            try
            {
                var result = await execute(cancellationToken).ConfigureAwait(false);

                // Committed rows only, and only after the commit. A cascaded row and a row that a trigger
                // wrote are real write volume, thus the budget counts RowsChanged and not RowsAffected.
                writeBudget.Record(result.RowsChanged);

                var text = format(result);
                deduplication.Store(key, payloadHash, text);
                committed = true;

                logCompleted(Elapsed(started), result.RowsAffected, false, "ok");
                return SidecarErrors.Success(text);
            }
            catch (WriteLimitExceededException exception)
            {
                // One code for both checks: nothing changed either way, and the correct next action is the
                // same. The log carries which check fired, because that is operator information.
                LogWriteLimitRejected(logger, tool, exception.Check, exception.Limit);
                logCompleted(Elapsed(started), 0, false, nameof(SidecarError.WriteLimitExceeded));
                return SidecarErrors.Failure(
                    SidecarError.WriteLimitExceeded,
                    $"The write would change more rows than the limit of {exception.Limit}, counting every "
                  + "row that a cascade or a trigger also changes. Nothing changed. Narrow the filter.");
            }
            catch (InvalidWriteException exception)
            {
                // The message names only what the caller already sent. It carries no value and no path.
                logCompleted(Elapsed(started), 0, false, nameof(SidecarError.InvalidWrite));
                return SidecarErrors.Failure(SidecarError.InvalidWrite, exception.Message);
            }
            catch (StatementRejectedException exception)
            {
                // Only execute_write_sql reaches this: a structured write sends no caller SQL. The mapping
                // is the one that the query tool uses, thus one statement kind gives one code everywhere.
                var (code, explanation) = exception.Check switch
                {
                    StatementCheck.MultipleStatements => (SidecarError.InvalidQuery,
                        "The request holds more than one statement. Send exactly one."),
                    StatementCheck.Empty => (SidecarError.InvalidQuery,
                        "The request holds no statement. Send one INSERT, UPDATE or DELETE; whitespace "
                      + "and a comment alone are not a statement."),
                    StatementCheck.Rejected => (SidecarError.QueryRejected,
                        "The requested action is not permitted. Only INSERT, UPDATE and DELETE are available."),
                    // The statement is valid and it belongs to the other tool. Naming that tool is the
                    // whole value of this code: the agent moves the call instead of rewriting the SQL.
                    StatementCheck.ReadOnlyOnAWritePath => (SidecarError.QueryRejected,
                        "The statement changes nothing. This tool runs one INSERT, UPDATE or DELETE. "
                      + "Send a SELECT to the query tool instead."),
                    StatementCheck.Busy => (SidecarError.DatabaseBusy,
                        ExplanationFor(SidecarError.DatabaseBusy)),
                    _ => (SidecarError.InvalidQuery,
                        "The statement did not compile. Check the syntax and the table and column names."),
                };

                logCompleted(Elapsed(started), 0, false, code.ToString());
                return SidecarErrors.Failure(code, explanation);
            }
            catch (SqliteException exception)
            {
                var code = exception.SqliteErrorCode switch
                {
                    SqliteConstraint => SidecarError.InvalidWrite,
                    SqliteAuthorizationDenied => SidecarError.QueryRejected,
                    SqliteInterrupt => SidecarError.QueryTimedOut,
                    SqliteBusy or SqliteLocked => SidecarError.DatabaseBusy,
                    SqliteReadOnly => SidecarError.DatabaseError,
                    _ => SidecarError.DatabaseError,
                };

                LogFailed(logger, tool, code.ToString(), exception);
                return SidecarErrors.Failure(code, ExplanationFor(code));
            }
            catch (OperationCanceledException)
            {
                // Nothing was committed, thus nothing is cached and a retry is correct.
                logCompleted(Elapsed(started), 0, false, nameof(SidecarError.QueryTimedOut));
                return SidecarErrors.Failure(
                    SidecarError.QueryTimedOut,
                    "The write passed the time limit and was stopped. Nothing changed. Retry with the same requestId.");
            }
        }
        finally
        {
            // Every path that did not commit ends the reservation here, the unexpected ones included.
            if (!committed)
            {
                deduplication.Release(key);
            }
        }
    }

    /// <summary>
    /// Validates the two arguments that a bounded structured write adds, and returns them non-null.
    /// </summary>
    /// <remarks>
    /// <b>Invariant.</b> An absent filter, an empty filter and an absent <c>maxRows</c> are each
    /// <see cref="SidecarError.InvalidWrite"/>, and the rejection happens before any database work.
    /// There is no structured equivalent of <c>DELETE FROM jobs;</c>.
    /// </remarks>
    private static (IReadOnlyList<WriteCondition> Filter, int Limit)? ValidateFilter(
        List<WriteCondition>? where,
        int? maxRows,
        out string? explanation)
    {
        explanation = null;

        if (where is null || where.Count == 0)
        {
            explanation = "The where list is required and must hold at least one condition. There is no "
                        + "way to change every row of a table.";
            return null;
        }

        if (maxRows is null)
        {
            explanation = "The maxRows value is required. Give the most rows this call may change, so "
                        + "that a filter that is wider than you expect is rejected instead of applied.";
            return null;
        }

        if (maxRows < 1)
        {
            explanation = $"The maxRows value is {maxRows}. It must be 1 or more.";
            return null;
        }

        return (where, maxRows.Value);
    }

    /// <summary>
    /// Validates the parts that every write tool shares, and returns the cache key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Lesson.</b> Each mandatory argument carries <c>= null</c> in the tool signature and is
    /// validated here. A nullable type alone is not enough: the binder of the SDK treats a parameter
    /// with no default value as required and throws when the argument is absent, and the SDK then
    /// replaces the message with <c>"An error occurred invoking 'insert'."</c>. The agent gets no code
    /// to select from, and the required case "write without requestId -&gt; InvalidWrite" cannot pass.
    /// </para>
    /// <para>
    /// The cost is that the JSON schema marks no argument as required. The tool description carries the
    /// requirement instead. That trade is correct: a description that the agent reads plus an actionable
    /// error beats a schema keyword plus an opaque failure. The error model must own every failure that
    /// an agent can cause.
    /// </para>
    /// </remarks>
    private static string? ValidateWriteRequest(string? requestId, string? table, out string? outcome)
    {
        if (ValidateRequestId(requestId, out outcome) is not { } key)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(table))
        {
            outcome = MissingTable;
            return null;
        }

        return key;
    }

    /// <summary>
    /// Validates the one argument that every write tool shares, and returns the cache key.
    /// </summary>
    /// <remarks>
    /// <c>execute_write_sql</c> names no table, thus it calls this method and not
    /// <see cref="ValidateWriteRequest"/>. The <c>= null</c> lesson above applies to it unchanged.
    /// </remarks>
    private static string? ValidateRequestId(string? requestId, out string? outcome)
    {
        outcome = null;

        if (string.IsNullOrWhiteSpace(requestId))
        {
            outcome = MissingRequestId;
            return null;
        }

        if (requestId.Length > WriteDeduplication.MaxKeyLength)
        {
            outcome = RequestIdTooLong;
            return null;
        }

        return requestId;
    }

    private const string MissingRequestId = "InvalidWrite.MissingRequestId";
    private const string RequestIdTooLong = "InvalidWrite.RequestIdTooLong";
    private const string MissingTable = "InvalidWrite.MissingTable";

    private static string ExplanationForWrite(string outcome) => outcome switch
    {
        MissingRequestId => "The requestId value is required. Send a unique identifier, and the same one when you retry.",
        RequestIdTooLong => $"The requestId value is longer than {WriteDeduplication.MaxKeyLength} characters. Use a shorter one.",
        _ => "The table value is required. Name a table from the schema tool.",
    };

    /// <summary>SQLite primary result codes that the query path maps to an error code.</summary>
    private const int SqliteError = 1;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteReadOnly = 8;
    private const int SqliteInterrupt = 9;
    private const int SqliteTooBig = 18;
    private const int SqliteConstraint = 19;
    private const int SqliteAuthorizationDenied = 23;

    private static string ExplanationFor(SidecarError code) => code switch
    {
        SidecarError.QueryRejected => "The requested action is not permitted. Only read statements are available.",
        SidecarError.QueryTimedOut => "The query passed the time limit and was stopped. Make the query smaller.",
        SidecarError.DatabaseBusy => "Another writer holds the lock. Retry later with the same requestId.",
        SidecarError.InvalidQuery => "The statement did not run. Check the syntax, the names and the statement size.",
        // A raw SQLite constraint message names the database file, thus the agent gets fixed text. The
        // detail is in the log.
        SidecarError.InvalidWrite => "The row broke a constraint of the table: a unique key, a check or a "
                                   + "foreign key. Read the schema and correct the values.",
        _ => "The operation could not be run.",
    };

    private static double Elapsed(long started) =>
        TimeProvider.System.GetElapsedTime(started).TotalMilliseconds;

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "Tool completed. tool={Tool} durationMs={DurationMs} outcome={Outcome}")]
    private static partial void LogCompleted(ILogger logger, string tool, double durationMs, string outcome);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information,
        Message = "Tool completed. tool={Tool} durationMs={DurationMs} rowsReturned={RowsReturned} "
                + "resultBytes={ResultBytes} outcome={Outcome}")]
    private static partial void LogQueryCompleted(
        ILogger logger, string tool, double durationMs, int rowsReturned, int resultBytes, string outcome);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error,
        Message = "Tool failed. tool={Tool} outcome={Outcome}")]
    private static partial void LogFailed(ILogger logger, string tool, string outcome, Exception exception);

    /// <summary>
    /// The write log line. It carries the table name, never a column value.
    /// </summary>
    /// <remarks>
    /// <c>replayed=true</c> marks an answer that came from the idempotency cache and wrote nothing. It
    /// is the only record of a retry, because the agent gets the original response unchanged.
    /// </remarks>
    [LoggerMessage(EventId = 2004, Level = LogLevel.Information,
        Message = "Write completed. tool={Tool} durationMs={DurationMs} table={Table} "
                + "rowsAffected={RowsAffected} replayed={Replayed} outcome={Outcome}")]
    private static partial void LogWriteCompleted(
        ILogger logger, string tool, double durationMs, string table, int rowsAffected, bool replayed, string outcome);

    /// <summary>
    /// The record of a write that the row limit rejected.
    /// </summary>
    /// <remarks>
    /// <c>check=pre</c> means that the bounded pre-count rejected the filter before anything ran.
    /// <c>check=post</c> means that the statement changed more rows than the limit and rolled back,
    /// which happens when a cascade or a trigger widened the write, or when the owning application
    /// changed the data between the count and the write. The agent cannot tell the two apart, thus
    /// this line is the only evidence an operator has.
    /// </remarks>
    [LoggerMessage(EventId = 2005, Level = LogLevel.Information,
        Message = "Write rejected by the row limit. tool={Tool} check={Check} limit={Limit}")]
    private static partial void LogWriteLimitRejected(
        ILogger logger, string tool, string check, int limit);

    /// <summary>
    /// The raw write log line. It carries a hash of the statement and never the statement itself.
    /// </summary>
    /// <remarks>
    /// A raw statement holds caller-chosen literal values, thus the text is row data and it must not
    /// reach a log pipeline. The hash still correlates the calls of one retry, which is what an operator
    /// needs. The line has no table field: the statement names the tables and the server does not parse
    /// it.
    /// </remarks>
    [LoggerMessage(EventId = 2006, Level = LogLevel.Information,
        Message = "Raw write completed. tool={Tool} durationMs={DurationMs} sqlHash={SqlHash} "
                + "rowsAffected={RowsAffected} replayed={Replayed} outcome={Outcome}")]
    private static partial void LogRawWriteCompleted(
        ILogger logger, string tool, double durationMs, string sqlHash, int rowsAffected, bool replayed, string outcome);

    /// <summary>
    /// The completion log line of one write, supplied by the tool that runs.
    /// </summary>
    /// <remarks>
    /// A structured write logs its table and a raw write logs a hash of its statement. One shared
    /// runner with this delegate keeps the order of the controls in one place and keeps each log line
    /// honest: a field named <c>table</c> that carried a hash would be worse than two messages.
    /// </remarks>
    private delegate void WriteCompletedLog(double durationMs, int rowsAffected, bool replayed, string outcome);
}
