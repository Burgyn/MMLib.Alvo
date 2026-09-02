# PR-E — load-test foundations: what we measure, on what rig, and what makes a number a gate

*Design, 2026-09-02. F4. No issue existed for this; the work is named by the phase's own
definition of done.*

## 0. Why now, and what the sources actually ask for

F4's definition of done, in the spec's own words (`alvo-specifikacia.md:337`):

> …crash test outboxu (kill process → žiadny stratený event); **p95 latencie zmerané a
> publikované** (kalibrácia akceptačných kritérií z analýzy).

The analysis puts numbers behind that. Two of them are in scope for a Data-API load test:

| Source | The criterion, verbatim | Status today |
|---|---|---|
| `baas-analyza.md:142` | *p95 latencia filtrovaného listu nad 100k riadkov (indexovaný stĺpec) < 50 ms lokálne; keyset pagination stabilná nad 1M riadkov* | **unmeasured** |
| `baas-analyza.md:871` | *Concurrent write benchmark na SQLite dáva konkrétne, publikované číslo (zápisov/s), pri ktorom framework odporúča prechod na server engine* | **unmeasured, and deferred — see §9** |

And `baas-analyza.md:1116` sets the disposition to read them with:

> **Akceptačné kritériá v §2–§3 sú návrh latky**, nie priemyselná norma — čísla (latencie,
> limity) kalibruj vlastnými benchmarkmi na cieľovom hardvéri.

So the numbers are a bar to *calibrate*, not a constant to assert. That distinction shapes
the whole design: the published number and the CI gate are two different artifacts with two
different rigs, and conflating them is what makes performance gates flaky and then ignored.

### 0.1 The second reason: six open issues are unmeasured performance claims

The Data API already carries a set of filed, documented, *unquantified* costs. Each is a
sentence in a doc or an issue that says "this is slower" with no number beside it:

| Issue | The claim | Where it is documented |
|---|---|---|
| **#100** | per-page cost grows with cursor depth on a multi-term sort | `data-api.md:298` |
| **#178** | a nullable sort key emits a `CASE` rank that no index can serve | `data-api.md:157` |
| **#179** | `Prefer: count=exact` is a second statement with no operator control | `data-api.md:320` |
| **#117** | `select` is applied to the response, so it costs the database a full read | `data-api.md`, PR-D |
| **#118** | the list endpoint resolves the policy twice | PR-D measured it: 2×, not 3× |
| **#126** | the OpenAPI document did O(N²) name scans (**fixed** in PR-D) | PR-D |

PR-D established the house habit here — *measure before you design* — and paid for it: three
of its four issues had moved since filing. This PR generalises that from a one-off probe into
standing instrumentation, so the next such claim arrives with a number attached and #126's
regression would be caught rather than re-discovered.

## 1. Decisions, up front

| Decision | Choice | §|
|---|---|---|
| Tool | **k6**, invoked as an external process (never a `PackageReference`) | §2 |
| NBomber | **Rejected on licence**, and the rejection is a hard rule | §2.1 |
| Layer | **HTTP black-box** against the running `docker-compose.field-service.yml` stack | §3 |
| Rig | **GitHub-hosted runners**; generator co-resident with the server | §4 |
| Load model | `constant-arrival-rate` (open model), fixed rate well below saturation | §4.1 |
| Gating statistic | **`min`** (service time). p95 is measured and published, never gated | §5.0 |
| Gate metric, sensitive half | **Ratios within one run** — self-normalising, no baseline arm | §5.1 |
| Gate metric, absolute half | **A/B: merge-base vs HEAD, interleaved in one job** | §5.2 |
| Tiers | `gate` (20 k rows, per PR) and `calibration` (200 k / 1 M rows, per release) | §6 |
| Pass/fail authority | **One script** (`scripts/assert-load-baseline`), with its own suite | §7 |
| Trend tracking | `docs/performance.md`, one row per release tag. No hosted store | §8 |

## 2. The tool, and the licence ruling it forces

k6 is chosen for four reasons, in order of weight:

