# Where MMLib.Alvo stands — handoff, 2026-08-05

Written before a three-week break. Facts here were verified at the time of writing, not
recalled. **Re-check anything time-sensitive before acting on it** — CI results, `BEHIND`
status and CodeRabbit comment counts all move.

`main` = `5515d73` (Merge pull request #159 from Burgyn/playground).

---

## 1. The two open PRs — both green, both need one thing before merge

| PR | branch | what it does | files | CI | CodeRabbit |
|---|---|---|---|---|---|
| **#160** | `f3/pr5b-before-hooks` | before-hooks in the write transaction + the `Mutate` CEL profile → **closes #114** | 44 | 11/11 pass | **reviewed**, 9 inline comments |
| **#163** | `f3/pr6-computed-rollup` | `computed` as a stored generated column, `rollup` as lock-then-recompute → **closes #21** | 61 | 11/11 pass | **reviewed**, 10 inline comments |

Both are `BEHIND` — `main` moved after they opened, so each needs *Update branch* (**merge, not
rebase**) plus a fresh CI cycle before merging.

**Why the file counts matter.** PR #157 carried 133 files and CodeRabbit skipped it outright
(*"133 files exceed the limit of 100"*), which is how a flaky fact and a MEDIUM in concurrency
SQL reached `main` with no outer-loop review. Both PRs above were deliberately split to stay
under 100, and both got a real review as a result. **Keep future PRs under 100 files.**

### Blocking, and only a human can do it

- **`/security-review` on #160 and #163.** Neither has had a real vulnerability scanner over it —
  reviewer subagents stood in and the PR bodies say so plainly. Both touch the security core
  (rule engine, tenancy, CEL), so pair it with the `alvo-security-core-review` checklist.
- **Add the `needs-deep-review` label to both.** `alvo-plan-guard` recommended it for each.

---

## 2. Unfinished work I was in the middle of: triage CodeRabbit

19 inline comments across the two PRs, **not yet triaged**. Four I had already singled out:

1. **#163 — `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlRollupRaceTests.cs:93`**
   *"Line 88 makes the commit assertion on line 90 unreachable."* **Do this one first.** If it
   holds, part of the rollup race fact is dead code — and that fact is the central correctness
   claim of #163, the one I reported as proven. It is exactly the vacuity class this repo hunts.
2. **#160 — `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.cs:158` (Major)**
   *"Deny `CelProfile.Mutate` at the renderer entry point."* PR5b's Task 2 refuses a `CelCall`
   node by name. If a `Mutate` expression can reach the renderer by another path, the
   interpreter-only *guarantee* has a hole, and that guarantee is what the whole profile rests on.
3. **#163 — `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreSchemaMigrator.cs:224` (Major, data
   integrity)** — in the migration path, so not ignorable.
4. **#160 — `src/MMLib.Alvo.Abstractions/Expressions/CelProfile.cs:49` (Major)** *"Use JSONata for
   `action.mutate` transforms."* **Reject this one, with the reason stated on the PR, not
   silently.** It contradicts Decision 2 of
   `docs/superpowers/specs/2026-08-02-f3-pr5-events-hooks-design-addendum.md`, which deferred
   JSONata entirely: there is no mature .NET implementation, and a hand-rolled subset would
   accept the part it implements and silently produce a different payload for the rest. That is a
   proposal to reopen a settled decision, not a defect.

The remaining 15 are Minor/quick-win (docs contradictions, a stale hook-point summary, a
whitespace-only-column theory case). Worth doing, none blocking.

---

## 3. What remains in F3

Milestone #4: **open 3, closed 9**.

| # | what | state |
|---|---|---|
| #114 | the six `entity.hooks.*` refusals | **closes with #160** |
| #21 | computed & rollup | **closes with #163** |
| #22 | event pipeline + lifecycle hooks | **stays open** — only its automation half is left |

**#22's remaining half is automation:** an ECA rule, a schedule trigger, and email. Its DoD is
*"an outbox crash test; a before-hook can reject/mutate; an after-hook runs post-commit; an ECA
rule + cron + email work."* PR5a delivered the outbox and after-hooks, #160 the before-hooks.
`IEmailSender` + a console sender already ship, so "basic email" is partly there; ECA and cron
are not started.

**`docs/PLAN.md`'s `← YOU ARE HERE` does not move.** `alvo-plan-guard` has confirmed this three
times: #21 closing is one line inside F3, not F3 itself, and `UnhonouredFeatures` still refuses
`validation`, `default` and `softDelete`.

