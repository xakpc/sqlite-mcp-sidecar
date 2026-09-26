# Open questions

Decisions that implementation must settle. Each item names the trigger, the options and the
recommendation. Remove an item when the code settles it, and move the outcome into the
matching design file.

Only the NativeAOT pair is open. Discussion cannot settle it, because it needs a real publish
run. The other items moved into the design files after the design review. Hard decisions with
a permanent effect are in [../decisions/](../decisions/).

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

## Verification tasks, not open questions

These items have a decision. Implementation must prove one fact for each of them.

| Task | Phase | Fact to prove |
| --- | --- | --- |
| Backup cancellation | 5 | `sqlite3_interrupt` on the source connection stops a running `BackupDatabase` call. If it does not, remove the runaway cap and document the size limit. See [design/backups.md](design/backups.md). |
| Schema output | 3 | A real agent uses the DDL output correctly. The choice is cheap to reverse. See [design/mcp-tool-catalog.md](design/mcp-tool-catalog.md). |

## Related

- [mvp-roadmap.md](mvp-roadmap.md)
- [design/](design/)
- [../decisions/](../decisions/)
