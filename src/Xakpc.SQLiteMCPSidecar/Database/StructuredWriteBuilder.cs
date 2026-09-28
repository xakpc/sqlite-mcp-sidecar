using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xakpc.SQLiteMCPSidecar.Exceptions;

namespace Xakpc.SQLiteMCPSidecar.Database;

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
/// One condition of a structured filter. The filter is a flat list and never a tree, and every
/// condition joins with <c>AND</c>.
/// </summary>
/// <param name="Column">A column of the target table, in any spelling.</param>
/// <param name="Operator">One of <c>eq ne lt lte gt gte is-null is-not-null</c>.</param>
/// <param name="Value">The literal to compare with. <c>is-null</c> and <c>is-not-null</c> take none.</param>
/// <remarks>
/// <para>
/// <b>Lesson.</b> <see cref="Operator"/> is a string and not an enum. An enum makes the binder of the
/// SDK throw on an unknown value, and the SDK then replaces the message with its own fixed text, thus
/// the agent gets no error code to select from. The operator set is named in the tool description and
/// in the rejection message instead. See <c>.lode/mcp/error-model.md</c>.
/// </para>
/// <para>
/// Every member is nullable for the same reason: the deserializer of the SDK leaves an absent JSON
/// member as <c>null</c> whatever the declared type says, thus the validation lives in the builder and
/// reaches the error model.
/// </para>
/// </remarks>
public sealed record WriteCondition(string? Column, string? Operator, JsonElement? Value = null);

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
    /// reason the structured tool exists: see <c>.lode/database/structured-writes.md</c>.
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

    /// <summary>
    /// Builds <c>UPDATE "t" SET "a" = $p0 WHERE "b" = $p1</c>.
    /// </summary>
    /// <remarks>
    /// The <c>SET</c> parameters and the <c>WHERE</c> parameters share one ordered list, thus the
    /// numbering continues across the two clauses.
    /// </remarks>
    public static StructuredStatement BuildUpdate(
        TableSchema schema,
        IReadOnlyDictionary<string, JsonElement> values,
        IReadOnlyList<WriteCondition> where)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(where);

        if (values.Count == 0)
        {
            throw new InvalidWriteException("The values object is empty. Name at least one column and its new value.");
        }

        GuardFilter(where);

        var parameters = new List<object?>(values.Count + where.Count);
        var sql = new StringBuilder("UPDATE ").Append(Quote(schema.Name)).Append(" SET ");

        var first = true;
        foreach (var (column, value) in values)
        {
            if (!first)
            {
                sql.Append(", ");
            }

            first = false;
            sql.Append(Quote(schema.CanonicalColumn(column))).Append(" = $p").Append(parameters.Count);
            parameters.Add(ToSqliteValue(column, value));
        }

        AppendWhere(sql, schema, where, parameters);
        sql.Append(';');

        GuardParameterCount(parameters.Count);
        return new StructuredStatement(sql.ToString(), parameters);
    }

    /// <summary>Builds <c>DELETE FROM "t" WHERE "a" = $p0</c>.</summary>
    public static StructuredStatement BuildDelete(TableSchema schema, IReadOnlyList<WriteCondition> where)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(where);

        GuardFilter(where);

        var parameters = new List<object?>(where.Count);
        var sql = new StringBuilder("DELETE FROM ").Append(Quote(schema.Name));

        AppendWhere(sql, schema, where, parameters);
        sql.Append(';');

        GuardParameterCount(parameters.Count);
        return new StructuredStatement(sql.ToString(), parameters);
    }

    /// <summary>
    /// Builds the bounded pre-count, <c>SELECT COUNT(*) FROM (SELECT 1 FROM "t" WHERE ... LIMIT N)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subquery carries the <c>LIMIT</c>, thus SQLite stops after <c>N</c> rows and a filter that
    /// matches a million rows costs the same as one that matches <c>N</c>. The exact count is therefore
    /// not available, which is why the rejection message names the limit and not the count.
    /// </para>
    /// <para>
    /// The limit is a server-computed integer and never a caller string, thus it goes into the text
    /// directly. Every value of the filter stays a parameter.
    /// </para>
    /// </remarks>
    public static StructuredStatement BuildPreCount(
        TableSchema schema,
        IReadOnlyList<WriteCondition> where,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(where);

        GuardFilter(where);

        var parameters = new List<object?>(where.Count);
        var sql = new StringBuilder("SELECT COUNT(*) FROM (SELECT 1 FROM ").Append(Quote(schema.Name));

        AppendWhere(sql, schema, where, parameters);
        sql.Append(" LIMIT ").Append(limit).Append(");");

        return new StructuredStatement(sql.ToString(), parameters);
    }

    /// <summary>
    /// Writes the <c>WHERE</c> clause and appends the bound values to the ordered parameter list.
    /// </summary>
    /// <remarks>
    /// <b>Invariant.</b> Every condition joins with <c>AND</c>. There is no <c>or</c> and no nesting:
    /// one call therefore has one filter with one blast radius, and a disjunction is two calls that the
    /// pre-count bounds separately. See <c>.lode/decisions/0005-and-only-filter.md</c>.
    /// </remarks>
    private static void AppendWhere(
        StringBuilder sql,
        TableSchema schema,
        IReadOnlyList<WriteCondition> where,
        List<object?> parameters)
    {
        sql.Append(" WHERE ");

        for (var index = 0; index < where.Count; index++)
        {
            if (index > 0)
            {
                sql.Append(" AND ");
            }

            var condition = where[index]
                ?? throw new InvalidWriteException(
                    $"Condition {index} of where is empty. Each condition needs a column and an operator.");

            if (string.IsNullOrWhiteSpace(condition.Column))
            {
                throw new InvalidWriteException(
                    $"Condition {index} of where names no column. Call the schema tool and name a column of the table.");
            }

            var column = Quote(schema.CanonicalColumn(condition.Column));
            var op = condition.Operator?.Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(op))
            {
                throw new InvalidWriteException(
                    $"Condition {index} of where names no operator. Use one of: {OperatorList}.");
            }

            if (op is IsNullOperator or IsNotNullOperator)
            {
                // A JSON null reads as "no value" and is accepted. A real value means that the agent
                // expected a comparison, thus it is a misunderstanding and not a harmless extra.
                if (condition.Value is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null })
                {
                    throw new InvalidWriteException(
                        $"The '{op}' operator of condition {index} takes no value. Remove the value, "
                      + "or use 'eq' to compare with one.");
                }

                sql.Append(column).Append(op == IsNullOperator ? " IS NULL" : " IS NOT NULL");
                continue;
            }

            var comparison = ComparisonOperator(op)
                ?? throw new InvalidWriteException(
                    $"Condition {index} of where uses the unknown operator '{condition.Operator}'. "
                  + $"Use one of: {OperatorList}.");

            if (condition.Value is not { } value || value.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidWriteException(
                    $"The '{op}' operator of condition {index} needs a value. Add one, or use "
                  + "'is-null' to test for an absent value.");
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                // "col = NULL" is never true in SQL. A filter that silently matches nothing is worse
                // for an agent than a rejection: the write reports rowsAffected 0 and looks correct.
                throw new InvalidWriteException(
                    $"Condition {index} of where compares '{condition.Column}' with null, which never "
                  + "matches any row. Use the 'is-null' operator instead.");
            }

            sql.Append(column).Append(' ').Append(comparison).Append(" $p").Append(parameters.Count);
            parameters.Add(ToSqliteValue(condition.Column, value));
        }
    }

    /// <summary>
    /// Rejects a filter that would make the statement apply to the whole table.
    /// </summary>
    /// <remarks>
    /// <b>Invariant.</b> There is no structured equivalent of <c>DELETE FROM jobs;</c>. The rejection
    /// happens before any database work, thus a broad request never takes the write lock.
    /// </remarks>
    private static void GuardFilter(IReadOnlyList<WriteCondition> where)
    {
        if (where.Count == 0)
        {
            throw new InvalidWriteException(
                "The where list is empty. A structured update or delete always needs at least one "
              + "condition: there is no way to change every row of a table.");
        }
    }

    private const string IsNullOperator = "is-null";
    private const string IsNotNullOperator = "is-not-null";

    private const string OperatorList = "eq, ne, lt, lte, gt, gte, is-null, is-not-null";

    /// <summary>Maps one filter operator onto its SQL spelling, or <c>null</c> when it is unknown.</summary>
    private static string? ComparisonOperator(string op) => op switch
    {
        "eq" => "=",
        "ne" => "<>",
        "lt" => "<",
        "lte" => "<=",
        "gt" => ">",
        "gte" => ">=",
        _ => null,
    };

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
        AppendCanonicalValues(builder, values);
        return builder.ToString();
    }

    /// <summary>The canonical text of an <c>update</c> request, for the idempotency payload hash.</summary>
    public static string CanonicalUpdatePayload(
        string table,
        IReadOnlyDictionary<string, JsonElement> values,
        IReadOnlyList<WriteCondition> where,
        int maxRows)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(where);

        var builder = new StringBuilder("update\n").Append(table.ToLowerInvariant()).Append('\n');
        AppendCanonicalValues(builder, values);
        AppendCanonicalFilter(builder, where);
        builder.Append("maxrows=").Append(maxRows).Append('\n');
        return builder.ToString();
    }

    /// <summary>The canonical text of a <c>delete</c> request, for the idempotency payload hash.</summary>
    public static string CanonicalDeletePayload(
        string table,
        IReadOnlyList<WriteCondition> where,
        int maxRows)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(where);

        var builder = new StringBuilder("delete\n").Append(table.ToLowerInvariant()).Append('\n');
        AppendCanonicalFilter(builder, where);
        builder.Append("maxrows=").Append(maxRows).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// Writes the values of a request in a stable order.
    /// </summary>
    /// <remarks>
    /// The column order of a JSON object is not significant, thus the keys are ordered here. Without
    /// the ordering the same request with a different key order would read as different work and
    /// return <c>InvalidWrite</c> on a correct retry.
    /// </remarks>
    private static void AppendCanonicalValues(StringBuilder builder, IReadOnlyDictionary<string, JsonElement> values)
    {
        foreach (var column in values.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(column.ToLowerInvariant()).Append('=').Append(values[column].GetRawText()).Append('\n');
        }
    }

    /// <summary>
    /// Writes the filter of a request.
    /// </summary>
    /// <remarks>
    /// The order of the list stays. A client library reorders the members of a JSON object, which is
    /// why <see cref="AppendCanonicalValues"/> sorts, but it never reorders the items of an array.
    /// </remarks>
    private static void AppendCanonicalFilter(StringBuilder builder, IReadOnlyList<WriteCondition> where)
    {
        foreach (var condition in where)
        {
            builder.Append("where ")
                .Append(condition?.Column?.ToLowerInvariant())
                .Append(' ')
                .Append(condition?.Operator?.ToLowerInvariant())
                .Append(' ')
                .Append(condition?.Value?.GetRawText() ?? "-")
                .Append('\n');
        }
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
