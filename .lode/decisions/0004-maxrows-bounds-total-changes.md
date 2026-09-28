# 0004 — `maxRows` bounds every changed row, not only the target table

## Decision

The post-execution check of a structured `update` and `delete` counts the `sqlite3_total_changes`
delta across the statement. That count includes the rows that `ON DELETE CASCADE` removes in other
tables and the rows that a trigger writes. `maxRows` therefore bounds the whole blast radius of one
call, not the rows of the target table.

## Context

The write connection sets `ForeignKeys = true`. `ExecuteNonQuery` reports the rows of the target
table alone, thus the obvious implementation of the limit does not see a cascade at all.

With that implementation a `delete` on one parent row passes both checks:

```text
pre-count      1 row matches the filter   -> 1 <= maxRows 1, pass
ExecuteNonQuery                           -> 1, pass
committed                                 -> the parent and every descendant are gone
```

One accepted request destroys an unbounded subtree and answers `rowsAffected: 1`. That is the exact
damage class this product exists to limit, and the MVP has no undo. See
[0001-no-undo-in-mvp.md](0001-no-undo-in-mvp.md).

## Consequences

The agent must count the cascade into `maxRows`. Deleting one row that has three cascading children
needs a `maxRows` of at least 4. The tool description states this in plain words, because it is
surprising.

`update` and `delete` answer two numbers, `rowsAffected` for the target table and `rowsChanged` for
the total. The second one is the number that `maxRows` bounds and that the write budget counts.

The write budget also charges `rowsChanged`. A cascaded row is real write volume.

The post-execution check becomes deterministically testable. Before this decision the branch could
only fire when the owning application changed the data between the pre-count and the write, which no
test can force. A cascade forces it from the public interface:
`DeleteToolTests.ACascadeOverTheLimitRollsBackAndKeepsEveryRow` seeds one job with three cascading
tags and asserts that `maxRows: 1` rolls back and leaves all four rows.

The count is cumulative for the lifetime of the connection, thus the code uses the difference across
one statement. Pooling is off and a write opens its own connection, so that difference belongs to the
one statement that ran.

## Alternatives that were rejected

**Count the target table only and document the gap.** Cheaper, and it leaves the product without the
guarantee that is its reason to exist. A non-guarantee in `SECURITY.md` does not help an agent that
already sent the call.

**Find the referencing tables and reject a cascading delete.** It needs `PRAGMA foreign_key_list` over
every table of the database for each write, and it would refuse a correct request that the limit would
have allowed. The total-changes counter gives the same bound for one native call.

## Related

- [../database/structured-writes.md](../database/structured-writes.md) — the two checks
- [../security/write-controls.md](../security/write-controls.md) — the budget
- [0001-no-undo-in-mvp.md](0001-no-undo-in-mvp.md)
- [0005-and-only-filter.md](0005-and-only-filter.md)
