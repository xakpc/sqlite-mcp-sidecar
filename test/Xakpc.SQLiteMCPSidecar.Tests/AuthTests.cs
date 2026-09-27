using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xakpc.SQLiteMCPSidecar.Mcp;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The bearer token contract. A missing header and a wrong token give the same answer, thus the
/// response does not state which check failed.
/// </summary>
public sealed class AuthTests
{
    private const string ToolsListBody = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    [Fact]
    public async Task HealthNeedsNoToken()
    {
        // A probe runs often. An expensive or authenticated probe is a problem for the operator.
        await using var harness = SidecarHarness.Create("schema,read");
        using var client = harness.CreateHttpClient();

        var response = await client.GetAsync(SidecarEndpoints.Health, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnAbsentAuthorizationHeaderIsUnauthorized()
    {
        await using var harness = SidecarHarness.Create("schema,read");

        var response = await PostMcpAsync(harness, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AWrongTokenIsUnauthorized()
    {
        await using var harness = SidecarHarness.Create("schema,read");

        var response = await PostMcpAsync(harness, token: "not-the-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ATokenWithTheCorrectPrefixButTheWrongLengthIsUnauthorized()
    {
        await using var harness = SidecarHarness.Create("schema,read");

        var response = await PostMcpAsync(harness, token: harness.Token[..^1]);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheCorrectTokenIsAccepted()
    {
        await using var harness = SidecarHarness.Create("schema,read");

        var response = await PostMcpAsync(harness, harness.Token);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnAbsentHeaderAndAWrongTokenGiveTheSameAnswer()
    {
        await using var harness = SidecarHarness.Create("schema,read");

        var absent = await PostMcpAsync(harness, token: null);
        var wrong = await PostMcpAsync(harness, token: "not-the-token");

        Assert.Equal(absent.StatusCode, wrong.StatusCode);
        Assert.Equal(
            absent.Headers.WwwAuthenticate.ToString(),
            wrong.Headers.WwwAuthenticate.ToString());
        Assert.Equal(
            await absent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await wrong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheChallengeCarriesNoErrorDescription()
    {
        await using var harness = SidecarHarness.Create("schema,read");

        var response = await PostMcpAsync(harness, token: "not-the-token");
        var challenge = response.Headers.WwwAuthenticate.ToString();

        Assert.DoesNotContain("error", challenge, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(harness.Token, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<HttpResponseMessage> PostMcpAsync(SidecarHarness harness, string? token)
    {
        using var client = harness.CreateHttpClient();
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
