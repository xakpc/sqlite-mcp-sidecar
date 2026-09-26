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

REST API
CLI
browser UI

DDL

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
[design/permission-model.md](design/permission-model.md).

**Deployment-level table allowlist.** This one is not an ACL, and the identity argument above
does not apply to it. We excluded it for cost control only. A permission therefore applies to
each table in the database. See
[../decisions/0002-write-is-a-whole-database-grant.md](../decisions/0002-write-is-a-whole-database-grant.md).

**OAuth, OIDC and mTLS.** The reverse proxy owns transport identity. One bearer token agrees
with the one-identity model. See
[design/authentication-and-network.md](design/authentication-and-network.md).

**REST API, CLI and browser UI.** The product has one MCP endpoint. A second surface makes the
security review area two times larger for no product gain. CORS is off, and browser access is
not a goal.

**DDL.** Schema change is the responsibility of the owning application. A sidecar that changes
the schema can break that application, and DDL is a hard boundary in the sandbox. See
[design/sqlite-sandbox.md](design/sqlite-sandbox.md).

**Restore, automatic backup before delete, retention and upload.** Restore is an operator
action on the host. A restore tool permits a remote caller to replace the live database, which
is a larger blast radius than each other tool together. The automatic backup before a delete
was in an earlier design, and we removed it. See
[../decisions/0001-no-undo-in-mvp.md](../decisions/0001-no-undo-in-mvp.md).

**Scheduled backups.** The product has no scheduler. An operator schedules a `backup` call, or
uses a host tool.

**Remote transaction sessions.** The MCP HTTP transport is stateless, and a transaction across
calls permits one caller to hold a write lock against the owning application for an unbounded
time. See [design/raw-writes.md](design/raw-writes.md).

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
