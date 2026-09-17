# Every mutation shard at or above `break: 80`, honestly

**Status:** design · 2026-09-15
**Refs:** #238, #239, #241, #225, PR #240 (merged), PR #242 (open)

## The ask, and the two halves of it that pull against each other

> Keep the thresholds. Get everything to them or above them. Systematically. But do not
> generate pointless tests — they have to actually mean something.

Those two constraints conflict on purpose, and naming the conflict is most of this design.
`break: 80` is a ratio, so there are two ways to raise it: **kill more mutants**, or **put fewer
mutants in the denominator**. The first is the work; the second is the cheat — and it is the
exact cheat PR #240 was written to stop, where a timeout that detected nothing was counted as a
kill.

But the second lever is not always a cheat, and refusing to use it at all would force the first
constraint to break the second: some mutants **cannot be killed by any test**, so the only way to
"reach 80" on a shard carrying enough of them is to write assertions that pin nothing. That is
precisely the pointless test the ask forbids.

So this design's whole job is a rule for which lever applies to which mutant.

## The rule

Every surviving mutant is sorted into exactly one of four buckets, and the bucket decides the
action. The buckets are ordered by how objective they are, and a mutant is only allowed to fall
into a later bucket if it fails the earlier tests.

### 1. Equivalent — leaves the domain, with evidence

A mutant that **no possible test can distinguish from the original**. This is a recognised
category with an objective criterion, not a judgement call, and that is what makes removing it
honest rather than convenient.

Admissible evidence, one of:
- the mutated expression is provably semantically identical in this runtime (`ConfigureAwait(false)`
  vs `(true)` with no `SynchronizationContext`; `a ?? b` where one operand is always null);
- the mutated code is **unreachable** (a `switch` arm the caller has already narrowed away; a
  second `ThrowIfNull` on an argument guarded two lines earlier);
