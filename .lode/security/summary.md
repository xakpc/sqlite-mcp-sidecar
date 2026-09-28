# Security domain

The code that keeps an authorized but mistaken agent inside its permission set.

| File | Contents |
| --- | --- |
| [authentication.md](authentication.md) | The deployment bearer token, the constant-time comparison, the claims that it issues, the health endpoint, the network model. |
| [permissions.md](permissions.md) | The six permissions, the read floor, the claim and policy mechanism, the two gating layers. |
| [public-endpoint.md](public-endpoint.md) | The paths, the path contract, the request budget, the absent host filtering. |
| [write-controls.md](write-controls.md) | The write budget, the idempotency cache, the order of the controls, the bounds. |

The innermost layer is in [../database/sqlite-sandbox.md](../database/sqlite-sandbox.md). The full
layered model and the threat model stay in
[../plans/design/security-model.md](../plans/design/security-model.md) and
[../plans/design/threat-model.md](../plans/design/threat-model.md), because `update`, `delete` and
`execute_write_sql` have no code yet.

## Layers today

```mermaid
flowchart TD
    req[Request] --> budget[Request budget, one window for the process]
    budget --> tok[Deployment token, constant-time]
    tok --> pol[Authorization policy per permission]
    pol --> list[tools/list holds the permitted tools only]
    pol --> call[tools/call rejects an unpermitted name]
    call --> ro[Read-only connection plus query_only]
    ro --> base[Baseline: defensive mode, trusted schema off, runtime limits]
    base --> authz[Allowlist authorizer for the operation]
    authz --> one[One-statement check]
    one --> bound[Row limit, byte limit, timeout with interrupt]
    bound --> err[A small error code, and no path in the answer]
    pol --> w1[Write: shape validation, then the idempotency cache]
    w1 --> w2[Write budget for the minute]
    w2 --> w3[Request slot, then the one write slot, bounded wait]
    w3 --> w4[Identifier validation against the live schema]
    w4 --> w5[BEGIN IMMEDIATE, then the write authorizer]
    w5 --> w6[Server-built parameterized statement, one row]
    w6 --> err
```

The budget is first on purpose: it counts a request with a wrong token too, thus a flood cannot fill
the log. See [public-endpoint.md](public-endpoint.md).

Every layer above `ro` applies to caller SQL. The write branch adds to this stack and replaces no part
of it: the baseline and the authorizer apply to the write connection too. `update` and `delete` add
the bounded pre-count and the row-limit rollback on top, and that limit counts every row the write
changes, a cascade and a trigger included. See
[../database/structured-writes.md](../database/structured-writes.md).

## Related

- [../database/sqlite-sandbox.md](../database/sqlite-sandbox.md)
- [../mcp/error-model.md](../mcp/error-model.md)
- [../configuration/options.md](../configuration/options.md)
- [../mcp/tool-catalog.md](../mcp/tool-catalog.md)
- [../decisions/](../decisions/)
