using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Xakpc.SQLiteMCPSidecar.Database;

/// <summary>
/// The request shape is wrong. The message reaches the agent, thus it names only what the caller
/// already sent and it never carries a value, a path or a SQLite message.
/// </summary>
internal sealed class InvalidWriteException(string reason) : Exception(reason);

/// <summary>
/// The columns of one table, as the live database spells them.
/// </summary>
/// <param name="Name">The canonical table name, from <c>sqlite_master</c>.</param>
/// <param name="Columns">The canonical column names, from <c>PRAGMA table_info</c>.</param>
internal sealed record TableSchema(string Name, IReadOnlyList<string> Columns)
{
    /// <summary>
    /// Returns the canonical spelling of one column, or throws when the table has no such column.
    /// </summary>
    /// <remarks>
    /// SQLite identifiers are not case-sensitive, thus the comparison ignores case and the result is
    /// the spelling that the database holds. The caller string never reaches the SQL text.
    /// </remarks>
    public string CanonicalColumn(string column)
    {
        foreach (var candidate in Columns)
        {
            if (string.Equals(candidate, column, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new InvalidWriteException(
            $"The table '{Name}' has no column '{column}'. Call the schema tool and use a column of that table.");
    }
}

/// <summary>One server-authored statement and its ordered parameter values.</summary>
internal sealed record StructuredStatement(string Sql, IReadOnlyList<object?> Parameters);

/// <summary>
/// Builds the parameterized statement of a structured write. The caller never supplies SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant.</b> Validation is the security control and quoting is the correctness control. A
/// table name and a column name are validated against the live schema, then the canonical spelling
/// from the database is quoted into the text. A caller string is never concatenated into SQL.
/// </para>
/// <para>
/// <b>Invariant.</b> <see cref="ReadTableSchemaAsync"/> runs a <c>PRAGMA</c>, and every authorizer
/// policy denies <c>PRAGMA</c>. It must therefore run before the authorizer is installed, which is
/// also before <c>BEGIN IMMEDIATE</c>. See <see cref="SqliteSecurity"/>.
/// </para>
/// <para>
/// The schema is never cached. The owning application can change it, and one extra read for each
/// write is cheap next to the write itself.
/// </para>
/// </remarks>
internal static class StructuredWriteBuilder
{
    /// <summary>
    /// The parameter cap of one request. The sandbox sets <c>SQLITE_LIMIT_VARIABLE_NUMBER</c> to 100,
    /// thus a larger request fails at preparation with a message that helps the agent less than this
    /// one does.
    /// </summary>
    public const int MaxParameters = 90;

    /// <summary>
    /// Reads the canonical name and the column list of a write target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target must be a real table. A view is excluded because an <c>INSERT</c> into a view needs
    /// an <c>INSTEAD OF</c> trigger and the SQLite failure would tell the agent much less than the
    /// rejection does. An <c>sqlite_%</c> object is excluded because it is SQLite internal state.
    /// </para>
    /// <para>
    /// The lookup does not filter on the type, thus the message can name the real reason. "The database
    /// has no table X" is wrong and misleading when X is a view that does exist.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidWriteException">No such name, or the name is not a writable table.</exception>
    public static async Task<TableSchema> ReadTableSchemaAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);

        string canonicalName;
        string objectType;
        await using (var lookup = connection.CreateCommand())
        {
            // The caller string is a parameter and never part of the text. COLLATE NOCASE, because
            // sqlite_master compares with BINARY by default and SQLite names are not case-sensitive.
            lookup.CommandText =
                """
                SELECT name, type FROM sqlite_master WHERE name = $table COLLATE NOCASE LIMIT 1;
                """;
            lookup.Parameters.AddWithValue("$table", table);

            await using var lookupReader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await lookupReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidWriteException(
                    $"The database has no object named '{table}'. Call the schema tool and use a table name from it.");
            }

            canonicalName = lookupReader.GetString(0);
            objectType = lookupReader.GetString(1);
        }

        if (!string.Equals(objectType, "table", StringComparison.Ordinal))
        {
            throw new InvalidWriteException(
                $"'{canonicalName}' is a {objectType} and not a table. A write needs a table. "
              + "Call the schema tool and write to the table behind it.");
        }

        if (canonicalName.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidWriteException(
                $"'{canonicalName}' is internal SQLite state and is never a write target.");
        }

        var columns = new List<string>();
        await using (var info = connection.CreateCommand())
        {
            // PRAGMA table_info takes no parameter. The name comes from sqlite_master and not from the
            // caller, thus quoting it is safe.
            info.CommandText = $"PRAGMA table_info({Quote(canonicalName)});";
            await using var reader = await info.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (columns.Count == 0)
        {
            throw new InvalidWriteException($"The table '{canonicalName}' reports no columns, thus the sidecar cannot write to it.");
        }

        return new TableSchema(canonicalName, columns);
    }

    /// <summary>
    /// Builds <c>INSERT INTO "t" ("a", "b") VALUES ($p0, $p1)</c>.
    /// </summary>
    /// <remarks>
    /// One call adds exactly one row. That bound needs no check at run time, because the statement
    /// shape cannot express a second row and it cannot express <c>INSERT ... SELECT</c>. This is the
    /// reason the structured tool exists: see <c>.lode/plans/design/structured-writes.md</c>.
    /// </remarks>
    public static StructuredStatement BuildInsert(
        TableSchema schema,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            throw new InvalidWriteException("The values object is empty. Name at least one column and its value.");
        }

        GuardParameterCount(values.Count);

        var columns = new StringBuilder();
        var placeholders = new StringBuilder();
        var parameters = new List<object?>(values.Count);

        foreach (var (column, value) in values)
        {
            if (parameters.Count > 0)
            {
                columns.Append(", ");
                placeholders.Append(", ");
            }

            columns.Append(Quote(schema.CanonicalColumn(column)));
            placeholders.Append("$p").Append(parameters.Count);
            parameters.Add(ToSqliteValue(column, value));
        }

        var sql = $"INSERT INTO {Quote(schema.Name)} ({columns}) VALUES ({placeholders});";
        return new StructuredStatement(sql, parameters);
    }

