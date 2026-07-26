# F3 PR2 de-risking spike — verdict: EF property bags, with named caveats

> **Verdict, in one sentence: option (a) — EF Core property-bag entity types with the
> policy predicate composed in via `FromSqlRaw` — works on both SQLite and PostgreSQL for
> every one of the eight questions, subject to three caveats that change how PR2 must be
> built: the hidden-field `SELECT` list needs a NULL-projection over an all-optional
> runtime read model (Q4), the `SqlPredicate` parameter prefix must never be the
> renderer's default `p` (Q6), and a change-tracker write can never carry a policy
> predicate, so `update`/`delete` must go through `ExecuteUpdate`/`ExecuteDelete` over a
> `FromSql` root (Q5).**

## How this was answered

Throwaway spike project: `spike/MMLib.Alvo.Data.Spike` (console app, registered in
`MMLib.Alvo.slnx`, `IsPackable=false`, deleted when PR2's implementation lands). Every
statement below is a captured real `DbCommand` — command text plus the provider's own
parameter collection, recorded by a `DbCommandInterceptor` — not a reasoned expectation.
Full transcripts:

- `spike/MMLib.Alvo.Data.Spike/evidence/sqlite.txt`
- `spike/MMLib.Alvo.Data.Spike/evidence/postgresql.txt`

Run it with `dotnet run --project spike/MMLib.Alvo.Data.Spike` (both engines; PostgreSQL
via Testcontainers `postgres:16-alpine`) or `-- sqlite` for the no-Docker subset.

Versions: EF Core 10.0.10, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, net10.0.

**No `src/` production code was modified.** The only tracked file touched outside `spike/`
is `MMLib.Alvo.slnx` (registering the spike project, required by
`SolutionConventionTests.Every_project_is_registered_in_the_solution`). `dotnet test` is
green: 764 passed / 0 failed / 3 skipped.

The predicates are **real**: the spike resolves PR1's `ICelCompiler` and
`IPredicateRenderer` out of `AddAlvo()` and renders `owner_id == @user.id` and
`tenant_id == @tenant.id` for the `Rule` profile against a real `EntitySchema`, through a
per-engine `IFieldSqlRenderer` written the way PR2's drivers would ship one.

## Results

| # | Question | SQLite | PostgreSQL |
|---|---|---|---|
| 1 | Raw-predicate composition over property bags works at all | **PASS** | **PASS** |
| 2 | Predicate lands in the `WHERE` of one statement, not a post-filter | **PASS** | **PASS** |
| 3 | Survives composition with a caller filter + `ORDER BY` + limit | **PASS** (EF wraps in a subquery; still one statement, predicate still innermost) | **PASS** |
| 4 | `SELECT` list can exclude a `hidden` field | **PASS only via NULL-projection over an all-optional read model**; omitting the column outright fails | same |
| 5 | Insert / update / delete + complete post-image in the same transaction | **PASS** | **PASS** |
| 6 | Parameters stay parameters, names disjoint from EF's | **PASS with `alvo_p`; FAILS SILENTLY with the default `p`** | **PASS with `alvo_p`; FAILS LOUDLY with `p`** |
| 7 | Policy check + data change + outbox row on one `DbTransaction` | **PASS** | **PASS** |
| 8 | Identifier quoting via `ISqlGenerationHelper` | **PASS, but the schema argument is silently dropped** | **PASS, but identifiers come back unquoted when Npgsql deems quoting unnecessary** |

Bonus questions, answered because they turned out to be load-bearing: **Q3c** explicit
`NULLS FIRST/LAST` (`AlvoSort.Nulls`), **Q3d** the keyset cursor predicate, **X1** exception
shapes, **X2** the F7 dynamic-entity rehearsal.

---

## Q0 — `DescriptorModelBuilder` already produces property bags

Not one of the eight, but it decides how much of F2 PR2 can reuse. `DescriptorModelBuilder`
calls `ModelBuilder.Entity(string)`; in EF Core 10 that is *already* a property-bag entity
type, not a CLR-less one:

```
ModelBuilder.Entity(string).ClrType   = System.Collections.Generic.Dictionary`2[[System.String, ...],[System.Object, ...]]
  IsPropertyBag                      = True
  HasSharedClrType                   = True
  id is an indexer property          = True
```

Identical on both engines. So the model shape F2 ships for the migrations differ is the
same shape the data path needs — `SharedTypeEntity<Dictionary<string, object>>(name)` is
just the explicit spelling of it.

## Q1 — PASS. Raw `WHERE` fragment + named parameters, rows read back as dictionaries

`DbSet<Dictionary<string, object>>.FromSqlRaw(sql, DbParameter[])` accepts a raw statement
whose `WHERE` is PR1's rendered predicate and materialises rows as dictionaries.

PostgreSQL:

```sql
SELECT * FROM "alvo_spike"."vehicle" WHERE (COALESCE("owner_id" = @alvo_p0, FALSE)) AND (COALESCE("tenant_id" = @alvo_t0, FALSE))
-- @alvo_p0  DbType=Guid  value=aaaaaaaa-0000-0000-0000-000000000001
-- @alvo_t0  DbType=Guid  value=11111111-0000-0000-0000-000000000001
```

SQLite (same call, the engine's own two-valued shape from `IFieldSqlRenderer`):

```sql
SELECT * FROM "vehicle" WHERE (COALESCE("owner_id" = @alvo_p0, 0)) AND (COALESCE("tenant_id" = @alvo_t0, 0))
```

**The CLR types match `IAlvoData`'s documented contract on both engines, for free.** On both:

```
CLR types: id:Guid, created_at:DateTimeOffset, is_active:Boolean, mileage:Int64,
           owner_id:Guid, plate:String, price:Decimal, secret_note:String,
           status:String, tenant_id:Guid
```

This is the strongest single argument for (a) over (b), and it is engine-specific: the
hand-rolled ADO.NET reader over the *identical* SQL (Q4d) gives, **on SQLite**,
`id:String, price:String, is_active:Int64, created_at:String`. Option (b) therefore has to
re-implement EF's SQLite type mapping on the read path just to satisfy `IAlvoData`'s
"`Guid` for a `uuid` field, `DateTimeOffset` for a timestamp, `decimal` for a `decimal`"
promise. On PostgreSQL the raw reader already returns `Guid/Decimal/Boolean`, so this cost
is invisible if you only test on PostgreSQL.

## Q2 — PASS. One statement, predicate in the `WHERE`

`statements executed: 1`; the interceptor saw exactly the text above. Alice sees 3 of the 4
seeded rows (`ACME-001, ACME-002, OTHR-001`); Bob's `ACME-003` is absent, and it is absent
because the engine never returned it, not because anything filtered afterwards.

## Q3 — PASS. Composition wraps the raw SQL in a subquery, and that is still correct

Policy predicate + tenant scope (raw) AND a caller filter AND `ORDER BY` AND a limit, all
in one statement. EF pushes the `FromSql` text into a derived table and adds its own
clauses outside it:

```sql
SELECT v.id, v.created_at, v.is_active, v.mileage, v.owner_id, v.plate, v.price, v.secret_note, v.status, v.tenant_id
FROM (
    SELECT * FROM "alvo_spike"."vehicle" WHERE (COALESCE("owner_id" = @alvo_p0, FALSE)) AND (COALESCE("tenant_id" = @alvo_t0, FALSE))
) AS v
WHERE v.status = @callerStatus
ORDER BY v.mileage
LIMIT @p2
```

The wrapping is **safe, and in one specific way better than concatenating**: the policy
predicate is in the innermost `FROM`, so no caller-supplied clause can be `OR`-ed alongside
it — the caller's terms can only ever narrow the derived table. SQLite produces the same
shape. One statement in both cases.

Two EF behaviours to know about, visible in the same capture:

- EF **inlines C# compile-time constants as SQL literals** (`WHERE v.status = 'open'` when
  the filter value was a literal; `WHERE v.id = 'dddddddd-…'` when it was a `static
  readonly` field) but **parameterizes closure-captured values** (`@callerStatus`). PR2's
  filter values always come from a runtime dictionary, so they parameterize — but a test
  that hard-codes a literal will snapshot inlined SQL and prove less than it looks.
- EF names closure parameters after the C# variable (`@callerStatus`, `@closureValue`,
  `@newId`, `@afterPlate`). Those names are *not* `@__p_0` any more, which widens the
  namespace PR2's prefix has to stay clear of (see Q6).

### Q3c — `AlvoSort.Nulls` has no native translation; the emulation works on both

`ORDER BY … NULLS FIRST` is not expressible in EF LINQ. The standard emulation
(`OrderBy(x => key == null ? 0 : 1).ThenBy(x => key)`) translates over a property bag, to
the same `CASE WHEN` on both engines:

```sql
ORDER BY CASE
    WHEN v.status IS NULL THEN 0
    ELSE 1
END, v.status
```

That is engine-agnostic and satisfies `AlvoSort.Nulls`' stated reason for existing — but it
is a `CASE` expression in `ORDER BY`, which **defeats an index** on the sort key. #19's
"p95 of a filtered list over 100k rows on an indexed column < 50 ms" is therefore at risk
whenever a nullable field is sorted. PostgreSQL supports native `NULLS FIRST/LAST`, so a
per-driver `ORDER BY` rendering (through the same `IFieldSqlRenderer` seam) buys back the
index on PostgreSQL. Recorded as a plan decision, not a blocker.

### Q3d — the keyset cursor predicate translates as-is

`(plate, id) > (@after_plate, @after_id)` written as the nested-OR form translates on both
engines, fully parameterized:

```sql
WHERE v.plate > @afterPlate OR (v.plate = @afterPlate AND v.id > @afterId)
ORDER BY v.plate, v.id
```

Row-value tuple comparison (`(a,b) > (x,y)`) has no LINQ form; the nested-OR expansion is
the one to build. It composes with the `FromSql` root without interference.

## Q4 — PASS only via NULL-projection over an all-optional read model

This is where the naive approach fails, on both engines.

**Q4a — omitting the hidden column from the `FromSql` `SELECT` list fails.** EF requires a
`FromSql` result set to contain every mapped property:

```
SELECT "id", "tenant_id", "owner_id", "plate", "status", "mileage", "price", "is_active", "created_at"
FROM "alvo_spike"."vehicle" WHERE COALESCE("owner_id" = @alvo_p0, FALSE)
```
```
System.InvalidOperationException
  The required column 'secret_note' was not present in the results of a 'FromSql' operation.
```

Identical message on SQLite. So "just don't select it" is not available.

**Q4b — a dynamic `Select` restricts the *outer* list only.** Building
`Select(e => new object[] { EF.Property<T>(e, f), … })` at runtime **does** translate over a
property bag, and EF emits exactly the requested columns in the outer `SELECT` — but the
inner `FromSql` is still `SELECT *`, so the hidden column is still read by the engine:

```sql
SELECT v.id, v.tenant_id, v.owner_id, v.plate, v.status, v.mileage, v.price, v.is_active, v.created_at
FROM ( SELECT * FROM "alvo_spike"."vehicle" WHERE COALESCE("owner_id" = @alvo_p0, FALSE) ) AS v
```

**Q4e — NULL-projecting the hidden column works, for a nullable column.**

```sql
SELECT "id", "tenant_id", "owner_id", "plate", "status", CAST(NULL AS text) AS "secret_note",
       "mileage", "price", "is_active", "created_at"
FROM "alvo_spike"."vehicle" WHERE COALESCE("owner_id" = @alvo_p0, FALSE)
```
```
rows: 3; secret_note = NULL
```

**Q4f — and fails on a `NOT NULL` column**, differently per engine, which is exactly the
kind of divergence principle 3 forbids:

| engine | exception |
|---|---|
| SQLite | `System.InvalidOperationException: The data is NULL at ordinal 3. This method can't be called on NULL values.` |
| PostgreSQL | `System.InvalidCastException: Column 'plate' is null.` |

**Q4g — with every property marked optional in the runtime model, it works on both.** The
recommended mechanism. Same SQL as Q4f, but the read model built with
`IndexerProperty(nullableClrType, name).IsRequired(false)`:

```
rows: 3; plate = NULL; status = open
=> a hidden NOT NULL column can be NULL-projected when the runtime model marks it optional
```

**Q4h — and the database's own `NOT NULL` still guards writes** through that same
all-optional model, so relaxing required-ness in the runtime model does not weaken write
validation:

| engine | on inserting NULL into a `NOT NULL` column |
|---|---|
| SQLite | `DbUpdateException` → `SqliteException: SQLite Error 19: 'NOT NULL constraint failed: vehicle.plate'` |
| PostgreSQL | `DbUpdateException` → `PostgresException: 23502: null value in column "plate" of relation "vehicle" violates not-null constraint` |

So: the hidden field's value never leaves the table, the key is dropped when the
`AlvoRecord` is assembled, and PR3's schema-derived validation plus the physical `NOT NULL`
remain the required-ness gate.

One rejected alternative, recorded so a later reader can tell a decision from an oversight:
declare **one property-bag entity type per (entity, visible-field-set)** mapped to the same
table (`SharedTypeEntity<…>("vehicle#public").ToTable("vehicle")`). It is legal and would
give a literally minimal `SELECT` list, but the visible-field set is per-caller-role, so it
multiplies the runtime model by the policy matrix and multiplies the EF model cache with it.
Not worth it against a `CAST(NULL AS …)`.

## Q5 — PASS. Insert, update, delete, and the complete post-image in one transaction

**Q5a — insert through the change tracker works**, with every value parameterized and
correctly typed (`DbType=Guid`, `DbType=Decimal`, `DbType=DateTimeOffset` on SQLite /
`DbType=DateTime` on PostgreSQL):

```sql
INSERT INTO alvo_spike.vehicle (id, created_at, is_active, mileage, owner_id, plate, price, secret_note, status, tenant_id)
VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9);
```

**Q5b — `ExecuteUpdate` over a `FromSql` root carrying the policy predicate works, in one
statement**, and — critically — the predicate is inside it:

```sql
UPDATE alvo_spike.vehicle AS v0
SET status = @p2
FROM (
    SELECT v.id
    FROM ( SELECT * FROM "alvo_spike"."vehicle" WHERE COALESCE("owner_id" = @alvo_p0, FALSE) ) AS v
    WHERE v.id = @newId
) AS v1
WHERE v0.id = v1.id
-- rows affected: 1
```

SQLite produces the same `UPDATE … FROM (…)` shape (requires SQLite ≥ 3.33, which
`SQLitePCLRaw.lib.e_sqlite3` 2.1.12 satisfies).

**Q5c — and the predicate actually denies.** Alice's `USING` predicate against Bob's row:
`rows affected: 0` on both engines. That is the `AlvoRecordNotFoundException` signal
`IAlvoData` needs, and it is indistinguishable from a non-existent id — which is what the
contract requires.

**Q5d — the change tracker is *not* usable for update/delete.** A tracked
`Attach`+set+`SaveChanges` builds its own `WHERE`, keyed on the primary key alone:

```sql
UPDATE alvo_spike.vehicle SET mileage = @p0 WHERE id = @p1;              -- PostgreSQL
UPDATE "vehicle" SET "mileage" = @p0 WHERE "id" = @p1 RETURNING 1;       -- SQLite
```

No policy predicate anywhere. **This is the single most dangerous affordance in option (a)**:
it is the shortest, most idiomatic EF code, it compiles, it passes a naive test, and it
bypasses policy completely. PR2 must make it unreachable (see *What this means for the
plan*).

**Q5h — the `ExecuteUpdate` setter list can be built at runtime**, which the port requires
(field names and CLR types are only known at request time). EF Core 10's non-expression
`ExecuteUpdateAsync(Action<UpdateSettersBuilder<T>>)` overload plus reflection over
`SetProperty<TProperty>` produced, for a five-field patch of five different CLR types:

```sql
UPDATE alvo_spike.vehicle AS v0
SET status = @p2, mileage = @p3, price = @p4, is_active = @p5, owner_id = @p6
FROM ( SELECT v.id FROM ( SELECT * FROM … WHERE COALESCE("owner_id" = @alvo_p0, FALSE) ) AS v WHERE v.id = @newId ) AS v1
WHERE v0.id = v1.id
-- rows affected: 1
```

All five setter values are bind parameters with the right `DbType`. This overload only
exists in EF Core 10; on EF 9 the same thing needs a hand-built
`Expression<Func<SetPropertyCalls<T>, SetPropertyCalls<T>>>` chain.

**Q5g — `ExecuteDelete` over the same root works**, as a single statement:

```sql
DELETE FROM alvo_spike.vehicle AS v
WHERE v.id IN ( SELECT v0.id FROM ( SELECT * FROM … WHERE COALESCE("owner_id" = @alvo_p0, FALSE) ) AS v0 WHERE v0.id = @newId )
-- rows deleted: 1
```

**Q5e — the complete post-image is readable in the same transaction after
`ExecuteUpdate`.** `BeginTransaction` → `ExecuteUpdate` → re-`SELECT` returns the mutated
row, before commit, and a rollback discards it:

```
post-image: { created_at=…, id=…, is_active=True, mileage=4242, owner_id=…, plate=NEW-001,
              price=1234.56, secret_note=new-secret, status=closed, tenant_id=… }
rolled back — the post-image was visible before commit
```

**Q5f — and `UPDATE … RETURNING *` gives the post-image in a single statement on both
engines** (SQLite ≥ 3.35 has `RETURNING`), if PR2 ever wants to skip the extra round trip:

```sql
UPDATE "alvo_spike"."vehicle" SET "status" = @new_status
WHERE "id" = @row_id AND (COALESCE("owner_id" = @alvo_p0, FALSE)) RETURNING *;
-- RETURNING gave 10 columns
```

Note the SQLite `RETURNING *` result set carries **storage** representations, not EF's
(`is_active=1`, `id=616E98D0-D537-…` uppercase text) — see Q6/X2. Reading a post-image
through the property bag (Q5e) avoids that entirely.

**Q5i — `SELECT … FOR UPDATE` composes with a property bag on PostgreSQL**
(`SELECT * FROM "alvo_spike"."vehicle" WHERE COALESCE(…) FOR UPDATE`, `rows: 4 (locked)`).
SQLite has no `FOR UPDATE`; a write transaction serializes instead. So a pre-image read
that a `WITH CHECK` decision will be based on can be locked on PostgreSQL and is
implicitly serialized on SQLite — a per-driver difference the `IFieldSqlRenderer`-style seam
does not currently cover.

## Q6 — PASS with `alvo_p`; the renderer's default prefix `p` is a real, silent bug

`IPredicateRenderer.Render`'s `parameterPrefix` defaults to `"p"`. **That default collides
with EF's own generated parameter names, and the failure mode is not an error.**

The spike passed the predicate's values as named `DbParameter`s *and* used one `{0}`
placeholder for a caller value — i.e. exactly what PR2 will do. With prefix `p`:

```sql
SELECT * FROM "alvo_spike"."vehicle" WHERE COALESCE("owner_id" = @p0, FALSE) AND "status" <> @p0
-- parameters:
--   p0     DbType=String  value='never'                                  <- EF's own, for {0}
--   @p00   DbType=Guid    value=aaaaaaaa-0000-0000-0000-000000000001     <- ours, RENAMED
```

EF minted its own `p0` for `{0}` and **silently renamed our `@p0` to `@p00`**, while the SQL
text still says `@p0` in both places. The policy predicate is now
`owner_id = 'never'` — the caller's value has been substituted into the security predicate,
and our bound value is never referenced.

| engine | outcome |
|---|---|
| SQLite | **no error**, `rows: 0` — silently wrong. Fail-closed here only because the predicate happens to be `=`; the same substitution under `<>`/`NOT` fails **open**. |
| PostgreSQL | `Npgsql.PostgresException: 42883: operator does not exist: uuid = text` — loud, because the substituted type differs. |

With prefix `alvo_p`, no collision on either engine:

```sql
SELECT v.id, … FROM (
    SELECT * FROM "alvo_spike"."vehicle" WHERE COALESCE("owner_id" = @alvo_p0, FALSE) AND "plate" <> @p0
) AS v
WHERE v.status = @closureValue
-- p0=never, @alvo_p0=<Guid>, @closureValue=open   -- rows: 2
```

**The names PR2's prefix must stay clear of, as observed:** `pN` (`FromSql` positional
args and `ExecuteUpdate` setters), and the **C# variable name of any closure-captured
filter value** (`@callerStatus`, `@closureValue`, `@afterPlate`, `@newId`). The second one
is open-ended, which argues for a prefix that no plausible identifier starts with. `alvo_`
is the natural choice, and `AlvoOptions.SchemaPrefix` already reserves that word.

