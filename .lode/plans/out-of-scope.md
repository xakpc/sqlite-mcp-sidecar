# Out of scope

Features that the MVP deliberately excludes. Read this file before you add a capability. If a
request matches an item here, discuss the scope change before you write code.

## Excluded

```text
multiple databases per process

multiple users
multiple tokens
RBAC
per-table ACLs
per-column ACLs
deployment-level table allowlist

OAuth / OIDC
mTLS
token rotation windows
per-caller rate partitions

REST API
CLI
browser UI

DDL

structured conflict clauses
or in a structured filter
nested filter trees
RETURNING on a structured write

restore
automatic backup before delete
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

## Why each group is out

**More databases, users, roles and ACLs.** The MVP has one authentication identity, thus an
internal permission model has nothing to attach to. A separate trust boundary uses a separate
deployment, which keeps the boundary visible in the deployment configuration. See
[../security/permissions.md](../security/permissions.md).

**Deployment-level table allowlist.** This one is not an ACL, and the identity argument above
does not apply to it. We excluded it for cost control only. A permission therefore applies to
each table in the database. See
[../decisions/0002-write-is-a-whole-database-grant.md](../decisions/0002-write-is-a-whole-database-grant.md).

**OAuth, OIDC and mTLS.** The reverse proxy owns transport identity. One bearer token agrees
with the one-identity model. A public endpoint does not change this: the consumer is one agent that
the operator configures, and such a client sends a static header. An OAuth client that needs
resource-metadata discovery cannot connect, and that is accepted. See
[../security/authentication.md](../security/authentication.md) and
[design/platform-deployment.md](design/platform-deployment.md).

**Token rotation windows and per-caller rate partitions.** Both need a list in place of one value: a
set of valid tokens, or a caller identity to partition by. The deployment has one token and one
identity, thus each feature needs that model to change first. The request budget is therefore global.
See [../security/public-endpoint.md](../security/public-endpoint.md).

**REST API, CLI and browser UI.** The product has one MCP endpoint. A second surface makes the
security review area two times larger for no product gain. CORS is off, and browser access is
not a goal.

**DDL.** Schema change is the responsibility of the owning application. A sidecar that changes
the schema can break that application, and DDL is a hard boundary in the sandbox. See
[../database/sqlite-sandbox.md](../database/sqlite-sandbox.md).

**Structured conflict clauses.** `insert` has no `OR IGNORE`, no `OR REPLACE` and no upsert. `OR
REPLACE` deletes the row that it replaces and it cascades into each referencing table, thus an
`insert` would destroy data. That is the failure that the structured layer exists to prevent. An
agent that needs conflict behaviour reads the row first, or the deployment gives it
`danger-raw-write`. The cost is one more call. See
[../database/structured-writes.md](../database/structured-writes.md).

**`or` in a structured filter.** Every condition joins with `and`. A disjunction is one call for each
branch, and that is the **tighter** bound: each call is pre-counted and limited on its own, while one
`or` call pools the blast radius of every branch into a single `maxRows`. The one real argument for
`or` is atomicity across the branches, and the sidecar offers no cross-call atomicity in any case. See
[../decisions/0005-and-only-filter.md](../decisions/0005-and-only-filter.md).

**Nested filter trees.** The structured filter is a flat condition list. A nested `and`/`or` tree
needs a recursive type and a self-referencing JSON schema, which is harder for an agent to fill
correctly, for a shape that a second call also expresses. Without `or` there is also no disjunction
left to nest.

**`RETURNING` on a structured write.** A structured write answers `rowsAffected`, and `insert` also
answers `rowid`. A `RETURNING` list would bring the TOON result path, a second result shape and a
rule for a committed write that cannot report its rows. An agent reads the row with `query`.
`execute_write_sql` does support `RETURNING`, because it already needs that path. See
[../database/raw-writes.md](../database/raw-writes.md).

**Restore, automatic backup before delete, retention and upload.** Restore is an operator
action on the host. A restore tool permits a remote caller to replace the live database, which
is a larger blast radius than each other tool together. The automatic backup before a delete
was in an earlier design, and we removed it. See
[../decisions/0001-no-undo-in-mvp.md](../decisions/0001-no-undo-in-mvp.md).

**Scheduled backups.** The product has no scheduler. An operator schedules a `backup` call, or
uses a host tool.

**Remote transaction sessions.** The MCP HTTP transport is stateless, and a transaction across
calls permits one caller to hold a write lock against the owning application for an unbounded
time. See [../database/raw-writes.md](../database/raw-writes.md).

**Replication and clustering.** SQLite is a local file database. This is not the product.

**Custom SQLite builds and a custom TOON serializer.** Both have a large maintenance cost. Use
the bundled native SQLite and the `Toon.DotNet` package. Examine this again only with a
concrete, recorded reason.

**Prometheus and OpenTelemetry.** Structured ASP.NET Core logs are sufficient for the MVP. See
[design/threat-model.md](design/threat-model.md) for the logging constraints.

**Plugin architecture and generic database abstractions.** Speculative extensibility. The
project follows YAGNI. See [../practices.md](../practices.md).

## Related

- [mvp-roadmap.md](mvp-roadmap.md)
- [../practices.md](../practices.md)
- [../decisions/](../decisions/)
