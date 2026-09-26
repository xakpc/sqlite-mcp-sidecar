# Lode map

Index of every lode file. Read this file first in a new session, together with
[summary.md](summary.md) and [terminology.md](terminology.md).

## Structure

```mermaid
flowchart TD
    root[.lode] --> s[summary.md]
    root --> t[terminology.md]
    root --> p[practices.md]
    root --> m[lode-map.md]
    root --> plans[plans/]
    root --> tmp[tmp/ — git-ignored]
    plans --> rm[mvp-roadmap.md]
    plans --> oq[open-questions.md]
    plans --> oos[out-of-scope.md]
    plans --> d[design/]
```

The project has no code beyond the template, so there is no domain directory yet. The whole
design sits in `plans/design/`. When a phase lands, move its design file into a domain
directory such as `security/`, `database/`, `mcp/` or `ops/`, rewrite it as current state, and
update this map.

## Root

| File | Contents |
| --- | --- |
| [summary.md](summary.md) | What the product is, the capability split, implementation status, repository layout. |
| [terminology.md](terminology.md) | Domain language. Use these exact words in code and logs. |
| [practices.md](practices.md) | Active rules: design limits, code organization, security and logging practices, MCP SDK 2.x notes, dependency table, NativeAOT stance. |
| [lode-map.md](lode-map.md) | This index. |

## plans/

| File | Contents |
| --- | --- |
| [plans/mvp-roadmap.md](plans/mvp-roadmap.md) | Nine phases from template to MVP, the required security and functional test lists, the definition of done. |
| [plans/open-questions.md](plans/open-questions.md) | Seven unresolved decisions with recommendations: NativeAOT viability, raw-write budget accounting, backup-before-delete for raw deletes, connection pooling, byte-limit behavior, schema output shape, base image. |
| [plans/out-of-scope.md](plans/out-of-scope.md) | Excluded features and the reason for each exclusion. |

## plans/design/

Target design, one topic per file. No code implements any of it yet.

| File | Contents |
| --- | --- |
| [security-model.md](plans/design/security-model.md) | The layered security model, the guarantees list, the non-guarantees list. Start here. |
| [permission-model.md](plans/design/permission-model.md) | The six permissions, permission-to-tool mapping, why deployment-level, documented risk levels. |
| [authentication-and-network.md](plans/design/authentication-and-network.md) | Bearer token contract, constant-time comparison, reverse proxy model, CORS off, health endpoint. |
| [sqlite-sandbox.md](plans/design/sqlite-sandbox.md) | The always-on SQLite controls, the verified native API surface, authorizer policy per tool, runtime limits, hard boundaries, cancellation. |
| [threat-model.md](plans/design/threat-model.md) | Agent mistakes, mitigation map, attacker goals and blocks, logging and errors as leak surfaces, out-of-scope threats. |
| [connection-policy.md](plans/design/connection-policy.md) | Read-only and read-write connections, lifecycle, pooling off, settings never changed, busy behavior, the three semaphores. |
| [structured-writes.md](plans/design/structured-writes.md) | `insert`, `update`, `delete`, the filter model, bounded writes and the row-limit rollback, write-rate budget, backup before delete. |
| [raw-writes.md](plans/design/raw-writes.md) | `execute_write_sql`, what the permission bypasses and what it does not, results, remaining limits, no remote transactions. |
| [backups.md](plans/design/backups.md) | Online Backup API, path safety, the backup semaphore, and the `diagnostics` tool. |
| [mcp-tool-catalog.md](plans/design/mcp-tool-catalog.md) | The eight tools, server registration, tool shape, permission gating, `schema` and `query`. |
| [toon-results.md](plans/design/toon-results.md) | The result pipeline, the `Toon.DotNet` serializer, output format, row and byte limits, truncation. |
| [error-model.md](plans/design/error-model.md) | The twelve error codes, a selection flowchart, disclosure rules. |
| [configuration.md](plans/design/configuration.md) | Every `SQLITE_SIDECAR_` variable, startup validation, the options shape. |
| [container-and-deployment.md](plans/design/container-and-deployment.md) | Deployment shape, compose example, image hardening, current Dockerfile gaps, build output. |

## tmp/

Session scraps. `.gitignore` ignores `.lode/tmp/`. Nothing here is permanent. Session handover
documents go here.

## Reading paths

| Task | Read |
| --- | --- |
| Start a session | `lode-map.md`, `terminology.md`, `summary.md` |
| Start implementation | `plans/mvp-roadmap.md`, `practices.md` |
| Touch SQLite access | `plans/design/sqlite-sandbox.md`, `plans/design/connection-policy.md` |
| Add or change a tool | `plans/design/mcp-tool-catalog.md`, `plans/design/permission-model.md` |
| Touch a write path | `plans/design/structured-writes.md`, `plans/design/raw-writes.md` |
| Write documentation | `plans/design/security-model.md`, `plans/design/permission-model.md`, `plans/design/raw-writes.md` |
| Consider a new feature | `plans/out-of-scope.md` |
