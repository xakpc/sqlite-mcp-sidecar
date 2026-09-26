using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
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
    public async Task<string> SchemaAsync(CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetTimestamp();
        using var slot = await database.AcquireRequestSlotAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var ddl = await database.ReadSchemaDdlAsync(cancellationToken).ConfigureAwait(false);
            LogCompleted(logger, "schema", TimeProvider.System.GetElapsedTime(started).TotalMilliseconds, "ok");
            return ddl;
        }
        catch (OperationCanceledException)
        {
            LogCompleted(logger, "schema", TimeProvider.System.GetElapsedTime(started).TotalMilliseconds, nameof(SidecarError.QueryTimedOut));
            throw SidecarErrors.Fail(SidecarError.QueryTimedOut, "The schema read was cancelled.");
        }
        catch (Exception exception)
        {
            // The detail reaches the log only. The caller gets the code and a fixed explanation.
            LogFailed(logger, "schema", nameof(SidecarError.DatabaseError), exception);
            throw SidecarErrors.Fail(SidecarError.DatabaseError, "The schema could not be read.");
        }
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information,
        Message = "Tool completed. tool={Tool} durationMs={DurationMs} outcome={Outcome}")]
    private static partial void LogCompleted(ILogger logger, string tool, double durationMs, string outcome);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error,
        Message = "Tool failed. tool={Tool} outcome={Outcome}")]
    private static partial void LogFailed(ILogger logger, string tool, string outcome, Exception exception);
}
