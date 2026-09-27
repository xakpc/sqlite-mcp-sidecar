using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The hard boundaries. The sidecar always rejects these actions, and no permission changes that.
/// </summary>
/// <remarks>
/// <para>
/// These are the mandatory security tests. Each statement runs through the <c>query</c> tool over a
/// real MCP client, thus the same test proves an external sidecar and later the published Linux
/// image. The sandbox depends on the native SQLite build, thus a Windows developer run is necessary
/// and not sufficient.
/// </para>
/// <para>
/// Phase 6 runs this same list again under <c>danger-raw-write</c> through
/// <c>execute_write_sql</c>: <c>danger-raw-write</c> is raw DML inside this sandbox and not
/// unrestricted SQLite.
/// </para>
/// </remarks>
public sealed class SandboxBoundaryTests
{
    /// <summary>
    /// A caller cannot reach another file, change the schema, change a pragma or run native code.
    /// </summary>
    [Theory]
    // ATTACH is a file-access primitive. The authorizer and SQLITE_LIMIT_ATTACHED both block it.
    [InlineData("ATTACH DATABASE '/tmp/x.db' AS x")]
    [InlineData("DETACH DATABASE x")]
    // DDL would break the owning application.
    [InlineData("CREATE TABLE hacked(id)")]
    [InlineData("DROP TABLE jobs")]
    [InlineData("ALTER TABLE jobs ADD COLUMN sneaky TEXT")]
    // writable_schema is direct schema corruption. journal_mode belongs to the owning application.
    [InlineData("PRAGMA writable_schema = ON")]
    [InlineData("PRAGMA journal_mode = OFF")]
    // A write has no path through the read tool, with or without a write permission elsewhere.
    [InlineData("UPDATE jobs SET retry = 1 WHERE id = 41")]
    [InlineData("DELETE FROM jobs WHERE id = 41")]
    [InlineData("INSERT INTO jobs (id, status) VALUES (99, 'pending')")]
    // Transaction control belongs to the server, never to a caller.
    [InlineData("BEGIN IMMEDIATE")]
    public async Task TheStatementIsRejected(string sql)
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => QueryToolTests.CallQueryAsync(client, sql));

        Assert.NotNull(failure);
        Assert.Contains("QueryRejected", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extension loading is arbitrary native code inside the sidecar process, and no permission
    /// enables it.
    /// </summary>
    /// <remarks>
    /// The code is <c>InvalidQuery</c> and not <c>QueryRejected</c>, because the block is one layer
    /// lower than the authorizer: extension loading is off on the connection, thus SQLite never
    /// registers the function and reports an unknown name. The authorizer also denies the name, and
    /// that rule stays as the second block. The assertion accepts either code, because both mean
    /// that the primitive is absent and a stricter assertion would fail on a safe change.
    /// </remarks>
    [Fact]
    public async Task LoadExtensionIsNotAvailable()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(
            () => QueryToolTests.CallQueryAsync(client, "SELECT load_extension('/tmp/malicious.so')"));

        Assert.NotNull(failure);
        Assert.True(
            failure.Message.Contains("InvalidQuery", StringComparison.Ordinal)
            || failure.Message.Contains("QueryRejected", StringComparison.Ordinal),
            $"Expected a sidecar rejection code, and the message was: {failure.Message}");
    }

    /// <summary>
    /// <c>VACUUM INTO</c> writes a database copy to a caller-chosen path, thus it is a file
    /// exfiltration primitive and it belongs with the DDL rejections and not with the backup
    /// feature.
    /// </summary>
    [Fact]
    public async Task VacuumIntoIsRejectedAndWritesNoFile()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var target = Path.Combine(Path.GetTempPath(), $"sidecar-vacuum-probe-{Guid.NewGuid():N}.db");
        var failure = await Record.ExceptionAsync(
            () => QueryToolTests.CallQueryAsync(client, $"VACUUM INTO '{target.Replace("\\", "/")}'"));

        Assert.NotNull(failure);
        Assert.False(File.Exists(target), "VACUUM INTO must not write a file.");
    }

    /// <summary>
    /// One statement for each request. The count comes from preparation: a semicolon appears inside
    /// a string literal and inside a comment, thus counting semicolons is not a check.
    /// </summary>
    /// <remarks>
    /// The first statement of each case is a legal read, thus the tail check is what rejects the
    /// request. A pair whose first statement is itself denied, for example
    /// <c>UPDATE ...; DELETE ...</c>, never reaches the tail check: the authorizer stops the
    /// preparation of the first statement and the code is <c>QueryRejected</c>.
    /// </remarks>
    [Theory]
    [InlineData("SELECT 1; SELECT 2")]
    [InlineData("SELECT 1; DROP TABLE jobs")]
    [InlineData("SELECT id FROM jobs; DELETE FROM logs")]
    public async Task MoreThanOneStatementIsRejected(string sql)
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => QueryToolTests.CallQueryAsync(client, sql));

        Assert.NotNull(failure);
        // A trailing statement is a request-shape problem, thus InvalidQuery and not QueryRejected:
        // the first statement is a legal read.
        Assert.Contains("InvalidQuery", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejection states the code and nothing else. A SQLite message often names the database
    /// file, thus a provider message never reaches a caller without a change.
    /// </summary>
    [Fact]
    public async Task ARejectionDisclosesNoPathAndNoToken()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(
            () => QueryToolTests.CallQueryAsync(client, "ATTACH DATABASE '/tmp/x.db' AS x"));

        Assert.NotNull(failure);
        Assert.DoesNotContain("app.db", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Token, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetTempPath(), failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sandbox is not a feature flag. A deployment with every permission rejects the same
    /// statements, because the policy comes from the tool that runs and not from the permission set.
    /// </summary>
    [Fact]
    public async Task ThePermissionSetDoesNotWeakenTheBoundaries()
    {
        await using var harness = SidecarHarness.Create("schema,read,write,backup,diagnostics,danger-raw-write");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => QueryToolTests.CallQueryAsync(client, "DROP TABLE jobs"));

        Assert.NotNull(failure);
        Assert.Contains("QueryRejected", failure.Message, StringComparison.Ordinal);
    }
}
