using Microsoft.Data.Sqlite;
using Xakpc.SQLiteMCPSidecar.Configuration;

namespace Xakpc.SQLiteMCPSidecar.Database;

/// <summary>
/// Opens connections to the one database of the deployment and runs the server-authored
/// statements. The sidecar opens a connection with the least privilege that the operation needs.
/// </summary>
public sealed class SqliteService : IDisposable
{
    private readonly SidecarOptions _options;
    private readonly SemaphoreSlim _requestSlots;

    public SqliteService(SidecarOptions options)
    {
        _options = options;
        _requestSlots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
    }

    /// <summary>
    /// Bounds the number of concurrent MCP operations. The write semaphore and the backup
    /// semaphore arrive with the write tools and the backup tool.
    /// </summary>
    public async ValueTask<IDisposable> AcquireRequestSlotAsync(CancellationToken cancellationToken)
    {
        await _requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Slot(_requestSlots);
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

            // TODO Phase 2: SqliteSecurity.ApplyBaseline(connection, AuthorizerPolicy.Read);
            // The baseline belongs on every connection that caller input reaches. No tool accepts
            // caller SQL yet, thus only the query_only block is in place.

            // A second, independent block next to the read-only open mode. The two mechanisms fail
            // in different ways, thus both stay.
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

    public void Dispose() => _requestSlots.Dispose();

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
