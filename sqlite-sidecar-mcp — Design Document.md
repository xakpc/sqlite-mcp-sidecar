# sqlite-sidecar-mcp

**Status:** MVP design  
**Platform:** Linux  
**Distribution:** Docker / OCI image  
**Implementation:** C# / .NET 10  
**Database:** SQLite  
**Protocol:** MCP over Streamable HTTP  
**Tabular format:** TOON  
**License:** Apache-2.0  
**NativeAOT:** Targeted where practical

---

# 1. Product

`sqlite-sidecar-mcp` is a small, security-hardened MCP sidecar for an existing SQLite database.

It runs next to a SQLite database already used by another application and gives remote MCP clients controlled access to:

- schema;
- queries;
- structured writes;
- optional dangerous raw writes;
- backups;
- basic diagnostics.

The existing application does not need to change.

```text
Existing application
        │
        │ normal SQLite access
        ▼
      app.db
        ▲
        │ normal SQLite access
        │
sqlite-sidecar-mcp
        │
        │ authenticated MCP
        ▼
     AI agent
```

The database remains a normal SQLite database.

The sidecar is just another SQLite client.

---

# 2. Product positioning

The primary use case is not merely:

> SQLite over MCP.

The sidecar acts as a security boundary between an AI agent and a live application database.

The product should make it difficult for an authorized but mistaken agent to cause excessive damage.

Primary positioning:

> **Agent-safe MCP access for live SQLite databases.**

Secondary positioning:

> **A secure-by-default way to expose SQLite through MCP.**

Security claims should be based on concrete, documented controls rather than implying that any network-exposed database is absolutely safe.

---

# 3. Design principles

The project follows KISS and YAGNI.

MVP rules:

- one SQLite database per sidecar;
- one production `.csproj`;
- one executable;
- one MCP endpoint;
- one deployment token;
- deployment-level permissions;
- no users;
- no roles;
- no internal ACL database;
- no generic database abstraction;
- no plugin architecture;
- no REST SQL API;
- no CLI;
- no browser UI;
- no custom TOON implementation;
- no custom SQLite build unless actually needed;
- no speculative extensibility.

When different trust boundaries are required, deploy another sidecar.

---

# 4. Deployment model

Typical deployment:

```text
Linux host

┌──────────────────────┐
│ application          │
│                      │
│ normal SQLite client │
└──────────┬───────────┘
           │
           ▼
       /data/app.db
           ▲
           │
┌──────────┴───────────┐
│ sqlite-sidecar-mcp   │
└──────────┬───────────┘
           │
           │ MCP
           ▼
      reverse proxy
           │
           │ HTTPS
           ▼
       MCP client
```

For Docker, both the application and sidecar mount the same local volume.

SQLite files must remain on a local filesystem.

Network-mounted SQLite databases through NFS/SMB are outside the supported deployment model.

---

# 5. One database per deployment

One sidecar manages exactly one database.

```text
SQLITE_SIDECAR_DB=/data/app.db
```

There is no database registry or alias system in MVP.

If an application has multiple databases, run multiple sidecars when remote access to each is required.

This keeps configuration, permissions and security boundaries simple.

---

# 6. Permissions

Permissions are configured per deployment.

Supported MVP permissions:

```text
schema
read
write
backup
diagnostics
danger-raw-write
```

Example:

```text
SQLITE_SIDECAR_PERMISSIONS=schema,read,write,backup
```

Default:

```text
schema,read
```

Permissions control which MCP tools are exposed.

```text
schema,read
```

exposes:

```text
schema
query
```

while:

```text
schema,read,write,backup
```

exposes:

```text
schema
query
insert
update
delete
backup
```

And:

```text
schema,read,write,backup,danger-raw-write
```

additionally exposes:

```text
execute_write_sql
```

The deliberately alarming permission name is intentional.

An operator should immediately understand that enabling it bypasses the normal agent-safe structured-write API.

---

# 7. Why deployment-level permissions

MVP has one authentication identity per deployment.

There is therefore no reason to build:

```text
users
roles
groups
token ACLs
permission database
```

Different trust levels can use separate deployments.

Example:

```text
sqlite-sidecar-readonly
    token=A
    permissions=schema,read

sqlite-sidecar-agent
    token=B
    permissions=schema,read,write,backup

sqlite-sidecar-admin
    token=C
    permissions=schema,read,write,backup,danger-raw-write
```

