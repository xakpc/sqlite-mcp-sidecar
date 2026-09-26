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
    Database/        SqliteService.cs, and later SqliteSecurity.cs,
                     StructuredWriteBuilder.cs, BackupService.cs
    Mcp/             SqliteTools.cs, SidecarError.cs
    Security/        PermissionSet.cs, DeploymentTokenAuthenticationHandler.cs,
                     and later WriteBudget.cs, WriteDeduplication.cs
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
- Apply the sandbox to every connection that untrusted input reaches. See [plans/design/sqlite-sandbox.md](plans/design/sqlite-sandbox.md).
- Parameterize every value. Validate every identifier against the live schema.
- Accept exactly one SQL statement per raw request.
- Start a write transaction with `BEGIN IMMEDIATE`, never with `BEGIN`. SQLite does not call the busy handler for a lock upgrade.
- Give each write tool a mandatory `requestId`. Cache a committed response only. See [plans/design/write-idempotency.md](plans/design/write-idempotency.md).
- Fail startup when required configuration is absent. Do not start in a degraded state.
- Return a small error code. Do not return a stack trace, a secret or a filesystem path.

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
[plans/design/threat-model.md](plans/design/threat-model.md).

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

Explicit registration has two reasons. It keeps the exposed tool set under permission
control, and assembly scanning is annotated `RequiresUnreferencedCode`, which blocks
NativeAOT. Every tool method and every parameter needs a `[Description]` attribute, because
that text is the only description the agent reads.

## Dependencies

Add a package only when it removes a meaningful amount of code.

| Package | Version | Purpose |
| --- | --- | --- |
| `Microsoft.Data.Sqlite` | 10.0.12 | ADO.NET provider and bundled native SQLite. |
| `ModelContextProtocol.AspNetCore` | 2.2.0 | MCP server and Streamable HTTP transport. |
| `Toon.DotNet` | 4.1.1 | TOON serializer. Namespace is `ToonFormat`. Unused until the `query` tool. |

Test project:

| Package | Version | Purpose |
| --- | --- | --- |
| `xunit.v3` | 4.0.1 | Test framework on Microsoft.Testing.Platform. |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.12 | `WebApplicationFactory` for the in-process target. |
| `ModelContextProtocol.Core` | 2.2.0 | The MCP client: `HttpClientTransport`, `McpClient`. |

`SQLitePCLRaw.core` arrives through `Microsoft.Data.Sqlite`. The sidecar calls it directly
for the sandbox. Do not add a separate reference unless the transitive one disappears.

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
app.MapGet("/health", static context =>
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsync("""{"status":"ok"}""");
});
```

Prefer `CreateSlimBuilder`, explicit registrations and source-generated JSON. If a
dependency makes AOT disproportionately hard, ship a self-contained .NET 10 Linux image
first. See [plans/open-questions.md](plans/open-questions.md).
