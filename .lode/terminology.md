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
- **`backup`** — permission to create a backup file in the backup directory.
- **`diagnostics`** — permission to read database health values.
- **`danger-raw-write`** — permission to run caller-supplied `INSERT`, `UPDATE` and `DELETE` SQL.

## Writes

- **structured write** — a write that the server builds from a table name, values and a filter. The caller sends no SQL.
- **raw write** — a caller-supplied DML statement. It needs `danger-raw-write`.
- **filter model** — the small set of operators that structured writes accept. It is not a SQL expression language.
- **`maxRows`** — the caller-declared upper bound on affected rows. It is mandatory for structured `update` and `delete`.
- **effective limit** — `min(client maxRows, SQLITE_SIDECAR_MAX_WRITE_ROWS)`.
- **write budget** — the rolling per-minute cap on total written rows for the deployment.
- **row-limit rollback** — the short transaction that reverts a structured write when the affected row count is over the effective limit.
- **backup-before-delete** — the optional policy that makes a successful backup a precondition of a delete.

## SQLite controls

- **sandbox** — the set of always-on SQLite controls. It is not a feature flag.
- **authorizer** — the native SQLite callback that accepts or rejects each action during statement preparation.
- **defensive mode** — `SQLITE_DBCONFIG_DEFENSIVE`. It blocks direct schema corruption.
- **trusted schema off** — `SQLITE_DBCONFIG_TRUSTED_SCHEMA = 0`. It distrusts objects stored in the schema.
- **runtime limit** — a per-connection `sqlite3_limit` value, for example SQL length or VDBE operation count.
- **interrupt** — the `sqlite3_interrupt` call that stops a running statement on timeout or cancellation.
- **hard boundary** — an action that the sidecar always rejects, even with `danger-raw-write`.

## Results

- **TOON** — Token-Oriented Object Notation. The compact tabular text format for results.
- **bounded result** — a buffered result that respects the row limit and the byte limit.
- **truncated** — the flag that reports that the sidecar stopped at a limit.

## Related

- Permission detail: [plans/design/permission-model.md](plans/design/permission-model.md)
- Sandbox detail: [plans/design/sqlite-sandbox.md](plans/design/sqlite-sandbox.md)
- Result format detail: [plans/design/toon-results.md](plans/design/toon-results.md)
