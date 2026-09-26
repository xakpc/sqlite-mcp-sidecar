# Security domain

The code that keeps an authorized but mistaken agent inside its permission set.

| File | Contents |
| --- | --- |
| [authentication.md](authentication.md) | The deployment bearer token, the constant-time comparison, the claims that it issues, the health endpoint, the network model. |
| [permissions.md](permissions.md) | The six permissions, the read floor, the claim and policy mechanism, the two gating layers. |

The layered model and the threat model stay in
[../plans/design/security-model.md](../plans/design/security-model.md) and
[../plans/design/threat-model.md](../plans/design/threat-model.md), because the SQLite sandbox layer
has no code yet.

## Layers today

```mermaid
flowchart TD
    req[Request] --> tok[Deployment token, constant-time]
    tok --> pol[Authorization policy per permission]
    pol --> list[tools/list holds the permitted tools only]
    pol --> call[tools/call rejects an unpermitted name]
    call --> ro[Read-only connection plus query_only]
    ro --> sandbox[SQLite sandbox: no code yet, Phase 2]
```

## Related

- [../configuration/options.md](../configuration/options.md)
- [../mcp/tool-catalog.md](../mcp/tool-catalog.md)
- [../decisions/](../decisions/)
