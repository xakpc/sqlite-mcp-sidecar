# Open questions

Decisions that implementation must settle. Each item names the trigger, the options and the
recommendation. Remove an item when the code settles it, and move the outcome into the
matching design file.

The NativeAOT pair needs a real publish run, thus discussion cannot settle it. Hard decisions with a
permanent effect are in [../decisions/](../decisions/).

The MCP Tasks question is settled: Tasks replaced the `backup_status` tool. See
[../decisions/0007-tasks-over-a-status-tool.md](../decisions/0007-tasks-over-a-status-tool.md). The
backup cancellation task is settled by the SQLite documentation: `sqlite3_backup_step` does not
document `SQLITE_INTERRUPT`, thus the step loop checks a token between steps instead. See
[../database/backups.md](../database/backups.md).

## 1. NativeAOT viability

**Trigger.** Phase 8. See [mvp-roadmap.md](mvp-roadmap.md).

The design asks for NativeAOT where practical, and it accepts a self-contained image as the
fallback.

Findings from package inspection:

- `Microsoft.Data.Sqlite` 10.0.12 with the bundled native SQLite operates under AOT.
- `ModelContextProtocol.Core` 2.2.0 has one `RequiresUnreferencedCode` annotation. Assembly scanning is the probable site, thus explicit `WithTools<T>()` registration prevents it. Tool schema generation from method signatures still uses reflection, and that is the real risk.
- `Toon.DotNet` 4.1.1 declares no `RequiresUnreferencedCode` and no `RequiresDynamicCode`. An absent annotation is not proof of AOT safety. **Settled:** the result pipeline uses the `DataTable` overload, thus nothing reflects over the argument type. See [../mcp/query-results.md](../mcp/query-results.md).
- `ModelContextProtocol.Extensions.Tasks` 2.2.0 ships `McpTasksJsonContext`, a source-generated JSON context, which is a good sign. The production project still builds with no IL warning after it was added.

The sandbox adds native callbacks, `delegate_authorizer` and `delegate_progress`. The production
project builds with no IL warning today, and that is the check to repeat before the publish attempt.

**Recommendation.** Attempt AOT. Do not change the architecture to get AOT. Decide with a real
`dotnet publish -r linux-x64` run, not from annotations.

## 2. Minimal container base image

**Trigger.** Phase 7 and Phase 8. See
[design/container-and-deployment.md](design/container-and-deployment.md).

The design asks for a minimal image with no development toolchain. The candidates are
different if AOT succeeds.

**Recommendation.** Decide after question 1. A successful AOT build permits a much smaller
base than the `aspnet` runtime image.

## Verification tasks, not open questions

These items have a decision. Implementation must prove one fact for each of them.

| Task | Phase | Fact to prove |
| --- | --- | --- |
| Schema output | 3 | A real agent uses the DDL output correctly. The choice is cheap to reverse. See [../mcp/schema-output.md](../mcp/schema-output.md). |

## Related

- [mvp-roadmap.md](mvp-roadmap.md)
- [design/](design/)
- [../decisions/](../decisions/)