This keeps the trust boundary visible in deployment configuration.

---

# 8. Authentication

The sidecar requires one high-entropy bearer token.

```text
SQLITE_SIDECAR_TOKEN=<secret>
```

Requests use:

```text
Authorization: Bearer <secret>
```

The token is:

- supplied by deployment infrastructure;
- never generated remotely;
- never persisted by the sidecar;
- never returned after startup;
- never logged.

If the token is missing, startup fails.

---

# 9. Network security

The sidecar does not implement certificate management.

Expected production setup:

```text
MCP client
    │ HTTPS
    ▼
Caddy / nginx / Traefik / ingress
    │ private HTTP
    ▼
sqlite-sidecar-mcp
```

The sidecar should not be directly exposed to an untrusted network over plaintext HTTP.

CORS is disabled.

Host validation uses ASP.NET Core host filtering.

Browser access is not a product goal.

---

# 10. MCP tools

MVP tools:

```text
schema
query

insert
update
delete

backup

diagnostics

execute_write_sql   // danger-raw-write only
```

The critical distinction is:

```text
read
    = arbitrary read SQL

write
    = constrained structured writes

danger-raw-write
    = arbitrary raw INSERT / UPDATE / DELETE
```

Normal write access does not allow caller-supplied modifying SQL.

---

# 11. `schema`

Requires:

```text
schema
```

Returns enough metadata for an agent to understand the database.

At minimum:

```text
tables
views
columns
types
nullability
primary keys
foreign keys
```

Tabular schema information is returned as TOON.

---

# 12. `query`

Requires:

```text
read
```

Executes one read-only SQL statement.

Example:

```json
{
  "sql": "SELECT id, status FROM jobs WHERE status = $status",
  "parameters": {
    "$status": "failed"
  }
}
```

Response:

```text
rows[2]{id,status}:
  41,failed
  52,failed

truncated: false
```

Arbitrary `SELECT` is allowed within the SQLite security sandbox.

The connection used by `query` is always opened read-only, even when the deployment also has write permissions.

---

# 13. TOON results

Tabular results are serialized using a maintained TOON NuGet package.

Flow:

```text
SqliteDataReader
       │
       ▼
bounded result model
       │
       ▼
TOON serializer
       │
       ▼
MCP text content
```

Result size is bounded, so buffering the limited result before serialization is acceptable.

No custom TOON serializer is required unless the chosen package proves unsuitable.

---

# 14. Structured writes

Normal `write` permission exposes:

```text
insert
update
delete
```

The MCP caller does not provide SQL.

The server constructs parameterized SQLite statements.

This is the normal agent-facing write interface.

---

# 15. `insert`

Requires:

```text
write
```

Example:

```json
{
  "table": "jobs",
  "values": {
    "status": "pending",
    "retry": 0
  }
}
```

The server generates parameterized SQL.

Conceptually:

```sql
INSERT INTO "jobs" ("status", "retry")
VALUES ($p0, $p1);
```

MVP inserts one row per call.

Response:

```text
rowsAffected: 1
```

SQLite `RETURNING` support may be used where useful.

---

# 16. `update`

Requires:

```text
write
```

Example:

```json
{
  "table": "jobs",
  "values": {
    "retry": 1
  },
  "where": {
    "column": "id",
    "operator": "eq",
    "value": 41
  },
  "maxRows": 1
}
```

Both are mandatory:

```text
where
maxRows
```

The caller cannot perform an unbounded update through this tool.

---

# 17. `delete`

Requires:

```text
write
```

Example:

```json
{
  "table": "jobs",
  "where": {
    "column": "status",
    "operator": "eq",
    "value": "obsolete"
  },
  "maxRows": 20
}
```

Again:

```text
where
maxRows
```

are mandatory.

There is no structured equivalent of:

```sql
DELETE FROM jobs;
```

---

# 18. Filter model

Structured writes use a deliberately small filter model.

Initial operators:

```text
eq
ne
lt
lte
gt
gte
is-null
is-not-null
```

Logical composition:

```text
and
or
```

Example:

```json
{
  "and": [
    {
      "column": "status",
      "operator": "eq",
      "value": "failed"
    },
    {
      "column": "created_at",
      "operator": "lt",
      "value": "2026-01-01"
    }
  ]
}
```

