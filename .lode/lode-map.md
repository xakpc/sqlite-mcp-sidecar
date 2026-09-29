# Lode map

Index of each lode file. Read this file first in a new session, together with
[summary.md](summary.md) and [terminology.md](terminology.md).

## Structure

```mermaid
flowchart TD
    root[.lode] --> s[summary.md]
    root --> t[terminology.md]
    root --> p[practices.md]
    root --> m[lode-map.md]
    root --> cfg[configuration/]
    root --> sec[security/ — summary, model, threat-model, authentication, permissions,
                  public-endpoint, write-controls]
    root --> dep[deployment/ — summary, container, platforms, distribution]
    root --> dbd[database/]
    root --> mcpd[mcp/]
    root --> tst[testing/ — e2e-harness, live-database-suite, bad-agent-suite]
    root --> dec[decisions/]
    root --> plans[plans/]
    root --> tmp[tmp/ — git-ignored]
    dbd --> dbs[connections.md, sqlite-sandbox.md, statement-check.md, structured-writes.md,
                raw-writes.md, backups.md, diagnostics.md]
    mcpd --> mcps[tool-catalog.md, query-results.md, error-model.md, schema-output.md,
                   write-tool-arguments.md]
    plans --> rm[mvp-roadmap.md]
    plans --> rt[required-tests.md]
    plans --> oq[open-questions.md]
    plans --> oos[out-of-scope.md]
```

A domain directory holds current state. `plans/` holds what no code has landed yet. A file moves out
of `plans/` when code implements it, and it is rewritten as current state. `plans/design/` is gone:
every file it held is current state now, under `deployment/` and `security/`.

## Root

| File | Contents |
| --- | --- |
| [summary.md](summary.md) | What the product is, the implementation status table, the repository layout, the capability split, the eight tools, how to run it. |
| [terminology.md](terminology.md) | Domain language. Use these exact words in code and logs. |
| [practices.md](practices.md) | Active rules: design limits, code organization, security and logging practices, MCP SDK notes, the dependency table, testing rules, the NativeAOT stance. |
| [lode-map.md](lode-map.md) | This index. |

## configuration/

| File | Contents |
| --- | --- |
| [configuration/options.md](configuration/options.md) | Every `SQLITE_SIDECAR_` variable, why the read is explicit and deferred, the validation flow, the journal mode warning, the ASP.NET Core settings. |

## security/

| File | Contents |
| --- | --- |
| [security/summary.md](security/summary.md) | The layers that have code today. |
| [security/model.md](security/model.md) | The layered security model, the guarantees list, the non-guarantees list. **`README.md` and `SECURITY.md` copy from here.** Start here. |
| [security/threat-model.md](security/threat-model.md) | Agent mistakes, the mitigation map, the mistake that the MVP does not mitigate, attacker goals and blocks, logging and errors as leak surfaces. |
| [security/authentication.md](security/authentication.md) | The deployment token, the constant-time comparison, the permission claims, the identical 401, the health endpoint, the network model. |
| [security/public-endpoint.md](security/public-endpoint.md) | The two paths and the path contract, the request budget, the middleware order, the absent host filtering, the forwarded address in a log. |
| [security/permissions.md](security/permissions.md) | The six permissions, the read floor, the claim and policy mechanism, the two gating layers, the invariants, the risk table. |
| [security/write-controls.md](security/write-controls.md) | The write budget, the idempotency cache, the order of the controls, the bounds, the process-state caveat. |

## database/

