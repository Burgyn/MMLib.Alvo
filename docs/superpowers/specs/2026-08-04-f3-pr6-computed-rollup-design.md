# F3 PR6 — computed & rollup fields (#21)

Design for issue #21, *[17] Computed & rollup fields*. Every measurement cited here is
recorded verbatim in `evidence/2026-08-04-f3-pr6-computed-rollup/spike.txt`.

## Sources this is designed against

- **spec §2.1** (`alvo-specifikacia.md:334`) — `computed` = arithmetic over the same row →
  **DB stored generated column**, field read-only, computed by the database. `rollup` =
  aggregation over related records → **transactionally consistent** (DB trigger or
  in-transaction recompute), *never a manual hook*.
- **analysis** (`baas-analyza.md:113`, `:122`, `:127`) — rollup must be a **first-class
  declarative concept with transactional consistency**, because "the sum of children on the
  parent is the classic source of a race condition". Names the on-read variant (an aggregate
  query at read time) as an alternative without denormalisation, but **not filterable**.
- **the ladder** (`baas-analyza.md:1358`) — the worked example that exercises every rung at
  once: `invoice_items.line_total = unit_price * amount` is *computed*;
  `invoices.net_total = sum(line_total)` is *rollup*; `vat_total` is a *before-hook*, because
  a VAT rate is contextual and time-valid business logic rather than arithmetic; and
  `gross_total = net_total + vat_total` is *computed* again.
- **frozen artifacts** — `field.computed` is a `$defs/cel` expression whose own description
  says it **may reference this entity's rollup fields**; `field.rollup` is
  `{ from, op: sum|count|avg|min|max, field? }` with `field` optional because `count` needs
  none.

**One source is stale and #21 is right.** `UnhonouredFeatures`' `rollup` fix-suggestion says
*"rollups are deferred past F3"*, while #21 sits in milestone F3 and both sources above require
it. The suggestion text is corrected here; it is not evidence of a deferral.

## What the spike settled, and what it killed

### `computed`: the two engines are asymmetric in opposite directions

| | SQLite (bundled `e_sqlite3` 3.53.3) | PostgreSQL 16 |
|---|---|---|
| `ADD COLUMN … STORED` on an **empty** table | OK | OK |
| `ADD COLUMN … STORED` on a table **with rows** | **refused** — `cannot add a STORED column` | OK, and existing rows are backfilled |
| `ADD COLUMN … VIRTUAL` | OK | **syntax error** — no `VIRTUAL` before PG 18 |

There is **no single spelling** that adds a computed field to an entity that already holds
rows on both engines.

**The empty/non-empty split is the whole finding.** On an empty table SQLite accepts `STORED`,
so a fact built on a fresh fixture passes while the only case that matters — a deployed entity
that already has data — fails. Any fact about adding a computed field must therefore write a
row **first**.

Two properties the spike confirmed are worth having, because they are what the ladder is
buying: the column is **read-only as enforced by the engine** (`cannot UPDATE generated
column`, `cannot INSERT into generated column`), so no write path — hook, custom endpoint or
bug — can set it; and a computed column **may reference a column the application maintains**
and tracks it, which is what makes `gross_total = net_total + vat_total` work over a rollup.

### `rollup`: the obvious implementation is a lost update, and the engines want opposite statements

Measured on PostgreSQL, `READ COMMITTED`, 40 concurrent writers against one parent:

| variant | result |
|---|---|
| `UPDATE parent SET total = (SELECT SUM …)`, no delay | children 40, total **40** — correct |
| the **same** statement with a 50 ms delay before it | children 40, total **31** — **lost update** |
| value computed outside the `UPDATE` | children 40, total **33** — lost update |
| `SELECT … FOR UPDATE` the parent, **then** recompute | children 40, total **40** — correct |

The hypothesis that "the row lock serialises it and the subquery re-evaluates" is **false**.
Under `READ COMMITTED` the `SET` expression is evaluated from the snapshot taken at statement
start; when the row lock is finally granted, EvalPlanQual re-checks only the outer `WHERE`
(`id = 1`, still true), so the stale value is written. **This is the same EvalPlanQual
mechanism that bit the outbox claim in PR5a** — second occurrence in this codebase.

On SQLite, 24 concurrent writers under WAL:

| variant | result |
|---|---|
| `BEGIN IMMEDIATE`, atomic subquery | 24 committed, 0 failed — correct |
| `BEGIN` (deferred), atomic subquery | 24 committed, 0 failed — correct |
| `BEGIN` (deferred), **read the parent first** | 12 committed, **12 failed**, `[5/517]` = `SQLITE_BUSY_SNAPSHOT` |

So the two engines impose **opposite** requirements: PostgreSQL *requires* the parent lock
before recomputing; SQLite must *not* read the parent before writing, and needs no explicit
lock at all because the child insert already took the write lock and SQLite admits one writer
at a time.

The difference is in the **statements, not the semantics** — which is exactly what the dialect
port is for, and `IAlvoSqlDialect.RowLockClause` is already a member of that shape.