The renderer's own validation is sound (Q6c): `p`, `alvo_p`, `_a`, `a1` accepted; `@p`,
`p-1`, `1p`, `""`, `p;--` all rejected with an `ArgumentException` naming the rule. So the
prefix is not an injection vector — it is a *collision* vector.

Values themselves always arrive as real bind parameters, never interpolated: every capture
above shows the predicate's values in the provider's parameter collection with a real
`DbType`.

**One binding trap, worth a line because it burned an hour of this spike (Q6d/X2c).** EF's
SQLite `Guid` mapping is `SqliteGuidTypeMapping`, store type `TEXT`, and
`GenerateSqlLiteral` emits **upper-case** `'AAAAAAAA-0000-0000-0000-000000000001'`. Binding
the `Guid` *value* (via `SqliteParameter` or via the mapping's `CreateParameter`) agrees
with that; hand-formatting the `Guid` to a lower-case string does not, and produces zero
rows with no error. The rule for PR2: **bind every `SqlPredicate` parameter through the
provider's own `RelationalTypeMapping`** (`IRelationalTypeMappingSource.FindMapping(clrType)`
→ `mapping.CreateParameter(command, name, value, nullable: true)`), never by formatting the
value yourself. On PostgreSQL, `GuidTypeMapping` / store type `uuid` makes the same mistake
throw (`42883: operator does not exist: uuid = text`).

