using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xakpc.SQLiteMCPSidecar.Tests.Fixtures;

namespace Xakpc.SQLiteMCPSidecar.Tests.Harness;

/// <summary>
/// One sidecar under test. The suite runs in process by default, and against a real sidecar when
/// <c>SIDECAR_E2E_URL</c> is set, thus the same tests prove the published Linux artifact later.
/// </summary>
/// <remarks>
/// The sandbox depends on the native SQLite build, thus a Windows developer run does not prove the
/// shipped behaviour. The external mode is how a container run reuses these tests unchanged.
/// </remarks>
public abstract class SidecarHarness : IAsyncDisposable
{
    public const string UrlVariable = "SIDECAR_E2E_URL";
    public const string TokenVariable = "SIDECAR_E2E_TOKEN";
    public const string PermissionsVariable = "SIDECAR_E2E_PERMISSIONS";

    /// <summary>The bearer token that this sidecar accepts.</summary>
    public abstract string Token { get; }

    /// <summary>The permission set that this sidecar was started with.</summary>
    public abstract string Permissions { get; }

    /// <summary>True when the suite runs against an external sidecar.</summary>
    public static bool IsExternal =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UrlVariable));

    /// <summary>An HTTP client for raw protocol assertions. It carries no Authorization header.</summary>
    public abstract HttpClient CreateHttpClient();

    /// <summary>
    /// Starts a sidecar with the given permission set.
    /// </summary>
    /// <remarks>
    /// An external sidecar has a fixed permission set. A test that needs a different one is skipped
    /// rather than failed, because the external run cannot restart the process.
    /// </remarks>
    public static SidecarHarness Create(string permissions, string journalMode = "wal")
    {
        if (IsExternal)
        {
            var external = new ExternalSidecarHarness();
            if (!PermissionsMatch(external.Permissions, permissions))
            {
                Assert.Skip($"The external sidecar has permissions '{external.Permissions}', and this test needs '{permissions}'.");
            }

            return external;
        }

        return new InProcessSidecarHarness(permissions, journalMode);
    }

    /// <summary>Connects an MCP client over Streamable HTTP with the deployment token.</summary>
    public async Task<McpClient> ConnectAsync(string? token = null, CancellationToken cancellationToken = default)
    {
        var httpClient = CreateHttpClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                // The transport is stateless, thus the GET endpoint is unavailable and a standalone
                // stream would only occupy a connection.
                EnableStandaloneGetStream = false,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {token ?? Token}",
                },
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    public abstract ValueTask DisposeAsync();

    private static bool PermissionsMatch(string left, string right) =>
        Normalize(left).SequenceEqual(Normalize(right));

    private static IEnumerable<string> Normalize(string specification) =>
        specification.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.ToLowerInvariant())
            .Order();
}

/// <summary>The default target: the sidecar hosted in this process over the in-memory transport.</summary>
public sealed class InProcessSidecarHarness : SidecarHarness
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly TemporaryDatabase _database;

    public InProcessSidecarHarness(string permissions, string journalMode)
    {
        Permissions = permissions;
        _database = SampleDatabase.CreateTemporary(journalMode);

        var settings = new Dictionary<string, string?>
        {
            ["DB"] = _database.Path,
            ["TOKEN"] = Token,
            ["PERMISSIONS"] = permissions,
            ["BACKUP_DIR"] = _database.BackupDirectory,
        };

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
        {
            // In-memory configuration and not process environment variables: environment variables
            // are process-global and would collide across parallel test classes.
            webHost.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        });
    }

    public override string Token => "test-token-9f2c41";

    public override string Permissions { get; }

    public override HttpClient CreateHttpClient() => _factory.CreateClient();

    public override async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _database.Dispose();
    }
}

/// <summary>A sidecar that already runs: a process from the dev script, or a container.</summary>
public sealed class ExternalSidecarHarness : SidecarHarness
{
    private readonly Uri _baseAddress;

    public ExternalSidecarHarness()
    {
        _baseAddress = new Uri(Environment.GetEnvironmentVariable(UrlVariable)!, UriKind.Absolute);
        Token = Environment.GetEnvironmentVariable(TokenVariable)
            ?? throw new InvalidOperationException($"{UrlVariable} is set, thus {TokenVariable} is also required.");
        Permissions = Environment.GetEnvironmentVariable(PermissionsVariable)
            ?? throw new InvalidOperationException($"{UrlVariable} is set, thus {PermissionsVariable} is also required.");
    }

    public override string Token { get; }

    public override string Permissions { get; }

    public override HttpClient CreateHttpClient() => new() { BaseAddress = _baseAddress };

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