### PR5b-2 — the piece deliberately held back from #160

Held back purely to keep #160 under 100 files. Scope, with its own acceptance bar already
written down in `docs/architecture/events.md`:

- **The wildcard subscription matcher.** `entity.orders.*` is a hard spec guarantee
  (`alvo-specifikacia.md:141`) with no matcher today. The bar is: every subscription **scoped to
  the envelope's tenant, with a named adversarial cross-tenant fact** — or refuse `*` at apply
  until that exists. Cross-tenant fan-out is otherwise the default failure mode.
- **The `Publish` namespace guard.** `Publish` must refuse a name matching
  `^(entity|auth|storage)\.` or a host can mint an event indistinguishable from a real data
  change. Verified it does **not** ship today (no `Publish` in any public-API baseline), so the
  forgery vector is unreachable — but it must be closed *before* `Publish` is added.
- The remaining three `crm.alvo.json` defects plus the strengthening of
  `Every_example_marked_not_runnable_really_is_refused`, which deviation 76 required in the PR
  lifting a `before*` refusal. Deferring was checked, not assumed: with all three `before*`
  entries gone the example is still refused by a **structured unhonoured-feature error**
  (`owner_id` declares `default`), not a CEL syntax error.

---

## 4. Issues filed while building, all open

| # | what |
|---|---|
| **#161** | A scoped `ref` may name a row in another tenant — the FK is built on `id` alone for *every* reference. Pre-existing and wider than PR6. With PR6's new tenant predicate such a child now aggregates **nowhere** instead of writing across the boundary, so the leak is contained but not fixed. Real fix: an FK spanning `(tenant_id, id)`, with its migration and destructive classification. |
| **#162** | A `decimal` computed field diverges per engine. **Measured:** EF Core 10's SQLite generator drops a computed column's type unconditionally (proved with a bogus `"ZZTOP"` type leaving the DDL byte-identical), and SQLite has no decimal arithmetic, so the loss is in the multiply, not the affinity. The fix is a new port member rounding to the declared scale — a PR, not a line. |
| **#158** | Pin the webhook URL non-leak on the refused-**connection** branch. Needs a real socket, so it belongs to the host integration suite. |
| **#152** | Per-endpoint field projection for webhook/email deliveries — today the envelope carries the unmasked record, `hidden` fields included. |
| **#155** | `email.to` is caller-controlled and unvalidated; CR/LF is SMTP header injection in any real sender. Inert today (only a console sender ships). |
| **#129** | CI runs a newer analyzer set than any dev machine, so a clean local build does not guarantee CI. `<AnalysisLevel>latest-recommended</AnalysisLevel>` plus `"rollForward": "latestFeature"` makes the enforced ruleset a function of the build machine. **This bit twice already** (CA1873, and CA1873 again). Four candidate fixes are on the issue; the decision is the maintainer's. |

---

## 5. Decisions waiting on you

- **`reject` answers 403** (`AlvoAuthorizationException`) in #160. For a business-rule refusal 422
  or 409 is arguably better, but that is a new public exception type, a sixth row in `IAlvoData`'s
  family table and an HTTP-mapping change. Flagged, not decided.
- **Deviation 53 cost (c)**, from the already-merged #147: under the default `Apply`, a descriptor
  rollback is an **unbootable deployment** — a forward deploy advances the applied snapshot,
  rolling back plans a `DropField`, the always-on destructive gate refuses it, and every pod exits
  78 in a crash loop. `docs/architecture/host.md` recommends `Verify` + a migration job for
  production and says plainly that this is **opt-out**.
- **The env names** `Alvo__Schema__Startup` / `Alvo__Schema__AllowDestructive`, which join
  deviation 39's set awaiting confirmation before the image publishes.
- **Drift under `Verify` fails the start** rather than starting un-ready. Recommend keeping it;
  revisit when #133 lands.

---

## 6. Local state — what is on this machine

```
/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo                  main, 2e7f46a  (STALE — pull)
/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-worktree/crud    detached, 2e7f46a  (STALE)
  .claude/worktrees/f3-pr5b     f3/pr5b-before-hooks   a46fce5   -> PR #160
  .claude/worktrees/f3-pr6      f3/pr6-computed-rollup  86f644e  -> PR #163
  .claude/worktrees/handoff     docs/vacation-handoff             -> this file
```