1. **`constant-arrival-rate`** — an open-model executor. Without one, a load generator that
   loops a fixed pool of virtual users measures its own back-pressure: when the server slows,
   the generator sends less, and the recorded percentiles improve. That is *coordinated
   omission*, and it is the single most common way a load test reports a healthy p95 for a
   sick server. k6 has the correct executor as a first-class option.
2. **Per-metric custom `Trend`s** and `handleSummary(data)`, which writes an arbitrary JSON
   map to files — so each scenario's percentiles come out cleanly separated, in a shape a
   guard script can read.
3. **§0 principle 4 (agent-first).** Alvo deliberately adopts formats an agent recognises
   from training data — PostgREST's query grammar, CloudEvents, Standard Webhooks. A k6
   script is in that same category: an agent asked to add a scenario in six months will
   recognise the file without reading a bespoke harness first.
4. It runs as a **single static binary or an official image**, so `scripts/test-load` can
   work on a fresh machine with no install step (§7.1).

### 2.1 NBomber is refused, and the reason generalises

NBomber v5+ is distributed under the **NBomber License Agreement** (v2.0, effective 2024-05-01;
v3.0 current) — closed source, free for personal use only, **a paid licence for any use inside
an organisation**. Its GitHub repository carries no `LICENSE` file and the GitHub API reports
`license: null`.

That is the same shape as the two bans already in `alvo-dotnet-conventions`: MediatR
(commercial from April 2025) and FluentAssertions v8+. NBomber v4 remains Apache-2.0, but
pinning a frozen v4 is the move the conventions skill already rejects in as many words —
*"Older FluentAssertions versions aren't the fix either."*

Critically, NBomber would be a `PackageReference` in a test project, loaded **in-process**. So
it fails on two independent grounds, and only one of them is about distribution.

### 2.2 k6 is AGPL-3.0, and that is not a problem — here is the exact reasoning

k6 is AGPL-3.0. The obligation attaches to the k6 *program* and to works derived from it.
Alvo:

