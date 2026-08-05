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
outcome must be identical on both engines (§0 principle 3), so SQLite performs the
create-new / copy / drop / rename sequence rather than Alvo emitting `VIRTUAL` there and
`STORED` on PostgreSQL. A `VIRTUAL` column is not stored, so it would silently differ in what
an index and a filter can do — the one thing the analysis names as the on-read variant's
drawback.

#### D1 revised — corrected by the third spike pass, against the product's migrator

The first two passes measured raw SQL. Measuring the **product's own migrator** moved three
things, and the paragraphs above were wrong about one of them.

**The rebuild does not have to be written — it has to be reached (Q7).** EF Core's SQLite
generator already implements create-new / copy / drop / rename, but it triggers on an
`AlterColumnOperation` and never on an `AddColumnOperation`. So the change is planned as **two
hops** — add the column plain, then alter it into a generated one — and EF emits the rebuild,
correctly omitting the generated column from its `INSERT … SELECT`. Measured against a table
holding a row: it succeeds, the value is computed, and a write to the column is refused. On
PostgreSQL the same two-hop emits `DROP COLUMN` + `ADD`, which also works but is more DDL than
the one-hop `ADD` that engine already accepts — **so the hop count is per engine, not global.**

**One dialect member is not enough (Q8), and this was a gap in the design rather than a
preference.** The two paths cannot come from the same mechanism: a rebuild needs every column's
DDL, which only EF's type mapping knows, so on SQLite **EF** spells the generated column; the
in-place `ADD` is a single statement, so the dialect member spells it, which is PostgreSQL's
path. The member from D1 keeps its job, and a **second default-implemented member** answers the
question the measurements actually demand — *can this engine add a stored generated column to a
table that already holds rows* — which is what selects one hop or two.

**A `computed` expression's literals become bind parameters, and DDL cannot carry one (Q9).**
`SqlPredicateRenderer` routes every non-boolean literal through the parameter bag, so
`unit_price * 1.2` renders `(… * @p0)`. Field-only arithmetic renders clean, and that covers
every example in the sources including `baas-analyza:1358`'s ladder. A `computed` expression
carrying a literal is therefore **refused at apply** with a message naming the literal, rather
than inlined: inlining would mean a second rendering path whose escaping rules nothing else in
this repository exercises, in DDL that is persisted in the schema. Widening it later is
additive; getting the escaping wrong once is not.

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

## What building it moved — the deviations, numbered, each with its measurement

Written from the implementation rather than from the plan, so a reader can tell a decision from an oversight.
Dev-1 and Dev-2 in the plan **no longer exist**, and that is the largest item here.

**Dev-1 and Dev-2 are withdrawn: spike Q7 is wrong against the product.** Q7 asserted that EF's SQLite
generator "triggers on an `AlterColumnOperation`, never on an `AddColumnOperation`", and the plan built a
two-hop diff plus a second dialect member (`GeneratedColumnAddRequiresTableRebuild`) on it. Measured through
the real migrator on EF Core 10: a **single**-hop diff from *plain column absent* to *computed column present*
already emits the whole create-new / copy / drop / rename rebuild. It never emits the bare
`ALTER TABLE … ADD COLUMN … STORED` the engine refuses. So the two-hop is unnecessary and strictly worse (an
extra plain `ADD` first), and the member answered a question nobody asks — a default interface member with no
consumer. Both are gone. The engine fact Q1 measured is still true; the inference drawn from it was not.

**Dev-7 — the rebuild's real missing piece was a foreign-key pragma outside the transaction, and no pass of the
spike found it.** EF emits `PRAGMA foreign_keys = 0` around its rebuild and marks those commands
transaction-suppressed, but `MigrationPlan.Sql` carries plain strings and cannot carry the flag — so the pragma
ran inside Alvo's single migration transaction, where SQLite documents it as a **no-op**. With foreign keys
still enforced, `DROP TABLE parent` performs an implicit `DELETE FROM`, firing `ON DELETE CASCADE` on every
reference to it. Measured: one invoice and one invoice item in, one invoice and **zero** items out. This is
**pre-existing** — any `AlterColumn` on a parent table with cascading children hits it, computed or not — and
#21 only made it reachable. Fixed by a **second** new dialect member, `IAlvoSqlDialect.MigrationFraming`, run
around the batch and restored in a `finally`. (Second, not third: Dev-1/Dev-2 withdrew the planned
`GeneratedColumnAddRequiresTableRebuild`, so this PR adds exactly two — `GeneratedColumnDefinition` and
`MigrationFraming`.)

**Dev-3 — `FOR NO KEY UPDATE`, not `FOR UPDATE`.** As planned. The recompute provably never touches the
parent's key, `PreImageMutation.Update` already means exactly that on this dialect, and the weaker mode still
conflicts with itself — which is the whole correctness argument. Asking for `PreImageMutation.Delete` to obtain
the literal words `FOR UPDATE` would serialise unrelated inserts against the parent for nothing.

