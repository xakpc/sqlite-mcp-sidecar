# SQLite sandbox

The innermost security layer. It is always on, it is not a feature flag, and no permission disables
any part of it. `danger-raw-write` does not weaken it.

Code: `src/Xakpc.SQLiteMCPSidecar/Database/SqliteSecurity.cs`.

## Where the API comes from

`Microsoft.Data.Sqlite` exposes none of these controls, but it exposes the raw handle, and
`SQLitePCLRaw.core` exposes the native functions. The transitive reference is sufficient.

```csharp
var connection = new SqliteConnection(ReadOnlyConnectionString);
connection.Open();
sqlite3 handle = connection.Handle!;   // public property, type SQLitePCL.sqlite3
```

Verified against `Microsoft.Data.Sqlite` 11.0.0-rc.1.26425.128 and `SQLitePCLRaw.core` 3.0.5. The
same surface exists on `SQLitePCLRaw.core` 2.1.12, thus the code does not depend on the version.

| Control | Native call |
| --- | --- |
| Defensive mode | `raw.sqlite3_db_config(h, raw.SQLITE_DBCONFIG_DEFENSIVE, 1, out _)` |
| Trusted schema off | `raw.sqlite3_db_config(h, raw.SQLITE_DBCONFIG_TRUSTED_SCHEMA, 0, out _)` |
| Authorizer | `raw.sqlite3_set_authorizer(h, callback, state)` |
| Runtime limit | `raw.sqlite3_limit(h, id, value)` |
| Statement count | `raw.sqlite3_prepare_v2(h, sql, out stmt, out tail)` |
| Cancellation | `raw.sqlite3_interrupt(h)`, `raw.sqlite3_progress_handler(...)` |
| Last inserted rowid | `raw.sqlite3_last_insert_rowid(h)` |
| Rows of the last statement | `raw.sqlite3_changes(h)` |
| Rows of every statement | `raw.sqlite3_total_changes(h)` |

`Microsoft.Data.Sqlite` has no `LastInsertRowId` property: that member belongs to
`System.Data.SQLite`, which is a different library. Each of the last three calls reads connection state
and runs no statement, thus each one also works while an authorizer is installed. `sqlite3_changes` is
`rowsAffected` and `sqlite3_total_changes` is the blast radius; a raw write needs the first one, because
it executes through a reader and `ExecuteNonQuery` gives it no count. See
[../decisions/0004-maxrows-bounds-total-changes.md](../decisions/0004-maxrows-bounds-total-changes.md).

## Three entry points

```csharp
SqliteSecurity.ApplyBaseline(connection, options);              // immediately after Open
using var interrupt  = SqliteSecurity.RegisterInterrupt(connection, token);
using var authorizer = SqliteSecurity.InstallAuthorizer(connection, AuthorizerPolicy.Read);
var check = SqliteSecurity.ValidateSingleStatement(connection, sql);
```

`AuthorizerPolicy` has three values. The policy comes from the tool that runs, never from the
deployment permission set.

| Policy | Accepts | Used by |
| --- | --- | --- |
| `Read` | `SELECT`, `READ`, `RECURSIVE`, `FUNCTION` by name | `query` |
| `Write` | the same, plus `INSERT`, `UPDATE`, `DELETE` | `insert`, `update`, `delete` |
| `Dml` | the same as `Write`, minus DML against an `sqlite_%` object | `execute_write_sql` |

`SQLITE_SELECT` and `SQLITE_READ` in the two write policies are necessary and they are not a weakness:
the bounded pre-count is a `SELECT`, a `CHECK` constraint reads the new row, a foreign key reads the
referenced table, and an `INSERT ... SELECT` needs them too.

**`Dml` is the one policy that sees caller SQL on a write connection**, and that is the whole reason it
is a separate value. `Write` runs server-authored statements only, thus it needs no rule about the
target name: `StructuredWriteBuilder` already refuses an `sqlite_%` table by name. `Dml` reads the
table name out of `arg1`, which carries it for those three actions, and denies an internal object.
`UPDATE sqlite_sequence SET seq = 0` would change the autoincrement behaviour of the owning
application, which is not manipulation of the caller's own data. See
[raw-writes.md](raw-writes.md).

