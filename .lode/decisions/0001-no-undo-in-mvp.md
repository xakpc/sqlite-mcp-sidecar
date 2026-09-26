# The MVP has no undo for a delete

**Status: accepted.**

The sidecar gives an agent delete capability against a live production database, and it
provides no way to reverse a delete. There is no restore tool, no scheduled backup and no
automatic backup before a delete. The operator owns the backup strategy.

## Why this is surprising

The product is a security boundary that limits the damage from a mistaken agent. A reader
expects such a product to keep a copy before a destructive operation. An earlier design had an
optional `SQLITE_SIDECAR_BACKUP_BEFORE_DELETE` policy, and we removed it.

## Why we removed it

The policy needed a synchronous backup in the delete path. A backup takes minutes on a large
database, thus each delete would have that cost. The policy also needed two semaphores in one
operation, a definition of the lock order, and an answer for raw deletes, where the sidecar
cannot know before execution that a statement deletes rows. The cost was a large part of the
write path for a control that is off by default.

## What does not replace it

The row-limit transaction is **not** an undo. It reverts a write that matched more rows than
the effective limit. A delete of 50 rows with `maxRows: 100` commits, and the rows are gone.
The transaction protects breadth. It does not protect correctness.

## Consequences

- `SECURITY.md` and the README must state that the sidecar has no undo, and that the operator must run backups.
- The agent-facing guidance is to call `backup` first when a delete is not certain. That guidance needs `backup_status`, because an agent must be able to confirm that the copy succeeded.
- `SQLITE_SIDECAR_BACKUP_BEFORE_DELETE` does not exist.
- Delete recovery is the first candidate after the MVP.

## Related

- [../plans/design/structured-writes.md](../plans/design/structured-writes.md)
- [../plans/design/backups.md](../plans/design/backups.md)
- [../plans/design/threat-model.md](../plans/design/threat-model.md)
