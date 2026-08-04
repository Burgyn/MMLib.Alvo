# The data path

How an Alvo read or write becomes one SQL statement, and the decisions that shape it. Written during F3 PR2
(#20).

> **Status: complete for PR2**, with the PR3 port widenings folded in where they closed a deferral and the
> PR5a outbox resolved where it closed a prediction. Everything below describes what the code does today.
> Where a decision was deliberately deferred it says so and names the phase that owns it — see *What later
> work inherits* at the end, which is the one place a PR3, PR5b or F7 author should start.
>
> **Sibling records:** this file owns the **port and the SQL**. `docs/architecture/data-api.md` owns the
> **HTTP layer** PR3 added — the URL grammar and its allow-lists, the status/`type`-slug catalogue, the
> `ETag` spelling, and Position A (what the framework publishes and what it treats as confidential).
> `docs/architecture/events.md` owns the **event backbone** PR5a added — the CloudEvents envelope,
> `alvo_outbox`, the claim protocol, the ordering guarantee and the after-hook pipeline. All three are
> deliberately split along the port boundary, so a decision about a wire format lives in the first, a
> decision about a delivered event in the second, and a decision about a statement here.

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
| `alvo_offset` | `AlvoQuery.Offset`'s leading-row skip count — reserved for the same reason `alvo_limit` is, and named separately because T-SQL's `OFFSET … FETCH` needs the two as distinct markers | `ReadStatementComposer` |

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

### `UseRelationalNulls()` is on, and its cost is a constraint on future LINQ

Both drivers register their provider with `UseRelationalNulls()` — `AlvoSqliteBuilderExtensions` and
`AlvoPostgreSqlBuilderExtensions`, one line each, now with a remark at the call site. What it does is switch
EF from **C#'s** null semantics to **SQL's**: by default EF expands a comparison it translates so that
`a == b` keeps C#'s meaning even when a side is `NULL` (`a = b OR (a IS NULL AND b IS NULL)`), and with the
option on it emits the bare `a = b`, whose answer is `UNKNOWN`.

It is on because this data path's authorization predicate is composed as **raw SQL** and the composed
statement is the only place a verdict is reached. A rendered `USING` predicate already carries SQL's
three-valued logic, folded to `FALSE` once in the core; if EF then expanded a *residual* LINQ comparison over
the same root with C#'s semantics, one statement would contain two different null contracts, and the
differential test that proves the SQL and in-memory verdicts agree would be proving it about only half the
statement.

**The cost, which is a constraint on future code rather than a behaviour today.** EF's own documentation says
it plainly: with this option "your LINQ queries no longer have the same meaning as they do in C#". Today the
data path composes almost no LINQ over the root — the row-id match on the write path is the exception, and
`id` is non-nullable — so nothing in this package currently depends on the difference. A predicate over a
nullable column written in LINQ would have to be spelled the way SQL reads it, `x != null && x != y` rather
than `x != y`. Turning the option off is not the escape hatch it looks like, because that would reintroduce
the two-contracts problem above; the escape hatch is to write those queries against SQL semantics and say so
where they are written.

**This paragraph predicted that PR5 would be the first PR the cost binds, and it was not — the approach
changed rather than the constraint.** PR5's outbox claim, mark and release are **raw SQL**
(`Internal/OutboxTable.cs`), where the three-valued predicates `claimed_at IS NULL` and
`dispatched_at IS NULL` carry SQL's own semantics natively, so the constraint is met **by construction**
instead of by whoever edits the file next remembering it. `ChangeTrackerReachTests`'
`The_outbox_claim_is_raw_sql_and_never_linq_over_the_context` is what holds that line, over both
`OutboxTable.cs` and `EfCoreOutboxStore.cs`. The change of approach is recorded rather than the paragraph
deleted: the cost above is still exactly what a *future* LINQ addition to this package pays, and the outbox
is the precedent for paying it by construction instead. See [`events.md`](./events.md), *The claim*.

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
because SQLite and PostgreSQL disagree on where `NULL` sorts for a given direction. The `IS NULL` test reads
the raw column, not the repaired one — a cast `NULL` is still `NULL`.

**It is emitted only where the key is nullable**, which is the one index-defeating construct in this data path
and it used to be on **every** read. On a paged read that was provably pointless:
`EnsureSortKeysCanBePaged` refuses a nullable paged sort key three frames earlier, so the rank expression was
a compile-time constant `0` that could not change a single row of the answer — while being the one thing
standing between this port and §2.1's *p95 < 50 ms on an indexed column*. A PR3 author measuring that
criterion would have started reworking where `ORDER BY` and paging live. Dropped where it cannot matter, kept
where it is load-bearing (an unpaged sorted read over a nullable key, where `AlvoSort.Nulls` is a real
promise); `SortSqlRendererTests.The_null_placement_rank_is_emitted_only_for_a_nullable_key` pins both arms.

**A paged read over a nullable sort key is refused, not answered.** `KeysetSqlRenderer` models no null
placement of its own: its boundary is a chain of comparisons with no `IS NULL` arm, so a `NULL` on either side
makes the term `NULL` and a `WHERE` treats that as false. Under `nullslast` the null-keyed tail became
unreachable; under `nullsfirst` the first page's anchor had a null key and page two came back empty. Paging
just stopped, silently.

The design's ruling is that a nullable sort column must declare its null placement **or be rejected**, and
`AlvoSort.Nulls` alone cannot deliver the first half while only the `ORDER BY` honours it — so
`AlvoQuery.EnsureSortKeysCanBePaged` takes the second: a read with a `Limit` or an `After` whose sort key
names a `Nullable` field is refused with an `ArgumentException`. **It lives in `Abstractions`, called by both
implementations**, on this codebase's own `AlvoFilter.EnsureWithinLimits` precedent — it was written twice,
verbatim including its three-line message, in two shipped assemblies, and F7's dynamic driver would have made
a third copy. That is the port's malformed-query channel,
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

> **⚠ SIGN-OFF REQUIRED — the two knowing exceptions to §0 principle 3 ("identical behaviour on
> SQLite/PostgreSQL/Azure SQL"). Ratify or reject in one reading:**
>
> | | What differs | On which engines | Cost to remove it | Why it was accepted |
> |---|---|---|---|---|
> | **1** | `ORDER BY` over a **string**, so `AlvoSort("title")` yields a different first page | SQLite compares `TEXT` as `BINARY`; PostgreSQL uses the **database's** collation, where `'a' < 'B'`. Both self-consistent; the two disagree with each other | Force a collation in every string `ORDER BY` (`COLLATE "C"` / `COLLATE BINARY`). That **overrides a collation the operator chose** and makes every string sort non-sargable on PostgreSQL — it defeats the index #19's "p95 < 50 ms on an indexed column" needs | Collation is a property of the database a host configures, not of Alvo's rendering. It is also already why the Rule profile refuses relational operators on a string, so allowing a string to be *sorted* while refusing to *order* by it would be incoherent |
> | **2** | `ilike`'s folding beyond ASCII, so `plate=ilike.čé%` may match on one engine and not the other | PostgreSQL's `ILIKE` folds by the database collation; SQLite's `UPPER` is ASCII-only (`upper('čé')` → `čé`) | A folding function per engine, or a normalised shadow column per foldable field — a schema change, and a per-engine native dependency. Filed as a follow-up rather than guessed at | The **ASCII** guarantee is now identical on both engines and asserted on the shipped suite; only the non-ASCII tail is host-owned, which is the same question as ruling 1 |
>
> **Blast radius, both:** which rows an already-authorized caller sees, or in what order. **No authorization
> verdict changes** — the CEL rule grammar has no string-match operator, so no `USING`/`WITH CHECK` result
> depends on either. **Not covered by this ask:** `like`'s case-sensitivity, which was a real defect and is
> **fixed** (leg 2 below), and string *equality*, which is byte-exact on both engines.
>
> Rejecting either means filing the removal above as work, not reverting this PR: both rulings are the current
> behaviour of the shipped drivers, and the tests that pin them are the reason the divergence is visible at all.

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

**PR5's outbox honours it, and the forward reference is closed.** `alvo_outbox`'s `created_at`, `claimed_at`
and `dispatched_at` all go through `StoredInstant.Text`, and `created_at` is rendered from the envelope's own
`time` — which is the write's audit instant and the instant embedded in the event id, so one write reads the
clock once and three places agree. `StoredInstant` is `internal` to this driver and unreachable from
`Abstractions`, so the envelope enforces the same rule at its own boundary instead of trusting it:
`AlvoEvent.Time` refuses a value whose `Offset` is not `TimeSpan.Zero`, with a message naming
`ToUniversalTime()`. See [`events.md`](./events.md), *The outbox*.

**One instant also means one *precision*, and the store's is the authoritative one.** "Three places agree"
was true of the three timestamps above and false of the fourth — the entity row's own `created_at`. That
column is not framework bookkeeping and does not go through `StoredInstant.Text`: it is a declared `datetime`
field, so `FieldClrType` maps it to `DateTimeOffset` and `DescriptorModelBuilder` lets the provider pick the
store type, which on PostgreSQL is `timestamp with time zone` — **microseconds**. A .NET clock keeps
100-nanosecond ticks, so an audit stamp read straight off it is a value the row cannot hold: measured on a
real engine, `…4567` stamped and `…4560` read back, 7 ticks apart, and the event's `time` (full precision, out
of the JSON payload) then unequal to the `created_at` it is supposed to *be*.

So the framework mints its own instants at storage precision — `StoredInstant.Storable`, floored to the whole
microsecond, called once per write site in `EfAlvoData.WriteInstantNow`. Floored rather than rounded, because
rounding up stamps a row with an instant that had not happened yet. One microsecond rather than each engine's
own answer, because SQLite keeps all seven digits of the rendered text: leaving the floor to the engine means
one write records a different instant per engine, and `AlvoDataOutboxTests.The_events_time_equals_the_rows_own_audit_instant`
holds on one of them only — §0 principle 3, on the framework's own bookkeeping. `AlvoPrecondition` states the
same hazard for the version channel and closes it differently, by only ever comparing values that came *out
of* the database; an envelope's `time` is not a column read back, so the instant itself is made storable
instead. A **caller-supplied** `datetime` is deliberately untouched: it is stored at whatever precision the
engine keeps, and flooring it would silently move a filter boundary the caller wrote.

This is also a warning about where a green local run comes from. The defect was invisible on macOS and failed
~9 writes in 10 on the Linux CI leg, for one reason: macOS reads the wall clock at microsecond granularity, so
every stamp there is already whole-microsecond and round-trips by luck. `A_write_whose_clock_lands_mid_microsecond_still_records_one_instant`
therefore injects a clock fixed 7 ticks past a whole microsecond, and asserts both halves — the stamp's exact
value *and* its equality with the envelope — because equality alone is free on SQLite and the stamp alone is
satisfied by a driver that floors the column and leaves the envelope at full precision.

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

**Every write runs in a transaction, and a delete reads a pre-image it does not need for a verdict.** A
delete carries no `WITH CHECK` — there is no post-image to check — so its pre-image read is there for the
*shape*, not for a decision:

- PR5's outbox row and a `record.deleted` event both need the row image, and an in-transaction before-hook
  needs something to run over. Without the transaction, the outbox row could not ride the same `DbTransaction`
  at all — and on SQLite a second connection writing while this one holds a write transaction on the same file
  gets `SQLITE_BUSY`, so PR5's happy path would **deadlock** rather than merely lose atomicity.
- It gives `PreImageMutation.Delete`, and therefore PostgreSQL's `FOR UPDATE`, the consumer it lacked — so
  `IAlvoSqlDialect.RowLockClause`'s remarks now describe a path that exists.

`create` opens one too, because it re-reads: **`CreateAsync` returns the row the database holds, not the
payload the caller sent.** Returning the candidate bag made the create response a different thing from the
update response, which already re-read — every database default missing, no `ETag` source for a 201, PR6's
`computed` column absent by construction (a `GENERATED ALWAYS AS … STORED` column has no value until the row
exists), and the caller unable to see the audit values the framework just assigned. One re-read inside the
transaction closes all four. It goes through the same composed root; `create` has no `USING`, so what
constrains it is the tenant scope the candidate was already checked against plus the row id just written, and
a row that cannot be read back is an invariant violation rather than a "not found".

### The `If-Match` precondition channel, landed in PR3 (#90)

PR2 left this as a note saying the *mechanism* was already in the right place — the merge-then-check pre-image
is read inside the transaction under the driver's row lock, which is exactly where a version comparison
belongs — and that PR3 would widen the signature when it owned the semantics. It now has, and this is what it
decided.

`UpdateAsync` and `DeleteAsync` take an `AlvoPrecondition?`; `CreateAsync` takes an `AlvoIdempotency?`. Both
sit **before** `CancellationToken`, which is a source break for a caller that passed the token positionally
(several tests in this repository did) and deliberately not a new overload: two overloads of a security-core
member is two things to keep in step.

**The version is `DateTimeOffset`, not an opaque string.** This port does not know what an HTTP `ETag` is; the
encoding belongs to the layer that speaks HTTP. `AlvoManagedColumns.VersionColumn` answers which column
versions a row, from the entity's **traits** — `updated_at`, and only on an `audit` entity — so a non-audited
entity has no version source at all.

**The version only ever comes out of the database.** PostgreSQL's `timestamptz` keeps microseconds, SQLite
keeps rendered text, and a .NET clock keeps 100-nanosecond ticks, so a version minted from `TimeProvider` at
write time would not equal the value the same write stored and every following `If-Match` would fail with
nothing to diagnose. `EfAlvoData.StoredVersion` reads it off the row-locked pre-image the `WITH CHECK` verdict
is already reached over — no second read — and `CreateAsync`'s existing re-read is what gives a 201 a version
in the first place. `AlvoDataConcurrencyTests.The_version_a_write_returns_is_the_one_a_following_precondition_accepts`
chains create → update → update, each precondition minted from the record the previous call returned.

**Three ordering rules, and they are the contract rather than an implementation detail:**

1. The comparison happens **inside the write transaction, against the locked pre-image**, so it cannot race the
   write it guards.
2. An entity with **no version column refuses a precondition** rather than ignoring it
   (`AlvoPrecondition.EnsureSupported`, from the schema alone, before any row lookup). A silently ignored
   `If-Match` is a lost update the caller believes it prevented — the only one of the three possible answers
   that tells them nothing.
3. **Invisibility outranks the precondition.** A row the `USING` predicate excludes raises
   `AlvoRecordNotFoundException` whichever precondition was supplied. The other order would confirm a row's
   existence to a caller who cannot read it, one request at a time, which is the oracle this port's whole
   failure contract exists to close.

The precondition is compared **before** `WITH CHECK`, which is a free choice between two already-visible-row
decisions: a stale precondition means the caller's patch was computed against a row that no longer exists in
that form, so a verdict over their merged post-image would be a verdict about a merge that should not happen.

### The idempotency-record table

`CreateAsync` with an `AlvoIdempotency` token records the key against the row it created, so a replay carrying
the same key and fingerprint returns that row and writes nothing. `IdempotencyTable` owns the name (via
`AlvoOptions.SchemaPrefix`, like the versions table), the DDL, and the two statements:

```sql
CREATE TABLE IF NOT EXISTS alvo_idempotency (
    idempotency_key TEXT NOT NULL,
    scope TEXT NOT NULL,
    fingerprint TEXT NOT NULL,
    row_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (idempotency_key, scope)
)
```

- **`scope` is part of the primary key, not a column beside it, and it carries the tenant *and* the acting
  user.** A key is the caller's own opaque string, so two clients collide on `"1"` — across tenants, and just
  as easily *within* one. A key space shared between two users in one tenant let one client's replay return the
  other's row, which is a row-level authorization bypass rather than a collision nuisance; it also made the
  409-versus-201 outcome a probe of the other client's key space. The scope is built by one member on the port,
  `AlvoIdempotency.IdentityOf`, because the reference implementation has to answer it identically and the two
  copies had no test that could catch them drifting. Its tenantless sentinel is the literal `global` rather
  than the empty GUID: no GUID text can equal it, so it needs no non-empty guard on `TenantId` — the empty-GUID
  version relied on an invariant nothing enforces.
- **No `entity` column.** One was stored and never read, which is a control that does not exist: it made a key
  unique per scope across every entity while telling the lookup nothing, so reusing a key on a second entity
  silently created nothing at all. `AlvoIdempotency.Fingerprint` covers the entity by contract (an HTTP
  fingerprint hashes method, path and body, and the path names the entity), so a matched fingerprint already
  proves the replay is for the entity the original wrote — and the same key on a different entity is a 409 like
  any other different request. A caller whose fingerprint does *not* distinguish the entity is still never
  handed a wrong row: the recorded id is re-read under the entity being served, is not there, and the answer is
  `AlvoRecordNotFoundException`. Both arms are pinned by
  `The_same_key_on_a_different_entity_is_a_conflict_not_a_silent_replay`.
- **`idempotency_key`, not `key`.** `KEY` is reserved in T-SQL, and §0 names Azure SQL as a target engine; this
  repository has already paid once for a T-SQL trap a seam's shape hid (see *Row locking has two grammars*).
  The **portability claim for this DDL is scoped to the two shipped engines**: `TEXT` is deprecated on T-SQL and
  would need `nvarchar` there. That mapping is follow-up work for whoever writes that driver rather than a
  guess made here for a driver nobody is writing.
- **The record stores a row id, never a response body.** A replay re-reads the row through the caller's
  *current* `get` policy, so it cannot hand back a representation that policy would no longer produce, and a row
  that has since been deleted answers `AlvoRecordNotFoundException` like any other missing row.
- **A different fingerprint under one key is a conflict, not a replay** (`AlvoIdempotencyConflictException`).
  Answering with the first row would report success for a create that never happened and silently discard the
  second payload.
- **The record's insert *is* the concurrency control.** Two requests carrying one key can both find no record
  and both insert a row; the primary key is what makes exactly one of them commit. The loser is rolled back and
  restarted, and its next attempt finds the winner's record and answers as a replay —
  `EfAlvoData.ReplayableCreateAsync`, ten attempts over ~450 ms, sized so ordinary contention on a real engine
  cannot exhaust it and still bounded, because a loop that retries forever turns a permanently failing write
  into a hung request. Which failure the loser sees is engine-specific and neither is distinguishable without a
  provider error code (which this package does not read — see `VersionRowWriter`'s own translation):
  PostgreSQL violates the primary key, SQLite refuses the write with `database is locked` before the key is
  ever consulted.
- **Exhaustion is `InvalidOperationException` with the provider exception inside**, not the raw
  `DbException`/`DbUpdateException`. The raw one escaped the five families `IAlvoData` promises a request layer
  can map a status from, so PR3's problem-details layer would have rendered a provider message as an unhandled
  500. It is family 3 — an invariant this implementation relies on — and the message names the exhausted retry
  and the constraint that guards the write.
- **Why a broad catch cannot become a false replay.** The retry catches any storage write failure, which
  includes a unique violation in the caller's *own* data (a duplicate `vin`). That never becomes a replay of an
  unrelated row, and the reason is structural rather than a classification: an attempt answers as a replay
  **only** if the lookup finds a record for this key in this scope, and a duplicate `vin` commits no such
  record, so every attempt takes the insert path again and fails again. Pinned on a real engine by
  `SqliteIdempotentCreateFailureTests` — which also needs the engine, since the in-memory reference cannot
  declare a unique constraint.

**The `CREATE TABLE IF NOT EXISTS` runs outside the write transaction, and that is measured rather than
tidiness.** Run *inside* it, the DDL serializes two concurrent idempotent creates — PostgreSQL will not let two
transactions create one table name at once, so the second blocks until the first commits and then finds the
record already there. The outcome is still correct, but the primary key is never reached: with the DDL inside
the transaction, `Two_concurrent_creates_with_one_idempotency_key_produce_exactly_one_row` **passed on real
PostgreSQL with the `PRIMARY KEY` clause deleted from the DDL**. Moving it out (ensure-once, on the context's
connection, before `BeginTransactionAsync`) makes the same deletion fail the fact, which is the only state in
which that fact is evidence of anything. A memo set outside a transaction is also honest, where one set inside
a transaction that later rolls back would claim a table exists that was rolled back with everything else.

**A replay re-reads under a freshly resolved `get` decision, never under the `create` decision the call
arrived with.** That was a row-level authorization bypass, and it is worth stating plainly because the first
implementation looked right: `create` has no stored row to filter, so `PolicyDecision.Using` is `null` by
contract and `ReadStatementComposer` renders it as a constant true. A replay read that way returns the recorded
row *whoever owns it* — and with the record's scope missing the acting user, a second client in the same tenant
sending the same key reached the first client's record and was handed their row. Two independent changes close
it, and each closes a different future one: the scope now carries the user (so the collision is unreachable),
and the read now resolves `get` (so even a reachable one is filtered by the caller's own predicate, and masked
by the caller's own `hidden` set — masking is per caller, and a replay must return what a `GET` by that caller
would). `A_replay_by_a_second_user_in_the_same_tenant_never_returns_the_first_users_row` fails if either half is
reverted, differently each way.

**A retry must not be worse than the create it replays, so a `get`-denied replay is no longer refused.** A caller
who may **create but not read** used to have their replay refused with `AlvoAuthorizationException`, while their
original create succeeded and returned its own row — the feature exists to make "did my first attempt land"
answerable, and for this caller it answered "you are not allowed to ask". When `_policy.Resolve(entity, Get,
context)` comes back denied outright (no policy allows `get` at all), the replay now answers with an `AlvoRecord`
carrying **only `id`**, taken from the idempotency record's own `RowId` — and performs **no row read** to produce
it. The safety argument is the record's own identity: it is keyed on the key, the tenant *and* the acting user
(`AlvoIdempotency.IdentityOf`), so a match proves this caller created that row, and the id disclosed is exactly
the id their own original `201` already gave them, in the body and in `Location`. This must never fall back to
reading the row under the `create` decision to mint that id-only record — that read is precisely the bypass
above, even with every field but `id` then discarded, because `create`'s `null` `Using` predicate would match the
row regardless of who owns it. `A_replay_on_an_entity_the_caller_cannot_read_performs_no_row_read` pins it, and
proves the "no read" half structurally by deleting the row before the replay: with the row physically gone, any
read of it — under any decision, `create`'s constant-true predicate included — answers
`AlvoRecordNotFoundException`, so the fact can only pass if the replay never reads it at all.

**The sibling case stays exactly as it was, deliberately.** A *configured* `get` whose own predicate excludes
this specific row (an entity whose rule is `USING (status == 'published')`, say) still reads, and the replay
still answers `AlvoRecordNotFoundException` — indistinguishable from a row that was genuinely deleted. Telling
the two apart would need a second, policy-free read, and refusing to add one is the more conservative of the two
errors; it is filed as an issue rather than fixed here.

**`EfCoreSchemaIntrospector` excludes both bookkeeping tables**, through
`SystemSchemaInitializer.FrameworkTableNames` — one member returning every framework table rather than a name
per caller, because an introspector that knows about one and not the next would plan a `DROP` for it on the
following re-apply, silently, and the symptom would be a lost idempotency history rather than an error. The
runner's fallback is the path that reaches it: `SchemaMigrationRunner` diffs against introspection whenever
there is no applied snapshot. **One fact per table**, in `AddAlvoIntegrationTests` against a real SQLite
database through the full container, each asserting the table physically exists (non-vacuity, read from
`sqlite_master`), that introspection does not report it as an entity, and that no step of the resulting plan
names it. The names are spelled out in the test rather than read from `FrameworkTableNames`, because taking
them from the member under test is how a name dropped from that member stops being checked at all.

**What actually breaks first, measured by deleting a name from that member**, is not the planned `DROP` this
section has claimed since PR2 — it is a hard `InvalidOperationException` out of the model build, *"the property
'id' cannot be added to the type 'alvo_idempotency'"*, because a bookkeeping table has no row key and the
property-bag model requires one. And it breaks on **every first run**, not only on a re-apply:
`SchemaMigrationRunner` reads the applied snapshot first — which is what creates these tables — and then falls
back to introspection because that read found no revision yet. Five pre-existing `AddAlvoIntegrationTests`
facts fail alongside the two new ones, so the exclusion was never as uncovered as it looked; what was missing
was a fact that *names the reason*, which is what makes a future failure diagnosable rather than mystifying.
The silent-`DROP` framing stays in the docs as the failure mode that would appear if the model builder ever
tolerated a keyless table.

**Its bind-parameter names (`@key`, `@scope`, `@fingerprint`, `@row_id`, `@created_at`) are
deliberately not in `PolicyParameterPrefix`.** That registry exists because one composed *read* statement
carries fragments from several renderers that never see each other's output; these two statements are
hand-written, single-fragment, and touch no entity table, so there is no second contributor a name could
collide with. `IdempotencyTable.cs` is added to `ChangeTrackerReachTests`' SQL-composing allow-list on the same
ground the five other framework-table files earn their place: it never touches an entity table. Its
`created_at` text form comes from `StoredInstant.Text`, this codebase's single conversion authority, rather
than a second `ToString("O")` beside `VersionRowWriter`'s.

### Row locking has two grammars, and T-SQL uses the one that is not a trailing clause

`IAlvoSqlDialect.RowLockClause` was first contracted as a clause appended at the very end of the statement.
That is PostgreSQL's grammar (`… ORDER BY … LIMIT … FOR NO KEY UPDATE`) and it is harmless on SQLite, which has
no locking clause at all. It is **not expressible on T-SQL**: SQL Server / Azure SQL takes a row lock as a
**table hint inside the `FROM`** — `FROM notes WITH (UPDLOCK, ROWLOCK)` — and has no trailing equivalent.

The trap was not that T-SQL was unsupported; it was that the seam made the wrong answer look right. Because
`string.Empty` is a *documented legitimate* answer (it is SQLite's), a T-SQL driver author following the
contract had two options: return the hint from `RowLockClause`, and get a syntax error on every `update`; or
return the empty string, and ship **silently unlocked `WITH CHECK` pre-images**. The second is a real
time-of-check/time-of-use race — Azure SQL runs READ COMMITTED by default, so a concurrent writer can change
the row between the verdict and the `ExecuteUpdate` — and it is indistinguishable from correct SQLite
behaviour. §0 principle 3 names Azure SQL explicitly and Alvo ships no driver for it, so the seam's *shape* is
the only thing protecting that author.

So `RenderTable` is told whether the read is a locking pre-image, and for which mutation:
`RenderTable(EntitySchema, PreImageMutation?)`. It is the member rendering at the position T-SQL's grammar
requires, and the argument is the mutation rather than a flag because a delete's pre-image needs a stronger
lock than an update's on either grammar. The rule the two members now share is that **exactly one position
carries the lock**: a dialect answering a different table source for a locking pre-image must return
`string.Empty` from `RowLockClause` for that same mutation. `AlvoSqlDialectContractTests` asserts the pairing,
so a driver cannot satisfy half of it.

`MMLib.Alvo.Testing.Data.TSqlSqlDialect` is the rehearsal that the seam is now sufficient — the statement-shape
counterpart of `TSqlFieldSqlRenderer`, which had already done this for expression shape. It emits the hint from
`RenderTable`, returns the empty string from `RowLockClause` honestly, and overrides `RowLimitClause` because
T-SQL spells truncation `OFFSET 0 ROWS FETCH NEXT @n ROWS ONLY`. `TSqlDialectSeamTests` composes a real
pre-image read through the production composer with nothing but the two T-SQL fakes registered, and neither
shipped driver nor the composer needed a change to accommodate it. That the fake did not exist is why the gap
survived to review: the one member T-SQL genuinely cannot express was the one nobody rehearsed.

**Still open for a T-SQL driver, and recorded so it is not rediscovered:** `ExecuteUpdate` emits
`UPDATE … SET … FROM (<policy root>) AS t WHERE t.id = n.id`, and `UPDATE … FROM` is **not universal SQL**. It
is a PostgreSQL/SQLite/T-SQL extension rather than a standard construct, the three engines spell it
differently, and T-SQL additionally names the *alias* as the update target rather than the table. EF's own
`ExecuteUpdate` translator produces the right shape per provider, so this is a risk a provider translation bug
would surface rather than one Alvo composes itself — but it is the second place a T-SQL driver will need its own
answer, and unlike the lock it is not behind a seam Alvo owns.

## `softDelete` is refused, not silently ignored

The frozen descriptor schema states the guarantee in full: *"Framework-managed soft delete: a managed
`deleted_at` column, DELETE becomes a soft delete, and reads/list/get/rollup auto-exclude soft-deleted rows.
A restore operation is provided."* **None of it was implemented.** Measured on real PostgreSQL:
`DeleteAsync` removed a row from a `softDelete: true` entity outright, and a row whose `deleted_at` was set
was still listed. That is irrecoverable data loss where the contract promises recoverability — the worst
failure mode in the diff, and the only one whose cost is not a wrong answer but a missing row.

**Ruling: refuse it, loudly, and do not implement it here.** Soft delete changes what every *read* means,
and that interacts with the policy predicate — the wrong thing to bolt on at the end of a PR. So:

- **At apply time**, `DescriptorToSchemaMapper` throws and the descriptor validator reports a
  `DescriptorValidationError` with a fix suggestion — exactly the shape `computed` already uses, and Alvo's
  own rule that a bad descriptor fails at save rather than per request.
- **At request time**, `DeleteAsync` refuses an entity whose `EntitySchema.SoftDelete` is set, in *both*
  shipped implementations. That is the fail-closed belt for a `SchemaModel` that did not come through the
  descriptor mapper — a host-assembled one, or F7's dynamic registry — and it is the same shape as
  `QueryFieldGuard.EnsureMaskable`. `InvalidOperationException`, because it is neither a denial nor a
  malformed query but a schema this port cannot serve.

`EntitySchema.SoftDelete` and `AlvoManagedColumns.DeletedAt` deliberately **stay**: the migrator still
creates the column for a hand-built schema, and the authority still reports it, so the implementation issue
inherits a shape rather than having to re-invent one. `AlvoManagedColumnsTests` is where that answer stays
pinned while the descriptor flag is unreachable.

**Two examples declared it and were amended in the same commit** — `examples/simple-tasks/tasks.alvo.json`
(`projects`) and `examples/complex-crm/crm.alvo.json` (`companies`, `deals`). `simple-tasks` is the "smallest
real backend" example, so leaving it declaring a flag that now fails at apply would have shipped a broken
starting point. This is a deliberate divergence from how `computed` is handled — that one stays in
`complex-crm` and is stripped by the test that maps it — and the reason is that `computed`'s failure mode is
a refused apply while `softDelete`'s was a deleted row. `examples/README.md` records the removal and says to
restore it when soft delete lands.

## A filter's shape is capped in three dimensions, by one guard

`AlvoFilter.EnsureWithinLimits` is the single entry point every implementation calls, and it caps depth,
**total term count** and **`in` candidate count** in one iterative walk. It replaced a depth-only
`EnsureWithinDepthLimit` that every implementation called faithfully while nothing capped breadth at all —
two guards would be two things to remember, and the one a driver author forgets is the one that was added
last.

Breadth was the third instance of a defect class this PR had already closed twice *per value* (the NUL
refusal, the UTC normalisation), each time with the same reasoning: one caller-supplied filter, an unhandled
provider exception on one engine and a silent answer on the other. Measured on both real engines:

| caller filter | SQLite | PostgreSQL 16 |
|---|---|---|
| 900 `AND` terms | ok, 14 ms | ok |
| **1000 `AND` terms** | `SqliteException` (expression tree too large, depth 1000) | **ok** |
| 1000 `IN` candidates | ok, 14 ms | ok |
| 32 000 `IN` candidates | ok, but **4.8 s** composing and parsing | ok |
| **40 000 `IN` candidates** | `SqliteException: too many SQL variables` after 3.5 s | **ok, 0.27 s** |

The caps, each justifiable in one sentence:

- **`MaxTerms = 256`** — a rendered `AND`/`OR` chain nests SQLite's parser once per term, so its default
  expression-tree ceiling of 1000 is the wall; 256 leaves room for the policy predicate's own terms in the
  same statement and is far past any query string a human or agent writes on purpose (256 terms answered in
  23 ms).
- **`MaxInCandidates = 1000` per list** — every candidate is one bind parameter, so 1000 keeps a whole
  statement inside SQLite's own 32 766-parameter ceiling even with several lists, and keeps the composition
  cost (seconds, at 32 000) off the caller's control.

An `in` list is counted with an **early exit**, because a candidate sequence is caller-supplied and may be
lazily generated: counting it to the end would let a hostile one run forever inside the guard that exists to
refuse it.

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
| Filter past `AlvoFilter.MaxDepth`/`MaxTerms`/`MaxInCandidates`, negative `Limit`, a paged read sorted by a nullable field | `ArgumentException` family | the query alone |
| `is` with a non-bool, `in` with a non-list, a value the column cannot hold, a fractional bound against an integral column, a NUL in text | `ArgumentException` family | the query alone |
| A schema this port cannot serve (`softDelete`), a field the read model does not map, an unknown bound-value origin | `InvalidOperationException` | the implementation's own invariant |
| Payload names a framework-managed column a caller may not write (`AlvoManagedColumns`), or a read-only or undeclared field | `AlvoAuthorizationException` | the payload alone |
| `get` of an invisible or absent row | `null` | the engine |
| `update`/`delete` of an invisible or absent row | `AlvoRecordNotFoundException`, identical message | rows affected / pre-image |
| Post-image fails `WITH CHECK` or the tenant scope | `AlvoAuthorizationException` | `IPredicateEvaluator` |
| A precondition that does not match the locked pre-image's version, or a precondition against an entity with no version column | `AlvoPreconditionFailedException` | the pre-image / the schema alone |
| An idempotency key already used for a request with a different fingerprint | `AlvoIdempotencyConflictException` | the recorded fingerprint |
| A replay whose entity allows this caller no `get` at all | `AlvoAuthorizationException` | a freshly resolved `get` decision |
| A replay whose recorded row is absent, or invisible under that `get` decision | `AlvoRecordNotFoundException`, identical message | that decision + the row |
| An idempotency token from an anonymous caller | `ArgumentException` family | the token and the context alone |

**Five families, and the boundary between them is the contract.** A request layer above this port has
nothing but the exception type to map a status code from, so it is stated on `IAlvoData`'s own remarks, where
a PR3 author reads it: `ArgumentException` = malformed query (422), `AlvoAuthorizationException` = denial
(403), `InvalidOperationException` = an invariant this implementation relies on (500), and PR3's two additions
— `AlvoPreconditionFailedException` = 412 (re-read and retry) and `AlvoIdempotencyConflictException` = 409
(send a fresh key). Neither of the last two is folded into `ArgumentException`: the request was well-formed,
and "your version is stale" and "your body is malformed" are different instructions to a client.

That needed settling because the two shipped implementations gave **four different answers** to four
malformed inputs:

| input | `InMemoryAlvoData` (before) | `EfAlvoData` (before) | both, now |
|---|---|---|---|
| `is` with a non-bool | `false`, row excluded | `AlvoAuthorizationException` | `ArgumentException` |
| `in` with a scalar or a bare string | `UNKNOWN`, row excluded | `AlvoAuthorizationException` | `ArgumentException` |
| `owner_id=eq."not-a-uuid"` | `UNKNOWN`, row excluded | `InvalidOperationException` | `ArgumentException` |
| `mileage=gt.12.7` | normalised and **answered** | `InvalidOperationException` | `ArgumentException` |

So `status=is.hello` — an ordinary agent typo — read as "not authorized": a 403 with no fix suggestion, in a
framework whose principle 4 is structured errors *with* fix suggestions. And `InvalidOperationException` is
the type the binder uses for genuine internal invariant violations, so a 422 and a 500 were
indistinguishable.

The reference implementation moved as much as the real one did, and that is deliberate: it could answer
`mileage=gt.12.7` exactly, because it compares in memory with no column type in the way. It refuses anyway,
for the reason `EnsureSortKeysCanBePaged` already gave — a reference implementation that answers where the
shipped backends refuse gives the port two contracts, and a driver author reading the inherited suite learns
the wrong one. `A_malformed_filter_is_refused_on_the_malformed_query_channel` is the shipped fact, with
`A_well_formed_filter_over_the_same_fields_still_answers` as its counterweight.

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

## One value funnel, for reading and for writing

`ColumnValue.For(clrType, column, value)` is the only answer to "what does this column hold, given this
value". Three call sites go through it: the parameter binder (a filter operand, a cursor value, a policy
predicate's column comparison), `WritePropertyBag` (an insert) and `UpdateSetterFactory` (an update).

It is one type because it was two rules, and the copy nobody was looking at was the wrong one. The read path
carried the whole funnel — the NUL refusal, the midpoint-rounding refusal, the UTC normalisation, the
`Guid`/`DateOnly`/`DateTimeOffset`/`TimeOnly` conversions `Convert.ChangeType` cannot do. The write path's
only type gate was the **reflection binder** driving EF's `SetProperty`, so:

| payload | read path | write path (before) |
|---|---|---|
| `price = 5L` against a `decimal` column | converts | `ArgumentException: Object of type 'System.Int64' cannot be converted to…` |
| `created_at = "2001-01-01T00:00:00Z"` | converts | the same reflection failure |
| `mileage = "10"` | converts | the same reflection failure |

Every value `System.Text.Json` produces for a JSON number or an RFC 3339 string, refused on the write path of
a framework whose stated primary user is an agent emitting JSON. And `StoredInstant.Stored`'s own remark —
*"a value that is not timestamp-shaped is passed through untouched … EF's own change tracker still rejects it,
with its own message"* — was false: the message was a reflection `ArgumentException`, not EF's.

The read path's funnel is the authority rather than the reverse because it is the tested one and its three
refusals each exist for a measured reason. A write now inherits all of them, which is the point:
`SqliteAlvoDataWriteTests` pins the fractional-into-integral and NUL refusals on the write side, and
`AlvoDataOrderingTests.A_write_accepts_every_value_the_read_path_converts` pins the conversions on both real
engines. `StoredInstant` lost its second entry point (`Stored`) in the same change — having two was precisely
how the write path came to apply the timestamp normalisation and none of the funnel's other rules.

**It converts; it does not validate.** An integer written to a `string` column becomes its invariant text,
exactly as it already did when compared against one. Deciding that a JSON number is the wrong *shape* for a
declared `string` field is schema-derived request validation, which belongs above this port — and a per-path
guess about it is how the two paths came to disagree.

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

One of them ships from a different **assembly** while keeping that namespace.
`AlvoSqlDialectContractTests` needs `IAlvoSqlDialect`, which lives in the EF package rather than in
`Abstractions`, so it lives in the companion project `MMLib.Alvo.Testing.EntityFrameworkCore` while
`MMLib.Alvo.Testing` stays Abstractions-only. That is not tidiness: `MMLib.Alvo.Testing` is referenced by
every test project and earns a package when *external provider authors* need these suites, so an EF dependency
there would hand EF to an author whose store is not EF-backed — foreclosing the audience the package exists
for. The namespace is shared so nothing a consumer already wrote moves, and `EfDependencyBoundaryTests`
asserts the boundary, because the runtime arch fact matches EF's types by name and would never notice.

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
fails. `PreImageMutation.Delete` and its stronger `FOR UPDATE` are exercised the same way, now that a delete
reads a locked pre-image for PR5's sake; `LockRecordingSqlDialect` additionally records *which* mutation the
lock was requested for, which is the only way that request is observable on an engine whose answer is the empty
string.

## Mutation-testing notes

Mutation runs post-merge on `main` (`.github/workflows/mutation.yml`), across five parallel configs. Nothing
blocks a merge on the score, so a red run is a notification someone has to act on — which makes it worth
knowing, before the merge, that each config is configured to answer at all.

> **The absolute scores below and elsewhere in this repository are not currently evidence — see #142.**
> Measured on Stryker 4.16.0 / .NET SDK 10.0.100 / MTP: the runner reports mutants as **Killed** that
> demonstrably survive the suite (124/124 "Killed", 100.00 %, for two files that the configured test project
> does not exercise at all; applying the same mutation by hand fails nothing in 731 tests). It is not an
> always-red suite — `--break-on-initial-test-failure` does not abort. Until #142 is understood, treat a high
> score as unproven and `break: 80` as unable to fire. Every "100.00 %" recorded in this file and in commit
> messages predates that measurement and may be the same artefact.

### Each config was verified non-vacuous, and here is how

A config that discovers **zero** mutants, or whose test projects yield **zero** tests, reports a false green;
this repository has shipped exactly that once already (`project_mutation_gate`), which is why the workflow's
own step greps for `0 total mutants will be tested` and `Number of tests found: 0` and fails loudly. Config
correctness therefore has to be measured, not asserted.

It was measured with a **discovery-only probe** rather than a local mutation run (which `CLAUDE.md` forbids):
each config was started with `dotnet-stryker -f ../<config> --concurrency 4` **from `test/`** (see below —
the working directory is load-bearing), watched until Stryker had printed the two numbers the workflow greps
for, and then killed before the mutation loop began. That exercises the whole configuration — glob resolution,
project resolution, the MTP runner, the initial test run — without paying for the run.

Measured 2026-08-02 on Stryker 4.16.0 / .NET SDK 10.0.100 / xunit.v3 3.2.2:

| Config | Mutated project | Tests found | Mutants to be tested | In the matrix? |
|---|---|---|---|---|
| `stryker-config.expressions.json` | `MMLib.Alvo` (`Expressions/**`) | 722 | 834 | yes |
| `stryker-config.json` | `MMLib.Alvo` (the rest, minus `Api/**`) | 722 | 657 | yes |
| `stryker-config.data-ef.json` | `MMLib.Alvo.Data.EntityFrameworkCore` | 858 | 596 | yes |
| `stryker-config.data-sqlite.json` | `MMLib.Alvo.Data.Sqlite` | 403 | 38 | yes |
| `stryker-config.data-postgresql.json` | `MMLib.Alvo.Data.PostgreSql` | 101 | 16 | yes |
| `stryker-config.api.json` | `MMLib.Alvo` (`Api/**`) | 333 | 1502 | **no — on demand** |

F3's PR3 took `stryker-config.json` from 478 mutants to 2159, of which 1502 were `Api/**`. Splitting them is
arithmetically exact — 657 + 1502 = 2159 — but **`Api/**` has no matrix leg**, so the Data API's query parsing,
authorization filter, idempotency and ETag code is not under mutation today. That is a declared, tracked gap
rather than an oversight, and it costs nothing real:

- judged by `MMLib.Alvo.Tests` (which is what `stryker-config.json` did until `Api/**` was excluded) the score
  is an artefact — that project contains no test touching `Api/**`, yet every mutant came back Killed; it is
  #142's repro;
- judged by `MMLib.Alvo.Api.Tests` — the suite that does exercise it, which `stryker-config.api.json` now names
  — the measured cost is **6.3 s/mutant** (124 mutants in 779 s), so ~2.6 h for 1502 on a 10-core dev machine
  and more on a 4-vCPU runner: no budget under GitHub's 6 h ceiling produces a verdict without sharding it
  several ways;
- sharding it that way is premature while #142 makes the resulting score untrustworthy, and a leg that always
  times out is noise rather than a gate.

The five matrix configs are non-vacuous. The two driver configs are small on purpose — each driver is two files of rendering
— and small is the point: `TrueLiteral => "1"` mutated to `"0"` inverts a boolean inside a policy `WHERE`, and
until PR2 the only other thing pinning those literals was an accepted Verify baseline, the one artefact a test
can be made green with.

### The working directory decides what the suite is (and the earlier numbers here were the tell)

Started in a directory containing `MMLib.Alvo.slnx`, Stryker enters solution mode and **ignores each config's
`test-projects`**, substituting every test project in the solution that references the mutated assembly. Same
commit, same 834 mutants, `stryker-config.expressions.json`: **2211** tests found from the repo root, **722**
from `test/`. The 1489 extra tests include the Testcontainers-backed `.Tests.Integration` projects that every
config deliberately excludes. Since a run costs mutants × suite, that is a silent ~3× — the cause of the three
shards that timed out on run 30292141967 (#99), and of `data-ef` regressing from 10 minutes at `2b6b340` to
past 120 without its config changing: F3's PR3/PR4 added `Api.Tests`, `Api.Tests.Integration` and `Host.Tests`,
which reference the mutated assemblies and were therefore swept into every shard.

Measured for `data-ef` specifically, the shard that regressed with no config change: **858** tests from `test/`
against **1489** from the repo root, and **596 mutants either way**.

**The previous edition of the table above was already showing this and nobody read it that way**: it recorded
1267 tests for a config whose single `test-projects` entry is `MMLib.Alvo.Tests` (722 today, fewer then). A
"tests found" number larger than the listed test projects can hold is the signature of the bug. From `test/`
there is no solution file and `test-projects` is honoured (`Analyzing 2 test project(s)` for `data-ef`), so the
paths inside every config are relative to `test/`, not to the repository root.

`scripts/assert-mutation-run` keeps it that way, and the shape of that guard is itself a lesson. Its first
version was `grep -q 'will mutate solution'` — a **negative** assertion, which fails **open** the moment a
Stryker release rewords the line, i.e. the same class of defect as the vacuous run it sits next to. It now
makes four **positive** assertions after every shard:

1. **not vacuous** — no zero-mutant run, no "unable to calculate a mutation score" (stryker-net#3094);
2. **the suite is the configured one** — the run must report analysing exactly as many test projects as the
   config declares, read from the config with `jq` so it cannot drift from what it guards;
3. **the mutate glob still matches** — the mutant count must not have fallen below 60 % of the calibrated
   `mutants:`;
4. **the suite did not shrink** — the test count must not have fallen below 60 % of the calibrated `suite:`.

Note which number catches solution mode and which does not: the mutant count is *identical* in both modes
(834/834, 596/596), so only assertion 2 can catch it. Assertions 3 and 4 exist for the two collapses a score
cannot show — a glob that stops matching after a rename, and a suite that stopped biting — both one-sided,
because growth is never the defect and a band would fail every ordinary PR. Assertion 4 matters especially
while #142 stands: with the score untrustworthy, a gutted suite would otherwise pass every check.

Two lessons are embedded in the implementation rather than the assertions, and both were found by review after
the guard was already "verified". The guard normalises the log into a temp **file** instead of piping into
`grep`, because `printf '%s\n' "$text" | grep -q PATTERN` under `set -o pipefail` returns **141** whenever grep
matches early and exits while `printf` is still writing — true of every real (hundreds-of-KB) log, and it
inverted assertion 2 so completely that the guard would have failed all five shards while quoting back the line
it claimed was missing. And the workflow step drops `set -e` around the Stryker pipeline before capturing
`PIPESTATUS`, because GitHub runs `shell: bash` as `bash -eo pipefail`: without that, a below-threshold run
aborted the step *before* the assertions could say what had actually gone wrong.

`scripts/test-assert-mutation-run` keeps the guard honest, because a guard whose job is "do not trust a green
signal" is worth nothing unguarded. It runs the guard over **real Stryker logs captured from both working
directories** and committed, with provenance headers, under `scripts/fixtures/mutation-logs/` — the `data-ef`
pair above and the `expressions` pair, plus a genuinely vacuous run. Perturbations (a reworded log line, a
collapsed count, ordinary drift) are derived from a fixture inside the harness with `sed`, so every committed
fixture stays real captured output. Two cases exist purely to pin the reasoning rather than the behaviour: one
asserts that **both** expressions fixtures report 834 mutants, so nobody re-proposes the mutant-count check that
cannot separate the modes; the fail-closed cases assert that a *reworded* line still fails, which is the only
thing that distinguishes the current positive assertion from the negative grep it replaced — reverting to that
grep passes every other case in the suite. One case builds a **>64 KiB** log on the fly, because the committed
fixtures are 3-5 KB and the pipe-buffer defect above is invisible below that size.

### `coverage-analysis: off` is a measurement, not a preference

Every config pins it. Under the MTP runner per-test coverage is not implemented upstream
([stryker-net#3516](https://github.com/stryker-mutator/stryker-net/issues/3516), open), and enabling it is both
wrong and pointless. Same 90 mutants of `MMLib.Alvo.Data.Sqlite`, same machine, same `--concurrency 4`:

| `coverage-analysis` | tested | Killed | NoCoverage | score | wall clock |
|---|---|---|---|---|---|
| `off` | 38 | 38 | 0 | 100.00 % | 63 s |
| `perTest` | 33 | 33 | 5 | 86.84 % | 59 s |

The five `NoCoverage` mutants are **false** — with coverage off the very same mutants are Killed. They sit in
`SqliteCaseSensitiveLike.cs` (lines 44, 45, 62) and `SqliteSqlDialect.cs` (137, 168): the LIKE shape and the
dialect rendering this gate exists to protect. Because Stryker counts `NoCoverage` against the score,
`perTest` reported 86.84 % for a suite that kills everything. It also bought nothing — 59 s against 63 s —
which [stryker-net#3750](https://github.com/stryker-mutator/stryker-net/pull/3750) explains: the MTP
`runTests` filter is serialised under a property the platform server does not bind, so a "filtered" run
silently executes the whole assembly anyway. That same defect is why the score moves in the first place, and
it can move in *either* direction, since tests outside the batch run against whichever mutant is active.
Re-measure before re-enabling; do not take this table on trust once Stryker is upgraded.

**The `data-ef` `test-projects` list is a result, not a hypothesis.** It names both
`MMLib.Alvo.Data.EntityFrameworkCore.Tests` and `MMLib.Alvo.Data.Sqlite.Tests`, because the killing tests for
`EfAlvoData`, `SortSqlRenderer`'s engine behaviour, `UpdateSetterFactory` and `WritePropertyBag` live in the
latter; the probe confirms 858 tests reach the run, which is the two projects together rather than the EF
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

Two of those rows are now closed: `AlvoQuery.Offset` (PR3 task 1) and the `If-Match`/`Idempotency-Key`
channels (PR3 task 2, #90 — see *The `If-Match` precondition channel* above). What PR3's own HTTP layer still
owns, and this port deliberately does not: how a version is spelled on the wire (`ETag` quoting, weak vs
strong), what a request's idempotency **fingerprint** is computed over, and whether an idempotency record is
ever pruned.

The **fingerprint** clause is now closed too: PR3 task 7 defines it as `SHA-256(method \n entity \n canonical
body)`, hex, where *canonical* means re-serialized from the parsed document with property names sorted
ordinally (`MMLib.Alvo.Api.Internal.IdempotencyFingerprint`). The route is deliberately **not** in the digest,
so moving `AlvoApiOptions.RoutePrefix` does not invalidate stored fingerprints.

#### Idempotency-record retention is an operator's job, and there is nothing automatic yet

Task 7 wired `Idempotency-Key` over HTTP, which makes `<prefix>_idempotency` the one framework table an
outside caller can add rows to. **Nothing expires a record**, so the table grows by one row per keyed create,
for ever — every row is read at most once (by the retry it exists to answer) and then never again.

Scope of the growth, stated so it is not mistaken for something worse: a record requires a caller who
authenticated **and** passed the entity's `create` policy, so it is a strict subset of the writes that caller
may already perform. There is no unauthenticated amplification and no cross-tenant reach (the record's scope
is the tenant plus the acting user — `AlvoIdempotency.IdentityOf`). It is a housekeeping cost, not a
vulnerability.

Until a retention window ships, prune it by hand. The `created_at` column exists for exactly this and nothing
else reads it:

```sql
-- Records older than the longest retry window any client of yours uses. Nothing reads a record after the
-- retry it answers, so anything past that window is dead weight. Run it as often as the table's growth
-- warrants; it takes no lock a write path waits on for long, and a concurrent create simply inserts again.
DELETE FROM alvo_idempotency WHERE created_at < '2026-01-01T00:00:00.0000000+00:00';
```

`created_at` is stored as portable text in the round-trip format `StoredInstant.Text` writes
(`DateTimeOffset` with `"O"`), so it compares lexicographically in the same order it compares chronologically
as long as every row carries the same offset — which the framework's own clock guarantees, since it always
writes UTC. Deleting a record for a key a client is *still* retrying costs that client one duplicate row on
its next retry rather than a replay, so the window has to outlast the longest retry any client performs.

**Filed as #115, not built:** a configurable retention window plus a background sweep. It is scheduler-shaped
work — Alvo has no hosted-service seam yet — and it belongs with whatever ships the first one rather than
bolted onto a request path. `docs/product/baas-analyza.md` puts cron and retention in the scheduling
component, which is where the issue is filed.

### PR5a — the outbox (#22, the durable half) — **shipped**

This section used to predict this work. What it predicted held, and both halves are now facts rather than
guidance. The subsystem's own record is [`events.md`](./events.md); what belongs *here* is only what it did to
this data path.

- **The transaction was the right seam, and it is the seam.** `EfAlvoData` has four emit sites — the create
  path, the idempotent recorded create, the update path and the delete path — and each is inside the
  transaction that write already opened. All four go through one private `EmitAsync`, which builds the envelope
  with `OutboxEventFactory.For` and hands it to `OutboxTable.InsertAsync` on `db.Database.GetDbConnection()`
  with `command.Transaction = transaction.GetDbTransaction()`, so the event and the row commit together or not
  at all — no second connection, no distributed transaction. The emit is **last** at every site, after the
  write's own re-read succeeded, so an event never describes a row the write did not produce.
  `AlvoDataOutboxTests`, which both drivers inherit, is where that is proved, including the two facts a
  rollback answers: `A_write_the_engine_refuses_leaves_no_outbox_row` and
  `An_insert_on_a_rolled_back_transaction_leaves_no_row`.
- **The `SaveChanges`-interceptor trap is closed, and a mutation is why that is a fact rather than a warning.**
  The idiomatic EF place to hang an outbox is a `SaveChangesInterceptor`, and on this data path it would
  silently never fire for an update or a delete — the two operations that most need an event — because
  `ExecuteUpdate`/`ExecuteDelete` do not go through the change tracker. The emit is therefore sequenced
  **explicitly on the transaction** at each site, and the suite covers update and delete first
  (`An_update_emits_exactly_one_event_carrying_both_images`,
  `A_delete_emits_exactly_one_event_carrying_the_pre_image`). Deleting either emit turns those facts red,
  which is the evidence the warning became a constraint the tests hold.
- **What makes the insert atomic is the *connection*; `command.Transaction` is the contract, not the
  mechanism.** Measured: deleting that assignment leaves every outbox fact green on both shipped engines,
  because a SQLite and a PostgreSQL transaction both belong to the connection. It stays because ADO.NET's own
  contract requires it — `SqlCommand` throws when a connection has an open transaction the command does not
  name — and because the seam is a claim about which transaction the row rides. The mutation that *is* caught
  is the one that moves the insert onto a connection of its own.
- **What the outbox added to this package's own guards.** `OutboxTable.cs` and `EfCoreOutboxStore.cs` are in
  `ChangeTrackerReachTests._sqlComposingFiles`, and the same class's
  `The_outbox_claim_is_raw_sql_and_never_linq_over_the_context` keeps both files off LINQ over the context —
  which is what closes the `UseRelationalNulls()` cost by construction (see that section above). The outbox
  table's name is in `SystemSchemaInitializer.FrameworkTableNames`, so a re-apply plans no `DROP` for it. No
  change-tracker write was added anywhere.
- **Still owed by PR5b:** before-hooks. They run *inside* the write, and the create path as built cannot
  satisfy their DoD line — `AuthorizedCandidate` runs before `BeginTransactionAsync`, so a hook placed where
  the candidate is built has nothing to roll back. That is a correction to the F3 design, not a deferral, and
  it is recorded in [`events.md`](./events.md)'s inheritance list.

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