`SQLITE_TRANSACTION` and `SQLITE_PRAGMA` stay denied in **all three** policies. See the order below.

## Order

```mermaid
flowchart TD
    open[Open connection] --> base[ApplyBaseline: DEFENSIVE, TRUSTED_SCHEMA, limits]
    base --> srv["Server statement: PRAGMA query_only=ON"]
    srv --> intr[RegisterInterrupt]
    intr --> authz[InstallAuthorizer]
    authz --> chk[ValidateSingleStatement]
    chk --> exec[Execute the caller statement]
    exec --> close["Dispose in reverse: authorizer, interrupt, connection"]
```

**Invariant.** Apply the baseline after every `Open`, never one time at startup. These settings live
on the connection handle and not on the process.

**Invariant.** Install the authorizer after every server-authored statement. Each policy rejects
`PRAGMA`, and the sidecar runs its own `PRAGMA query_only=ON` on a read and its own
`PRAGMA table_info` on a write.

**Invariant.** On a write path the authorizer also goes on **after** `BEGIN IMMEDIATE` and comes off
**before** `COMMIT` or `ROLLBACK`, because each policy denies transaction control. The server would
otherwise reject its own transaction. Declaration order does not give this: a write path calls
`Dispose()` explicitly before the commit and in the `catch`. See
[structured-writes.md](structured-writes.md).

**`execute_write_sql` installs the authorizer two times.** The first scope is before the transaction
and covers `ValidateSingleStatement`, thus a refused statement never takes the write lock; the second
covers the execution. See [raw-writes.md](raw-writes.md).

**Invariant.** Remove the authorizer and the progress handler before the handle closes. On the read
path declaration order gives that for free: C# disposes in the reverse order.

**Lesson.** Connection pooling keeps handle state, including an authorizer from an earlier
operation. That is a privilege-escalation path: a read connection could inherit a write authorizer.
`Pooling=false` removes the whole class of residual-state defects. See
[connections.md](connections.md).

**Lesson.** The authorizer callback is a native callback. `AuthorizerScope` holds the delegate in a
field for the lifetime of the operation. A collected delegate causes a hard crash, not an exception.
The callback body also has a `catch` that returns `SQLITE_DENY`: an exception must never cross a
native callback boundary.

## The authorizer is an allowlist

The policy accepts a named few actions and rejects each other one.

```csharp
if (actionCode is SQLITE_SELECT or SQLITE_READ or SQLITE_RECURSIVE) return SQLITE_OK;
if (actionCode == SQLITE_FUNCTION) return IsDeniedFunction(name) ? SQLITE_DENY : SQLITE_OK;
return SQLITE_DENY;
```

Three reasons for an allowlist and not a denylist:

- SQLite has more than thirty action codes, with many `CREATE_*`, `DROP_*`, temporary and virtual
  table variants.
- A denylist admits with no message any action code that a later SQLite version adds.
- The action code of `VACUUM INTO` is not dependable. An allowlist rejects it with no need to name it.

`SQLITE_PRAGMA` is denied in every policy. No legitimate caller statement needs a pragma.

`SQLITE_FUNCTION` is allowed by default and denied by name, because a policy that rejected every
function would reject ordinary SQL. The denied names are `load_extension` and `fts3_tokenizer`.
`QueryToolTests.OrdinarySqlFunctionsStillWork` protects the default from becoming a rejection.

Do not inspect SQL with regular expressions or string matching. A regular expression does not see
comments, nested constructs or alternative spellings. The authorizer runs inside the parser and sees
the resolved action.

## Runtime limits

Only the SQL length comes from configuration, `SQLITE_SIDECAR_MAX_SQL_BYTES`. The others are
constants in `SqliteSecurity`: an operator has no reason to raise them, and a configuration value is
a way to weaken the sandbox by accident.

```text
SQLITE_LIMIT_ATTACHED             0        a second, independent block on ATTACH
SQLITE_LIMIT_SQL_LENGTH           MaxSqlBytes
SQLITE_LIMIT_COLUMN               512
SQLITE_LIMIT_EXPR_DEPTH           100
SQLITE_LIMIT_COMPOUND_SELECT      10
SQLITE_LIMIT_VARIABLE_NUMBER      100
SQLITE_LIMIT_LIKE_PATTERN_LENGTH  1000
SQLITE_LIMIT_VDBE_OP              100000
```

