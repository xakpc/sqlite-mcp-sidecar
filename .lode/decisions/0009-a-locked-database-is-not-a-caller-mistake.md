# 0009 — A locked database is not a caller mistake

A statement that does not prepare because another connection holds the lock returns
`DatabaseBusy`, and the busy timeout lives on the SQLite handle so that the preparation waits for
it.

## The defect

`SqliteSecurity.ValidateSingleStatement` mapped **every** non-`SQLITE_AUTH` prepare result to
`StatementCheck.Invalid`, and `Invalid` reaches the agent as:

```text
InvalidQuery: The statement did not compile. Check the syntax and the table and column names.
```

`SQLITE_BUSY` and `SQLITE_LOCKED` arrive through that same path. A correct `SELECT` against a
briefly locked database therefore told the agent to rewrite a statement that had nothing wrong with
it, and the agent would keep rewriting for as long as the owning application held the lock.

The second half was worse. `ValidateSingleStatement` calls `raw.sqlite3_prepare_v2` **directly**,
thus the busy retry loop that `Microsoft.Data.Sqlite` drives from `DefaultTimeout` never saw the
call. `SQLITE_SIDECAR_BUSY_TIMEOUT_SECONDS` did nothing at all on the read path: a query against a
locked database failed in about 20 milliseconds with a 3-second timeout configured.

## The decision

```csharp
if (result is raw.SQLITE_BUSY or raw.SQLITE_LOCKED)
{
    return StatementCheck.Busy;
}
```

`StatementCheck.Busy` maps to `SidecarError.DatabaseBusy` at both switch sites, `query` and
`execute_write_sql`. That is the code the execution path already returns for the same condition,
thus one condition gives one code wherever it appears.

`ApplyBaseline` now sets the busy timeout on the handle:

```csharp
raw.sqlite3_busy_timeout(handle, options.BusyTimeoutSeconds * 1000);
```

The earlier comment said the opposite: that the timeout belonged to the provider alone and that a
second mechanism on the same handle would be a conflict. That reasoning held while every statement
went through `Microsoft.Data.Sqlite`. The raw prepare broke the assumption. The two compose rather
than conflict: SQLite waits inside the call and the provider retries around it.

## Why it matters more than an error code usually does

The whole premise of the product is a sidecar next to an application that keeps writing. A locked
database is the **ordinary** condition here, not an exotic one. The error model exists so that an
agent selects its next action from the code, and these two codes ask for opposite actions:

| Code | What the agent does |
| --- | --- |
| `InvalidQuery` | Rewrites the statement. Useless here, and it never succeeds. |
| `DatabaseBusy` | Waits and retries. Correct. |

## How it was found, and why no earlier test could find it

The Phase 7 container run found it. The in-process harness gives **each test its own database
file**, thus no in-process test ever has two clients on one file, and the whole condition is
unreachable there. The container run puts the entire suite on one mounted database, and
`QueryToolTests.AnEmptyResultIsNotAFailure` failed with `InvalidQuery` against a statement that runs
correctly by hand.

`QueryToolTests.AQueryAgainstALockedDatabaseIsDatabaseBusy` covers it now. The test holds
`BEGIN EXCLUSIVE` from a second connection and asserts the code, and it also asserts that the call
took longer than half a second — that second assertion is what proves the handle-level timeout,
because without it the call returns in milliseconds.

**The test needs a rollback-journal database**, `journalMode: "delete"`. A WAL writer does not block
a reader, thus the condition cannot be produced in WAL at all and a WAL version of this test would
assert nothing.

## Related

- [../database/sqlite-sandbox.md](../database/sqlite-sandbox.md) — the baseline and the one-statement check
- [../database/connections.md](../database/connections.md) — the busy timeout and the semaphores
- [../mcp/error-model.md](../mcp/error-model.md) — the code set
- [../testing/e2e-harness.md](../testing/e2e-harness.md) — the container target