- does not modify k6;
- does not distribute k6, in any package or image it ships;
- does not offer k6 to users over a network (AGPL §13's trigger);
- invokes k6 as a **separate executable**, handing it a script as input.

A k6 script is input to the k6 runtime in the same sense a Python script is input to CPython
or a JMeter test plan is input to JMeter. Running a program over input does not make the input
a derivative work of the program.

**The ruling this establishes, and it belongs in `alvo-dotnet-conventions`:**

> The licence bans apply to **dependencies of a shipped package** — anything an embedding host
> would acquire transitively. They do not apply to a **development or CI tool invoked as a
> separate process**, which no consumer of `MMLib.Alvo.*` ever receives.

That rule is not invented for k6; it is the rule the repository already follows without having
written it down. Stryker.NET, TeaPie, Vacuum, Husky.Net and the `postgres:16-alpine` image are
all in that category today.

### 2.3 What was considered and rejected

| Candidate | Licence | Why not |
|---|---|---|
| **vegeta** / **oha** / **bombardier** | MIT | Correct licences, correct open model. But no scenarios, no per-endpoint tagging, no threshold logic — with ten scenarios we would be writing our own smaller, worse k6 in bash. Kept in reserve if k6's licence position ever changes. |
| **dotnet/crank** | MIT | Microsoft's own harness behind the ASP.NET Core benchmarks, and the closest thing to .NET-native prior art. Its model is *"benchmark a job I start on an agent"*, needing a `crank-agent` topology; ours is *"benchmark a compose stack that already exists"*, which `scripts/test-e2e` already stands up. Rejected as machinery we would have to bend, not use. |
| **Gatling**, **JMeter**, **Locust** | Apache-2.0 / MIT | Heavier runtimes (JVM, Python) for no capability we need. |
| A hand-written `HttpClient` driver in xUnit | n/a | Percentile estimation, warm-up, and coordinated omission are solved problems. Re-solving them is a defect, not a saving. |

## 3. The layer, and what the demo descriptor already gives us for free

HTTP black-box only, against `docker-compose.field-service.yml`. No `BenchmarkDotNet` layer in
this PR — the acceptance criteria are about API latency, not about how long CEL takes to
compile, and a second harness with a second result format and a second baseline is scope this
PR has not earned.

The field-service descriptor turns out to be an almost purpose-built load-test fixture, and
that is why no new example is added. Verified against the running stack rather than assumed:

```
work_orders   IX_work_orders_status_priority   btree (status, priority)   -- an index-servable filter+sort
              IX_work_orders_assigned_to       btree (assigned_to)        -- an index-servable row predicate
              IX_work_orders_tenant_id_reference UNIQUE (tenant_id, reference)
              priority  bigint  NOT NULL   -- a required sort key: no CASE rank
              scheduled_for timestamptz NULL -- a nullable sort key: CASE rank, #178
              is_emergency  boolean NULL     -- an unindexed filter
```

- `work_orders.rules.list` is `'dispatcher' in @user.roles || 'admin' in @user.roles ||
  assigned_to == @user.id`. The **same URL** answered by `dispatcher-north` carries no row
  predicate; answered by `tech-north` it carries `assigned_to == @user.id`. That is a
  controlled measurement of the rule engine's cost in the hot path with one variable changed.
- `tenancy: scoped` on `work_orders`/`customers`, `global` on `regions` — the tenant predicate
  is present or absent by entity, not by configuration.
- Five dev keys already exist covering both tenants and three role shapes.
- `audit: true` on `work_orders` means the write path carries the audit stamp and mints an
  `ETag`; `customers` is unaudited, so the two write paths differ by exactly that.

## 4. The rig, and what it can and cannot honestly claim

The load generator runs on the **same GitHub-hosted runner** as the Alvo container and its
PostgreSQL — four cores between all three. This is a real constraint and the design states its
consequences rather than producing numbers that quietly depend on ignoring it:

| Claim | Credible on this rig? | Why |
|---|---|---|
| **Service time** (`min`) at a fixed, modest arrival rate | **Yes** | At a rate far below saturation the server is idle between requests, and the minimum is the observation with the least queueing and interference in it. |
| **Ratio** between two such measurements | **Yes, and best of all** | Both terms pay the same contention, so it cancels. This is why §5.1 is the sensitive half of the gate. |
| **p95** at a fixed, modest arrival rate | **Publishable, not gateable** | Measured honestly, and it is the number the sources ask to have published — but §5.0 shows it is dominated by jitter at this volume, so it cannot decide anything. |
| **Maximum throughput** (requests/s, writes/s) | **No** | At saturation the generator competes with the server for the same four cores; the number produced is a property of the runner, not of Alvo. |

The `baas-analyza.md:871` SQLite writes/s criterion is therefore **not delivered here** (§9).

### 4.1 The load model, and the invariant that makes a run valid

Every measured scenario uses `constant-arrival-rate` at a declared rate, with
`preAllocatedVUs`/`maxVUs` sized generously above what the rate needs.

**A run with dropped iterations is void, not slow.** If k6 cannot start iterations at the
declared rate, the generator — not the server — is the bottleneck, and the percentiles describe
the wrong thing. The guard therefore refuses the run outright on either of:

- `dropped_iterations > 0`
- `http_req_failed > 0` (any non-2xx: a 500-storm otherwise reports a *lovely* p95)

This is the load-test equivalent of the mutation gate's lesson (#142/#71): a green result from
a harness that measured nothing is worse than a red one.

Scenarios run **sequentially**, staggered by `startTime`, never concurrently — two scenarios in
flight contend and neither one's latency is attributable. A 10-second unrecorded **warm-up**
scenario runs first, so JIT, the EF model cache, the connection pool and PostgreSQL's plan
cache are all warm before the first recorded observation.

## 5. The two kinds of gate metric

### 5.0 The gating statistic is `min`, and that was measured rather than chosen

This section exists because the first real run overturned the obvious assumption. The design was
written to gate on p95 — it is the statistic the sources name and the one every load-test article
reaches for. The first gate-tier run said otherwise:

| Scenario | `min` | p95 |
|---|---|---|
| `read_by_id` | 1.14 ms | 4.91 ms |
| `list_indexed` | 2.18 ms | 8.16 ms |
| `count_exact` | 3.57 ms | 7.90 ms |
| `sort_nullable` | 4.04 ms | 7.90 ms |
| `page_deep` | 2.75 ms | 8.53 ms |
| `row_policy` | 2.97 ms | 8.37 ms |
| `select_projection` | 2.13 ms | 8.04 ms |

**Every p95 lands within 8–9 ms of every other**, so the p95 ratios came out **0.97 to 1.03**
across shapes that plainly do different amounts of work — a gate that would have passed
everything, forever. `min` separates them cleanly: `count=exact` costs 1.64× the reference list,
a nullable sort 1.85×, the row predicate 1.36×.

The reason is that the two statistics answer different questions:

- **`min` is service time** with queueing and interference removed, which is exactly *"how much
  work does this shape do"* — the question a **gate** asks.
- **p95 is what a caller experiences under this load**, which is the question
  `baas-analyza.md:142` asks and the number F4's definition of done wants **published**. At gate
  volume it is dominated by scheduling and container-network jitter, not by the query.

So: **`min` gates; p95 is measured, printed on every row, and published by the calibration tier.**
Both halves of §5 use `min`.

**The gap this leaves, named rather than hidden.** A regression that leaves the fast path alone
and makes a small fraction of requests much slower — a new lock, a cache-miss branch — is not
caught by this gate. On a rig whose p95 noise is larger than the tail effect we could resolve,
pretending otherwise would be worse than saying so.
`scripts/test-assert-load-baseline` pins that behaviour with a case named *"a p95 blow-out with a
flat min does not fail (the named gap)"*, so nobody closes it by accident without deciding to.

### 5.1 Ratios within one run — the sensitive half

Each ratio compares two measurements taken in the **same k6 run, on the same machine, minutes
apart**. Machine speed, runner generation, noisy neighbours and container overhead all divide
out. A ratio therefore needs **no baseline arm and no absolute number in git** — only a
declared ceiling, which is a design statement about the feature, not a property of the
hardware.

Every ratio is anchored on **one reference measurement**, `list_indexed`, which is the
analysis's own headline shape: a filtered, sorted, paged list over an indexed column.

| Ratio | Numerator | Ceiling | What a breach means | Traces to |
|---|---|---|---|---|
| `count_exact` | the reference list + `Prefer: count=exact` | measured | the opt-in count stopped being one extra statement | #179, #110 |
| `page_deep` | the **last** page of a two-term sort, over the **first** page of that same sort | measured | keyset paging degraded beyond its documented cost | **#100** |
| `sort_nullable` | the same list ordered by `scheduled_for` (nullable) | measured | the `CASE` rank got worse, or #178's native fix regressed | **#178** |
| `row_policy` | the same list as `tech-north` (`assigned_to == @user.id`) | measured | the rule engine's hot-path cost grew | §2.4, security core |
| `select_projection` | the same list + `select=id,reference` | measured | **recorded, not defended** — see below | **#117** |
| `unindexed_filter` | `is_emergency=is.true` (no index) | *calibration only* | context for what an index is worth | — |

**Every ceiling is measured, not chosen**, and the baseline file ships with the numbers the
first real run produced, rounded up with headroom stated per row. This follows the precedent
`data-api.md` already sets for the filter budgets — *"The two term/candidate numbers are
measured rather than chosen: 900 filter terms answered in 14 ms and 1000 threw…"* A ceiling
invented at design time is either so loose it gates nothing or so tight it fires on the first
PR, and there is no way to tell which from a desk.

`page_deep` needs care, because #100 is **not** about the number of sort terms — it is about how
deep into the result set the cursor sits. #100's own evidence is rows-removed-by-filter growing
one-for-one with depth (280 001 at depth 280 000) and wall-clock growing 107× across a 28 000×
depth increase. So the scenario must (a) use a **multi-term sort**, which is what makes the
keyset predicate a nested disjunction at all, and (b) actually get deep. It therefore walks to
the last page in an **unmeasured setup phase** at `limit=200` (100 requests at the gate tier,
1 000 at calibration), keeps that final cursor, and the measured scenario hammers that one deep
cursor against the same sort's first page. The denominator being the *same sort shape* is what
isolates depth from sort width.

`unindexed_filter` has no row in the baseline file at all, and that is deliberate: it is
*published*, not judged. Its value is context — what an index is worth on this data — and a
ceiling on it would be a ceiling on PostgreSQL's sequential-scan speed rather than on anything
Alvo decides.

`select_projection` deserves its note. Today `select` is applied to the *response*, not to the
`SELECT` list, so the ratio is ≈ 1.0 and the metric proves nothing. It is included anyway,
because it is the instrument that will *demonstrate* #117 when the port gains a projection
member: the number moves below 1.0 and the improvement is measured rather than asserted. Its
ceiling is a tripwire against the value going *up*, not a claim that it is low.

### 5.2 A/B across branches — the absolute half

Four scenarios have no meaningful denominator; they *are* the floor. For these the gate builds
two images — the PR's merge-base and its HEAD — and runs them **interleaved** (`main`, `HEAD`,
`main`, `HEAD`) so a drift in runner performance over the job's lifetime biases both arms
equally.