Table and column identifiers are validated against the SQLite schema.

All values are parameterized.

Do not implement a general SQL expression language.

---

# 19. Bounded writes

The caller must specify:

```text
maxRows
```

for every structured update or delete.

The deployment also defines an absolute limit:

```text
SQLITE_SIDECAR_MAX_WRITE_ROWS=100
```

Effective limit:

```text
min(client maxRows, deployment maxWriteRows)
```

The operation runs inside a short transaction:

```text
BEGIN
   │
   ▼
execute UPDATE/DELETE
   │
   ▼
rowsAffected
   │
   ├── <= limit → COMMIT
   │
   └── > limit  → ROLLBACK
```

This protects against an unexpectedly broad filter.

---

# 20. Write-rate budget

The deployment can also limit cumulative structured writes:

```text
SQLITE_SIDECAR_MAX_WRITE_ROWS_PER_MINUTE=500
```

This protects against an agent issuing many individually valid small destructive operations.

If exceeded:

```text
WriteBudgetExceeded
```

is returned.

A simple in-memory counter/window is sufficient.

---

# 21. `danger-raw-write`

Some operators need normal SQL semantics for writes.

That is explicitly separated from structured agent-safe writes through:

```text
danger-raw-write
```

permission.

When enabled, the sidecar exposes:

```text
execute_write_sql
```

Example:

```json
{
  "sql": "UPDATE jobs SET retry = retry + 1 WHERE status = $status",
  "parameters": {
    "$status": "failed"
  }
}
```

Allowed statement categories:

```text
INSERT
UPDATE
DELETE
```

including normal SQLite features related to those statements, such as:

```text
CTEs
RETURNING
subqueries
expressions
conflict clauses
```

where permitted by the SQLite authorizer.

The permission exists for cases where the structured API is too restrictive.

It is deliberately named `danger-raw-write`, rather than something neutral like `sql-write`, because enabling it removes important agent-proof protections.

---

# 22. Raw-write safety semantics

`danger-raw-write` bypasses:

```text
structured filter model
mandatory structured WHERE
caller maxRows requirement
server-generated SQL
```

It therefore allows valid SQL such as:

```sql
DELETE FROM jobs;
```

unless additional deployment policies reject it.

This is intentional.

An operator enabling `danger-raw-write` is explicitly choosing standard raw SQLite DML semantics.

However, it does **not** bypass the fundamental SQLite sandbox.

---

# 23. Raw-write hard boundaries

Even with `danger-raw-write`, always reject:

```text
ATTACH
DETACH

CREATE
DROP
ALTER

VACUUM INTO

load_extension

dangerous PRAGMAs

transaction-control statements
```

The permission allows arbitrary data manipulation, not arbitrary SQLite administration or filesystem access.

In other words:

```text
danger-raw-write
    ≠ unrestricted SQLite
```

It means:

```text
raw INSERT / UPDATE / DELETE
inside the sidecar security sandbox
```

---

# 24. Raw-write result

Normal write:

```sql
UPDATE jobs
SET retry = retry + 1
WHERE status = 'failed';
```

returns:

```text
rowsAffected: 17
```

A statement with `RETURNING`:

```sql
UPDATE jobs
SET retry = retry + 1
WHERE id = $id
RETURNING id,retry;
```

returns TOON:

```text
rows[1]{id,retry}:
  41,2
```

---

# 25. Raw-write limits

Raw writes still inherit:

```text
SQL size limit
query timeout
SQLite runtime limits
busy timeout
write serialization
authentication
deployment permissions
SQLite authorizer
```

The structured `maxRows` safety guarantee does not apply.

The write-rate budget should be documented as applying to structured writes only unless a reliable way of accounting for affected raw rows is implemented.

If raw-write row accounting is trivial using SQLite's affected-row count, it may also count toward the global write budget.

Prefer doing so if implementation remains simple.

---

# 26. No remote transaction sessions

MVP does not support:

```text
BEGIN
...
COMMIT
```

across MCP calls.

Each operation is self-contained.

Structured update/delete use internal short transactions solely for row-limit enforcement.

Raw transaction-control SQL is rejected.

---

# 27. One SQL statement per raw request

