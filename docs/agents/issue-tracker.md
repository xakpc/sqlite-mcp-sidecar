# Issue tracker: Local Markdown

Issues and specs for this repo live as markdown files in `.scratch/`.

This repo has no git remote yet. If it later gains a GitHub remote, switch this file to the
GitHub workflow (`gh issue create`) rather than adding a second tracker.

## `.scratch/` vs `.lode/tmp/`

Two scratch locations exist. They have different jobs. Do not mix them.

| Path | Purpose | Committed |
| --- | --- | --- |
| `.scratch/` | The issue tracker. Specs and tickets. | yes |
| `.lode/tmp/` | Session scraps and handover notes. | no, git-ignored |

An issue never goes in `.lode/tmp/`. A session handover never goes in `.scratch/`.

## Conventions

- One feature per directory: `.scratch/<feature-slug>/`
- The spec is `.scratch/<feature-slug>/spec.md`
- Implementation issues are one file per ticket at `.scratch/<feature-slug>/issues/<NN>-<slug>.md`, numbered from `01`, never a single combined tickets file
- Triage state is recorded as a `Status:` line near the top of each issue file (see `triage-labels.md` for the role strings)
- Comments and conversation history append to the bottom of the file under a `## Comments` heading

## When a skill says "publish to the issue tracker"

Create a new file under `.scratch/<feature-slug>/` (creating the directory if needed).

## When a skill says "fetch the relevant ticket"

Read the file at the referenced path. The user will normally pass the path or the issue number directly.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a file with one **child** file per ticket.

- **Map**: `.scratch/<effort>/map.md` (the Notes / Decisions-so-far / Fog body).
- **Child ticket**: `.scratch/<effort>/issues/NN-<slug>.md`, numbered from `01`, with the question in the body. A `Type:` line records the ticket type (`research`/`prototype`/`grilling`/`task`); a `Status:` line records `claimed`/`resolved`.
- **Blocking**: a `Blocked by: NN, NN` line near the top. A ticket is unblocked when every file it lists is `resolved`.
- **Frontier**: scan `.scratch/<effort>/issues/` for files that are open, unblocked, and unclaimed; first by number wins.
- **Claim**: set `Status: claimed` and save before any work.
- **Resolve**: append the answer under an `## Answer` heading, set `Status: resolved`, then append a context pointer (gist + link) to the map's Decisions-so-far in `map.md`.

## Relationship to the roadmap

The phased plan of work lives in `.lode/plans/mvp-roadmap.md`. That file is the plan, and
`.scratch/` holds the tickets that carry out a phase. Keep the roadmap as the source of
sequence, and do not restate the phase list inside a ticket.
