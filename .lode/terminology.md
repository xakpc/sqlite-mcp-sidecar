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

## Writes

- **structured write** — a write that the server builds from a table name, values and a filter. The caller sends no SQL.
- **raw write** — a caller-supplied DML statement. It needs `danger-raw-write`.
- **filter model** — the small set of operators that structured writes accept. It is not a SQL expression language.
- **`maxRows`** — the caller-declared upper bound on affected rows. It is mandatory for structured `update` and `delete`.
- **effective limit** — `min(client maxRows, SQLITE_SIDECAR_MAX_WRITE_ROWS)`.
- **bounded pre-count** — the `LIMIT N+1` count that runs before a structured `update` or `delete`. It rejects a broad filter before any write.
- **row-limit rollback** — the rollback that reverts a structured write when the affected row count is more than the effective limit.
- **write budget** — the rolling per-minute cap on total written rows for the sidecar **process**. It is not shared between processes.
- **`requestId`** — the mandatory idempotency key on each write tool. The same key returns the stored response of a committed write.
- **replayed write** — a write request that the sidecar answered from the idempotency cache. It changed nothing.

## SQLite controls

- **sandbox** — the set of always-on SQLite controls. It is not a feature flag.
- **authorizer** — the native SQLite callback that accepts or rejects each action during statement preparation.
- **defensive mode** — `SQLITE_DBCONFIG_DEFENSIVE`. It blocks direct schema corruption.
- **trusted schema off** — `SQLITE_DBCONFIG_TRUSTED_SCHEMA = 0`. It distrusts objects stored in the schema.
- **runtime limit** — a per-connection `sqlite3_limit` value, for example SQL length or VDBE operation count.
- **interrupt** — the `sqlite3_interrupt` call that stops a running statement on timeout or cancellation.
- **hard boundary** — an action that the sidecar always rejects, also with `danger-raw-write`.

## Backups

- **background backup** — the copy that the `backup` tool starts. The tool call does not wait for it.
- **partial backup** — a file with the `*.db.partial` suffix. It is an incomplete copy. The sidecar renames it only after success.
- **runaway cap** — the hardcoded 10-minute limit on one background backup.
- **backup ring** — the small in-memory list of recent backup outcomes that `backup_status` reports.

## Results

- **TOON** — Token-Oriented Object Notation. The compact tabular text format for row data only.
- **bounded result** — a buffered result that respects the row limit and the byte limit.
- **truncated** — the flag that reports that the sidecar stopped at a limit.

## Related

- Permission detail: [plans/design/permission-model.md](plans/design/permission-model.md)
- Sandbox detail: [plans/design/sqlite-sandbox.md](plans/design/sqlite-sandbox.md)
- Idempotency detail: [plans/design/write-idempotency.md](plans/design/write-idempotency.md)
- Result format detail: [plans/design/toon-results.md](plans/design/toon-results.md)