`query` and `execute_write_sql` accept exactly one SQLite statement.

Allowed:

```sql
UPDATE jobs SET retry = 1 WHERE id = 41
```

Rejected:

```sql
UPDATE jobs SET retry = 1;
DELETE FROM logs;
```

This simplifies authorization, auditing, cancellation and error handling.

---

# 28. SQLite security baseline

Every externally influenced SQLite operation uses:

```text
SQLITE_DBCONFIG_DEFENSIVE = 1
SQLITE_DBCONFIG_TRUSTED_SCHEMA = 0

sqlite3_set_authorizer(...)
sqlite3_limit(...)

query timeout/cancellation
```

These are part of the baseline and are not optional feature flags.

---

# 29. SQLite authorizer

Use SQLite's native authorizer rather than regex/string inspection.

For `query`:

```text
reads                 allowed
writes                rejected
DDL                   rejected
ATTACH/DETACH         rejected
dangerous PRAGMAs     rejected
extension loading     rejected
```

For structured writes:

```text
required DML          allowed
necessary reads       allowed
unrelated operations  rejected
```

For `execute_write_sql`:

```text
INSERT/UPDATE/DELETE   allowed
necessary reads       allowed

DDL                    rejected
ATTACH/DETACH          rejected
dangerous PRAGMAs      rejected
extension loading      rejected
transaction control    rejected
```

---

# 30. SQLite runtime limits

Use `sqlite3_limit()` to constrain untrusted SQL.

At minimum:

```text
SQL length
column count
expression depth
compound SELECT count
host parameter count
LIKE pattern length
VDBE operation count
attached databases
```

Attached database limit:

```text
0
```

---

# 31. Extension loading

SQLite extension loading is never enabled.

There is no deployment permission that enables it in MVP.

---

# 32. Query limits

Suggested defaults:

```text
max SQL size:             32 KB
query timeout:            10 seconds
max returned rows:        1,000
max result size:          4 MB
max concurrent requests:  4
busy timeout:             3 seconds
```

Client SQL does not override server-side limits.

---

# 33. Query cancellation

Request timeout and cancellation must actually interrupt SQLite execution.

Conceptually:

```text
CancellationToken
       │
       ▼
SQLite progress handler / interrupt
       │
       ▼
stop execution
```

No query planner or cost estimator is needed.

---

# 34. Result limits

The server independently counts:

```text
rows
serialized bytes
```

When the row limit is reached:

```text
truncated: true
```

is returned.

Unlimited result streaming into an agent context is not allowed.

---

# 35. Concurrency

Use one global request semaphore.

Default:

```text
4
```

Use one write semaphore:

```text
1
```

Both structured writes and `danger-raw-write` pass through the same write semaphore.

Use one backup semaphore:

```text
1
```

No scheduler framework is needed.

---

# 36. SQLite busy behavior

The owning application may hold a write lock.

The sidecar must expect:

```text
SQLITE_BUSY
```

Use a finite busy timeout.

Return:

```text
DatabaseBusy
```

instead of waiting indefinitely.

---

# 37. Backups

Requires:

```text
backup
```

Backups use SQLite's Online Backup API.

Destination:

```text
SQLITE_SIDECAR_BACKUP_DIR=/backups
```

Remote callers cannot supply arbitrary paths.

---

# 38. `backup`

Example:

```json
{
  "name": "before-cleanup"
}
```

Resulting file:

```text
app-20260926T120315Z-before-cleanup.db
```

Response:

```text
name: app-20260926T120315Z-before-cleanup.db
sizeBytes: 1835008
```

There is no restore MCP tool in MVP.

---

# 39. Backup-before-delete

Optional deployment policy:

```text
SQLITE_SIDECAR_BACKUP_BEFORE_DELETE=true
```

For structured delete:

```text
delete request
      │
      ▼
backup
      │
      ├── failure → reject delete
      ▼
bounded delete
```

Whether this policy should also apply to raw `DELETE` executed through `danger-raw-write` should be configurable or, preferably, automatically applied if implementation remains straightforward.

---

# 40. Diagnostics

Requires:

```text
diagnostics
```

MVP diagnostics may include:

```text
SQLite version
journal mode
page size
page count
quick_check
```

Do not turn diagnostics into a broad administration interface.

---

# 41. Logging and auditability

