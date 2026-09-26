# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the
codebase.

This repo keeps its domain documentation in **the Lode** at `.lode/`, not in a root
`CONTEXT.md`. `CLAUDE.md` mandates the Lode as the single home for persistent project memory,
so there is no separate `CONTEXT.md` and no `docs/adr/` directory. The Lode replaces both.

## Before exploring, read these

- **`.lode/lode-map.md`** — the index of every lode file. Always read this first. It tells you which file covers the area you are about to touch.
- **`.lode/terminology.md`** — the glossary. This is the `CONTEXT.md` equivalent.
- **`.lode/summary.md`** — what the product is, and the current implementation status.
- **`.lode/decisions/`** — accepted decision records, the ADR equivalent. Read the ones that touch your area.
- **`.lode/plans/design/`** — target design and rationale for work that has no code yet. Each file carries a planned-status banner.

If any of these do not exist, **proceed silently**. Do not flag their absence and do not
suggest creating them upfront. `.lode/decisions/` does not exist yet; `/domain-modeling`
creates it lazily when a decision actually gets resolved.

## File structure

```
/
├── .lode/
│   ├── lode-map.md           ← index; read first
│   ├── summary.md            ← product + implementation status
│   ├── terminology.md        ← the glossary
│   ├── practices.md          ← active coding rules
│   ├── decisions/            ← ADR equivalent, created lazily
│   ├── plans/
│   │   ├── mvp-roadmap.md
│   │   ├── open-questions.md
│   │   ├── out-of-scope.md
│   │   └── design/           ← target design, not yet implemented
│   └── tmp/                  ← git-ignored session scraps
└── src/
```

## Use the glossary's vocabulary

When your output names a domain concept (an issue title, a refactor proposal, a hypothesis, a
test name), use the term as defined in `.lode/terminology.md`. Do not drift to synonyms.

This project has precise terms that matter. `structured write` and `raw write` are not
interchangeable. `permission` is a deployment property, not a user property. `sandbox` names
the always-on SQLite controls, not a general safety concept.

If the concept you need is not in the glossary, that is a signal: either you are inventing
language the project does not use (reconsider), or there is a real gap (note it for
`/domain-modeling`).

## Flag decision conflicts

If your output contradicts a record in `.lode/decisions/`, or an invariant in
`.lode/plans/design/`, surface it explicitly rather than silently overriding:

> _Contradicts the pooling invariant in `plans/design/connection-policy.md`, but worth
> reopening because…_

Check `.lode/plans/out-of-scope.md` before proposing a new capability. That file records what
the MVP deliberately excludes and why.

## Writing into the Lode

Per `CLAUDE.md`:

- You have full authority to create, update, rename, move and delete inside `.lode/`.
- Write lode files in ASD-STE100 Simplified Technical English.
- A lode file describes the **current state** of the system, never a history of changes. Changelog-style notes go in `.lode/tmp/`.
- Every lode file covers one topic, stays under 250 lines, uses Mermaid for diagrams, and links to related files with relative paths.
- Update the matching lode file immediately after a change to code behavior or structure, and update `.lode/lode-map.md` when you add or move a file.
