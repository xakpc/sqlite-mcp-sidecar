using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The rolling per-minute write budget. A broad filter is one failure mode; many small valid writes
/// are another, and this control covers the second one.
/// </summary>
/// <remarks>
/// The budget is a process value, thus a test needs its own sidecar with a small cap. Such a test is
/// skipped on the external target, which cannot restart the process. <c>RateLimitTests</c> has the
/// same shape for the request budget.
/// </remarks>
public sealed class WriteBudgetTests
{
    private const string WritePermissions = "schema,read,write";

    [Fact]
    public async Task ManySmallWritesRunOutOfBudget()
    {
        await using var harness = SidecarHarness.Create(
            WritePermissions,
            settings: new Dictionary<string, string?> { ["MAX_WRITE_ROWS_PER_MINUTE"] = "3" });
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Three rows fit the budget. The check is a gate and not a reservation, thus the fourth call is
        // the first one that finds the budget used up.
        for (var i = 0; i < 3; i++)
        {
            var text = await InsertToolTests.CallInsertAsync(
                client,
                InsertToolTests.NewRequestId(),
                "jobs",
                new Dictionary<string, object?> { ["status"] = "pending", ["payload"] = InsertToolTests.NewMarker() });

            Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);
        }

        var failure = await Record.ExceptionAsync(() => InsertToolTests.CallInsertAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "pending", ["payload"] = InsertToolTests.NewMarker() }));

        Assert.NotNull(failure);
        Assert.Contains("WriteBudgetExceeded", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A replayed write writes no row, thus it consumes no budget. The deduplication check therefore
    /// runs before the budget check.
    /// </summary>
    [Fact]
    public async Task AReplayedWriteConsumesNoBudget()
    {
        await using var harness = SidecarHarness.Create(
            WritePermissions,
            settings: new Dictionary<string, string?> { ["MAX_WRITE_ROWS_PER_MINUTE"] = "1" });
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var requestId = InsertToolTests.NewRequestId();
        var values = new Dictionary<string, object?>
        {
            ["status"] = "pending",
            ["payload"] = InsertToolTests.NewMarker(),
        };

        var first = await InsertToolTests.CallInsertAsync(client, requestId, "jobs", values);

        // The one row of budget is now used. A new write would fail, and this retry must not.
        var replay = await InsertToolTests.CallInsertAsync(client, requestId, "jobs", values);

        Assert.Equal(first, replay);
    }

    /// <summary>
    /// A rejected write changed nothing, thus it must not consume the budget of a write that would
    /// succeed. The budget counts committed rows only.
    /// </summary>
    [Fact]
    public async Task ARejectedWriteConsumesNoBudget()
    {
        await using var harness = SidecarHarness.Create(
            WritePermissions,
            settings: new Dictionary<string, string?> { ["MAX_WRITE_ROWS_PER_MINUTE"] = "1" });
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var rejected = await Record.ExceptionAsync(() => InsertToolTests.CallInsertAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "not-a-status" }));
        Assert.NotNull(rejected);

        var text = await InsertToolTests.CallInsertAsync(
            client,
            InsertToolTests.NewRequestId(),
            "jobs",
            new Dictionary<string, object?> { ["status"] = "pending", ["payload"] = InsertToolTests.NewMarker() });

        Assert.Contains("rowsAffected: 1", text, StringComparison.Ordinal);
    }
}
