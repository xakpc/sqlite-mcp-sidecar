# Write idempotency

> **Status: implemented.** Current state is in
> [../../security/write-controls.md](../../security/write-controls.md): the outcomes, the three bounds,
> the order of the controls, the payload hash and the invariants. The decision is in
> [../../decisions/0003-mandatory-idempotency-key.md](../../decisions/0003-mandatory-idempotency-key.md).

This file keeps only the part that no code covers yet: the scope row of the one write tool that the
catalog does not have yet, `execute_write_sql`.

## Scope

| Tool | `requestId` | State |
| --- | --- | --- |
| `insert` | mandatory | Implemented |
| `update`, `delete` | mandatory | Implemented |
| `execute_write_sql` | mandatory | Phase 6 |
| `query`, `schema`, `diagnostics` | not used | — |
| `backup`, `backup_status` | not used | — |

Reads need no deduplication, because a repeated read changes nothing. `backup` needs none, because the
filename carries a timestamp and a repeated backup costs disk space only. See
[backups.md](backups.md).

`execute_write_sql` hashes the SQL text and the parameters in place of the table and the value map. The
same rules apply: order the parameter keys, store a committed outcome only, and reject a repeated
identifier that carries a different payload. See [raw-writes.md](raw-writes.md).

## Related

- [../../security/write-controls.md](../../security/write-controls.md) — current state
- [../../database/structured-writes.md](../../database/structured-writes.md)
- [raw-writes.md](raw-writes.md)
- [../../mcp/error-model.md](../../mcp/error-model.md)