## Q7 — PASS. One `DbTransaction` covers the policy read, the write, and an outbox row

`context.Database.BeginTransactionAsync()` →
`transaction.GetDbTransaction()` gives the real `SqliteTransaction`/`NpgsqlTransaction` on
the same `DbConnection`. In one transaction the spike ran the policy `SELECT` (through the
property bag), the `ExecuteUpdate` (through the property bag), **and** a hand-rolled
`INSERT` into a stand-in outbox table with `command.Transaction = dbTransaction`. Rollback
discarded all three:

```
DbTransaction: NpgsqlTransaction on connection True
policy SELECT inside the transaction: 1 row(s)
rolled back — verifying both the update and the outbox row vanished:
vehicle.status after rollback = open
outbox rows after rollback    = 0
```

Identical on SQLite. PR5's outbox row can therefore be written on the same
`DbTransaction` as the data change, whether it goes through EF or through raw ADO.NET.

## Q8 — PASS with two per-provider surprises

```
                                        SQLite                        PostgreSQL
implementation                          SqliteSqlGenerationHelper     NpgsqlSqlGenerationHelper
DelimitIdentifier("plate")              "plate"                       plate            <-- unquoted!
DelimitIdentifier("we\"ird")            "we""ird"                     "we""ird"
DelimitIdentifier("vehicle", "alvo")    "vehicle"                     alvo.vehicle     <-- schema dropped on SQLite
DelimitIdentifier("vehicle", null)      "vehicle"                     vehicle
GenerateParameterName("alvo_p0")        @alvo_p0                      @alvo_p0
StatementTerminator                     ;                             ;
```

