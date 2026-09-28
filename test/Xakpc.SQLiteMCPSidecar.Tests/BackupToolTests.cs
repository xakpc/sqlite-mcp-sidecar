using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Xakpc.SQLiteMCPSidecar.Database;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The <c>backup</c> tool over a real MCP client.
/// </summary>
/// <remarks>
/// <para>
/// <c>backup</c> is the one task-mode tool of the sidecar, thus a caller does not receive the result
/// from <c>tools/call</c>. It receives a task and polls for the outcome. These tests therefore use the
/// client half of the Tasks extension and not <c>CallToolAsync</c>.
/// </para>
/// <para>
/// The permission set is <c>schema,read,backup</c> on purpose: it proves that a backup needs no write
/// handle to the live database.
/// </para>
/// </remarks>
public sealed class BackupToolTests
{
    private const string Permissions = "schema,read,backup";

    [Fact]
    public async Task ABackupProducesOneCompleteFileAndNoPartial()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await BackupAsync(client, "before-cleanup");

        Assert.Contains("name: ", text);
        Assert.Contains("restarts: 0", text);

        var directory = harness.BackupDirectory;
        Assert.SkipWhen(directory is null, "The external target does not expose its backup directory.");

        // INVARIANT: a .db file in the backup directory is always complete, and no partial survives.
        Assert.Single(Directory.GetFiles(directory!, "*.db"));
        Assert.Empty(Directory.GetFiles(directory!, "*.partial"));
    }

    [Fact]
    public async Task TheCallAnswersWithATaskAndNotWithTheResult()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var answer = await client.CallToolAsTaskAsync(
            new CallToolRequestParams { Name = "backup" },
            TestContext.Current.CancellationToken);

        // This is the "call now, fetch later" contract, and it is asserted on the shape of the answer
        // rather than on a clock. A timing assertion would be a race: the sample database copies in
        // milliseconds, so a slow-enough copy cannot be arranged reliably. What matters is that the
        // sidecar never answers a backup inline, whatever the database size, because a reverse proxy
        // closes a long connection and the agent would read a false failure.
        Assert.True(answer.IsTask, "The backup tool must answer with a task and never with an inline result.");
        Assert.NotNull(answer.TaskCreated);

        var taskId = answer.TaskCreated!.TaskId;
        Assert.False(string.IsNullOrWhiteSpace(taskId));

        // The task reaches a terminal state, and it is Completed and not Failed.
        var status = await PollToTerminalAsync(client, taskId);
        Assert.Equal(McpTaskStatus.Completed, status);
    }

    [Fact]
    public async Task TheServerBuildsTheNameFromTheLabelAndATimestamp()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await BackupAsync(client, "before-cleanup");

        // app-<utc timestamp>-before-cleanup.db
        var name = NameOf(text);
        Assert.StartsWith("app-", name);
        Assert.EndsWith("-before-cleanup.db", name);
        Assert.Contains('Z', name);
    }

    [Fact]
    public async Task TheBackupFileIsAUsableDatabaseWithTheSameRows()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var name = NameOf(await BackupAsync(client, "verify"));

        var directory = harness.BackupDirectory;
        Assert.SkipWhen(directory is null, "The external target does not expose its backup directory.");

        // The point of the Online Backup API over a file copy: the result is a valid database and not a
        // torn file.
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory!, name),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await integrity.ExecuteScalarAsync(TestContext.Current.CancellationToken) as string);

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM jobs;";
        Assert.True(Convert.ToInt64(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken)) > 0);
    }

    [Fact]
    public async Task ARepeatedLabelDoesNotOverwriteTheEarlierBackup()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var first = NameOf(await BackupAsync(client, "nightly"));
        // The timestamp has one-second resolution, thus the second call must land in a later second for
        // the names to differ. A backup is not a high-frequency operation, so this is the real contract.
        await Task.Delay(TimeSpan.FromSeconds(1.1), TestContext.Current.CancellationToken);
        var second = NameOf(await BackupAsync(client, "nightly"));

        Assert.NotEqual(first, second);

        var directory = harness.BackupDirectory;
        Assert.SkipWhen(directory is null, "The external target does not expose its backup directory.");
        Assert.Equal(2, Directory.GetFiles(directory!, "*.db").Length);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/dir/name")]
    public async Task ALabelIsNeverAPath(string label)
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The separators and the dots are stripped, thus the label still yields one contained file.
        var name = NameOf(await BackupAsync(client, label));

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain("..", name);

        var directory = harness.BackupDirectory;
        Assert.SkipWhen(directory is null, "The external target does not expose its backup directory.");

        // Nothing appeared outside the backup directory.
        Assert.Single(Directory.GetFiles(directory!, "*.db"));
        Assert.Empty(Directory.GetDirectories(directory!));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(directory!.TrimEnd(Path.DirectorySeparatorChar))!, "escape.db")));
    }

    [Fact]
    public async Task ALabelOfOnlyRejectedCharactersIsABackupFailure()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Nothing survives sanitization, thus the server refuses instead of inventing a name.
        var failure = await Assert.ThrowsAsync<McpToolFailure>(() => BackupAsync(client, "///"));
        Assert.Contains("BackupFailed", failure.Message);
    }

    [Fact]
    public async Task ASecondBackupDuringABackupFails()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var services = harness.Services;
        Assert.SkipWhen(services is null, "The external target does not expose its services.");

        // Holding the slot is the deterministic way to prove the refusal. A real second backup would
        // race the first one, because the sample database copies in milliseconds.
        var database = services!.GetRequiredService<SqliteService>();
        using var held = database.TryAcquireBackupSlot();
        Assert.NotNull(held);

        var failure = await Assert.ThrowsAsync<McpToolFailure>(() => BackupAsync(client, "second"));

        Assert.Contains("BackupFailed", failure.Message);
        Assert.Contains("already in progress", failure.Message);

        var directory = harness.BackupDirectory;
        if (directory is not null)
        {
            // A refused backup writes nothing at all, not even a partial.
            Assert.Empty(Directory.GetFiles(directory));
        }
    }

    [Fact]
    public async Task TheBackupSlotNeverWaitsAndIsReleased()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var services = harness.Services;
        Assert.SkipWhen(services is null, "The external target does not expose its services.");
        var database = services!.GetRequiredService<SqliteService>();

        // INVARIANT: the backup slot is the one semaphore that does not wait. A second attempt returns
        // at once instead of holding the caller for the length of a copy.
        var first = database.TryAcquireBackupSlot();
        Assert.NotNull(first);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var second = database.TryAcquireBackupSlot();
        elapsed.Stop();

        Assert.Null(second);
        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(500), $"The second attempt waited {elapsed.Elapsed}.");

        first!.Dispose();
        using var third = database.TryAcquireBackupSlot();
        Assert.NotNull(third);
    }

    [Fact]
    public async Task StartupDeletesAStalePartialFile()
    {
        await using var harness = SidecarHarness.Create(Permissions);

        var directory = harness.BackupDirectory;
        Assert.SkipWhen(directory is null, "The external target cannot restart, thus it cannot run the sweep.");

        // A process that stops during a backup leaves this file. An operator must never mistake it for a
        // usable backup. The host has not started yet: the harness starts it at the first connect.
        var stale = Path.Combine(directory!, "app-20240101T000000Z-interrupted.db.partial");
        await File.WriteAllTextAsync(stale, "not a database", TestContext.Current.CancellationToken);

        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(File.Exists(stale));
        Assert.Empty(Directory.GetFiles(directory!, "*.db"));
    }

    [Fact]
    public async Task BackupIsAbsentWithoutThePermission()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("backup", tools.Select(t => t.Name));
        Assert.DoesNotContain("backup_status", tools.Select(t => t.Name));
    }

    /// <summary>
    /// Calls <c>backup</c> the way a real client must: as a task, polled to a terminal state.
    /// </summary>
    private static async Task<string> BackupAsync(McpClient client, string? label)
    {
        var arguments = new Dictionary<string, System.Text.Json.JsonElement>();
        if (label is not null)
        {
            arguments["label"] = System.Text.Json.JsonSerializer.SerializeToElement(label);
        }

        var result = await client.CallToolWithPollingAsync(
            new CallToolRequestParams { Name = "backup", Arguments = arguments },
            maxConsecutiveStuckPolls: 100,
            cancellationToken: TestContext.Current.CancellationToken);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return result.IsError == true ? throw new McpToolFailure(text) : text;
    }

    /// <summary>Polls one task until it leaves <see cref="McpTaskStatus.Working"/>.</summary>
    private static async Task<McpTaskStatus> PollToTerminalAsync(McpClient client, string taskId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var task = await client.GetTaskAsync(taskId, TestContext.Current.CancellationToken);
            if (task.Status != McpTaskStatus.Working)
            {
                return task.Status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"The task {taskId} never reached a terminal state.");
    }

    private static string NameOf(string resultText)
    {
        var line = resultText.Split('\n').First(l => l.StartsWith("name: ", StringComparison.Ordinal));
        return line["name: ".Length..].Trim();
    }
}
