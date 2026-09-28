using System.Globalization;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Xakpc.SQLiteMCPSidecar.Configuration;

namespace Xakpc.SQLiteMCPSidecar.Database;

/// <summary>Why a backup stopped.</summary>
public enum BackupOutcome
{
    /// <summary>The copy reached <c>SQLITE_DONE</c> and the file carries its final name.</summary>
    Succeeded,

    /// <summary>The restart cap stopped the copy. Nothing usable was left behind.</summary>
    AbandonedAfterRestarts,
}

/// <summary>The result of one backup attempt.</summary>
/// <param name="Outcome">Why the copy stopped.</param>
/// <param name="FileName">The final file name, or <c>null</c> when the copy did not finish.</param>
/// <param name="Bytes">The size of the finished file, or 0.</param>
/// <param name="Restarts">How many times the source changed and forced the copy to start again.</param>
public sealed record BackupResult(BackupOutcome Outcome, string? FileName, long Bytes, int Restarts);

/// <summary>
/// Copies the live database with the SQLite Online Backup API, so the owning application continues to
/// write while the copy runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant.</b> A file with the <c>.db</c> suffix in the backup directory is always a complete
/// copy. The copy writes <c>name.db.partial</c> and renames it only after <c>SQLITE_DONE</c>. A rename
/// inside one directory is atomic. The SQLite documentation is explicit that an unfinished backup does
/// not leave a usable file: "If sqlite3_backup_step() has not yet returned SQLITE_DONE, then any active
/// write-transaction on the destination database is rolled back."
/// </para>
/// <para>
/// <b>Invariant.</b> A remote caller never supplies a path. It supplies a label, and the server builds
/// the file name under the configured directory. See <see cref="BuildFileName"/>.
/// </para>
/// <para>
/// Do not copy the database with a filesystem copy. A plain copy of a live database can give a torn
/// file.
/// </para>
/// </remarks>
public sealed partial class BackupService(
    SidecarOptions options,
    SqliteService database,
    ILogger<BackupService> logger)
{
    /// <summary>
    /// Pages for each <c>sqlite3_backup_step</c> call, about 1 MiB at the usual 4 KiB page size.
    /// </summary>
    /// <remarks>
    /// <b>This constant is the availability trade of the whole feature.</b> The SQLite documentation
    /// states that "every call to sqlite3_backup_step() obtains a shared lock on the source database
    /// that lasts for the duration of the sqlite3_backup_step() call". A single step over the whole
    /// database, which is what <c>SqliteConnection.BackupDatabase</c> does with <c>nPage = -1</c>, holds
    /// that lock for the entire copy and blocks every writer of the owning application on a
    /// rollback-journal database. A smaller value releases the lock more often and costs more restart
    /// exposure. Do not raise it without measuring the lock hold time.
    /// </remarks>
    private const int PagesPerStep = 256;

    /// <summary>
    /// The runaway cap. It counts restarts, not seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The source is unlocked between steps, thus a write by the owning application makes the next step
    /// start the copy again. A database under continuous write can therefore make no progress for ever
    /// while it still consumes read bandwidth, and no caller waits for the result, thus nothing else
    /// would ever stop it.
    /// </para>
    /// <para>
    /// It counts restarts and not wall-clock time on purpose. A deadline cannot tell a livelock from a
    /// large database: a database that copies steadily for twenty minutes is healthy, and a deadline
    /// reports it as the same failure as one that can never finish. A restart count names the real
    /// pathology. See <c>.lode/decisions/0006-backup-restart-cap.md</c>.
    /// </para>
    /// </remarks>
    private const int MaxRestarts = 50;

    /// <summary>The suffix of an incomplete copy. The writer and the startup sweep share it.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>The longest label a caller may send.</summary>
    public const int MaxLabelLength = 40;

    /// <summary>The pause before a retry when the source or the destination is locked.</summary>
    private static readonly TimeSpan BusyDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Runs one backup to completion. The caller holds the backup slot for the whole call.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The label is not usable, the deployment has no backup directory, or SQLite failed.
    /// </exception>
    public async Task<BackupResult> RunAsync(string? label, CancellationToken cancellationToken)
    {
        var directory = options.BackupDirectory
            ?? throw new InvalidOperationException("The deployment has no backup directory.");

        var fileName = BuildFileName(label);
        var finalPath = Contain(directory, fileName);
        var partialPath = Contain(directory, fileName + PartialSuffix);

        var started = TimeProvider.System.GetTimestamp();
        var result = await CopyAsync(partialPath, finalPath, fileName, cancellationToken).ConfigureAwait(false);

        LogBackupFinished(
            logger,
            result.Outcome.ToString(),
            TimeProvider.System.GetElapsedTime(started).TotalMilliseconds,
            fileName,
            result.Bytes,
            result.Restarts);

        return result;
    }

    private async Task<BackupResult> CopyAsync(
        string partialPath,
        string finalPath,
        string fileName,
        CancellationToken cancellationToken)
    {
        // The source opens read-only, exactly like a query does. The Online Backup API reads the source
        // and writes the destination, thus the source needs no write access: a deployment with
        // schema,read,backup never opens a write handle to the live database.
        await using var source = await database.OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);

        int restarts;
        await using (var destination = await SqliteService
            .OpenBackupDestinationAsync(partialPath, cancellationToken)
            .ConfigureAwait(false))
        {
            try
            {
                restarts = await StepAsync(source, destination, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The handle closes with the using block, and only then can the file be removed.
                await destination.CloseAsync().ConfigureAwait(false);
                DeleteQuietly(partialPath);
                throw;
            }

            // The handle must close before the rename: an open handle keeps the file locked on Windows,
            // and the -wal and -shm files of the destination must be gone too.
            await destination.CloseAsync().ConfigureAwait(false);
        }

        if (restarts < 0)
        {
            DeleteQuietly(partialPath);
            return new BackupResult(BackupOutcome.AbandonedAfterRestarts, null, 0, MaxRestarts);
        }

        // Only now does the file get a name that an operator may trust.
        File.Move(partialPath, finalPath, overwrite: false);
        var bytes = new FileInfo(finalPath).Length;
        return new BackupResult(BackupOutcome.Succeeded, fileName, bytes, restarts);
    }

    /// <summary>
    /// The copy loop. It returns the restart count, or <c>-1</c> when the restart cap stopped it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loop is what makes a backup cancellable. <c>sqlite3_backup_step</c> does not document
    /// <c>SQLITE_INTERRUPT</c> among its return codes, and the documentation never mentions
    /// <c>sqlite3_interrupt</c> for a backup, thus the sidecar does not try to interrupt one. It checks
    /// the token between steps instead, and the cancellation latency is one step and not one backup.
    /// </para>
    /// <para>
    /// A rising <c>sqlite3_backup_remaining</c> is the restart signal. The value only ever falls while
    /// the copy makes progress, thus a rise means the source changed and the step started again.
    /// </para>
    /// </remarks>
    private async Task<int> StepAsync(
        SqliteConnection source,
        SqliteConnection destination,
        CancellationToken cancellationToken)
    {
        var sourceHandle = source.Handle
            ?? throw new InvalidOperationException("The backup source is not open, thus it has no handle.");
        var destinationHandle = destination.Handle
            ?? throw new InvalidOperationException("The backup destination is not open, thus it has no handle.");

        var backup = raw.sqlite3_backup_init(destinationHandle, "main", sourceHandle, "main");
        if (backup is null)
        {
            throw Failure(destinationHandle, raw.sqlite3_errcode(destinationHandle), "could not start");
        }

        try
        {
            var restarts = 0;
            var lastRemaining = int.MaxValue;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var code = raw.sqlite3_backup_step(backup, PagesPerStep);
                if (code == raw.SQLITE_DONE)
                {
                    return restarts;
                }

                if (code is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
                {
                    // The busy handler already waited. A lock conflict spends the same budget as a
                    // restart, because both mean the owning application is busy writing.
                    if (++restarts > MaxRestarts)
                    {
                        return -1;
                    }

                    await Task.Delay(BusyDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (code != raw.SQLITE_OK)
                {
                    throw Failure(destinationHandle, code, "failed");
                }

                var remaining = raw.sqlite3_backup_remaining(backup);
                if (remaining > lastRemaining)
                {
                    LogBackupRestarted(logger, remaining, lastRemaining, restarts + 1);
                    if (++restarts > MaxRestarts)
                    {
                        return -1;
                    }
                }

                lastRemaining = remaining;
            }
        }
        finally
        {
            raw.sqlite3_backup_finish(backup);
        }
    }

    /// <summary>
    /// Builds the failure for a SQLite result code. The message reaches the log only: a SQLite message
    /// often names the database file.
    /// </summary>
    private static InvalidOperationException Failure(sqlite3 handle, int code, string what) =>
        new($"The backup {what}. SQLite returned {code}: {raw.sqlite3_errmsg(handle).utf8_to_string()}");

    /// <summary>
    /// Builds the file name from the configured database name, a UTC timestamp and the caller label.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Invariant.</b> The label is untrusted input and it never becomes a path. Only
    /// <c>A-Z a-z 0-9 . _ -</c> survive, the length is bounded, and a path separator, a <c>..</c>
    /// sequence, a null byte and a control character all fail the same rule: they are not in the
    /// permitted set. An allowlist and not a denylist, because a denylist over path syntax is a defect
    /// waiting for a platform difference.
    /// </para>
    /// <para>
    /// The timestamp keeps the name unique, thus a repeated label cannot overwrite an earlier backup.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The label holds no permitted character.</exception>
    internal string BuildFileName(string? label)
    {
        var stem = Path.GetFileNameWithoutExtension(options.DatabasePath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "database";
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        if (string.IsNullOrWhiteSpace(label))
        {
            return $"{stem}-{timestamp}.db";
        }

        var clean = Sanitize(label);
        if (clean.Length == 0)
        {
            throw new InvalidOperationException(
                "The label holds no usable character. Use letters, digits, a dot, an underscore or a hyphen.");
        }

        return $"{stem}-{timestamp}-{clean}.db";
    }

    private static string Sanitize(string label)
    {
        var builder = new System.Text.StringBuilder(MaxLabelLength);
        foreach (var character in label)
        {
            if (builder.Length == MaxLabelLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            {
                builder.Append(character);
            }
        }

        // A name of dots only would still resolve to a directory entry.
        return builder.ToString().Trim('.');
    }

    /// <summary>
    /// Joins a server-built name to the configured directory and proves the result stays inside it.
    /// </summary>
    /// <remarks>
    /// The check is redundant while <see cref="Sanitize"/> holds, and it stays because it is the last
    /// line of defence: it is the assertion that no future change to the name builder can turn a label
    /// into a path outside the backup directory.
    /// </remarks>
    private static string Contain(string directory, string fileName)
    {
        var root = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));

        var rooted = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rooted, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The backup path escaped the configured backup directory.");
        }

        return candidate;
    }

    /// <summary>Removes a partial file. It never throws: the caller already has the real outcome.</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The startup sweep removes it at the next launch. See SidecarStartup.
        }
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Backup finished. outcome={Outcome} durationMs={DurationMs} name={Name} bytes={Bytes} restarts={Restarts}")]
    private static partial void LogBackupFinished(
        ILogger logger, string outcome, double durationMs, string name, long bytes, int restarts);

    /// <summary>
    /// The record of one restart. It is the only evidence an operator has that the owning application
    /// writes faster than the backup copies.
    /// </summary>
    [LoggerMessage(EventId = 3002, Level = LogLevel.Debug,
        Message = "The backup restarted. remainingNow={Remaining} remainingBefore={Before} restarts={Restarts}")]
    private static partial void LogBackupRestarted(ILogger logger, int remaining, int before, int restarts);
}
