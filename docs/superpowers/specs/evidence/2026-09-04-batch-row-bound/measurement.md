# The batch row bound — what was measured, and why the number is *chosen*

**Date:** 2026-09-04 · **Issue:** #106 · **Design:** §7 · **Verdict: chosen, not measured to a ceiling.**

#106 asks for a bound measured the way `AlvoFilter.MaxTerms` was. `MaxTerms` had a failure to point at —
SQLite threw *too many SQL variables* at 40 000 candidates. **This measurement found no such point.** Both
shipped engines stayed linear to N = 5000 and nothing broke, so `MaxBatchRows` is a *chosen* number with
stated reasons, the way PR-G recorded `MaxPatternLength` rather than claiming a measurement it did not have.

## What was measured

A throwaway harness (not committed) drove `CreateManyAsync` → `UpdateManyAsync` → `DeleteManyAsync` over a
**policed, owner-scoped entity** — the `tickets` fixture, whose every rule is `owner_id == @user.id`, so each
row costs a real `WITH CHECK` evaluation plus a row-scoped read rather than a bare insert. Measuring an
unpoliced entity would have reported a ceiling several times too high, because the per-row cost the bound
exists to hold *is* the policy evaluation.

- Hardware: Apple Silicon (arm64), .NET 10.0, Debug build.
- SQLite: `Microsoft.Data.Sqlite`, file-backed fixture.
- PostgreSQL: `postgres:16-alpine` in Testcontainers.
- `allocMB` is `GC.GetTotalAllocatedBytes()` across all three verbs for that N.

## The defect the measurement found

The first run was **quadratic**, and that is the measurement's real return on cost:

| N | allocMB | **MB per row** |
|---|---|---|
| 100 | 24.4 | 0.24 |
| 500 | 183.1 | 0.37 |
| 1000 | 529.3 | 0.53 |
| 2500 | 2561.0 | 1.02 |
| 5000 | 9268.9 | **1.85** |

The per-row cost rose eight-fold purely because the batch was bigger. Cause: the batch called the single-row
`InsertAsync` in a loop, which does `Add` + **`SaveChangesAsync` per row**, and EF's change tracker walks
every tracked entry on each save — O(N²) in the batch size. Fixed by adding every judged candidate and saving
**once** (`InsertManyAsync`). A 5000-row create went from **5201 ms to 580 ms**.

A first version of the fix also called `ChangeTracker.Clear()` before the re-reads. `ChangeTrackerReachTests`
refused it, correctly: `AlvoDataContext` turns query tracking off globally, so nothing was ever fixed up
against the tracked copies. Re-measured without it — **identical numbers**, confirming the entire win is the
single save and none of it was the clear.

## After the fix

**SQLite**

| N | create ms | update ms | delete ms | allocMB | MB/row |
|---|---|---|---|---|---|
| 100 | 69 | 24 | 13 | 20.6 | 0.206 |
| 500 | 57 | 113 | 65 | 98.0 | 0.196 |
| 1000 | 116 | 218 | 122 | 194.8 | 0.195 |
| 2500 | 286 | 530 | 295 | 485.5 | 0.194 |
| 5000 | 580 | 910 | 417 | 968.0 | 0.194 |

**PostgreSQL**

| N | create ms | update ms | delete ms | allocMB | MB/row |
|---|---|---|---|---|---|
| 100 | 62 | 98 | 68 | 21.6 | 0.216 |
| 500 | 198 | 446 | 304 | 101.2 | 0.202 |
| 1000 | 393 | 893 | 589 | 201.1 | 0.201 |
| 2500 | 928 | 1614 | 1046 | 484.6 | 0.194 |
| 5000 | 1472 | 3071 | 2182 | 961.0 | 0.192 |

Both engines are flat at ~0.19–0.20 MB per row and linear in time. **Nothing fails within the range
measured.**

## The number, and why

**`MaxBatchRows = 1000`.** With no failure point to name, two properties decide it, and neither is a
throughput question:

1. **Lock-hold duration.** A batch reaches its verdict over each row's *locked* pre-image and holds those
   locks until it commits, so its duration is time other writers spend blocked on those rows. At N = 1000 the
   slowest verb on PostgreSQL is 893 ms; at N = 5000 it is 3.1 s. Sub-second is a defensible thing to make a
   concurrent writer wait; three seconds is not.
2. **Per-request allocation.** ~195 MB at N = 1000 against ~960 MB at N = 5000. A single authenticated
   request that allocates a gigabyte is a cheap denial-of-service lever, and the bound is the only thing
   between a caller and it.

1000 also matches `AlvoFilter.MaxInCandidates`, which is the other caller-chosen count in this framework —
one number for "how many things may one request name" is easier to hold than two.

## What was not measured

- **Contention.** All three verbs ran uncontended. The judging pass is N reads before the first write, which
  widens SQLite's `SQLITE_BUSY_SNAPSHOT` window; the contended-write retry is the absorber and is deliberately
  left wide. A contended measurement is worth having and is not attempted here.
- **Row width.** The fixture's rows carry two declared fields. A wide entity costs more per row, so the
  allocation figures are a floor rather than a typical case.