Both PR worktrees are **clean**. After the PRs merge, remove all four worktrees and both stale
checkouts need `pull --ff-only`. Note `main` is checked out in the first worktree, which is why
`gh pr merge --delete-branch` fails its local step (the server merge still succeeds) — delete the
remote branch by hand.

Also open: **#136** (dependabot, 5 packages). Merge it *after* the stack so CI cycles do not mix.

---

## 7. Process rules that cost something to learn

Keep these — each one was paid for.

- **One writing agent per worktree.** Read-only reviewers may overlap; two committing agents must
  not (a `git add -A` once swept another agent's files into a commit).
- **Assert the literal `Build succeeded`** before reading any test result. A broken build silently
  runs the old binary — this produced twelve false readings once.
- **State the mutation that proves each significant fact discriminates, and run it.** This session
  caught: a `lowerAscii` fold fact that passed under its own `ToLowerInvariant()` mutation
  (because `"İ".ToLowerInvariant()` is *unchanged* on .NET 10 — the plan and the addendum were
  both wrong, now corrected); a managed-column refusal that passed for the wrong reason; and a
  seed guard that let a stood-down boot through, making the whole race test vacuously green.
- **Investigate an unexpected green.** Twice this session a passing run was hiding a bug: the
  rollup race showed 40 of 40 until a 50 ms delay widened the window, and SQLite accepts
  `ADD COLUMN … STORED` on an **empty** table while refusing it on a non-empty one — so a
  fresh-fixture test passes while the only case that matters fails.
- **`.gitattributes` pins `*.cs` to CRLF.** Verify a mutation's edit landed with `git diff`, never
  with an LF search string, or a "green" run measured unmutated code.
- **Never hand-edit a `*.verified.*` baseline.** Let the tool rewrite it, then let
  `alvo-snapshot-judge` rule on it.
- **Commit after each item, and commit before mutating.** A `git checkout` to revert a mutation
  once destroyed uncommitted work.
- **EvalPlanQual has now bitten this codebase twice** — the outbox claim's outer `WHERE`, and the
  rollup's `SET` expression. Under `READ COMMITTED` PostgreSQL re-checks the outer predicate after
  granting a row lock and **nothing else**. Assume any read-modify-write in one statement is a
  lost update until measured otherwise.
- **The API dropped three subagents mid-task this session** (529 / connection closed). Each time
  the worktree was clean because of per-task commits. Keep that discipline.

---

## 8. Prompt to continue

Paste this:

> Resume MMLib.Alvo. Read `todo.md` on branch `docs/vacation-handoff` first — it is the handoff
> from before my break and it records the state, the open decisions and the process rules.
>
> Work **autonomously, without checking in**, in this order:
>
> 1. **Triage the CodeRabbit comments on PR #160 and PR #163** (19 inline, untriaged). Start with
>    `PostgreSqlRollupRaceTests.cs:93` on #163 — if the commit assertion really is unreachable,
>    part of #163's central correctness fact is dead and I need to know that before merging.
>    Then the two Majors (`SqlPredicateRenderer.cs:158` on #160,
>    `EfCoreSchemaMigrator.cs:224` on #163). **Reject the JSONata suggestion on
>    `CelProfile.cs:49` with the reason on the PR** — it contradicts Decision 2 of the events
>    addendum. Verify each finding against the code before acting; several CodeRabbit comments in
>    this repo have been right about the mechanism and wrong about the severity.
> 2. Once both PRs are triaged and green, tell me what to merge and in what order — **do not merge
>    anything yourself**, and remember both will need *Update branch* (merge, not rebase) first.
> 3. Then build **PR5b-2** (wildcard matcher scoped to the envelope's tenant with a named
>    adversarial cross-tenant fact — or refuse `*` at apply; the `Publish` namespace guard; the
>    remaining `crm.alvo.json` defects). **Keep it under 100 files** or CodeRabbit skips the review
>    entirely.
> 4. Then **#22's automation half** — ECA rule, schedule trigger, email — which is all that stands
>    between F3 and its close-out.
>
> Rules: never push or merge to `main`; branch → PR → I merge. One writing agent per worktree.
> Each PR in its own worktree. Dispatch `alvo-plan-guard` before opening any PR, and label
> reviewer subagents as substitutes in the PR body, because `/code-review` and `/security-review`
> are user-invocation-only here. State the mutation that proves each significant fact
> discriminates, and run it. Do not move `docs/PLAN.md`'s `← YOU ARE HERE` while #22 is open.