    /// <summary>Binds the ordered parameters of a built statement onto a command.</summary>
    public static void Bind(SqliteCommand command, StructuredStatement statement)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(statement);

        command.CommandText = statement.Sql;
        for (var i = 0; i < statement.Parameters.Count; i++)
        {
            command.Parameters.AddWithValue($"$p{i}", statement.Parameters[i] ?? DBNull.Value);
        }
    }

    /// <summary>
    /// The canonical text of an <c>insert</c> request, for the idempotency payload hash.
    /// </summary>
    /// <remarks>
    /// The column order of a JSON object is not significant, thus the keys are ordered here. Without
    /// the ordering the same request with a different key order would read as different work and
    /// return <c>InvalidWrite</c> on a retry.
    /// </remarks>
    public static string CanonicalInsertPayload(string table, IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(values);

        var builder = new StringBuilder("insert\n").Append(table.ToLowerInvariant()).Append('\n');
        foreach (var column in values.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(column.ToLowerInvariant()).Append('=').Append(values[column].GetRawText()).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Rejects a request that would pass the SQLite parameter limit.</summary>
    public static void GuardParameterCount(int count)
    {
        if (count > MaxParameters)
        {
            throw new InvalidWriteException(
                $"The request needs {count} parameters and the limit is {MaxParameters}. Use fewer columns or fewer conditions.");
        }
    }

    /// <summary>
    /// Converts one JSON value into a value that <c>Microsoft.Data.Sqlite</c> binds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A value is a literal and never a SQL expression. An agent that needs a computed value omits the
    /// column and lets the column default apply, or it computes the value itself.
    /// </para>
    /// <para>
    /// An object and an array have no SQLite equivalent. Reject them instead of storing a JSON string,
    /// because a silent conversion writes a value that the agent did not ask for.
    /// </para>
    /// </remarks>
    private static object? ToSqliteValue(string column, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDouble(),
        _ => throw new InvalidWriteException(
            $"The value of '{column}' is an object or an array. A value must be a string, a number, a boolean or null."),
    };

    /// <summary>
    /// Quotes one identifier. The name comes from the live schema, and doubling the quote character
    /// keeps a name that itself holds a quote correct.
    /// </summary>
    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
