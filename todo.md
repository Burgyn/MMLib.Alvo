# Where MMLib.Alvo stands — 2026-08-27

Rewritten after the three-week break. The previous handoff (2026-08-05) is in this file's
history; everything below was re-verified today, not recalled. **Re-check anything
time-sensitive before acting on it.**

`main` = `5515d73`. It has not moved since 4 August — nothing merged during the break.

---

## 1. Merge order, and it is not the old one

**A dependency advisory landed during the break and it blocks everything.** `SSH.NET`
2025.1.0 gained a high-severity advisory ([GHSA-q939-rpr3-3284]) on 2026-08-23; it is
transitive through Testcontainers, and `NuGetAudit` + `TreatWarningsAsErrors` turns it into
**NU1903** on the two integration projects. So `main` itself would fail CI today, and both
feature PRs are red for that reason and no other.

1. **#169** — dependabot, minor+patch group of 16. **Fully green**; the only non-success is a
   pending `license/cla`. It bumps `Testcontainers.PostgreSql` to 4.14.0, which depends on the
   patched `SSH.NET` 2026.0.0, so it is the unblocker. Verified locally on top of #163: build
   clean, `dotnet format` clean, ring0 green at 2658, ring2 green.
2. **#160** (`f3/pr5b-before-hooks`, closes #114) — *Update branch*, then merge.
3. **#163** (`f3/pr6-computed-rollup`, closes #21) — *Update branch*, then merge.
4. **#164** is the older, superseded group; **#167** (xunit.v3.extensibility.core 4.0) and
   **#170** (Roslynator 5.0) are majors, both `BLOCKED`, both worth their own cycle.

**#160 before #163, and the second one will conflict.** The two branches overlap in 11 files,
including three Verify baselines (`UnhonouredFeaturesTests.Both_unhonoured_tables_are_pinned`
and two `PublicApi.*`). Whichever merges second must let the tool regenerate them and let
`alvo-snapshot-judge` rule — never hand-edit.

[GHSA-q939-rpr3-3284]: https://github.com/advisories/GHSA-q939-rpr3-3284

---

## 2. Both PRs are triaged — 19 threads, 17 fixed, 2 declined

Every finding was verified against the code before acting. Full per-thread replies are on the
PRs; a summary comment sits on each. What matters here:

- **The old handoff's priority was wrong.** It said to check
  `PostgreSqlRollupRaceTests.cs:93` first, fearing part of #163's central correctness fact was
  dead code. It is not — lines 90/91 assert the rollup's own value and both were reachable.
  The real defect there is narrower (a rethrow hid the assertion that distinguishes a fix from
  failing the writers).
- **The real vacuity was elsewhere, and nobody had flagged it.** `RollupLadderTests` asserted
  `ShouldContain("gross_total")` where the offending field is `net_total`; it passed only
  because the refusal's closing prose quotes the schema's static
  `gross_total = net_total + vat_total` example.
- **Two genuine holes, both measured rather than argued.** The `Mutate` profile's
  interpreter-only guarantee did not hold — `Render(Mutate("true"))` returned `TRUE`; and the
  before-hook architecture scan passed a pipeline call placed after `CommitAsync` (old scan: 9
  tests, 0 failures on that source).
- **A third: the depth-cap theory row never reached a tree.** It was refused by the
  source-length cap (2397 > 2000 chars), so `MaxTreeDepth` had no fact covering it at all.
