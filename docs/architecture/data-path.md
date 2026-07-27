# The data path

How an Alvo read or write becomes one SQL statement, and the decisions that shape it. Written during F3 PR2
(#20).

> **Status: partial.** **PR2's Task 12 completes it** — what is still missing is the parameter-prefix table,
> the all-optional read model's own section, and the F7 notes. What is below is settled, so a later reader can
> tell a decision from an oversight.

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
- **Strings — divergent, and deliberately left alone.** SQLite compares `TEXT` with `BINARY` collation;
  PostgreSQL uses the database collation, where `'a' < 'B'`. So one `AlvoSort("title")` yields a different first
  page on the two engines. **Verdict: acceptable, not a defect to repair here.** Collation is a property of the
  *database* a host configures, not of Alvo's rendering; forcing one (`COLLATE "C"`, `COLLATE BINARY`) would
  override an operator's deliberate choice and make every string sort non-sargable on PostgreSQL. It is also
  already the reason relational operators on a string are refused in the Rule profile
  (`CelTypeChecker`: *"collation-dependent and are not available"*), so refusing to *order* by a string would
  be inconsistent with allowing it to be sorted at all. What matters for correctness is that the boundary uses
  the identical unrepaired operand, so a page is self-consistent on each engine — which it is.

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

## The failure contract, and where each refusal is decided

| Situation | Outcome | Decided from |
|---|---|---|
| No policy for the operation | `AlvoAuthorizationException` | the decision alone |
| Entity undeclared, or `EntityStorage.Dynamic` | `AlvoAuthorizationException`, one shared message | the applied schema |
| Filter/sort names a hidden or undeclared field | `AlvoAuthorizationException`, one shared message | the decision + schema |
| Filter deeper than `AlvoFilter.MaxDepth`, negative `Limit`, a paged read sorted by a nullable field | `ArgumentException` family | the query alone |
| Payload names `id`, or `tenant_id` on update, or a read-only or undeclared field | `AlvoAuthorizationException` | the payload alone |
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

`ReadStatementComposer.Collect` refuses a parameter name two fragments both claim. It is **unreachable
today** — every fragment renders with a name from `PolicyParameterPrefix` and those are pairwise disjoint by
test — and deliberately kept, because last-writer-wins on a real collision returns wrong rows with no error.
Task 12 should record it as a known survivor (or exclude the arm) rather than let it read as a missing test.
The same applies to `Internal/AlvoDataSeed.cs`, which is test-only seeding and is excluded from mutation by
design.

`ReadStatementComposer.RequiresTotalOrder`'s three disjuncts each have an independent killing fact, plus the
negative, so there is no survivor for Task 12 to chase: `A_sorted_read_carries_its_order_by_inside_the_one_statement`
(sort only), `A_limited_read_orders_before_it_truncates_and_binds_the_limit` (limit only),
`A_cursored_page_is_ordered_even_with_no_caller_sort_key` (anchor only) and
`An_unsorted_unlimited_first_page_needs_no_ordering_at_all`.
