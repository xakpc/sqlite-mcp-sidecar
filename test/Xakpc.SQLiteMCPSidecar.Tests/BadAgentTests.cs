using System.Net;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xakpc.SQLiteMCPSidecar.Mcp;
using Xakpc.SQLiteMCPSidecar.Tests.Harness;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The badly behaved agent: malformed, wrongly typed and hostile tool calls.
/// </summary>
/// <remarks>
/// <para>
/// Every other tool test builds correctly typed arguments through a helper, thus none of them proves
/// what happens when an agent sends the wrong JSON shape. The cases here come from
/// <c>Fixtures/bad-agent-corpus.json</c> and reach the server as the raw JSON that the file holds.
/// </para>
/// <para>
/// <b>Invariant.</b> Four assertions run for every case, whatever it expects: the transport does not
/// fault, the message discloses nothing, a known-good call still works afterwards, and a case that is
/// not expected to succeed changes no row. The per-case expectation is the fifth assertion and the
/// weakest one. See <c>.lode/testing/bad-agent-suite.md</c>.
/// </para>
/// <para>
/// <b>Against an external sidecar, raise the request budget.</b> Each case connects and makes a few
/// calls, and one permission group is over a hundred requests in the same minute. The default
/// <c>MAX_REQUESTS_PER_MINUTE</c> of 120 is a per-process control that this class does not test, thus
/// an external run sets it high and <c>RateLimitTests</c> keeps owning the budget. Without it the
/// corpus fails with <c>429</c> and proves nothing about the corpus.
/// </para>
/// </remarks>
public sealed class BadAgentTests
{
    public static TheoryData<string> CaseIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in BadAgentCorpus.Ids)
            {
                data.Add(id);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public async Task TheCallIsHandledCleanly(string id)
    {
        var testCase = BadAgentCorpus.Find(id);

        await using var harness = SidecarHarness.Create(testCase.Permissions);
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        var before = await FingerprintAsync(client);

        var (isError, text) = await CallAsync(client, testCase);

        // 1. The message discloses nothing. A SQLite message names the database file and an unhandled
        //    exception carries a stack trace, thus neither may reach a caller.
        AssertDisclosesNothing(harness, text, testCase);

        // 2. The per-case expectation.
        switch (testCase.Expect)
        {
            case BadAgentCase.ExpectOk:
                Assert.False(isError, $"The case '{id}' expects success and the answer was: {text}");
                break;

            case BadAgentCase.ExpectErrorCode:
                Assert.True(isError, $"The case '{id}' expects a failure and the call succeeded: {text}");
                Assert.True(
                    testCase.Codes.Exists(code => text.Contains(code, StringComparison.Ordinal)),
                    $"The case '{id}' expects one of [{string.Join(", ", testCase.Codes)}] and the answer was: {text}");
                break;

            case BadAgentCase.ExpectRejectedOpaque:
                Assert.True(isError, $"The case '{id}' expects a failure and the call succeeded: {text}");

                // The bucket is pinned on purpose. When the product starts returning a code for this
                // shape the assertion fails, and the corpus entry moves to error-code. A bucket that
                // accepted both answers would record nothing.
                var named = KnownCodes.FirstOrDefault(code => text.Contains(code, StringComparison.Ordinal));
                Assert.True(
                    named is null,
                    $"The case '{id}' is recorded as opaque and the answer now names '{named}'. "
                  + "Move it to expect: error-code and close the issue it belongs to.");
                break;

            case BadAgentCase.ExpectAcceptedGap:
                // Pinned for the same reason as the bucket above, in the other direction. The call is
                // accepted today and the contract says it must not be, thus a fix makes this fail and
                // the corpus entry moves to error-code.
                Assert.False(
                    isError,
                    $"The case '{id}' is recorded as an accepted gap and the sidecar now refuses it: {text}. "
                  + "Move it to expect: error-code and close the issue it belongs to.");
                break;
        }

        // 3. The sidecar is not poisoned, and 4. a call that was not expected to succeed changed no
        //    row. One call proves both: the fingerprint IS a known-good query, thus it answering at
        //    all is the liveness check and its value is the unchanged-database check. Two calls would
        //    buy nothing and the corpus is already the chattiest class in the suite.
        var after = await FingerprintAsync(client);
        Assert.Contains("truncated: false", after, StringComparison.Ordinal);

        // The row counts are only comparable when this harness owns the database. An external sidecar
        // serves ONE database to the whole run, and xunit runs test classes in parallel, thus another
        // class writes a row between these two reads and the count moves for a reason that has
        // nothing to do with this case. The other three invariants still hold there, and the
        // in-process job proves this one on Linux in CI.
        if (!testCase.MayWrite && harness.DatabasePath is not null)
        {
            Assert.Equal(before, after);
        }
    }

    /// <summary>
    /// The corpus loads, every case is well formed, and it holds a succeeding case for each group.
    /// </summary>
    /// <remarks>
    /// A corpus of nothing but failures would still pass its own assertions while proving that the
    /// sidecar refuses everything, thus each permission group carries at least one <c>ok</c> case.
    /// </remarks>
    [Fact]
    public void TheCorpusIsWellFormedAndNotOnlyFailures()
    {
        Assert.NotEmpty(BadAgentCorpus.Cases);

        foreach (var group in BadAgentCorpus.Cases.GroupBy(c => c.Permissions))
        {
            Assert.True(
                group.Any(c => c.Expect == BadAgentCase.ExpectOk),
                $"The permission group '{group.Key}' has no succeeding case, thus it proves only that "
              + "the sidecar refuses things.");
        }
    }

    /// <summary>
    /// Sends one case and normalizes the two shapes a failure arrives in.
    /// </summary>
    /// <remarks>
    /// A tool failure comes back as a result with <c>IsError</c> set. A call that the protocol layer
    /// refuses, for example an unknown tool name, arrives as a thrown <see cref="McpException"/>
    /// instead. Both are a rejection that the agent has to read, thus both are normalized here and the
    /// invariants apply to the message either way.
    /// </remarks>
    private static async Task<(bool IsError, string Text)> CallAsync(McpClient client, BadAgentCase testCase)
    {
        try
        {
            var result = await client.CallToolAsync(
                new CallToolRequestParams { Name = testCase.Tool, Arguments = testCase.ExpandedArguments },
                cancellationToken: TestContext.Current.CancellationToken);

            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            return (result.IsError == true, text);
        }
        catch (McpException exception)
        {
            return (true, exception.Message);
        }
    }

    /// <summary>
    /// A cheap whole-database fingerprint: the row count of each table in one query.
    /// </summary>
    /// <remarks>
    /// It is taken through the sidecar and not from the file, thus it also works when the harness did
    /// not create the database. Every permission group carries <c>read</c>, because the read floor
    /// requires it.
    /// </remarks>
    private static Task<string> FingerprintAsync(McpClient client) =>
        QueryToolTests.CallQueryAsync(
            client,
            "SELECT (SELECT count(*) FROM users) AS u, (SELECT count(*) FROM jobs) AS j, "
          + "(SELECT count(*) FROM logs) AS l, (SELECT count(*) FROM job_tags) AS t, "
          + "(SELECT count(*) FROM events) AS e");

    private static void AssertDisclosesNothing(SidecarHarness harness, string text, BadAgentCase testCase)
    {
        var because = $"The case '{testCase.Id}' disclosed something in: {text}";

        Assert.False(text.Contains(harness.Token, StringComparison.Ordinal), because);
        Assert.False(text.Contains("app.db", StringComparison.OrdinalIgnoreCase), because);
        Assert.False(text.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), because);
        Assert.False(text.Contains("   at ", StringComparison.Ordinal), because);
        Assert.False(text.Contains("SqliteException", StringComparison.Ordinal), because);
        Assert.False(text.Contains("Xakpc.SQLiteMCPSidecar", StringComparison.Ordinal), because);
    }

    /// <summary>The closed error-code set, by name. A code in a message is what an agent selects on.</summary>
    private static readonly string[] KnownCodes = Enum.GetNames<SidecarError>();
}

