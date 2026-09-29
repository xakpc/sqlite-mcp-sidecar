# execute_write_sql runs a SELECT and takes the write lock for it

Status: resolved

## What happens

The tool description of `execute_write_sql` says:

> It must be exactly one statement: a second statement, any SELECT-only work that belongs in the
> query tool, DDL, ATTACH, VACUUM, any PRAGMA and BEGIN or COMMIT are all rejected

A bare `SELECT` is not rejected. It prepares, it runs inside `BEGIN IMMEDIATE`, and the answer is:

```text
rowsAffected: 0
```

## Why it happens

`AuthorizerPolicy.Dml` has to accept read actions, because `INSERT INTO t SELECT ...`, a subquery and
a `WHERE` clause all read. The authorizer sees actions and not statement kinds, thus it cannot tell a
read inside a write from a read on its own. Nothing else checks the statement kind.

## How much it matters

Not much, and it is not nothing.

- **No data leaks.** A raw write returns rows only through a `RETURNING` clause, and a `SELECT` has
  none, so `result.Rows` is null and the answer carries the row count alone.
- **No privilege is gained.** The read floor means `danger-raw-write` never exists without `read`,
  thus the caller could already run the statement through `query`.
- **It takes the write lock.** The statement runs inside `BEGIN IMMEDIATE`, so a slow `SELECT` sent
  to this tool holds the write lock of the database for its whole duration, and the owning
  application waits. The `query` tool runs the same statement on a read-only connection and blocks
  nobody. An agent that picks the wrong tool therefore degrades the application it is a sidecar to.
- **The description is wrong**, and the description is the only thing the agent reads.

## Reproduction

The corpus entry `raw-write-sql-is-a-select`, `expect: "accepted-gap"`.

```powershell
dotnet test --solution Xakpc.SQLiteMCPSidecar.slnx --filter-method '*BadAgentTests*'
```

## The two options

1. **Reject a read-only statement.** After preparation, ask SQLite whether the statement writes:
   `sqlite3_stmt_readonly` returns true for a `SELECT` and false for DML. Reject a read-only
   statement with `QueryRejected` and text that sends the agent to the `query` tool. This is one call
   on a handle the code already has, and it costs nothing at runtime.
2. **Correct the description** and accept that the tool runs a `SELECT` that returns no rows. Cheaper,
   and it leaves the write-lock behaviour in place.

Option 1 is the better one: it makes the description true, it keeps the tools apart, and it removes
a way for a careless agent to block the owning application.

## Afterwards

- Move `raw-write-sql-is-a-select` to `expect: "error-code"`, or to `ok` with a `why` that records
  the decision if option 2 is chosen.
- Update [../../../.lode/database/raw-writes.md](../../../.lode/database/raw-writes.md) and the tool
  description in `SqliteTools.cs`.

## Resolution

Option 1. `ValidateSingleStatement` takes `mustWrite`, which `ExecuteWriteSqlAsync` passes, and it
returns the new `StatementCheck.ReadOnlyOnAWritePath` when `sqlite3_stmt_readonly` reports that the
statement writes nothing. The code is `QueryRejected` and the text names the other tool: "The
statement changes nothing. This tool runs one INSERT, UPDATE or DELETE. Send a SELECT to the query
tool instead."

The check runs before `BEGIN IMMEDIATE`, thus a `SELECT` sent here never takes the write lock at all.
The tool description is true now. `raw-write-sql-is-a-select` moved to `expect: "error-code"`.
