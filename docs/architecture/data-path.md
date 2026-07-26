# The data path

How an Alvo read or write becomes one SQL statement, and the decisions that shape it. Written during F3 PR2
(#20).

> **Status: partial.** This file is created early, by the slice that needed somewhere to record a port
> decision. **PR2's Task 12 completes it** — the mechanism end to end, the three parameter prefixes, the
> no-`SaveChanges` rule, the all-optional read model, and the F7 notes. What is below is only what is already
> settled, so a later reader can tell a decision from an oversight.

## One statement, one `WHERE`

Every read composes exactly one statement, in `ReadStatementComposer`: a `SELECT` list, a `FROM`, and a
`WHERE` whose terms are the resolved `USING` predicate, the synthesized tenant scope, and then — only ever
`AND`-ed onto those, each fully parenthesised — a row id, the caller's filter and a keyset cursor. Nothing a
caller supplies can reach the policy term's nesting level, and a snapshot of that one string is the proof
that the policy predicate is in the `WHERE` clause rather than applied afterwards.

A `hidden` field is not omitted from the `SELECT` list — EF refuses a `FromSql` result set missing a mapped
property — it is projected as a typed SQL `NULL` under its own alias, so the column is never read.

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

`PredicateParameterBinder` binds every value through `IRelationalTypeMappingSource` — and, where the call
site knows its column, through *that column's* mapping, after converting the value to the column's CLR type.
Formatting a value into text is not a cosmetic shortcut: EF's SQLite `Guid` mapping writes upper-case `TEXT`,
so a hand-formatted lower-case Guid matches no row and raises nothing.

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

## Mutation-testing notes

`ReadStatementComposer.Collect` refuses a parameter name two fragments both claim. It is **unreachable
today** — every fragment renders with a name from `PolicyParameterPrefix` and those are pairwise disjoint by
test — and deliberately kept, because last-writer-wins on a real collision returns wrong rows with no error.
Task 12 should record it as a known survivor (or exclude the arm) rather than let it read as a missing test.
The same applies to `Internal/AlvoDataSeed.cs`, which is test-only seeding and is excluded from mutation by
design.
