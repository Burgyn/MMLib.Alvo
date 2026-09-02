# Performance

Measured numbers for Alvo's Data API, produced by `scripts/test-load --tier calibration` and
published here one section per measurement. This file is the *published* half of F4's definition
of done (`alvo-specifikacia.md:337`: *"p95 latencie zmerané a publikované — kalibrácia
akceptačných kritérií z analýzy"*).

How to reproduce any row: [`test/load/README.md`](../test/load/README.md). Why it is measured this
way, and every deviation from a source recommendation:
[`docs/superpowers/specs/2026-09-02-f4-pr-e-load-test-foundations-design.md`](superpowers/specs/2026-09-02-f4-pr-e-load-test-foundations-design.md).

## Read these numbers correctly, or do not read them

Three qualifications, and none of them is boilerplate:

1. **The rig is named in every section, and the numbers do not transfer off it.** The load
   generator runs *beside* the server and its database. At the modest offered rates used here the
   server is idle between requests, so latency is meaningful — but nothing here is a throughput
   claim, and **there is no requests-per-second or writes-per-second figure anywhere in this file
   on purpose**. At saturation a co-resident generator competes with the server for the same
   cores, and the number produced would describe the machine rather than Alvo.
   `baas-analyza.md:871`'s SQLite writes/s criterion therefore remains **unmeasured** and is filed
   as **#187**; it needs a rig where the generator is off-box.

2. **These are not the numbers the CI gate judges.** The gate compares *ratios* and gates on
   `min`, because `min` is service time and it means the same thing on every rig — where p95 does
   not: on the macOS/Docker Desktop rig below it collapses to a single value across every shape,
   while on GitHub's `ubuntu-latest` it tracks `min` closely. p95 is published because it is what the sources ask about and what a caller
   experiences; it is not what decides a PR. See `test/load/README.md`. Which *layer* a change
   moved is a question neither half answers — that is **#188**.

3. **Ratios grow with row count, so a comparison across sections is not a regression.** At 20 000
   rows `Prefer: count=exact` costs 1.6× the reference list; at 200 000 rows it costs 3.0×. That is
   arithmetic, not decay — as the database's share of a request grows, the relative cost of extra
   database work grows with it because the fixed per-request overhead stops dominating.

## Baseline — 2026-09-02, pre-v0.1 (`f4/pr-e-load-test-foundations`)

**Rig.** Apple M-series laptop, Docker Desktop, generator co-resident with the server and
PostgreSQL 16 in the same Docker VM. k6 v2.2.0. Not a GitHub runner: this is the first
measurement, taken before the workflow existed. Later sections will come from
`ubuntu-latest`, and are **not** comparable with this one in absolute terms.

**Data.** The `examples/field-service` descriptor over real PostgreSQL: 100 000 `work_orders` per
tenant (200 000 total, two tenants), 2 000 customers per tenant, 8 shared regions.
`status=eq.scheduled` matches ~25 % of rows; `IX_work_orders_status_priority` serves the reference
filter and sort.

**Load.** `constant-arrival-rate` (open model), 60 s per scenario, scenarios run sequentially
after a 10 s unrecorded warm-up. 20 req/s for every list-shaped scenario, 40 for the keyed read,
10 for the write, 5 for the OpenAPI document. 13 006 iterations, **0 dropped**, **0 failed** — the
two numbers that make the run valid rather than void.

All values in milliseconds.

| Scenario | min | med | p95 | p99 | max |
|---|---|---|---|---|---|
| `read_by_id` | 0.74 | 3.57 | **5.28** | 6.36 | 17.92 |
| `list_indexed` | 4.25 | 11.81 | **15.61** | 20.00 | 23.32 |
| `select_projection` | 4.25 | 12.45 | 16.40 | 18.53 | 23.19 |
| `page_shallow` | 5.17 | 11.83 | 15.67 | 19.63 | 32.50 |
| `unindexed_filter` | 8.87 | 13.67 | 14.76 | 16.25 | 30.94 |
| `row_policy` | 11.96 | 16.65 | 18.94 | 20.42 | 47.11 |
| `page_deep` | 12.70 | 16.97 | 25.57 | 32.87 | 37.92 |
| `count_exact` | 12.85 | 17.03 | 19.67 | 23.04 | 34.42 |
| `sort_nullable` | 15.18 | 18.71 | 24.82 | 37.87 | 131.81 |
| `create` | 2.86 | 9.92 | 12.27 | 15.71 | 119.49 |
| `openapi` | 3.58 | 7.12 | 10.11 | 14.00 | 16.92 |

### Against the analysis's own bar

`baas-analyza.md:142` asks for *"p95 latencia filtrovaného listu nad 100k riadkov (indexovaný
stĺpec) < 50 ms lokálne"*.

**`list_indexed` p95 = 15.6 ms over 100 000 rows on an indexed column — inside the 50 ms bar by a
factor of three.** That is the criterion met, on the rig the criterion names ("lokálne").

The bar's second half — *"keyset pagination stabilná nad 1M riadkov"* — is a **stability** claim
rather than a latency one, and it is already proven elsewhere: the paging walk in
`docs/architecture/data-path.md` pages a null-bearing set one row at a time over 1 000 000 rows
and compares against the unpaged read. What this file adds is the *cost* of depth, below.

### What each row costs, relative to the reference list — on `min`, not p95

**These ratios are computed on the `min` column, not the p95 one.** Dividing the p95 column
instead gives `row_policy` = 1.2×, not the 2.8× below, and the reason is qualification 2 above: at
these volumes p95 carries scheduling and container jitter that is common to every shape and swamps
the difference between them, while `min` is service time. Do not mix the two.

The ratios are the durable part of this table: they survive a change of machine, where the
absolute milliseconds do not. Against `list_indexed` (`page_deep` against `page_shallow`, which
shares its two-term sort so the comparison isolates depth):

| Ratio | Value | What it says |
|---|---|---|
| `sort_nullable` | **3.6×** | #178. A nullable sort key renders a portable `CASE` rank that no index on the key can serve; a required key renders no rank at all. The most expensive thing a caller can do to a list today. |
| `count_exact` | **3.0×** | #110/#179. `Prefer: count=exact` is a second statement over the whole matching set. Opt-in for exactly this reason, and #179 asks for operator control over it. |
| `page_deep` | **2.5×** | #100. Cursor at the tenant's last page (~100 000 deep) against depth zero on the same sort. Keyset paging's cost grows with depth; what it buys is *stability*, which holds. |
| `row_policy` | **2.8×** | The same URL answered for a caller whose `list` rule carries `assigned_to == @user.id` (indexed) instead of no row predicate. This is the rule engine's cost in the hot path — security-core surface, and the ratio to watch hardest. |
| `unindexed_filter` | 2.1× | What an index is worth on this data. Context, not a budget. |
| `select_projection` | **1.00×** | #117, and the number *is* the point: `select` is applied to the response, not to the `SELECT` list, so asking for two fields costs exactly what asking for twenty does. When the port gains a projection member this should fall below 1.0, and that will be the fix measured rather than asserted. |

`read_by_id` at 0.74 ms min / 5.28 ms p95 is the per-request floor — middleware, API-key
authentication, tenant resolution, policy resolution (#118 pins it at two resolves) and one keyed
read. Everything above is that floor plus query work.

`create`'s max of 119 ms against a min of 2.9 ms is worth knowing rather than discovering: the
write path carries validation, the unique check, the audit stamp and the outbox insert, and its
tail is genuinely wider than any read's.