/// <summary>
/// The same badly behaved agent one layer lower: a hand-built HTTP request to the MCP endpoint.
/// </summary>
/// <remarks>
/// A shape that <c>CallToolRequestParams</c> cannot express, for example a body that is not JSON at
/// all, only reaches the server this way. What matters for each is the same thing: the sidecar
/// answers, it answers with no 5xx and no stack trace, and it still serves the next request.
/// <c>AuthTests</c> already covers the token cases and they are not repeated here.
/// </remarks>
public sealed class BadAgentProtocolTests
{
    [Theory]
    // Not JSON at all.
    [InlineData("please drop the database")]
    // JSON, but not a JSON-RPC message.
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    // A JSON-RPC message with no method.
    [InlineData("""{"jsonrpc":"2.0","id":1}""")]
    // An unknown method.
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"database/drop"}""")]
    // tools/call with no arguments member, and with arguments of the wrong JSON type.
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"query"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"query","arguments":[1,2]}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"query","arguments":"SELECT 1"}}""")]
    // A duplicate key, which no typed client can send.
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"query","arguments":{"sql":"SELECT 1","sql":"DROP TABLE jobs"}}}""")]
    // Truncated JSON, as a dropped connection would produce.
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/ca""")]
    public async Task AMalformedBodyIsAnsweredWithoutAServerError(string body)
    {
        await using var harness = SidecarHarness.Create("schema,read");

        using var http = harness.CreateHttpClient();
        var response = await PostAsync(http, harness.Token, body);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        AssertNoServerFailure(response, text);

        // The sidecar still serves the next caller.
        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("truncated: false", await QueryToolTests.CallQueryAsync(client, "SELECT 1 AS ok"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A body far larger than any legitimate statement. The sidecar refuses it and keeps serving.
    /// </summary>
    [Fact]
    public async Task AnEnormousBodyIsRefusedAndTheSidecarKeepsServing()
    {
        await using var harness = SidecarHarness.Create("schema,read");

        var filler = new string('A', 5 * 1024 * 1024);
        var body =
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"query","arguments":{"sql":"SELECT '"""
            + filler
            + """'"}}}""";

        using var http = harness.CreateHttpClient();
        var response = await PostAsync(http, harness.Token, body);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        AssertNoServerFailure(response, text);

        await using var client = await harness.ConnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("truncated: false", await QueryToolTests.CallQueryAsync(client, "SELECT 1 AS ok"),
            StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, string token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SidecarEndpoints.Mcp)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        // The Streamable HTTP transport needs both media types on Accept. Without them the request is
        // refused for the wrong reason and the case proves nothing.
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static void AssertNoServerFailure(HttpResponseMessage response, string text)
    {
        // A malformed request is the caller's mistake. A 5xx would tell an agent that the sidecar is
        // broken and send it to retry for ever.
        Assert.True(
            (int)response.StatusCode < (int)HttpStatusCode.InternalServerError,
            $"The sidecar answered {(int)response.StatusCode} to a malformed request: {Truncate(text)}");

        Assert.False(text.Contains("   at ", StringComparison.Ordinal), $"A stack trace reached the caller: {Truncate(text)}");
        Assert.False(text.Contains("Xakpc.SQLiteMCPSidecar", StringComparison.Ordinal), $"An internal name reached the caller: {Truncate(text)}");
        Assert.False(text.Contains("app.db", StringComparison.OrdinalIgnoreCase), $"The database path reached the caller: {Truncate(text)}");
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "...";
}
