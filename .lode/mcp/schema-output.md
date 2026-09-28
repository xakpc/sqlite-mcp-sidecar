# Schema output: DDL and not TOON

Why the `schema` tool returns SQL text while every row-returning tool returns TOON.

Code: `SqliteService.ReadSchemaDdlAsync`, the `schema` tool in
`src/Xakpc.SQLiteMCPSidecar/Mcp/SqliteTools.cs`.

## The statement

```sql
SELECT sql FROM sqlite_master
WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'
ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'view' THEN 1 WHEN 'index' THEN 2 ELSE 3 END, name;
```

Server-authored and it takes no caller input, thus the tool needs no sandbox and has no parameter. It
runs over a read-only connection. See [../database/connections.md](../database/connections.md).

## The output

```text
CREATE TABLE jobs (
  id        INTEGER PRIMARY KEY,
  status    TEXT NOT NULL DEFAULT 'pending',
  retry     INTEGER NOT NULL DEFAULT 0,
  owner_id  INTEGER REFERENCES users(id),
  ...
);
CREATE VIEW failed_jobs AS ...;
CREATE INDEX idx_jobs_status ON jobs(status);
```

## Why not TOON

**TOON is for row data only.** One `CREATE TABLE` statement states the columns, the types, the
nullability, the defaults, the primary key, the foreign keys and the constraints in one string. A set
of TOON tables holds the same facts in a shape the agent must assemble again, and it costs more
tokens.

`SchemaToolTests.SchemaReturnsNoRowData` holds the boundary: the output carries no row values and no
`rows[` header.

## What is hidden

The internal `sqlite_%` objects are excluded, thus `sqlite_autoindex` and `sqlite_sequence` never
reach the agent. `SchemaToolTests.SchemaDoesNotDiscloseTheDatabasePath` also holds that the output
never names the database file.

## Still unproven

This choice is cheap to reverse, and a real agent must still confirm that the DDL output is usable in
practice. It is the one open verification task left from the read path. See
[../plans/open-questions.md](../plans/open-questions.md).

## Related

- [tool-catalog.md](tool-catalog.md)
- [query-results.md](query-results.md) — the TOON path that this tool deliberately avoids
- [../database/connections.md](../database/connections.md)
