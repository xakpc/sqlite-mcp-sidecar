# Connection policy: what is still unbuilt

> **Status: one row left.** Every connection and every semaphore of this design now has code. The
> read-only connection, the write connection and its `ForeignKeys` rule, the pooling rule,
> `BEGIN IMMEDIATE`, the busy behaviour, the request semaphore, the write semaphore and its bounded
> wait, and the filesystem constraints are current state in
> [../../database/connections.md](../../database/connections.md) and
> [../../database/structured-writes.md](../../database/structured-writes.md). The backup source, the
> backup destination and the backup semaphore are current state in
> [../../database/backups.md](../../database/backups.md).

## The one remaining row

| Operation | Open mode | Extra | State |
| --- | --- | --- | --- |
| `execute_write_sql` | `ReadWrite` | `ForeignKeys = true` | Phase 6, the same connection as a structured write |

`execute_write_sql` adds no new connection kind. It reuses `OpenReadWriteAsync` and the write
semaphore, and it differs only in its authorizer policy, which is the DML policy. See
[raw-writes.md](raw-writes.md).

Delete this file when Phase 6 lands.

## Related

- [../../database/connections.md](../../database/connections.md) — current state
- [../../database/backups.md](../../database/backups.md) — the backup connections and the backup slot
- [raw-writes.md](raw-writes.md) — what Phase 6 builds
