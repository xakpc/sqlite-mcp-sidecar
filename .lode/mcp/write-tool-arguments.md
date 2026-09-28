# The write tools: arguments and binding

How `insert`, `update` and `delete` declare their arguments, why the generated JSON schema marks
none of them required, and the one shared code path behind all three.

Code: `src/Xakpc.SQLiteMCPSidecar/Mcp/SqliteTools.cs`. The statement building is in
[../database/structured-writes.md](../database/structured-writes.md).

## `insert`

```csharp
public async Task<CallToolResult> InsertAsync(
    string? requestId = null,
    string? table = null,
    Dictionary<string, JsonElement>? values = null,
    CancellationToken cancellationToken = default)
```

Three arguments. The caller sends no SQL: the server builds one parameterized `INSERT` that adds
exactly one row. See [../database/structured-writes.md](../database/structured-writes.md).

```text
rowsAffected: 1
rowid: 78
```

**Lesson.** Each mandatory argument carries `= null` and is validated in the method body. A nullable
type alone is **not** enough: the binder of the SDK treats a parameter with no default value as
required and throws when the argument is absent, and the SDK then replaces the message with `"An error
occurred invoking 'insert'."`. The agent gets no code to select from, and the required case
"write without requestId -> `InvalidWrite`" cannot pass.
`WriteIdempotencyTests.AnAbsentRequestIdIsInvalidWrite` fails if the defaults are removed.

The cost is that the generated JSON schema marks **no** argument as required:

```json
{"type":"object","properties":{
  "requestId":{"type":["string","null"],"default":null,"description":"..."},
  "table":{"type":["string","null"],"default":null,"description":"..."},
  "values":{"type":["object","null"],"default":null,"description":"..."}}}
```

The tool description carries the requirement instead. That trade is correct: a description that the
agent reads plus an actionable error beats a schema keyword plus an opaque failure.

The description must also state the three limits that the schema cannot show: a value is a literal and
never a SQL expression, one call adds one row, and there is no conflict clause.

## `update` and `delete`

```csharp
public async Task<CallToolResult> UpdateAsync(
    string? requestId = null,
    string? table = null,
    Dictionary<string, JsonElement>? values = null,
    List<WriteCondition>? where = null,
    int? maxRows = null,
    CancellationToken cancellationToken = default)
```

`delete` is the same without `values`. `where` and `maxRows` are mandatory and follow the same
`= null` rule as every other write argument. See
[../database/structured-writes.md](../database/structured-writes.md).

```text
rowsAffected: 1
rowsChanged: 4
```

The description of `maxRows` must state that the number counts every row the write touches, including
a row that `ON DELETE CASCADE` removes in another table. It is the surprising part of the contract:
deleting one row that has three cascading children needs a `maxRows` of at least 4. See
[../decisions/0004-maxrows-bounds-total-changes.md](../decisions/0004-maxrows-bounds-total-changes.md).

The description of `where` must name the eight operators. The generated schema types them as a plain
string, thus the text is the only place the agent can read the set.

**The three write tools share one private path**, `RunStructuredWriteAsync`. It holds the order of the
controls — deduplication, budget, request slot, write slot, execute, charge, cache — thus the order
cannot drift between one tool and another. Each tool method owns only its own shape validation and its
result format.


## Related

- [tool-catalog.md](tool-catalog.md) — the catalog and the registration
- [../database/structured-writes.md](../database/structured-writes.md)
- [../security/write-controls.md](../security/write-controls.md)
- [../decisions/0004-maxrows-bounds-total-changes.md](../decisions/0004-maxrows-bounds-total-changes.md)
