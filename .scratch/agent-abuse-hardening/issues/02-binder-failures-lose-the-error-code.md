# A wrong argument type gives the agent no error code

Status: resolved

## What happens

An argument of the wrong JSON type fails in the SDK binder, before the tool method runs. The SDK
catches that and replaces the message with its own fixed text:

```text
An error occurred invoking 'query'.
An error occurred invoking 'insert'.
```

The agent gets `IsError` and nothing else. No `SidecarError` name, thus nothing to select a next
action from. The error model is supposed to own every failure that an agent can cause, and it does
not own this one.

## Why it is reachable

An agent builds arguments from a JSON schema and its own reasoning. The schema marks nothing as
required, because of the `= null` lesson in
[../../../.lode/mcp/write-tool-arguments.md](../../../.lode/mcp/write-tool-arguments.md), thus the
only thing standing between the agent and this failure is the tool description. A wrong type is the
most ordinary mistake such an agent makes.

This is the same root cause as that lesson. `= null` fixed the absent-argument half. The
wrong-type half is still open.

## The cases

Thirteen corpus entries carry `expect: "rejected-opaque"`, and every one of them is this issue:

```text
query-sql-is-a-number          query-sql-is-an-array        query-sql-is-an-object
query-sql-is-json-null         query-sql-is-absent
insert-values-is-an-array      insert-values-is-a-string
insert-requestid-is-a-number   insert-requestid-is-true
update-where-is-a-string       update-where-is-an-object
update-maxrows-is-a-string     raw-write-sql-is-a-number
```

`query-sql-is-json-null` and `query-sql-is-absent` are worth naming separately: `query` is the one
tool whose mandatory argument has **no** `= null` default, thus it reaches the binder where the write
tools return `InvalidWrite`. Compare `raw-write-sql-is-absent`, which does return a code.

## Reproduction

```powershell
dotnet test --solution Xakpc.SQLiteMCPSidecar.slnx --filter-method '*BadAgentTests*'
```

The cases pass today: the corpus pins the present behaviour. Each one asserts that the message names
**no** `SidecarError`, thus fixing this issue makes them fail and forces the entry to move to
`expect: "error-code"`.

## Candidate fix

Take the arguments as `JsonElement?` and validate the kind in the method, which is the pattern the
tool methods already use for absence.

```csharp
public async Task<CallToolResult> QueryAsync(JsonElement? sql = null, ...)
// sql is null or not a JSON string -> SidecarError.InvalidQuery with actionable text
```

`query` is the smallest first step and it closes two of the thirteen. The write tools need the same
treatment for `values`, `where`, `maxRows` and `requestId`.

The cost is the JSON schema: a `JsonElement` parameter loses its declared type, so the tool
description has to carry the shape. That is the same trade the `= null` lesson already accepted, and
it is recorded there.

## Afterwards

- Move each fixed case to `expect: "error-code"` with the code it now returns.
- Update [../../../.lode/mcp/write-tool-arguments.md](../../../.lode/mcp/write-tool-arguments.md) and
  [../../../.lode/mcp/error-model.md](../../../.lode/mcp/error-model.md).

## Resolution

`Mcp/ArgumentBindingFilter.cs`, registered with `AddCallToolFilter`, catches the `JsonException`
that the binder raises and answers with `InvalidQuery` for `query` and `InvalidWrite` for the write
tools.

**Not the `JsonElement` parameters that this ticket proposed.** That approach was built first and
then discarded: it returns a code and it costs the JSON schema, and
`UpdateToolTests.TheFilterArgumentHasAUsableSchema` caught the loss. `where` would have stopped
describing the condition object, which is the one machine-readable account of the shape an agent has
to build. The filter keeps the rich schema for the agent that reads it and gives a code to the agent
that ignored it, and it covers each tool added later with no per-argument work.

The trade is that the message names the tool and not the argument: the binder converts each argument
on its own and the exception carries the path `$`. The description and the schema carry the shape of
each argument.

`query` also gained `string? sql = null` and its own required check, which is the `= null` lesson it
never had. Thirteen corpus cases moved to `expect: "error-code"`.
