# Configuration and startup validation

All configuration comes from environment variables with the `SQLITE_SIDECAR_` prefix. One process
binds one `SidecarOptions` instance and treats it as immutable. There is no reconfiguration at
runtime, because a permission change must be a visible deployment change.

Code: `src/Xakpc.SQLiteMCPSidecar/Configuration/SidecarOptions.cs`,
`src/Xakpc.SQLiteMCPSidecar/Configuration/SidecarStartup.cs`.

## Required

```text
SQLITE_SIDECAR_DB=/data/app.db
SQLITE_SIDECAR_TOKEN=<secret>
```

Startup fails when one of these values is absent. The sidecar does not start in a degraded state
and it does not fall back to read-only, because a silent fallback hides a deployment mistake.

## Optional

```text
SQLITE_SIDECAR_PERMISSIONS=schema,read     # the default
SQLITE_SIDECAR_BACKUP_DIR=/backups

SQLITE_SIDECAR_MAX_ROWS=1000
SQLITE_SIDECAR_MAX_RESULT_BYTES=4194304
SQLITE_SIDECAR_MAX_SQL_BYTES=32768

SQLITE_SIDECAR_QUERY_TIMEOUT_SECONDS=10
SQLITE_SIDECAR_BUSY_TIMEOUT_SECONDS=3

SQLITE_SIDECAR_MAX_CONCURRENCY=4
SQLITE_SIDECAR_MAX_REQUESTS_PER_MINUTE=120

SQLITE_SIDECAR_MAX_WRITE_ROWS=100
SQLITE_SIDECAR_MAX_WRITE_ROWS_PER_MINUTE=500
```

`MAX_CONCURRENCY`, `BUSY_TIMEOUT_SECONDS` and `MAX_REQUESTS_PER_MINUTE` are active. The other limits
are bound and validated, and the code that applies them arrives with the write tools.

`MAX_REQUESTS_PER_MINUTE` is the whole request budget of the process. It needs one number only,
because the window is a constant minute. See
[../security/public-endpoint.md](../security/public-endpoint.md).

There is no backup timeout variable and no idempotency variable. Those values are constants.

## Why the read is explicit and not a binder call

`SidecarOptions.Load(IConfiguration)` reads each key by name.

```csharp
builder.Configuration.AddEnvironmentVariables("SQLITE_SIDECAR_");
```

The provider removes the prefix and keeps a single underscore, thus the key is `MAX_ROWS` and the
configuration binder never matches it to the `MaxRows` property. `PERMISSIONS=schema,read` also
needs a conversion to `PermissionSet`. The binder therefore needs
`[ConfigurationKeyName]` on each property and a type converter, and the validation stays bespoke in
any case. An explicit read is less machinery for the same result, and its message can name the
variable that the operator set:

```text
SQLITE_SIDECAR_MAX_CONCURRENCY is 0. It must be between 1 and 1024.
```

**Lesson.** The read is deferred to the first resolve of the singleton and it does not run on
`builder.Configuration` directly.

```csharp
builder.Services.AddSingleton(services => SidecarOptions.Load(services.GetRequiredService<IConfiguration>()));
```

A read on `builder.Configuration` happens before `builder.Build()`, thus a configuration source
that the host adds later is invisible. The test host adds its source that way. The first resolve
happens in `SidecarStartup` before the first request, thus startup still fails early. See
[../testing/e2e-harness.md](../testing/e2e-harness.md).

`IOptions<T>` is not used. One instance and constructor injection agree with
[../practices.md](../practices.md), and no code needs the value during service registration.

## Validation

