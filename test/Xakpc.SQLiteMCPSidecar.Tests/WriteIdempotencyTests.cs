using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The mandatory <c>requestId</c>. A write applies one time, also when the caller sends the request
/// two times.
/// </summary>
/// <remarks>
/// A write can commit and then lose its response, because the MCP HTTP transport is stateless and the
/// timeout is finite. An MCP client retries such a call. This is the most frequent way that a correct
/// agent damages a database, thus these tests are security tests and not convenience tests.
/// </remarks>
public sealed class WriteIdempotencyTests
{
    private const string WritePermissions = "schema,read,write";

    /// <summary>
    /// The second call must return the identical response and must write nothing. A retry has to look
    /// like the first call.
    /// </summary>
    [Fact]
    public async Task TheSameRequestIdAppliesTheWriteOneTime()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var requestId = InsertToolTests.NewRequestId();
        var marker = InsertToolTests.NewMarker();
        var values = new Dictionary<string, object?>
        {
            ["status"] = "pending",
            ["payload"] = marker,
        };

        var first = await InsertToolTests.CallInsertAsync(client, requestId, "jobs", values);
        var second = await InsertToolTests.CallInsertAsync(client, requestId, "jobs", values);

        Assert.Equal(first, second);

        // One row, not two.
        var rows = await QueryToolTests.CallQueryAsync(
            client, $"SELECT count(*) FROM jobs WHERE payload = '{marker}'");
        Assert.Contains("rows[1]", rows, StringComparison.Ordinal);
        Assert.Contains("\n  1", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// The caller asked for different work, thus the stored response is the wrong answer. A rejection
    /// is the honest one.
    /// </summary>
    [Fact]
    public async Task TheSameRequestIdWithANewPayloadIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var requestId = InsertToolTests.NewRequestId();

        await InsertToolTests.CallInsertAsync(client, requestId, "jobs", new Dictionary<string, object?>
        {
            ["status"] = "pending",
            ["payload"] = InsertToolTests.NewMarker(),
        });

        var failure = await Record.ExceptionAsync(() => InsertToolTests.CallInsertAsync(
            client, requestId, "jobs", new Dictionary<string, object?>
            {
                ["status"] = "done",
                ["payload"] = InsertToolTests.NewMarker(),
            }));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The column order of a JSON object is not significant. Without normalization the same request
    /// with a different key order would read as different work and break a correct retry.
    /// </summary>
    [Fact]
    public async Task TheKeyOrderOfTheValuesObjectDoesNotChangeTheIdentity()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var requestId = InsertToolTests.NewRequestId();
        var marker = InsertToolTests.NewMarker();

        var first = await InsertToolTests.CallInsertAsync(client, requestId, "jobs", new Dictionary<string, object?>
        {
            ["status"] = "pending",
            ["payload"] = marker,
        });

        var second = await InsertToolTests.CallInsertAsync(client, requestId, "jobs", new Dictionary<string, object?>
        {
            ["payload"] = marker,
            ["status"] = "pending",
        });

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task AnAbsentRequestIdIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => InsertToolTests.CallInsertAsync(
            client, requestId: null, "jobs", new Dictionary<string, object?> { ["status"] = "pending" }));

        // The code of the error model, and not a binder message of the SDK. Every failure that an
        // agent can cause belongs to the error model.
        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dictionary with caller-supplied keys is a memory-exhaustion primitive. The key length is one
    /// of the three bounds on the cache.
    /// </summary>
    [Fact]
    public async Task ARequestIdOver128CharactersIsInvalidWrite()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => InsertToolTests.CallInsertAsync(
            client, new string('k', 129), "jobs", new Dictionary<string, object?> { ["status"] = "pending" }));

        Assert.NotNull(failure);
        Assert.Contains("InvalidWrite", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure is never cached. The agent retries a rejected write with the same identifier, thus a
    /// cached failure would make the instruction of the error model impossible to follow.
    /// </summary>
    [Fact]
    public async Task ARequestIdThatFailedStaysUsable()
    {
        await using var harness = SidecarHarness.Create(WritePermissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var requestId = InsertToolTests.NewRequestId();

        // The CHECK constraint on jobs.status rejects this row.
        var failure = await Record.ExceptionAsync(() => InsertToolTests.CallInsertAsync(
            client, requestId, "jobs", new Dictionary<string, object?> { ["status"] = "not-a-status" }));
        Assert.NotNull(failure);

        // The same identifier now carries valid work, and it must execute.
        var marker = InsertToolTests.NewMarker();
        var text = await InsertToolTests.CallInsertAsync(client, requestId, "jobs", new Dictionary<string, object?>
        {
            ["status"] = "pending",
            ["payload"] = marker,
        });

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);
    }
}
