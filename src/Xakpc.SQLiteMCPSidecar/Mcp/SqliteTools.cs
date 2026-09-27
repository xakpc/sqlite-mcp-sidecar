using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xakpc.SQLiteMCPSidecar.Database;
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
        [Description("One read-only SQL statement, with no trailing second statement. "
                   + "Write literal values into the statement; there is no parameter list.")]
        string sql,
        CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetTimestamp();
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
                    "The request holds no statement."),
                StatementCheck.Rejected => (SidecarError.QueryRejected,
                    "The requested action is not permitted. Only read statements are available."),
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
        [Description("A unique identifier for this write, at most 128 characters. Send the SAME value "
                   + "when you retry a call that failed or timed out: the row is then added one time "
                   + "only. Use a NEW value for new work.")]
        string? requestId = null,
        [Description("The table to add the row to. It must be a table from the schema tool, not a view.")]
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

        // Deduplication runs before the budget check. A replayed response writes no row, thus it
        // consumes no budget.
        var payloadHash = WriteDeduplication.HashPayload(StructuredWriteBuilder.CanonicalInsertPayload(table!, values));
        switch (deduplication.Check(key, payloadHash, out var stored))
        {
            case DeduplicationOutcome.Replay:
                // Byte-identical to the original answer: a retry must look like the first call.
                LogWriteCompleted(logger, "insert", Elapsed(started), table!, 0, true, "ok");
                return SidecarErrors.Success(stored!);

            case DeduplicationOutcome.PayloadConflict:
                LogWriteCompleted(logger, "insert", Elapsed(started), table!, 0, false, nameof(SidecarError.InvalidWrite));
                return SidecarErrors.Failure(
                    SidecarError.InvalidWrite,
                    "This requestId was used for a different row. Use a new requestId for new work.");
        }

        if (!writeBudget.HasCapacity())
        {
            LogWriteCompleted(logger, "insert", Elapsed(started), table!, 0, false, nameof(SidecarError.WriteBudgetExceeded));
            return SidecarErrors.Failure(
                SidecarError.WriteBudgetExceeded,
                "The write budget for this minute is used up. Wait, then retry with the same requestId.");
        }

        using var slot = await database.AcquireRequestSlotAsync(cancellationToken).ConfigureAwait(false);
        using var writeSlot = await database.TryAcquireWriteSlotAsync(cancellationToken).ConfigureAwait(false);
        if (writeSlot is null)
        {
            LogWriteCompleted(logger, "insert", Elapsed(started), table!, 0, false, nameof(SidecarError.DatabaseBusy));
            return SidecarErrors.Failure(SidecarError.DatabaseBusy, ExplanationFor(SidecarError.DatabaseBusy));
        }

        try
        {
            var result = await database.InsertAsync(table!, values, cancellationToken).ConfigureAwait(false);

            // Committed rows only, and only after the commit.
            writeBudget.Record(result.RowsAffected);

            var text = $"rowsAffected: {result.RowsAffected}\nrowid: {result.RowId}";
            deduplication.Store(key, payloadHash, text);

            LogWriteCompleted(logger, "insert", Elapsed(started), table!, result.RowsAffected, false, "ok");
            return SidecarErrors.Success(text);
        }
        catch (InvalidWriteException exception)
        {
            // The message names only what the caller already sent. It carries no value and no path.
            LogWriteCompleted(logger, "insert", Elapsed(started), table!, 0, false, nameof(SidecarError.InvalidWrite));
            return SidecarErrors.Failure(SidecarError.InvalidWrite, exception.Message);
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

            LogFailed(logger, "insert", code.ToString(), exception);
            return SidecarErrors.Failure(code, ExplanationFor(code));
        }
        catch (OperationCanceledException)
        {
            // Nothing was committed, thus nothing is cached and a retry is correct.
            LogWriteCompleted(logger, "insert", Elapsed(started), table!, 0, false, nameof(SidecarError.QueryTimedOut));
            return SidecarErrors.Failure(
                SidecarError.QueryTimedOut,
                "The write passed the time limit and was stopped. Nothing changed. Retry with the same requestId.");
        }
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

        if (string.IsNullOrWhiteSpace(table))
        {
            outcome = MissingTable;
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
}
