# Connection policy: the backup paths

> **Status: mostly implemented.** The read-only connection, the write connection and its
> `ForeignKeys` rule, the pooling rule, `BEGIN IMMEDIATE`, the busy behaviour, the request semaphore,
> the write semaphore and its bounded wait, and the filesystem constraints are all current state in
> [../../database/connections.md](../../database/connections.md) and
> [../../database/structured-writes.md](../../database/structured-writes.md). This file keeps only the
> backup paths, which have no code.

## Connection kinds

| Operation | Open mode | Extra | State |
| --- | --- | --- | --- |
| `schema`, `query` | `ReadOnly` | `PRAGMA query_only=ON` | Implemented |
| `insert` | `ReadWrite` | `ForeignKeys = true` | Implemented |
| `update`, `delete` | `ReadWrite` | `ForeignKeys = true` | Implemented, same connection |
| `diagnostics` | `ReadOnly` | `PRAGMA query_only=ON` | Phase 5 |
| `execute_write_sql` | `ReadWrite` | `ForeignKeys = true` | Phase 6, same connection |
| `backup` source | `ReadOnly` | — | Phase 5 |
| `backup` destination | new file | — | Phase 5 |

**Invariant.** The backup source connection opens read-only. The Online Backup API reads the source
and writes the destination, thus the source needs no write access. A deployment with
`schema,read,backup` therefore never opens a write handle to the live database. See
[backups.md](backups.md).

The sidecar makes a write connection only for a write operation.

## The backup semaphore

| Semaphore | Default | Scope | State |
| --- | --- | --- | --- |
| Request | 4 | Each MCP operation | Implemented |
| Write | 1 | Structured writes and `execute_write_sql` | Implemented |
| Backup | 1 | The background backup | Phase 5 |

**Invariant.** No operation holds the write semaphore and the backup semaphore at the same time. The
background backup runs outside the request that started it, thus it holds no request slot. A second
backup request fails immediately instead of a wait. See [backups.md](backups.md).

The write semaphore bounds its wait by the busy timeout and then returns `DatabaseBusy`. The backup
semaphore must **not** copy that rule: a backup that waits would hold the caller for the whole copy,
and the design answers the second request at once. See
[../../database/connections.md](../../database/connections.md).

## Related

- [../../database/connections.md](../../database/connections.md) — current state
- [../../database/structured-writes.md](../../database/structured-writes.md) — the transaction order
- [../../database/sqlite-sandbox.md](../../database/sqlite-sandbox.md)
- [backups.md](backups.md)
