# 0008 — A committed raw write never fails on result size

A `RETURNING` clause on `execute_write_sql` answers through the same bounded pipeline as `query`, but
it never returns `ResultTooLarge`. A result that does not fit reports truncation, and the write
commits.

## Context

The read path has one clean failure for a result that does not fit: `ResultTooLarge`, when one single
row is larger than the whole byte budget. Nothing is lost, because a `SELECT` changes nothing and the
agent retries with fewer columns.

A `RETURNING` clause breaks that symmetry. The rows arrive **after** the write has run. By the time the
sidecar knows the result is too large, the work is done.

## Decision

The result size never fails a raw write.

| Case | Answer |
| --- | --- |
| Rows fit | `rowsAffected` and the TOON block, `truncated: false` |
| Row limit or byte limit reached | `rowsAffected` and the rows that fit, `truncated: true` |
| No single row fits the byte budget | `rowsAffected` and a line that says the rows were not returned |

The write commits in every one of those cases.

The reader is drained to completion before the commit:

```csharp
rows = await QueryResult.ReadAsync(reader, _options, token).ConfigureAwait(false);
while (await reader.ReadAsync(token).ConfigureAwait(false))
{
}
```

## Why

**A failure code would be a lie.** `IsError` tells an agent that its request did not happen. The
request did happen: rows are gone or changed. An agent that reads `ResultTooLarge` and retries with
fewer `RETURNING` columns would apply the write a second time, and the retry carries a new `requestId`
because the agent believes the first call failed. The idempotency cache cannot save it, because only a
committed outcome is stored under the key the agent abandoned.

**The drain is not optional.** SQLite produces the rows of a `RETURNING` clause as the write
progresses. A reader that stops at the row limit stops the statement, thus the remaining rows are never
written. That is a half-applied write that no code path reports: `rowsAffected` would even look
plausible. The bounded read gives the agent what fits and the drain gives the database the whole write.

**The number the agent must act on is `rowsAffected`.** The rows of a `RETURNING` clause are a
convenience. Losing some of them to a limit costs the agent a follow-up `query`; losing the write costs
the operator data.

## Consequences

- `ResultTooLarge` stays a read-path code. `error-model.md` says which tools can return it.
- A large `RETURNING` clause is read work that the agent cannot see the whole of. The row limit and the
  byte limit still bound the memory of the sidecar, which is what they exist for.
- `RawWriteToolTests.ATruncatedReturningResultStillAppliesTheWholeWrite` is the guard. It sets
  `MAX_ROWS=1`, deletes three rows with `RETURNING id`, and asserts one row in the answer and zero rows
  left in the table. Remove the drain and that test fails.

## Related

- [../database/raw-writes.md](../database/raw-writes.md)
- [../mcp/query-results.md](../mcp/query-results.md)
- [../mcp/error-model.md](../mcp/error-model.md)
- [0003-mandatory-idempotency-key.md](0003-mandatory-idempotency-key.md)
