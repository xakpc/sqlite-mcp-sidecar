# 0007 — MCP Tasks carries the backup outcome, not a `backup_status` tool

`backup` is a task-mode tool. The catalog has eight tools and there is no `backup_status`.

## The problem

A backup is the longest operation in the product and its duration follows the database size. A
synchronous call holds an HTTP connection for that time, and a reverse proxy closes such a connection
first — the nginx `proxy_read_timeout` default is 60 seconds. The client then reads a gateway timeout
while the backup continues and succeeds, which is a false failure report.

An agent must also be able to learn that a backup succeeded. The MVP has no undo for a delete and
the documentation tells an agent to back up first when it is unsure, and that instruction is
worthless if the agent cannot confirm the outcome. See [0001-no-undo-in-mvp.md](0001-no-undo-in-mvp.md).

## The options

**A hand-written pair**: a non-waiting `backup` plus a `backup_status` tool over an in-memory ring of
about ten recent outcomes. Nine tools, no new package.

**MCP Tasks**: `ModelContextProtocol.Extensions.Tasks` 2.2.0. `tools/call` answers with a task and
the client polls. Eight tools, one new package.

## The decision

Adopt Tasks.

The mechanism is in the protocol, thus an agent already knows how to poll it, and it removes both a
tool and a hand-written outcome ring. The task id is the state key, thus it works with the stateless
transport.

## What made it safe

`McpTasksOptions.ExecutionModeSelector` is
`Func<RequestContext<CallToolRequestParams>, McpTaskExecutionMode>`, and `Program` pins every tool
except `backup` to `McpTaskExecutionMode.Synchronous`.

**This is load-bearing.** The default for an async tool is `Optional`. Without the selector, simply
registering a task store would give `schema`, `query`, `insert`, `update` and `delete` a second
calling convention and a second result shape. On a security boundary that is unacceptable: the error
model must own every failure an agent can cause, and a second shape routes some of them past it.
`PermissionGatingTests.OnlyTheImplementedToolsAreExposed` pins the catalog so a future change cannot
widen it silently.

`backup` is `Required` and not `Optional`. `Optional` would let a non-task client fall back to the
inline call, which is exactly the false-gateway-timeout failure this decision exists to prevent.

## What it costs

**A client that does not implement the tasks extension cannot call `backup` at all.** It receives
JSON-RPC error `-32021`, naming the required capability. This is verified live, and it is the price
of `Required`. Document it wherever `backup` is documented for an operator.

`InMemoryMcpTaskStore` is process memory, so it carries the same caveat as `WriteBudget` and
`WriteDeduplication`: it resets at a restart and two processes do not share it. That makes the
one-sidecar rule stronger, not weaker.

`IMcpTaskStore` exposes `CreateTaskAsync`, `GetTaskAsync`, `SetCompletedAsync`, `SetFailedAsync` and
`SetCancelledAsync`, but nothing to update `McpTaskInfo.StatusMessage` while the work runs. Restart
counts therefore reach the final result and the log, not an intermediate poll. Do not write a custom
`IMcpTaskStore` for that alone.

The operator-facing history that `backup_status` would have kept is gone. The log line carries it
instead: `Backup finished. outcome=… durationMs=… name=… bytes=… restarts=…`.

## A trap to remember

The published v1 documentation page for Tasks describes an API that does not exist in 2.2.0:
`McpServerOptions` has no `TaskStore` property and `McpServerToolCreateOptions` has no `Execution`
property. The v2 page is the correct one. When an SDK API looks absent, reflect over the shipped
assembly before believing a documentation page.

## Related

- [../database/backups.md](../database/backups.md)
- [../mcp/tool-catalog.md](../mcp/tool-catalog.md)
- [0001-no-undo-in-mvp.md](0001-no-undo-in-mvp.md)