Use structured ASP.NET Core logs.

Reads:

```text
requestId
operation
duration
rowsReturned
resultBytes
outcome
```

Structured writes:

```text
requestId
operation
table
rowsAffected
duration
outcome
```

Raw writes:

```text
requestId
operation=execute_write_sql
sqlHash
rowsAffected
duration
outcome
```

Backups:

```text
requestId
backupName
size
duration
outcome
```

Never log:

```text
bearer tokens
returned database contents
parameter values
```

Raw SQL text is disabled by default.

---

# 42. Agent-specific threat model

The sidecar protects against both unauthorized clients and authorized but mistaken agents.

Typical agent mistakes:

```text
wrong table
wrong WHERE
unexpectedly broad update
unexpectedly broad delete
repeated destructive operations
expensive recursive query
huge result request
attempted schema changes
```

The normal `write` capability mitigates these through:

```text
structured writes
mandatory WHERE
mandatory maxRows
deployment maxWriteRows
write-rate budget
no DDL
no ATTACH
no extensions
```

`danger-raw-write` intentionally weakens the first four protections.

That distinction must be obvious in documentation.

---

# 43. Permission risk levels

The README should communicate permissions approximately like this:

```text
schema
    low risk
    metadata only

read
    sensitive
    can read database contents

write
    higher risk
    constrained data modifications

backup
    sensitive
    can create database copies

diagnostics
    low/moderate risk
    database metadata and checks

danger-raw-write
    high risk
    arbitrary INSERT/UPDATE/DELETE SQL
```

This is descriptive documentation, not a runtime ranking system.

---

# 44. Security guarantees

With default configuration, concrete guarantees include:

- remote access requires authentication;
- default permissions are read-only;
- read does not imply write;
- normal write does not allow caller-supplied write SQL;
- structured UPDATE and DELETE require a predicate;
- structured UPDATE and DELETE have hard row limits;
- repeated structured writes are rate-limited;
- raw writes require an explicitly dangerous permission;
- raw writes still cannot perform DDL, ATTACH or native extension loading;
- arbitrary backup filesystem paths cannot be supplied;
- queries have time/result/concurrency limits;
- SQLite native defensive mechanisms are enabled;
- database files themselves are never served over the network.

---

# 45. Security non-guarantees

If:

```text
danger-raw-write
```

is enabled, a holder of the deployment token can intentionally run statements such as:

```sql
DELETE FROM jobs;
```

That is expected behavior.

The permission deliberately bypasses structured write safeguards.

A stolen deployment token receives the capabilities configured for that deployment.

The sidecar limits capability and blast radius but cannot make intentionally granted destructive permissions harmless.

---

# 46. Container security

Official container should:

```text
run as non-root
drop all Linux capabilities
use no-new-privileges
use a minimal image
contain no development toolchain
```

Recommended runtime:

```text
--cap-drop=ALL
--security-opt=no-new-privileges
```

Ideally:

```text
root filesystem     read-only
/data               database access
/backups            writable only when backup enabled
```

---

# 47. Database connection privileges

For `schema` and `query`:

```text
SQLite connection opened ReadOnly
PRAGMA query_only=ON
```

For structured writes and raw writes:

```text
SQLite connection opened ReadWrite
```

Write connections are created only for write operations.

The sidecar never changes application-level database settings such as:

```text
journal_mode
synchronous
locking_mode
checkpoint configuration
```

---

# 48. Configuration

Required:

```text
SQLITE_SIDECAR_DB=/data/app.db
SQLITE_SIDECAR_TOKEN=<secret>
```

Default:

```text
SQLITE_SIDECAR_PERMISSIONS=schema,read
```

Normal agent deployment:

```text
SQLITE_SIDECAR_PERMISSIONS=schema,read,write,backup
```

Privileged deployment:

```text
SQLITE_SIDECAR_PERMISSIONS=schema,read,write,backup,diagnostics,danger-raw-write
```

Optional:

