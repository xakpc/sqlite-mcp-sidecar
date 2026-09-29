# The one-statement check

`SqliteSecurity.ValidateSingleStatement` is the gate that every caller statement passes before
it runs: the `query` tool and `execute_write_sql`. It answers one `StatementCheck` value, and
both tools map that value to the same error code, thus one statement kind gives one code
wherever it appears.

Code: `src/Xakpc.SQLiteMCPSidecar/Database/SqliteSecurity.cs`.


`ValidateSingleStatement` prepares the text, reads the unconsumed `tail`, and finalizes the
statement. A non-empty tail is a second statement.

```sql
SELECT id FROM jobs                      -- Ok
SELECT 1; SELECT 2                       -- MultipleStatements -> InvalidQuery
DROP TABLE jobs                          -- Rejected -> QueryRejected
-- nothing at all                        -- Empty -> InvalidQuery
SELECT 1                                 -- ReadOnlyOnAWritePath -> QueryRejected,
                                         --   only with mustWrite
SELECT id FROM jobs                      -- Busy, when another connection holds the lock
                                         --   -> DatabaseBusy
```

**Lesson. An empty statement is not detected by a null check.** The branch used to read
`if (statement is null)`, which looks right and never runs: SQLitePCLRaw hands back a non-null
`sqlite3_stmt` wrapper around a null pointer for empty input. `Empty` was therefore dead code, the
text fell through to `Ok`, and `""`, whitespace and a comment alone all answered as a successful
query of **zero rows**. The agent read "the table is empty" where "you sent no statement" was the
truth, and on the raw write path the call even committed and cached that answer under the
`requestId`. Ask the statement for its own text instead:

```csharp
if (statement is null || string.IsNullOrWhiteSpace(raw.sqlite3_sql(statement).utf8_to_string()))
```

**`mustWrite` tells a bare `SELECT` from the read half of a write.** `execute_write_sql` passes it,
and `sqlite3_stmt_readonly` answers. The authorizer cannot: `AuthorizerPolicy.Dml` has to accept
read actions, because a `WHERE` clause, a subquery and `INSERT ... SELECT` all read, thus it sees
actions and never statement kinds. The check runs before `BEGIN IMMEDIATE`, so such a statement
never takes the write lock. See [raw-writes.md](raw-writes.md).

**`Busy` is separate from `Invalid` on purpose.** A prepare that fails because another connection
holds the lock says nothing about the statement. Folding it into `Invalid` told the agent to correct
correct SQL, and the sidecar exists to sit next to an application that writes, thus a locked
database is the ordinary condition here. See
[../decisions/0009-a-locked-database-is-not-a-caller-mistake.md](../decisions/0009-a-locked-database-is-not-a-caller-mistake.md).

Count statements by preparation, never by counting semicolons: a semicolon appears inside a string
literal and inside a comment.

**Lesson.** The authorizer runs during preparation, thus it decides first. `UPDATE ...; DELETE ...`
is `QueryRejected` and not `InvalidQuery`, because the first statement is denied before the tail
check happens.

The check prepares the statement one time, and execution prepares it again through `SqliteCommand`.
The cost is one extra preparation. Execution wholly through `raw` would need hand-written column
typing and value extraction.


## Related

- [sqlite-sandbox.md](sqlite-sandbox.md) — the sandbox that installs the authorizer this check runs under
- [raw-writes.md](raw-writes.md) — the `mustWrite` caller
- [../mcp/error-model.md](../mcp/error-model.md) — the codes each value maps to
- [../decisions/0009-a-locked-database-is-not-a-caller-mistake.md](../decisions/0009-a-locked-database-is-not-a-caller-mistake.md)
