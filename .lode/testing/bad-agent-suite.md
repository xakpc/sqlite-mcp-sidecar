# The bad-agent suite

`BadAgentTests` sends malformed, wrongly typed and hostile tool calls. It is fully deterministic:
there is no generator and no seed, only a corpus file.

Code: `test/Xakpc.SQLiteMCPSidecar.Tests/BadAgentTests.cs`,
`test/Xakpc.SQLiteMCPSidecar.Tests/Harness/BadAgentCorpus.cs` and
`test/Xakpc.SQLiteMCPSidecar.Tests/Fixtures/bad-agent-corpus.json`.

## Why the cases are data and not C#

Every other tool test builds its arguments through a typed helper, thus none of them can send a wrong
JSON shape: the helper corrects it. A corpus case goes into `CallToolRequestParams.Arguments` as the
`JsonElement` that the file holds, thus `{"sql": 12345}` reaches the SDK binder exactly as an agent
would send it.

Adding a case is one JSON object. No code changes.

```json
{
  "id": "insert-values-is-an-array",
  "tool": "insert",
  "permissions": "schema,read,write",
  "arguments": { "requestId": "corpus-01", "table": "jobs", "values": [1, 2, 3] },
  "expect": "rejected-opaque",
  "why": "values is a column-to-value object, not a positional list."
}
```

`@@repeat:A:100000@@` inside a string expands to that many characters, thus a hundred-kilobyte
statement is one short line in the file.

## The four invariants, for every case

```mermaid
flowchart TD
    c[One corpus case] --> f1[Fingerprint before]
    f1 --> call[tools/call with the raw arguments]
    call --> i1[1. A CallToolResult comes back: no 500, no dropped connection]
    i1 --> i2[2. The message names no path, no token, no stack frame, no internal type]
    i2 --> e[The per-case expectation]
    e --> i34[3. and 4. Fingerprint after: it answers, and it is unchanged]
```

The fingerprint is one query of the row count of each table. It doubles as the liveness probe: it
answering at all proves the sidecar is not poisoned, and its value proves nothing was written. Two
separate calls would buy nothing, and this class is already the chattiest in the suite.

**The unchanged-database comparison runs in process only.** An external sidecar serves ONE database
to the whole run and xunit runs test classes in parallel, thus another class writes a row between
the two reads and the count moves for a reason that has nothing to do with the case. The other three
invariants hold on both targets, and CI proves this one on Linux in the in-process job.
`SandboxBoundaryTests.ARejectedRawWriteChangesNothing` had the same latent problem and compares the
schema instead of a row count now, which is also the closer assertion for `DROP TABLE`.

The per-case expectation is the weakest of the five assertions. The four invariants are the point.

## The four expectations

| `expect` | Meaning |
| --- | --- |
| `ok` | The call succeeds, and that is correct. |
| `error-code` | It fails and the message carries one of `codes`. |
| `rejected-opaque` | It fails with no error code, because the SDK binder masked it. |
| `accepted-gap` | It succeeds although the tool contract says it must not. |

`codes` is usually a `SidecarError` name. A refusal that the protocol layer makes before the tool
method runs never reaches the error model, thus such a case names the distinctive text instead, for
example `Unknown tool` or `requires authorization`.

**The last two record what the sidecar does, not what it should do, and both are pinned.**
`rejected-opaque` asserts that the message names **no** `SidecarError`; `accepted-gap` asserts that
the call **succeeds**. A fix therefore makes the case fail, which forces the entry to move and the
issue it names to be closed. A gap is never written down as `ok`.

**No case carries either one today.** The corpus is 43 `error-code` and 10 `ok`. The four gaps it
found are fixed and every entry moved to `error-code`; the two buckets stay because the next gap
needs somewhere honest to go. See `.scratch/agent-abuse-hardening/`, which holds all four with their
resolutions.

`TheCorpusIsWellFormedAndNotOnlyFailures` holds the other half: each permission group needs at least
one `ok` case, thus the corpus cannot decay into proving only that the sidecar refuses things.

## The protocol half

`BadAgentProtocolTests` posts a hand-built body to `/db/mcp`: text that is not JSON, JSON that is not
JSON-RPC, a duplicate JSON key, truncated JSON, and a five-megabyte body. A typed client cannot send
any of those. The assertion is the same for all: no 5xx, no stack trace, no internal name, no path,
and the sidecar serves the next caller. `AuthTests` owns the token cases and they are not repeated.

## Against an external sidecar, raise the request budget

The corpus connects and calls for each case, so one permission group is over a hundred requests in
the same minute. The default `MAX_REQUESTS_PER_MINUTE` of 120 answers `429` and the run proves
nothing about the corpus.

```powershell
$env:SQLITE_SIDECAR_MAX_REQUESTS_PER_MINUTE = '100000'
./scripts/dev-sidecar.ps1 -Permissions 'schema,read'
```

`.github/workflows/ci.yml` sets the same value on the `container-e2e` sidecar. **No coverage is
lost**: `RateLimitTests` owns the request budget, it sets its own small value through the harness,
and it is skipped against an external sidecar for that reason.

`SidecarHarness.Create` skips a group whose permission set the external sidecar does not carry, thus
one container run exercises one group and reports the rest honestly as skipped.

## Related

- [e2e-harness.md](e2e-harness.md) — the harness and the skip rules
- [live-database-suite.md](live-database-suite.md) — the other new suite, and the same pinning idea
- [../mcp/error-model.md](../mcp/error-model.md) — the closed code set that a case asserts on
- [../mcp/write-tool-arguments.md](../mcp/write-tool-arguments.md) — the `= null` lesson, which is the
  same root cause as the `rejected-opaque` bucket
- [../security/public-endpoint.md](../security/public-endpoint.md) — the request budget