Two things a plan must design around:

1. **`NpgsqlSqlGenerationHelper.DelimitIdentifier` returns the identifier unquoted when it
   judges quoting unnecessary.** It is not unsafe — it quotes and doubles `"` when needed
   (`we"ird` → `"we""ird"`) — but it means the same field renders differently per driver,
   and an unquoted identifier is case-folded by PostgreSQL. `IFieldSqlRenderer.RenderField`
   should therefore **always quote** (its own contract already says to treat `fieldName` as
   untrusted and never emit it verbatim) rather than delegate to `DelimitIdentifier`.
   Verified compatible: EF's own generated SQL and the always-quoted `FromSql` text address
   the same objects.
2. **A schema-qualified name is not portable.** SQLite silently drops the schema argument
   (it has no schemas; only `ATTACH`ed databases). `Fixture` used a real PostgreSQL schema
   (`alvo_spike`) precisely to check this, and `SELECT * FROM alvo_spike.vehicle` through
   `FromSqlRaw` works there. Since `AlvoOptions.SchemaPrefix` is a **table-name prefix**
   today (`alvo_descriptor_versions`), not a DB schema, nothing is broken — but PR2 must
   not introduce a DB schema, and the qualified table name must be produced by the driver,
   not by the core.

## X1 — exception shapes (recorded, not blockers)

