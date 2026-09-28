# Terminology

Short definitions of the domain language. Use these exact words in code, logs and
documentation.

## Product

- **sidecar** — the `sqlite-sidecar-mcp` process. It runs next to the owning application.
- **owning application** — the program that already uses the SQLite database. It does not change.
- **deployment** — one running sidecar with one database, one token and one permission set.
- **agent** — the MCP client. It is authorized but it can be mistaken.
- **agent-safe** — a control that limits the damage from a mistaken agent, not only from an attacker.
- **blast radius** — the maximum damage that one accepted request can cause.

## Permissions

- **permission** — a deployment-level capability name. It controls which MCP tools exist.
- **`schema`** — permission to read database metadata.
- **`read`** — permission to run arbitrary read-only SQL.
- **`write`** — permission to run structured writes. It does not accept caller SQL.
- **`backup`** — permission to start a backup and to read the backup status.
- **`diagnostics`** — permission to read database health values.
- **`danger-raw-write`** — permission to run caller-supplied `INSERT`, `UPDATE` and `DELETE` SQL.
- **read floor** — the rule that `write` and `danger-raw-write` are only valid together with `schema` and `read`. Startup enforces it.
- **permission claim** — the `perm` claim that the authentication handler issues for each permission of the deployment. An `[Authorize(Policy = "perm:<name>")]` policy tests it.
- **primary gating** — the removal of an unpermitted tool from `tools/list`. It is the main control.
- **backstop gating** — the rejection of a direct `tools/call` of an unpermitted tool name. It covers a registration mistake.

## Writes

- **structured write** — a write that the server builds from a table name, values and a filter. The caller sends no SQL.
- **raw write** — a caller-supplied DML statement. It needs `danger-raw-write`.
- **filter model** — the small set of operators that structured writes accept. It is not a SQL expression language. It is a flat list and not a tree, and every condition joins with `and`. There is no `or`.
- **condition** — one item of a filter: a column, an operator and, for a comparison, a value.
- **canonical name** — the spelling of a table or a column that the live schema holds. The server quotes this name into a statement, never the name that the caller sent.
- **literal value** — a value that a structured write accepts: a string, a number, a boolean or null. A SQL expression is not a value.
- **`maxRows`** — the caller-declared upper bound on changed rows. It is mandatory for structured `update` and `delete`.
- **effective limit** — `min(client maxRows, SQLITE_SIDECAR_MAX_WRITE_ROWS)`.
- **`rowsAffected`** — the rows of the target table that a write changed. It is what the agent asked about.
- **`rowsChanged`** — every row that a write changed, including a row that `ON DELETE CASCADE` removed and a row that a trigger wrote. It is the blast radius, and it is the number that the effective limit bounds and that the write budget counts.
- **bounded pre-count** — the `LIMIT N+1` count that runs before a structured `update` or `delete`. It rejects a broad filter before any write.
- **row-limit rollback** — the rollback that reverts a structured write when `rowsChanged` is more than the effective limit.
- **write budget** — the rolling per-minute cap on total written rows for the sidecar **process**. It is not shared between processes. Do not confuse it with the **request budget**.
- **`requestId`** — the mandatory idempotency key on each write tool. The same key returns the stored response of a committed write.
- **replayed write** — a write request that the sidecar answered from the idempotency cache. It changed nothing.

## Testing

- **harness** — `SidecarHarness`. It gives one sidecar under test, in this process or an external one.
- **in-process target** — the default. `WebApplicationFactory` hosts the sidecar and the MCP client speaks over its `HttpClient`.
- **external target** — a sidecar that already runs. `SIDECAR_E2E_URL`, `SIDECAR_E2E_TOKEN` and `SIDECAR_E2E_PERMISSIONS` select it.
- **sample database** — the database that `Fixtures/sample-db.sql` builds. The test suite and the dev script share it.
- **dev sidecar** — the live sidecar that `scripts/dev-sidecar.ps1` starts for a manual session.

## Public endpoint

- **request budget** — the fixed one-minute cap on MCP requests for the sidecar **process**. It counts each request, also one with no token. It has no partition.
- **path contract** — the rule that the sidecar routes the whole public path, `/db/mcp`, thus no proxy rewrites it.
- **host gate** — the reverse proxy. It matches the `Host` header to route a request. The sidecar does no host filtering.
- **forwarded address** — the `X-Forwarded-For` value. It is a claim of the caller, not a fact.

## SQLite controls

- **sandbox** — the set of always-on SQLite controls. It is not a feature flag.
- **authorizer** — the native SQLite callback that accepts or rejects each action during statement preparation.
- **defensive mode** — `SQLITE_DBCONFIG_DEFENSIVE`. It blocks direct schema corruption.
- **trusted schema off** — `SQLITE_DBCONFIG_TRUSTED_SCHEMA = 0`. It distrusts objects stored in the schema.
- **runtime limit** — a per-connection `sqlite3_limit` value, for example SQL length or VDBE operation count.
- **baseline** — the settings that `SqliteSecurity.ApplyBaseline` puts on a handle after each `Open`.
- **allowlist policy** — the authorizer rule that accepts a named few actions and rejects each other one.
- **interrupt** — the `sqlite3_interrupt` call that stops a running statement on timeout or cancellation.
- **progress handler** — the callback that reports cancellation from inside the virtual machine. It is the second stop path.
- **one-statement check** — the preparation that proves the caller sent exactly one statement. A non-empty `tail` is a second statement.
- **hard boundary** — an action that the sidecar always rejects, also with `danger-raw-write`.

## Backups

- **background backup** — the copy that the `backup` tool starts. The tool call answers with a task and does not wait for it.
- **partial backup** — a file with the `*.db.partial` suffix. It is an incomplete copy. The sidecar renames it only after success.
- **step loop** — the loop of `sqlite3_backup_step(backup, 256)` calls that makes one backup. It releases the source lock between steps, thus the owning application keeps writing.
- **restart** — the copy starting again from the first page because the source changed. A rising `sqlite3_backup_remaining` is the signal.
- **runaway cap** — the hardcoded limit of 50 restarts on one background backup. It counts restarts and not seconds.
- **label** — the short caller-supplied string that the server sanitizes into a backup file name. It is never a path.
- **task-mode tool** — a tool that answers with a task id instead of a result. `backup` is the only one.
- **execution mode selector** — the delegate that gives each tool its `McpTaskExecutionMode`. It pins every tool except `backup` to `Synchronous`.

## Results

- **TOON** — Token-Oriented Object Notation. The compact tabular text format for row data only.
- **bounded result** — a buffered result that respects the row limit and the byte limit.
- **truncated** — the flag that reports that the sidecar stopped at a limit.
- **blob placeholder** — the text `<blob: N bytes>` that replaces a BLOB value in a result.
- **tool failure** — a `CallToolResult` with `IsError` set. It carries an error code. A tool never throws to report one.

## Related

- Permission detail: [security/permissions.md](security/permissions.md)
- Public endpoint detail: [security/public-endpoint.md](security/public-endpoint.md)
- Testing detail: [testing/e2e-harness.md](testing/e2e-harness.md)
- Sandbox detail: [database/sqlite-sandbox.md](database/sqlite-sandbox.md)
- Idempotency detail: [security/write-controls.md](security/write-controls.md)
- Backup detail: [database/backups.md](database/backups.md)
- Diagnostics detail: [database/diagnostics.md](database/diagnostics.md)
- Structured write detail: [database/structured-writes.md](database/structured-writes.md)
- Result format detail: [mcp/query-results.md](mcp/query-results.md)