```mermaid
flowchart TD
    load[SidecarOptions.Load] --> req{DB and TOKEN present?}
    req -->|no| collect[Collect the problem]
    req -->|yes| file{DB file exists?}
    file -->|no| collect
    file -->|yes| perms{Permission names known?}
    perms -->|no| collect
    perms -->|yes| floor{Read floor satisfied?}
    floor -->|no| collect
    floor -->|yes| bk{backup permission on?}
    bk -->|yes| dir{BACKUP_DIR writable?}
    dir -->|no| collect
    bk -->|no| num[Check each numeric limit]
    dir -->|yes| num
    num --> any{Any problem?}
    collect --> num
    any -->|yes| fail[SidecarConfigurationException with every problem]
    any -->|no| start[SidecarStartup]
    start --> jm[Read journal_mode]
    jm --> warn{WAL, or no write permission?}
    warn -->|no| log[Log the warning, then serve]
    warn -->|yes| sweep[Delete stale *.db.partial files]
    log --> sweep
    sweep --> ok[Serve]
```

**Invariant.** `SidecarConfigurationException` reports **every** problem at one time, thus an
operator repairs one deployment and not one variable.

The database file must exist. The sidecar never creates it: creation hides a wrong path and makes
an empty database next to the correct one.

## How a startup failure reaches the operator

`Program.cs` catches `SidecarConfigurationException` around the `SidecarStartup.RunAsync` call, logs
the problem list at `Critical`, and returns exit code 1. The catch covers the configuration problems
as well, because the `SidecarOptions` singleton resolves inside that call.

```text
Critical: The sidecar cannot start. The sidecar configuration is not valid:
  - SQLITE_SIDECAR_DB is '/data/notadb.db' and SQLite cannot read it: SQLite Error 26: 'file is
    not a database'. Check that the path holds a SQLite database, that this process may read it,
    and that the directory is writable, which a WAL database needs even for a read.
```

Three rules hold this together:

- **A SQLite failure of the startup check becomes a configuration problem.**
  `SidecarStartup.ReadJournalModeOrFailAsync` catches `SqliteException` and carries the SQLite
  message into the problem list with the path. `SidecarOptions.Load` proves that the path exists and
  it can prove no more: the content of the file, the read permission of this process and the lock
  support of the filesystem are all invisible to it.
- **`await app.DisposeAsync()` runs before the return, and it is load-bearing.** `AddJsonConsole`
  writes from a background queue. Without the dispose the process can end before the queue drains,
  and the operator gets an exit code and **no message**. This was measured: one build printed the
  list for an absent token and printed nothing for a file that is not a database.
- **Only `SidecarConfigurationException` is caught.** Any other exception is a defect of this
  product and it keeps its stack trace. This is the same rule that `ArgumentBindingFilter` follows
  with `JsonException`. See [../mcp/error-model.md](../mcp/error-model.md).

`StartupTests.ADatabasePathThatIsNotADatabaseFailsAsAConfigurationProblem` pins the translation.

The writable test of the backup directory is a probe file. A permission bit check is not portable.

## Journal mode warning

`SidecarStartup` reads `journal_mode` one time. It never writes the value: the owning application
owns that setting.

A database that is not in WAL mode blocks all readers of the owning application during a sidecar
write. The sidecar therefore logs a prominent warning when the database is not in WAL mode **and**
the deployment has `write` or `danger-raw-write`. It still serves. The journal mode is a property
of the database of another application, not a mistake in the sidecar configuration, and a refusal
to start would make the sidecar unusable with many correct deployments.

## ASP.NET Core settings

```text
ASPNETCORE_URLS=http://0.0.0.0:8080
```

`appsettings.json` sets **no** `AllowedHosts` value. `CreateSlimBuilder` adds no host-filtering
middleware, thus the key would read as a control that does not run. The reverse proxy is the host
gate. A comment in the file records this, because the removal is the surprising part. See
[../security/public-endpoint.md](../security/public-endpoint.md).

## Related

- [../security/permissions.md](../security/permissions.md) — the read floor
- [../security/authentication.md](../security/authentication.md) — the token
- [../database/connections.md](../database/connections.md)
- [../deployment/container.md](../deployment/container.md)