```text
SQLITE_SIDECAR_BACKUP_DIR=/backups

SQLITE_SIDECAR_MAX_ROWS=1000
SQLITE_SIDECAR_MAX_RESULT_BYTES=4194304
SQLITE_SIDECAR_MAX_SQL_BYTES=32768

SQLITE_SIDECAR_QUERY_TIMEOUT_SECONDS=10
SQLITE_SIDECAR_BUSY_TIMEOUT_SECONDS=3

SQLITE_SIDECAR_MAX_CONCURRENCY=4

SQLITE_SIDECAR_MAX_WRITE_ROWS=100
SQLITE_SIDECAR_MAX_WRITE_ROWS_PER_MINUTE=500

SQLITE_SIDECAR_BACKUP_BEFORE_DELETE=false
```

---

# 49. Project structure

One production project:

```text
sqlite-sidecar-mcp/
│
├── SqliteSidecarMcp.csproj
├── Program.cs
│
├── Configuration/
├── Database/
├── Mcp/
├── Security/
│
├── Dockerfile
├── README.md
├── SECURITY.md
├── LICENSE
└── tests/
```

Folders organize source code only.

They are not separate libraries.

---

# 50. Suggested source organization

```text
Configuration/
    SidecarOptions.cs

Database/
    SqliteService.cs
    SqliteSecurity.cs
    StructuredWriteBuilder.cs
    BackupService.cs

Mcp/
    SqliteTools.cs

Security/
    TokenAuthentication.cs
    WriteBudget.cs
```

Avoid interfaces where there is only one implementation.

---

# 51. Dependencies

Approximately:

```xml
<ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" />
    <PackageReference Include="Toon.DotNet" />
</ItemGroup>
```

Add dependencies only when they meaningfully reduce code.

---

# 52. SQLite packaging

Use the normal SQLite native dependency supplied by the selected `Microsoft.Data.Sqlite` configuration.

Do not initially implement:

```text
custom SQLite builds
SQLite version switching
runtime native downloads
provider selection
```

---

# 53. NativeAOT

Test NativeAOT from the beginning:

```xml
<PublishAot>true</PublishAot>
```

Prefer:

```text
ASP.NET Core Minimal API
CreateSlimBuilder
explicit registrations
source-generated JSON where required
```

But NativeAOT must not significantly distort the architecture.

If required dependencies make it disproportionately difficult, ship a self-contained .NET 10 Linux image first.

---

# 54. Docker deployment

Example:

```yaml
services:

  app:
    image: my-app
    volumes:
      - sqlite-data:/data

  sqlite-sidecar:
    image: ghcr.io/example/sqlite-sidecar-mcp:latest

    environment:
      SQLITE_SIDECAR_DB: /data/app.db
      SQLITE_SIDECAR_TOKEN: ${SQLITE_SIDECAR_TOKEN}

      SQLITE_SIDECAR_PERMISSIONS: schema,read,write,backup

      SQLITE_SIDECAR_BACKUP_DIR: /backups

      SQLITE_SIDECAR_MAX_WRITE_ROWS: 100
      SQLITE_SIDECAR_MAX_WRITE_ROWS_PER_MINUTE: 500

      ASPNETCORE_URLS: http://0.0.0.0:8080

    volumes:
      - sqlite-data:/data
      - sqlite-backups:/backups

volumes:
  sqlite-data:
  sqlite-backups:
```

For raw write administration:

```text
SQLITE_SIDECAR_PERMISSIONS=
schema,read,write,backup,danger-raw-write
```

should be an explicit operator decision.

---

# 55. Health

Expose:

```text
GET /health
```

It reports process health only.

Do not perform database integrity checks or backups from health probes.

---

# 56. Errors

Small error model:

```text
Unauthorized
PermissionDenied

InvalidQuery
QueryRejected
QueryTimedOut
ResultTooLarge

InvalidWrite
WriteLimitExceeded
WriteBudgetExceeded

DatabaseBusy
DatabaseError

BackupFailed
```

Do not expose stack traces, secrets or unnecessary filesystem details remotely.

---

# 57. Required security tests

Explicitly attempt:

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

All must fail remotely, including with `danger-raw-write`.

Structured write tests:

```text
UPDATE without WHERE
DELETE without WHERE

update over maxRows
delete over maxRows

many small writes exceeding write budget
```

Raw write tests:

```text
raw INSERT succeeds with danger-raw-write
raw UPDATE succeeds with danger-raw-write
raw DELETE succeeds with danger-raw-write

raw write rejected without danger-raw-write

raw DROP rejected
raw ATTACH rejected
raw PRAGMA mutation rejected
raw transaction control rejected
```

