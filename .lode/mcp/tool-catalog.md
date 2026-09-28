# MCP tool catalog

The sidecar has one MCP endpoint at `/db/mcp` over Streamable HTTP. The catalog has eight tools at
most, and seven of them exist. The path is the whole public path on purpose. See
[../security/public-endpoint.md](../security/public-endpoint.md).

Code: `src/Xakpc.SQLiteMCPSidecar/Mcp/SqliteTools.cs`, `src/Xakpc.SQLiteMCPSidecar/Program.cs`.

| Tool | Permission | Caller supplies SQL | `requestId` | State |
| --- | --- | --- | --- | --- |
| `schema` | `schema` | no | no | **Implemented** |
| `query` | `read` | yes, read-only | no | **Implemented** |
| `insert` | `write` | no | mandatory | **Implemented** |
| `update` | `write` | no | mandatory | **Implemented** |
| `delete` | `write` | no | mandatory | **Implemented** |
| `backup` | `backup` | no | no | **Implemented**, task-mode |
| `diagnostics` | `diagnostics` | no | no | **Implemented** |
| `execute_write_sql` | `danger-raw-write` | yes, DML | mandatory | Phase 6 |

A permission that has no tool yet exposes nothing.
`PermissionGatingTests.OnlyTheImplementedToolsAreExposed` starts a deployment with every permission
and asserts that `tools/list` holds `schema`, `query`, `insert`, `update`, `delete`, `backup` and
`diagnostics` only. `backup_status` must never appear: MCP Tasks carries the backup outcome. See
[../decisions/0007-tasks-over-a-status-tool.md](../decisions/0007-tasks-over-a-status-tool.md).

Adding a tool needs no name list anywhere. `AddAuthorizationFilters()` reads the `[Authorize]`
attribute on the method, and that one attribute gives both gating layers.

## Server registration

```csharp
builder.Services
    .AddMcpServer()
    .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
    .AddAuthorizationFilters()
    .WithTasks(new InMemoryMcpTaskStore(), tasks =>
        tasks.ExecutionModeSelector = request =>
            string.Equals(request.Params?.Name, SqliteTools.BackupToolName, StringComparison.Ordinal)
                ? McpTaskExecutionMode.Required
                : McpTaskExecutionMode.Synchronous)
    .WithTools<SqliteTools>();

app.MapMcp(SidecarEndpoints.Mcp)
    .RequireAuthorization()
    .RequireRateLimiting(SidecarEndpoints.McpRateLimitPolicy);
```

Register tools explicitly. `WithToolsFromAssembly` is annotated `RequiresUnreferencedCode`, which
blocks NativeAOT, and it removes control of the exposed surface.

`AddAuthorizationFilters()` must be present. It is what makes the `[Authorize]` attribute on a tool
effective, in `tools/list` and in `tools/call`. See
[../security/permissions.md](../security/permissions.md).

## One task-mode tool

`backup` answers with a task and the client polls for the result. Every other tool answers inline.

**Invariant. The `ExecutionModeSelector` pins every tool except `backup` to `Synchronous`.** The
default for an async tool is `Optional`, and every tool here is `async Task<CallToolResult>`, thus
registering a task store without the selector would give the whole catalog a second calling
convention and a second result shape. The error model must own every failure an agent can cause, and
a second shape routes some of them past it.

`backup` is `Required` and not `Optional`, thus a client that does not implement the extension cannot
call it at all:

```text
{"error":{"code":-32021,
  "message":"The request requires the 'io.modelcontextprotocol/tasks' client extension capability.",
  "data":{"requiredCapabilities":{"extensions":{"io.modelcontextprotocol/tasks":{}}}}}}
```

That is the intended behaviour. `Optional` would let such a client fall back to an inline call, which
a reverse proxy cuts at its read timeout. See
[../decisions/0007-tasks-over-a-status-tool.md](../decisions/0007-tasks-over-a-status-tool.md).

A test calls it through `McpTasksClientExtensions`: `CallToolAsTaskAsync` to assert the shape, or
`CallToolWithPollingAsync` to get the finished result. `CallToolAsync` does not work on `backup`.

**Lesson.** The v1 documentation page for Tasks describes an API absent from 2.2.0 —
`McpServerOptions` has no `TaskStore` property and `McpServerToolCreateOptions` has no `Execution`
property. The v2 page is the correct one. Reflect over the shipped assembly before believing a page.

## The transport is stateless

`HttpServerSessionMode.Stateless` is the explicit setting, and it is also the default of the SDK as
of the `2026-07-28` protocol revision.

