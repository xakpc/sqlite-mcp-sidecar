# MCP tool catalog

The sidecar has one MCP endpoint at `/db/mcp` over Streamable HTTP. The catalog has nine tools at
most, and five of them exist. The path is the whole public path on purpose. See
[../security/public-endpoint.md](../security/public-endpoint.md).

Code: `src/Xakpc.SQLiteMCPSidecar/Mcp/SqliteTools.cs`, `src/Xakpc.SQLiteMCPSidecar/Program.cs`.

| Tool | Permission | Caller supplies SQL | `requestId` | State |
| --- | --- | --- | --- | --- |
| `schema` | `schema` | no | no | **Implemented** |
| `query` | `read` | yes, read-only | no | **Implemented** |
| `insert` | `write` | no | mandatory | **Implemented** |
| `update` | `write` | no | mandatory | **Implemented** |
| `delete` | `write` | no | mandatory | **Implemented** |
| `backup` | `backup` | no | no | Phase 5 |
| `backup_status` | `backup` | no | no | Phase 5 |
| `diagnostics` | `diagnostics` | no | no | Phase 5 |
| `execute_write_sql` | `danger-raw-write` | yes, DML | mandatory | Phase 6 |

A permission that has no tool yet exposes nothing.
`PermissionGatingTests.OnlyTheImplementedToolsAreExposed` starts a deployment with every permission
and asserts that `tools/list` holds `schema`, `query`, `insert`, `update` and `delete` only.

Adding a tool needs no name list anywhere. `AddAuthorizationFilters()` reads the `[Authorize]`
attribute on the method, and that one attribute gives both gating layers.

## Server registration

```csharp
builder.Services
    .AddMcpServer()
    .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
    .AddAuthorizationFilters()
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

```sql
SELECT sql FROM sqlite_master
WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'
ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'view' THEN 1 WHEN 'index' THEN 2 ELSE 3 END, name;
```

The statement is server-authored and takes no caller input, thus the tool needs no sandbox and it
has no parameter. It runs over a read-only connection. See
[../database/connections.md](../database/connections.md).

```text
CREATE TABLE jobs (
  id        INTEGER PRIMARY KEY,
  status    TEXT NOT NULL DEFAULT 'pending',
  retry     INTEGER NOT NULL DEFAULT 0,
  owner_id  INTEGER REFERENCES users(id),
  ...
);
CREATE VIEW failed_jobs AS ...;
CREATE INDEX idx_jobs_status ON jobs(status);
```

**The schema tool returns no TOON.** TOON is for row data only. One `CREATE TABLE` statement states
the columns, the types, the nullability, the defaults, the primary key, the foreign keys and the
constraints in one string. A set of TOON tables holds the same facts in a shape that the agent must
assemble again, and it costs more tokens.

The internal `sqlite_%` objects are excluded, thus `sqlite_autoindex` and `sqlite_sequence` never
reach the agent.

This choice is cheap to reverse. A real agent must still confirm that the DDL output is usable. See
[../plans/open-questions.md](../plans/open-questions.md).

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

## `insert`

```csharp
public async Task<CallToolResult> InsertAsync(
    string? requestId = null,
    string? table = null,
    Dictionary<string, JsonElement>? values = null,
    CancellationToken cancellationToken = default)
```

Three arguments. The caller sends no SQL: the server builds one parameterized `INSERT` that adds
exactly one row. See [../database/structured-writes.md](../database/structured-writes.md).

```text
rowsAffected: 1
rowid: 78
```

**Lesson.** Each mandatory argument carries `= null` and is validated in the method body. A nullable
type alone is **not** enough: the binder of the SDK treats a parameter with no default value as
required and throws when the argument is absent, and the SDK then replaces the message with `"An error
occurred invoking 'insert'."`. The agent gets no code to select from, and the required case
"write without requestId -> `InvalidWrite`" cannot pass.
`WriteIdempotencyTests.AnAbsentRequestIdIsInvalidWrite` fails if the defaults are removed.

The cost is that the generated JSON schema marks **no** argument as required:

```json
{"type":"object","properties":{
  "requestId":{"type":["string","null"],"default":null,"description":"..."},
  "table":{"type":["string","null"],"default":null,"description":"..."},
  "values":{"type":["object","null"],"default":null,"description":"..."}}}
```

The tool description carries the requirement instead. That trade is correct: a description that the
agent reads plus an actionable error beats a schema keyword plus an opaque failure.

The description must also state the three limits that the schema cannot show: a value is a literal and
never a SQL expression, one call adds one row, and there is no conflict clause.

## `update` and `delete`

```csharp
public async Task<CallToolResult> UpdateAsync(
    string? requestId = null,
    string? table = null,
    Dictionary<string, JsonElement>? values = null,
    List<WriteCondition>? where = null,
    int? maxRows = null,
    CancellationToken cancellationToken = default)
```

`delete` is the same without `values`. `where` and `maxRows` are mandatory and follow the same
`= null` rule as every other write argument. See
[../database/structured-writes.md](../database/structured-writes.md).

```text
rowsAffected: 1
rowsChanged: 4
```

The description of `maxRows` must state that the number counts every row the write touches, including
a row that `ON DELETE CASCADE` removes in another table. It is the surprising part of the contract:
deleting one row that has three cascading children needs a `maxRows` of at least 4. See
[../decisions/0004-maxrows-bounds-total-changes.md](../decisions/0004-maxrows-bounds-total-changes.md).

The description of `where` must name the eight operators. The generated schema types them as a plain
string, thus the text is the only place the agent can read the set.

**The three write tools share one private path**, `RunStructuredWriteAsync`. It holds the order of the
controls — deduplication, budget, request slot, write slot, execute, charge, cache — thus the order
cannot drift between one tool and another. Each tool method owns only its own shape validation and its
result format.

## Related

- [../database/structured-writes.md](../database/structured-writes.md)
- [../security/write-controls.md](../security/write-controls.md)
- [../security/permissions.md](../security/permissions.md)
- [../database/connections.md](../database/connections.md)
- [../testing/e2e-harness.md](../testing/e2e-harness.md)
- [query-results.md](query-results.md)