- **Declined, both with the reason on the PR:** the JSONata redesign on `CelProfile.cs:49`
  (contradicts Decision 2 of the events addendum; tracked as #149), and the per-phase hook
  consequence rewrite on #163 (#160 deletes those exact lines).

### Still owed, and only you can do it

- **`/security-review` on #160 and #163.** User-invocation-only here; reviewer subagents stood
  in and both PR bodies say so plainly. Pair with `alvo-security-core-review`.
- `needs-deep-review` is **already labelled** on both.

---

## 3. What remains in F3

Milestone #4: open 3 — #114 (closes with #160), #21 (closes with #163), **#22 stays open**.

`alvo-plan-guard` confirmed again today that **`docs/PLAN.md`'s `← YOU ARE HERE` does not
move** while #22 is open.

- **PR5b-2** — the wildcard subscription matcher scoped to the envelope's tenant with a named
  adversarial cross-tenant fact (or refuse `*` at apply); the `Publish` namespace guard
  (`Publish` does not ship yet, so the forgery vector is unreachable — close it *before* it
  does); the remaining three `crm.alvo.json` defects and the strengthening of
  `Every_example_marked_not_runnable_really_is_refused`. Keep it **under 100 files**.
- **#22's automation half** — an ECA rule, a schedule trigger, email. `IEmailSender` and a
  console sender already ship; ECA and cron are not started.

---

## 4. Decisions still waiting on you

Unchanged from the previous handoff, none of them touched today:

- **`reject` answers 403** (`AlvoAuthorizationException`). 422 or 409 may be better for a
  business-rule refusal, but that is a new public exception type, a sixth row in `IAlvoData`'s
  family table and an HTTP-mapping change.
- **Deviation 53 cost (c)** — under the default `Apply`, a descriptor rollback is an unbootable
  deployment. `docs/architecture/host.md` recommends `Verify` + a migration job for production
  and says plainly that this is opt-out.
- **The env names** `Alvo__Schema__Startup` / `Alvo__Schema__AllowDestructive`.
- **Drift under `Verify` fails the start.** Recommend keeping it; revisit when #133 lands.
- **#129** — CI runs a newer analyzer set than any dev machine. Four candidate fixes are on the
  issue. Note #170 (Roslynator 5.0) will make this concrete.

Issues filed while building are all still open: #161, #162, #158, #152, #155. Sixty-odd open
issues in total, most of them deliberately deferred F3 follow-ups.

---

## 5. Local state

```
/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo                  main, 5515d73
/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-worktree/crud    detached, 2e7f46a (STALE)
  .claude/worktrees/f3-pr5b     f3/pr5b-before-hooks   1cd3a99  -> PR #160
  .claude/worktrees/f3-pr6      f3/pr6-computed-rollup ae1a2ea  -> PR #163
  .claude/worktrees/handoff     docs/vacation-handoff            -> this file
```

All three PR worktrees are clean and pushed. After the stack merges, remove all four worktrees
and `pull --ff-only` the stale checkout. `main` is checked out in the first path, which is why
`gh pr merge --delete-branch` fails its local step (the server merge still succeeds) — delete
the remote branch by hand.

---

## 6. Process rules that cost something to learn

The previous handoff's list still holds in full. Three of its rules paid for themselves again
today, and one is new.

- **Assert the literal `Build succeeded` before reading any test result.** Paid off within the
  hour: a mutation that dropped an interpolation tripped `IDE0060`, the build failed, the test
  ran against the old binary and reported a false green.
- **An analyzer can refuse your mutation.** `RCS1215` rejected an always-false guard condition,
  so "make the condition never fire" is not always an available mutation — delete the arm
  instead.
- **State the mutation that proves each fact discriminates, and run it.** Every fact added
  today has one, and running them is what exposed the depth-cap row measuring the wrong cap.
- **Verify baselines: let the tool rewrite them, then let `alvo-snapshot-judge` rule.** Two
  moved today; both ruled ok.
- **New: `HUSKY=0` does not skip these hooks** — use `git commit --no-verify` when the hook is
  blocked by something unrelated to the diff, and say so in the commit message.
- **New: writing a file with `utf-8-sig` adds a BOM to files that had none.** `alvo-plan-guard`
  caught one in a Markdown file; only C# carries a BOM in this repo.

---

## 7. Prompt to continue

> Resume MMLib.Alvo. Read `todo.md` on branch `docs/vacation-handoff` first.
>
> Work autonomously, in this order:
>
> 1. Nothing can go green until **#169** merges — say so and stop if it has not.
> 2. After #169 and the two feature PRs are in, build **PR5b-2** (wildcard matcher scoped to
>    the envelope's tenant with a named adversarial cross-tenant fact, or refuse `*` at apply;
>    the `Publish` namespace guard; the remaining `crm.alvo.json` defects). Under 100 files.
> 3. Then **#22's automation half** — ECA rule, schedule trigger, email — which is all that
>    stands between F3 and its close-out.
>
> Rules: never push or merge to `main`; branch → PR → I merge. One writing agent per worktree,
> each PR in its own. Dispatch `alvo-plan-guard` before opening any PR and label reviewer
> subagents as substitutes in the PR body. State the mutation that proves each significant fact
> discriminates, and run it. Do not move `docs/PLAN.md`'s `← YOU ARE HERE` while #22 is open.
