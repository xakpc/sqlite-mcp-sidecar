# Practices

Patterns and constraints that apply to all code in this project. They govern the code that exists
and the code that the remaining phases in [plans/mvp-roadmap.md](plans/mvp-roadmap.md) produce.

## Design rules

The project follows KISS and YAGNI. The MVP has these hard limits:

```text
one SQLite database per sidecar
one production .csproj
one executable
one MCP endpoint
one deployment token
deployment-level permissions
```

Do not add users, roles, an ACL database, a generic database abstraction, a plugin system,
a REST SQL API, a CLI or a browser UI. When a different trust boundary is necessary, deploy
a second sidecar. The full exclusion list is in [plans/out-of-scope.md](plans/out-of-scope.md).

## Code organization

Folders group source files. They are not separate libraries. Do not add a project, and do not add a
fifth folder.

```text
src/Xakpc.SQLiteMCPSidecar/
    Program.cs
    Configuration/   SidecarOptions.cs, SidecarStartup.cs
    Database/        SqliteService.cs, SqliteSecurity.cs, QueryResult.cs,
                     StructuredWriteBuilder.cs, BackupService.cs
    Mcp/             SqliteTools.cs, SidecarError.cs, SidecarEndpoints.cs
    Security/        PermissionSet.cs, DeploymentTokenAuthenticationHandler.cs,
                     WriteBudget.cs, WriteDeduplication.cs
```

Startup validation lives in `Configuration/`, not in a `Startup/` folder. One file does not earn a
folder.

Do not declare an interface when only one implementation exists. Use concrete classes and
constructor injection. `IOptions<T>` is an interface wrapper with one implementation: register the
one `SidecarOptions` instance and inject it. See
[configuration/options.md](configuration/options.md).

Keep a `[LoggerMessage]` partial method in the class that uses it. Source-generated logging is
AOT-friendly and it needs no separate folder.

## Security practices

- Use the native SQLite authorizer. Do not inspect SQL with regular expressions or string matching.
- Apply the sandbox to every connection that untrusted input reaches. See [database/sqlite-sandbox.md](database/sqlite-sandbox.md).
- Parameterize every value. Validate every identifier against the live schema.
- Accept exactly one SQL statement per raw request.
- Start a write transaction with `BEGIN IMMEDIATE`, never with `BEGIN`. SQLite does not call the busy handler for a lock upgrade.
- Give each write tool a mandatory `requestId`. Cache a committed response only. See [security/write-controls.md](security/write-controls.md).
- Fail startup when required configuration is absent. Do not start in a degraded state.
- Return a small error code. Do not return a stack trace, a secret or a filesystem path.
- Give the MCP endpoint its whole public path, `/db/mcp`. Do not depend on a proxy to rewrite a
  prefix. See [security/public-endpoint.md](security/public-endpoint.md).
- Keep `app.UseRateLimiter()` before `app.UseAuthentication()`. A rejected token writes a log line,
  thus the budget must also bound an unauthenticated flood.

**Lesson.** `CreateSlimBuilder` adds **no** host-filtering middleware and **no** forwarded-headers
middleware. An `AllowedHosts` value and `ASPNETCORE_FORWARDEDHEADERS_ENABLED` therefore do nothing
in this application. Do not put a setting in the repository that reads as a control and runs no code.
The reverse proxy is the host gate, and code that needs the caller address reads
`X-Forwarded-For` itself and treats it as a claim.

## Logging practices

Use structured ASP.NET Core logging. Log the request identifier, the operation, the
duration and the outcome.

Never log these values:

```text
bearer tokens
returned database contents
parameter values
```

Raw SQL text is off by default. Log a hash of the SQL instead. See
[security/threat-model.md](security/threat-model.md).

## MCP SDK practices

The project uses `ModelContextProtocol.AspNetCore` 2.x. Version 2.x differs from 1.x:

- HTTP transport is **stateless** by default. Keep it stateless. Every operation is self-contained.
- Roots, sampling and MCP-channel logging are obsolete. They raise warning `MCP9005`. Do not use them.
- Use `ILogger` for logs, never the MCP logging channel.

Register tools explicitly. Do not use `WithToolsFromAssembly`.

```csharp
builder.Services
    .AddMcpServer()
    .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
    .AddAuthorizationFilters()
    .WithTools<SqliteTools>();
```

Use `SessionMode` and not the `Stateless` property. `Stateless` is a convenience proxy over
`SessionMode`, and `SessionMode` also names the `StatefulForInitializeClients` value. Stateless is
already the default as of the `2026-07-28` protocol revision.

**Always call `AddAuthorizationFilters()`.** It is what makes `[Authorize]` on a tool effective, in
`tools/list` and in `tools/call`. Without it the attribute is inert and every tool is reachable.
Permission gating is therefore an attribute on the tool method and never a hand-written check. See
[security/permissions.md](security/permissions.md).

**A tool returns `CallToolResult` and never throws to report an error.** The SDK catches an
exception out of a tool and replaces the message with its own fixed text, `"An error occurred
invoking '<tool>'."`. A thrown error therefore loses every code of the error model, and no build
warning reports it. See [mcp/error-model.md](mcp/error-model.md).

