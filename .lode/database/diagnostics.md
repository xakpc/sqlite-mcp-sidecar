# Diagnostics

The `diagnostics` tool reports a small fixed set of database health values. It requires the
`diagnostics` permission and it takes no arguments.

Code: `SqliteService.ReadDiagnosticsAsync`, the `diagnostics` tool in
`src/Xakpc.SQLiteMCPSidecar/Mcp/SqliteTools.cs`.

## The value set

```text
sqliteVersion: 3.53.4
journalMode: wal
pageSize: 4096
pageCount: 8
quickCheck: ok
```

| Value | Statement | Why it is here |
| --- | --- | --- |
| `sqliteVersion` | `SELECT sqlite_version()` | The library of the **sidecar**, not of the owning application. |
| `journalMode` | `PRAGMA journal_mode` | The most important value. See below. |
| `pageSize` | `PRAGMA page_size` | With `pageCount`, the size of the database. |
| `pageCount` | `PRAGMA page_count` | `pageSize * pageCount` is the byte size. |
| `quickCheck` | `PRAGMA quick_check` | `ok` means no structural damage was found. |

**Invariant. The value set is fixed and every statement is server-authored.** Never accept a
caller-supplied `PRAGMA` name. A caller-chosen pragma is a write primitive and an information leak,
and it would turn this tool into a general administration interface.
`DiagnosticsToolTests.DiagnosticsTakesNoArguments` asserts that the JSON schema has no property at
all, which is the machine-checkable form of that rule.

## Why the journal mode matters most

A database that is not in WAL mode blocks the readers of the owning application during a sidecar
write. The sidecar reads this value at startup too and warns when a write permission meets a non-WAL
database, but an agent and an operator also need it on demand. The sidecar never writes the value:
the owning application owns it. See [connections.md](connections.md).

## `quick_check`, never `integrity_check`

`quick_check` does not verify index content, thus it does not stall the owning application.

It still reads every page, so the read runs under `QUERY_TIMEOUT_SECONDS` with
`SqliteSecurity.RegisterInterrupt`. A database too large for the check at the configured timeout
returns `QueryTimedOut` rather than hanging the tool. That is the honest outcome: the alternative is
a tool that an operator cannot use and cannot diagnose.

## Connection

One read-only connection with the baseline applied, `PRAGMA query_only=ON`, and **no authorizer**.
Every authorizer policy denies `PRAGMA`, and these statements are the server's own, thus this is the
same shape as `ReadJournalModeAsync` and `ReadSchemaDdlAsync`. See
[sqlite-sandbox.md](sqlite-sandbox.md).

The tool takes a request slot, unlike `backup`. The read is short and bounded, thus it belongs under
the concurrency limit like any other read.

## Not the health endpoint

`GET /health` is separate. It reports process liveness, touches no database and needs no token. A
probe runs every few seconds, and an expensive probe becomes a denial-of-service vector against the
owning application. See [../security/authentication.md](../security/authentication.md).

## Related

- [backups.md](backups.md) — the other Phase 5 tool
- [connections.md](connections.md) — the read-only connection and the journal-mode rule
- [../mcp/tool-catalog.md](../mcp/tool-catalog.md)
- [../mcp/error-model.md](../mcp/error-model.md)