- the mutated value is a **default** (`ToTable(name)` where `name` is already EF's default).

Action: remove the *cause*, not the symptom. Delete unreachable code; use `ignore-methods` for a
whole class like `ConfigureAwait`. **Never** a blanket mutator exclusion, which would take real
mutants with it.

Measurement discipline: every domain change states the mutant count it is expected to remove, and
is verified against that number. Anything else means the exclusion caught more than it was aimed at.

### 2. Behavioural — gets a test

A mutant whose survival means a real fact about the product is unpinned. This is the bucket the
work lives in, and it is the default: **a mutant is behavioural unless it can be shown to belong
elsewhere.**

A test written for this bucket must pass the test-for-the-test:

> If this assertion were wrong, would a user, an agent, or an operator notice?

Concretely, it asserts a value, a decision, a refusal, a SQL token, a type, an order, or a
boundary — never a call count as a proxy for behaviour, never an implementation detail, never a
sentence.

### 3. Message prose — left alive, counted

A `String mutation` inside human-readable text whose content carries no contract: explanatory
sentences, advice paragraphs, the prose around a value.

Killing these means asserting English. The suite then breaks on the next rewording while proving
nothing, which is a worse outcome than an honest survivor — and Alvo's own contract is
**structured** errors, not exact text.

The line inside a message is not "prose vs not-prose", it is **which substring carries the
contract**: the field name, the column, the constant the author wrote, the SQL aggregate, the
parameter name. That substring IS asserted. The sentence around it is not.

Action: none. Counted and reported per shard.

### 4. Residue — reported, never faked

Anything that is not equivalent, not behavioural-and-worth-testing, and not prose. Expected to be
near zero. If a shard cannot reach 80 with buckets 1–3 honestly handled, **the shard is reported
below threshold with the arithmetic**, and the decision to move the threshold or accept the gap is
the maintainer's. It is not taken here, and it is not taken by writing bucket-3 assertions.

## Why not the alternatives

**Move `break` per shard to match measurement.** Rejected by the ask, and rightly: the threshold
was set when every score was a fake 100 %, so matching it to today's number would ratify whatever
the suite happens to be. #225 recorded the same argument.

**Move files between shards until the ratios work.** This is what PR #242 declined: shifting
`data-ef-rest`'s four worst files onto `data-ef-core` would have turned 88 survivors green by
buying a second test assembly rather than by detecting anything. The union's score does not move,
so nothing was gained and the expensive shard got slower. Re-partitioning is legitimate only on
measured evidence that a file's killers live elsewhere — and that evidence is now spent.

**`mutation-level: "Basic"`.** Drops the Standard mutators, which removes ~63 % of mutants on the
data shards. It would clear every threshold immediately and it is the purest form of the cheat: a
different measurement wearing the old one's number.

## Scope

Six scoring shards. The canary is exempt by construction — it must score 0 %, and check 5 enforces
that.

Baselines are being measured honestly (post-#240, `additional-timeout: 300000`) rather than read
from the pre-#240 runs, because those counted timeouts as kills. The plan is written against the
measured numbers, not the reported ones.

**All six measured 2026-09-15, post-#240.** Every honest number is worse than the reported one,
and two are dramatically worse.

| shard | tested | K / S / T | reported (pre-#240) | **honest** | kills needed |
|---|---|---|---|---|---|
| `data-ef-rest` | 649 | 557 / 92 / 0 | 54.51 % | **85.82 %** | done (PR #242) |
| `data-postgresql` | 20 | 15 / 5 / 0 | 75.00 % | **75.00 %** | **+1** |
| `data-sqlite` | 44 | 29 / 15 / 0 | 100.00 % | **65.91 %** | **+7** |
| `expressions` | 900 | 659 / 225 / 16 | 88.22 % | **75.00 %** | **+45** |
| `data-ef-core` | 459 | 262 / 196 / 1 | 99.82 % | **57.30 %** | **+105** |
| `rules, auth, rest` | 1707 | 888 / 819 / 0 | 55.34 % | **52.02 %** | **+478** |
| | | | | **total** | **+636** |

`expressions` is the surprise: 88.22 % → 75.00 %, because 52 of its 68 timeouts turned out to be
survivors once the timeout was raised. Its remaining **16 timeouts survive a 300 s ceiling**, which
is the signature of genuine endless loops — plausible in an expression evaluator, and the reason
check 6's ceiling was set at 20 % rather than 10 %.

### `rules, auth, rest` decomposed — three quarters of the work

| area | K | S | score | kills needed |
|---|---|---|---|---|
| Migrations | 121 | 231 | 34.4 % | +161 |
| Descriptor | 247 | 231 | 51.7 % | +136 |
| Events | 211 | 202 | 51.1 % | +120 |
| Rules | 202 | 117 | 63.3 % | +54 |
| Auth | 75 | 29 | 72.1 % | +9 |
| Internal | 32 | 6 | 84.2 % | — |

Three files carry 205 mutants and **2 kills** between them, which is the misassignment signature
rather than a debt signature. Probed against `MMLib.Alvo.Tests + MMLib.Alvo.Host.Tests`:

| file | killed by adding Host.Tests | still surviving |
|---|---|---|
| `Migrations/Internal/AlvoBootService.cs` | **63** | 64 |
| `Events/Internal/AlvoEventOptionsConfiguration.cs` | **17** | 22 |
| `Descriptor/Internal/ManagedColumnNames.cs` | 0 | **37** |

**Decision: port the coverage, do not add `Host.Tests` to the shard and do not split it.**
Adding the second assembly measures 26 s/mutant against ~6 s, which puts the 1707-mutant shard at
~185 min against a 120-minute budget, and splitting it would be the `data-ef` pattern applied to a
case that does not need it. `AlvoBootService` is an `IHostedLifecycleService`: its lifecycle
methods can be called directly from `MMLib.Alvo.Tests`, which already has
`Hosting.Abstractions` transitively — the core could not implement the interface otherwise. No host,
no package-boundary inversion. `Host.Tests` kills those 63 only because booting a host happens to
run them; that is not the same as needing a host.

`ManagedColumnNames.cs` is the opposite: the second assembly adds nothing, so its 37 are real debt,
and it is a Descriptor internal defining the managed column names — worth testing on its merits.

## Order of work, and why

1. **Bucket 1 first, everywhere** (#241). It is cheap, objective, and it changes the denominator —
   so doing it after the tests would invalidate every target computed before it. Expected effect
   is stated and verified: −36 `ConfigureAwait` mutants across the two EF shards, −1 dead arm.
2. **`data-postgresql`** — 5 survivors, all in one file, already enumerated in #225. Smallest
   distance to threshold in the repository.
3. **`data-sqlite`** — small shard (44 mutants), and its honest number is unknown but its floor is
   50 %. Few mutants means few tests.
4. **`data-ef-core`** — 221 survivors, 133 of them in `EfAlvoData.cs`. Needs its own decomposition
   before any test is written; the file is the provider's main surface and attacking it mutant by
   mutant is how a suite of pointless tests gets written.
5. **`rules, auth, rest`** — the largest, ~765 survivors over 1713 mutants, and it is the security
   core. Last because it is biggest, and because the discipline will be best rehearsed by then.
6. **`expressions`** — likely closest to threshold already; confirm, then close the gap.

## What success looks like

Every scoring shard at or above 80 on a locally measured, honest run, with:
- every domain change justified by bucket 1 and verified against a stated expected count;
- every new test justified by the test-for-the-test;
- the bucket-3 count reported per shard, so the residue is visible rather than hidden;
- no threshold moved, no file moved between shards, no mutator level lowered.

If a shard cannot get there under those constraints, the plan says so with numbers and stops —
that is a result, not a failure to finish.

---

## Addendum, 2026-09-17: the thresholds moved, and this is not the alternative rejected above

**The instruction changed.** The section "Why not the alternatives" rejects *"move `break` per shard
to match measurement"* on the maintainer's own constraint ("keep the thresholds"). That constraint
was lifted once every shard had been measured honestly:

> Ok keď dokončíš tak uprav prahy aby boli reálne a priprav už PR.

So the rejection above stands **as the reason not to move a threshold instead of doing the work**,
and it is not a reason not to move one *after* the work. The work is done first, and the numbers
below are what it produced.

### What the work produced

Five shards were raised by tests, not by arithmetic. No threshold was touched while they were being
raised, no file moved between shards, no mutator level lowered.

| shard | honest baseline (2026-09-15) | Stryker now | **Killed-only now** | by |
|---|---|---|---|---|
| `data-postgresql` | 75.00 % | 100.00 % | **100.00 %** | +5 kills |
| `expressions` | 75.00 % | 94.33 % | **92.33 %** | +172 kills |
| `data-sqlite` | 65.91 % | 86.36 % | **86.36 %** | +9 kills |
| `data-ef-rest` | 56.86 % | 85.82 % | **85.82 %** | +188 kills (PR #242) |
| `data-ef-core` | 57.30 % | 80.13 % | **79.91 %** | +104 kills |
| `rules, auth, rest` | 52.02 % | 57.88 % | **57.88 %** | +100 kills |

**Both columns, because this design's own subject forbids quoting only one.** Stryker scores
`(Killed + Timeout) / tested`; PR #240 exists because that counts a slow run as a detection. So the
honest headline is **four shards at or above 80, with `data-ef-core` 0.09 pp short on its single
remaining timeout** — not five. The two readings differ only on the two shards that have timeouts.

### Why the sixth shard did not reach 80, stated as arithmetic

`rules, auth, rest` is 1707 tested mutants, 988 killed, 719 survivors. Classifying every survivor
by whether the mutated string literal is **message prose** (bucket 3 — a sentence, no contract) or
anything else:

| area | K | S | prose | non-prose | score |
|---|---|---|---|---|---|
| Events | 211 | 202 | 70 | **132** | 51.1 % |
| Migrations | 219 | 133 | 36 | **97** | 62.2 % |
| Descriptor | 249 | 229 | 141 | **88** | 52.1 % |
| Rules | 202 | 117 | 44 | **73** | 63.3 % |
| Auth | 75 | 29 | 9 | **20** | 72.1 % |
| Internal + root | 32 | 9 | 0 | **9** | 78.0 % |
| **total** | **988** | **719** | **300** | **419** | **57.88 %** |

`break: 80` needs 1366 kills. That is **+378, out of 419 non-prose survivors — 90 % of everything
left that is not a sentence**, with no allowance for the equivalent mutants inside that 419 (the
bucket-1 class this design opens with, of which `ConfigureAwait` alone accounted for 36 on the two
EF shards). It is not arithmetically impossible; it is one shard's worth of work again, and it is
not what "the thresholds are realistic" means.

**Correction to an earlier claim of mine.** Before the `AlvoBootService` work landed I told the
maintainer this shard's 80 was *arithmetically unreachable* — 478 needed against 424 behavioural
survivors. That was true of the measurement it was computed from and is no longer true of this one:
the gap is now 378 against 419. The honest statement is the one above — reachable, and expensive —
not the stronger one.

### The rule the new thresholds follow

`break` stops being an ambition and becomes a **regression latch calibrated from measurement**:

> `break` = the **Killed-only** score minus the larger of **2 percentage points** or **2 mutants**,
> floored to a whole percent.

Killed-only rather than Stryker's own score, and not only for consistency with the paragraph above.
A margin computed from `(Killed + Timeout)` is partly *made of* the timeouts, so on a shard that has
any the headroom is not what it looks like. `expressions` is the case: 18 of its mutants are
timeouts sitting in Stryker's numerator, so a `break` of 92 taken from 94.33 % leaves 21 mutants of
apparent headroom of which 18 are those timeouts — **3 mutants of real room for new code**. Taken
from 92.33 % the threshold is 90 and the 21 are genuine. `data-ef-core` moves 78 → 77 for the same
reason on one mutant. The other four shards have no timeouts, so the two bases agree.

The margin is sized for *code churn* — one new unkilled line of product code — not for measurement
noise. That is why it is expressed in mutants on the small shards, where 2 pp is less than one
mutant, and in percent on the large ones.

`low` is the Killed-only score floored, so a shard's report goes amber the moment it drops below
what it measured today. `high` stays the **ambition**, which is where 80 now lives for the shard
under it — the goal did not move, only the gate did.

| config | Killed-only | break | low | high |
|---|---|---|---|---|
| `stryker-config.data-postgresql.json` | 100.00 % | 90 | 95 | 100 |
| `stryker-config.expressions.json` | 92.33 % | 90 | 92 | 96 |
| `stryker-config.data-sqlite.json` | 86.36 % | 81 | 86 | 90 |
| `stryker-config.data-ef-rest.json` | 85.82 % | 83 | 85 | 90 |
| `stryker-config.data-ef-core.json` | 79.91 % | 77 | 79 | 90 |
| `stryker-config.json` (rules, auth, rest) | 57.88 % | 55 | 57 | 80 |

**What the aggregate on `stryker-config.json` costs, stated rather than left to be discovered.**
That shard covers Rules, Auth, Events, Migrations and Descriptor — the rule engine and RBAC, the
code §0 principle 5 rests on — and at 1707 mutants a 2 pp margin is 34 kills of slack. `Auth` is
104 mutants with 75 killed, so a regression unpinning nearly half the authorization area stays
inside that slack and reports green. Two things bound the damage and neither makes it acceptable
indefinitely: the gate is post-merge and advisory, so the slack costs a *notification* rather than
a merge; and `low: 57` still takes the report off green on any real drop. The fix is to give `Auth`
— or `Auth` + `Rules` — its own shard. It is filed in #245 with the numbers rather than done here,
because splitting a shard in the same change that re-cuts every threshold is the
change-two-things-at-once mistake this design rejects elsewhere.

`stryker-config.api.json` is deliberately left at 90/85/80. It has no matrix leg (DECLARED GAP,
#143), so it has no measurement to calibrate against, and a calibrated-looking number there would be
the fabrication this whole design is about.

**Movement rule: up freely, down only with a written reason and a re-measurement it names.** A
threshold raised because a shard improved needs no ceremony. A threshold lowered is the gate
weakening, so it carries the log or report it was recomputed from, in the commit message.

### Why the provenance is not in the configs, though that was the plan

The intent was to record the date, the measured number and the run beside each threshold. **Probed,
and it does not work**, for two independent reasons — both verified rather than assumed:

- Stryker rejects an unknown key outright: *"The allowed keys for the `stryker-config` object are
  { … } but `_calibration` was found"*. It accepts `//` comments (JSONC), so that half would have
  worked;
- but `scripts/assert-mutation-run` reads each config's `test-projects` with **`jq`**, and
  `scripts/test-assert-mutation-run` reads three more `mutate` lists the same way. `jq` is strict
  JSON and fails on the first `//`. A comment in a config reddens the PR gate.

So the configs stay pure JSON carrying values only, and the calibration table, its date and its
source runs live in `.github/workflows/mutation.yml`'s header beside every other measurement this
gate is sized from.

### What is still owed

The `+378` is not written off. **#245** files it per area and per file, together with the same
prose/non-prose split for every other shard — **581 non-prose survivors, plus the 19 mutants that
still time out (18 on `expressions`, 1 on `data-ef-core`) and are therefore also undetected, for 600
in total** — so the next person picks up a bucket rather than a percentage.