| File | Contents |
| --- | --- |
| [database/connections.md](database/connections.md) | The read-only connection, the write connection and `ForeignKeys`, `query_only`, pooling off, where the sandbox applies, the request and write semaphores, the filesystem constraints. |
| [database/sqlite-sandbox.md](database/sqlite-sandbox.md) | The always-on SQLite controls, the verified native API, the three allowlist policies, runtime limits, hard boundaries, cancellation. |
| [database/statement-check.md](database/statement-check.md) | `ValidateSingleStatement`: the `StatementCheck` values, why an empty statement is not a null wrapper, and how a bare `SELECT` is kept off the write path. |
| [database/structured-writes.md](database/structured-writes.md) | Why the server builds the statement, the three tools, the flat filter model, the order of an insert and of a mutation, the two row-limit checks, identifier validation, the value table, the result shapes. |
| [database/raw-writes.md](database/raw-writes.md) | `execute_write_sql`: the two arguments and why there is no parameter map, what the permission bypasses and what it does not, the DML policy and the `sqlite_%` rule, the step order, the `RETURNING` drain, the result, the budget, the `sqlHash` log. |
| [database/backups.md](database/backups.md) | The task-mode call, the step loop and why it is not `BackupDatabase`, restarts and the restart cap with measured numbers, partial files, path safety, the backup slot. |
| [database/diagnostics.md](database/diagnostics.md) | The fixed value set, why the journal mode matters most, `quick_check` over `integrity_check`, the connection. |

## mcp/

| File | Contents |
| --- | --- |
| [mcp/tool-catalog.md](mcp/tool-catalog.md) | The eight tools, server registration, the one task-mode tool and the execution mode selector, the stateless transport and the removed handshake, tool shape. |
| [mcp/query-results.md](mcp/query-results.md) | The result pipeline, the `Toon.DotNet` serializer, output format, value handling, row and byte limits, truncation. |
| [mcp/error-model.md](mcp/error-model.md) | The twelve codes, how a code reaches the agent, which reach a caller today, a selection flowchart, disclosure rules. |
| [mcp/schema-output.md](mcp/schema-output.md) | Why `schema` returns DDL and not TOON, what is hidden, the one open verification task. |
| [mcp/write-tool-arguments.md](mcp/write-tool-arguments.md) | The write tool signatures, the mandatory-argument `= null` lesson, why the JSON schema marks nothing required, the shared write path. |

## testing/

| File | Contents |
| --- | --- |
| [testing/e2e-harness.md](testing/e2e-harness.md) | The dual-target harness, the sample database, the dev script, the runner. Every later phase depends on it. |
| [testing/live-database-suite.md](testing/live-database-suite.md) | The sidecar against a database that another client is using. The invariant-not-interleaving rule, the owning-application simulator, why it is in process only. |
| [testing/bad-agent-suite.md](testing/bad-agent-suite.md) | The malformed-call corpus. Why the cases are data, the four invariants, the four expectations and how a recorded gap is pinned. |

## decisions/

Decisions that are difficult to reverse, surprising without context, and the result of a real
trade-off.

| File | Contents |
| --- | --- |
| [decisions/0001-no-undo-in-mvp.md](decisions/0001-no-undo-in-mvp.md) | The MVP has no undo for a delete. Why the automatic backup before delete is gone. |
| [decisions/0002-write-is-a-whole-database-grant.md](decisions/0002-write-is-a-whole-database-grant.md) | No table allowlist. A permission applies to each table. |
| [decisions/0003-mandatory-idempotency-key.md](decisions/0003-mandatory-idempotency-key.md) | Each write tool needs `requestId`. Why failures are not cached. |
| [decisions/0004-maxrows-bounds-total-changes.md](decisions/0004-maxrows-bounds-total-changes.md) | `maxRows` counts a cascade and a trigger, not the target table alone. Why `ExecuteNonQuery` is the wrong count. |
| [decisions/0005-and-only-filter.md](decisions/0005-and-only-filter.md) | The structured filter joins with `AND` only. Why N calls bound tighter than one `or`. |
| [decisions/0006-backup-restart-cap.md](decisions/0006-backup-restart-cap.md) | The backup cap counts restarts, not seconds. The measured write rates. |
| [decisions/0007-tasks-over-a-status-tool.md](decisions/0007-tasks-over-a-status-tool.md) | MCP Tasks replaced `backup_status`. Why the execution mode selector is load-bearing. |
| [decisions/0008-a-committed-raw-write-never-fails-on-result-size.md](decisions/0008-a-committed-raw-write-never-fails-on-result-size.md) | A truncated `RETURNING` result commits. Why the reader is drained and why `ResultTooLarge` would be a lie. |
| [decisions/0009-a-locked-database-is-not-a-caller-mistake.md](decisions/0009-a-locked-database-is-not-a-caller-mistake.md) | `SQLITE_BUSY` at preparation is `DatabaseBusy` and not `InvalidQuery`, and the busy timeout lives on the handle. Why the container run found it and no in-process test could. |