```csharp
return SidecarErrors.Failure(SidecarError.QueryRejected, "The requested action is not permitted.");
```

**A mandatory tool argument needs `= null` and in-method validation.** A nullable type alone is not
enough: the binder of the SDK treats a parameter with no default value as required and throws when the
argument is absent, and the SDK masks that message the same way it masks a thrown exception. The cost
is that the JSON schema marks no argument as required, and the tool description carries the requirement
instead. See [mcp/tool-catalog.md](mcp/tool-catalog.md).

```csharp
public async Task<CallToolResult> InsertAsync(string? requestId = null, ...)
```

Explicit registration has two reasons. It keeps the exposed tool set under permission
control, and assembly scanning is annotated `RequiresUnreferencedCode`, which blocks
NativeAOT. Every tool method and every parameter needs a `[Description]` attribute, because
that text is the only description the agent reads.

## Dependencies

Add a package only when it removes a meaningful amount of code.

| Package | Version | Purpose |
| --- | --- | --- |
| `Microsoft.Data.Sqlite` | 11.0.0-rc.1.26425.128 | ADO.NET provider and bundled native SQLite. |
| `ModelContextProtocol.AspNetCore` | 2.2.0 | MCP server and Streamable HTTP transport. |
| `ModelContextProtocol.Extensions.Tasks` | 2.2.0 | MCP Tasks. It carries the `backup` outcome and it removes a hand-written status tool. It pulls in the `ModelContextProtocol` meta package. |
| `Toon.DotNet` | 4.1.1 | TOON serializer. Namespace is `ToonFormat`. The `DataTable` overload only. |

Test project:

| Package | Version | Purpose |
| --- | --- | --- |
| `xunit.v3` | 4.0.1 | Test framework on Microsoft.Testing.Platform. |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.12 | `WebApplicationFactory` for the in-process target. |
| `ModelContextProtocol.Core` | 2.2.0 | The MCP client: `HttpClientTransport`, `McpClient`. |
| `ModelContextProtocol.Extensions.Tasks` | 2.2.0 | The client half of Tasks: `CallToolWithPollingAsync` for the `backup` tool. |

Developer tools. A `scripts/*.cs` file-based app is not part of the shipped image, thus a package
here has no effect on the sidecar or on NativeAOT:

| Package | Version | Purpose |
| --- | --- | --- |
| `Spectre.Console` | 0.57.2 | Prompts, panels and validation in `scripts/token.cs`. |

`SQLitePCLRaw.core` 3.0.5 arrives through `Microsoft.Data.Sqlite`. The sidecar calls it directly for
the sandbox. Do not add a separate reference unless the transitive one disappears. The sandbox uses
the same API surface on 2.1.12, thus it does not depend on that version.

Do not build a custom SQLite, do not switch SQLite versions at runtime and do not write a
TOON serializer.

## Testing

One test project, `test/Xakpc.SQLiteMCPSidecar.Tests`. Security tests are mandatory, not optional.
The required cases are listed in [plans/mvp-roadmap.md](plans/mvp-roadmap.md).

Write a new test through `SidecarHarness`, never against a hand-built host. The harness runs the
same test in process and against a real sidecar, and the second target is how the functional suite
reaches the published Linux artifact. The sandbox depends on the native SQLite build, thus a Windows
developer run does not prove the shipped behaviour. See
[testing/e2e-harness.md](testing/e2e-harness.md).

The test project uses `xunit.v3` on Microsoft.Testing.Platform. It has no `Microsoft.NET.Test.Sdk`
and no `xunit.runner.visualstudio`: the .NET 10 SDK no longer supports the VSTest target.
`global.json` selects the runner, thus the commands are `dotnet test --solution <file>` and
`--filter-method` in place of `--filter`.

Seed test data from `Fixtures/sample-db.sql` only. It is the one source of truth, and the dev script
uses it too.

## NativeAOT

Target NativeAOT from the start, but do not distort the architecture for it.

```xml
<IsAotCompatible>true</IsAotCompatible>
```

The analyzers run now and `PublishAot` stays off until Phase 8. This catches an AOT hazard at the
line that causes it, at no publish cost. The production project builds with no IL warning today, and
keep it that way.

**Lesson.** The `Delegate` overload of `MapGet` reflects over the delegate signature and it raises
IL2026 and IL3050. The `RequestDelegate` overload does not:

```csharp
app.MapGet("/example", static context => context.Response.WriteAsync("ok"));
```

Prefer a framework endpoint over a hand-written one when the framework has the path. The health path
is `app.MapHealthChecks(SidecarEndpoints.Health)`, which is AOT-clean and needs no handler. See
[security/authentication.md](security/authentication.md).

Prefer `CreateSlimBuilder`, explicit registrations and source-generated JSON. If a
dependency makes AOT disproportionately hard, ship a self-contained .NET 10 Linux image
first. See [plans/open-questions.md](plans/open-questions.md).
