# Two concurrent calls with one requestId both write

Status: resolved

## What happens

`WriteDeduplication.Check` and `WriteDeduplication.Store` each take the lock separately, and
`SqliteTools.RunWriteAsync` calls `Store` only after the transaction commits. Nothing records that a
write is in flight. Every caller that reaches `Check` before the first one commits is told
`NotFound`, which means "execute".

The window is the whole duration of the write.

```text
caller A: Check(key) -> NotFound
caller B: Check(key) -> NotFound      <- B must not be told to execute here
caller A: execute, commit, Store(key)
caller B: execute, commit, Store(key) <- the row is written a second time
```

This is the failure that the idempotency key exists to prevent. `maxRows` bounds one call; it does
not bound the same call two times. See
[../../../.lode/decisions/0003-mandatory-idempotency-key.md](../../../.lode/decisions/0003-mandatory-idempotency-key.md).

## Why it is reachable

An MCP client retries a call that timed out. The HTTP transport is stateless and the retry opens a
new request, thus the retry and the original run at the same time whenever the original is still
working. That is exactly the case the key was added for.

## Reproduction

Both tests are `Explicit`, because they are red against today's code.

```powershell
dotnet test --solution Xakpc.SQLiteMCPSidecar.slnx `
  --filter-method '*ASecondCheckBeforeTheFirstStore*' -- --explicit only
dotnet test --solution Xakpc.SQLiteMCPSidecar.slnx `
  --filter-method '*ConcurrentCallsWithOneRequestId*' -- --explicit only
```

`WriteDeduplicationTests.ASecondCheckBeforeTheFirstStoreDoesNotSayExecute` is the proof. It calls
`Check` two times and needs no timing at all.

`LiveDatabaseTests.ConcurrentCallsWithOneRequestIdApplyOneWrite` is the end-to-end demonstration. It
holds the write slot, fires two calls with one `requestId`, releases, and counts the rows. The
observed run logs both writes as real:

```text
Write completed. tool=insert table=events rowsAffected=1 replayed=False outcome=ok
Write completed. tool=insert table=events rowsAffected=1 replayed=False outcome=ok
```

That test is one-sided: nothing proves both calls reached the parked state, thus it can pass while
the defect is present. It cannot fail while the defect is absent. The class-level test is the one
that decides.

## Candidate fix

Reserve the key inside `Check`, under the same lock that reads it.

- `Check` records an in-flight entry for a key it did not find, and returns `NotFound`.
- A second caller that finds an in-flight entry with the same payload hash waits for it, or gets
  `DatabaseBusy` with the existing "retry later with the same requestId" text.
- `Store` replaces the in-flight entry with the committed response.
- Every failure path releases the reservation, because a cached failure would make the
  "retry later" instruction impossible to follow. That invariant is already written down in
  `WriteDeduplication`.

The release path is the part that needs care: `RunWriteAsync` has six `catch` blocks and each one
must release.

## Afterwards

- Drop `Explicit` from both tests.
- Update [../../../.lode/security/write-controls.md](../../../.lode/security/write-controls.md), which
  describes the cache as it is today.

## Resolution

`WriteDeduplication.Check` reserves the identifier inside the lock that reads it. A second caller
that arrives before the first commits gets the new `DeduplicationOutcome.InFlight`, which
`SqliteTools.RunWriteAsync` answers with `DatabaseBusy` and "This requestId is already running."

The reservation ends in `Store` on a commit and in the new `Release` on every other path.
`RunWriteAsync` owns it from the `Check` onwards and releases it in a `finally`, thus an unexpected
exception cannot leak one either.

`Release` marks the entry rather than removing it: the key is already in the order queue and a
removal would let a later reservation enqueue it twice, which would let eviction drop a live entry.

Tests: `WriteDeduplicationTests` (four cases, no timing) and
`LiveDatabaseTests.ConcurrentCallsWithOneRequestIdApplyOneWrite`. Neither is `Explicit` any more.
