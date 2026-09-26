# Open questions

Decisions that implementation must settle. Each item names the trigger, the options and the
recommendation. Remove an item when the code settles it, and move the outcome into the
matching design file.

The NativeAOT pair needs a real publish run, thus discussion cannot settle it. The MCP Tasks
question needs the backup code. Hard decisions with a permanent effect are in
[../decisions/](../decisions/).

## 1. NativeAOT viability

**Trigger.** Phase 8. See [mvp-roadmap.md](mvp-roadmap.md).

The design asks for NativeAOT where practical, and it accepts a self-contained image as the
fallback.

Findings from package inspection:

- `Microsoft.Data.Sqlite` 10.0.12 with the bundled native SQLite operates under AOT.
- `ModelContextProtocol.Core` 2.2.0 has one `RequiresUnreferencedCode` annotation. Assembly scanning is the probable site, thus explicit `WithTools<T>()` registration prevents it. Tool schema generation from method signatures still uses reflection, and that is the real risk.
- `Toon.DotNet` 4.1.1 declares no `RequiresUnreferencedCode` and no `RequiresDynamicCode`. An absent annotation is not proof of AOT safety. The `Toon.Encode(object, ...)` path reflects over the argument type. The `DataTable` overload prevents that.

**Recommendation.** Attempt AOT. Prefer the `DataTable` shape for results. Do not change the
architecture to get AOT. Decide with a real `dotnet publish -r linux-x64` run, not from
annotations.

## 2. Minimal container base image

**Trigger.** Phase 7 and Phase 8. See
[design/container-and-deployment.md](design/container-and-deployment.md).

The design asks for a minimal image with no development toolchain. The candidates are
different if AOT succeeds.

**Recommendation.** Decide after question 1. A successful AOT build permits a much smaller
base than the `aspnet` runtime image.

## 3. MCP Tasks for `backup`

**Trigger.** Phase 5, when `Database/BackupService.cs` is written. See
[design/backups.md](design/backups.md).

`ModelContextProtocol.Extensions.Tasks` 2.2.0 gives the protocol-standard "call now, fetch later"
shape: `tools/call` answers with a `taskId` and `status=working`, and the client polls `tasks/get`
until a terminal status carries the result.

This is precisely the problem that [design/backups.md](design/backups.md) solves by hand. That file
gives two reasons for the non-waiting `backup` plus a separate `backup_status` tool: a reverse proxy
closes a long connection, for example at the 60-second `proxy_read_timeout` default of nginx, and an
agent that cannot know that the backup succeeded cannot follow the instruction to back up before a
delete.

For it:

- The mechanism is in the protocol, thus an agent already knows how to poll it.
- It removes the `backup_status` tool. The catalog becomes eight tools.
- It removes the hand-written outcome ring.
- `ToolTaskSupport` works with the stateless transport, because the `taskId` is the state key.

Against it:

- `IMcpTaskStore` in memory carries the same single-process caveat as the write budget and the idempotency cache. It makes the one-sidecar rule stronger, not weaker.
- A task store drops terminal state at its TTL. `backup_status` reports a ring of about 10 recent outcomes, which is operator-facing history that a task store does not keep.
- It adds a package and a second result shape to the product.

**Recommendation.** Decide with the backup code in front of you. The deciding question is whether
the operator-facing history of `backup_status` is worth one hand-written tool. If it is not, adopt
Tasks and drop the tool.

## Verification tasks, not open questions

These items have a decision. Implementation must prove one fact for each of them.

| Task | Phase | Fact to prove |
| --- | --- | --- |
| Backup cancellation | 5 | `sqlite3_interrupt` on the source connection stops a running `BackupDatabase` call. If it does not, remove the runaway cap and document the size limit. See [design/backups.md](design/backups.md). |
| Schema output | 3 | A real agent uses the DDL output correctly. The choice is cheap to reverse. See [../mcp/tool-catalog.md](../mcp/tool-catalog.md). |

## Related

- [mvp-roadmap.md](mvp-roadmap.md)
- [design/](design/)
- [../decisions/](../decisions/)
