using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xakpc.SQLiteMCPSidecar.Configuration;
using Xakpc.SQLiteMCPSidecar.Database;
using Xakpc.SQLiteMCPSidecar.Exceptions;
using Xakpc.SQLiteMCPSidecar.Security;
using Xakpc.SQLiteMCPSidecar.Tests.Fixtures;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// Startup validation. The sidecar does not start in a degraded state: a silent fallback hides a
/// deployment mistake, and an orchestrator sees a failed start immediately.
/// </summary>
public sealed class StartupTests
{
    [Fact]
    public void WritePermissionWithoutTheReadFloorFailsAndNamesTheMissingPermissions()
    {
        var problems = Problems(permissions: "write");

        Assert.Contains(problems, p => p.Contains("write requires schema and read"));
        Assert.Contains(problems, p => p.Contains("schema") && p.Contains("read"));
    }

    [Fact]
    public void DangerRawWritePermissionWithoutTheReadFloorFails()
    {
        var problems = Problems(permissions: "danger-raw-write");

        Assert.Contains(problems, p => p.Contains("danger-raw-write requires schema and read"));
    }

    [Fact]
    public void AMisspelledPermissionNameFails()
    {
        // An ignored name gives a deployment with fewer tools than the operator expects.
        var problems = Problems(permissions: "reed");

        Assert.Contains(problems, p => p.Contains("unknown permission name") && p.Contains("reed"));
    }

    [Fact]
    public void AnAbsentTokenFails()
    {
        var problems = Problems(token: null);

        Assert.Contains(problems, p => p.Contains("SQLITE_SIDECAR_TOKEN is required"));
    }

    [Fact]
    public void AnAbsentDatabaseValueFails()
    {
        var problems = Problems(databasePath: null);

        Assert.Contains(problems, p => p.Contains("SQLITE_SIDECAR_DB is required"));
    }

    [Fact]
    public void ADatabaseFileThatDoesNotExistFails()
    {
        // The sidecar never creates the file: creation hides a wrong path.
        var problems = Problems(databasePath: Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.db"));

        Assert.Contains(problems, p => p.Contains("does not name an existing file"));
    }

    [Fact]
    public void TheBackupPermissionWithoutABackupDirectoryFails()
    {
        var problems = Problems(permissions: "schema,read,backup", backupDirectory: null);

        Assert.Contains(problems, p => p.Contains("SQLITE_SIDECAR_BACKUP_DIR is required"));
    }

    [Fact]
    public void TheBackupPermissionWithAnUnwritableBackupDirectoryFails()
    {
        var problems = Problems(
            permissions: "schema,read,backup",
            backupDirectory: Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"));

        Assert.Contains(problems, p => p.Contains("does not name a writable directory"));
    }

    [Fact]
    public void ANumericLimitOutsideItsRangeFails()
    {
        var problems = Problems(extra: new() { ["MAX_CONCURRENCY"] = "0" });

        Assert.Contains(problems, p => p.Contains("SQLITE_SIDECAR_MAX_CONCURRENCY"));
    }

    [Fact]
    public void EveryProblemIsReportedAtOneTime()
    {
        // An operator repairs one deployment, not one variable.
        var problems = Problems(token: null, databasePath: null, permissions: "write");

        Assert.True(problems.Count >= 3, $"Expected at least three problems, got: {string.Join(" | ", problems)}");
    }

    [Fact]
    public void TheDefaultPermissionSetIsReadOnly()
    {
        using var database = SampleDatabase.CreateTemporary();
        var options = Load(new Dictionary<string, string?>
        {
            ["DB"] = database.Path,
            ["TOKEN"] = "t",
        });

        Assert.True(options.Permissions.Has(Permission.Schema));
        Assert.True(options.Permissions.Has(Permission.Read));
        Assert.False(options.Permissions.Has(Permission.Write));
        Assert.False(options.Permissions.Has(Permission.DangerRawWrite));
    }

    /// <summary>
    /// A path that exists and is not a SQLite database fails as a configuration problem.
    /// </summary>
    /// <remarks>
    /// <c>SidecarOptions.Load</c> proves that the path exists and it cannot prove more: the content of
    /// the file, the read permission of this process and the lock support of the filesystem under it
    /// are all invisible to it. The failure must still read as a deployment problem, because the raw
    /// <c>SqliteException</c> reached the operator as <c>Unhandled exception.</c> and a stack trace of
    /// this product, which names no path and gives no repair.
    /// </remarks>
    [Fact]
    public async Task ADatabasePathThatIsNotADatabaseFailsAsAConfigurationProblem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-a-database-{Guid.NewGuid():N}.db");
        await File.WriteAllTextAsync(path, "this is not a SQLite database", TestContext.Current.CancellationToken);

        try
        {
            var options = Load(new Dictionary<string, string?>
            {
                ["DB"] = path,
                ["TOKEN"] = "t",
                ["PERMISSIONS"] = "schema,read",
            });

            var services = new ServiceCollection()
                .AddSingleton(options)
                .AddSingleton<SqliteService>()
                .BuildServiceProvider();

            var exception = await Assert.ThrowsAsync<SidecarConfigurationException>(() =>
                SidecarStartup.RunAsync(services, NullLogger.Instance, TestContext.Current.CancellationToken));

            // The operator has to be able to find the file that the message is about.
            Assert.Contains(path, exception.Message, StringComparison.Ordinal);

            // The cause that SQLite reported is the useful half and it survives the translation.
            Assert.Contains("not a database", exception.Message, StringComparison.OrdinalIgnoreCase);

            // One problem list, the same shape as every other startup failure.
            Assert.Single(exception.Problems);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ARollbackJournalDatabaseWithAWritePermissionStartsAndServes()
    {
        if (SidecarHarness.IsExternal)
        {
            Assert.Skip("This test starts its own host with a rollback-journal database.");
        }

        // The journal mode is a property of another application's database, not a sidecar
        // configuration mistake, thus the sidecar warns and still serves.
        await using var harness = SidecarHarness.Create("schema,read,write", journalMode: "delete");
        using var client = harness.CreateHttpClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
    }

    private static List<string> Problems(
        string? databasePath = "",
        string? token = "test-token",
        string permissions = "schema,read",
        string? backupDirectory = "",
        Dictionary<string, string?>? extra = null)
    {
        using var database = SampleDatabase.CreateTemporary();

        var settings = new Dictionary<string, string?>
        {
            ["DB"] = databasePath == "" ? database.Path : databasePath,
            ["TOKEN"] = token,
            ["PERMISSIONS"] = permissions,
            ["BACKUP_DIR"] = backupDirectory == "" ? database.BackupDirectory : backupDirectory,
        };

        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        var exception = Assert.Throws<SidecarConfigurationException>(() => Load(settings));
        return [.. exception.Problems];
    }

    private static SidecarOptions Load(Dictionary<string, string?> settings) =>
        SidecarOptions.Load(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
}