**Dev-4 — a `computed` expression may not carry a bound value.** As planned, and reproduced in the product:
`unit_price * 1.2` renders `(<col> * @p0)`, and the field is refused naming the constant rather than inlined.
Inlining would put engine-specific literal escaping into DDL that is then persisted.

**Dev-5 — `rollup.where` is refused, not implemented.** As planned. `via` is implemented; `where` is an
unhonoured slot, because ignoring it aggregates every child instead of the declared subset.

**Dev-6 — the before-hook rung is not assemblable in this branch.** As planned. PR5b-1 is PR #160, still open,
so the ladder fact writes `vat_total` explicitly and its remarks name the gap and the assertion that replaces
the write once #160 merges. Merging #160 in to close it is refused: it would put an unmerged PR's ~40 files
into this diff.

**Dev-8 — the parent's lock is taken after the child write, not before it.** The design numbers it 1-2-3
(lock, write child, recompute) and then says "step 1 before step 3 is the whole correctness argument" — which
is what the measurement establishes; where the child write sits is free. Taking the lock immediately before the
recompute holds it for less time and puts every parent this write touches in one place **in id order**, which
is what stops two writers moving children between the same two parents in opposite directions from
deadlocking. Locking first would spread acquisition over three call sites with no ordering between them.

**Dev-9 — an update recomputes BOTH the parent it left and the parent it joined.** The design does not name
this case; it exists because the foreign key is writable, and only the child's *pre*-image knows the parent it
is leaving.

**Dev-10 — `MIN`/`MAX` over a decimal child column needs the driver's value repair.** Not in the design.
SQLite stores a `decimal` as `TEXT`, so `MIN('10.0','6.0')` answers `'10.0'`. `RollupRecompute` therefore routes
the aggregated column through `IFieldSqlRenderer.RenderComparableOperands` — the member that already owns that
repair, and whose own remarks call it an ordering key, which is exactly what an extreme-value aggregate is. It
is applied for every operation so there is one code path; the repair is a no-op wherever the storage already
orders correctly.

**Dev-11 — a payload naming a computed field is refused, not ignored.** Not in the design. The runtime model
marks a computed property store-generated, so EF omits it from the `INSERT` — without a guard the caller would
get a `201` whose body reports a different number, with nothing saying theirs was discarded. `WritePayloadGuard`
refuses it by name; the *engine's* refusal remains the guarantee for anything that reaches the column
otherwise.

**Dev-12 — `GeneratedColumnDefinition` is read for its nullness, as the migrator's capability gate.** Its
returned text is not spliced into any plan, because EF's own per-provider generator already emits the full
definition on both shipped engines (Q5–Q7) and a second author for correct DDL is a liability. The member's
documented contract — "`null` when the engine cannot express one, in which case the migrator refuses the field
and names the engine" — is honoured exactly as written.

**Dev-13 — a `decimal` computed field diverges per engine, and the store type is NOT the cause (fourth spike
pass).** `0.1 * 3` answers `0.30` on PostgreSQL and `0.30000000000000004` on SQLite, measured on both engines
through the port. The obvious reading — SQLite's shipped DDL names no store type, so the value lands as a float
while every other `decimal` column on the table is exact `TEXT` — is only half right, and the actionable half is
false:

- EF Core 10's SQLite migrations generator emits a computed column as `"col" AS (<expr>) STORED` and **drops the
  column type unconditionally**. Configuring `HasColumnType` on the property changes nothing: measured with the
  real store type and with a deliberately bogus one, the emitted `CREATE TABLE` was byte-identical both times
  (the golden snapshot stayed green). So neither the model builder nor `GeneratedColumnDefinition` can name it.
- And naming it would not move the value. SQLite has no decimal arithmetic: measured on the bundled provider,
  `'0.1' * 3` stores `0.30000000000000004` in an untyped, a `TEXT` **and** a `REAL` generated column alike, and
  `SUM` over the three answers identically. A `TEXT` affinity merely stores the double's own text — and for a
  16-digit value it stored one *extra* spurious digit (`12345678901234.561` against the untyped column's
  `12345678901234.56`).

So the residual divergence is SQLite's arithmetic plus the absent rounding to the field's declared scale, which
is the same limitation `SqliteFieldSqlRenderer`'s remarks already record for every decimal comparison on that
engine ("a storage change — a scaled integer — is the real fix and is a schema decision this port cannot make").
Closing it here would mean a new port member wrapping a computed expression per driver
(`CAST(ROUND(<expr>, <scale>) AS …)`), which is a public-contract addition with its own snapshot and contract-test
surface — a PR, not a line. **Decision: pin the measured state per engine (`SqliteComputedDecimalStorageTests`),
correct the dialect remark that read as though the shipped DDL named the type, and file the rounding as its own
issue.** The shared suite keeps choosing values exact in binary floating point, so what it asserts is what the
two engines genuinely agree on.


