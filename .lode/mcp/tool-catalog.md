# MCP tool catalog

The sidecar has one MCP endpoint at `/mcp` over Streamable HTTP. The catalog has nine tools at most,
and one of them exists.

Code: `src/Xakpc.SQLiteMCPSidecar/Mcp/SqliteTools.cs`, `src/Xakpc.SQLiteMCPSidecar/Program.cs`.

| Tool | Permission | Caller supplies SQL | `requestId` | State |
| --- | --- | --- | --- | --- |
| `schema` | `schema` | no | no | **Implemented** |
| `query` | `read` | yes, read-only | no | Phase 3 |
| `insert` | `write` | no | mandatory | Phase 4 |
| `update` | `write` | no | mandatory | Phase 4 |
| `delete` | `write` | no | mandatory | Phase 4 |
| `backup` | `backup` | no | no | Phase 5 |
| `backup_status` | `backup` | no | no | Phase 5 |
| `diagnostics` | `diagnostics` | no | no | Phase 5 |
| `execute_write_sql` | `danger-raw-write` | yes, DML | mandatory | Phase 6 |

A permission that has no tool yet exposes nothing.
`PermissionGatingTests.OnlyTheImplementedToolsAreExposed` starts a deployment with every permission
and asserts that `tools/list` holds `schema` only.

## Server registration

```csharp
builder.Services
    .AddMcpServer()
    .WithHttpTransport(transport => transport.SessionMode = HttpServerSessionMode.Stateless)
    .AddAuthorizationFilters()
    .WithTools<SqliteTools>();

app.MapMcp("/mcp").RequireAuthorization();
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
public async Task<string> SchemaAsync(CancellationToken cancellationToken)
```

Every tool and every parameter needs a `[Description]`. That text is the only description the agent
reads, and a vague description is the main reason an agent misuses a tool. Name the constraints in
the text. `PermissionGatingTests.EveryExposedToolCarriesADescription` asserts that each exposed tool
has one.

A failure reaches the caller as a code from
[../plans/design/error-model.md](../plans/design/error-model.md) and a short fixed explanation. The
detail goes to the log. A tool logs the name, the duration and the outcome through a
source-generated `[LoggerMessage]` method.

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

## Related

- [../security/permissions.md](../security/permissions.md)
- [../database/connections.md](../database/connections.md)
- [../testing/e2e-harness.md](../testing/e2e-harness.md)
- [../plans/design/toon-results.md](../plans/design/toon-results.md)
