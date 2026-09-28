using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Xakpc.SQLiteMCPSidecar.Tests.Fixtures;

/// <summary>
/// Builds the sample database from the embedded <c>sample-db.sql</c> script.
/// </summary>
public static class SampleDatabase
{
    /// <summary>The script text. Both the test suite and the dev script use it.</summary>
    public static string Script
    {
        get
        {
            const string name = "Xakpc.SQLiteMCPSidecar.Tests.Fixtures.sample-db.sql";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"The embedded resource {name} is absent.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>
    /// Writes a new sample database at <paramref name="path"/>, replacing any earlier file.
    /// </summary>
    /// <param name="journalMode">
    /// <c>wal</c> for the normal case, or <c>delete</c> to build a rollback-journal database for the
    /// journal-mode warning test.
    /// </param>
    public static void CreateAt(string path, string journalMode = "wal")
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            File.Delete(path + suffix);
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ConnectionString);

        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = Script;
            command.ExecuteNonQuery();
        }

        // AFTER the script, and not before it. The script carries its own `PRAGMA journal_mode = WAL`
        // line, thus a mode set before it is overwritten and the journalMode argument does nothing. That
        // defect made every rollback-journal test run against a WAL database and pass for the wrong
        // reason.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA journal_mode = {journalMode};";
        var applied = pragma.ExecuteScalar() as string;

        if (!string.Equals(applied, journalMode, StringComparison.OrdinalIgnoreCase))
        {
            // A silent fallback here would recreate the same class of defect.
            throw new InvalidOperationException(
                $"The sample database asked for journal mode '{journalMode}' and SQLite reported '{applied}'.");
        }
    }

    /// <summary>
    /// Creates a sample database in a new temporary directory. Dispose removes the directory.
    /// </summary>
    public static TemporaryDatabase CreateTemporary(string journalMode = "wal")
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlite-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db");
        CreateAt(path, journalMode);
        return new TemporaryDatabase(root, path);
    }
}

/// <summary>A sample database in a temporary directory, with a backup directory next to it.</summary>
public sealed class TemporaryDatabase(string root, string path) : IDisposable
{
    public string Path { get; } = path;

    public string BackupDirectory { get; } = Directory.CreateDirectory(System.IO.Path.Combine(root, "backups")).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A held SQLite handle on Windows can block the delete. A leftover temporary directory
            // is not a test failure.
        }
    }
}