## plans/

| File | Contents |
| --- | --- |
| [plans/mvp-roadmap.md](plans/mvp-roadmap.md) | The phases, which are done, what each remaining one builds, the definition of done. |
| [plans/required-tests.md](plans/required-tests.md) | The mandatory test lists: hard boundaries, startup, structured writes, idempotency, raw writes, functional. |
| [plans/open-questions.md](plans/open-questions.md) | The NativeAOT pair, MCP Tasks for `backup`, and the remaining verification tasks. |
| [plans/out-of-scope.md](plans/out-of-scope.md) | Excluded features and the reason for each exclusion. |

## deployment/

| File | Contents |
| --- | --- |
| [deployment/summary.md](deployment/summary.md) | How the sidecar ships, the three rules every recipe repeats, and where the operator documentation lives. |
| [deployment/container.md](deployment/container.md) | The repository-root build context, cross-compilation, the final stage, hardening, filesystem access, `compose.yaml`, the container launch profile. |
| [deployment/platforms.md](deployment/platforms.md) | The path contract, then Kamal, Coolify, a plain VPS with systemd and Fly.io. The client header and token rotation. |
| [deployment/distribution.md](deployment/distribution.md) | GHCR and why not Docker Hub, the trigger and tag scheme, both architectures, provenance. |

## tmp/

Session scraps. `.gitignore` ignores `.lode/tmp/`. Nothing here is permanent. Session handover
documents go here.

## Reading paths

| Task | Read |
| --- | --- |
| Start a session | `lode-map.md`, `terminology.md`, `summary.md` |
| Continue implementation | `plans/mvp-roadmap.md`, `practices.md` |
| Write or run a test | `testing/e2e-harness.md`, `plans/required-tests.md` |
| Test contention or a badly behaved agent | `testing/live-database-suite.md`, `testing/bad-agent-suite.md` |
| Look for a known gap | `plans/required-tests.md`, then `.scratch/agent-abuse-hardening/` |
| Touch SQLite access | `database/sqlite-sandbox.md`, `database/statement-check.md`, `database/connections.md` |
| Touch a backup | `database/backups.md`, `decisions/0006-backup-restart-cap.md`, `decisions/0007-tasks-over-a-status-tool.md` |
| Add or change a tool | `mcp/tool-catalog.md`, `security/permissions.md`, `mcp/error-model.md` |
| Touch authentication | `security/authentication.md`, `security/permissions.md` |
| Touch an endpoint, a path or a rate limit | `security/public-endpoint.md`, `deployment/platforms.md` |
| Touch configuration | `configuration/options.md` |
| Touch a write path | `database/structured-writes.md`, `database/raw-writes.md`, `security/write-controls.md`, `decisions/0004-maxrows-bounds-total-changes.md` |
| Write documentation | `security/model.md`, `security/threat-model.md`, `security/permissions.md`, and then `README.md` and `SECURITY.md`, which are copies |
| Touch the container or the launch configuration | `deployment/container.md`, `testing/e2e-harness.md` |
| Publish or deploy the image | `deployment/distribution.md`, `deployment/platforms.md` |
| Consider a new feature | `plans/out-of-scope.md`, `decisions/` |
