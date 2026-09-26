# Required tests

The mandatory test lists of the MVP. The phase that owns each list is in
[mvp-roadmap.md](mvp-roadmap.md), and the harness that runs them is in
[../testing/e2e-harness.md](../testing/e2e-harness.md).

Write each one through `SidecarHarness`, thus the same test also runs against the published Linux
artifact. The sandbox depends on the native SQLite build, thus a Windows developer run does not
prove the shipped behaviour.

## Required security tests

These are mandatory. Each statement must fail remotely, **also** with `danger-raw-write`.

```sql
ATTACH DATABASE '/tmp/x.db' AS x;
DETACH DATABASE x;

CREATE TABLE hacked(id);
DROP TABLE jobs;

PRAGMA writable_schema = ON;
PRAGMA journal_mode = OFF;

SELECT load_extension('/tmp/malicious.so');

VACUUM INTO '/tmp/copy.db';
```

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

## Required functional tests

```text
schema discovery returns usable DDL
TOON query output
read while the application writes

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
- [design/sqlite-sandbox.md](design/sqlite-sandbox.md) — the hard boundaries