`IAlvoData`'s failure contract must never reveal whether a row exists. What EF gives PR2 to
map from:

| provoked by | SQLite | PostgreSQL |
|---|---|---|
| unknown field in a LINQ `Where` | `InvalidOperationException: Translation of 'EF.Property<string>(StructuralTypeShaperExpression(…), "no_such_field")' failed. Either the query source is not an entity type, or the specified property does not exist on the entity type.` | identical |
| unknown column inside `FromSqlRaw` | `SqliteException: SQLite Error 1: 'no such column: "no_such_column" …'` | `PostgresException: 42703: column "no_such_column" does not exist` + `POSITION: 44` |
| unknown entity name | `InvalidOperationException: Cannot create a DbSet for 'Dictionary<string, object>' because it is configured as a shared-type entity type. Access the entity type via the 'Set' method overload that accepts an entity type name.` | identical |

Two consequences. First, **every one of these messages echoes schema internals** (the
offending column name, a query-shape dump, sometimes a character offset), so none of them
may reach a caller — which is why `IAlvoData` insists a filter/sort key is validated
against the entity's schema *before* the statement is composed, rather than relying on the
engine's own unknown-column error. Second, the engine-specific ones are *different types*
per provider, so the mapping to `AlvoAuthorizationException` /
`AlvoRecordNotFoundException` belongs in the shared EF layer, keyed on nothing
provider-specific.

