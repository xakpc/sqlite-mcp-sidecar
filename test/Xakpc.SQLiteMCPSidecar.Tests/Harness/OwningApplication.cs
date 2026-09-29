using Microsoft.Data.Sqlite;

namespace Xakpc.SQLiteMCPSidecar.Tests.Harness;

/// <summary>
/// The owning application, as a second SQLite client on the same file.
/// </summary>
/// <remarks>
/// <para>
/// This is the deployment that the product exists for: an application already owns the database and
/// keeps writing while the sidecar reads and writes it. A test that only ever sees a quiet, private
/// database proves the controls and not the product.
/// </para>
/// <para>
/// <b>Invariant.</b> The workload is scripted and seeded, thus a run produces the same statements
/// every time. The interleaving with the sidecar is not reproducible and no test asserts on one:
/// every assertion is an invariant that holds for each possible ordering. See
/// <c>.lode/testing/live-database-suite.md</c>.
/// </para>
/// </remarks>
public sealed class OwningApplication : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _databasePath;

    private OwningApplication(SqliteConnection connection, string databasePath)
    {
        _connection = connection;
        _databasePath = databasePath;
    }

    /// <summary>The prefix of every log message that the scripted workload writes.</summary>
    public const string Marker = "owner-op";

    /// <summary>The tags of the balanced pair that <see cref="WriteBalancedPairsAsync"/> writes.</summary>
    public const string PairLeft = "pair-left";

    /// <summary>The second half of that pair. It is written in the same transaction as the first.</summary>
    public const string PairRight = "pair-right";

    /// <summary>Opens a read-write connection on the database that the sidecar serves.</summary>
    public static async Task<OwningApplication> OpenAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);
        return new OwningApplication(connection, databasePath);
    }

    /// <summary>
    /// Runs the scripted workload: a fixed number of operations, chosen by a seeded generator.
    /// </summary>
    /// <remarks>
    /// The data of each row comes from the operation index and not from the generator, thus the rows
    /// are predictable by name and a test asserts on them without knowing the mix of operations. The
    /// return value is the number of rows that this run added to <c>logs</c>, which is what a test
    /// compares the file against afterwards.
    /// </remarks>
    public async Task<int> RunScriptedWritesAsync(int operations, int seed, CancellationToken cancellationToken)
    {
        var script = new Random(seed);
        var inserted = 0;

        for (var index = 0; index < operations; index++)
        {
            // Three of four operations insert and the fourth updates. Both are real write volume for
            // the file lock, and the update keeps rows moving under the sidecar.
            if (script.Next(4) == 3)
            {
                await ExecuteAsync(
                    "UPDATE jobs SET retry = retry + 1 WHERE id = 53",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            await ExecuteAsync(
                "INSERT INTO logs (job_id, level, message) VALUES (53, 'info', $message)",
                cancellationToken,
                ("$message", Marker + "-" + index)).ConfigureAwait(false);
            inserted++;
        }

        return inserted;
    }

    /// <summary>
    /// Writes <paramref name="pairs"/> transactions, each of which adds both halves of one pair.
    /// </summary>
    /// <remarks>
    /// The two rows are written inside one transaction, thus a reader that ever sees a different
    /// number of each half has read a half-written transaction. That is the assertion of the
    /// torn-read test, and it holds for every interleaving.
    /// </remarks>
    public async Task WriteBalancedPairsAsync(int pairs, CancellationToken cancellationToken)
    {
        for (var index = 0; index < pairs; index++)
        {
            await using var transaction = await _connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await ExecuteAsync(
                "INSERT INTO job_tags (job_id, tag) VALUES (53, $tag)",
                cancellationToken,
                ("$tag", PairLeft)).ConfigureAwait(false);
            await ExecuteAsync(
                "INSERT INTO job_tags (job_id, tag) VALUES (53, $tag)",
                cancellationToken,
                ("$tag", PairRight)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the write lock of the database and holds it until the handle is disposed.
    /// </summary>
    /// <remarks>
    /// It runs on a connection of its own, thus the caller keeps using this instance while the lock
    /// is held. <c>BEGIN IMMEDIATE</c> and not <c>BEGIN</c>: a deferred transaction takes no lock
    /// until its first write, and a test that asked for a lock would get none.
    /// </remarks>
    public async Task<IAsyncDisposable> HoldWriteLockAsync(CancellationToken cancellationToken)
    {
        var holder = await OpenConnectionAsync(_databasePath, cancellationToken).ConfigureAwait(false);

        try
        {
            await using var begin = holder.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await holder.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new WriteLock(holder);
    }

    /// <summary>Runs a scalar count against the file. The test reads the database, not the sidecar.</summary>
    public async Task<long> CountAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Reports the structural health of the file after a contended run.</summary>
    public async Task<string> QuickCheckAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string ?? "unknown";
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    private async Task ExecuteAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        // Pooling off, exactly as the sidecar opens its own handles: a pooled handle outlives the test
        // and keeps the temporary directory locked on Windows.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>A held <c>BEGIN IMMEDIATE</c>. Disposing it rolls back and closes the connection.</summary>
    private sealed class WriteLock(SqliteConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var rollback = connection.CreateCommand();
                rollback.CommandText = "ROLLBACK;";
                await rollback.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
