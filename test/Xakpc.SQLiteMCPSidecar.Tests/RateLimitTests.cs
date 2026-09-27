using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xakpc.SQLiteMCPSidecar.Mcp;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The request budget. One sidecar process has one budget, and the limiter runs before the token
/// check, thus an unauthenticated flood is bounded too.
/// </summary>
public sealed class RateLimitTests
{
    private const string ToolsListBody = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    private static readonly Dictionary<string, string?> TwoPerMinute = new()
    {
        ["MAX_REQUESTS_PER_MINUTE"] = "2",
    };

    [Fact]
    public async Task ARequestOverTheBudgetIsTooManyRequests()
    {
        await using var harness = SidecarHarness.Create("schema,read", settings: TwoPerMinute);
        using var client = harness.CreateHttpClient();

        var first = await PostMcpAsync(client, harness.Token);
        var second = await PostMcpAsync(client, harness.Token);
        var third = await PostMcpAsync(client, harness.Token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // 429 and not 503. A 503 reads as a broken sidecar and it invites an immediate retry.
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task TheBudgetCountsARequestWithNoToken()
    {
        // The limiter is before authentication on purpose: each rejected request writes a warning
        // line, thus an unbounded flood of wrong tokens is a log-volume attack.
        await using var harness = SidecarHarness.Create("schema,read", settings: TwoPerMinute);
        using var client = harness.CreateHttpClient();

        var first = await PostMcpAsync(client, token: null);
        var second = await PostMcpAsync(client, token: null);
        var third = await PostMcpAsync(client, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task HealthIsOutsideTheBudget()
    {
        // A platform probes this path every few seconds. A throttled probe reports a healthy
        // sidecar as unhealthy and the platform then restarts it.
        await using var harness = SidecarHarness.Create("schema,read", settings: TwoPerMinute);
        using var client = harness.CreateHttpClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await client.GetAsync(SidecarEndpoints.Health, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static async Task<HttpResponseMessage> PostMcpAsync(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SidecarEndpoints.Mcp)
        {
            Content = new StringContent(ToolsListBody, Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
