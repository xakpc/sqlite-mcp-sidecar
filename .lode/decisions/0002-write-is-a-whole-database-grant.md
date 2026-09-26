# The write permission applies to all tables

**Status: accepted.**

The `write` permission and the `danger-raw-write` permission apply to each table in the
database. There is no table allowlist and no column allowlist. An operator that must protect
one table from the agent uses a different database, or accepts the risk.

## Why this is surprising

The product limits blast radius, and a one-variable allowlist such as
`SQLITE_SIDECAR_WRITABLE_TABLES=jobs` would cost almost no code. The structured write path
validates each identifier against the live schema already, thus the allowlist is one more
comparison at that point.

A deployment with `write` can therefore change the migration history table, the outbox table
and the session table of the owning application.

## Why we did not add it

KISS and YAGNI. The MVP keeps the configuration surface small, and each variable is a
permanent contract with operators. The separation of trust levels is a deployment concern in
this product: a different trust boundary is a different sidecar with a different token.

## The rejected argument

`out-of-scope.md` excludes per-table ACLs because the MVP has one identity, thus an internal
permission model has nothing to attach to. That argument is correct for ACLs and it does not
apply to a deployment-level allowlist, which attaches to the deployment in the same way as the
permission set. The real reason is cost control, not the identity model.

## Consequences

- The README permission risk table must state that `write` is a whole-database grant.
- An operator that needs table isolation must split the database or accept the risk. A second sidecar does not help, because both sidecars see the same file.

## Related

- [../plans/design/permission-model.md](../plans/design/permission-model.md)
- [../plans/out-of-scope.md](../plans/out-of-scope.md)
