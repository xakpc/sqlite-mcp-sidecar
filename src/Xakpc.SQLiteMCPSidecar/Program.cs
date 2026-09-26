using Microsoft.AspNetCore.Authentication;
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

app.UseAuthentication();
app.UseAuthorization();

// Process health only. It needs no token and it touches no database: a probe runs often, and an
// expensive probe becomes a denial-of-service vector against the owning application.
// The RequestDelegate overload, not the Delegate overload. The Delegate overload reflects over the
// delegate signature, which the AOT analyzer reports as IL2026 and IL3050.
app.MapGet("/health", static context =>
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsync("""{"status":"ok"}""");
});

app.MapMcp("/mcp").RequireAuthorization();

app.Run();

/// <summary>
/// Declared so that <c>WebApplicationFactory&lt;Program&gt;</c> in the test project finds the entry
/// point of this assembly.
/// </summary>
public partial class Program;
