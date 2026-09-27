using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// A permission controls which MCP tools exist. Absence from the tool list is the primary control,
/// and the rejection of a direct call by name is the backstop.
/// </summary>
public sealed class PermissionGatingTests
{
    [Fact]
    public async Task TheSchemaPermissionExposesTheSchemaTool()
    {
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("schema", tools.Select(t => t.Name));
    }

    [Fact]
    public async Task WithoutTheSchemaPermissionTheToolIsAbsentFromTheList()
    {
        // A tool that the permission set does not cover does not appear and then fail.
        await using var harness = SidecarHarness.Create("read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("schema", tools.Select(t => t.Name));
    }

    [Fact]
    public async Task WithoutTheSchemaPermissionADirectCallIsRejected()
    {
        // The backstop layer: a registration mistake must not open access.
        await using var harness = SidecarHarness.Create("read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => SchemaToolTests.CallSchemaAsync(client));

        // The authorization filter of the SDK rejects the call with its own message, and not with a
        // code of the sidecar error model. The message states the outcome and the agent must stop,
        // which agrees with PermissionDenied. This assertion records the exact behaviour, thus an
        // SDK change that weakens the backstop fails here.
        Assert.NotNull(failure);
        Assert.Contains("forbidden", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authorization", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARejectedCallDisclosesNothingAboutTheDeployment()
    {
        await using var harness = SidecarHarness.Create("read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var failure = await Record.ExceptionAsync(() => SchemaToolTests.CallSchemaAsync(client));

        Assert.NotNull(failure);
        Assert.DoesNotContain("app.db", failure.Message);
        Assert.DoesNotContain(harness.Token, failure.Message);
    }

    [Fact]
    public async Task OnlyTheImplementedToolsAreExposed()
    {
        // The backup, diagnostics and danger-raw-write tools arrive in later phases, and update and
        // delete arrive in Phase 4b. A permission for one of them must not invent a tool.
        await using var harness = SidecarHarness.Create("schema,read,write,backup,diagnostics,danger-raw-write");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["insert", "query", "schema"], tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task EveryExposedToolCarriesADescription()
    {
        // The description text is the only description that the agent reads.
        await using var harness = SidecarHarness.Create("schema,read");
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description)));
    }
}
