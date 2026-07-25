# Snapshot judge gate — design

*Date: 2026-07-25 · Status: designed, not implemented*

## The problem

A Verify baseline (`*.verified.txt`) is the one place in this repo where an
agent can turn a failing test green **with zero change to product code**.
Copy `received` over `verified` — or run `dotnet verify accept` — and the test
suite stops describing intended behaviour and starts encoding whatever the
code currently does. The regression is now the baseline, and every later run
is green.

The 24 baselines in `test/` are also, by accident, the most expensive things
in the repo to get wrong:

- `PublicApi.*.verified.txt` (5) — the public API approval surface, i.e.
  backward compatibility, which the project treats as a first-class value.
- `*GeneratedSqlSnapshotTests.*.verified.txt` (16) — generated DDL, including
  destructive operations. A silently rewritten baseline turns a data-losing
  migration into "expected output".
- `canonical-complex-crm.verified.txt`,
  `DescriptorToSchemaMapperTests.*.verified.txt`,
  `negative-error-output.verified.txt` — descriptor → schema mapping and the
  negative-path error contract.

No deterministic check can catch this. A grep can say *"the baseline
changed"*; it cannot say *"the baseline changed **without justification**"*.
That distinction is a judgment call, so it needs a judge.

## Scope

**In:** every `*.verified.*` baseline in the repo, treated uniformly. No
risk tiering — the cheap deterministic pre-filters below keep the cost down
without splitting the gate into two code paths.

**Out:** code quality, test design, and anything the arch tests or the
public-API approval test already enforce mechanically.

## Architecture

Three hooks plus one subagent. This is a direct port of the two-level
PostToolUse-ledger → Stop-gate pattern already debugged in the KROS
Invoicing repo, including its **generic hook names**: the gate is a registry
of judgment checks and the snapshot check is merely the first one
registered. A future check (e.g. security core) is added as a function in the
same process — **never** as a second Stop hook, because Claude Code runs an
event's hooks in parallel and cooperating logic that reads and clears one
ledger must live in a single process.

| File | Event | Job |
|---|---|---|
| `.claude/hooks/record-edited-paths` | PostToolUse `Edit\|Write\|MultiEdit\|Bash` | append to the per-turn ledger; never blocks |
| `.claude/hooks/turn-review-gate` | Stop | sole owner: read the ledger **once**, delete it **once**, run the checks, block with one aggregated reason |
| `.claude/hooks/reset-edited-paths` | SessionStart `startup\|clear` | delete a ledger orphaned by an ungraceful exit, so it cannot cause a spurious review on the next session's first turn |
| `.claude/agents/alvo-snapshot-judge.md` | — | the judge: `model: haiku`, `tools: Read, Grep, Bash` (read-only) |

Written in bash, matching this repo's `scripts/` and Husky tooling (the
source repo's hooks are PowerShell; that is a portability choice there, not a
convention to import here).

Wiring goes into `.claude/settings.json` alongside the existing
`SessionStart` → `scripts/session-orient` entry.

### The ledger's location and its filter

The ledger path is resolved with `git rev-parse --git-path edited-paths`, not
hardcoded to `.git/edited-paths`. This matters concretely here: this repo is
worked on through git worktrees, where `.git` is a file and the real git dir
lives elsewhere — a hardcoded path would put the recorder's ledger and the
gate's ledger in different places, or in a place shared across worktrees.

The PostToolUse `matcher` filters by **tool name only**, never by path, so
path scoping lives inside the recorder: it records a path only when it matches
`*.verified.*`, and otherwise records nothing (except the `ACCEPT` marker
below). This keeps the ledger small while the snapshot check is its only
consumer; a future check that needs other paths loosens this filter, and the
gate re-filters precisely for its own check anyway.

### Why the gate dispatches rather than judging itself

The gate blocks Stop with an instruction to invoke `alvo-snapshot-judge`; the
main agent performs the dispatch and sees the verdict in its own context.

The alternative — the Stop hook calling a headless `claude -p` judge and
blocking directly with the resulting verdict — was considered and rejected.
It would keep the main context clean on the happy path and make the gate
unskippable, but it is a new unproven mechanism, it depends on `claude -p`
being available inside the hook environment, and it is harder to debug.
Reusing the pattern that is already debugged wins.

## Detection contract

The two data sources answer two different questions and neither replaces the
other:

