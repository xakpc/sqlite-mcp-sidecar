# Agent abuse hardening

Status: resolved

Four gaps that the two new end-to-end suites found. **All four are fixed.** The corpus holds no
`rejected-opaque` and no `accepted-gap` case any more, and no test is `Explicit`.

The suites are `LiveDatabaseTests` and `BadAgentTests`. See
[../../.lode/testing/live-database-suite.md](../../.lode/testing/live-database-suite.md) and
[../../.lode/testing/bad-agent-suite.md](../../.lode/testing/bad-agent-suite.md).

| Issue | What | Severity | Fix |
| --- | --- | --- | --- |
| [01](issues/01-concurrent-requestid-applies-twice.md) | Two concurrent calls with one `requestId` both write. | High | The identifier is reserved in the lock that reads it. |
| [02](issues/02-binder-failures-lose-the-error-code.md) | A wrong argument type gives the agent no error code. | Medium | A `tools/call` filter maps a binder failure to a code. |
| [03](issues/03-an-empty-statement-is-reported-as-success.md) | An empty or comment-only statement answers success. | Medium | The one-statement check asks the statement for its text. |
| [04](issues/04-execute-write-sql-accepts-a-select.md) | `execute_write_sql` runs a `SELECT` and takes the write lock. | Low | `sqlite3_stmt_readonly` rejects it before the transaction. |

Issue 01 was the one that damages data. The other three misled an agent rather than corrupting a
database, which still matters: the product is a security boundary between an agent and a live
database, and an agent acts on what the sidecar tells it.

Each fix is verified in process and against a real sidecar process on three permission sets. The
behaviour of 03 belongs to the native SQLite build, thus the external run is what proves it.
