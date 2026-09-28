using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The <c>delete</c> tool. The caller sends no SQL: it names a table, a flat filter and
/// <c>maxRows</c>, and the server builds one parameterized <c>DELETE</c>.
/// </summary>
/// <remarks>
/// Each test seeds the rows that it then removes, and it marks them with a unique value. The external
/// target shares one live database across the whole suite, thus a test must not remove a row of the
/// fixture and must not depend on a fixture row identifier.
/// </remarks>
public sealed class DeleteToolTests
{
    private const string WritePermissions = "schema,read,write";

    [Fact]
    public async Task DeleteRemovesTheRowsThatTheFilterSelects()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await UpdateToolTests.SeedJobAsync(client, "pending");

        var text = await CallDeleteAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            [UpdateToolTests.Condition("payload", "eq", marker)],
            maxRows: 1);

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);
        Assert.Contains("rowsChanged: 1", text, StringComparison.Ordinal);

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT COUNT(*) FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("0", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// The filter is required. There is no structured equivalent of <c>DELETE FROM jobs;</c>, and the
    /// rejection happens before any database work.
    /// </summary>
    [Fact]
    public async Task AnAbsentFilterIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallDeleteAsync(
            client, InsertToolTests.NewRequestId(), "jobs", where: null, maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyFilterIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallDeleteAsync(
            client, InsertToolTests.NewRequestId(), "jobs", [], maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsentMaxRowsIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallDeleteAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            [UpdateToolTests.Condition("id", "eq", 41)],
            maxRows: null));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bounded pre-count rejects a filter that is wider than <c>maxRows</c> before the statement
    /// runs, thus every row survives.
    /// </summary>
    [Fact]
    public async Task ABroadFilterIsRejectedAndRemovesNothing()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = InsertToolTests.NewMarker();
        for (var i = 0; i < 3; i++)
        {
            await InsertToolTests.CallInsertAsync(
                client, InsertToolTests.NewRequestId(), "jobs", new Dictionary<string, object?>
                {
                    ["status"] = "pending",
                    ["payload"] = marker,
                });
        }

        var failure = await Record.ExceptionAsync(() => CallDeleteAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            [UpdateToolTests.Condition("payload", "eq", marker)],
            maxRows: 2));

        Assert.NotNull(failure);
        Assert.Contains("WriteLimitExceeded", failure.Message, StringComparison.Ordinal);

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT COUNT(*) FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("3", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// The post-execution check and the row-limit rollback. The filter selects one row, thus the
    /// pre-count passes, but <c>ON DELETE CASCADE</c> removes three more rows in
    /// <c>job_tags</c>. <c>maxRows</c> bounds every changed row, thus the write rolls back.
    /// </summary>
    /// <remarks>
    /// This is the failure that the product exists to prevent: a tool call that asks to remove one row
    /// and destroys a whole subtree. The count comes from <c>sqlite3_total_changes</c> and not from
    /// <c>ExecuteNonQuery</c>, which reports the target table only. See
    /// <c>.lode/decisions/0004-maxrows-bounds-total-changes.md</c>.
    /// </remarks>
    [Fact]
    public async Task ACascadeOverTheLimitRollsBackAndKeepsEveryRow()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (marker, jobId) = await SeedJobWithTagsAsync(client, tags: 3);

        var failure = await Record.ExceptionAsync(() => CallDeleteAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            [UpdateToolTests.Condition("payload", "eq", marker)],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("WriteLimitExceeded", failure.Message, StringComparison.Ordinal);

        // The parent and every child survived the rollback.
        var job = await QueryToolTests.CallQueryAsync(
            client, $"SELECT COUNT(*) FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("1", job, StringComparison.Ordinal);

        var tags = await QueryToolTests.CallQueryAsync(
            client, $"SELECT COUNT(*) FROM job_tags WHERE job_id = {jobId}");
        Assert.Contains("3", tags, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same cascade succeeds when <c>maxRows</c> covers every row that it removes, and the answer
    /// reports both counts.
    /// </summary>
    [Fact]
    public async Task ACascadeInsideTheLimitSucceedsAndReportsEveryChangedRow()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var (marker, jobId) = await SeedJobWithTagsAsync(client, tags: 3);

        var text = await CallDeleteAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            [UpdateToolTests.Condition("payload", "eq", marker)],
            maxRows: 4);

        // One row of the target table, four rows in total.
        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);
        Assert.Contains("rowsChanged: 4", text, StringComparison.Ordinal);

        var tags = await QueryToolTests.CallQueryAsync(
            client, $"SELECT COUNT(*) FROM job_tags WHERE job_id = {jobId}");
        Assert.Contains("0", tags, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>logs</c> does not cascade, thus a delete of a job that has a log row breaks referential
    /// integrity and fails. The write connection sets <c>ForeignKeys = true</c>, and SQLite defaults
    /// that setting to off.
    /// </summary>
    [Fact]
    public async Task AForeignKeyViolationIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await UpdateToolTests.SeedJobAsync(client, "pending");
        var jobId = await ReadJobIdAsync(client, marker);

        await InsertToolTests.CallInsertAsync(
            client, InsertToolTests.NewRequestId(), "logs", new Dictionary<string, object?>
            {
                ["job_id"] = jobId,
                ["level"] = "info",
                ["message"] = "keeps the job referenced",
            });

        var failure = await Record.ExceptionAsync(() => CallDeleteAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            [UpdateToolTests.Condition("payload", "eq", marker)],
            maxRows: 10));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);

        // The rollback kept the job.
        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT COUNT(*) FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("1", rows, StringComparison.Ordinal);
    }

    /// <summary>The same key removes the rows one time only and returns the stored response.</summary>
    [Fact]
    public async Task TheSameRequestIdRemovesTheRowsOneTimeOnly()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await UpdateToolTests.SeedJobAsync(client, "pending");
        var requestId = InsertToolTests.NewRequestId();
        var where = new[] { UpdateToolTests.Condition("payload", "eq", marker) };

        var first = await CallDeleteAsync(client, requestId, "jobs", where, maxRows: 1);

        // The row is gone. A second execution would answer rowsAffected: 0, thus an answer that is
        // identical to the first proves that the cache replied.
        var second = await CallDeleteAsync(client, requestId, "jobs", where, maxRows: 1);

        Assert.Equal(first, second);
        Assert.Contains("rowsAffected: 1", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsentRequestIdIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallDeleteAsync(
            client,
            requestId: null,
            "jobs",
            [UpdateToolTests.Condition("id", "eq", 41)],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownTableIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallDeleteAsync(
            client,
            InsertToolTests.NewRequestId(),
            "no_such_table",
            [UpdateToolTests.Condition("id", "eq", 41)],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <c>sqlite_%</c> object is internal SQLite state and is never a write target, also with the
    /// write permission.
    /// </summary>
    [Fact]
    public async Task AnInternalSqliteObjectIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallDeleteAsync(
            client,
            InsertToolTests.NewRequestId(),
            "sqlite_sequence",
            [UpdateToolTests.Condition("name", "eq", "jobs")],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheWritePermissionTheDeleteToolIsAbsent()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("delete", tools.Select(t => t.Name));
    }

    /// <summary>
    /// Adds one job with a unique payload marker and <paramref name="tags"/> cascading child rows.
    /// Returns the marker and the new job identifier.
    /// </summary>
    private static async Task<(string Marker, long JobId)> SeedJobWithTagsAsync(McpClient client, int tags)
    {
        var marker = await UpdateToolTests.SeedJobAsync(client, "pending");
        var jobId = await ReadJobIdAsync(client, marker);

        for (var i = 0; i < tags; i++)
        {
            await InsertToolTests.CallInsertAsync(
                client, InsertToolTests.NewRequestId(), "job_tags", new Dictionary<string, object?>
                {
                    ["job_id"] = jobId,
                    ["tag"] = $"tag-{i}",
                });
        }

        return (marker, jobId);
    }

    /// <summary>Reads back the identifier of the job that carries one marker.</summary>
    private static async Task<long> ReadJobIdAsync(McpClient client, string marker)
    {
        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT id FROM jobs WHERE payload = '{marker}'");

        // The TOON result holds a header line and then the one value.
        var value = rows
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => long.TryParse(line, out var parsed) ? parsed : (long?)null)
            .FirstOrDefault(parsed => parsed is not null);

        Assert.NotNull(value);
        return value.Value;
    }

    /// <summary>
    /// Calls <c>delete</c> and returns the text. An argument that is <c>null</c> here is left out of
    /// the request, thus a test can prove that an absent argument is <c>InvalidWrite</c> and not an
    /// error of the SDK binder.
    /// </summary>
    internal static async Task<string> CallDeleteAsync(
        McpClient client,
        string? requestId,
        string? table,
        IEnumerable<Dictionary<string, object?>>? where,
        int? maxRows)
    {
        var arguments = new Dictionary<string, object?>();
        if (requestId is not null)
        {
            arguments["requestId"] = requestId;
        }

        if (table is not null)
        {
            arguments["table"] = table;
        }

        if (where is not null)
        {
            arguments["where"] = where.ToList();
        }

        if (maxRows is not null)
        {
            arguments["maxRows"] = maxRows;
        }

        var result = await client.CallToolAsync(
            "delete", arguments, cancellationToken: TestContext.Current.CancellationToken);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (result.IsError == true)
        {
            throw new McpToolFailure(text);
        }

        return text;
    }
}
