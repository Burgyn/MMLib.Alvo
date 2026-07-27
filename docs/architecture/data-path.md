# The data path

How an Alvo read or write becomes one SQL statement, and the decisions that shape it. Written during F3 PR2
(#20).

> **Status: complete for PR2.** Everything below describes what the code does today, on the branch that
> closes #20. Where a decision was deliberately deferred it says so and names the phase that owns it — see
> *What later work inherits* at the end, which is the one place a PR3, PR5 or F7 author should start.

## One statement, one `WHERE`

Every read composes exactly one statement, in `ReadStatementComposer`: a `SELECT` list, a `FROM`, and a
`WHERE` whose terms are the resolved `USING` predicate, the synthesized tenant scope, and then — only ever
`AND`-ed onto those, each fully parenthesised — a row id, the caller's filter and a keyset cursor. Nothing a
caller supplies can reach the policy term's nesting level, and a snapshot of that one string is the proof
that the policy predicate is in the `WHERE` clause rather than applied afterwards.

A `hidden` field is not omitted from the `SELECT` list — EF refuses a `FromSql` result set missing a mapped
property — it is projected as a typed SQL `NULL` under its own alias, so the column is never read.

**`ORDER BY` and `LIMIT` are in that same statement, and nothing is composed over the root in LINQ.** That is
a deliberate reversal of the plan's own *Deviations* 4, which put the ordering in a LINQ chain because EF
wraps a `FromSql` body in a derived table whose row order is not guaranteed to survive. The reversal is
sound because the objection only bites when something *is* composed: with the whole statement in the raw
text and a bare `ToListAsync()` over it, EF runs the text verbatim and there is no derived table. Measured,
not assumed — see *A page's order and its boundary must be the same sequence* below.

A read whose row order is not observable — no sort key, no limit, no cursor — gets no `ORDER BY` at all;
`AlvoQuery.Sort` documents that order as implementation-defined. Every other read is ordered by the caller's
keys and then, always ascending, by the row key, because the keyset boundary's own tie-breaker is exactly
that comparison.

## Every bind-parameter name is reserved, and none of them starts with `p`

One statement carries values contributed by several independent renderers that never see each other's
output. Their names may not collide, and a collision here raises nothing. `PolicyParameterPrefix` is the
single declaration of every name this data path can generate:

| Name / prefix | Carries | Contributed by |
|---|---|---|
| `alvo_u` | the resolved `USING` predicate's values | `IPredicateRenderer.Render(decision.Using, …)` |
| `alvo_c` | the `WITH CHECK` predicate's values — **reserved, never rendered in PR2** | nothing; see below |
| `alvo_t` | the synthesized tenant scope's values | `IPredicateRenderer.Render(decision.TenantScope, …)` |
| `alvo_f<n>` | the caller filter's bound values | `FilterSqlRenderer` |
| `alvo_k<n>` | the keyset boundary's bound values | `KeysetSqlRenderer` |
| `alvo_id` | the row id of a single-row read — a `get`, and an update's pre-image | `ReadStatementComposer.AddRowId` |
| `alvo_limit` | a page's row limit — bound, never formatted into the text | `ReadStatementComposer` |

A write's own row id is **not** in that table: `update` and `delete` match the row with a LINQ `Where` over
the policy root, and EF names that parameter after the C# local (`@id`). It cannot collide because every name
this data path generates begins with `alvo_` — see *Writes never reach a change tracker* for the emitted
shapes.

**Why not `p`.** Spike `Q6` bound a value under `@p0` and EF, which mints `p0`, `p1`, … for its own
positional `FromSql` arguments and `ExecuteUpdate` setters, **renamed our parameter** while leaving the SQL
text reading `@p0`. Nothing raised. On PostgreSQL that usually resurfaces later as a type error; on SQLite
the security predicate simply compares against the caller's value and returns the wrong rows. PR1 acted on
that finding by changing `IPredicateRenderer.Render`'s own default to `alvo_p` (commit `54d612c`), so the
default is already safe; what this package must additionally do is **pass an explicit prefix at every call
site**, because a `PolicyDecision` carries three predicates, each render numbers its parameters from zero,
and one shared default would bind two different values to `alvo_p0`.

The invariant is a test rather than a convention. `PolicyParameterPrefixTests` reflects over every `const
string` the type declares and compares that to `All` — so a seventh name someone forgets to list fails
instead of escaping every other check — then asserts that **no reserved name is a prefix of another** (not
merely that they differ: `alvo_u` and `alvo_u2` would both mint `alvo_u0`), that none starts with `p`, and
that `alvo_p` — the renderer's own shipped default, the name a forgotten explicit prefix actually produces —
neither prefixes nor is prefixed by any of them. `ReadStatementComposer.Collect` is the belt: it refuses a name
two fragments both claim rather than letting last-writer-wins return wrong rows quietly.

`alvo_c` is declared and unused on purpose. `WITH CHECK` is evaluated in memory over the merged post-image
(see *Writes never reach a change tracker*), so no SQL carries it today; reserving the name now means a
future SQL-side check — a `RETURNING`-based write, say — inherits a name that already cannot collide with
the other two.

## The read model is all-optional, and that is what makes masking possible

`AlvoDataContext`'s model is built at request time from the applied `SchemaModel` as
`SharedTypeEntity<Dictionary<string, object>>` property bags — a record has no CLR type, so there is no
entity class to map. **Every `IndexerProperty` is declared with the nullable CLR type and `IsRequired(false)`,
whatever the column's own nullability says.**

That is not laziness, it is the only shape that lets a `hidden` field be removed from a response. The mask
works by projecting `CAST(NULL AS <store type>) AS <column>` in the `SELECT` list, so the column is never
read; a *required* property would then make EF's shaper throw on that `NULL` — and spike `Q4f` measured it
throwing **a different exception type on each engine** (`InvalidOperationException` on SQLite,
`InvalidCastException` on PostgreSQL), which §0's engine-agnostic principle forbids outright. Omitting the
column from the `SELECT` list instead is not available: EF refuses a `FromSql` result set that is missing a
mapped property.

Two things follow, and both are load-bearing:

- **Required-ness is still enforced, just not here.** The physical `NOT NULL` the migrator created enforces
  it on the write path (spike `Q4h`), and PR3's schema-derived request validation enforces it above this
  port. What the all-optional model gives up is only the *shape* of the failure, which is why a missing
  required value surfaces as `DbUpdateException` — recorded in the failure contract below as a named PR3
  edge, not an accident.
- **The row key is the one property that does not stay optional.** EF re-marks a key property required
  however `IsRequired(false)` asked, so a projected `NULL` for `id` throws at materialization. `ReadProjection`
  therefore refuses to mask the key and `QueryFieldGuard` refuses a hidden set containing it —
  `A_mask_that_hides_the_row_key_is_refused`, `A_model_with_no_key_at_all_is_refused_rather_than_masked` and
  `The_row_key_is_never_masked_however_the_mask_arrived` are the fail-closed belt, which matters because a
  `SchemaModel` may one day arrive from somewhere other than the descriptor (F7's dynamic registry).

The rejected alternative, from the spike: one entity type per *(entity, visible-field-set)* mapped to the
same table. Legal, and schema-faithful — but the visible set is per-caller-role, so the EF model cache would
multiply by the policy matrix.

The model also configures **no foreign keys and no navigations**. F2's `DescriptorModelBuilder` owns the
physical relationships; here a `Ref` field is simply a `uuid` column, and relation embedding is not part of
this query path.

## Identifiers are quoted by one helper, and there is no database schema

`AlvoSqlIdentifier.Quote` is the single implementation of double-quote escaping, and every driver's
`RenderField`, `RenderColumn` and `RenderTable` goes through it. **EF's `ISqlGenerationHelper.DelimitIdentifier`
is banned** (spike `Q8`): `NpgsqlSqlGenerationHelper` returns an identifier *unquoted* whenever it judges
quoting unnecessary — which PostgreSQL then case-folds, so one field renders differently per driver — and
SQLite's helper *silently drops* the schema argument it is handed. A driver always quotes, unconditionally,
because a field or entity name may have been assembled programmatically by a host and is therefore untrusted.

**Only the `DelimitIdentifier` half is banned.** `PredicateParameterBinder` deliberately *does* read
`ISqlGenerationHelper`'s parameter marker, so the name it creates and the name `IFieldSqlRenderer.RenderParameter`
writes into the text cannot drift apart — hardcoding `@` would be a second authority for one decision, and a
driver whose marker is `:` would emit a statement referencing a parameter that was never supplied. A sigil is
dialect syntax; a quoting judgement is a correctness decision, and only the second one is unsafe to delegate.

**PR2 introduces no database schema.** `AlvoOptions.SchemaPrefix` is a framework-*table* name prefix, not a
`CREATE SCHEMA` name — SQLite has no schemas at all — and a qualified table name is produced by the driver's
`RenderTable`, never assembled by shared code. That keeps the one construction spike `Q8` showed to be
per-engine-divergent out of the shared layer entirely.

## A page's order and its boundary must be the same sequence

A keyset page is correct only while its `ORDER BY` and its cursor predicate describe the same total order.
Both are therefore rendered from one seam — `IFieldSqlRenderer.RenderComparableOperands`, at the sort
column's own `CelFieldType` — by `SortSqlRenderer` and `KeysetSqlRenderer` respectively. The ordering
operand asks the pair-returning port with the same operand on both sides and takes either; the repair is
symmetric by contract, so that is the same seam rather than a second reading of it.

**What was measured, against a real SQLite database.** EF's LINQ `ORDER BY` over a `decimal` property does
translate — the plan suspected a translation error and there is none — but it translates to
`ORDER BY "price" COLLATE EF_DECIMAL`, EF's own collation, which orders **exactly**. The keyset boundary
compares the driver's `CAST(… AS REAL)` repair, which orders **approximately**. Those are two different
orders, and they disagree wherever a `decimal(18,2)` exceeds double's 53-bit mantissa: two values the
collation separates, the repair ties. A page then does not merely mis-sort — the boundary excludes a row the
order placed after the anchor, and the row is **skipped**. Reproduced as a failing test by rendering
`COLLATE EF_DECIMAL` into the `ORDER BY` while leaving the boundary repaired
(`SqliteAlvoDataDecimalPagingTests.Two_prices_that_collide_in_the_repaired_space_are_still_both_walked`).

The suite walks every page one row at a time over prices `2, 9, 10, 100` — lexically `10, 100, 2, 9`, so a
lost repair on *either* side is visible as a skipped row rather than as a mis-sort. Both directions are
verified by mutation.

**The obligation is stated on the port member itself**, not only here: `RenderComparableOperands`' remarks now
name ordering as its second consumer and spell out that the repair has to be *order-preserving*, because a
third-party driver reads that member and nothing else. `LOWER(x)` is the named trap — a sound comparison
repair and a wrong ordering key.

### Rejected alternative: EF's `EF_DECIMAL` collation on both sides

`ORDER BY "price" COLLATE EF_DECIMAL` is what a LINQ `OrderBy` emits, and using it on *both* sides — the
ordering and the boundary — would have been **strictly better on exactness**: the collation compares parsed
decimal values, so it orders correctly at every magnitude and removes the documented 53-bit / ±90-trillion
cliff, while leaving the operand `TEXT` and so no worse for indexability than the `CAST`. It was still
declined, and the reasons belong here so a later reader does not re-derive it as an obvious improvement:

1. **`EF_DECIMAL` is a collation EF registers on the connection** — not a SQLite feature and not part of EF's
   public API. Writing it into Alvo's SQL text couples the driver to an undocumented EF internal whose rename
   surfaces as `no such collation sequence` at query time, **on the security path**.
2. **A collation only applies `TEXT`-vs-`TEXT`.** It repairs a comparison only if the parameter is bound as
   EF's decimal `TEXT` representation too, which was not the case while values bound by their own CLR type —
   so adopting it was not even available as a local change until the binding was fixed.
3. It is SQLite-only and would live behind the same port member, so the port shape is unaffected. The coupling
   *is* the whole cost, which makes this a judgement call rather than a mistake — and one worth revisiting if
   EF ever documents the collation.

### `ORDER BY` is engine-divergent for two types, and only one of them needs a repair

Ordering and boundary agree *with each other* on each engine — that is the point above and it holds. Agreement
*between* engines is a separate property, which §0's engine-agnostic rule wants and which rendering an
`ORDER BY` newly makes observable. `RenderComparableOperands` repairs `Decimal` only, so:

- **Timestamps — no repair, because there is nothing left to repair.** Once every timestamp is normalised to
  UTC (see *Every timestamp is one instant* below), SQLite's stored `TEXT` orders as an instant and the
  ordering and the boundary compare the same unrepaired operand. The suite proves that positively rather than
  by omission: inverting `RenderComparableOperands` so it repairs everything *except* `Decimal` fails six of
  `AlvoDataOrderingTests`' facts, the timestamp ones included.

  The reason lexical order equals instant order is **not** that the text is fixed-width, which is what an
  earlier revision of this document claimed. The date and time components are zero-padded, but the fraction is
  not: EF formats it with `.FFFFFFF`, which trims trailing zeros and drops the separator entirely at a whole
  second, so one column really does hold `…00:00:00+00:00`, `…00:00:00.1+00:00` and `…00:00:00.12+00:00`. It
  works because the offset terminator `+` (0x2B) sorts *below* every digit (0x30–0x39) under SQLite's `BINARY`
  collation, which makes a shorter trimmed fraction correctly compare less than a longer one extending it.
  That property is load-bearing and would break if the stored form ever gained a `Z` suffix (0x5A, above every
  digit), so `Paging_over_sub_second_timestamps_keeps_instant_order` asserts it instead of leaving it as an
  argument here.
- **Strings — divergent. See *Collation belongs to the host* below**, which is the one place both
  string-collation decisions are ruled on together.

**Null placement** is the portable `CASE WHEN <key> IS NULL THEN 0/1 ELSE 1/0 END` emulation (spike `Q3c`),
because SQLite and PostgreSQL disagree on where `NULL` sorts for a given direction. It is known to defeat an
index on the sort key; that cost belongs with the latency criterion, which #19 owns. The `IS NULL` test reads
the raw column, not the repaired one — a cast `NULL` is still `NULL`.

**A paged read over a nullable sort key is refused, not answered.** `KeysetSqlRenderer` models no null
placement of its own: its boundary is a chain of comparisons with no `IS NULL` arm, so a `NULL` on either side
makes the term `NULL` and a `WHERE` treats that as false. Under `nullslast` the null-keyed tail became
unreachable; under `nullsfirst` the first page's anchor had a null key and page two came back empty. Paging
just stopped, silently.

The design's ruling is that a nullable sort column must declare its null placement **or be rejected**, and
`AlvoSort.Nulls` alone cannot deliver the first half while only the `ORDER BY` honours it — so
`EfAlvoData.EnsureSortKeysCanBePaged` takes the second: a read with a `Limit` or an `After` whose sort key
names a `Nullable` field is refused with an `ArgumentException`. That is the port's malformed-query channel,
not an authorization refusal — the field is one the caller can read, nothing is hidden, and a request layer
above this port turns it into a 422 with a fix suggestion.

Scoped to a paged read deliberately: an **unpaged** sorted read has no boundary, so its ordering over nulls is
already correct and stays legal. Making such a page work needs an `IS NULL`-aware boundary whose predicate
form depends on the anchor's own null-ness (so `KeysetAnchor` has to carry it), which doubles that renderer's
test matrix and must stay in lockstep with `SortSqlRenderer`'s rank expression or it reintroduces exactly the
order/boundary divergence above. **PR3 owns that**, together with the paging surface and the cursor contract.

The consequence for fixtures is real and worth knowing: a suite that pages has to sort by a **required**
column, which is why `AlvoDataWorlds` grew a required `label` on `notes` and a purpose-built `ledger` entity
whose `amount` and `occurred_at` are both required.

**It is a rule of the port, not of one backend.** `AlvoDataAdversarialTests`
`A_paged_read_sorted_by_a_nullable_field_is_refused_rather_than_dropping_rows` is inherited, so every
implementation is held to it — and `InMemoryAlvoData` refuses too, although it compares rows in memory and
could page over a null key correctly. That is deliberate: a reference implementation answering where the
shipped backends refuse would give the port two contracts, and a driver author reading the inherited suite
would learn the wrong one. Its sibling fact pins that an **unpaged** sorted read still answers, so the refusal
cannot be implemented as "reject a nullable sort key".

### Collation belongs to the host — two rulings that need the maintainer's sign-off

Two places where a string's *collation* decides an answer, grouped because they are one question and because
**neither is ratified by anyone but the implementing agent**. Both are read-visible: they change which rows a
caller sees or in what order, not whether they are authorized to see them.

**1. `ORDER BY` over a string diverges between engines, and is deliberately left alone.** SQLite compares
`TEXT` with `BINARY` collation; PostgreSQL uses the database collation, where `'a' < 'B'`. So one
`AlvoSort("title")` yields a different first page on the two engines. *Verdict: acceptable, not a defect to
repair here.* Collation is a property of the **database a host configures**, not of Alvo's rendering; forcing
one (`COLLATE "C"`, `COLLATE BINARY`) would override an operator's deliberate choice and make every string
sort non-sargable on PostgreSQL. It is also already the reason relational operators on a string are refused in
the Rule profile (`CelTypeChecker`: *"collation-dependent and are not available"*), so refusing to *order* by
a string would be inconsistent with allowing it to be sorted at all. What matters for correctness is that the
boundary uses the identical unrepaired operand, so a page is self-consistent on each engine — which it is.
This is the one place a read's **contents** differ per engine, so it is a knowing, narrow exception to §0
principle 3 rather than an oversight.

**2. `like` is case-sensitive on both engines; `ilike` guarantees ASCII folding and nothing more.** This one
*was* a defect and is fixed. Measured on both real engines:

| expression | SQLite | PostgreSQL 16 |
|---|---|---|
| `'ACME' LIKE 'acme'` | `1` — match | `f` — no match |
| `upper('čé')` | `čé` (ASCII-only folding) | `ČÉ` |

So `plate=like.acme%` returned rows on SQLite that the identical deployment on PostgreSQL did not — silently,
per request, on a channel the caller controls, and a **superset** where a filter is used as a coarse
allow-list above the port. Unlike ordering, this is not a collation an operator configured: it is SQLite's
`LIKE` operator's own documented behaviour and cannot be configured away per query.

*Ruling, in two parts:*

- **`like` is case-sensitive on every engine.** That is standard SQL's meaning, it is what
  `AlvoFilterOperator.Like` documents, and it is PostgreSQL's — so SQLite is the engine that moves.
  `SqliteCaseSensitiveLike` runs `PRAGMA case_sensitive_like = ON` on every connection this driver opens.
  Rejected alternative: render something case-sensitive instead. The only case-sensitive matching SQLite can
  express in an operator is `GLOB`, whose wildcards are `*`/`?`/`[…]`, so adopting it means translating and
  escaping a caller-supplied pattern into a second wildcard language — rewriting caller text, which this data
  path refuses to do everywhere else. The pragma does not disturb the `ilike` emulation, which folds both
  operands explicitly with `UPPER` before comparing.
- **`ilike`'s guarantee is ASCII case-insensitivity, on every engine.** Non-ASCII folding is explicitly **not**
  guaranteed: PostgreSQL's `ILIKE` folds by the database's collation while SQLite's `UPPER` is ASCII-only, and
  that difference is the same host-owned collation question as ruling 1. A full Unicode-correct `ilike` — which
  would need a folding function per engine, or a normalised shadow column — is filed as a follow-up rather than
  guessed at here.

Both legs are on the **shipped** suite (`AlvoDataOrderingTests.A_like_filter_is_case_sensitive_on_every_engine`,
`An_ilike_filter_folds_ascii_case_on_every_engine`), which is where the hole was: that suite's stated job is
"the same rows, the same query, the same expected answer on every engine" and it had legs for `decimal` and
`datetime` and none for the two pattern operators, so every future driver inherited the divergence.

Neither of these is an authorization question — the CEL rule grammar has no string-match operator, so no
`USING`/`WITH CHECK` verdict changes; `RenderCaseInsensitiveLike`'s only production consumer is the caller
filter. The blast radius is "which rows an already-authorized caller sees".

## Every timestamp is one instant

**An offset is a spelling of a timestamp, never part of its value.** Every `datetime` value is normalised to
UTC by `StoredInstant` before it is stored or bound — on the create path, on the update path, on the test-only
seeding seam, and in `PredicateParameterBinder` for a caller's comparison operand. One helper, four call
sites; a second copy of this rule is how the two copies come to disagree, and a disagreement here is invisible
until it costs a row.

**What it fixes.** Unnormalised, the two engines did not merely order these differently — they disagreed on
whether the value was legal at all:

| | PostgreSQL | SQLite |
|---|---|---|
| write a row at `-02:00` | refuses — Npgsql *"only offset 0 (UTC) is supported"*, as `DbUpdateException` | stores it, then orders by the text: an ascending page walks in reverse-instant order |
| filter with a boundary at `-02:00` | throws a raw `Npgsql` `ArgumentException` **out of `QueryAsync`**, off this port's failure contract | compares `-02:00` text against stored `+00:00` text: `occurred_at > 01:30Z` returned all four rows |

Two routes reached the second row of that table, and the second one is the reason this was found late.
`PredicateParameterBinder.AsColumnType` short-circuited on `target.IsInstanceOfType(value)` — a
`DateTimeOffset` *is* an instance of `DateTimeOffset`, so the conversion (and with it the normalisation the
docs credited it with) was never called. It is now the one type for which "already the right type" does not
mean "nothing to do".

**Why normalise rather than refuse.** Refusal was the alternative and is rejected, for reasons in order of
weight:

1. There is no offset to preserve. `datetime` maps to `timestamptz`, which discards it by definition, so
   refusing a value the reference storage type accepts makes Alvo stricter than the type it chose.
2. It is a worse agent-facing contract. Alvo's stated primary user is an agent emitting JSON, `System.Text.Json`
   serialises a `DateTimeOffset` at its own offset, and RFC 3339's numeric-offset form is the second most common
   wire spelling after `Z`. Refusal means rejecting well-formed input on the most-used filter type.
3. It buys no simplicity: refusing still needs one authority inspecting `Offset` on the same call sites.
4. Normalisation makes SQLite's ordering correct **by construction**. Under refusal it would be correct only
   because no non-UTC row exists — an invariant every future write path (PR5's outbox, F7's dynamic driver)
   would have to remember.

**Deliberately not covered: `date`.** `PredicateParameterBinder.AsDate` keeps its own rule — the calendar date
the caller wrote, read at the offset they wrote it with. Normalising one to UTC would shift the day for any
caller east or west of UTC, so `StoredInstant` tests the column's CLR type and leaves `DateOnly` alone.

**Related: a value no engine can carry is refused before it reaches one.** A `NUL` inside a text filter value
is the case found alongside this one: PostgreSQL cannot encode it (`22021: invalid byte sequence for encoding
"UTF8": 0x00`, again a raw provider exception out of a read) while SQLite binds it and answers. It is refused
in the binder, through the same funnel that names the column for every other value a column cannot hold. The
write-side analogue still surfaces as EF's `DbUpdateException`, like every other storage-constraint violation —
see the failure-contract note below, which PR3's request-validation layer closes.

## Writes never reach a change tracker

`ChangeTrackerReachTests` is what keeps that true, and it scans **all three EF-referencing packages** — the
shared one and both drivers — because a tracked write in a driver bypasses policy exactly as completely. Its
banned vocabulary includes `AsTracking(`, which is the whole point: `QueryTrackingBehavior.NoTracking` on the
context is a *default*, and one `AsTracking()` call overrides it per query. Without that pattern, this —

```csharp
var row = await db.Rows(entity).AsTracking().FirstAsync(bag => (Guid)bag["id"]! == id);
row["title"] = patch;
await db.SaveChangesAsync();          // UPDATE … WHERE id = @p, no policy predicate
```

— passed every encapsulation fact in the repository, inside `EfAlvoData.cs`, the file already allow-listed for
`SaveChanges`. It was landed, watched to fail the widened gate, and reverted. `EntityState.` and `.State =` are
banned alongside it, and every pattern carries a positive and a negative sample so a typo cannot make a row
silently unenforceable.

**Every banned call form tolerates an explicit generic argument list**, because the first version did not and
that was a live bypass: `\.AsTracking\(` requires the *non-generic* spelling, and
`db.Rows(entity).AsTracking<Dictionary<string, object>>()` is one keystroke away, returns tracked rows, and
built with **zero warnings** under `TreatWarningsAsErrors` while all 284 facts passed. The same gap applied to
`Attach`, `Update`, `Remove` and `Entry`. Measured, landed and reverted.

### A hand-built command is forbidden by an allow-list, not by a ban-list

The second bypass was not in the banned vocabulary at all:

```csharp
var connection = db.Database.GetDbConnection();
using var command = connection.CreateCommand();
command.CommandText = "UPDATE \"" + entity + "\" SET \"title\" = '" + title + "' WHERE \"id\" = '" + id + "'";
await command.ExecuteNonQueryAsync();          // no predicate, and a first-order injection
```

It is not an exotic shape — it is **the house style of five sibling files in these very packages**
(`VersionRowWriter`, `SystemSchemaInitializer`, `RelationalSqlBatch`, `EfCoreDescriptorVersionStore`,
`EfCoreRuntimeSchemaWriter`), so a contributor writing it is copying the file next door.

`Only_allow_listed_files_compose_sql_or_build_a_command` closes it as an **allow-list**: the files permitted
to compose SQL text or construct a `DbCommand` are named, and any other file in a data package that reaches
`CreateCommand`, `CommandText`, `ExecuteNonQuery`/`Scalar`/`Reader`, `ExecuteSqlRaw*`, `FromSql*` or
`GetDbConnection` fails. A ban-list is a guess about what the next contributor will type; an allow-list is a
decision. Each name earns its place by writing a *framework* table, by executing SQL EF's own generator
produced, or by being the parameter-binding seam — and `Every_allow_listed_file_still_exists` makes a rename
fail rather than leave a permission covering nothing.

The allow-list's non-vacuity is asserted against sample lines rather than by landing a policy-free writer, so
the proof is permanent instead of reverted and no such file has to exist in a shipped package.

### EF's own `EF1002`/`EF1003` are a real control, and they were undocumented

Credit where it is due: `db.Database.ExecuteSqlRawAsync($"…{title}…")` fails the build with **`EF1002`** and
the concatenated form with **`EF1003`**, both as *errors* because of `TreatWarningsAsErrors`. That is a
genuine control and it retroactively validates the choice of the root namespace over `NoWarn EF1001` (a
suppression would also hide genuine EF internal-API misuse).

But it is **EF's** control, not Alvo's. It was named nowhere as a security control, so nothing stopped a
future `NoWarn EF1002` from switching it off silently, and it is blind to a hand-built `DbCommand` — which is
why the allow-list above exists rather than relying on it. Recorded here because a control nobody knows about
is one refactor away from being turned off.

`update` and `delete` are `ExecuteUpdateAsync`/`ExecuteDeleteAsync` composed over the **same `FromSql` root
that carries `USING`**, so the predicate is a subquery inside the emitted statement and `rows affected == 0`
is the `AlvoRecordNotFoundException` signal — indistinguishable, as `IAlvoData` requires, from a row that
never existed. `SaveChangesAsync` is reached from exactly one production path, the insert, plus the test-only
`AlvoDataSeed` seam. The alternative is not a style preference: a tracked `Attach` + set + `SaveChanges`
emits `UPDATE … WHERE id = @p` with **no policy predicate at all** (spike `Q5d`), and it is the shortest,
most idiomatic EF code available.

The shapes SQLite actually emits:

```sql
-- update: the policy root is the inner derived table
UPDATE "notes" AS "n0" SET "title" = @p2
FROM (SELECT "n"."id" FROM (
        SELECT "id", "owner_id", "title", "tenant_id" FROM "notes"
        WHERE (COALESCE("owner_id" = @alvo_u0, 0)) AND (COALESCE("tenant_id" = @alvo_t0, 0))
      ) AS "n" WHERE "n"."id" = @id) AS "n1"
WHERE "n0"."id" = "n1"."id"

-- delete: the same root, as an IN subquery
DELETE FROM "notes" AS "n" WHERE "n"."id" IN (
    SELECT "n0"."id" FROM (<the same policy-filtered SELECT>) AS "n0" WHERE "n0"."id" = @id)
```

**The row id is matched in LINQ, and EF names that parameter after the C# local** — `@id` here, which is
spike `Q6`'s widened namespace in the flesh. Every name this data path generates begins with `alvo_`, so it
cannot collide; a future reserved name that did not would be substituted silently.

**`WITH CHECK` is merge-then-check, never write-then-rollback.** Inside one transaction: read the pre-image
under `USING` with the driver's row lock in `PreImageMutation.Update` mode, merge the patch over it, evaluate
`WithCheck` and the tenant scope through `IPredicateEvaluator`, then write — still constrained by `USING`.
Spike `Q5e` proved write-then-read-then-rollback also works; it is rejected because it makes a rollback
control flow and moves the verdict out of the engine-agnostic core.

**The pre-image read is unmasked** (`ReadStatementOptions.Unmasked`). A verdict is reached over the complete
stored row, so a rule referencing a `hidden` field must see its real value; read through the mask it would be
the projected `NULL` and the rule would decide differently. Masking applies to what the port *returns*, which
is a separate step (`RecordMaterializer`).

**The pre-image read is a second gate in front of the write's own predicate**, and the two are not
interchangeable: with the policy root swapped for the bare `DbSet`, every outcome-level fact still passes
(the pre-image read has already refused the invisible row) and only the statement-level facts fail. Both
kinds of test are therefore kept.

**`PreImageMutation.Delete` has no consumer in PR2.** A delete carries no `WITH CHECK`, so it reads no
pre-image at all and goes straight to `ExecuteDelete` over the policy root. The enum member is the dialect
contract for a future path that does read one.

## The framework-managed columns have one authority, and the framework writes them

`AlvoManagedColumns` (in `Abstractions`) is the only place that answers "which columns does the framework
own for this entity". `DescriptorToSchemaMapper` injects exactly what it reports, `WritePayloadGuard` and
`InMemoryAlvoData` refuse exactly what it reports, and each refusal's wording comes from
`AlvoManagedColumns.RefusalReason` so two implementations of one port cannot word one refusal two ways.

It exists because the two sides had drifted, and the drift was exploitable on both engines: the mapper
injected six columns (`tenant_id`, the audit quartet, `deleted_at`) and the guard named **two**. A caller
could therefore create a row asserting a victim authored it — `created_by` is the only "who made this"
column Alvo injects, and `create` has no `USING` predicate to contradict the claim, only `WITH CHECK` — and
could then back-date `created_at` on update. Measured on SQLite and on real PostgreSQL: a row created with
`created_by = dddddddd-…` at an invented instant, then rewritten to `Guid.Empty` and back-dated to 1989,
with no rule violated. The lesson is not "add four names to the guard": an enumeration in the guard is what
went stale, so there is none.

Membership is answered from the entity's **traits** (tenancy, audit, soft delete) rather than from a flat
name list, because a name alone is not enough — an entity that does not declare `audit` may legitimately
declare an ordinary field called `created_at`, and `AlvoDataFixtures.Vehicle` does.

**The framework populates them**, through `AlvoAuditStamp.Applied`, which is one function of its inputs in
`Abstractions` for the same reason: there are two shipped implementations of `IAlvoData` and F7 adds a third.
The ruling on which columns are written when:

| Path | Stamped | Why |
|---|---|---|
| `create` | `created_at`, `created_by`, `updated_at`, `updated_by` | `updated_at` is `required`, so a row whose first write left it empty violates its own `NOT NULL`; and "last written" really is the creation instant for a row that has only been created |
| `update` | `updated_at`, `updated_by` | rewriting the creation record on every write erases the authorship the audit trail exists to hold |
| `delete` | nothing | soft delete is refused at apply time (see *`softDelete` is refused, not silently ignored*), so `deleted_at` has no writer |

The instant comes from an injected `TimeProvider` (registered `TryAddSingleton(TimeProvider.System)`, so a
host can substitute one), never `DateTimeOffset.UtcNow` inline — an inline clock cannot be asserted on, and
`SqliteAlvoDataAuditTests` pins the exact stamped instants through a fixed one. The actor is `null` for a
caller with no identity: the all-zero `UserId` is reserved to mean exactly that, so recording it would assert
that the anonymous caller wrote the row.

The stamp is applied **after** `WritePayloadGuard` and **before** `EnsureWriteAllowed`. That is the only
order that is both safe and useful: the guard has to judge the caller's own keys, and `WITH CHECK` has to see
the values that will be stored — so a create rule reading `created_by == @user.id` is satisfied by the stamp
rather than by something the caller claimed.

**A descriptor declaring a managed name no longer duplicates the field.** `AddManagedColumn` skips a name the
descriptor already declares, which only `id` was guarded against before; the unguarded case produced two
`FieldSchema` entries with one name and every later operation on that entity died with
`ArgumentException: An item with the same key has already been added` out of the data path — so declaring
`readOnly: created_by`, the documented way to protect a managed column, broke the entity instead of
protecting it. De-duplication is **by name only**: a descriptor that declares a managed name with a
*different type* still wins the mapping, and rejecting that is the reserved-name validation the descriptor
validator owns. It is not implemented and is declared here rather than left looking handled.

## The failure contract, and where each refusal is decided

| Situation | Outcome | Decided from |
|---|---|---|
| No policy for the operation | `AlvoAuthorizationException` | the decision alone |
| Entity undeclared, or `EntityStorage.Dynamic` | `AlvoAuthorizationException`, one shared message | the applied schema |
| Filter/sort names a hidden or undeclared field | `AlvoAuthorizationException`, one shared message | the decision + schema |
| Filter deeper than `AlvoFilter.MaxDepth`, negative `Limit`, a paged read sorted by a nullable field | `ArgumentException` family | the query alone |
| Payload names a framework-managed column a caller may not write (`AlvoManagedColumns`), or a read-only or undeclared field | `AlvoAuthorizationException` | the payload alone |
| `get` of an invisible or absent row | `null` | the engine |
| `update`/`delete` of an invisible or absent row | `AlvoRecordNotFoundException`, identical message | rows affected / pre-image |
| Post-image fails `WITH CHECK` or the tenant scope | `AlvoAuthorizationException` | `IPredicateEvaluator` |

`Limit = 0` is *accepted* and renders `LIMIT 0`, which both engines answer with an empty page. That is
deliberate rather than overlooked — unlike a negative limit the two engines agree on it, so it is not an
engine-agnosticism defect — but this port does not define whether an empty page or a refusal is the right
answer to "give me nothing", and the request-validation layer above it should.

Every payload refusal is decided **before any row is looked up**, so "was my write rejected" can never answer
"does this row exist". The undeclared-entity message is `AlvoDataContext.UnmappedEntityMessage`, referenced
rather than re-declared: two copies of an indistinguishability string are two authorities for one security
guarantee.

A missing **required** value is refused by the database's own `NOT NULL` and surfaces as EF's
`DbUpdateException` — deliberately not one of `IAlvoData`'s declared exceptions, because schema-derived
request validation belongs above this port. Pinned by
`SqliteAlvoDataCreateTests.A_missing_required_value_is_refused_by_the_database_constraint` as the rough edge
PR3's RFC 7807 layer closes.

## The cursor carries no data

`KeysetCursor` is base64url over the anchor row's primary key and nothing else. The anchor's sort-key values
are re-read **under the same policy predicate as the page**, so a stale, forged or cross-tenant cursor finds
no anchor and yields an empty page rather than an oracle. Cost: one extra round trip per page.

`Base64Url.TryDecodeFromChars` **throws** `FormatException` on a non-alphabet character despite the `Try`
name — it returns `false` only for a destination it cannot fill. A cursor is caller-supplied text, so the
text is validated with `Base64Url.IsValid` first; without that, a garbage cursor is an unhandled exception
out of `QueryAsync` rather than an empty page.

## Comparison operands are repaired in pairs, by the dialect

`IFieldSqlRenderer.RenderComparableOperands(left, right, CelValueType)` takes **both** operands of a
comparison and returns both. Three decisions are folded into that one signature.

**Why the member exists at all.** SQLite has no decimal storage class, so EF maps a `decimal` to a `TEXT`
column and an unguarded `price > 100` is a *string* comparison: it matches a row priced `12.34`, and
`price != 100` matches a row priced exactly 100. PostgreSQL's `numeric` answers correctly, so the same rule
admits different rows per engine — a fail-open authorization outcome on one of them, and exactly what §0's
engine-agnostic principle forbids. SQLite's driver casts both operands to `REAL`.

**Why it lives on `IFieldSqlRenderer` (in `Abstractions`) and not on `IAlvoSqlDialect`.** The CEL→SQL
comparison is rendered by the *core*, which may not reference the EF package
(`Core_depends_only_on_Abstractions`). A member on the EF-side dialect port would have been unreachable from
the `USING`/`WITH CHECK` path — the very path the fail-open lived on — so the fix would have shipped inert.
`IFieldSqlRenderer` renders *expressions*, and a comparison operand is an expression. **Declared cost:** one
added line in `PublicApi.MMLib.Alvo.Abstractions.verified.txt`, against PR2's "Abstractions does not move"
constraint. It is an addition, source- and binary-compatible for every existing implementor, and it ships as
a **default** interface member like the port's three two-valued members.

**Why it takes a `CelValueType` rather than a store type.** A store type is resolved by the provider's own
type mapping from the column; naming one in the core would add a second authority for it. The CEL type asks
a driver only the question it alone can answer — "does my storage for this type order the way the type
does?" — and it arrives *after* numeric promotion, so a whole-number literal against a decimal column is a
`Decimal` comparison.

**Why the pair, not one operand at a time.** Repairing one side only does not approximate the right answer,
it produces a new wrong one: SQLite orders every `TEXT` value above every `REAL` one, so a cast column
against an uncast parameter inverts. The first shape of this member took one operand and documented "both
sides, always" in prose; four call sites (the core's two comparison paths, the caller filter, the keyset
cursor) then had to remember it. The pair-shaped signature makes the mistake unrepresentable. The operator
stays with the caller — which comparison this is, is Alvo's semantics, not a driver's.

**Applied to:** every ordering and equality comparison, and once per candidate of a value-membership
`IN (…)` list. **Not applied to:** a `LIKE`/`ILIKE` pattern match (a string operation by definition),
`has(...)` (an `IS NOT NULL` test), or CEL role membership (decided against the caller's role set, never
compared in SQL).

**Two accepted costs on SQLite**, recorded rather than hidden: the cast is non-sargable, so a decimal
comparison cannot use an index on that column; and `REAL` is an IEEE-754 double, so a comparison is exact
only while the value fits 53 bits of mantissa — about 9·10^15 minor units (±90 trillion at two decimal
places). Both are strictly better than answering on the *lexical* value at every magnitude. The real fix is a
storage change (a scaled integer, exact and orderable), which is a schema decision this port cannot make; if
it lands, **SQLite's override must be deleted, not left to rot**.

**How it is pinned.** `TestFieldSqlRenderer` — the fake every core suite and every golden CEL→SQL baseline
renders through — deliberately overrides the member with a *visible* `CAST(… AS numeric)`. With the port's
identity default, deleting the core's repair call left every core assertion and the `cel-to-sql-core`
baseline byte-identical; the visible wrapper makes the call site part of the frozen text. The engine-level
proof is `AlvoDataComparisonTests`, a shipped abstract base both engines inherit rather than a per-engine
copy — the defect *is* a disagreement between engines, so the proof of the fix is inherently differential.

## `IS TRUE` / `IS FALSE` go through the two-valued seam

The caller filter's `is` operator is the one definite, two-valued comparison. `IS NULL` is universal SQL and
is spelled literally. `IS TRUE`/`IS FALSE` are **not** — T-SQL, which §0 principle 3 names through Azure SQL,
has no boolean type and cannot parse either — so they are composed from the dialect's own boolean literal and
its two-valued fold: `RenderTwoValued($"{field} = {TrueLiteral}")`. The semantics are identical by
construction (`COALESCE(x = TRUE, FALSE)` is true precisely when `x IS TRUE` is, a `NULL` `x` included), so
no new port member was added — this is the seam PR1 already built for the two-valued fold, reused. Proved the
way PR1 proved that seam: through `TSqlFieldSqlRenderer`, a fake dialect neither in-repo driver speaks, which
renders `(CASE WHEN [is_public] = 1 THEN 1 ELSE 0 END = 1)` with no change to the filter renderer.

## Values are bound through EF's own type mapping, never formatted

`PredicateParameterBinder` binds every value through `IRelationalTypeMappingSource`. Formatting a value into
text is not a cosmetic shortcut: EF's SQLite `Guid` mapping writes upper-case `TEXT`, so a hand-formatted
lower-case Guid matches no row and raises nothing.

**A value compared against a column is bound through *that column's* mapping, and the shape of the data is
what enforces it.** Each value a statement carries travels as a `BoundValue` tagged with one of three
origins, and the binder switches on it exhaustively:

| Origin | Produced by | Bound through |
|---|---|---|
| `ColumnComparison` | the caller filter, the keyset boundary, the row id | the named column's own mapping, after converting the value to the column's CLR type |
| `PolicyPredicate` | `IPredicateRenderer.Render`'s bag | the value's own CLR type — see below |
| `Framework` | the page's row limit | the value's own CLR type (an `int` this data path chose) |

There is deliberately **no** way to bind a bare `name → value` bag on the statement path. The previous shape
had one, every production call site used it, and the column-aware method — whose own documentation said it
was mandatory — ended up with **zero callers** while its ~19 KB of tests kept passing. So the guarantee is now
carried by `BoundValue`, which has no constructor taking a value alone: a fragment author has to name which
of the three cases theirs is. Removing the column lookup fails four named facts in
`SqliteAlvoDataFilterBindingTests`, all of which go through `IAlvoData.QueryAsync`.

**Why the policy predicate is safe without a column.** A rendered `SqlPredicate` records names and values
only, so there is no field to consult. Every value in it is a context value or a CEL literal, and the type
checker has already forced both operands of the comparison to one type. The literal kinds the grammar admits
are exactly `Int`, `Decimal`, `String`, `Bool` and `Null`; the context values are a `Guid` or the role set.
**The language has no date or timestamp literal at all**, so the one mismatch the collapse of `date` and
`timestamp` into a single CEL type could otherwise produce is unreachable — a rule comparing a `date` field
against anything the grammar can express fails to compile. The only reachable numeric mismatch is an `Int`
literal against a `Decimal` column, which promotes to a `Decimal` comparison and is repaired on both operands.
`CelRuleBindingTests` pins all of that, so a grammar that grows a temporal literal fails a test rather than
silently invalidating the argument.

Two conversions are refused rather than performed, because both would be silent and wrong:

- **A fractional value against an integral column.** `Convert.ChangeType` rounds (midpoint-to-even), so
  `mileage=gt.12.7` would bind `13` and answer `mileage > 13`, dropping the row with `mileage = 13` from a
  request whose stated predicate included it. There *is* a correct answer, but it is per-operator (floor for
  `gt`, ceiling for `lt`, no match for `eq`) and it is request-validation work: **PR3 owns deciding whether a
  fractional bound against an integral column is a 422 or is floored/ceiled per operator.**
- **A host-relative timestamp.** An offset-less input is read as **UTC**
  (`AssumeUniversal | AdjustToUniversal`), never in the process's local zone, and a `DateTime` with
  `Kind == Unspecified` likewise. Otherwise two replicas of one service in two regions bind two different
  instants for one request — and CI, which runs UTC, never sees it. A `date` column keeps its own documented
  rule: the calendar date the caller wrote, read at the offset they wrote it with.

## How `IAlvoData` reaches a host, and why it is a singleton

`AlvoEfCoreProvider.AddRelationalProvider` registers the driver's own `IFieldSqlRenderer` and `IAlvoSqlDialect`
from `RelationalProviderRegistration`, then composes `IAlvoData` from them plus the engine-agnostic core's
`IPolicyEngine`, `IPredicateEvaluator` and `IPredicateRenderer`. So `AddAlvo(alvo => alvo.UseSqlite(...))` alone
yields a resolvable data port — the wiring, not just the type, is what a host gets.

Every registration is `TryAdd`, which has a deliberate direction: a host that registers its own dialect
*before* attaching the provider keeps it. That is the seam an out-of-repo engine (or a test wanting to observe
a dialect decision) substitutes through, and it is how `SqliteAlvoDataFixture` keeps its lock-recording dialect
in the graph while still resolving the production-composed port.

**Singleton**, deliberately: the port holds no per-request state, it creates one `DbContext` per operation and
disposes it, and every member takes the caller's `AlvoContext` as a parameter precisely so that no ambient
scope decides who is asking. A scoped registration would imply the opposite and invite an accessor to be read
instead of an argument to be passed.

## What is proved on a real engine, and where

The milestone's acceptance criteria are per-engine facts, so they are inherited suites in
`MMLib.Alvo.Testing.Data` with one thin subclass per engine — never a per-engine copy, because a copied suite is
how two engines come to test different things.

| Suite | What it proves | SQLite | PostgreSQL |
|---|---|---|---|
| `AlvoDataAdversarialTests` | two-user / two-tenant / default-deny, masking, write scoping | real temp-file database | real container |
| `AlvoDataStatementTests` | the resolved predicate is bound **inside the `WHERE`** of one statement — never an in-memory post-filter | ✔ | ✔ |
| `AlvoDataDifferentialTests` | the rendered `USING` predicate and the in-memory evaluator agree on the shared matrix, judged by the engine's own `WHERE` | ✔ | ✔ |
| `AlvoDataComparisonTests` | a **rule** compares a decimal by value | ✔ | ✔ |
| `AlvoDataOrderingTests` | the **filter** and the **page** (order + keyset boundary) compare by value, and a timestamp is one instant | ✔ | ✔ |
| `AlvoDataSqlSnapshotTests` | golden CEL→SQL, per engine | `cel-to-sql-sqlite` | `cel-to-sql-postgresql` |

Four of these are new in PR2 and each closes a hole a single-engine suite left.

**`AlvoDataStatementTests` exists because no outcome can carry §2.4's "never a post-filter".** An
implementation that fetched the candidate rows and filtered them with `IPredicateEvaluator` returns the same
rows, throws the same exceptions and pages the same way — it passes the adversarial suite, the differential
matrix and the ordering suite in full. The only observable difference is the SQL it sends, so the assertion is
on the statement, and it has to run per engine: a criterion proved on one engine is a property of that engine's
test project, not of the port. The predicate's *text* is engine-specific, so the facts assert on what is not —
the reserved parameter prefixes (`alvo_u`, `alvo_t`) a resolved predicate binds its values under, and their
position after the statement's first `WHERE`. A post-filtering implementation binds none of them, because it
never renders the predicate. Dropping `decision.Using` from the composer fails two of the four facts; the
fourth is the non-vacuity control, which asserts a bare `"true"` rule binds **no** policy parameter at all.

`SqlCapture` was the enabler and is now linked into both engine projects from `test/_shared/ef/`: nothing in it
is SQLite-specific — it observes EF's process-wide `DiagnosticListener` and filters by a marker in the
connection string, which is the database file name on SQLite and the generated database name on PostgreSQL.

The differential matrix runs as **one fact over one probe** rather than a theory row per case, so the loop can
assert a non-vacuity counter afterwards — "the two backends never disagreed" is worthless if the probe answered
`false` to everything. The golden PostgreSQL baseline deliberately lives in the *non-Docker*
`MMLib.Alvo.Data.PostgreSql.Tests` project: rendering needs no engine, and a Docker-gated snapshot goes
unverified on every host that skips the container, which is how a per-engine baseline drifts unnoticed.

The `FOR NO KEY UPDATE` clause SQLite cannot test is covered behaviourally rather than by inspection: it is
emitted at the very end of the pre-image `SELECT` (after `ORDER BY`/`LIMIT`, which is what PostgreSQL's grammar
requires), so a misplaced clause is a syntax error and every PostgreSQL `update` fact in the adversarial suite
fails. `PreImageMutation.Delete` still has no consumer — a delete carries no `WITH CHECK`, so it reads no
pre-image.

## Mutation-testing notes

Mutation runs post-merge on `main` (`.github/workflows/mutation.yml`), across five parallel configs. Nothing
blocks a merge on the score, so a red run is a notification someone has to act on — which makes it worth
knowing, before the merge, that each config is configured to answer at all.

### Each config was verified non-vacuous, and here is how

A config that discovers **zero** mutants, or whose test projects yield **zero** tests, reports a false green;
this repository has shipped exactly that once already (`project_mutation_gate`), which is why the workflow's
own step greps for `0 total mutants will be tested` and `Number of tests found: 0` and fails loudly. Config
correctness therefore has to be measured, not asserted.

It was measured with a **discovery-only probe** rather than a local mutation run (which `CLAUDE.md` forbids):
each config was started with `dotnet-stryker -f <config> --concurrency 4`, watched until Stryker had printed
the two numbers the workflow greps for, and then killed before the mutation loop began. That exercises the
whole configuration — glob resolution, project resolution, the MTP runner, the initial test run — without
paying for the run.

| Config | Mutated project | Tests found | Mutants to be tested |
|---|---|---|---|
| `stryker-config.expressions.json` | `MMLib.Alvo` (`Expressions/**`) | 1267 | 834 |
| `stryker-config.json` | `MMLib.Alvo` (the rest) | 1267 | 478 |
| `stryker-config.data-ef.json` | `MMLib.Alvo.Data.EntityFrameworkCore` | 686 | 497 |
| `stryker-config.data-sqlite.json` | `MMLib.Alvo.Data.Sqlite` | 268 | 13 |
| `stryker-config.data-postgresql.json` | `MMLib.Alvo.Data.PostgreSql` | 132 | 11 |

`data-ef`'s row is the second measurement, taken after `02f815d` fixed the negated-declaration-pattern blind
spot below — the first probe (same commands, before that fix) found 677 tests and 488 mutants. The other
four rows are unchanged since neither their mutated project nor their test projects were touched afterward.

All five are non-vacuous. The two driver configs are small on purpose — each driver is two files of rendering
— and small is the point: `TrueLiteral => "1"` mutated to `"0"` inverts a boolean inside a policy `WHERE`, and
until PR2 the only other thing pinning those literals was an accepted Verify baseline, the one artefact a test
can be made green with.

**The `data-ef` `test-projects` list is now a result, not a hypothesis.** It names both
`MMLib.Alvo.Data.EntityFrameworkCore.Tests` and `MMLib.Alvo.Data.Sqlite.Tests`, because the killing tests for
`EfAlvoData`, `SortSqlRenderer`'s engine behaviour, `UpdateSetterFactory` and `WritePropertyBag` live in the
latter; the probe confirms 686 tests reach the run, which is the two projects together rather than the EF
project's own suite alone. `MMLib.Alvo.Data.PostgreSql.Tests.Integration` is deliberately **not** added: it is
Docker-gated end to end, so on a CI shard with no daemon every one of its kills would report as a survivor and
the score would read as a regression that is really an absent container. `MMLib.Alvo.Data.PostgreSql.Tests` is
not added either, for a different reason — it holds the per-engine golden CEL→SQL snapshot, which renders
through the *core*'s predicate renderer and never touches the EF package's composers, so it would add wall
clock and no kills.

### The exclusions, and the proof one of them bites

`Internal/AlvoDataSeed.cs` is excluded. It is the test-only out-of-band seeding seam the adversarial suites
use to place rows behind the port, so mutating it measures the harness rather than the product (plan
*Deviations* 11). **Measured, so the exclusion cannot be a silently mis-matched glob:** the same discovery
probe run with the exclusion removed reports **493** mutants against **488** with it — the five mutants in
that file, and nothing else, are what the line removes.

The rest of the exclusion list is unchanged from before PR2 and is all DB-round-trip or DI wiring
(`RelationalConnectionFactory`, `RelationalSqlBatch`, `SystemSchemaInitializer`, `VersionRowWriter`, the four
`EfCore*` services, `AlvoEfCoreProvider`, `RelationalProviderRegistration`): their defects only manifest
against a real database, so mutating them produces noise the SQLite and PostgreSQL suites already answer.

### Known survivors, declared rather than left looking like missing tests

Two arms are deliberately unreachable and will survive. Both are kept, because both fail *silently* if the
invariant that makes them unreachable ever stops holding — which is exactly when a throw earns its place.

- **`ReadStatementComposer.Collect`'s duplicate-name throw.** Unreachable while every fragment renders with a
  name from `PolicyParameterPrefix` and no reserved name prefixes another (asserted by
  `PolicyParameterPrefixTests`). Kept because last-writer-wins on a real collision returns wrong rows with no
  error at all.
- **`SqlPredicateRenderer.ValueTypeOf`'s `CelUnary` / `CelBinary` / `CelConditional` arms and its `_` fallback**
  (in the `expressions` shard, not `data-ef`). Both operand renderers accept only a literal, a field reference
  and — on the predicate path — a context value, so `total + 1 > 100` throws `NotSupportedException` at
  `RenderScalarOperand` and `(total > 0 ? total : 0) > 100` is refused a layer earlier by the type checker.
  The unreachability itself is pinned by two named facts rather than by tests that cannot reach the code.

Neither is suppressed with a Stryker directive: this package writes no inline comments, and a
`// Stryker disable` line is a comment whose reason lives outside the code. Declaring them here keeps the
reason where a reader of a red run will look for it.

### The negated-declaration-pattern trap, and the fix

Two methods on this data path — `ReadStatementComposer.AddRowId` and `AlvoModelCacheKeyFactory.Create` —
used to be *entered* through a negated declaration pattern: a guard that binds a pattern variable only on
the branch where the pattern **fails to match**, then uses that variable after the guard.
`if (rowId is not { } id) { return; }` … `id` used below; `if (context is not AlvoDataContext alvo)
{ throw …; }` … `alvo` used below. The shape is idiomatic and completely safe at run time — the compiler's
definite-assignment analysis proves `id`/`alvo` is bound on every path that reaches the code after the
guard, because the only way to get there is for the negated pattern to have failed to match.

That same proof is exactly what a mutation tool's job destroys. Stryker mutates the guard's condition (a
boundary, a negation, an equality) to see whether the test suite notices; the mutated condition no longer
matches the shape the compiler proved definite assignment from, so the variable becomes "used but possibly
unassigned" on the mutated path — `CS0165`, a compile error, not a behavioural difference.

**A `CompileError` mutant is not a survivor, and the difference matters.** A survivor still lowers the
mutation score and is visible in the report as one specific mutation someone can go read and decide whether
to accept. A `CompileError` is invisible in a different way: Stryker's Safe Mode responds to *one* compile
error inside a method by discarding **every** mutation of that whole method and marking them all
`CompileError`, so the method's contribution to the score is not low, not flagged — it is simply **absent**.
A green `data-ef` run then looks exactly like one where the method was fully exercised. `ReadStatementComposer.AddRowId`
composes the `alvo_id` term that carries a row identity into a policy-filtered `WHERE` — the security path —
so this was the worst place in the package for a mutation gate to be silently unable to speak at all.

The fix (`02f815d`) is not suppressing the mutant — that is what `data-ef`'s two *declared* survivors above
do, deliberately, because they stay reachable rather than uncompilable. It is rewriting each guard so no
pattern variable's assignment depends on a branch the compiler can only prove from the *exact* syntactic
form of the condition:

- **`ReadStatementComposer.AddRowId`** dropped the pattern-variable capture entirely: `if (rowId is not { }
  id) { return; }` became `if (rowId is null) { return; }`, with the body reading `rowId.Value` in place of
  `id`. A nullable value type's `.Value` is legal to call regardless of the guard, so no mutation of
  `is null` can make the method fail to compile — the worst a mutant can do is change which rows the
  statement answers, which is exactly the kind of change a mutation score needs to be able to see.
- **`AlvoModelCacheKeyFactory.Create`** flipped the pattern to its accepting form: `if (context is not
  AlvoDataContext alvo) { throw …; }` became `if (context is AlvoDataContext alvo) { return (…,
  alvo.ModelToken, …); } … throw …;`. `alvo` is now bound and read inside the same block the pattern matched
  in, so the compiler needs no cross-branch reasoning to prove it assigned, and mutating the condition can at
  worst make the method answer wrong rather than fail to build.

Behaviour is unchanged in both; only the guard's shape is — and both methods' own remarks point back here.
**The general rule this leaves for later code on this path:** a declaration pattern whose variable is used
*after* the `if` rather than *inside* the branch that bound it is a mutation-coverage blind spot waiting to
happen, not merely a style preference. Prefer the accepting-branch form, or a plain null/type test with no
pattern variable at all, whenever the code after the guard would otherwise depend on the negative branch
having been taken.

**Measured, not assumed.** The same discovery-only probe from *How each config was verified* was re-run
after the fix: `data-ef`'s tested-mutant count went from **488 to 497** (+9), and its `CompileError` count
went from **146 to 134** (−12) — the two guards' mutants stopped disappearing, and most of the freed
mutants landed in *tested* rather than in the (unrelated) already-covered/mutate-filter exclusions that
account for the remaining three. Each core shard still reports about 157 `CompileError` mutants; that
figure is unaffected by this fix (both rewritten methods live in `MMLib.Alvo.Data.EntityFrameworkCore`) and
remains normal for pattern-matching-heavy C#, not a coverage signal on its own.

`ReadStatementComposer.RequiresTotalOrder`'s three disjuncts each have an independent killing fact, plus the
negative, so there is **no** survivor to chase there: `A_sorted_read_carries_its_order_by_inside_the_one_statement`
(sort only), `A_limited_read_orders_before_it_truncates_and_binds_the_limit` (limit only),
`A_cursored_page_is_ordered_even_with_no_caller_sort_key` (anchor only) and
`An_unsorted_unlimited_first_page_needs_no_ordering_at_all`.

## Where the code lives, and one naming rule that looks like an oversight

Every type this data path adds is `internal` and lives under `Internal/` in
`src/MMLib.Alvo.Data.EntityFrameworkCore`. **The namespace of a file in that folder is the package root
(`MMLib.Alvo.Data.EntityFrameworkCore`), not `….Internal`** — which is why the folder's `namespace`
declarations are mixed, 27 at the root against 3 in `.Internal`.

The reason is EF's own `EF1001` analyzer: it matches any namespace containing `EntityFrameworkCore` followed
by `.Internal` and treats a cross-assembly reference to a type there as an error, which under
`TreatWarningsAsErrors` fails the build. Four sibling projects already set `NoWarn EF1001` to live with that,
so suppression was available and cheap; the root namespace was chosen anyway, because a suppression would also
hide genuine EF internal-API misuse in a package whose whole job is to use EF correctly. Nothing is lost:
`SharedArchitectureRules.Types_in_an_Internal_namespace_are_not_public` keys on the namespace and only forbids
*public* types there, and every type here is `internal`. Moving the three stragglers to the root would end the
confusion and is a tidy-up nobody has spent a PR on.

## What later work inherits

Everything PR2 deliberately did not answer, in one place, so the phase that owns it does not have to
reconstruct the reasoning from a scattered set of remarks.

### PR3 — the HTTP Data API and request validation (#19)

| Item | Why it is not PR2's | Where the seam is |
|---|---|---|
| An **`IS NULL`-aware keyset boundary**, so a paged read can sort by a nullable column instead of being refused | The boundary's predicate form depends on the anchor's own null-ness, so `KeysetAnchor` has to carry it, and it must stay in lockstep with `SortSqlRenderer`'s rank expression or it reintroduces the order/boundary divergence that skips rows | `KeysetSqlRenderer` + `SortSqlRenderer`; the refusal it would replace is `EfAlvoData.EnsureSortKeysCanBePaged` |
| The **coercion policy for a fractional bound against an integral column** — is `mileage=gt.12.7` a 422, or floored/ceiled per operator? | There *is* a correct answer but it is per-operator (floor for `gt`, ceiling for `lt`, no match for `eq`), which makes it request validation, not binding. `Convert.ChangeType` rounds midpoint-to-even, so binding `13` would drop the row with `mileage = 13` from a request whose stated predicate included it | `PredicateParameterBinder` refuses the conversion today rather than performing it |
| ***"p95 latencia filtrovaného listu nad 100k riadkov (indexovaný stĺpec) < 50 ms lokálne"*** and *"keyset pagination stabilná nad 1M riadkov"* (§2.1) | An explicit non-goal of #20; and `AlvoSort.Nulls`' portable `CASE WHEN` emulation is known to defeat an index on the sort key, so the target cannot be met without revisiting it | The whole statement text is composed in **one** place (`ReadStatementComposer`), so moving `ORDER BY`/paging fully into the raw root — or adopting native `NULLS FIRST`/`NULLS LAST` per dialect — is a change to one file |
| A **missing required value** surfaces as EF's `DbUpdateException`, not as RFC 7807 | Schema-derived request validation belongs above this port; the all-optional read model deliberately does not enforce required-ness | Pinned by `SqliteAlvoDataCreateTests.A_missing_required_value_is_refused_by_the_database_constraint` |
| On a **create**, an explicit `null` is indistinguishable from an omitted key | `WritePropertyBag` drops nulls, and for an insert "absent" and "null" mean the same thing to the database. On an **update** they do not, and that path uses `ExecuteUpdate` setters where a `null` is a real `SET col = NULL` | `WritePropertyBag.For`; the asymmetry is stated in its own remarks |
| `Limit = 0` is accepted and renders `LIMIT 0` | Both engines agree on it, so it is not an engine-agnosticism defect — but whether "give me nothing" is an empty page or a refusal is a request-layer decision | `ReadStatementComposer` |
| A **`NUL` in a text value on the *write* path** still surfaces as `DbUpdateException` | The read path refuses it in the binder (PostgreSQL cannot encode it, SQLite can); the write analogue is one more storage-constraint violation, on the same boundary as the row above | `PredicateParameterBinder` holds the read-side guard |
| The query-string surface, the offset mode, and a server-enforced maximum page size | `AlvoQuery.After` is opaque by contract and PR2 owns only its encoding | `KeysetCursor` |

### PR5 — outbox, events and hooks (#22)

- **The transaction is already the right seam.** `EfAlvoData`'s update path opens
  `db.Database.BeginTransactionAsync()`; `transaction.GetDbTransaction()` yields the real provider
  `DbTransaction`, so an outbox insert can ride the same transaction as the data change without a second
  connection or a distributed transaction.
- **`ExecuteUpdate`/`ExecuteDelete` do not go through the change tracker, so they fire no `SaveChanges`
  interceptor.** This is the trap: the idiomatic EF place to hang an outbox is a `SaveChangesInterceptor`, and
  on this data path it would silently never fire for an update or a delete — the two operations that most need
  an event. PR5's hooks and outbox must be sequenced **explicitly on the transaction**, never hung off
  `SaveChanges`.

### F7 — dynamic (metadata-driven) entities

- **The dynamic store is a different `IAlvoSqlDialect` + `IFieldSqlRenderer` pair, not a different data path.**
  `EfAlvoData`, the composer, the guards, the binder and the masking all stay; what changes is how a table and
  a column render (a JSON path into the shared partitioned `entity_records` store rather than a quoted column).
  That is the seam §2.1's *"the same adversarial and policy suite passes identically over a physical and a
  virtual entity"* criterion runs through, and it is why `IAlvoSqlDialect` was introduced as a port rather than
  as a member on `IFieldSqlRenderer`. **`AlvoDataAdversarialTests.Same_suite_passes_over_a_dynamic_entity`** is
  the reserved leg — skipped, with its reason — following the idiom
  `SchemaMigratorContractTests` already set for F2's migrator contract, so the obligation shows up as a named
  skip in every driver's own test run rather than as a paragraph nobody re-reads.
- **A named F7 test, not a warning:
  `AlvoDataAdversarialTests.A_uuid_rule_over_a_dynamic_entity_matches_rows_on_every_engine`.** Spike `X2`
  rehearsed the dynamic driver and found one trap. EF's SQLite `Guid` mapping stores an **upper-case** `TEXT` value, while
  `json_extract` returns whatever case the JSON payload happens to hold — so a `uuid`-typed JSON path compares
  upper against lower and **silently returns zero rows**, with no error on either side. Every row-ownership
  rule in Alvo is a `uuid` comparison (`owner_id == @user.id`, `tenant_id == @tenant.id`), so on the dynamic
  driver that failure mode is fail-*closed* on a read and would look like an over-strict policy rather than a
  bug. The dynamic driver must normalise the case of a `uuid`-typed JSON path per engine, and the reserved fact above is what
  says so, so this is a discovery already made rather than one waiting to happen. It is the same class of
  defect as spike `Q6d`, which is why *Values are bound through EF's own type mapping* exists.
- **The migrator maps `Dynamic` entities; the read model does not.** `DescriptorModelBuilder.Build` maps every
  entity, while `AlvoDataContext.OnModelCreating` filters to `EntityStorage.Physical`. So a descriptor
  declaring a dynamic entity today gets a physical table created for it that the read path then refuses with
  `AlvoAuthorizationException`. Harmless and fail-closed, but it is an asymmetry F7 should resolve
  deliberately rather than rediscover.
- **The fail-closed belt around the row key exists for F7's benefit.** `ReadProjection` and `QueryFieldGuard`
  refuse a hidden set containing the key however that set arrived, precisely because a `SchemaModel` from a
  dynamic registry has not been through the descriptor's validation.
