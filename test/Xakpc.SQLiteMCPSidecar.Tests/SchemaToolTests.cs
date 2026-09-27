using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The <c>schema</c> tool over a real MCP client. It returns DDL text and not table rows.
/// </summary>
public sealed class SchemaToolTests
{
    [Fact]
    public async Task SchemaReturnsUsableDdl()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ddl = await CallSchemaAsync(client);

        // One CREATE statement states the columns, the types, the nullability, the defaults, the
        // primary key, the foreign keys and the constraints.
        Assert.Contains("CREATE TABLE jobs", ddl);
        Assert.Contains("CREATE TABLE users", ddl);
        Assert.Contains("owner_id", ddl);
        Assert.Contains("REFERENCES users(id)", ddl);
        Assert.Contains("CREATE INDEX idx_jobs_status", ddl);
        Assert.Contains("CREATE VIEW failed_jobs", ddl);
    }

    [Fact]
    public async Task SchemaHidesTheInternalSqliteObjects()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ddl = await CallSchemaAsync(client);

        Assert.DoesNotContain("sqlite_autoindex", ddl);
        Assert.DoesNotContain("sqlite_sequence", ddl);
    }

    [Fact]
    public async Task SchemaReturnsNoRowData()
    {
        // TOON is for row data only, and the schema tool returns no rows.
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ddl = await CallSchemaAsync(client);

        Assert.DoesNotContain("ada@example.com", ddl);
        Assert.DoesNotContain("rows[", ddl);
    }

    [Fact]
    public async Task SchemaDoesNotDiscloseTheDatabasePath()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ddl = await CallSchemaAsync(client);

        Assert.DoesNotContain("app.db", ddl);
    }

    internal static async Task<string> CallSchemaAsync(ModelContextProtocol.Client.McpClient client)
    {
        var result = await client.CallToolAsync("schema", cancellationToken: TestContext.Current.CancellationToken);
        var text = string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));

        // A tool failure arrives as a result with IsError set, not as a transport exception.
        return result.IsError == true ? throw new McpToolFailure(text) : text;
    }
}