- **The ledger is the trigger.** It answers *"did anything actually happen
  in this run?"* Without it, a gate keyed on `git diff` fires on every turn
  of a pure conversation in which the agent changed nothing, because the
  uncommitted baseline change is still sitting in the working tree.
- **git answers *what*.** The ledger records paths only. "`canonical-complex-crm.verified.txt`
  changed" is not a judgeable fact; "`price` stopped being required" is.

Sequence in `turn-review-gate`:

1. Ledger missing → `exit 0`. **No conversation can ever trigger a judge.**
2. Read the ledger, then delete it immediately — single owner, so no sibling
   hook can race it. The drain is **unconditional**: it happens before any
   decision, including the `stop_hook_active` check below.
3. `stop_hook_active == true` → `exit 0` without deciding anything. The ledger
   is already drained, so a block-induced re-Stop cannot ping-pong, and nothing
   it recorded leaks into a later turn. This ordering is load-bearing — see
   below.
4. Entries empty → `exit 0`.
5. Candidates = ledger entries matching `*.verified.*`, plus — if the ledger
   contains an `ACCEPT` marker — every `*.verified.*` path reported by
   `git status --porcelain --untracked-files=all`.
6. Drop no-ops: a tracked candidate whose `git diff HEAD -- <file>` is empty
   is discarded. An untracked candidate is kept (that is a new baseline).
7. No candidates survive → `exit 0`, silently.
8. Otherwise emit a single `{"decision":"block","reason":...}` listing the
   surviving files and instructing the agent to invoke
   `alvo-snapshot-judge` and address its findings.

### The Bash accept path

`dotnet verify accept` and `cp *.received.txt *.verified.txt` produce **no
Edit event**, and they are the most natural way a baseline gets accepted —
so a recorder watching only `Edit|Write|MultiEdit` leaves the primary hole
open. The `Bash` matcher closes it: when `tool_input.command` matches
`verify.*accept` or a `cp`/`mv` involving `received`, the recorder writes an
`ACCEPT` marker rather than a path, because `tool_input.command` does not
reveal which baselines were accepted. Step 5 resolves the paths from git.

### Two consequences worth stating explicitly

**`git diff HEAD` is cumulative.** It also shows baseline changes from
earlier turns that are not yet committed. This is intended: the judge should
weigh the whole uncommitted baseline change, not its most recent fragment.
Per-run scoping stays on the *trigger*, not on the content.

**One judge pass per turn, and the ledger is always drained.** The tempting
ordering — check `stop_hook_active` first, drain the ledger second — is wrong,
and wrong in exactly the way this design exists to avoid. After a block the
agent fixes the code and regenerates the baseline; those edits land in a fresh
ledger. If the block-induced re-Stop exits at the guard without draining, they
survive into a later, unrelated turn and produce a block on a turn where nothing
happened.

So the drain is unconditional and `stop_hook_active` suppresses only the
decision. The cost is honest: the fix's own baseline edits are **not** re-judged
in that turn. That is acceptable — the fix was made under the judge's finding,
the next turn's edits are judged normally, and the PR-level gates still see the
committed baseline. Buying the re-check instead would mean a bounded-retry
marker in the git dir: more state and another branch to test, for a second
opinion on a change the judge already caused.

## The judge

`alvo-snapshot-judge` gathers its own inputs, in its own context:

1. The baselines named in the block reason, cross-checked against
   `git status --porcelain --untracked-files=all`.
2. Per baseline: `git diff HEAD -- <file>`; if untracked, read the file whole
   (a new baseline does not appear in a diff).
3. The accompanying source change: `git diff HEAD -- src/`, via `--stat`
   first when the diff is large.
4. Intent evidence: the newest `docs/superpowers/plans/*.md` (and its spec
   when the plan points at one). This is what separates *intended* behaviour
   changes from laundering, and it is the single biggest false-positive
   reducer in the design.

### The verdict is asymmetric and its trigger list is closed

`suspicious` may be returned **only** on one of these fingerprints:

- The baseline changed but there is **no change anywhere in `src/`** — the
  pure laundering fingerprint.
- A `PublicApi.*` baseline lost a member or type, or narrowed a signature,
  and neither the plan nor the spec mentions the break.
- **The baseline's content contradicts its own test's name** — e.g.
  `Add_column_sql_is_stable` now containing a `DROP`.