## X2 — F7 dynamic entities: the mechanism carries over, with one uuid caveat

The same property-bag entity type, pointed by `FromSqlRaw` at a **JSON-projecting query**
over one shared partitioned table, materialises identically. PostgreSQL:

```sql
SELECT "id" AS "id",
       CAST("data" ->> 'tenant_id' AS uuid) AS "tenant_id",
       CAST("data" ->> 'owner_id'  AS uuid) AS "owner_id",
       "data" ->> 'plate' AS "plate", "data" ->> 'status' AS "status", "data" ->> 'secret_note' AS "secret_note",
       CAST("data" ->> 'mileage' AS bigint) AS "mileage",
       CAST("data" ->> 'price' AS numeric(18,2)) AS "price",
       CAST("data" ->> 'is_active' AS boolean) AS "is_active",
       CAST("data" ->> 'created_at' AS timestamptz) AS "created_at"
FROM "alvo_spike"."alvo_records"
WHERE "entity" = @entity_name AND COALESCE(CAST("data" ->> 'owner_id' AS uuid) = @alvo_p0, FALSE)
-- rows: 1  { …, owner_id=aaaaaaaa-…, plate=DYN-001, … }
```

This is a genuinely good sign for the port: `FromSql`'s table source can be an arbitrary
query, so a dynamic entity is *a different `FromSql` prefix*, not a different data path.
The adversarial suite would run over it unchanged.

