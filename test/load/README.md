# Load tests

Black-box HTTP load measurement of the Data API, driven by k6 against the field-service demo
stack over real PostgreSQL.

Design and every deviation from a source recommendation:
[`docs/superpowers/specs/2026-09-02-f4-pr-e-load-test-foundations-design.md`](../../docs/superpowers/specs/2026-09-02-f4-pr-e-load-test-foundations-design.md).
Published numbers: [`docs/performance.md`](../../docs/performance.md).

## Run it

Nothing to install beyond Docker, `jq`, `curl` and `openssl` — when `k6` is not on `PATH` the
harness runs the pinned `grafana/k6` image instead.

```bash
scripts/test-load                                # gate tier, ratios only
scripts/test-load --baseline-ref origin/main     # + the absolute A/B half
scripts/test-load --tier calibration             # the publishable numbers
scripts/test-load --keep                         # leave the stack up at :8091 afterwards
```

`--tier calibration --rows 1000000 --scenarios page_shallow,page_deep` is the 1 M-row keyset
variant; it seeds two million rows and takes a while.

## The two tiers

| | `gate` | `calibration` |
|---|---|---|
| When | on a PR touching the data path | on a `v*` tag, or `workflow_dispatch` |
| Rows | 10 k work orders per tenant | 100 k per tenant |
| Per scenario | 15 s | 60 s |
| Offered rate (list shapes) | 40 /s | 20 /s |
| Arms | merge base + HEAD, interleaved | HEAD only |
| Verdict | **pass/fail** | **publishes** (`--report-only`); never fails on a number |

The large tier's rate is **lower**, and that is correctness rather than timidity. Every
list-shaped scenario must see the same offered rate or the ratio between two of them becomes a
statement about the generator — so the rate has to suit the *heaviest* shape in the set, and at
200 000 rows `unindexed_filter` is a sequential scan. A rate that saturates it does not make that
one scenario slow; it makes the whole run **void**, and a voided thirteen-minute run tells you
nothing.

The gate tier is small on purpose. A lost index, an N+1, a policy resolved once more than before
— all of that is a *shape* regression, and shape is visible at 20 k rows long before it is at
200 k. Volume is what calibration is for.

## A ceiling is only valid for the tier it was measured on

The ratios **grow with row count** — `count_exact` costs 1.6× the reference list at 20 k rows and
3.0× at 200 k; `sort_nullable` goes 1.8× → 3.6×. That is arithmetic, not decay: as the database's
share of a request grows, the relative cost of extra database work grows with it because the fixed
per-request overhead stops dominating.

So every row in `baselines/gate.json` records `measuredOn: gate tier` and means it, and the
calibration tier runs the guard with `--report-only`: a ratio over its gate ceiling prints `over`
rather than `BREACH`, and the run still publishes. **Validity is enforced either way** — a void
run publishes garbage.

## The two kinds of judgement

**Ratios, within one run** — the sensitive half. Both halves of a ratio are measured on the same
machine minutes apart, so machine speed, runner generation and container overhead divide out.
A ratio needs no baseline arm and no absolute number in git: only a declared ceiling, which is a
statement about the feature rather than about the hardware. Every ratio is anchored on
`list_indexed`, the analysis's own headline shape.

**Absolute, across two arms** — the coarse half. Four scenarios have no meaningful denominator;
they are the floor. A regression fires only when HEAD is **both** a factor slower than the merge
base **and** slower by more than an absolute floor in milliseconds. Both conditions, because a
factor alone on a two-millisecond endpoint is noise, and a gate that fires on noise gets muted.

## The gating statistic is `min`, and p95 is reported but never gated

This was measured, not assumed. On the first real gate run every p95 landed within 8–9 ms of
every other — ratios of 0.97 to 1.03 across shapes that plainly do different amounts of work —
while `min` separated them cleanly:

| | min | p95 |
|---|---|---|
| `list_indexed` | 2.18 ms | 8.16 ms |
| `count_exact` | 3.57 ms | 7.90 ms |
| `sort_nullable` | 4.04 ms | 7.90 ms |

That measurement came from macOS with Docker Desktop, where every request pays the same VM and
container-network overhead and it swamps the difference between shapes. **On GitHub's
`ubuntu-latest` p95 does not degenerate** — its p95 ratios track the `min` ratios within ~10 %
(`count_exact` 1.87 against 1.90, `sort_nullable` 1.98 against 2.03).

**That is the argument for `min`, not against it.** `min` means the same thing on both rigs; p95
collapses on one of them, and a gate whose statistic behaves differently on the maintainer's
machine than in CI is a gate nobody can debug locally. `min` is also simply the right statistic
for the question the *gate* asks — "how much work does this shape do" — because the minimum is
service time with queueing and interference removed. p95 answers a different question, "what does
a caller experience under this load", and that is the number F4's definition of done asks to have
published. So it is measured, printed on every row, and published by the calibration tier. It just
does not gate.

**The gap this leaves, named rather than hidden:** a regression that leaves the fast path alone
and makes a small fraction of requests much slower — a new lock, a cache-miss branch — is not
caught here. `scripts/test-assert-load-baseline` pins that behaviour deliberately, so nobody
"fixes" it by accident.

