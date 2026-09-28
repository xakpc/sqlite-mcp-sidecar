using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The <c>diagnostics</c> tool over a real MCP client. It reports a fixed value set and takes no
/// arguments.
/// </summary>
public sealed class DiagnosticsToolTests
{
    private const string Permissions = "schema,read,diagnostics";

    [Fact]
    public async Task DiagnosticsReportsTheWholeFixedValueSet()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await CallDiagnosticsAsync(client);

        Assert.Contains("sqliteVersion: 3.", text);
        Assert.Contains("journalMode: wal", text);
        Assert.Contains("pageSize: ", text);
        Assert.Contains("pageCount: ", text);
        Assert.Contains("quickCheck: ok", text);
    }

    [Fact]
    public async Task DiagnosticsReportsARollbackJournalDatabase()
    {
        // The journal mode is the value that matters most in this set: a database that is not in WAL
        // mode blocks the readers of the owning application during a sidecar write.
        await using var harness = SidecarHarness.Create(Permissions, journalMode: "delete");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await CallDiagnosticsAsync(client);

        Assert.Contains("journalMode: delete", text);
    }

    [Fact]
    public async Task DiagnosticsReportsAPlausibleSize()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await CallDiagnosticsAsync(client);

        var pageSize = long.Parse(ValueOf(text, "pageSize"), System.Globalization.CultureInfo.InvariantCulture);
        var pageCount = long.Parse(ValueOf(text, "pageCount"), System.Globalization.CultureInfo.InvariantCulture);

        // A SQLite page size is a power of two between 512 and 65536, and the sample database is not empty.
        Assert.InRange(pageSize, 512, 65536);
        Assert.True(pageCount > 0, "The page count must be positive.");
    }

    [Fact]
    public async Task DiagnosticsDiscloseNoPathAndNoRowData()
    {
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var text = await CallDiagnosticsAsync(client);

        Assert.DoesNotContain("app.db", text);
        Assert.DoesNotContain("ada@example.com", text);
    }

    [Fact]
    public async Task DiagnosticsIsAbsentWithoutThePermission()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("diagnostics", tools.Select(t => t.Name));
    }

    [Fact]
    public async Task DiagnosticsTakesNoArguments()
    {
        // INVARIANT: the value set is fixed. A caller-chosen PRAGMA would be a write primitive and an
        // information leak, thus the tool exposes no argument at all.
        await using var harness = SidecarHarness.Create(Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var diagnostics = tools.Single(t => t.Name == "diagnostics");

        var properties = diagnostics.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.Empty(properties.EnumerateObject());
    }

    private static async Task<string> CallDiagnosticsAsync(McpClient client)
    {
        var result = await client.CallToolAsync("diagnostics", cancellationToken: TestContext.Current.CancellationToken);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return result.IsError == true ? throw new McpToolFailure(text) : text;
    }

    private static string ValueOf(string text, string key)
    {
        var line = text.Split('\n').First(l => l.StartsWith(key + ": ", StringComparison.Ordinal));
        return line[(key.Length + 2)..].Trim();
    }
}
