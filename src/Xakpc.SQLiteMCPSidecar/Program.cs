using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.AspNetCore;
using Xakpc.SQLiteMCPSidecar.Configuration;
using Xakpc.SQLiteMCPSidecar.Database;
using Xakpc.SQLiteMCPSidecar.Mcp;
using Xakpc.SQLiteMCPSidecar.Security;

var builder = WebApplication.CreateSlimBuilder(args);

// Every configuration value comes from an environment variable with the SQLITE_SIDECAR_ prefix.
// The provider removes the prefix, thus the keys arrive flat: DB, TOKEN, PERMISSIONS, MAX_ROWS.
builder.Configuration.AddEnvironmentVariables("SQLITE_SIDECAR_");

// A container operator reads logs from a log pipeline, thus JSON outside development.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.AddJsonConsole();
}

// One instance for the process, and immutable after that. There is no reconfiguration at runtime,
// because a permission change must be a visible deployment change.
//
// The read is deferred to the first resolve, which happens in SidecarStartup below, before the
// first request. It is not read here, because a configuration source that the host adds later, for
// example in a test host, would then be invisible.
builder.Services.AddSingleton(services => SidecarOptions.Load(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<SqliteService>();

// Both hold process state, thus both are singletons and both carry the same caveat: they reset at a
// restart and two processes do not share them. That is why one sidecar serves one database.
builder.Services.AddSingleton<WriteBudget>();
builder.Services.AddSingleton<WriteDeduplication>();

builder.Services
    .AddAuthentication(DeploymentTokenDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, DeploymentTokenAuthenticationHandler>(
        DeploymentTokenDefaults.Scheme, displayName: null, configureOptions: _ => { });

// One policy for each permission name, and not for each permission of this deployment. A tool
// carries [Authorize(Policy = "perm:<name>")], and the authentication handler issues one "perm"
// claim for each permission that the deployment does have. The claim decides the outcome, thus the
// policy set needs no configuration value.
var authorization = builder.Services.AddAuthorizationBuilder();
foreach (var permissionName in PermissionSet.AllNames)
{
    authorization.AddPolicy(
        PermissionSet.PolicyFor(permissionName),
        policy => policy
            .AddAuthenticationSchemes(DeploymentTokenDefaults.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(PermissionSet.ClaimType, permissionName));
}

// One request budget for the whole process, and no partition. The deployment has one identity, thus
// one budget says the same thing. A partition by client address would need X-Forwarded-For, which
// the caller controls, and an evadable limit is worse than an honest global one.
//
// The middleware runs before authentication, thus a flood of wrong tokens is bounded too. Each
// rejection writes a warning line, thus an unbounded flood is a log-volume attack even though the
// token comparison itself is cheap.
builder.Services.AddRateLimiter(rateLimiter =>
{
    // 429 and not the 503 default. A 503 tells an agent that the sidecar is broken, and a correct
    // agent then retries a request that it should slow down instead.
    rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    rateLimiter.AddPolicy(SidecarEndpoints.McpRateLimitPolicy, context =>
        // The options are resolved here and not above, because SidecarOptions is read at the first
        // resolve. The partition key is constant, thus every request shares one limiter.
        RateLimitPartition.GetFixedWindowLimiter(
            SidecarEndpoints.McpRateLimitPolicy,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = context.RequestServices.GetRequiredService<SidecarOptions>().MaxRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                // Reject, never queue. A queue hides the overload and it holds a connection while
                // the agent already waits for an answer.
                QueueLimit = 0,
            }));
});

builder.Services
    .AddMcpServer()
    .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
    // Honours [Authorize] on every tool: it removes an unauthorized tool from tools/list and
    // rejects a direct tools/call of that name.
    .AddAuthorizationFilters()
    // Explicit registration. Assembly scanning is annotated RequiresUnreferencedCode, which blocks
    // NativeAOT, and it would remove permission control of the exposed surface.
    .WithTools<SqliteTools>();

var app = builder.Build();

// Checks that need the database file itself. A startup check is much better than a failure at the
// first request: an orchestrator sees a failed start and an operator sees it immediately.
await SidecarStartup.RunAsync(app.Services, app.Logger, CancellationToken.None);

// Before authentication on purpose. See the registration above.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Process health only. It needs no token and it touches no database: a probe runs often, and an
// expensive probe becomes a denial-of-service vector against the owning application.
// The RequestDelegate overload, not the Delegate overload. The Delegate overload reflects over the
// delegate signature, which the AOT analyzer reports as IL2026 and IL3050.
app.MapGet(SidecarEndpoints.Health, static context =>
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsync("""{"status":"ok"}""");
});

// The whole public path, thus no proxy rewrites it. See SidecarEndpoints.
app.MapMcp(SidecarEndpoints.Mcp)
    .RequireAuthorization()
    .RequireRateLimiting(SidecarEndpoints.McpRateLimitPolicy);

app.Run();

/// <summary>
/// Declared so that <c>WebApplicationFactory&lt;Program&gt;</c> in the test project finds the entry
/// point of this assembly.
/// </summary>
public partial class Program;
