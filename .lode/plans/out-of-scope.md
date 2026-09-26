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

OAuth / OIDC
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

## Why each group is out

**More databases, users, roles and ACLs.** The MVP has one authentication identity, so an
internal permission model would have nothing to attach to. A separate trust boundary uses a
separate deployment, which keeps the boundary visible in deployment configuration. See
[design/permission-model.md](design/permission-model.md).

**OAuth, OIDC and mTLS.** The reverse proxy owns transport identity. A single bearer token
matches the one-identity model. See
[design/authentication-and-network.md](design/authentication-and-network.md).

**REST API, CLI and browser UI.** The product has one MCP endpoint. A second surface doubles
the security review area for no product gain. CORS is off, and browser access is not a goal.

**DDL.** Schema change is the owning application's responsibility. A sidecar that alters the
schema can break that application, and DDL is a hard boundary in the sandbox. See
[design/sqlite-sandbox.md](design/sqlite-sandbox.md).

**Restore, scheduled backups, retention and upload.** Restore is an operator action on the
host. A restore tool would let a remote caller replace the live database, which is a larger
blast radius than every other tool combined.

**Remote transaction sessions.** The MCP HTTP transport is stateless, and a cross-call
transaction would let one caller hold a write lock against the owning application for an
unbounded time. See [design/raw-writes.md](design/raw-writes.md).

**Replication and clustering.** SQLite is a local file database. This is not the product.

**Custom SQLite builds and a custom TOON serializer.** Both are large maintenance costs. Use
the bundled native SQLite and the `Toon.DotNet` package. Revisit only with a concrete,
recorded reason.

**Prometheus and OpenTelemetry.** Structured ASP.NET Core logs are enough for the MVP. See
[design/threat-model.md](design/threat-model.md) for the logging constraints.

**Plugin architecture and generic database abstractions.** Speculative extensibility. The
project follows YAGNI. See [../practices.md](../practices.md).

## Related

- [mvp-roadmap.md](mvp-roadmap.md)
- [../practices.md](../practices.md)
