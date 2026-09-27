# Required tests

The mandatory test lists of the MVP. The phase that owns each list is in
[mvp-roadmap.md](mvp-roadmap.md), and the harness that runs them is in
[../testing/e2e-harness.md](../testing/e2e-harness.md).

Write each one through `SidecarHarness`, thus the same test also runs against the published Linux
artifact. The sandbox depends on the native SQLite build, thus a Windows developer run does not
prove the shipped behaviour.

## Required security tests

These are mandatory. Each statement must fail remotely, **also** with `danger-raw-write`.

The list runs in `SandboxBoundaryTests` through the `query` tool, and Phase 6 runs it again through
`execute_write_sql`. Two codes appear, and both mean that the action is not available:

```text
QueryRejected   the authorizer refused a valid statement
InvalidQuery    the text is not one statement, or the name does not exist
```

`load_extension` gives `InvalidQuery`, because extension loading is off on the connection and SQLite
never registers the function. See [../database/sqlite-sandbox.md](../database/sqlite-sandbox.md).

```sql
ATTACH DATABASE '/tmp/x.db' AS x;
DETACH DATABASE x;

CREATE TABLE hacked(id);
DROP TABLE jobs;

PRAGMA writable_schema = ON;
PRAGMA journal_mode = OFF;

SELECT load_extension('/tmp/malicious.so');

VACUUM INTO '/tmp/copy.db';

SELECT 1; SELECT 2;
```

`VACUUM INTO` also asserts that no file appears at the target path.

Startup tests. These run in `StartupTests`, and the list is complete:

```text
permissions=write                    -> startup fails, the message names schema and read
permissions=danger-raw-write         -> startup fails
permissions=reed                     -> startup fails
non-WAL database + write permission  -> starts, logs the warning
```

Structured write tests:

```text
UPDATE without WHERE            -> InvalidWrite
DELETE without WHERE            -> InvalidWrite
write without requestId         -> InvalidWrite
broad filter, pre-count fails   -> WriteLimitExceeded, no rows written
over maxRows after execution    -> rollback, WriteLimitExceeded
many small writes over budget   -> WriteBudgetExceeded
```

Idempotency tests:

```text
same requestId twice            -> one write, the same response two times
same requestId, new payload     -> InvalidWrite
requestId after DatabaseBusy    -> the retry executes
requestId over 128 characters   -> InvalidWrite
```

Raw write tests:

```text
raw INSERT succeeds with danger-raw-write
raw UPDATE succeeds with danger-raw-write
raw DELETE succeeds with danger-raw-write

raw write rejected without danger-raw-write
raw write without requestId rejected
raw write rows count toward the budget

raw DROP rejected
raw ATTACH rejected
raw PRAGMA mutation rejected
raw transaction control rejected
```

## Required endpoint tests

`RateLimitTests` runs the request budget list and it is complete:

```text
a request over the budget -> 429
a request with no token also uses a permit
/health is outside the budget
```

The budget test sets `MAX_REQUESTS_PER_MINUTE=2`, thus it is skipped on the external target, which
cannot restart the process. The `429` matters: a `503` would tell an agent that the sidecar is
broken. See [../security/public-endpoint.md](../security/public-endpoint.md).

## Required functional tests

The read list runs in `SchemaToolTests` and `QueryToolTests` and it is complete:

```text
schema discovery returns usable DDL
TOON query output
read while the application writes
empty result
ordinary SQL functions still work
join with repeated column names
row-limit truncation
byte-limit truncation
one oversized row -> ResultTooLarge
runaway query -> QueryTimedOut
```

Each remaining item belongs to its phase:

```text
structured insert
structured update
structured delete
rollback after a structured maxRows violation

raw INSERT
raw UPDATE
raw DELETE
raw UPDATE RETURNING

backup returns before the copy completes
backup_status reports success
backup_status reports failure
a second backup during a backup -> BackupFailed
a stopped backup leaves only a partial file
startup deletes a stale partial file

read-only deployment
structured-write deployment
danger-raw-write deployment

WAL database
rollback-journal database

SQLITE_BUSY behavior
query timeout
concurrency limiting
```

Run the functional suite against the published Linux artifact. The sandbox depends on the
native SQLite build, thus a Windows developer run does not prove the shipped behavior.


## Related

- [mvp-roadmap.md](mvp-roadmap.md) — which phase owns which list
- [../testing/e2e-harness.md](../testing/e2e-harness.md)
- [../database/sqlite-sandbox.md](../database/sqlite-sandbox.md) — the hard boundaries