| Scenario | Request | What regresses here |
|---|---|---|
| `read_by_id` | `GET /api/work_orders/{id}` as `dispatcher-north` | the whole per-request floor: middleware, API-key auth, tenant resolution, policy resolve (#118), one keyed read |
| `list_indexed` | `GET /api/work_orders?status=eq.scheduled&order=priority.asc&limit=50` | the reference shape, and the analysis's own criterion |
| `create` | `POST /api/work_orders` | validation, the unique check, the audit stamp, the outbox insert |
| `openapi` | `GET /openapi/v1.json` | #126's O(N²), guarded against return |

**A regression fires only when both conditions hold:**

1. `min(HEAD) > min(merge-base) × factor` (default **1.8**, per-scenario overridable), and
2. `min(HEAD) − min(merge-base) > floor` (default **3 ms**).

Both numbers come from measuring the **same code twice**. Across two interleaved arms of
identical images, `min` varied by up to 1.5× — `read_by_id` 0.71–1.07 ms, `list_indexed`
1.57–2.27 ms, `openapi` 2.44–3.97 ms. A factor of 1.4 would therefore have fired on noise; 1.8
sits above the observed spread and still catches anything that doubles. `openapi` gets 2.0,
because `min` for a large JSON document is dominated by serialization scheduling.

The floor exists because a factor alone on a fast endpoint is meaningless: `read_by_id` going
from 1.9 ms to 2.8 ms is 1.47× and 0.9 ms, and gating on it produces a check that fails randomly
and is switched off within a month. Two conditions is the difference between a gate that is
trusted and one that is muted.

**Stated honestly:** at 20 k rows every absolute latency is small, so the **floor** is usually
what decides. That makes the absolute half a coarse tripwire for regressions larger than a few
milliseconds, and the §5.1 ratios the sensitive instrument. That division is the design's, not an
accident of these numbers — but it is worth knowing which half is doing the work.

### 5.3 A ceiling is only valid for the tier it was measured on

The calibration run overturned a second assumption, and this one had shipped as a bug: the design
had **one** baseline file judging both tiers. **The ratios grow with row count.**

| Ratio | gate tier (20 k rows) | calibration tier (200 k rows) |
|---|---|---|
| `count_exact` | 1.54–1.64× | **3.02×** |
| `sort_nullable` | 1.76–1.85× | **3.57×** |
| `row_policy` | 1.36–1.57× | **2.82×** |
| `page_deep` | 1.04–1.18× | **2.46×** |
| `select_projection` | 0.85–0.98× | 1.00× |

That is arithmetic rather than decay: as the database's share of a request grows, the *relative*
cost of extra database work grows with it, because the fixed per-request overhead — middleware,
auth, tenant resolution, serialization — stops dominating. Every ratio row therefore records
`measuredOn: gate tier` and means it.

So the calibration tier **reports** rather than judges (`assert-load-baseline --report-only`),
which is what §6's table always said it did — *"never fails on a number"* — and what the
implementation initially got wrong by running the gate's ceilings against it. A calibration ratio
over its gate ceiling prints `over`, not `BREACH`.

**Validity is still enforced under `--report-only`.** A void run publishes garbage, and garbage in
`docs/performance.md` is worse than no row at all.

A separate `baselines/calibration.json` is deliberately **not** added. The calibration tier's job
is to publish, and a second set of ceilings would be a second thing to keep measured for no gate
to enforce them. If a release-cadence regression ever needs catching, that file is the addition —
recorded here so it reads as a decision.

## 6. The two tiers

| | `gate` | `calibration` |
|---|---|---|
| Trigger | `pull_request`, paths-filtered | push of a `v*` tag, plus `workflow_dispatch` |
| Rows | 10 k `work_orders` per tenant (20 k total), 500 customers per tenant, 8 shared regions | 100 k per tenant (200 k total); a 1 M variant for `page_deep` only |
| `page_deep` depth | ≈ 10 000 (the tenant's own last page) | ≈ 100 000, and 1 M in the large variant |
| Scenarios | the 4 A/B + the 5 gating ratios | all of them, plus `unindexed_filter` |
| Duration | 15 s per measured scenario | 60 s per measured scenario |
| Offered rate (list shapes) | 40 /s | 20 /s — see below |
| Arms | merge-base + HEAD, interleaved (2 reps each) | HEAD only |
| Verdict | **pass/fail** | **publishes** (`--report-only`); never fails on a number — §5.3 |
| Budget | ≤ 20 min including two image builds | ≤ 45 min |

**The large tier's offered rate is lower, and that is a correctness constraint.** §5.1 requires
every list-shaped scenario to see the *same* offered rate, or a ratio between two of them stops
being a statement about Alvo and becomes one about the generator. That forces one rate for the
whole set, which must therefore suit the heaviest shape in it — and at 200 000 rows
`unindexed_filter` is a sequential scan. A rate that saturates it does not make that scenario
slow; per §4.1 it makes the entire run **void**.

The 20 k gate tier is deliberately small. A missing index, an N+1, a lost projection or a
policy resolved once more than before all show up at 20 k rows — those are *shape* regressions,
and shape is visible long before volume. Volume is what the calibration tier is for.

`calibration` runs **on release** rather than weekly, so every published number is attributable
to a version rather than to a date, and a quiet week costs nothing.

### 6.1 Seeding, and how its coupling is kept honest

200 k rows cannot be seeded over HTTP, and `AlvoDataSeed` is `internal` to the EF package,
in-process, and routes every row through the change tracker — unusable from outside the host
and wrong for bulk anyway. So the seed is **SQL, executed inside the `postgres` container**:
`INSERT … SELECT FROM generate_series(…)`, writing the framework-managed columns
(`id`, `tenant_id`, `created_at`, `created_by`, `updated_at`, `updated_by`) directly.

That buys speed and costs a coupling: the seed now knows the physical layout, which
`DescriptorToSchemaMapper` owns. **The coupling is made to fail loudly rather than rot.** After
seeding and before k6 starts, `scripts/test-load` reads the same set back **through the public
API**:

```
GET /api/work_orders?limit=1   with   Prefer: count=exact
```

and aborts unless the returned `count` equals the number of rows it inserted. A renamed column,
a changed table name or a new NOT NULL constraint therefore stops the run with "the seed no
longer matches the schema" instead of silently producing an empty set — which would otherwise
report a *spectacular* p95 for a list of nothing.

An empty-set run is the single most likely failure mode of this whole design, so it is guarded
twice: the count check above, and a per-scenario assertion that the reference list returns a
full page.

## 7. Where things live, and who owns the verdict

```
test/load/scenarios.js              the k6 script: one exec function + one Trend per scenario
test/load/seed.sql                  the bulk seed, parameterised by :rows
test/load/baselines/gate.json       ratio ceilings + A/B factors and floors — the reviewable artifact
test/load/README.md                 how to run it, and how to add a scenario
scripts/test-load                   the driver: image, stack, seed, k6, verdict. --tier, --baseline-ref
scripts/assert-load-baseline        the verdict: summary JSON + baseline JSON -> table + exit code
scripts/test-assert-load-baseline   the guard's own suite
.github/workflows/load.yml          gate (PR) + calibration (tag/dispatch) + notify
docs/performance.md                 the published numbers, one row per release tag
```

`test/load/` rather than a new project, following the `test/teapie` precedent: a non-.NET test
suite that lives under `test/` without being in `MMLib.Alvo.slnx`. Per CLAUDE.md's *"do not
create projects ahead of time"*, no `.csproj` appears.

**Pass/fail lives in exactly one place** — `scripts/assert-load-baseline` — and it touches
neither Docker nor k6. It is a pure function from JSON to an exit code, which is what makes it
testable, and `scripts/test-assert-load-baseline` tests it with synthetic summaries. That is a
direct application of the mutation-gate lesson recorded in CLAUDE.md: *"A guard nothing tests
is the defect it exists to prevent"* — `scripts/assert-mutation-run`'s own first review found
it failing open in five places.

k6's native `thresholds` are deliberately **not** the gate. A ratio between two trends is not a
k6 metric, and an A/B comparison spans two k6 runs; splitting the verdict between k6 thresholds
and a script would give one decision two authorities.

### 7.1 k6 with no install step

`scripts/test-load` prefers a `k6` on `PATH` and otherwise falls back to the official
`grafana/k6` image on the compose network. A maintainer on a fresh machine runs the same thing
CI runs with nothing to install — the property `scripts/test-e2e` already has and the reason it
exists.

### 7.2 The baseline is a moved-goalpost surface, and the repo already has a gate for those

`test/load/baselines/gate.json` is exactly the kind of artifact a failing check can be made
green by editing — the same hazard `*.verified.*` snapshots and the PublicAPI approvals carry,
and the reason `.claude/hooks/turn-review-gate` + `alvo-snapshot-judge` exist. The hook watches
`*.verified.*` only. **Extending its ledger to `test/load/baselines/*.json` is part of this
PR**, added as a function inside `turn-review-gate` (never as a second Stop hook — an event's
hooks run in parallel and would race the drain).

## 8. Tracking over time

`docs/performance.md` holds one table, one row per release tag, with the rig named in the row.
The calibration workflow uploads its `calibration.json` and `calibration.md` as artifacts and
opens a tracking issue containing the table — it does **not** commit, because pushing to `main`
from CI is banned outright by CLAUDE.md. Folding the row into `docs/performance.md` is a normal
PR.

A hosted trend store (Grafana Cloud k6, a `gh-pages` time series, a Prometheus remote-write) is
**declined for now**, and the reason is that a release-cadence series of a dozen rows is a
markdown table's job. The decision is recorded so a later reader sees a choice rather than an
omission; the trigger to revisit it is the day the calibration tier runs more often than
releases do.

## 9. Scope: what this PR does not do, and why

| Not delivered | Why, and what it needs |
|---|---|
| The **SQLite writes/s** number (`baas-analyza.md:871`) | It is a *throughput* claim, and §4 shows throughput is not credible with a co-resident generator. Needs a rig where the generator is off-box. **Filed as #187.** |
| **Max throughput** for any engine | Same reason. |
| **BenchmarkDotNet** micro-benchmarks over CEL compile / filter parse / SQL render | A second harness, second format, second baseline. Valuable as the *"which layer regressed"* follow-up once the HTTP gate has fired at least once in anger. **Filed as #188.** |
| **Azure SQL** leg | There is no T-SQL driver yet (#92). |
| Load over **realtime**, **outbox delivery**, **before-hooks** | No HTTP surface to drive, or none shipped. Each arrives with its own scenario, per §10. |
| Making the gate a **required** check | It ships advisory. Promoting it is the maintainer's call and wants evidence: no false positive across a couple of weeks of real PRs. Named here so it does not get forgotten. |

## 10. How this grows — the mechanism, not a promise

The question this design has to answer is not *"what do we measure today"* but *"what does
adding a feature cost in measurement work"*. The answer is **two edits, never a new harness**:

1. one `exec` function and one `Trend` in `test/load/scenarios.js`;
2. one row in `test/load/baselines/gate.json`, expressing the feature's cost as a **ratio
   against `list_indexed`**.

A ratio against a fixed reference endpoint is the right unit because it survives every rig
change, states the design intent in the number itself (*"embedding one relation may cost at
most 2× a plain list"*), and needs no historical data to be meaningful on its first run.

The rows already foreseeable, each an open issue:

| Feature | Issue | The ratio it will declare |
|---|---|---|
| Aggregations over the policy-filtered set | #109 | `count/min/max/sum` vs the reference list |
| Relation embedding, depth 1 | #108 | `select=…,owner(name)` vs the reference list |
| `POST /query` | #107 | the same filter by body vs by URL — should be ≈ 1.0 |
| Bulk operations | #106 | per-row cost of a 1000-row batch vs 1000 singles |
| Upsert / `PUT` | #105 | vs `create` |
| Rate limiting & quotas | #112 | the limiter's own overhead on an allowed request |
| Projection pushdown | #117 | `select_projection` moves **below** 1.0 — the fix, measured |
| Native `NULLS FIRST/LAST` | #178 | `sort_nullable` collapses toward 1.0 |
| Dynamic entities | #41 | the same scenarios over a *virtual* entity vs a physical one — the analysis's *"identically over both"* criterion becomes a number |

That last row is the one that matters most for the phase map: `baas-analyza.md:165` requires
that the same suite pass identically over a physical and a virtual entity, and F7's driver is
where a metadata-driven store either holds up or does not. A ratio harness anchored on a
physical-entity reference is precisely the instrument that will answer it.

## 11. Deviations from the sources, stated

0. **The gating statistic is `min`, not the p95 the sources name** (§5.0). p95 is measured,
   printed and published — the DoD's own requirement — but it cannot decide anything at gate
   volume, and the tail-only regression that `min` cannot see is named and pinned rather than
   left implied.

1. **`baas-analyza.md:142`'s `< 50 ms` is not adopted as a CI threshold.** The criterion says
   *"lokálne"*, naming a rig CI does not have. It is measured and published by the calibration
   tier; the gate uses ratios and a relative A/B instead. Per `baas-analyza.md:1116` this is
   calibration, which is what the source asks for.
2. **The `1 M` keyset criterion runs only in the calibration tier**, and only for `page_deep`.
   Seeding 1 M rows twice per PR buys nothing the 20 k tier does not already catch.
3. **`baas-analyza.md:871`'s SQLite writes/s is deferred, not quietly dropped** (§9).
4. **k6's own thresholds are not the verdict** (§7).
5. **`select_projection` is recorded rather than defended** (§5.1) — a metric whose present
   value proves nothing, kept because it is the instrument for #117's fix.
6. **The gate ships advisory, not required** (§9).
7. **The calibration tier offers a lower rate than the gate tier** (§6). Uniform rate across
   list shapes is a §5.1 requirement, so the rate must suit the heaviest shape in the set, and at
   200 k rows that is `unindexed_filter`'s sequential scan.

## 12. Acceptance criteria for this PR itself

- `scripts/test-load --tier gate` runs green on a clean checkout with **no k6 installed**.
- `scripts/test-assert-load-baseline` passes, and it proves the guard **fails** on: a breached
  ratio, a breached A/B factor *with* the absolute floor exceeded, a breached factor *without*
  it (must pass), `dropped_iterations > 0`, `http_req_failed > 0`, and a malformed summary.
- A seed that no longer matches the physical schema aborts the run **before** k6 starts, naming
  the mismatch.
- `docs/performance.md` exists and carries **real measured numbers**, with the rig named.
- Every ceiling in `test/load/baselines/gate.json` carries the measurement it came from and the
  headroom applied. No invented number ships.
- `.claude/hooks/turn-review-gate` treats `test/load/baselines/*.json` as a judged baseline;
  `scripts/test-hooks` covers it.
- The load workflow is paths-filtered so a docs-only PR does not run it.
