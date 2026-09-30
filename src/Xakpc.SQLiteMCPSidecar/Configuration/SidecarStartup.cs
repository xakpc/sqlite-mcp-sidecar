using Microsoft.Data.Sqlite;
using Xakpc.SQLiteMCPSidecar.Database;
using Xakpc.SQLiteMCPSidecar.Exceptions;
using Xakpc.SQLiteMCPSidecar.Security;

namespace Xakpc.SQLiteMCPSidecar.Configuration;

/// <summary>
/// The startup checks that need the database file or the backup directory. The pure configuration
/// checks are in <see cref="SidecarOptions.Load"/>.
/// </summary>
public static class SidecarStartup
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var options = services.GetRequiredService<SidecarOptions>();
        var database = services.GetRequiredService<SqliteService>();

        // Proves that the file is a readable SQLite database before the first request arrives.
        var journalMode = await ReadJournalModeOrFailAsync(database, options, cancellationToken)
            .ConfigureAwait(false);

        var canWrite = options.Permissions.Has(Permission.Write) || options.Permissions.Has(Permission.DangerRawWrite);
        if (canWrite && !string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            // The sidecar never changes journal_mode: the owning application owns that setting. A
            // refusal to start would make the sidecar unusable with many correct deployments.
            logger.LogWarning(
                "The database journal mode is {JournalMode} and not WAL, and this deployment can write. "
              + "A sidecar write blocks every reader of the owning application for the duration of the write.",
                journalMode);
        }
        else
        {
            logger.LogInformation("Database journal mode is {JournalMode}.", journalMode);
        }

        if (options.Permissions.Has(Permission.Backup) && options.BackupDirectory is { } backupDirectory)
        {
            SweepStalePartialBackups(backupDirectory, logger);
        }

        logger.LogInformation(
            "Sidecar ready. permissions={Permissions} maxConcurrency={MaxConcurrency}",
            options.Permissions.ToString(), options.MaxConcurrency);
    }

    /// <summary>
    /// Reads the journal mode, and reports a SQLite failure as a configuration problem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The database file is the one thing that <see cref="SidecarOptions.Load"/> cannot prove. It
    /// checks that the path exists; it cannot check that the path holds a SQLite database, that this
    /// process may read it, or that the filesystem under it carries the locks that SQLite needs. A
    /// bind mount of a Windows folder and a network share both fail that last one.
    /// </para>
    /// <para>
    /// <b>Every startup failure must read as a deployment problem.</b> A <see cref="SqliteException"/>
    /// here would leave the process with an unhandled exception, thus the operator gets
    /// <c>Unhandled exception. Microsoft.Data.Sqlite.SqliteException ...</c> and a stack trace of this
    /// product, and an orchestrator gets an exit code that means a crash. The message SQLite gives is
    /// the useful half, thus it is carried into the problem list that every other startup check uses.
    /// </para>
    /// </remarks>
    private static async Task<string> ReadJournalModeOrFailAsync(
        SqliteService database, SidecarOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return await database.ReadJournalModeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            // The path is an operator value and this message never leaves the process, thus it names
            // the path. The disclosure rules bind what a remote caller reads, not a startup log.
            throw new SidecarConfigurationException(
            [
                $"SQLITE_SIDECAR_DB is '{options.DatabasePath}' and SQLite cannot read it: "
                + exception.Message
                + " Check that the path holds a SQLite database, that this process may read it, and "
                + "that the directory is writable, which a WAL database needs even for a read.",
            ]);
        }
    }

    /// <summary>
    /// Deletes stale partial backups. A process that stops during a backup leaves such a file, and
    /// an operator must never mistake it for a usable backup.
    /// </summary>
    private static void SweepStalePartialBackups(string backupDirectory, ILogger logger)
    {
        // The suffix comes from BackupService, thus the sweep and the writer can never disagree.
        foreach (var partial in Directory.EnumerateFiles(backupDirectory, "*.db" + BackupService.PartialSuffix))
        {
            try
            {
                File.Delete(partial);
                logger.LogInformation("Deleted a stale partial backup. name={Name}", Path.GetFileName(partial));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("A stale partial backup could not be deleted. name={Name}", Path.GetFileName(partial));
            }
        }
    }
}
