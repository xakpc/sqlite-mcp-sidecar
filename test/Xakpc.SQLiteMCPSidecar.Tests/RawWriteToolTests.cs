using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The <c>execute_write_sql</c> tool of the <c>danger-raw-write</c> permission. The caller writes the
/// statement, thus the structured protections are gone and the SQLite sandbox is not.
/// </summary>
/// <remarks>
/// The hard boundaries of this tool live in <c>SandboxBoundaryTests</c>, which runs one list through
/// both <c>query</c> and this tool. This class covers what the tool does when it accepts a statement.
/// Each test marks its own rows, because the external target shares one live database.
/// </remarks>
public sealed class RawWriteToolTests
{
    private const string RawPermissions = "schema,read,danger-raw-write";

    [Fact]
    public async Task ARawInsertAddsTheRow()
    {
        await using var harness = SidecarHarness.Create(RawPermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = InsertToolTests.NewMarker();
        var text = await CallExecuteWriteSqlAsync(
            client,
            InsertToolTests.NewRequestId(),
            $"INSERT INTO jobs (status, retry, payload) VALUES ('pending', 0, '{marker}')");

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT status, retry FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("pending,0", rows, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARawUpdateChangesTheRows()
    {
        await using var harness = SidecarHarness.Create(RawPermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await SeedJobAsync(client, "pending", retry: 0);

        // An expression on the right side: this is the work that a structured update cannot express,
        // and it is the reason the permission exists.
        var text = await CallExecuteWriteSqlAsync(
            client,
            InsertToolTests.NewRequestId(),
            $"UPDATE jobs SET retry = retry + 3 WHERE payload = '{marker}'");

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);

        var rows = await QueryToolTests.CallQueryAsync(client, $"SELECT retry FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("3", rows, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARawDeleteRemovesTheRows()
    {
        await using var harness = SidecarHarness.Create(RawPermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await SeedJobAsync(client, "done", retry: 0);

        var text = await CallExecuteWriteSqlAsync(
            client, InsertToolTests.NewRequestId(), $"DELETE FROM jobs WHERE payload = '{marker}'");

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT count(*) AS n FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("0", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>RETURNING</c> clause answers through the same bounded TOON pipeline as a query.
    /// </summary>
    [Fact]
    public async Task ARawUpdateWithReturningAnswersWithToonRows()
    {
        await using var harness = SidecarHarness.Create(RawPermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await SeedJobAsync(client, "failed", retry: 1);

        var text = await CallExecuteWriteSqlAsync(
            client,
            InsertToolTests.NewRequestId(),
            $"UPDATE jobs SET retry = retry + 1 WHERE payload = '{marker}' RETURNING status, retry");

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);
        Assert.Contains("rows[1]{status,retry}:", text, StringComparison.Ordinal);
        Assert.Contains("failed,2", text, StringComparison.Ordinal);
        Assert.Contains("truncated: false", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rows of a <c>RETURNING</c> clause are produced while the write runs, thus a result that
    /// stops at the row limit must not stop the write.
    /// </summary>
    /// <remarks>
    /// This is the test of the drain invariant in <c>SqliteService.ExecuteWriteSqlAsync</c>. Without
    /// the drain the reader is abandoned after the first row and the remaining rows survive, which is a
    /// half-applied write that no error reports.
    /// </remarks>
    [Fact]
    public async Task ATruncatedReturningResultStillAppliesTheWholeWrite()
    {
        await using var harness = SidecarHarness.Create(
            RawPermissions,
            settings: new Dictionary<string, string?> { ["MAX_ROWS"] = "1" });
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tag = InsertToolTests.NewMarker();
        for (var i = 0; i < 3; i++)
        {
            await CallExecuteWriteSqlAsync(
                client,
                InsertToolTests.NewRequestId(),
                $"INSERT INTO jobs (status, payload) VALUES ('pending', '{tag}')");
        }

        var text = await CallExecuteWriteSqlAsync(
            client, InsertToolTests.NewRequestId(), $"DELETE FROM jobs WHERE payload = '{tag}' RETURNING id");

        // The agent sees one row and an honest flag, and the database lost all three.
        Assert.Contains("rowsAffected: 3", text, StringComparison.Ordinal);
        Assert.Contains("truncated: true", text, StringComparison.Ordinal);

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT count(*) AS n FROM jobs WHERE payload = '{tag}'");
        Assert.Contains("0", rows, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARawWriteWithoutARequestIdIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(RawPermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallExecuteWriteSqlAsync(
            client, requestId: null, "DELETE FROM jobs WHERE id = -1"));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARawWriteWithoutASqlStatementIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(RawPermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallExecuteWriteSqlAsync(
            client, InsertToolTests.NewRequestId(), sql: null));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same <c>requestId</c> applies the statement one time and answers with the stored response.
    /// </summary>
    [Fact]
    public async Task TheSameRequestIdAppliesTheStatementOneTime()
    {
        await using var harness = SidecarHarness.Create(RawPermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await SeedJobAsync(client, "pending", retry: 0);
        var requestId = InsertToolTests.NewRequestId();
        var sql = $"UPDATE jobs SET retry = retry + 1 WHERE payload = '{marker}'";

        var first = await CallExecuteWriteSqlAsync(client, requestId, sql);
        var second = await CallExecuteWriteSqlAsync(client, requestId, sql);

        // A retry must look like the first call, byte for byte.
        Assert.Equal(first, second);

        // One increment and not two.
        var rows = await QueryToolTests.CallQueryAsync(client, $"SELECT retry FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("1", rows, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameRequestIdWithADifferentStatementIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(RawPermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await SeedJobAsync(client, "pending", retry: 0);
        var requestId = InsertToolTests.NewRequestId();

        await CallExecuteWriteSqlAsync(client, requestId, $"UPDATE jobs SET retry = 1 WHERE payload = '{marker}'");

        var failure = await Record.ExceptionAsync(() => CallExecuteWriteSqlAsync(
            client, requestId, $"UPDATE jobs SET retry = 9 WHERE payload = '{marker}'"));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Raw writes and structured writes share one budget counter. One regime is easier to explain than
    /// two, and the budget is a resource control on the database.
    /// </summary>
    [Fact]
    public async Task RawWriteRowsCountTowardTheWriteBudget()
    {
        await using var harness = SidecarHarness.Create(
            RawPermissions,
            settings: new Dictionary<string, string?> { ["MAX_WRITE_ROWS_PER_MINUTE"] = "3" });
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Three rows fit the budget. The check is a gate and not a reservation, thus the fourth call is
        // the first one that finds the budget used up.
        for (var i = 0; i < 3; i++)
        {
            var text = await CallExecuteWriteSqlAsync(
                client,
                InsertToolTests.NewRequestId(),
                $"INSERT INTO jobs (status, payload) VALUES ('pending', '{InsertToolTests.NewMarker()}')");

            Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);
        }

        var failure = await Record.ExceptionAsync(() => CallExecuteWriteSqlAsync(
            client,
            InsertToolTests.NewRequestId(),
            $"INSERT INTO jobs (status, payload) VALUES ('pending', '{InsertToolTests.NewMarker()}')"));

        Assert.NotNull(failure);
        Assert.Contains("WriteBudgetExceeded", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A constraint violation is <c>InvalidWrite</c> and the message names no path, exactly as it does
    /// on the structured path.
    /// </summary>
    [Fact]
    public async Task AConstraintViolationIsInvalidWriteAndDisclosesNoPath()
    {
        await using var harness = SidecarHarness.Create(RawPermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallExecuteWriteSqlAsync(
            client,
            InsertToolTests.NewRequestId(),
            "INSERT INTO jobs (status) VALUES ('not-a-status')"));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("app.db", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutThePermissionTheToolIsAbsent()
    {
        await using var harness = SidecarHarness.Create("schema,read,write");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("execute_write_sql", tools.Select(t => t.Name));
    }

    /// <summary>
    /// The backstop layer: a write permission is not a raw write permission, also on a direct call.
    /// </summary>
    [Fact]
    public async Task WithoutThePermissionADirectCallIsRejected()
    {
        await using var harness = SidecarHarness.Create("schema,read,write");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallExecuteWriteSqlAsync(
            client, InsertToolTests.NewRequestId(), "DELETE FROM jobs WHERE id = -1"));

        Assert.NotNull(failure);
        Assert.Contains("forbidden", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds one job row through the raw tool and returns its marker.</summary>
    private static async Task<string> SeedJobAsync(McpClient client, string status, int retry)
    {
        var marker = InsertToolTests.NewMarker();
        await CallExecuteWriteSqlAsync(
            client,
            InsertToolTests.NewRequestId(),
            $"INSERT INTO jobs (status, retry, payload) VALUES ('{status}', {retry}, '{marker}')");

        return marker;
    }

    /// <summary>
    /// Calls <c>execute_write_sql</c> and returns the text. An argument that is <c>null</c> here is
    /// left out of the request, thus a test can prove that an absent argument is <c>InvalidWrite</c>
    /// and not an error of the SDK binder.
    /// </summary>
    internal static async Task<string> CallExecuteWriteSqlAsync(McpClient client, string? requestId, string? sql)
    {
        var arguments = new Dictionary<string, object?>();
        if (requestId is not null)
        {
            arguments["requestId"] = requestId;
        }

        if (sql is not null)
        {
            arguments["sql"] = sql;
        }

        var result = await client.CallToolAsync(
            "execute_write_sql", arguments, cancellationToken: TestContext.Current.CancellationToken);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (result.IsError == true)
        {
            throw new McpToolFailure(text);
        }

        return text;
    }
}