**Lesson.** `SQLITE_LIMIT_VDBE_OP` bounds the number of instructions in the prepared program. That
is statement complexity and not work done. A recursive CTE is a short program that runs for ever,
thus this limit does not stop a runaway query. The timeout with `sqlite3_interrupt` stops it, and
the progress handler is the second path.

The baseline calls `sqlite3_busy_timeout(handle, BusyTimeoutSeconds * 1000)`.

**Lesson, and the earlier comment here said the opposite.** Leaving the busy timeout to
`DefaultTimeout` on the connection string is only correct while every statement goes through
`Microsoft.Data.Sqlite`. `ValidateSingleStatement` calls `sqlite3_prepare_v2` **directly**, thus the
retry loop of the provider never sees that call, and the configured timeout did nothing on the read
path: a query against a locked database failed in about 20 milliseconds with a 3-second timeout set.
The two mechanisms compose rather than conflict — SQLite waits inside the call and the provider
retries around it. See
[../decisions/0009-a-locked-database-is-not-a-caller-mistake.md](../decisions/0009-a-locked-database-is-not-a-caller-mistake.md).

## Extension loading

Extension loading is never on. `ApplyBaseline` calls `connection.EnableExtensions(false)`, which is
also the default. Extension loading is arbitrary native code inside the sidecar process.

**Lesson.** `load_extension` therefore returns `InvalidQuery` and not `QueryRejected`: SQLite does
not register the function at all and reports an unknown name. The block is one layer below the
authorizer. The authorizer name rule stays as the second block.

## One statement for each request

`ValidateSingleStatement` prepares the text, reads the unconsumed `tail`, and finalizes the
statement. A non-empty tail is a second statement.

```sql
SELECT id FROM jobs                      -- Ok
SELECT 1; SELECT 2                       -- MultipleStatements -> InvalidQuery
DROP TABLE jobs                          -- Rejected -> QueryRejected
SELECT id FROM jobs                      -- Busy, when another connection holds the lock
                                         --   -> DatabaseBusy
```

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

## Hard boundaries

The sidecar always rejects these actions, and `danger-raw-write` does not change it:

```text
ATTACH, DETACH
CREATE, DROP, ALTER
VACUUM, VACUUM INTO
load_extension
every PRAGMA
transaction control
more than one statement
DML against an sqlite_% object    (the Dml policy; a structured write refuses it by name)
```

`VACUUM INTO` writes a database copy to a caller-chosen path. It is a file exfiltration primitive,
thus it belongs with the DDL rejections and not with the backup feature.

**`VACUUM` fails through a different mechanism on each path**, and neither one is the authorizer:
SQLite runs no callback for it. The read connection is read-only with `query_only` on, and a raw write
runs inside `BEGIN IMMEDIATE`, thus SQLite reports `cannot VACUUM from within a transaction` and the
code is `DatabaseError`. Both tests assert the fact that matters: no file appears.

```text
danger-raw-write  !=  unrestricted SQLite
danger-raw-write  ==  raw INSERT / UPDATE / DELETE inside this sandbox
```

`SandboxBoundaryTests` proves each item remotely, and it runs the list two times: through `query` and
through `execute_write_sql`.

## Cancellation

```mermaid
flowchart LR
    ct[Linked token, CancelAfter QueryTimeoutSeconds] --> reg[Token registration]
    reg --> intr[sqlite3_interrupt]
    ct --> prog[Progress handler, each 1000 VM steps]
    prog --> stop[Non-zero return stops the statement]
```

Two stop paths. The registration fires on the token, and the progress handler reports the
cancellation from inside the virtual machine. SQLite then returns `SQLITE_INTERRUPT`, which the tool
maps to `QueryTimedOut`.

## Related

- [connections.md](connections.md) — the connection that this sandbox applies to
- [../mcp/error-model.md](../mcp/error-model.md) — the codes that a rejection produces
- [../mcp/query-results.md](../mcp/query-results.md) — the result pipeline
- [../security/threat-model.md](../security/threat-model.md)
- [raw-writes.md](raw-writes.md)