**The one finding**: the SQLite leg of the same query returned **0 rows**, and X2b/X2c
isolate why. SQLite stores a `Guid` as upper-case `TEXT`, so an EF-bound `Guid` parameter
arrives as `'AAAAAAAA-…'`, while `json_extract(data, '$.owner_id')` returns whatever
case the JSON payload holds (lower-case, here). Binding the same value as lower-case text
returns the row. So **F7's dynamic driver must normalize `uuid`-typed JSON paths per
engine** — a `CAST(… AS uuid)` on PostgreSQL, an explicit case normalization (or storing
`uuid` values upper-cased) on SQLite. This is invisible if the dynamic driver is only ever
tested on PostgreSQL, and it is precisely the class of bug the differential suite exists to
catch, so it belongs in F7's plan as a named test, not as a discovery.

---

## What this means for the plan

### The mechanism PR2 should implement

1. **One EF `DbContext` over a runtime property-bag model.** Build it from `SchemaModel`
   with `SharedTypeEntity<Dictionary<string, object>>(entity.Name)` +
   `IndexerProperty(clrType, field.Name)`, using the same CLR-type mapping
   `DescriptorModelBuilder.ClrType` already has. F2's `builder.Entity(string)` is already
   this shape (Q0), so the type mapping is shared, not duplicated.
2. **Two runtime models, or one all-optional one.** The read path needs every property
   optional so a hidden column can be NULL-projected (Q4g); the physical `NOT NULL` still
   guards writes (Q4h). Simplest: build the model all-optional and let the database and
   PR3's validation own required-ness. Either way this needs a
   **custom `IModelCacheKeyFactory`** — EF caches one model per `DbContext` CLR type, so a
   second runtime model (or a re-applied descriptor) silently reuses the first. The spike
   had to add one (`SpikeModelCacheKeyFactory`) the moment it built two.
3. **Read path** — `Set<Dictionary<string, object>>(entity).FromSqlRaw(sql, parameters)` where
   `sql` is `SELECT <projection> FROM <driver-qualified table> WHERE (<Using>) AND (<TenantScope>)`,
   `<projection>` names **every** mapped column with `CAST(NULL AS <type>) AS <col>` in place
   of each `PolicyDecision.HiddenFields` entry, then LINQ `.Where` for the caller filter,
   `.OrderBy/.ThenBy` for sort, `.Take` for the limit. One statement, predicate innermost
   (Q2, Q3). Drop the hidden keys when assembling the `AlvoRecord`.
4. **Write path** — `CreateAsync` via the change tracker (`Add` + `SaveChanges`);
   `UpdateAsync`/`DeleteAsync` via `ExecuteUpdateAsync`/`ExecuteDeleteAsync` **over the
   `FromSql` root that carries the `USING` predicate**, with the setter list built at
   runtime through EF Core 10's `Action<UpdateSettersBuilder<T>>` overload (Q5b, Q5g, Q5h).
   `rows affected == 0` is the `AlvoRecordNotFoundException` signal.
5. **`WITH CHECK`** — inside one `BeginTransactionAsync`: read the pre-image under `USING`
   (with `FOR UPDATE` where the driver supports it, Q5i), merge `values` over it, evaluate
   the check with PR1's in-memory `IPredicateEvaluator`, then `ExecuteUpdate` still
   constrained by `USING`, then re-read the post-image for the response. Prefer this over
   "write, read the post-image, roll back if the check fails" (Q5e proves that works too):
   the merge-then-check order keeps the decision in the engine-agnostic core, and does not
   use a rollback as control flow.
6. **Bind every predicate parameter through the provider's own type mapping** —
   `IRelationalTypeMappingSource.FindMapping(value.GetType())` →
   `CreateParameter(command, "@" + name, value, nullable: true)`. Never format a value into
   a string (Q6d, X2b).
7. **Parameter prefixes**: `alvo_u` (`Using`), `alvo_c` (`WithCheck`), `alvo_t`
   (`TenantScope`) — three disjoint prefixes for the three predicates a `PolicyDecision`
   carries, none of them `p`.

### The seams it needs

- **`IFieldSqlRenderer` per driver** (already the port): always-quote `RenderField`;
  per-dialect two-valued shape; `RenderCaseInsensitiveLike`. **Do not** route `RenderField`
  through `ISqlGenerationHelper.DelimitIdentifier` (Q8).
- **A driver-owned qualified-table-name function.** The core must never assemble
  `schema.table` — SQLite has no schema (Q8). Natural home: alongside `IFieldSqlRenderer`,
  or a `RenderTable(EntitySchema)` member on it.
- **A driver-owned `ORDER BY` renderer**, if #19's p95 target is to survive a nullable sort
  key: `NULLS FIRST/LAST` natively on PostgreSQL, the `CASE WHEN` emulation elsewhere (Q3c).