- Weakened semantics: required → optional, a validation error disappeared, a
  `negative-error-output` baseline now expects fewer errors.
- The baseline change is *broader* than the source change can plausibly
  explain — a one-line source edit against a wholly reshaped model.

Everything else is `ok`, and the prompt states it outright: **uncertainty
resolves to `ok`.**

The third fingerprint is deliberately phrased against the test name rather
than against the SQL. The naive rule "a destructive operation in a SQL
baseline is suspicious" would fire on
`Drop_column_sql_is_stable.verified.txt`, which legitimately contains
`DROP COLUMN` — 16 existing baselines sit in that blast radius. Relative to
the test's own name the check is sharp and nearly false-positive-free, and
trivial for a small model.

### Output

Per baseline: the verdict plus at most two sentences. One overall line at the
end. Nothing else — the bound is what stops a narrow judgment turning into a
review essay.

## False-positive controls

A blocking gate that cries wolf gets switched off within a week, so every
control below is load-bearing:

1. **Default-pass asymmetry** — positive evidence is required to flag;
   uncertainty resolves to `ok`.
2. **Closed fingerprint list** — the judge may not invent new grounds for
   suspicion.
3. **Plan/spec as intent evidence** — a change the plan called for does not
   get flagged.
4. **A new baseline for a new test is explicitly normal**, and is the most
   common legitimate case.
5. **Diff size cap** (~400 lines) — an oversized diff returns "not judged,
   review manually" instead of a guess.
6. **Bounded output** — two sentences per file.
7. **Silence on the happy path** — an empty ledger produces no output at all;
   the gate is invisible unless a baseline genuinely moved.
8. **The judge cannot write** — read-only tools, so it can raise a concern
   and nothing more.

## Where this sits among the existing gates

This is an inner-loop catch: it fires in the turn where the baseline moved,
when the fix is cheapest. Underneath it sit the arch tests and the public-API
approval test (deterministic, `dotnet test`), and above it `alvo-plan-guard`,
`/code-review`, `/security-review`, and the PR itself with CodeRabbit and
CodeQL. Because those backstops exist, this gate only has to catch obvious
laundering — which is exactly what licenses the default-pass bias.

It is **not** wired into ring0/1/2. The rings run `dotnet test`; hooks are a
different mechanism with a different trigger.

## Testing

`scripts/test-hooks` — plain bash, no new dependency — drives each hook with
crafted JSON payloads on stdin inside a temporary git repo:

- recorder: records a `*.verified.*` path; ignores an unrelated path; writes
  an `ACCEPT` marker for a matching Bash command and not for an unrelated one
- gate: empty/missing ledger → `exit 0` and no output; `stop_hook_active` →
  `exit 0`; a tracked no-op candidate is dropped; an untracked candidate is
  kept; `ACCEPT` marker resolves paths from git; the ledger is deleted after
  one read; multiple candidates produce exactly one block
- reset: deletes an orphaned ledger; creates no ledger when none exists;
  creates nothing at all outside a git repository (the early-return guard)

The judge's prompt cannot be unit tested. Two manual acceptance criteria go
with it instead:

- **Must fire:** revert a source change while keeping the accepted baseline,
  then finish a turn. The gate blocks, and the judge returns `suspicious`
  with the no-source-change fingerprint.
- **Must stay silent:** make a legitimate source change with a matching
  baseline update described in the active plan. The judge returns `ok`.

## Deliberately out of scope

- **Risk tiering** of baselines (strict for `PublicApi`/SQL, lax elsewhere) —
  the pre-filters make uniform treatment affordable, and one code path is
  worth more than the saved tokens.
- **A hook-invoked headless judge** — see "Why the gate dispatches" above.
- **A judged-content hash memo** — does not solve the two-pass case it would
  exist for.
- **Wiring into the rings** or into CI — the PR-level gates already cover
  committed baselines; this one is about catching the change as it happens.

## Known risks

- The judge reads the *newest* plan file, which may not be the plan the
  current work belongs to. The failure mode is a missing intent signal, which
  under default-pass yields `ok` — it degrades toward silence, not toward
  noise.
- The `ACCEPT` command patterns are a heuristic. A baseline accepted by some
  other means (an IDE diff tool, a script) is invisible to the recorder and
  falls through to the PR-level gates.
- Every hook must fail open. A hook that errors or times out must never block
  the turn, and must not silently pass either — it says that the check did
  not run.
