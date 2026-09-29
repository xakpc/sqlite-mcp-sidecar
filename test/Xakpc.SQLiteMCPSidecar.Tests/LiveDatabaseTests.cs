using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xakpc.SQLiteMCPSidecar.Database;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The sidecar on a database that another client is using at the same time.
/// </summary>
/// <remarks>
/// <para>
/// Every other test class works on a quiet, private database. This one runs the deployment that the
/// product exists for: the owning application reads and writes the file while the sidecar queries
/// and writes it. It closes the two gaps that
/// <c>.lode/plans/required-tests.md</c> recorded as uncovered, a forced lock conflict and a
/// saturated request semaphore.
/// </para>
/// <para>
/// <b>Invariant.</b> No test here asserts on an interleaving. Each one asserts something that holds
/// for every possible ordering, thus the suite is reproducible while the timing is not. The two
/// deterministic lock windows are produced by holding a lock from the test, never by hoping for a
/// race. See <c>.lode/testing/live-database-suite.md</c>.
/// </para>
/// <para>
/// These tests need <c>DatabasePath</c> and <c>Services</c>, which the external harness does not
/// offer, thus they skip against a container. That is the same trade the file assertions of
/// <c>BackupToolTests</c> make: contention is .NET and SQLite locking logic, and CI runs it on Linux
/// in the in-process job.
/// </para>
/// </remarks>
public sealed class LiveDatabaseTests
{
    private const string WritePermissions = "schema,read,write";

    /// <summary>The seed of the scripted workload. One number, thus one reproducible statement list.</summary>
    private const int Seed = 20260928;

    /// <summary>
    /// The sidecar writes while the owning application writes. Every accepted write lands exactly one
    /// time and the file stays sound.
    /// </summary>
    [Fact]
    public async Task SidecarWritesSucceedWhileTheApplicationWrites()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        Assert.SkipWhen(harness.DatabasePath is null, SkipReason);

        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var owner = await OwningApplication.OpenAsync(
            harness.DatabasePath!, TestContext.Current.CancellationToken);

        const int sidecarWrites = 20;
        var accepted = new List<string>();
        var refusals = new List<string>();

        // Both sides run at the same time, and the owner is the busier of the two.
        var ownerWork = owner.RunScriptedWritesAsync(200, Seed, TestContext.Current.CancellationToken);
        var sidecarWork = Task.Run(async () =>
        {
            // The loop below drives the MCP client, which carries the test cancellation token itself.
            for (var index = 0; index < sidecarWrites; index++)
            {
                var kind = "sidecar-" + index;
                try
                {
                    await InsertToolTests.CallInsertAsync(
                        client,
                        InsertToolTests.NewRequestId(),
                        "events",
                        new Dictionary<string, object?> { ["kind"] = kind });
                    accepted.Add(kind);
                }
                catch (McpToolFailure failure)
                {
                    refusals.Add(failure.Message);
                }
            }
        }, TestContext.Current.CancellationToken);

        var ownerInserts = await ownerWork;
        await sidecarWork;

        // A refusal is allowed under contention, and only for the two codes that mean "retry later".
        // Any other code would mean the sidecar reported a caller mistake for a correct request.
        foreach (var refusal in refusals)
        {
            Assert.True(
                refusal.Contains("DatabaseBusy", StringComparison.Ordinal)
                || refusal.Contains("WriteBudgetExceeded", StringComparison.Ordinal),
                "A write under contention was refused with an unexpected code: " + refusal);
        }

        // Exactly one row for each accepted write. A duplicate would mean a retry applied two times,
        // and a missing row would mean the sidecar reported a write that it did not commit.
        foreach (var kind in accepted)
        {
            var rows = await owner.CountAsync(
                "SELECT count(*) FROM events WHERE kind = '" + kind + "'",
                TestContext.Current.CancellationToken);
            Assert.Equal(1, rows);
        }

        // The owning application lost nothing to the sidecar.
        var ownerRows = await owner.CountAsync(
            "SELECT count(*) FROM logs WHERE message LIKE '" + OwningApplication.Marker + "-%'",
            TestContext.Current.CancellationToken);
        Assert.Equal(ownerInserts, ownerRows);