**Dev-14 — tenancy is part of the rollup contract, and the design did not name it at all.** Neither D2 nor D3
mentions `tenant_id`, and the first implementation carried no tenant predicate anywhere: the recompute wrote
`UPDATE <parent> … WHERE <id> = @parent` with the row id on the outer statement and the foreign key on the inner
aggregate, both unqualified by tenant. Because a `ref` is a foreign key on the parent's `id` alone — never
`(tenant_id, id)`, which `DescriptorModelBuilder.ConfigureReferences` makes explicit — two reachable descriptor
shapes broke:

- **scoped parent + scoped child** — a caller in tenant A creates a child whose `ref` names tenant B's parent,
  and the recompute writes B's row from an aggregate that includes A's child: a cross-tenant **write**.
- **global parent + scoped child** — every tenant's children aggregate into one globally readable row, so a
  `count` discloses how many rows other tenants hold and a `sum` discloses their values: a cross-tenant **read
  oracle**, the same class as the unique-index one (#137).

Every rollup fact in the branch used `Global`/`Global`, which is why the suite was green. Now:

1. **A tenancy-crossing pair is refused at apply**, naming both entities and their modes with a fix suggestion
   (`RollupResolver.EnsureTenancyDoesNotCross`), and refused again inside `RollupRecompute` as the fail-closed
   belt for a `SchemaModel` that never came through the mapper — the same shape as
   `EfAlvoData.EnsureNotSoftDeleted`. Supported shapes are therefore exactly two: scoped/scoped and
   global/global (a project with tenancy off resolves to no `tenant_id` at all, which is the same thing as
   global for every question here, so `null`-versus-`global` is not a crossing).
2. **Both statements of a scoped recompute carry the tenant predicate** — the parent's `UPDATE` and its locking
   read, plus the child aggregate's `SELECT`. The child's is **qualified by the child's table**: an unqualified
   `tenant_id` inside that subquery binds to the parent's column the moment the child has none, which is true
   for every child row and therefore aggregates every tenant's children with no error anywhere. The tenant value
   is the written child row's own `tenant_id` — the aggregation key, and a value the synthesized tenant scope has
   already checked — never the ambient context, which could narrow the parent to one tenant while aggregating a
   child from another.
3. **The wider hole is named rather than half-closed.** A scoped child may still name a parent in another tenant
   *at all*, because the foreign key does not span `(tenant_id, id)`. With the predicate in place that child now
   aggregates **nowhere** (the parent's `UPDATE` matches no row) instead of writing across the boundary. That is
   the conservative outcome, and the real fix is a change to every `ref` on every scoped entity — plus the
   accompanying question of whether the FK's existence check is itself an oracle — so it is filed as its own
   issue rather than widened into this PR. See *Open, for the maintainer*.

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
   computed, in one descriptor — **minus the before-hook rung, which is not assemblable in this branch**
   (Dev-6: PR5b-1 is PR #160 and still open, so the fact writes `vat_total` explicitly and its own remarks name
   the gap and the assertion that replaces the write once #160 merges).
6. **Cross-tenant isolation, per the security-core checklist.** A two-tenant adversarial fact in the shared
   suite, so both engines run it: tenant A's child write must not move tenant B's parent, and tenant A's rollup
   must not aggregate tenant B's children. Plus a fact per crossing direction that a tenancy-crossing rollup is
   refused at apply. Both mutations were run and both killed their facts — removing the tenant predicates made
   Acme's `sum` read 15 instead of 10 and 17 instead of 12 on **both** engines; removing the apply refusal made
   both refusal facts fail while the same-tenancy control stayed green.

## Open, for the maintainer

- **The on-read variant is not built.** The analysis names an aggregate-query-at-read
  alternative that needs no denormalisation but is not filterable. This design takes the
  stored/denormalised route because filtering and sorting on a rollup is what a Data API
  consumer will reach for first. If that is wrong, it is a descriptor-level choice
  (`rollup.materialize: false`) and a separate issue, not a change here.
- **A stale rollup after an out-of-band child write** has no repair path in this design. A
  `POST /admin/entities/{e}/rollups/rebuild` would be the obvious one and belongs to the
  Management API, not here.
- **A `ref` may name a row in another tenant, on every scoped entity** — the physical foreign key is on the
  target's `id` alone. This PR makes the *rollup* safe against it (the child aggregates nowhere rather than
  writing across the boundary) but does not close it: the reference is still stored, and the difference between
  "no such row" (a foreign-key violation) and "a row another tenant owns" (accepted) is observable, which is an
  existence oracle in the #137 family. Closing it means the foreign key spanning `(tenant_id, id)` for every
  scoped `ref`, which touches the migration model, the destructive-change classification and every existing
  descriptor. Filed as its own issue.
- **A `decimal` computed field is not exact on SQLite, and rounds to no scale.** `0.1 * 3` is `0.30` on
  PostgreSQL and `0.30000000000000004` on SQLite (Dev-13). The fix is the driver rounding a computed decimal
  expression to the field's declared scale, which needs a port member this design does not have. Filed as its
  own issue; the current behaviour is pinned per engine so it cannot drift unnoticed.
