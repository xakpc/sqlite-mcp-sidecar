using Xakpc.SQLiteMCPSidecar.Database;
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
        var journalMode = await database.ReadJournalModeAsync(cancellationToken).ConfigureAwait(false);

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
