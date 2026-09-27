using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The <c>insert</c> tool. The caller sends no SQL: it names a table and a column-to-value object,
/// and the server builds one parameterized <c>INSERT</c> that adds exactly one row.
/// </summary>
/// <remarks>
/// Each test seeds the row that it then reads, and it marks that row with a unique value. The
/// external target shares one live database across the whole suite, thus a test must not depend on
/// the row identifiers of the fixture.
/// </remarks>
public sealed class InsertToolTests
{
    private const string WritePermissions = "schema,read,write";

    [Fact]
    public async Task InsertAddsOneRowAndReportsItsRowid()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = NewMarker();
        var text = await CallInsertAsync(client, NewRequestId(), "jobs", new Dictionary<string, object?>
        {
            ["status"] = "pending",
            ["retry"] = 0,
            ["payload"] = marker,
        });

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);
        Assert.Contains("rowid: ", text, StringComparison.Ordinal);

        // The row is really in the database, and the values arrived unchanged.
        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT status, retry FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("pending,0", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// A JSON null becomes a SQLite NULL, and not the text "null".
    /// </summary>
    [Fact]
    public async Task ANullValueBecomesSqliteNull()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = NewMarker();
        await CallInsertAsync(client, NewRequestId(), "jobs", new Dictionary<string, object?>
        {
            ["status"] = "done",
            ["payload"] = marker,
            ["owner_id"] = null,
        });

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT owner_id IS NULL FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("1", rows, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownTableIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallInsertAsync(
            client, NewRequestId(), "no_such_table", new Dictionary<string, object?> { ["a"] = 1 }));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownColumnIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallInsertAsync(
            client, NewRequestId(), "jobs", new Dictionary<string, object?> { ["not_a_column"] = 1 }));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A view is not a write target. An <c>INSERT</c> into a view needs an <c>INSTEAD OF</c> trigger,
    /// and the SQLite failure would tell the agent much less than this rejection does.
    /// </summary>
    [Fact]
    public async Task AViewTargetIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallInsertAsync(
            client, NewRequestId(), "failed_jobs", new Dictionary<string, object?> { ["status"] = "failed" }));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The <c>jobs.status</c> CHECK constraint accepts four values only.</summary>
    [Fact]
    public async Task ACheckConstraintViolationIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallInsertAsync(
            client, NewRequestId(), "jobs", new Dictionary<string, object?> { ["status"] = "not-a-status" }));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The write connection sets <c>ForeignKeys = true</c>. SQLite defaults that setting to off, thus
    /// this test proves the rule and not the library default.
    /// </summary>
    [Fact]
    public async Task AForeignKeyViolationIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallInsertAsync(
            client, NewRequestId(), "logs", new Dictionary<string, object?>
            {
                ["job_id"] = 987654321,
                ["level"] = "error",
                ["message"] = "no such job",
            }));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyValuesObjectIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallInsertAsync(
            client, NewRequestId(), "jobs", new Dictionary<string, object?>()));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An object has no SQLite equivalent. A silent conversion to a JSON string would store a value
    /// that the agent did not ask for.
    /// </summary>
    [Fact]
    public async Task AnObjectValueIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallInsertAsync(
            client, NewRequestId(), "jobs", new Dictionary<string, object?>
            {
                ["status"] = "pending",
                ["payload"] = new Dictionary<string, object?> { ["kind"] = "import" },
            }));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectionDisclosesNoPathAndNoToken()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallInsertAsync(
            client, NewRequestId(), "jobs", new Dictionary<string, object?> { ["status"] = "not-a-status" }));

        Assert.NotNull(failure);
        Assert.DoesNotContain("app.db", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Token, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheWritePermissionTheInsertToolIsAbsent()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("insert", tools.Select(t => t.Name));
    }

    /// <summary>A marker value that no other test and no fixture row holds.</summary>
    internal static string NewMarker() => $"marker-{Guid.NewGuid():N}";

    /// <summary>A fresh idempotency key. Each call is new work unless a test reuses the value.</summary>
    internal static string NewRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Calls <c>insert</c> and returns the text. An argument that is <c>null</c> here is left out of
    /// the request, thus a test can prove that an absent argument is <c>InvalidWrite</c> and not an
    /// error of the SDK binder.
    /// </summary>
    internal static async Task<string> CallInsertAsync(
        McpClient client,
        string? requestId,
        string? table,
        IDictionary<string, object?>? values)
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

        if (values is not null)
        {
            arguments["values"] = values;
        }

        var result = await client.CallToolAsync(
            "insert", arguments, cancellationToken: TestContext.Current.CancellationToken);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (result.IsError == true)
        {
            throw new McpToolFailure(text);
        }

        return text;
    }
}
