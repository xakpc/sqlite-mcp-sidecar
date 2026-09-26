# Practices

Patterns and constraints that apply to all code in this project. These rules are active now:
they govern the code that the phases in [plans/mvp-roadmap.md](plans/mvp-roadmap.md) produce.
The folder layout below does not exist yet.

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

Folders group source files. They are not separate libraries. Do not add a project.

```text
src/Xakpc.SQLiteMCPSidecar/
    Program.cs
    Configuration/   SidecarOptions.cs
    Database/        SqliteService.cs, SqliteSecurity.cs,
                     StructuredWriteBuilder.cs, BackupService.cs
    Mcp/             SqliteTools.cs
    Security/        TokenAuthentication.cs, WriteBudget.cs,
                     WriteDeduplication.cs
```

Do not declare an interface when only one implementation exists. Use concrete classes and
constructor injection.

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
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<SqliteTools>();
```

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
| `Toon.DotNet` | 4.1.1 | TOON serializer. Namespace is `ToonFormat`. |

`SQLitePCLRaw.core` arrives through `Microsoft.Data.Sqlite`. The sidecar calls it directly
for the sandbox. Do not add a separate reference unless the transitive one disappears.

Do not build a custom SQLite, do not switch SQLite versions at runtime and do not write a
TOON serializer.

## Testing

Tests live in `test/`. Security tests are mandatory, not optional. The required cases are
listed in [plans/mvp-roadmap.md](plans/mvp-roadmap.md). Run the functional suite against
the published Linux artifact, because the sandbox depends on the native SQLite build.

## NativeAOT

Target NativeAOT from the start, but do not distort the architecture for it.

```xml
<PublishAot>true</PublishAot>
```

Prefer `CreateSlimBuilder`, explicit registrations and source-generated JSON. If a
dependency makes AOT disproportionately hard, ship a self-contained .NET 10 Linux image
first. See [plans/open-questions.md](plans/open-questions.md).