**Lesson.** That revision removed the `Mcp-Session-Id` header (SEP-2567) and the `initialize`
handshake (SEP-2575). A request is therefore fully self-contained: `tools/list` and `tools/call`
work with no preceding `initialize`, and there is no session id to carry from one request to the
next. This is verified by hand in `src/Xakpc.SQLiteMCPSidecar/mcp.http`.

Stateless mode disables the GET and DELETE endpoints and every server-to-client request, thus
sampling, elicitation and roots are unavailable. The sidecar needs none of them. Statelessness also
agrees with the no-remote-transaction rule in
[../plans/design/raw-writes.md](../plans/design/raw-writes.md).

`HttpServerTransportOptions.Stateless` is a convenience proxy over `SessionMode` and it is not
obsolete. `SessionMode` is the fuller API, thus the code uses it.

## Tool shape

```csharp
[McpServerTool(Name = "schema")]
[Description("Return the DDL of every table, view and index in the database. ...")]
[Authorize(Policy = "perm:schema")]
public async Task<CallToolResult> SchemaAsync(CancellationToken cancellationToken)
```

Every tool and every parameter needs a `[Description]`. That text is the only description the agent
reads, and a vague description is the main reason an agent misuses a tool. Name the constraints in
the text. `PermissionGatingTests.EveryExposedToolCarriesADescription` asserts that each exposed tool
has one.

**A tool returns `CallToolResult` and never throws to report an error.** A failure reaches the caller
as a result with `IsError` set, which carries a code from [error-model.md](error-model.md) and a
short fixed explanation. The SDK masks a thrown exception, thus a throw loses the code. The detail
goes to the log. A tool logs the name, the duration and the outcome through a source-generated
`[LoggerMessage]` method.

## `schema`

One server-authored `SELECT` over `sqlite_master`, no parameter, no sandbox needed. It returns DDL
text and **not** TOON, because one `CREATE TABLE` statement carries the columns, types, nullability,
defaults, keys and constraints in one string. See [schema-output.md](schema-output.md).

## `query`

```csharp
public async Task<CallToolResult> QueryAsync(
    [Description("One read-only SQL statement, ...")] string sql,
    CancellationToken cancellationToken)
```

One parameter, the SQL text. **There is no parameter list.** A caller that holds `read` can already
write any `SELECT`, thus parameters would add surface with no security gain on the read path.

The tool takes a request slot, then `SqliteService.QueryAsync` opens a read-only connection, applies
the sandbox, proves that the text is one statement, executes it and bounds the result.

```text
rows[2]{id,status}:
  41,failed
  52,failed

truncated: false
```

The rows are TOON. See [query-results.md](query-results.md). The rejections are in
[../database/sqlite-sandbox.md](../database/sqlite-sandbox.md), and the codes are in
[error-model.md](error-model.md).

## `insert`, `update` and `delete`

Three, four and five arguments. The caller sends no SQL: the server builds one parameterized
statement. Every mandatory argument carries `= null` and is validated in the method body, thus the
generated schema marks nothing required and the description carries the requirement instead. All three
share one private path, `RunStructuredWriteAsync`, which owns the order of the controls.

See [write-tool-arguments.md](write-tool-arguments.md) and
[../database/structured-writes.md](../database/structured-writes.md).

## `backup`

```csharp
public const string BackupToolName = "backup";

[McpServerTool(Name = BackupToolName, ReadOnly = true)]
public async Task<CallToolResult> BackupAsync(string? label = null, CancellationToken ct = default)
```

One optional argument, and it is a **label and not a path**: the server builds the file name. The
tool takes the backup slot and **no request slot**, because the copy outlives the request that
started it. `Program` reads `BackupToolName` to mark this one tool as task-mode, thus the name lives
in one place.

```text
name: app-20260928T034150Z-before-cleanup.db
bytes: 18468864
restarts: 2
```

See [../database/backups.md](../database/backups.md).

## `diagnostics`

```csharp
[McpServerTool(Name = "diagnostics", ReadOnly = true)]
public async Task<CallToolResult> DiagnosticsAsync(CancellationToken cancellationToken)
```

No arguments at all, and `DiagnosticsToolTests.DiagnosticsTakesNoArguments` asserts that the
generated schema has no property. A caller-supplied `PRAGMA` name would be a write primitive and an
information leak. See [../database/diagnostics.md](../database/diagnostics.md).

## Related

- [../database/structured-writes.md](../database/structured-writes.md)
- [../database/backups.md](../database/backups.md)
- [../database/diagnostics.md](../database/diagnostics.md)
- [schema-output.md](schema-output.md)
- [../security/write-controls.md](../security/write-controls.md)
- [../security/permissions.md](../security/permissions.md)
- [../database/connections.md](../database/connections.md)
- [../testing/e2e-harness.md](../testing/e2e-harness.md)
- [query-results.md](query-results.md)