---

# 58. Functional tests

Required scenarios:

```text
schema discovery
TOON query output

read while app writes

structured insert
structured update
structured delete

rollback after structured maxRows violation

raw INSERT
raw UPDATE
raw DELETE
raw UPDATE RETURNING

backup while app is running

read-only deployment
structured-write deployment
danger-raw-write deployment

WAL database
rollback-journal database

SQLITE_BUSY behavior
query timeout
concurrency limiting
```

Tests should run against the actual published Linux artifact.

---

# 59. Security documentation

`SECURITY.md` should explain:

- deployment assumptions;
- authentication;
- permission meanings;
- token handling;
- reverse proxy requirements;
- structured write guarantees;
- `danger-raw-write` consequences.

The `danger-raw-write` documentation should be explicit:

> `danger-raw-write` permits caller-supplied raw INSERT, UPDATE and DELETE statements. It bypasses structured-write WHERE and row-limit protections. Enable it only for clients trusted with direct data-modification SQL.

---

# 60. Outside MVP

Do not implement yet:

```text
multiple databases per process

multiple users
multiple tokens
RBAC

per-table ACLs
per-column ACLs

OAuth/OIDC
mTLS

REST API
CLI
browser UI

DDL

restore
scheduled backups
backup retention
cloud backup upload

remote transaction sessions

replication
clustering

custom SQLite builds
custom TOON serializer

Prometheus
OpenTelemetry

plugin architecture
generic database abstractions
```

---

# 61. MVP definition of done

An MCP agent can:

```text
inspect schema
run arbitrary read queries
perform bounded structured writes
create safe SQLite backups
run basic diagnostics
```

and, when explicitly permitted:

```text
execute raw INSERT
execute raw UPDATE
execute raw DELETE
```

while the owning application continues operating normally.

The sidecar provides:

```text
TOON results
deployment permissions
strong authentication

structured bounded writes
optional danger-raw-write

SQLite authorizer
SQLite defensive mode
SQLite runtime limits
query cancellation

write-rate protection
safe backup handling

container isolation
```

---

# 62. Final architecture

```text
                         AI / MCP client
                               │
                               │ HTTPS
                               ▼
                          reverse proxy
                               │
                               ▼
┌────────────────────────────────────────────────────────┐
│ sqlite-sidecar-mcp                                     │
│                                                        │
│ bearer token                                           │
│      │                                                 │
│ deployment permissions                                 │
│      │                                                 │
│ MCP                                                    │
│ ├── schema                                             │
│ ├── query                    [read]                    │
│ │                                                      │
│ ├── insert                   [write]                   │
│ ├── update                   [write]                   │
│ ├── delete                   [write]                   │
│ │                                                      │
│ ├── execute_write_sql        [danger-raw-write]        │
│ │                                                      │
│ ├── backup                   [backup]                  │
│ └── diagnostics              [diagnostics]             │
│      │                                                 │
│ Agent protections                                      │
│ ├── structured writes                                  │
│ ├── mandatory WHERE                                    │
│ ├── mandatory maxRows                                  │
│ ├── server write limit                                 │
│ ├── write-rate budget                                  │
│ └── optional backup-before-delete                      │
│      │                                                 │
│ SQLite sandbox                                         │
│ ├── authorizer                                         │
│ ├── defensive mode                                     │
│ ├── trusted_schema off                                 │
│ ├── runtime limits                                     │
│ ├── timeout / interrupt                                │
│ ├── no ATTACH                                          │
│ ├── no extensions                                      │
│ └── no DDL                                             │
│      │                                                 │
│ Microsoft.Data.Sqlite                                  │
└───────────────────────┬────────────────────────────────┘
                        │
                        ▼
                      app.db
                        ▲
                        │
                 existing application
```

---

# 63. Product statement

> **sqlite-sidecar-mcp is an agent-safe MCP sidecar for securely querying, modifying and backing up a live SQLite database without changing the application that owns it.**

The important capability split is:

```text
read
    → arbitrary SELECT

write
    → structured, bounded writes

danger-raw-write
    → caller-supplied INSERT / UPDATE / DELETE
```

This keeps the default write path suitable for agents while retaining an explicit escape hatch for operators and advanced clients that genuinely need normal SQLite DML.