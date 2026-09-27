using System.Data;
using System.Text;
using Microsoft.Data.Sqlite;
using ToonFormat;
using Xakpc.SQLiteMCPSidecar.Configuration;

namespace Xakpc.SQLiteMCPSidecar.Database;

/// <summary>
/// A bounded, serialized read result. Row data goes to the agent as TOON, because TOON costs fewer
/// tokens than JSON for row data.
/// </summary>
/// <param name="Text">The TOON document, with the <c>truncated</c> flag.</param>
/// <param name="RowCount">The rows that the result holds, after any truncation.</param>
/// <param name="ByteCount">The serialized size of <paramref name="Text"/> in UTF-8 bytes.</param>
/// <param name="Truncated">True when the sidecar stopped at the row limit or the byte limit.</param>
/// <param name="RowTooLarge">
/// True when one single row is larger than the whole byte budget and no row fits. The caller turns
/// this into <c>ResultTooLarge</c>. It is the one cause of that code, because the sidecar cannot
/// send a part of a row.
/// </param>
public sealed record QueryResult(string Text, int RowCount, int ByteCount, bool Truncated, bool RowTooLarge)
{
    /// <summary>
    /// Reads the rows within the row limit and the byte limit, then serializes them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Invariant.</b> Apply the limits during the read of the rows and not after it. The result
    /// is buffered before serialization, and buffering is acceptable exactly because the bound
    /// exists first.
    /// </para>
    /// <para>
    /// <b>Invariant.</b> The row limit and the byte limit both truncate. Return the rows that fit
    /// and set the flag. A partial answer with an honest flag is more useful to an agent than an
    /// error.
    /// </para>
    /// </remarks>
    public static async Task<QueryResult> ReadAsync(
        SqliteDataReader reader,
        SidecarOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);

        var table = CreateTable(reader);
        var truncated = false;
        var estimate = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object[reader.FieldCount];
            var rowBytes = 0;
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = Convert(reader.GetValue(i));
                rowBytes += EstimateBytes(values[i]) + 1;
            }

            if (table.Rows.Count >= options.MaxRows)
            {
                truncated = true;
                break;
            }

            if (estimate + rowBytes > options.MaxResultBytes)
            {
                if (table.Rows.Count == 0)
                {
                    // No partial answer is possible: the sidecar cannot send a part of a row.
                    return new QueryResult(string.Empty, 0, 0, Truncated: false, RowTooLarge: true);
                }

                truncated = true;
                break;
            }

            estimate += rowBytes;
            table.Rows.Add(values);
        }

        var text = Serialize(table, truncated);
        return new QueryResult(text, table.Rows.Count, Encoding.UTF8.GetByteCount(text), truncated, RowTooLarge: false);
    }

    /// <summary>
    /// Serializes the buffered rows. The <c>Toon.Encode</c> overload for <see cref="DataTable"/> is
    /// deliberate: the <c>object</c> overload reflects over the argument type, which is a NativeAOT
    /// hazard.
    /// </summary>
    /// <remarks>
    /// That overload writes a root-level tabular array, <c>[2]{id,status}:</c>. The product format
    /// names the array, <c>rows[2]{id,status}:</c>, thus the key goes in front of the encoded text.
    /// A key and a tabular array on one line is the same TOON shape, and the alternative is the
    /// reflecting overload with a wrapper object.
    /// </remarks>
    private static string Serialize(DataTable table, bool truncated)
    {
        var encoded = Toon.Encode(table, new EncodeOptions());

        // A literal newline and not Environment.NewLine: the serializer writes "\n", thus the whole
        // document keeps one line ending on Windows and on Linux.
        // The flag is always present, thus an agent never has to calculate completeness.
        return $"rows{encoded}\n\ntruncated: {(truncated ? "true" : "false")}";
    }

    /// <summary>
    /// Builds the buffer. Every column is <see cref="object"/>, because SQLite is dynamically typed
    /// and one column can hold a different type in each row.
    /// </summary>
    private static DataTable CreateTable(SqliteDataReader reader)
    {
        var table = new DataTable("rows");
        for (var i = 0; i < reader.FieldCount; i++)
        {
            table.Columns.Add(UniqueColumnName(table, reader.GetName(i), i), typeof(object));
        }

        return table;
    }

    /// <summary>
    /// Makes a column name that the buffer accepts. A join returns a repeated name, for example
    /// <c>id</c> from two tables, and a repeated name is a caller mistake to report and not a
    /// failure.
    /// </summary>
    private static string UniqueColumnName(DataTable table, string name, int ordinal)
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? $"column{ordinal + 1}" : name;
        if (!table.Columns.Contains(candidate))
        {
            return candidate;
        }

        for (var suffix = 2; ; suffix++)
        {
            var next = $"{candidate}_{suffix}";
            if (!table.Columns.Contains(next))
            {
                return next;
            }
        }
    }

    /// <summary>
    /// Converts one SQLite value into a value that the serializer writes usefully.
    /// </summary>
    /// <remarks>
    /// A BLOB becomes a placeholder that states its size. The serializer would otherwise write the
    /// type name, and the raw bytes have no use inside an agent context: they cost tokens, they do
    /// not survive the text format, and a large one empties the context window.
    /// </remarks>
    private static object Convert(object value) => value switch
    {
        byte[] blob => $"<blob: {blob.Length} bytes>",
        _ => value,
    };

    /// <summary>
    /// Estimates the serialized size of one value. The estimate runs during the read, thus the byte
    /// budget stops a large result before the sidecar buffers it.
    /// </summary>
    private static int EstimateBytes(object value) => value switch
    {
        DBNull => 4,
        string text => Encoding.UTF8.GetByteCount(text) + 2,
        long or int => 20,
        double => 24,
        _ => 32,
    };
}