**Two inversions of this repo's usual assumptions, recorded so nobody re-derives them:**

1. For this question **SQLite is the weaker engine.** The concurrent-boot facts treat it as the
   harder leg because one writer at a time exposes lock contention; here that same property
   makes the lost update structurally impossible. A SQLite-only suite would never see the
   PostgreSQL defect, so **the rollup race fact must run on PostgreSQL to mean anything.**
2. **The race fact must widen its window.** Without the 50 ms delay the lost update showed
   40 of 40 and looked correct. The delay is the entire difference between a fact and an
   illusion.

## Design

### D1 — `computed` is a stored generated column, emitted through the dialect port

The core renders the CEL expression to SQL through the existing scalar entry point
(`SqlPredicateRenderer` already refuses a non-`Computed` profile there) and hands the rendered
text to a new dialect member. The **core never spells the DDL**, so a third engine implements
a member instead of the core growing a branch.

New member on `IAlvoSqlDialect`, following `RowWindowClause`'s precedent of shipping with a
default so existing implementors do not break:

```csharp
/// <summary>The column definition for a stored generated column, or null when the engine
/// cannot express one.</summary>
string? GeneratedColumnDefinition(string columnName, string storeType, string renderedExpression);
```

- PostgreSQL: `"{col}" {type} GENERATED ALWAYS AS ({expr}) STORED`
- SQLite: `"{col}" {type} GENERATED ALWAYS AS ({expr}) STORED`
- default: `null` → the migrator refuses the field with a structured error naming the engine.

**Adding a computed field to an entity that already holds rows takes a table rebuild on
SQLite.** This is not optional and not a per-engine behaviour difference: the observable
outcome must be identical on both engines (§0 principle 3), so SQLite performs the documented
create-new / copy / drop / rename sequence rather than Alvo emitting `VIRTUAL` there and
`STORED` on PostgreSQL. A `VIRTUAL` column is not stored, so it would silently differ in what
an index and a filter can do — the one thing the analysis names as the on-read variant's
drawback.

**Deviation from the spec, stated:** spec §2.1 names three engines
(Postgres/SQL Server/SQLite). Only two ship. Azure SQL's spelling is
`AS ({expr}) PERSISTED`, and the T-SQL fake proves seam sufficiency without shipping the
engine — the member is what keeps the third cheap.

### D2 — `rollup` is a lock-then-recompute inside the writer's own transaction

Not a trigger. A trigger is per-engine DDL that would have to be generated, versioned and
migrated for every rollup, and the schema-diff engine has no step type for it; the ladder
allows either mechanism and the port already gives us the lock clause.

Order, in the child write's own transaction:

1. acquire the parent row's write lock using the dialect's lock clause — `FOR UPDATE` on
   PostgreSQL, **nothing** on SQLite;
2. write the child;
3. `UPDATE parent SET <rollup> = (SELECT <op>(<field>) FROM <child> WHERE <fk> = @parent)`.

Step 1 before step 3 is the whole correctness argument, and step 1 being a **no-op on SQLite**
is why the port renders it rather than the core.

`sum|count|avg|min|max` all go through the same recompute. A `total = total + delta` shortcut
is rejected: it drifts with no self-correction and is simply wrong for `min`/`max`.

**What this does not claim.** The recompute is unbypassable only for writes that go through
the data port. A direct `INSERT` into the child table by another application leaves the rollup
stale — which is the honest difference from `computed`, whose value the engine itself
maintains. Named here rather than discovered.

### D3 — the ladder is enforced at apply, not documented

`computed` and `rollup` on the same field is refused. A `rollup` naming a `from` entity that
does not reference this one is refused. A `computed` expression referencing another row is
already impossible — the `Computed` profile admits no `old.`/`new.` and no context.

## Acceptance

#21's DoD, plus what the spike showed those two facts must contain:

1. `total = unit_price * amount` **is a generated column** — asserted by the engine refusing a
   write to it, not by reading the value back.
2. **Adding a computed field to an entity that already holds a row** succeeds on both engines.
   Written first, because an empty table hides the SQLite refusal.
3. `sum(items.line_total)` stays consistent under concurrent child writes — **on PostgreSQL,
   with a widened window**, because SQLite cannot fail this and no delay means no defect.
4. The non-vacuity control for 3: the same fact with the lock step removed must go **red**.
5. The ladder end to end over `baas-analyza:1358`'s invoice: computed → rollup → before-hook →
   computed, in one descriptor. PR5b-1 landed the before-hook rung, so this is now assemblable.

## Open, for the maintainer

- **The on-read variant is not built.** The analysis names an aggregate-query-at-read
  alternative that needs no denormalisation but is not filterable. This design takes the
  stored/denormalised route because filtering and sorting on a rollup is what a Data API
  consumer will reach for first. If that is wrong, it is a descriptor-level choice
  (`rollup.materialize: false`) and a separate issue, not a change here.
- **A stale rollup after an out-of-band child write** has no repair path in this design. A
  `POST /admin/entities/{e}/rollups/rebuild` would be the obvious one and belongs to the
  Management API, not here.
