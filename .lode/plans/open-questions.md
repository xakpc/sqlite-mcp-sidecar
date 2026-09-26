# Open questions

Decisions that implementation must settle. Each item names the trigger, the options and the
recommendation. Remove an item when the code settles it, and move the outcome into the
matching design file.

## 1. NativeAOT viability

**Trigger.** Phase 8. See [mvp-roadmap.md](mvp-roadmap.md).

The design asks for NativeAOT where practical, and it accepts a self-contained image as the
fallback.

Findings from package inspection:

- `Microsoft.Data.Sqlite` 10.0.12 with the bundled native SQLite works under AOT.
- `ModelContextProtocol.Core` 2.2.0 carries one `RequiresUnreferencedCode` annotation. Assembly scanning is the likely site, so explicit `WithTools<T>()` registration avoids it. Tool schema generation from method signatures is still reflection-based, and that is the real risk.
- `Toon.DotNet` 4.1.1 declares no `RequiresUnreferencedCode` and no `RequiresDynamicCode`. Absence of an annotation is not proof of AOT safety. The `Toon.Encode(object, ...)` path reflects over the argument type. The `DataTable` overload avoids that.

**Recommendation.** Attempt AOT. Prefer the `DataTable` shape for results. Do not restructure
the architecture to obtain AOT. Decide with a real `dotnet publish -r linux-x64` run, not from
annotations.

## 2. Raw-write accounting against the write budget

**Trigger.** Phase 6. See [design/raw-writes.md](design/raw-writes.md).

The design says the budget applies to structured writes, and that raw writes should also count
when the implementation stays simple.

SQLite reports the affected row count after execution, so counting is easy. The nuance is that
the count arrives too late to prevent the write.

**Recommendation.** Count raw writes toward the budget after execution. Document the budget as
a throttle on subsequent operations, not as a pre-check for raw writes. Record the final
wording in `SECURITY.md`.

## 3. Backup-before-delete for raw deletes

**Trigger.** Phase 6. See [design/structured-writes.md](design/structured-writes.md).

The policy is well defined for structured `delete`. A raw statement may delete, may update, or
may do both through a CTE, so the sidecar cannot cheaply know that a delete will happen before
execution.

Options:

1. Apply the policy to every `execute_write_sql` call. It is simple and safe, but it makes a
   backup for every raw update as well.
2. Apply it only when the authorizer observes a delete action. It is accurate, but the
   authorizer runs during preparation, which complicates the ordering.
3. Do not apply it to raw writes, and document the gap.

**Recommendation.** Option 1. An operator that enables both `danger-raw-write` and
backup-before-delete has chosen caution over speed. Make the behavior obvious rather than
clever.

## 4. Connection pooling

**Trigger.** Phase 2. See [design/connection-policy.md](design/connection-policy.md).

A pooled handle keeps the authorizer and the limits from the previous operation. That is a
privilege-escalation path.

**Recommendation.** Set `Pooling=False` for the MVP. It removes the whole class of
residual-state defects. Measure the open cost before any change, because a local SQLite open is
cheap.

## 5. Byte-limit behavior

**Trigger.** Phase 3. See [design/toon-results.md](design/toon-results.md).

The design lists both a `truncated` flag and a `ResultTooLarge` error, and it does not say
which one the byte limit produces.

**Recommendation.** Truncate and set `truncated: true`, the same as the row limit. Reserve
`ResultTooLarge` for a single row that exceeds the budget on its own, where no useful partial
result exists.

## 6. Schema tool output shape

**Trigger.** Phase 3. See [design/mcp-tool-catalog.md](design/mcp-tool-catalog.md).

Schema metadata is not one flat table. Tables, columns, primary keys and foreign keys have
different shapes, and the design says only that tabular parts are TOON.

**Recommendation.** Emit one TOON block per relation kind under a labeled heading. Decide the
exact layout against a real agent, because readability for the agent is the only criterion.

## 7. Minimal container base image

**Trigger.** Phase 7. See [design/container-and-deployment.md](design/container-and-deployment.md).

The design asks for a minimal image with no development toolchain. The candidates differ if
AOT succeeds.

**Recommendation.** Decide after question 1. A successful AOT build allows a much smaller base
than the `aspnet` runtime image.

## Related

- [mvp-roadmap.md](mvp-roadmap.md)
- [design/](design/)
