# 0005 — The structured filter joins with `AND` only

## Decision

Every condition of a structured filter joins with `AND`. There is no `or`, and there is no `combine`
argument. An earlier design had `combine` with the values `and` and `or`, defaulting to `and`. It is
not built.

## Context

The filter model exists to make the bounded pre-count constructible. It is not a SQL expression
language, and each operator in it has to earn its place.

## Reasons

**N calls are safer than one `or` call.** Every disjunction is expressible as one call for each
branch, and each of those calls is pre-counted and bounded by its own `maxRows`. One `or` call pools
the blast radius of all its branches into a single limit. The alternative is therefore not only as
capable, it is the tighter bound.

**The atomicity argument does not apply here.** The one real reason to keep `or` is a change that must
be atomic across a disjunction. The sidecar already offers no cross-call atomicity: the MCP transport
is stateless and remote transaction sessions are out of scope. Splitting a disjunction into two calls
therefore concedes nothing that the product ever offered. See
[../plans/design/raw-writes.md](../plans/design/raw-writes.md).

**One fewer argument to fill in wrong.** The tool description is the only thing the agent reads. Each
argument it describes competes for attention with the ones that carry a guarantee, `where` and
`maxRows`.

**The reverse is not available.** Adding `combine` later is additive and non-breaking: a new optional
argument whose default is the behaviour of today. Removing it later would break a caller.

## Consequences

An agent that needs a disjunction sends one call for each branch, with its own `requestId`. The cost
is one more call.

A nested `and` / `or` tree stays out of scope, and now for a second reason: without `combine` there is
no disjunction at any level to nest.

An `and` between two conditions is proven by
`UpdateToolTests.TwoConditionsJoinWithAnd`.

## Related

- [../database/structured-writes.md](../database/structured-writes.md) — the filter model
- [../plans/out-of-scope.md](../plans/out-of-scope.md)
- [0004-maxrows-bounds-total-changes.md](0004-maxrows-bounds-total-changes.md)
