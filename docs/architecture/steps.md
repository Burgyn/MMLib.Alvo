# Steps, sleep, debounce and concurrency limits

How Alvo lets a handler split a side effect into steps that complete once, wait durably, collapse a burst of events and
cap its running deliveries (HX-14; spec `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` §4.10 and its
*HX-14 settled details*). One rule: **everything rides the existing delivery engine** (`IDeliveryStore`,
`alvo_deliveries`). Nothing new runs beside the dispatcher; there is no timer, no second queue, no in-memory state.

| Thing | Surface | Stored as |
|---|---|---|
| Step | `IAlvoSteps.RunAsync(name, work)` on `AlvoEventContext.Steps` / `AlvoJobContext.Steps` | a row of `alvo_steps` per completed step |
| Sleep | `IAlvoSteps.SleepUntilAsync(name, at)` | the delivery's own `not_before`, plus a step row holding the wake instant |
| Debounce | `AlvoHandlerOptions.Debounce` | the new delivery's `not_before`; older waiting rows become `superseded` |
| Concurrency | `AlvoHandlerOptions.Concurrency = AlvoConcurrency.PerKey(key, n)` | `n` lockable rows of `alvo_concurrency_slots` per key |

## The tables

```sql
CREATE TABLE alvo_steps (
    event_id TEXT NOT NULL,
    subscriber TEXT NOT NULL,
    step TEXT NOT NULL,
    result TEXT NOT NULL,           -- System.Text.Json, web defaults; the JSON text null for a value-less step
    completed_at TEXT NOT NULL,
    PRIMARY KEY (event_id, subscriber, step));

CREATE TABLE alvo_concurrency_slots (
    slot_key TEXT NOT NULL,         -- not "key": reserved in T-SQL
    slot INTEGER NOT NULL,          -- 0 … limit − 1
    delivery_id TEXT NULL,
    lease_until TEXT NULL,
    PRIMARY KEY (slot_key, slot));
CREATE INDEX ix_alvo_concurrency_slots_delivery ON alvo_concurrency_slots (delivery_id);
```

(PostgreSQL uses its own column types; the shape is the same.) Steps are keyed by **event and subscriber**, not by
delivery id (C11): a redelivered or replayed delivery of the same event finds the same steps.

## Steps

**Read once, record after.** A delivery's attempt reads its steps once (`StepsAsync`). `RunAsync(name, work)` returns
the stored result when `name` is there; otherwise it runs `work`, serializes the result (at most 256 KiB) and records it
with `RecordStepAsync` — `INSERT … ON CONFLICT DO NOTHING`, then a read of the key — and returns **the stored** result.
Two runners racing one step therefore keep the first record and both continue with it.

**At least once per step.** The record is written after `work` returns, outside any unit: a crash in between runs that
step again. Completed steps are never re-run. This is Temporal's and Durable Functions' activity contract, and Inngest's
`step.run`.

**Names** are 1–200 characters, no control characters, unique within one run; a run has at most 1 000 steps. A reused
name or the 1 001st step throws `InvalidOperationException` before anything is recorded.

**Outside a delivery** — `AlvoEventContext.Create`, `AlvoJobContext.Create`, a `DeliverAsync` fixture run — the context
carries a pass-through: every step runs its work, a sleep returns at once. Steps are deliberately not a DI service.

## Sleep: a fenced reset of the delivery itself

`SleepUntilAsync(name, at)`:

1. Returns at once when `now ≥ at` (first call or resume), recording the wake step if it is new.
2. Otherwise records the step `name` with `at` as its result, flags the run, and throws an internal `StepSleep`.
3. The runner sees the flag — whether the handler returned, rethrew or swallowed the exception — and answers
   `SleepAsync`: in one transaction, fenced on `(claimed_by, attempts, status = 'running')`,
   ```sql
   UPDATE alvo_deliveries SET status = 'pending', not_before = @wake_at, attempts = attempts - 1,
                              lease_until = NULL, claimed_by = NULL, last_error_type = NULL
    WHERE <fence>
   ```
   and records the attempt with outcome `skipped`. The attempt counter goes back down: **a sleep is not an attempt**.
4. Once the flag is set, every later step call of that attempt throws `StepSleep` without running or recording.

On resume the stored wake instant is compared with `now`; a delivery woken early (a redeliver) sleeps again. A sleeping
delivery is `pending` with a future `not_before` — the dashboard reads that, plus a last attempt `skipped`, as
*sleeping until*. An ordered lane waits behind it exactly as behind a retry.

## Debounce: supersede in the fan-out's transaction

A debounced subscriber's new row is due `now + Debounce`. Before inserting it, the fan-out takes an exclusive dialect
transaction lock on `(subscriber, tenant, partition key)` (keys ascending), then, in the same transaction:

```sql
UPDATE alvo_deliveries SET status = 'superseded', completed_at = @now, lease_until = NULL
 WHERE subscriber = @subscriber AND partition_key = @partition_key
   AND COALESCE(tenant_id, '') = COALESCE(@tenant_id, '')
   AND status IN ('pending', 'retrying')
   AND NOT EXISTS (SELECT 1 FROM alvo_delivery_attempts started WHERE started.delivery_id = alvo_deliveries.id)
   AND event_id < (SELECT MAX(newest.event_id) FROM alvo_deliveries newest
                    WHERE newest.subscriber = @subscriber AND newest.partition_key = @partition_key
                      AND COALESCE(newest.tenant_id, '') = COALESCE(@tenant_id, '')
                      AND newest.status IN ('pending', 'retrying', 'running', 'succeeded'))
```

