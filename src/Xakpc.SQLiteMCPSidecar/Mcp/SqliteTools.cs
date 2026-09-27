using System.ComponentModel;
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
public sealed partial class SqliteTools(SqliteService database, ILogger<SqliteTools> logger)
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

    /// <summary>SQLite primary result codes that the query path maps to an error code.</summary>
    private const int SqliteError = 1;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteInterrupt = 9;
    private const int SqliteTooBig = 18;
    private const int SqliteAuthorizationDenied = 23;

    private static string ExplanationFor(SidecarError code) => code switch
    {
        SidecarError.QueryRejected => "The requested action is not permitted. Only read statements are available.",
        SidecarError.QueryTimedOut => "The query passed the time limit and was stopped. Make the query smaller.",
        SidecarError.DatabaseBusy => "Another writer holds the lock. Retry later.",
        SidecarError.InvalidQuery => "The statement did not run. Check the syntax, the names and the statement size.",
        _ => "The query could not be run.",
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
}