- **A driver-owned row-lock hint** for the pre-image read (`FOR UPDATE` vs nothing, Q5i).
- **A parameter binder** wrapping `IRelationalTypeMappingSource` (item 6 above) — one type,
  shared by both drivers, in `MMLib.Alvo.Data.EntityFrameworkCore`.
- **`IModelCacheKeyFactory`** keyed on the applied descriptor version (item 2).
- **An `internal`, non-bypassable data path.** The one thing this spike would insist a
  reviewer check: `SaveChanges` on a tracked, mutated property bag emits
  `UPDATE … WHERE id = @p1` with **no policy predicate** (Q5d). The `DbContext`, its
  `DbSet`s and its change tracker must not be reachable from outside the `IAlvoData`
  implementation, and read queries must be `AsNoTracking()` so a returned row cannot become
  a tracked entity. This is a testable invariant, not a convention — worth an architecture
  test.

### Caveats a plan author must design around

1. **The predicate is composed as text, so it is only safe because `SqlPredicate`
   guarantees it is.** Nothing in the EF path re-validates it. The `parameterPrefix`
   collision (Q6) is the proof that a mistake here produces working, wrong SQL rather than
   an error — on SQLite, with no diagnostic at all.
2. **EF inlines compile-time-constant filter values as SQL literals** (Q3). A golden-SQL
   snapshot written with literal filter values will freeze inlined SQL and will not prove
   that runtime values parameterize. Snapshot tests must drive values through variables.
3. **`ExecuteUpdate`/`ExecuteDelete` do not go through the change tracker**, so they do not
   fire `SaveChanges` interceptors and do not participate in optimistic concurrency. PR5's
   hooks and outbox must be sequenced explicitly on the transaction (Q7), not hung off
   `SaveChanges`.
4. **Hidden required fields.** The all-optional read model (Q4g) is what makes them work;
   if PR2 instead keeps the schema-faithful model, a `hidden` + `required` field throws
   with two different exception types per engine (Q4f).
5. **SQLite `Guid`/`bool`/`decimal`/`DateTimeOffset` are all stored as `TEXT`/`INTEGER`**,
   and only EF's mapping knows how. Any place PR2 writes SQL that compares against a stored
   value — the outbox, `RETURNING *`, F7's JSON paths — has to use the mapping, not string
   formatting (Q5f, X2).
6. **`RETURNING *` read through raw ADO.NET returns storage representations**, not the CLR
   types `IAlvoData` promises, on SQLite (Q5f). Read post-images through the property bag.
7. **Option (b) is a real fallback but a more expensive one than the design assumed.** It
   is not just "hand-built parameterized ADO.NET plus `ISqlGenerationHelper` for quoting":
   on SQLite it also means re-implementing EF's read-side type mapping to satisfy
   `IAlvoData`'s CLR-type contract (Q4d vs Q1). Recorded so the fallback is costed
   honestly if PR2 ever has to take it.

## Open risks

- **Model invalidation on descriptor re-apply is not proven.** The spike added an
  `IModelCacheKeyFactory` to hold two models at once, but never re-applied a descriptor and
  re-queried. A stale EF model after a runtime schema change would be a live correctness
  bug in embedded mode. First thing PR2 should test.
- **`ExecuteUpdate`'s `UPDATE … FROM (subquery)` shape is not universal SQL.** It works on
  PostgreSQL and on SQLite ≥ 3.33. Azure SQL / T-SQL — which principle 3 names — was not
  tested here at all; EF emits a different shape there and it may or may not accept a
  `FromSql` root. Not a PR2 blocker (no T-SQL driver exists yet), but it is the second
  place after `IFieldSqlRenderer`'s three default members where a T-SQL driver will need
  its own answer.
- **`ORDER BY CASE WHEN` and #19's p95 < 50 ms over 100k rows.** Q3c shows the portable
  null-placement emulation defeats an index on the sort key. Nobody measured it. Either
  measure it in PR3 with the emulation, or make the driver-owned `ORDER BY` renderer part
  of PR2's scope.
- **Concurrency of the pre-image/`WITH CHECK`/write sequence on SQLite.** `FOR UPDATE`
  does not exist there; the spike relied on the assertion that a write transaction
  serializes, and did not run a concurrent race. #21's rollup race test will need this
  answered anyway.
- **`RenderCaseInsensitiveLike`'s wildcard-escaping contract is still open** (recorded as
  *Deviations* 7 in the F3 design). The spike did not exercise `like`/`ilike` at all, so it
  neither confirms nor closes that gap; PR2 still has to decide whether a caller-supplied
  literal may carry `%`/`_`.
- **The `FromSql` subquery wrapper and query-plan quality** were not measured. On both
  engines the wrapper is a derived table the optimizer should flatten, but "should" is not
  an `EXPLAIN`. Worth one `EXPLAIN` per engine before #19's performance criterion is
  claimed.