**Why the lane lock.** On PostgreSQL's READ COMMITTED a blocked `UPDATE` re-checks only its outer `WHERE` against the
newly committed row (EvalPlanQual) — never the `MAX` subquery, which keeps the statement's snapshot. Two fan-outs of one
lane racing without the lock could each read a `MAX` that predates the other's insert and both leave an older row
waiting. The transaction lock serialises the lane's fan-outs, so each sees the other's committed row.
`PostgreSqlConcurrencyRaceTests.Racing_debounced_fan_outs_of_one_lane_leave_only_the_newest_waiting` fails without it.

The `NOT EXISTS` over attempts means only a delivery that **never started** is superseded: a running, retrying or
sleeping one runs on. A late fan-out of an older event supersedes its own row at once. The tenant is in every term, so
two tenants' lanes never touch.

## Concurrency: a counting semaphore of lockable rows

A cap is `limit` rows of one **slot key**, `subscriber ␟ tenant|record ␟ tenant id ␟ partition key` (U+001F; the
partition key part is empty for `tenant`). The subscriber and tenant may hold no control character (refused at fan-out).

**Why not `COUNT`.** A `COUNT(*) < limit` predicate passes for every claimant that read the same snapshot, so N
claimants can all pass it. A slot is instead taken by a conditional `UPDATE` of one row:

```sql
UPDATE alvo_concurrency_slots SET delivery_id = @delivery_id, lease_until = @lease_until
 WHERE slot_key = @slot_key AND slot = @slot AND (delivery_id IS NULL OR lease_until < @now)
```

A second claimant reaching a row the first just took waits on its lock, re-checks the outer condition against the
committed row, fails, and moves to the next slot. Once all `limit` rows are held, nobody gets one.

**The claim transaction**, capped half first (P19), then uncapped:

1. Read up to `limit` candidates per slot key (`ROW_NUMBER() … <= @limit`, so one saturated key cannot fill the read).
2. Insert the key's rows lazily (`ON CONFLICT DO NOTHING`), then take slots — keys ascending, slots ascending — for at
   most the capped share of the batch: all of it when every named subscriber is capped, else its larger half. A key that
   yields no slot is skipped for the rest of the claim.
3. Claim the deliveries the slots were taken for, ids ascending; give back the slot of one no longer claimable.
4. Run the uncapped batch `UPDATE` with what is left of the batch.

**Lock order:** every slot before any delivery row, everywhere. An answer (complete, fail, sleep) deletes its slot row
first — `DELETE … WHERE delivery_id = @id AND EXISTS(fence)` — then runs the fenced delivery update, and **rolls back
when the fence failed**: on PostgreSQL the `EXISTS` is evaluated from the statement's snapshot, so a late finisher could
otherwise delete the slot a reclaimer had just taken for the same delivery.

**One bounded deadlock window remains.** A delivery reclaimed after its lease expired can take a lower slot while its
expired row still names it. If that second lease expires too, a claimant and the late answer can wait on each other's
rows; PostgreSQL aborts one with `40P01`. A claim is retried next round; an answer within the runner's settle grace,
then by lease expiry and re-run. It needs two expired leases on one key plus a late answer.

## Decisions (settled, with their cost)

1. **The sleep uses the delivery row, not an `alvo_timers` row** — a deviation from §4.10's timer
   `step:{delivery}:{name}`. A timer fire appends to the outbox and cannot reset a delivery without a new `ITimerStore`
   member. *Cost:* a sleeping delivery is absent from the *Timers* tile; it shows as pending with a future due time.
2. **A sleep is detected even if the handler swallows it** (flag + exception). *Cost:* none.
3. **A sleep's step row stores the wake instant.** *Cost:* one row per sleep.
4. **Each step is at least once; completed steps are never re-run.** *Cost:* step bodies must be idempotent.
5. **Results are System.Text.Json (web defaults) and never leave the store** through the Management API —
   `ManagementDeliveryStep` is name and time. *Cost:* an operator cannot see a step's value (intended: it can be PII).
6. **Steps are reached through the context, not DI**; outside a delivery a pass-through. *Cost:* unit tests do not
   exercise memoization (`AlvoTestHost` does).
7. **Redeliver or replay of a `succeeded` delivery deletes its steps** in the reset's transaction; `dead` and
   `cancelled` keep them. *Cost:* no full re-run of a dead delivery that completed steps.
8. **Debounce is trailing and scoped to (subscriber, partition key, tenant)**; `running` is never superseded, nor
   anything that ever started. *Cost:* a stream faster than the period delays delivery until it pauses (capped at 1 day).
9. **`superseded` is finished but not replayable or redeliverable**; it holds no lane and no slot. *Cost:* a stale event
   cannot be forced through.
10. **Concurrency is per subscriber**; slot rows are inserted lazily and only slots `0 … limit−1` are tried. *Cost:*
    after a limit is lowered, higher slots already held run on until they finish.
11. **A slot's lease is the delivery's claim lease**, released by the fenced answer only. *Cost:* a crash costs one
    slot for up to the lease (90 s at the defaults); a crashed or fenced-out answer's row stays until a taker of its key
    overwrites it.
12. **`IDeliveryStore` gains `StepsAsync`, `RecordStepAsync`, `SleepAsync`**, and a provider must honour
    `DeliveryRequest.Debounce` and `DeliveryClaim.Limits`. *Cost:* a third-party store fails to compile until it
    implements them; the contract suite pins the semantics.

See also: [`time.md`](time.md) — timers and schedules; a step's sleep is not a timer.
