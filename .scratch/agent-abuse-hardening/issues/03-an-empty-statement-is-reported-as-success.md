# An empty or comment-only statement is reported as success

Status: resolved

## What happens

`SqliteSecurity.ValidateSingleStatement` has a branch for a statement that prepares to nothing:

```csharp
if (statement is null)
{
    // Whitespace or a comment only. SQLite prepares nothing and reports no error.
    return StatementCheck.Empty;
}
```

`StatementCheck.Empty` maps to `InvalidQuery` with "The request holds no statement." in both
`SqliteTools.QueryAsync` and `SqliteTools.RunWriteAsync`.

**That branch never runs.** `sqlite3_prepare_v2` through SQLitePCLRaw hands back a non-null
`sqlite3_stmt` wrapper for empty input, thus the check falls through to `StatementCheck.Ok` and the
statement executes as nothing at all.

Observed:

| Call | Answer |
| --- | --- |
| `query` with `sql: ""` | `rows[0]`, `truncated: false` |
| `query` with `sql: "   \n\t  "` | `rows[0]`, `truncated: false` |
| `query` with `sql: "-- select nothing at all"` | `rows[0]`, `truncated: false` |
| `execute_write_sql` with `sql: "/* nothing */"` | `rowsAffected: 0` |

## Why it matters

The agent reads "the query ran and matched no rows" where the truth is "you sent no statement". Those
two lead to opposite next actions: the first says the data is not there, the second says fix the
request. An agent that concludes a table is empty can then decide that an insert is safe.

The raw write case costs more. It answers `rowsAffected: 0`, commits, and stores that answer under
the `requestId`. The key is spent. A retry with the same key replays "0 rows" for the whole five
minute window, so the agent cannot recover by retrying, which is exactly what the error message of
every other failure tells it to do.

## Reproduction

Four corpus entries, all passing today with `expect: "accepted-gap"`:

```text
query-sql-is-empty   query-sql-is-whitespace   query-sql-is-only-a-comment
raw-write-sql-is-only-a-comment
```

```powershell
dotnet test --solution Xakpc.SQLiteMCPSidecar.slnx --filter-method '*BadAgentTests*'
```

Each asserts that the call **succeeds**, thus fixing this issue makes them fail and forces the entry
to move to `expect: "error-code"`.

## Candidate fix

Do not depend on the wrapper being null. Ask the handle whether it holds a statement, or treat a
prepared statement with no SQL text as empty:

```csharp
if (statement is null || string.IsNullOrWhiteSpace(raw.sqlite3_sql(statement).utf8_to_string()))
{
    return StatementCheck.Empty;
}
```

Verify against the native build rather than against the wrapper type: the behaviour here belongs to
SQLitePCLRaw and it is the reason the branch looked correct. A test on Windows is necessary and not
sufficient, per [../../../.lode/practices.md](../../../.lode/practices.md).

## Afterwards

- Move the four cases to `expect: "error-code"` with `InvalidQuery`.
- Update [../../../.lode/database/sqlite-sandbox.md](../../../.lode/database/sqlite-sandbox.md),
  which describes the one-statement check.

## Resolution

`ValidateSingleStatement` no longer depends on the wrapper being null. It asks the prepared
statement for its own text and calls an empty one `StatementCheck.Empty`:

```csharp
if (statement is null || string.IsNullOrWhiteSpace(raw.sqlite3_sql(statement).utf8_to_string()))
```

`query` also rejects an absent, empty or whitespace `sql` in the tool method, before the database.
Both explanations now name the cause: "whitespace and a comment alone are not a query".

Four corpus cases moved to `expect: "error-code"` with `InvalidQuery`. Verified against a real
sidecar process as well as in process, because the behaviour belongs to the native build.
