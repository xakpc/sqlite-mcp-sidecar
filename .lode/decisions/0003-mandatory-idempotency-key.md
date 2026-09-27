# Each write tool needs a mandatory requestId

**Status: accepted.**

`insert`, `update`, `delete` and `execute_write_sql` need a `requestId` value. The sidecar
stores the response of a committed write for 5 minutes and returns the stored response for a
repeated identifier. A missing identifier is `InvalidWrite`.

## Why the control exists

The MCP HTTP transport is stateless and the query timeout is 10 seconds. A write can commit
and then lose its response. An MCP client retries such a call, and without deduplication the
write applies two times. `maxRows` bounds one call. It does not bound the same call two times.
This is the most frequent way that a correct agent damages a database.

## Why it is mandatory and not optional

An optional key protects only the clients that send one. The clients that need the protection
most are the clients that do not think about retries.

## Why failures are not cached

Only a committed outcome goes into the cache. `DatabaseBusy` and `WriteBudgetExceeded` tell
the agent to retry later, and an agent retries with the same identifier. A cached failure would
make that retry impossible for the full window, thus the error model would give false
instructions.

## Consequences

- `requestId` is part of the tool contract. Removal later breaks clients, thus this decision is difficult to reverse.
- The tool descriptions must tell the agent to keep the same identifier for a retry and to use a new identifier for new work.
- A repeated identifier with a different payload is `InvalidWrite`, which needs a hash of the request in each cache entry.
- The cache is process memory with a bounded size. It gives no guarantee across a restart or across two processes.

## Related

- [../plans/design/write-idempotency.md](../plans/design/write-idempotency.md)
- [../mcp/error-model.md](../mcp/error-model.md)