## Adding a scenario — two edits, never a new harness

1. **`scenarios.js`**: add an entry to `CATALOGUE` and export the `exec` function it names. The
   catalogue is the single list of what exists — it drives the Trends, the scenario map and the
   `--scenarios` filter alike, so there is nowhere else to register anything.
2. **`baselines/gate.json`**: add a row expressing the new shape's cost as a **ratio against
   `list_indexed`**, and add the name to `GATE_SCENARIOS` in `scripts/test-load`.

A ratio against a fixed reference endpoint is the right unit because it survives every rig
change, states the design intent in the number itself ("embedding one relation may cost at most
2× a plain list"), and is meaningful on its first run with no historical data.

**Measure the ceiling on the rig that judges; never invent it, and never trust one rig.** Run the
tier twice, take the worst ratio you saw, and give it headroom — then record it in the row's
`observed` / `observedOnTheRunner` and `headroom` fields. The two arrays are separate because the
runner's ratios come out 15–30 % higher than a macOS laptop's, and ceilings set from laptop
numbers alone left `count_exact` 18 % of margin on the rig that actually gates. A ceiling
chosen at a desk is either so loose it gates nothing or so tight it fires on the first PR, and
there is no way to tell which without measuring.

Rows already foreseeable, each an open issue: aggregations (#109), relation embedding (#108),
`POST /query` (#107), bulk operations (#106), upsert (#105), rate limiting (#112), projection
pushdown (#117, where the number should drop *below* 1.0), native `NULLS FIRST/LAST` (#178, where
`sort_nullable` should collapse toward 1.0), and dynamic entities (#41 — the same scenarios over a
virtual entity against a physical one, which turns the analysis's *"identically over both"*
criterion into a number).

## What the harness refuses to measure

A run is **void, not slow**, and the guard exits 2 rather than 1, when:

- `dropped_iterations > 0` — k6 could not start iterations at the offered rate, so the generator
  and not the server was the bottleneck;
- `http_req_failed > 0` — a 500-storm has a flattering latency profile;
- no scenarios were recorded at all — which is exactly what a lazily declared k6 metric produces;
- the seed is not visible through the API. `scripts/test-load` reads the seeded set back with
  `Prefer: count=exact` and aborts before k6 starts unless the count matches. `seed.sql` writes the
  physical tables directly, so it knows a layout `DescriptorToSchemaMapper` owns; this is what
  stops that coupling rotting silently instead of producing an empty list with a spectacular p95;
- **the row predicate stopped filtering, or started refusing everything.** `row_policy` is a
  ratio, and a ratio can only ever reward a *cheaper* policy path — the cheapest possible row
  predicate is one that matches nothing. So the technician's set is asserted to be a strict subset
  of the dispatcher's before k6 starts: not empty, and smaller. Without that, a default-deny bug
  would make `row_policy` faster, drop its ratio, keep `http_req_failed` at 0 (an empty 200 list is
  not a failure) and publish an improvement for a broken rule engine;
- a declared baseline row was **not measured** at all, on a full gate run (`--strict`). A scenario
  that silently stops producing samples — a renamed catalogue entry, an `exec` throwing before its
  first `record()` — would otherwise print `not measured` and leave the gate green;
- the **baseline itself** has no `.ratios`/`.absolute` object. Both judgement loops read from a
  process substitution, which `set -e` cannot see into, so a one-letter typo used to judge nothing
  and print `ok`;
- a `min` of **zero** on either side of a ratio. An HTTP request cannot take zero time; a zero is a
  trend with no samples.

## What this rig cannot claim

The generator runs beside the server and its database. At a modest offered rate that is fine —
the server is idle between requests. At saturation it is not, and the number produced would be a
property of the machine. So there is **no throughput scenario here**, and
`baas-analyza.md:871`'s SQLite writes/s figure is deliberately not attempted; it needs a rig where
the generator is off-box.

## Files

| | |
|---|---|
| `scenarios.js` | the k6 script: one `exec` function and one `Trend` per shape |
| `seed.sql` | the bulk seed, parameterised by `:rows` |
| `baselines/gate.json` | ratio ceilings and A/B factors — the reviewable artifact |
| `docker-compose.load.yml` | the field-service stack with a pre-built image, so the A/B can swap arms |
| `../../scripts/test-load` | the driver: image, stack, seed, cursor walk, k6, verdict |
| `../../scripts/assert-load-baseline` | the verdict — the one authority on pass/fail |
| `../../scripts/test-assert-load-baseline` | the guard's own suite |
| `../../.github/workflows/load.yml` | `gate` on a PR, `calibration` on a tag, `notify` |

`baselines/gate.json` is a moved-goalpost surface: a red gate can be made green by editing it.
`.claude/hooks/turn-review-gate` therefore treats it as a judged baseline and dispatches
`alvo-snapshot-judge` when it moves, the same way it does for a `*.verified.*` snapshot. Move a
ceiling only in the same PR as the change that caused it.
