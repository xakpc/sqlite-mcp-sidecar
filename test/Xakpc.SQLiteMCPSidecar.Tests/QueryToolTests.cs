using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The <c>query</c> tool. It runs one caller-supplied read statement and returns the rows as TOON,
/// inside the row limit, the byte limit and the timeout.
/// </summary>
public sealed class QueryToolTests
{
    [Fact]
    public async Task QueryReturnsToonRows()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await CallQueryAsync(client, "SELECT id, status FROM jobs WHERE status = 'failed' ORDER BY id");

        // The header states the row count and the column names, thus an agent reads the shape from
        // the first line.
        Assert.StartsWith("rows[2]{id,status}:", text, StringComparison.Ordinal);
        Assert.Contains("41,failed", text, StringComparison.Ordinal);
        Assert.Contains("52,failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTruncatedFlagIsAlwaysPresent()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await CallQueryAsync(client, "SELECT id FROM jobs ORDER BY id");

        // An agent never has to calculate completeness.
        Assert.Contains("truncated: false", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyResultIsNotAFailure()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await CallQueryAsync(client, "SELECT id FROM jobs WHERE status = 'nothing-matches'");

        Assert.StartsWith("rows[0]", text, StringComparison.Ordinal);
        Assert.Contains("truncated: false", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The authorizer allowlist accepts SQL functions by default and rejects a named few. A policy
    /// that rejected every function would reject ordinary SQL, thus this test guards the allowlist
    /// against becoming useless.
    /// </summary>
    [Fact]
    public async Task OrdinarySqlFunctionsStillWork()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await CallQueryAsync(
            client,
            "SELECT count(*) AS n, upper(substr(max(status), 1, 4)) AS s FROM jobs");

        Assert.Contains("4,PEND", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A join returns a repeated column name. That is an ordinary agent query and not a failure.
    /// </summary>
    [Fact]
    public async Task AJoinWithRepeatedColumnNamesSucceeds()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await CallQueryAsync(
            client,
            "SELECT j.id, u.id FROM jobs j JOIN users u ON u.id = j.owner_id ORDER BY j.id LIMIT 1");

        Assert.Contains("{id,id_2}", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A recursive CTE is the case that the VDBE operation limit does not catch: the program is
    /// short and it runs for ever. The timeout must interrupt SQLite itself.
    /// </summary>
    [Fact]
    public async Task ARunawayQueryIsInterruptedByTheTimeout()
    {
        await using var harness = SidecarHarness.Create(
            "schema,read",
            settings: new Dictionary<string, string?> { ["QUERY_TIMEOUT_SECONDS"] = "1" });
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallQueryAsync(
            client,
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c"));

        Assert.NotNull(failure);
        Assert.Contains("QueryTimedOut", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRowLimitTruncates()
    {
        await using var harness = SidecarHarness.Create(
            "schema,read",
            settings: new Dictionary<string, string?> { ["MAX_ROWS"] = "2" });
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The sample database holds four jobs.
        var text = await CallQueryAsync(client, "SELECT id FROM jobs ORDER BY id");

        // A partial answer with an honest flag is more useful to an agent than an error.
        Assert.StartsWith("rows[2]", text, StringComparison.Ordinal);
        Assert.Contains("truncated: true", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheByteLimitTruncates()
    {
        await using var harness = SidecarHarness.Create(
            "schema,read",
            settings: new Dictionary<string, string?> { ["MAX_RESULT_BYTES"] = "1024" });
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Each row carries about 400 bytes, thus the row count stays low while the bytes run out.
        var text = await CallQueryAsync(
            client,
            "SELECT id, hex(randomblob(200)) AS filler FROM jobs ORDER BY id");

        Assert.Contains("truncated: true", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ResultTooLarge</c> has exactly one cause: one single row is larger than the byte budget,
    /// and the sidecar cannot send a part of a row.
    /// </summary>
    [Fact]
    public async Task OneOversizedRowIsResultTooLarge()
    {
        await using var harness = SidecarHarness.Create(
            "schema,read",
            settings: new Dictionary<string, string?> { ["MAX_RESULT_BYTES"] = "1024" });
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(
            () => CallQueryAsync(client, "SELECT hex(randomblob(4000)) AS huge"));

        Assert.NotNull(failure);
        Assert.Contains("ResultTooLarge", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The owning application keeps writing while the agent reads. That is the normal deployment,
    /// and the read must not block it or fail.
    /// </summary>
    [Fact]
    public async Task AReadSucceedsWhileTheApplicationWrites()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        if (harness.DatabasePath is null)
        {
            Assert.Skip("This test writes to the database file, and the external sidecar did not share its path.");
        }

        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        // A second SQLite client, exactly like the owning application.
        await using var owner = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = harness.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);
        await owner.OpenAsync(TestContext.Current.CancellationToken);

        await using (var insert = owner.CreateCommand())
        {
            insert.CommandText = "INSERT INTO jobs (id, status, retry) VALUES (77, 'running', 0)";
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var text = await CallQueryAsync(client, "SELECT id FROM jobs WHERE id = 77");

        Assert.Contains("77", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedSqlIsInvalidQuery()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallQueryAsync(client, "SELEKT * FROM jobs"));

        Assert.NotNull(failure);
        Assert.Contains("InvalidQuery", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownTableIsInvalidQueryAndNamesNoPath()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallQueryAsync(client, "SELECT * FROM no_such_table"));

        Assert.NotNull(failure);
        Assert.Contains("InvalidQuery", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("app.db", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheReadPermissionTheQueryToolIsAbsent()
    {
        await using var harness = SidecarHarness.Create("schema");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("query", tools.Select(t => t.Name));
    }

    /// <summary>
    /// Calls <c>query</c> and returns the text. A tool failure arrives as a result with
    /// <c>IsError</c> set and not as a transport exception, thus the helper raises it so that a test
    /// asserts on the error code the same way for both shapes.
    /// </summary>
    internal static async Task<string> CallQueryAsync(McpClient client, string sql)
    {
        var result = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?> { ["sql"] = sql },
            cancellationToken: TestContext.Current.CancellationToken);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (result.IsError == true)
        {
            throw new McpToolFailure(text);
        }

        return text;
    }
}

/// <summary>A <c>tools/call</c> that came back with <c>IsError</c>. The message is what the agent reads.</summary>
public sealed class McpToolFailure(string message) : Exception(message)
{
}
