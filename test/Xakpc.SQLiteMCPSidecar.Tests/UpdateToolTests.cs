using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The <c>update</c> tool. The caller sends no SQL: it names a table, the new values, a flat filter
/// and <c>maxRows</c>, and the server builds one parameterized <c>UPDATE</c>.
/// </summary>
/// <remarks>
/// Each test seeds the row that it then changes, and it marks that row with a unique value. The
/// external target shares one live database across the whole suite, thus a test must not depend on
/// the row identifiers of the fixture.
/// </remarks>
public sealed class UpdateToolTests
{
    private const string WritePermissions = "schema,read,write";

    [Fact]
    public async Task UpdateChangesTheRowsThatTheFilterSelects()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await SeedJobAsync(client, "pending");

        var text = await CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done", ["retry"] = 3 },
            [Condition("payload", "eq", marker)],
            maxRows: 1);

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT status, retry FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("done,3", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>rowsChanged</c> is the number that <c>maxRows</c> bounds. It equals <c>rowsAffected</c> when
    /// no cascade and no trigger widen the write.
    /// </summary>
    [Fact]
    public async Task UpdateReportsTheRowsItChanged()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await SeedJobAsync(client, "pending");

        var text = await CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "running" },
            [Condition("payload", "eq", marker)],
            maxRows: 1);

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);
        Assert.Contains("rowsChanged: 1", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The filter is required. There is no structured equivalent of <c>UPDATE jobs SET ...</c> with no
    /// <c>WHERE</c>, and the rejection happens before any database work.
    /// </summary>
    [Fact]
    public async Task AnAbsentFilterIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            where: null,
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyFilterIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsentMaxRowsIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("id", "eq", 41)],
            maxRows: null));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMaxRowsBelowOneIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("id", "eq", 41)],
            maxRows: 0));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bounded pre-count rejects a filter that is wider than <c>maxRows</c> before the statement
    /// runs, thus the rows are unchanged.
    /// </summary>
    [Fact]
    public async Task ABroadFilterIsRejectedAndChangesNothing()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Three rows share one marker, and the call permits two.
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

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("payload", "eq", marker)],
            maxRows: 2));

        Assert.NotNull(failure);
        Assert.Contains("WriteLimitExceeded", failure.Message, StringComparison.Ordinal);

        // Nothing changed: each of the three rows still has its original status.
        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT COUNT(*) FROM jobs WHERE payload = '{marker}' AND status = 'pending'");
        Assert.Contains("3", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// The effective limit is <c>min(client maxRows, SQLITE_SIDECAR_MAX_WRITE_ROWS)</c>. A caller
    /// cannot raise its own bound past the cap of the deployment.
    /// </summary>
    /// <remarks>
    /// The cap is a process value, thus this test needs its own sidecar and is skipped on the external
    /// target. <c>WriteBudgetTests</c> has the same shape.
    /// </remarks>
    [Fact]
    public async Task TheDeploymentCapWinsOverALargerMaxRows()
    {
        await using var harness = SidecarHarness.Create(
            WritePermissions,
            settings: new Dictionary<string, string?> { ["MAX_WRITE_ROWS"] = "1" });
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = InsertToolTests.NewMarker();
        for (var i = 0; i < 2; i++)
        {
            await InsertToolTests.CallInsertAsync(
                client, InsertToolTests.NewRequestId(), "jobs", new Dictionary<string, object?>
                {
                    ["status"] = "pending",
                    ["payload"] = marker,
                });
        }

        // The caller asks for 100 and the deployment permits 1, thus two matching rows are too many.
        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("payload", "eq", marker)],
            maxRows: 100));

        Assert.NotNull(failure);
        Assert.Contains("WriteLimitExceeded", failure.Message, StringComparison.Ordinal);

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT COUNT(*) FROM jobs WHERE payload = '{marker}' AND status = 'pending'");
        Assert.Contains("2", rows, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownColumnInTheFilterIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("no_such_column", "eq", 1)],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The operator is a string and an unknown value must reach the error model. An enum would make
    /// the binder of the SDK throw, and the SDK then masks the message and the agent gets no code.
    /// </summary>
    [Fact]
    public async Task AnUnknownOperatorIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("status", "like", "pend%")],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>col = NULL</c> is never true. A filter that silently matches nothing reports
    /// <c>rowsAffected: 0</c> and looks correct, which is worse for an agent than a rejection.
    /// </summary>
    [Fact]
    public async Task ComparingWithNullIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("payload", "eq", null)],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheIsNullOperatorMatchesAnAbsentValue()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        // One row with a NULL payload, found again by its unique retry count.
        var retry = Random.Shared.Next(100_000, 999_999);
        await InsertToolTests.CallInsertAsync(
            client, InsertToolTests.NewRequestId(), "jobs", new Dictionary<string, object?>
            {
                ["status"] = "pending",
                ["retry"] = retry,
            });

        var text = await CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("retry", "eq", retry), Condition("payload", "is-null", null, omitValue: true)],
            maxRows: 1);

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT status FROM jobs WHERE retry = {retry}");
        Assert.Contains("done", rows, StringComparison.Ordinal);
    }

    /// <summary>Two conditions join with <c>AND</c>. There is no <c>or</c> and no nesting.</summary>
    [Fact]
    public async Task TwoConditionsJoinWithAnd()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = InsertToolTests.NewMarker();
        await InsertToolTests.CallInsertAsync(
            client, InsertToolTests.NewRequestId(), "jobs", new Dictionary<string, object?>
            {
                ["status"] = "pending",
                ["retry"] = 0,
                ["payload"] = marker,
            });
        await InsertToolTests.CallInsertAsync(
            client, InsertToolTests.NewRequestId(), "jobs", new Dictionary<string, object?>
            {
                ["status"] = "failed",
                ["retry"] = 0,
                ["payload"] = marker,
            });

        // Both rows share the marker, thus only the second condition selects one of them.
        var text = await CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["retry"] = 9 },
            [Condition("payload", "eq", marker), Condition("status", "eq", "failed")],
            maxRows: 1);

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT status, retry FROM jobs WHERE payload = '{marker}' ORDER BY status");
        Assert.Contains("failed,9", rows, StringComparison.Ordinal);
        Assert.Contains("pending,0", rows, StringComparison.Ordinal);
    }

    /// <summary>The same key returns the stored response and applies the change one time only.</summary>
    [Fact]
    public async Task TheSameRequestIdAppliesTheUpdateOneTimeOnly()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var marker = await SeedJobAsync(client, "pending");
        var requestId = InsertToolTests.NewRequestId();
        var values = new Dictionary<string, object?> { ["retry"] = 5 };
        var where = new[] { Condition("payload", "eq", marker) };

        var first = await CallUpdateAsync(client, requestId, "jobs", values, where, maxRows: 1);
        var second = await CallUpdateAsync(client, requestId, "jobs", values, where, maxRows: 1);

        // Byte-identical: a retry must look like the first call.
        Assert.Equal(first, second);

        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT retry FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("5", rows, StringComparison.Ordinal);
    }

    /// <summary>A key that an <c>insert</c> used is different work for an <c>update</c>.</summary>
    [Fact]
    public async Task ARequestIdFromAnInsertConflictsWithAnUpdate()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var requestId = InsertToolTests.NewRequestId();
        var marker = InsertToolTests.NewMarker();
        await InsertToolTests.CallInsertAsync(
            client, requestId, "jobs", new Dictionary<string, object?>
            {
                ["status"] = "pending",
                ["payload"] = marker,
            });

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            requestId,
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("payload", "eq", marker)],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbsentRequestIdIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            requestId: null,
            "jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("id", "eq", 41)],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A view is not a write target.</summary>
    [Fact]
    public async Task AViewIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "failed_jobs",
            new Dictionary<string, object?> { ["status"] = "done" },
            [Condition("id", "eq", 41)],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectionDisclosesNoPathAndNoToken()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => CallUpdateAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "not-a-status" },
            [Condition("id", "eq", 41)],
            maxRows: 1));

        Assert.NotNull(failure);
        Assert.DoesNotContain("app.db", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Token, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheWritePermissionTheUpdateToolIsAbsent()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("update", tools.Select(t => t.Name));
    }

    /// <summary>
    /// The filter reaches the agent as a list of objects with three named members. A schema that the
    /// agent cannot fill correctly is the main reason a tool goes unused.
    /// </summary>
    [Fact]
    public async Task TheFilterArgumentHasAUsableSchema()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var update = Assert.Single(tools, t => t.Name == "update");

        var where = update.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("where");
        var schema = where.GetRawText();

        Assert.Contains("array", schema, StringComparison.Ordinal);
        Assert.Contains("column", schema, StringComparison.Ordinal);
        Assert.Contains("operator", schema, StringComparison.Ordinal);
        Assert.Contains("value", schema, StringComparison.Ordinal);
    }

    /// <summary>Adds one job with a unique payload marker, and returns the marker.</summary>
    internal static async Task<string> SeedJobAsync(McpClient client, string status)
    {
        var marker = InsertToolTests.NewMarker();
        await InsertToolTests.CallInsertAsync(
            client, InsertToolTests.NewRequestId(), "jobs", new Dictionary<string, object?>
            {
                ["status"] = status,
                ["retry"] = 0,
                ["payload"] = marker,
            });

        return marker;
    }

    /// <summary>
    /// One filter condition, as the wire shape. <paramref name="omitValue"/> leaves the member out of
    /// the object, which is what an agent sends for <c>is-null</c> and <c>is-not-null</c>.
    /// </summary>
    internal static Dictionary<string, object?> Condition(
        string? column,
        string? @operator,
        object? value,
        bool omitValue = false)
    {
        var condition = new Dictionary<string, object?>
        {
            ["column"] = column,
            ["operator"] = @operator,
        };

        if (!omitValue)
        {
            condition["value"] = value;
        }

        return condition;
    }

    /// <summary>
    /// Calls <c>update</c> and returns the text. An argument that is <c>null</c> here is left out of
    /// the request, thus a test can prove that an absent argument is <c>InvalidWrite</c> and not an
    /// error of the SDK binder.
    /// </summary>
    internal static async Task<string> CallUpdateAsync(
        McpClient client,
        string? requestId,
        string? table,
        IDictionary<string, object?>? values,
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

        if (values is not null)
        {
            arguments["values"] = values;
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
            "update", arguments, cancellationToken: TestContext.Current.CancellationToken);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (result.IsError == true)
        {
            throw new McpToolFailure(text);
        }

        return text;
    }
}