        Assert.Equal("ok", await owner.QuickCheckAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The owning application holds the write lock. The sidecar write is correct, thus the agent must
    /// read "retry later" and not "correct your request".
    /// </summary>
    /// <remarks>
    /// This is the forced lock conflict that <c>.lode/plans/required-tests.md</c> listed as having no
    /// test. WAL is enough here and a rollback journal is not needed: a WAL writer does block another
    /// writer, although it does not block a reader.
    /// </remarks>
    [Fact]
    public async Task AWriteAgainstALockedDatabaseIsDatabaseBusy()
    {
        await using var harness = SidecarHarness.Create(
            WritePermissions,
            settings: new Dictionary<string, string?> { ["BUSY_TIMEOUT_SECONDS"] = "1" });
        Assert.SkipWhen(harness.DatabasePath is null, SkipReason);

        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var owner = await OwningApplication.OpenAsync(
            harness.DatabasePath!, TestContext.Current.CancellationToken);

        await using var held = await owner.HoldWriteLockAsync(TestContext.Current.CancellationToken);

        var started = Stopwatch.GetTimestamp();
        var failure = await Record.ExceptionAsync(() => InsertToolTests.CallInsertAsync(
            client,
            InsertToolTests.NewRequestId(),
            "events",
            new Dictionary<string, object?> { ["kind"] = "blocked" }));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.IsType<McpToolFailure>(failure);
        Assert.Contains("DatabaseBusy", failure.Message, StringComparison.Ordinal);

        // The request itself is valid. InvalidWrite would send the agent to rewrite a correct call,
        // and it would keep rewriting for as long as the application holds the lock.
        Assert.DoesNotContain("InvalidWrite", failure.Message, StringComparison.Ordinal);

        // It waited for the busy timeout instead of failing at once.
        Assert.True(
            elapsed > TimeSpan.FromMilliseconds(500),
            $"The write failed after {elapsed.TotalMilliseconds:F0}ms, thus it did not wait for the busy timeout.");

        // Nothing was written while the lock was held.
        Assert.Equal(0, await owner.CountAsync(
            "SELECT count(*) FROM events WHERE kind = 'blocked'", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The retry that <c>DatabaseBusy</c> asks for works, and the same requestId still writes one row.
    /// </summary>
    /// <remarks>
    /// A failure is deliberately not cached, thus the retry executes. This is the pair of assertions
    /// that makes the instruction in the error message followable.
    /// </remarks>
    [Fact]
    public async Task ARetryAfterDatabaseBusySucceedsAndWritesOneRow()
    {
        await using var harness = SidecarHarness.Create(
            WritePermissions,
            settings: new Dictionary<string, string?> { ["BUSY_TIMEOUT_SECONDS"] = "1" });
        Assert.SkipWhen(harness.DatabasePath is null, SkipReason);

        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var owner = await OwningApplication.OpenAsync(
            harness.DatabasePath!, TestContext.Current.CancellationToken);

        var requestId = InsertToolTests.NewRequestId();
        var values = new Dictionary<string, object?> { ["kind"] = "retried" };

        var held = await owner.HoldWriteLockAsync(TestContext.Current.CancellationToken);
        var failure = await Record.ExceptionAsync(
            () => InsertToolTests.CallInsertAsync(client, requestId, "events", values));
        Assert.Contains("DatabaseBusy", failure!.Message, StringComparison.Ordinal);

        // The application finishes its transaction, and the agent retries with the SAME requestId.
        await held.DisposeAsync();

        var text = await InsertToolTests.CallInsertAsync(client, requestId, "events", values);
        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);

        Assert.Equal(1, await owner.CountAsync(
            "SELECT count(*) FROM events WHERE kind = 'retried'", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Every request waits for a slot and none of them fails. This is the saturated request semaphore
    /// that <c>.lode/plans/required-tests.md</c> listed as having no test.
    /// </summary>
    [Fact]
    public async Task TheRequestSemaphoreSerializesAndNothingFails()
    {
        await using var harness = SidecarHarness.Create(
            "schema,read",
            settings: new Dictionary<string, string?> { ["MAX_CONCURRENCY"] = "1" });
        Assert.SkipWhen(harness.DatabasePath is null, SkipReason);

        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var owner = await OwningApplication.OpenAsync(
            harness.DatabasePath!, TestContext.Current.CancellationToken);

        // The application writes underneath the saturated reader, thus the slot wait and the file lock
        // are both in play.
        var ownerWork = owner.RunScriptedWritesAsync(100, Seed, TestContext.Current.CancellationToken);

        const int callers = 12;
        var reads = Enumerable.Range(0, callers)
            .Select(_ => QueryToolTests.CallQueryAsync(client, "SELECT count(*) AS n FROM jobs"))
            .ToArray();

        var texts = await Task.WhenAll(reads);
        await ownerWork;

        // One slot serialises them. A queued request must wait, never be refused.
        Assert.Equal(callers, texts.Length);
        Assert.All(texts, text => Assert.Contains("truncated: false", text, StringComparison.Ordinal));
    }

    /// <summary>
    /// A read never observes half of a transaction of the owning application.
    /// </summary>
    /// <remarks>
    /// The owner writes both halves of a pair inside one transaction. The reader asks for the
    /// difference between the two halves, thus the answer is zero for every committed state and any
    /// other answer is a torn read. The assertion holds for each interleaving and the test needs no
    /// timing at all.
    /// </remarks>
    [Fact]
    public async Task AQueryNeverSeesAHalfWrittenTransaction()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        Assert.SkipWhen(harness.DatabasePath is null, SkipReason);

        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var owner = await OwningApplication.OpenAsync(
            harness.DatabasePath!, TestContext.Current.CancellationToken);

        var ownerWork = owner.WriteBalancedPairsAsync(120, TestContext.Current.CancellationToken);

        const string sql =
            "SELECT (SELECT count(*) FROM job_tags WHERE tag = '" + OwningApplication.PairLeft + "') "
          + "- (SELECT count(*) FROM job_tags WHERE tag = '" + OwningApplication.PairRight + "') AS diff";

        for (var read = 0; read < 60; read++)
        {
            var text = await QueryToolTests.CallQueryAsync(client, sql);
            Assert.Equal("0", SingleValue(text));
        }

        await ownerWork;

        // And one more time after the writer stopped, so the final state is asserted too.
        Assert.Equal("0", SingleValue(await QueryToolTests.CallQueryAsync(client, sql)));
    }

    /// <summary>
    /// A backup runs while the owning application keeps writing. That is the case the step loop
    /// exists for.
    /// </summary>
    /// <remarks>
    /// Two outcomes are correct and the test accepts both: a complete copy, or <c>BackupFailed</c>
    /// after the restart cap when the writer outran the copy. What must hold either way is that no
    /// partial file survives, and that a completed copy is a sound database. See
    /// <c>.lode/decisions/0006-backup-restart-cap.md</c>.
    /// </remarks>
    [Fact]
    public async Task ABackupCompletesWhileTheApplicationWrites()
    {
        await using var harness = SidecarHarness.Create("schema,read,backup");
        Assert.SkipWhen(harness.DatabasePath is null, SkipReason);

        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var owner = await OwningApplication.OpenAsync(
            harness.DatabasePath!, TestContext.Current.CancellationToken);

        var ownerWork = owner.RunScriptedWritesAsync(400, Seed, TestContext.Current.CancellationToken);
        var outcome = await Record.ExceptionAsync(
            () => BackupToolTests.BackupAsync(client, "under-load"));
        await ownerWork;

        if (outcome is not null)
        {
            Assert.IsType<McpToolFailure>(outcome);
            Assert.Contains("BackupFailed", outcome.Message, StringComparison.Ordinal);
        }

        var directory = harness.BackupDirectory!;

        // A partial file is never left behind, on either path.
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));

        var copies = Directory.GetFiles(directory, "*.db");
        if (outcome is null)
        {
            var copy = Assert.Single(copies);
            Assert.Equal("ok", await IntegrityCheckAsync(copy, TestContext.Current.CancellationToken));
        }

        // The source survived the copy under load.
        Assert.Equal("ok", await owner.QuickCheckAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Two calls that carry one requestId apply one write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WriteDeduplication.Check</c> reserves the identifier in the lock that reads it, thus the
    /// second caller sees <c>InFlight</c> and answers <c>DatabaseBusy</c> instead of executing. Before
    /// the reservation existed both callers saw <c>NotFound</c> and both wrote the row, which is the
    /// failure the identifier exists to prevent.
    /// </para>
    /// <para>
    /// <b>The test is one-sided.</b> Holding the write slot parks the first call, which opens the
    /// window instead of hoping for it, but nothing proves the second call reached <c>Check</c> while
    /// the first was still running. It can therefore pass although a regression is present; it cannot
    /// fail although the behaviour is correct.
    /// <c>WriteDeduplicationTests.ASecondCheckBeforeTheFirstStoreDoesNotSayExecute</c> carries the
    /// proof that needs no timing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrentCallsWithOneRequestIdApplyOneWrite()
    {
        await using var harness = SidecarHarness.Create(
            WritePermissions,
            // Long enough that a parked write waits for the test and not for the timeout.
            settings: new Dictionary<string, string?> { ["BUSY_TIMEOUT_SECONDS"] = "30" });
        Assert.SkipWhen(harness.DatabasePath is null, SkipReason);

        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var owner = await OwningApplication.OpenAsync(
            harness.DatabasePath!, TestContext.Current.CancellationToken);

        var database = harness.Services!.GetRequiredService<SqliteService>();
        var slot = await database.TryAcquireWriteSlotAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(slot);

        var requestId = InsertToolTests.NewRequestId();
        var values = new Dictionary<string, object?> { ["kind"] = "one-request-id" };

        var first = Record.ExceptionAsync(
            () => InsertToolTests.CallInsertAsync(client, requestId, "events", values)).AsTask();
        var second = Record.ExceptionAsync(
            () => InsertToolTests.CallInsertAsync(client, requestId, "events", values)).AsTask();

        // The one piece of timing in this class, and it only widens a window that the held slot has
        // already opened. See the one-sided note above.
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        slot!.Dispose();

        var outcomes = await Task.WhenAll(first, second);

        // One call does the work. The other is refused or replays the stored answer, and both of
        // those are correct: what must never happen is that both execute.
        var refusals = outcomes.Where(o => o is not null).ToList();
        Assert.True(refusals.Count <= 1, "Both calls failed, thus the write never ran at all.");
        foreach (var refusal in refusals)
        {
            Assert.Contains("DatabaseBusy", refusal!.Message, StringComparison.Ordinal);
            Assert.Contains("already running", refusal.Message, StringComparison.Ordinal);
        }

        Assert.Equal(1, await owner.CountAsync(
            "SELECT count(*) FROM events WHERE kind = 'one-request-id'",
            TestContext.Current.CancellationToken));

        // The refused caller does what the message tells it to: it retries with the same identifier
        // and gets the stored answer, thus the row count does not move.
        var retry = await InsertToolTests.CallInsertAsync(client, requestId, "events", values);
        Assert.Contains("rowsAffected: 1", retry, StringComparison.Ordinal);
        Assert.Equal(1, await owner.CountAsync(
            "SELECT count(*) FROM events WHERE kind = 'one-request-id'",
            TestContext.Current.CancellationToken));
    }

    private const string SkipReason =
        "This test drives a second SQLite client on the database file, and the external sidecar did not share its path.";

    /// <summary>
    /// The one value of a one-row, one-column TOON result.
    /// </summary>
    /// <remarks>
    /// The header line states the shape and the next line carries the row, thus the value is the
    /// second non-empty line. A test that searched the whole text for a number would also match the
    /// row count in the header.
    /// </remarks>
    private static string SingleValue(string toon)
    {
        var lines = toon.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var header = Array.FindIndex(lines, line => line.StartsWith("rows[", StringComparison.Ordinal));
        Assert.True(header >= 0 && header + 1 < lines.Length, "The result carries no row: " + toon);
        return lines[header + 1];
    }

    /// <summary>Opens a backup copy and reports what <c>integrity_check</c> says about it.</summary>
    private static async Task<string> IntegrityCheckAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? "unknown";
    }
}
