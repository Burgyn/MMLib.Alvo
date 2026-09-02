# PR-E — load-test foundations (implementation plan)

> **Status: executed.** Every step below is checked because this plan was written alongside the
> work rather than ahead of it — the harness had to be *run* before its numbers could be chosen, and
> three of the design's decisions were overturned by what the first runs said (slice 3 step 7,
> slice 5 step 2, slice 6 step 7). What the checkboxes record is therefore the order a reader should
> reconstruct it in, and the failures worth causing on purpose along the way, not work still to do.
>
> For a future agent replaying it: use `superpowers:executing-plans` slice by slice.

**Goal:** Give Alvo standing HTTP load instrumentation — a per-PR regression gate and a
per-release calibration run — so F4's "p95 latencies measured and published" is satisfied and the
six filed-but-unquantified performance claims arrive with numbers attached.

**Architecture:** k6, invoked as an external process, against the existing field-service demo
stack over real PostgreSQL. Two kinds of verdict: **ratios within one run** (machine-independent,
the sensitive half) and an **absolute A/B** between the PR's merge base and its HEAD, interleaved
in one job. Pass/fail lives in one testable shell guard, never in k6 thresholds. Two tiers: a
small `gate` tier per PR and a large `calibration` tier per release tag.

**Tech Stack:** k6 v2.2.0 (pinned) · bash + `jq` · Docker Compose · PostgreSQL 16 · GitHub Actions

**Spec:** `docs/superpowers/specs/2026-09-02-f4-pr-e-load-test-foundations-design.md`

Branch: `f4/pr-e-load-test-foundations`. No issue existed; the work is named by F4's own
definition of done (`alvo-specifikacia.md:337`). Two follow-up issues are filed rather than
delivered (slice 7).

## Global Constraints

- **k6 is a CI tool invoked as a separate process, never a `PackageReference`.** Its AGPL-3.0
  licence therefore places no obligation on Alvo's Apache-2.0 core. **NBomber is refused** — v5+
  is closed-source under the NBomber License Agreement and requires a paid licence for
  organisational use, and it would be in-process. Design §2.1–2.2.
- **The gating statistic is `min`; p95 is measured, printed and published but never gated.**
  This was measured, not chosen — design §5.0 and slice 3 step 7's evidence.
- **A ceiling is only valid for the tier it was measured on.** The ratios grow with row count, so
  the calibration tier reports (`--report-only`) and only the gate tier judges — design §5.3 and
  slice 6 step 7's evidence. Validity is enforced on both.
- **Every ceiling in `test/load/baselines/gate.json` is measured, not chosen**, and records the
  observations it came from. No invented number ships.
- **Nothing is added to `MMLib.Alvo.slnx`.** No `.csproj` appears: CLAUDE.md's *"do not create
  projects ahead of time"*, and `test/teapie` is the in-repo precedent for a non-.NET suite under
  `test/`.
- **The load harness is in no ring.** Same reason `scripts/test-e2e` is in none: the rings are
  `dotnet test` tiers and this builds an image and stands up a multi-service stack.
- **Target framework / SDK unchanged.** This PR adds no C# and touches no `.csproj`.
- **The gate ships advisory, not required.** Promoting it is the maintainer's call.

---

## Slice 1 — the seed, and the guard that stops its coupling rotting

The calibration tier needs 200 000 work orders. `AlvoDataSeed` is `internal` to the EF package,
in-process, and routes every row through the change tracker, so it is unreachable from a
black-box harness and wrong for bulk regardless. The seed is therefore SQL against the physical
tables — which means it knows a layout `DescriptorToSchemaMapper` owns, and that coupling has to
fail loudly rather than silently.

- [x] **Step 1: Read the real physical schema, do not infer it.** Start the field-service stack
  and look:

```bash
docker compose --env-file examples/field-service/demo-identities.env \
  -f docker-compose.field-service.yml up --wait
docker exec alvo-field-service-postgres-1 \
  psql -U alvo -d alvo_field_service -c '\d work_orders' -c '\d customers' -c '\d regions'
```

  Facts this establishes, each of which the seed depends on: `work_orders` carries the audit
  quartet and `tenant_id`; `customers` carries only `tenant_id` (it is unaudited by descriptor);
  `regions` carries neither (`tenancy: global`); the unique index is
  `(tenant_id, reference)`, **not** `reference` alone (#137's fix), so references may repeat
  across tenants; `IX_work_orders_status_priority` and `IX_work_orders_assigned_to` both exist,
  which is what makes the reference list and the row predicate index-servable.

  **The secrets must be 32 characters** (#125's entropy floor) or the host refuses to start with
  *"has a Secret of N characters; at least 32 are required"*. `openssl rand -hex 16` produces
  exactly that.

- [x] **Step 2: Create `test/load/seed.sql`.** Parameterised by `psql -v` on `rows`, `customers`,
  `tenant_north`, `tenant_south`, `tech_north`. One `TRUNCATE … CASCADE`, then three set-based
  inserts over `generate_series`, then `ANALYZE` on all three tables.

  Identifiers are derived from the row ordinal (`('33333333-' || … || lpad(to_hex(n),12,'0'))::uuid`)
  rather than `gen_random_uuid()`, so two runs of one tier produce byte-identical data and a
  difference between two measurements cannot be the data.

  The distributions exist so each scenario has something to measure: `status` cycles the four
  enum values (`status=eq.scheduled` matches ~25 %); `priority` is 1–5, the second index term;
  `scheduled_for` is NULL for ~30 % so ordering by it exercises the `CASE` rank (#178);
  `assigned_to` names the tenant-north technician on ~10 % of north's rows so that caller's list
  is a genuine indexed subset rather than empty; `is_emergency` is true for ~5 %.

  Write the audit quartet exactly as `AlvoAuditStamp.Applied` would on a create — all four
  columns, `updated_at` equal to `created_at`.

- [x] **Step 3: Run it, and expect the reference collision the first time.**

```bash
docker exec -i alvo-field-service-postgres-1 psql -U alvo -d alvo_field_service \
  -v rows=10000 -v customers=500 \
  -v tenant_north=7e9a1c00-0001-4000-8000-00000000000a \
  -v tenant_south=7e9a1c00-0002-4000-8000-00000000000b \
  -v tech_north=0a51c4e1-0002-4000-8000-000000000002 \
  -f - < test/load/seed.sql
```

  Expected on a naive per-tenant offset: `duplicate key value violates unique constraint
  "IX_work_orders_tenant_id_reference"`. **`lpad` truncates a value longer than its width**, so an
  offset that pushes the ordinal past eight digits collapses every row onto one reference. Fix:
  `(t.ordinal - 1) * 50000000 + n`, which keeps both tenants inside eight digits even in the 1 M
  variant, and matches the descriptor's `work-order-ref` format (`WO-[0-9]{4,8}`).

  Expected after the fix: `INSERT 0 8`, `INSERT 0 1000`, `INSERT 0 20000`, in under a second.

- [x] **Step 4: Verify every scenario's URL against the running API before writing any of them.**
  Nine `curl` calls, and each one is a fact the design rests on:

```bash
K="dispatcher-north.$SECRET"; B=http://127.0.0.1:8081
curl -s -D- -H "X-Alvo-Api-Key: $K" -H 'Prefer: count=exact' "$B/api/work_orders?limit=1"
curl -s -H "X-Alvo-Api-Key: $K" "$B/api/work_orders?status=eq.scheduled&order=priority.asc&limit=50"
curl -s -H "X-Alvo-Api-Key: $K" "$B/api/work_orders?status=eq.scheduled&order=scheduled_for.asc&limit=50"
curl -s -H "X-Alvo-Api-Key: $K" "$B/api/work_orders?status=eq.scheduled&order=priority.asc,reference.asc&limit=200"
curl -s -H "X-Alvo-Api-Key: $KTECH" -H 'Prefer: count=exact' "$B/api/work_orders?status=eq.scheduled&order=priority.asc&limit=50"
```

  Expected: `Preference-Applied: count=exact` and `count: 10000` (tenant-scoped, so north's
  10 000 and not 20 000); a full page of 50; the nullable sort **accepted** (#116 landed, and the
  descriptor's own field comment saying a paged read "may NOT sort by it" is stale); a non-null
  opaque `next` on the two-term sort; and `count: 500` for the technician — 10 % assigned × 25 %
  scheduled of 10 000, which is the row predicate working rather than an empty refusal.

  Also POST one work order to pin the create body: `tenant_id`, `reference`, `title`, `status`,
  `priority`, `access_code`, `customer_id`, `region_id`. Expected `201`.

- [x] **Step 5: Commit.**

```bash
git add test/load/seed.sql
git commit -m "test(load): the bulk seed, and why it reads itself back through the API"
```

---

## Slice 2 — the k6 scenarios

- [x] **Step 1: Create `test/load/scenarios.js`.** One `exec` function and one `Trend` per shape.
  Three properties the file exists to hold, each easy to lose by accident:

  1. `constant-arrival-rate`, never a looping VU pool. A closed model sends less when the server
     slows, so percentiles *improve* as the server sickens — coordinated omission.
  2. Scenarios run **sequentially**, staggered by `startTime`. Two in flight contend and neither
     one's latency is attributable.
  3. Every list-shaped scenario is offered the **same** rate, or the ratio between two of them
     becomes a statement about the generator.

  Plus a 10-second **unrecorded** warm-up scenario, so JIT, the EF model cache, the connection
  pool and PostgreSQL's plan cache are warm before the first observation.

- [x] **Step 2: Declare every `Trend` at module scope, driven off `CATALOGUE`.**

```javascript
const trends = {};
for (const scenario of CATALOGUE) {
    trends[scenario.name] = new Trend(`alvo_${scenario.name}`, true);
}
```

  This is not a style choice. k6 refuses `new Metric()` outside the init context —
  `GoError: metrics must be declared in the init context` — so a lazily created trend throws on
  every iteration of every scenario and the run records **nothing**. `CATALOGUE` is the single
  list of what exists: it drives the trends, the scenario map and the `--scenarios` filter alike,
  so there is nowhere else to register a scenario and no second list to fall out of step.

- [x] **Step 3: Give every request a bounded `timeout: '10s'`.** k6's default is 60 s, and a
  request that stalls for a minute would be recorded as latency when what it is is a failure.

- [x] **Step 4: Write `handleSummary(data)`** returning `{ '<OUT_DIR>/<name>': JSON.stringify(…) }`
  — the current k6 mechanism; `--summary-export` is not used. Emit the per-scenario trend values
  under `scenarios`, plus the three numbers that decide whether the run is valid at all:
  `http_req_failed`, `dropped_iterations`, `iterations`.

- [x] **Step 5: Set no k6 `thresholds`.** A ratio between two trends is not a k6 metric and the
  A/B spans two runs, so splitting the verdict between k6 and a script would give one decision
  two authorities.

- [x] **Step 6: Syntax-check and commit.**

```bash
node --check test/load/scenarios.js
git add test/load/scenarios.js test/load/docker-compose.load.yml
git commit -m "test(load): k6 scenarios, one Trend per shape, open-model arrivals"
```

  `test/load/docker-compose.load.yml` belongs to this commit: it overrides the field-service
  `alvo` service to run a **pre-built** image with `pull_policy: never`, which is what lets the
  A/B swap arms by recreating one service while PostgreSQL and its rows stay put.

---

## Slice 3 — the driver, and the measurement that changed the design

- [x] **Step 1: Create `scripts/test-load`.** Arguments `--tier gate|calibration`,
  `--baseline-ref REF`, `--rows N`, `--scenarios LIST`, `--reps N`, `--keep`. Its own compose
  project (`alvo-load`) and its own port (8091), so a running e2e stack and playground are
  untouched.

- [x] **Step 2: Export `ALVO_FS_PORT` *after* sourcing `demo-identities.env`, not before.** That
  file sets `ALVO_FS_PORT=8081`, and `set -a` sourcing overwrites an exported value rather than
  deferring to it. Getting this backwards puts the load stack on the e2e port; the symptom is
  `curl: (7) Failed to connect to 127.0.0.1 port 8091`, on a stack that started cleanly.

- [x] **Step 3: Add `assert_seed_is_visible`.** Read the seeded set back through the **public
  API** with `Prefer: count=exact` and abort unless the count equals what was inserted; then
  assert the reference list returns a full page of 50. This is what stops the seed's coupling to
  the physical layout rotting silently — an empty result set would otherwise report a spectacular
  p95 for a list of nothing, which is this design's single most likely failure mode.

- [x] **Step 4: Add `deep_cursor`.** #100 is about how **deep the cursor sits**, not how many sort
  terms there are — its own evidence is rows-removed-by-filter growing one-for-one with depth
  (280 001 at depth 280 000). So the deep cursor is *earned* by walking to the last page in an
  **unmeasured** phase at `limit=200` (the API maximum, four times cheaper than the measured page
  size), on the same two-term sort `page_shallow` uses.

- [x] **Step 5: Make k6 optional.** Prefer a `k6` on `PATH`; otherwise run the **pinned**
  `grafana/k6:2.2.0` image on the compose network. Pinned rather than `latest` because the
  generator is part of the instrument: a k6 release that changes iteration scheduling would show
  up as a latency change in a table whose whole purpose is attributing latency changes to Alvo.
  Warn when a local k6 is a different version.

- [x] **Step 6: Run the gate tier and expect it to record nothing the first time.**

```bash
scripts/test-load --tier gate --keep
```

  Expected on a lazily created trend: pages of
  `GoError: metrics must be declared in the init context`, then the guard refusing the run with
  *"recorded no scenarios — the run measured nothing"*. **That is slice 2 step 2's failing test**,
  and the guard catching it is slice 4's, both observed before either fix existed.

- [x] **Step 7: Run it again and read the numbers, because they change the design.** Expected
  after the fix — and this is the measurement that matters:

| Scenario | min | p95 |
|---|---|---|
| `read_by_id` | 1.14 | 4.91 |
| `list_indexed` | 2.18 | 8.16 |
| `count_exact` | 3.57 | 7.90 |
| `sort_nullable` | 4.04 | 7.90 |
| `page_deep` | 2.75 | 8.53 |
| `row_policy` | 2.97 | 8.37 |
| `select_projection` | 2.13 | 8.04 |

  **Every p95 is within 8–9 ms of every other**, so the p95 ratios come out 0.97–1.03 across
  shapes that plainly do different amounts of work, while `min` separates them cleanly. At gate
  volume p95 is dominated by scheduling and container-network jitter, not by the query. **The
  gating statistic must therefore be `min`.** p95 stays measured, printed and published — it is
  the number F4's DoD asks for — but it does not gate. Record the gap this leaves out loud: a
  tail-only regression is not caught.

- [x] **Step 8: Commit.**

```bash
git add scripts/test-load
git commit -m "test(load): the driver — stack, seed, earned cursor, k6 with no install step"
```

---

## Slice 4 — the verdict, and its own suite

- [x] **Step 1: Create `scripts/assert-load-baseline`.** A pure function from JSON to an exit
  code: no Docker, no k6. `--baseline`, repeatable `--head` and `--base`, optional `--verdict`.

  **Exit 2 means "could not judge"; exit 1 means "judged, and it failed".** Conflating them would
  let a broken harness read as a clean bill of health, which is the failure mode the mutation gate
  exists to stop (#142, #71).

- [x] **Step 2: Make a void run void.** Refuse with exit 2 on `dropped_iterations > 0` (the
  generator, not the server, was the bottleneck), `http_req_failed > 0` (a 500-storm has a
  flattering latency profile), no scenarios recorded, a malformed or missing summary, or a zero
  denominator.

- [x] **Step 3: Take the minimum across repetitions.** Interference is one-sided — a noisy
  neighbour or a co-resident generator can only make a request slower — so the smallest
  observation is the best estimate, and averaging would fold the interference into the answer.

- [x] **Step 4: Require BOTH conditions for an absolute breach.** `min(HEAD) > min(base) × factor`
  **and** `min(HEAD) − min(base) > floorMs`. A factor alone on a two-millisecond endpoint is
  noise, and a gate that fires on noise is muted within a month.

- [x] **Step 5: Guard against empty-array expansion under `set -u`.**

```bash
for file in ${BASE_FILES[@]+"${BASE_FILES[@]}"}; do assert_run_is_valid "$file"; done
```

  Bash treats an empty `"${a[@]}"` as an unbound variable, so a ratio-only run (no `--base`)
  aborts with `BASE_FILES[@]: unbound variable` instead of doing its job. Observed.

- [x] **Step 6: Write `scripts/test-assert-load-baseline` — 22 cases, synthetic summaries.**
  Synthetic rather than captured (unlike the mutation suite's real fixtures) because the point is
  to drive the arithmetic through exact values, which real k6 output cannot do; what makes them
  trustworthy is that their *shape* is copied from `artifacts/load/head-1.json`.

  The cases that carry the design, each of which must be present:
  - `min` decides even when p95 is flat — the slice 3 step 7 regression, pinned;
  - a p95 blow-out with a flat `min` does **not** fail — the named gap, pinned so nobody
    "fixes" it by accident;
  - factor exceeded but under the floor **passes**;
  - floor exceeded but under the factor **passes**;
  - a noisy rep beside a clean one does not fail;
  - each of the five void conditions exits **2**, not 1.

- [x] **Step 7: Run the suite.**

```bash
scripts/test-assert-load-baseline
```

  Expected: `22 passed, 0 failed` at this point (29 after slice 6 step 7a).

- [x] **Step 8: Commit.**

```bash
git add scripts/assert-load-baseline scripts/test-assert-load-baseline
git commit -m "test(load): one authority for the verdict, gated on min, with its own suite"
```

---

## Slice 5 — the baseline file, measured twice

- [x] **Step 1: Run the full A/B against `main` to exercise the path that has no other test.**

```bash
scripts/test-load --tier gate --baseline-ref main --reps 2
```

  This validates the worktree checkout, the second image build, the arm swap and the
  interleaving. Because the branch changes no `src/`, both images behave identically, so the
  absolute factors **should** come out near 1.0.

- [x] **Step 2: Read the A/B result as evidence about the thresholds, not just as a pass.**
  Expected: `read_by_id` 0.86, `list_indexed` 0.89, `create` 0.96 — and **`openapi` 1.48 on
  identical code**. Across two interleaved arms of the same image, `min` varied by up to 1.5×
  (`read_by_id` 0.71–1.07 ms, `list_indexed` 1.57–2.27 ms, `openapi` 2.44–3.97 ms).

  Two conclusions, both of which go into the file:
  - a default factor of 1.4 would have fired on noise; **1.8** sits above the observed spread and
    still catches anything that doubles;
  - at 20 k rows every absolute latency is small, so the **floor** is usually what decides. That
    makes the absolute half a coarse tripwire and the ratios the sensitive instrument — which is
    the design's own division, now with evidence. State it in the file.

- [x] **Step 3: Write `test/load/baselines/gate.json` from the worst of the two runs.** Each
  ratio row carries `over`, `max`, `observed` (the array of what was actually seen), `measuredOn`,
  `headroom` and `why`. Ceilings: `count_exact` 2.5 (observed 1.64, 1.54), `page_deep` 2.2
  (1.18, 1.04 — wider headroom because #100's growth is documented as sublinear but *unbounded*
  and the depth reached scales with the tier), `sort_nullable` 2.8 (1.85, 1.76), `row_policy` 2.4
  (1.36, 1.57), `select_projection` 1.3 (0.98, 0.85 — a tripwire against the value going *up*).

  `select_projection` is **recorded, not defended**: `select` is applied to the response, not the
  `SELECT` list, so ~1.0 is correct today and proves nothing (#117). It is the instrument that
  will demonstrate the fix when the number drops below 1.0.

- [x] **Step 4: Re-run the guard against the committed baseline.**

```bash
scripts/assert-load-baseline --baseline test/load/baselines/gate.json \
  --head artifacts/load/head-1.json --head artifacts/load/head-2.json
```

  Expected: every row `ok`, and `row_policy` visibly *not* near its ceiling any more (it sat at
  1.57 against a provisional 1.6 before this slice — the reason the ceiling moved to 2.4).

- [x] **Step 5: Commit.**

```bash
git add test/load/baselines/gate.json
git commit -m "test(load): the gate's ceilings, measured across two runs rather than chosen"
```

---

## Slice 6 — CI, the judged-baseline gate, and the published numbers

- [x] **Step 1: Create `.github/workflows/load.yml`** with three jobs: `gate` on
  `pull_request` (paths-filtered to `src/`, `schema/`, `examples/field-service/`, `test/load/`,
  the two scripts, the compose file, `Directory.Packages.props`, the workflow itself), `calibration`
  on `push: tags: v*` plus `workflow_dispatch`, and `notify`.

  The gate needs `fetch-depth: 0` — the A/B builds an image from
  `github.event.pull_request.base.sha`, which is what *"did this change make it slower"* means; a
  fixed `main` would charge a week-old branch for everything merged since.

  Install the pinned k6 binary rather than using the image: on a runner the published port is on
  the host's loopback, and a local binary removes the `host.docker.internal` hop from the
  measurement entirely.

- [x] **Step 2: Make `notify` fire on `always() && result != 'success'`, not `failure()`.** A job
  killed by `timeout-minutes` ends `cancelled`, so `failure()` is false for the one outcome that
  matters most — a run that produced no numbers and told nobody. This is #98's lesson, already
  recorded at length in `mutation.yml`.

  Say in the issue body **what a red calibration means**: the tier never fails on a measurement,
  so it is always the harness — a build, `up --wait`, a seed that no longer matches the schema, a
  void run, or a timeout.

- [x] **Step 3: Do not commit results from CI.** Pushing to `main` from CI is banned outright, so
  the calibration job builds `artifacts/load/calibration.md`, writes it to `$GITHUB_STEP_SUMMARY`
  and uploads it; a normal PR lands the row in `docs/performance.md`.

- [x] **Step 4: Teach the ledger and the Stop gate about load baselines.** Widen
  `.claude/hooks/record-edited-paths`'s scope filter to `test/load/baselines/*.json`, and add a
  `check_load_baselines` function to `.claude/hooks/turn-review-gate`, registered in `checks`.

  **A function in the existing hook, never a second Stop hook** — an event's hooks run in
  parallel and a sibling would race the ledger drain. That rule is already written in
  `turn-review-gate`'s own header; this slice is the first time it is exercised.

  The reason it belongs here at all: `assert-load-baseline` reads that file, so **raising a
  ceiling is the one edit that turns a red gate green without touching product code** — the same
  hazard a `*.verified.*` snapshot carries, and nothing mechanical can tell an intended cost from
  a silenced regression.

- [x] **Step 5: Extend `alvo-snapshot-judge` to the second baseline kind.** Add two closed-list
  fingerprints — a ceiling raised with no source change to buy it, and a `max` moved without its
  `observed` array moving too — and one explicitly normal case: a ceiling coming **down** (which
  is what #178's fix looks like).

- [x] **Step 6: Extend `scripts/test-hooks`.** Add a committed `test/load/baselines/gate.json` to
  `setup_repo`, then: the recorder records a load baseline; the recorder **ignores**
  `test/load/scenarios.js` (the scope is the baselines directory, not every JSON under
  `test/load` — the scenarios are code, and code is reviewed, not judged); a changed load
  baseline blocks and the reason names the judge; an unchanged one does not; and a Verify and a
  load baseline together still produce **exactly one** block naming both halves.

```bash
scripts/test-hooks
```

  Expected: `34 passed, 0 failed`.

- [x] **Step 7: Run the calibration tier — and expect the gate to fail on it.**

```bash
scripts/test-load --tier calibration
```

  Two things go wrong here, and both are worth causing on purpose.

  **First, do not edit `scripts/test-load` while it is running.** Bash reads a script
  incrementally, so an edit that shifts byte offsets corrupts the running interpretation; the
  symptom is `scripts/test-load: line 323: unexpected EOF while looking for matching '"'` after
  thirteen minutes of measurement, and the run is lost.

  **Second, the run's ratios breach every gate ceiling**, and that is the design bug this step
  exists to find:

| Ratio | gate tier (20 k) | calibration (200 k) | gate ceiling |
|---|---|---|---|
| `count_exact` | 1.54–1.64× | **3.02×** | 2.5 |
| `sort_nullable` | 1.76–1.85× | **3.57×** | 2.8 |
| `row_policy` | 1.36–1.57× | **2.82×** | 2.4 |
| `page_deep` | 1.04–1.18× | **2.46×** | 2.2 |

  **The ratios grow with row count**, because as the database's share of a request grows the
  relative cost of extra database work grows with it. So a ceiling is only valid for the tier it
  was measured on, and the calibration tier must **report**, not judge — which is what the design's
  own tier table always said (*"never fails on a number"*) and what the implementation got wrong.

- [x] **Step 7a: Add `--report-only` to the guard, and pass it for any tier but `gate`.** A ratio
  over its ceiling prints `over` instead of `BREACH` and the run exits 0. **Validity stays
  enforced** — a void run publishes garbage, and garbage in `docs/performance.md` is worse than no
  row.

  **Do not implement it as `verdict="$(breach_verdict)"`.** Command substitution runs the function
  in a **subshell**, so an assignment to `FAILED` inside it is discarded and the guard exits 0 on a
  real breach — fail-open, in the one script whose entire job is not to. Set a global instead
  (`mark_breach; verdict="$BREACH_LABEL"`), and add the case that pins it:

```bash
assert_rejected_because "the same input still FAILS without --report-only" 1 BREACH \
    --baseline "$BASELINE" --head "$TMP/ratio-breach.json"
```

  Expected after this slice: `29 passed, 0 failed`.

- [x] **Step 7b: Lower the calibration tier's offered rate to 20/s for list shapes.** §5.1 requires
  a uniform rate across list shapes, so the rate must suit the heaviest one — and at 200 k rows
  `unindexed_filter` is a sequential scan. A rate that saturates it does not make that scenario
  slow, it makes the whole run **void**.

- [x] **Step 7c: Write `docs/performance.md` from the result**, naming the rig, the row count, the
  k6 version and the tier in the section heading itself. The headline: **`list_indexed` p95 =
  15.6 ms at 100 k rows per tenant on an indexed column, against `baas-analyza.md:142`'s 50 ms
  bar** — met by a factor of three. Publish the ratio table too, because the ratios are the part
  that survives a change of machine.

- [x] **Step 8: Write `test/load/README.md`** — how to run it, the two tiers, the two kinds of
  judgement, why the gating statistic is `min` (with the table), the **two edits** that add a
  scenario, and what the rig cannot claim.

- [x] **Step 9: Record the licensing ruling where a future author will look for it.** Add to the
  `alvo-dotnet-conventions` skill: the bans apply to **dependencies of a shipped package**, not to
  a development or CI tool invoked as a separate process. Name NBomber as refused on both grounds
  (licence *and* in-process), and name the tools already in the CI-only category — Stryker,
  TeaPie, Vacuum, Husky.Net, the `postgres:16-alpine` image.

- [x] **Step 10: Commit.**

```bash
git add .github/workflows/load.yml .claude/hooks/ .claude/agents/alvo-snapshot-judge.md \
        scripts/test-hooks test/load/README.md docs/performance.md \
        .claude/skills/alvo-dotnet-conventions/SKILL.md CHANGELOG.md
git commit -m "ci(load): gate on a PR, calibration on a tag, and a judged load baseline"
```

---

## Slice 7 — what is filed rather than delivered

- [x] **Step 1: File the SQLite writes/s issue — #187.** `baas-analyza.md:871` asks for a published
  writes/s figure at which the framework recommends a server engine. It is a **throughput** claim,
  and design §4 shows throughput is not credible with a co-resident generator — the number would
  be a property of the runner. Needs a rig where the generator is off-box.

- [x] **Step 2: File the in-process micro-benchmark issue — #188.** BenchmarkDotNet over CEL compile,
  filter parse, SQL render and policy resolve — the *"which layer regressed"* follow-up, worth
  building once the HTTP gate has fired in anger at least once. A second harness with a second
  result format and a second baseline is scope this PR has not earned.

- [x] **Step 3: Do not close #100, #117, #178 or #179.** This PR *quantifies* them; it fixes none
  of them. Each gets the measured ratio recorded so the fix arrives with a before-and-after.

---

---

## Slice 8 — what review changed, and it was not cosmetic

Three reviews ran before the PR opened: `alvo-snapshot-judge` on the new baseline (**ok**),
`alvo-plan-guard` on the whole diff (**ISSUES**, five), and a stand-in for `/code-review medium`
over the shell/JS/SQL (**twelve findings**, several reproduced rather than reasoned about). The
fixes below are the ones that changed behaviour rather than prose, and every one is pinned by a
test.

- [x] **The `row_policy` ratio was unfalsifiable, and it is the security-core row.** A ratio can
  only reward a *cheaper* policy path, and the cheapest row predicate is one that matches nothing —
  so a default-deny bug would make it faster, drop the ratio, keep `http_req_failed` at 0 (an empty
  200 list is not a failure) and publish an improvement for a broken rule engine. Fix:
  `assert_seed_is_visible` now asserts the technician's set is a **strict subset** of the
  dispatcher's — non-empty *and* smaller — before k6 starts. Verified by nulling every
  `assigned_to` on a live stack and confirming the count went 1000 → 0, i.e. the `die` fires.

- [x] **Three fail-open paths in the guard, all reproduced.** (1) Both judgement loops read from
  `done < <(jq …)`, which `set -e` and `pipefail` cannot see into — a one-letter typo in `.ratios`
  judged nothing and printed `ok` past a 30× breach. (2) A row that resolved to `not measured`
  could not fail, so a scenario that silently stopped producing samples turned the gate green.
  (3) A `min` of `0` on the numerator printed `0.00 … ok`. Fixes: validate the baseline's shape and
  row count up front; add `--strict` (passed only for a *complete* gate run) so an unmeasured
  declared row is exit 2; reject a zero on either side of a ratio. Eleven new cases; the suite goes
  22 → **40**.

- [x] **A truncated flag exited 1 with no message**, and exit 1 is the documented *"judged, and it
  failed"* — so a typo read to CI as a real regression. `needs_value` before every `shift 2`, in
  both scripts.

- [x] **`--rows` on a gate run enforced tier-bound ceilings at the wrong volume.** This was a
  regression introduced *while fixing* plan-guard's issue 4: honouring `rows` in the gate job made
  `--tier gate --rows 200000` enforce a 2.5× `count_exact` ceiling against a dataset measured at
  3.0×, a guaranteed false BREACH. Fix: a row-count override disarms the ceilings (`--report-only`).

- [x] **The baseline arm was built with HEAD's Dockerfile.** `build_image` hardcoded
  `$ROOT/src/.../Dockerfile` while passing the base worktree as the context, and the workflow
  triggers on `src/**` — which includes that Dockerfile. A PR touching the build would have
  compared two different builds while looking valid. The Dockerfile now comes from the context.

- [x] **`use_image` recreated PostgreSQL too**, contradicting the comment that justifies the whole
  arm-swap design ("both arms measure the identical dataset"). Data survived only by anonymous-volume
  reuse. Added `--no-deps`.

- [x] **The containerised k6 could not write its summary on Linux.** The image runs as uid 12345
  against a bind mount owned by the invoking user, so `handleSummary` got EACCES, the run produced
  nothing, and the guard died twelve minutes later — failing exactly for the maintainer-on-Linux
  case the fallback exists to serve. Added `--user "$(id -u):$(id -g)"`.

- [x] **The scenario boundary had no slack.** An iteration in flight when a graceful window closes
  is aborted, an aborted request is a failed request, and any failed request voids the run — with a
  10 s timeout inside a 2 s window. Fix: timeout 4 s inside a 5 s window, staggered so no two
  scenarios overlap. The margin is now stated from measurement: the slowest single request across
  every run to date was 132 ms.

- [x] **`page_deep` could measure a partial page.** The walk stops at the cursor yielding the last
  page, which holds `matching mod 200` rows — freely chosen `--rows` can leave fewer than the 50 the
  measured scenario asks for, at which point it serialises less than `page_shallow` and flatters
  itself. Fix: step back one page when the deep page is not full.

- [x] **`--reps 0` aborted with `unbound variable`** after all the Docker work. Validated at parse
  time, along with `--rows`.

- [x] **Two comments overstated what the code does** — `seededId()` claims to spread over the table
  where it walks a `rate x DURATION` window in order, and `use_image` claimed PostgreSQL stayed put.
  Both now say what is true; the first states the trade (identical across arms, warm rather than
  cold) instead of implying a property it does not have.

- [x] **Housekeeping from plan-guard:** the licensing subsection was splitting a bullet list and
  burying the outbox hard rule (moved after the list); `docs/performance.md`'s ratio table did not
  say it is computed on `min`, so a reader dividing the bolded p95 column got a contradictory
  number; the plan's checkboxes were all unchecked on finished work; `row_policy`'s baseline row now
  records that moving *that* ceiling carries `needs-deep-review`.

- [x] **The `observed` arrays now list every run, not the first two.** Four runs of identical code
  gave `count_exact` 1.17–1.65 and `row_policy` 0.98–1.57, and that spread is the evidence for why
  each ceiling sits ~1.5× above the *worst* observation rather than snugly above the average.

## Self-review against the spec

| Spec section | Where it lands |
|---|---|
| §2 tool, §2.1 NBomber refused, §2.2 AGPL ruling | Global constraints; slice 6 step 9 |
| §3 layer + the descriptor's fitness | Slice 1 step 1 |
| §4 rig, §4.1 load model and void runs | Slice 2 steps 1/3; slice 4 step 2 |
| §5.1 ratios, and `min` over p95 | Slice 3 step 7; slice 4 steps 1–3; slice 5 |
| §5.2 absolute A/B, both conditions | Slice 4 step 4; slice 5 steps 1–2 |
| §6 two tiers | Slice 3 step 1; slice 6 steps 1, 7 |
| §6.1 seeding and its API-side guard | Slice 1; slice 3 step 3 |
| §7 file layout, one verdict authority | Slices 3, 4 |
| §7.1 k6 with no install step | Slice 3 step 5 |
| §7.2 baseline as a judged surface | Slice 6 steps 4–6 |
| §8 tracking over time | Slice 6 steps 3, 7 |
| §9 scope, what is filed | Slice 7 |
| §10 growth mechanism | Slice 6 step 8 (README's "two edits") |
| §11 deviations | Recorded in the design; the ones with code are slices 3 step 7, 4 step 4, 5 step 3 |
| §12 acceptance criteria | Slice 4 step 7, slice 6 step 6, slice 6 step 7 |

**Type/name consistency:** the scenario names in `CATALOGUE` (slice 2), `GATE_SCENARIOS` /
`ABSOLUTE_SCENARIOS` (slice 3), the `ratios` and `absolute` keys in `baselines/gate.json`
(slice 5) and the trend prefix `alvo_` stripped by `handleSummary` (slice 2 step 4) are one set
of strings; a name added in one place and not the others reads as *"not measured"* in the guard's
table rather than failing, so slice 6 step 8's README states the two-edit rule explicitly.

**No spec requirement is left without a slice.** The two that are deliberately *not* implemented
(§9's SQLite throughput and the micro-benchmark layer) have slice 7 filing them instead.
