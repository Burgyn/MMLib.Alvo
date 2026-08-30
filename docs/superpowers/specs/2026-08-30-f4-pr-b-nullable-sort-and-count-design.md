# F4 PR-B — a nullable field becomes a sort key, and a page can carry its total

Issues: **#116** (paged read over a nullable sort key), **#110** (`Prefer: count=exact`).

Filed separately, designed together because they are the same shape of change to one surface:
**the list endpoint's two remaining "modelled but unreachable" members.** `AlvoSort.Nulls` parses,
validates and is unobservable; `AlvoPage.TotalCount` exists and is always `null`. Both were left
that way deliberately in F3 — the first by a guard, the second by "PR3 owns the paging surface" —
and both are now the first thing a demo asks for. Neither touches the rule engine's SQL rendering,
the tenancy predicate, or the policy resolve, so this is **not a security-core change**; #110 does
compose a *second* statement over the resolved `USING` predicate, which is the one place it sits
beside the security core, and it is reviewed against `alvo-security-core-review` for that reason
alone.

---

## Part 1 — #116: paging over a nullable sort key

### What is broken, precisely

`AlvoQuery.EnsureSortKeysCanBePaged` refuses **any** nullable sort key on a paged read. Every HTTP
list is paged (`limit` always resolves, to `DefaultPageSize` when the request names none), so over
HTTP:

- `?order=display_name` on a nullable column is **422**, and `display_name` is the single most
  obvious thing an agent asks a generated list for;
- `order=<field>.nullsfirst|.nullslast` parse, validate, and can change nothing — null placement
  only matters on a nullable column, and a nullable column is refused. **Half the published sort
  grammar is decorative.**

### Why the guard exists, and why that reason has expired

`KeysetSqlRenderer` renders the boundary as a nested-OR expansion with **no `IS NULL` arm**:

```
(k > @k0 OR (k = @k0 AND (… OR (… AND id > @id))))
```

A `NULL` on either side makes the term `NULL`, and a `WHERE` treats that as false. Under
`nullslast` the null-keyed tail became unreachable; under `nullsfirst` the first page's anchor had
a null key and page two came back empty. Paging just stopped, **silently** — which is why F3 chose
refusal over a wrong answer. `docs/architecture/data-path.md` records the ruling and, in the same
paragraph, names its own successor:

> Making such a page work needs an `IS NULL`-aware boundary whose predicate form depends on the
> anchor's own null-ness […] and must stay in lockstep with `SortSqlRenderer`'s rank expression or
> it reintroduces exactly the order/boundary divergence above. **PR3 owns that.**

This is that work. The one correction to the sentence above: `KeysetAnchor` does **not** have to
grow a member to carry the anchor's null-ness — `KeysetAnchor.Values` already holds `null` for a
null-keyed anchor, and the renderer branches on that. Nothing is added to the record.

### The order this boundary has to agree with

`SortSqlRenderer` already renders a nullable key as **two** ordering terms:

```
CASE WHEN <raw col> IS NULL THEN <a> ELSE <b> END,   <repaired col> [DESC]
```

with `(a,b) = (0,1)` for `nullsfirst` and `(1,0)` for `nullslast`. The rank term carries **no**
direction suffix, so it is always ascending; the direction applies to the column term only. The
sort key is therefore the pair `(rank, value)` compared lexicographically, and the boundary must
compare **that same pair**, not the value alone. That is the whole of the bug and the whole of the
fix.

### The four shapes, derived rather than chosen

Expanding `(rank, value) > (rank₀, value₀)` and folding away the arms that are constant:

| anchor value | placement | boundary term for this key |
|---|---|---|
| not null | `nullsfirst` | `(col ⊘ @v OR (col = @v AND tail))` — **unchanged from today** |
| not null | `nullslast` | `(col IS NULL OR col ⊘ @v OR (col = @v AND tail))` |
| null | `nullsfirst` | `(col IS NOT NULL OR tail)` |
| null | `nullslast` | `(col IS NULL AND tail)` |

where `⊘` is `<` for a descending key and `>` for an ascending one, `tail` is the next key's term
(ending in the ascending `id` tie-breaker), and a **non-nullable** declared field keeps today's
first row verbatim.

Three things fall out of the derivation and are worth stating, because each is a place a
hand-written version would have gone wrong:

1. **The direction never reaches the null arms.** Where nulls sort is decided by the rank term,
   which is always ascending; `DESC` flips only the value comparison. Row 2 is the same under
   `.desc` as under `.asc`.
2. **`nullsfirst` with a non-null anchor needs no new arm at all.** Nulls sort *before* the anchor,
   and `col ⊘ @v` is already `NULL`→false for them, so they are excluded for free. The existing
   shape is correct there, which is why this table has two rows that add nothing and two that do.
3. **`col = @v` is never emitted against a null anchor.** In rows 3 and 4 the surviving rows are
   exactly the ones whose key is also null, so they tie by construction and the term collapses to
   `tail` — never to `col = NULL`, which is the three-valued trap the F3 renderer fell into.

The `IS NULL` / `IS NOT NULL` test reads the **raw** column, not the one repaired by
`IFieldSqlRenderer.RenderComparableOperands` — same rule `SortSqlRenderer` already follows: a cast
`NULL` is still `NULL`, and the raw column is the form an index can serve. Every value comparison
still goes through the repaired pair, unchanged; a cursor is only comparisons, and one repaired
side against one unrepaired side is how a page skips a row.

### Keeping the two renderers in lockstep

The stated risk is that the `ORDER BY`'s rank and the boundary's null arms drift into disagreeing.
Two mechanisms, one structural and one behavioural:

- **Structural** — `AlvoNullPlacement.First` is read in exactly one expression in each renderer and
  both derive from the same enum; there is no second spelling of "nulls come first" to get wrong.
  A shared helper was considered and rejected: the two need *different* artifacts from the same
  fact (a `CASE` expression vs. a choice between two SQL shapes), so a shared helper would be a
  seam with one caller for each of its two outputs — the shape this codebase has already recorded
  as how a member ends up with zero real callers while its tests pass.
- **Behavioural, and it is the real one** — a new inherited fact in `AlvoDataPagingTests`:
  **page all the way through a set with a nullable key, one row at a time, and the concatenation
  must equal the unpaged sorted read of the same query.** Order and boundary disagreeing is
  observable there and nowhere else, and being inherited it runs on SQLite, on PostgreSQL and on
  the in-memory reference — the three implementations that must not diverge. Run for the four
  combinations of `{asc, desc} × {nullsfirst, nullslast}`, over a fixture whose nullable key has
  several nulls *and* several duplicate non-null values, so both the null-bucket tie-break and the
  ordinary one are exercised.

### What the guard becomes: deleted, not relaxed

The issue proposes honouring an **explicit** null placement. That is rejected, and the reason is
that it would not fix the reported problem. `AlvoSort.Nulls` is non-nullable with a default of
`Last`, so "explicit" is not representable without modelling explicitness — and even then,
`?order=display_name` (the day-one request the issue is about) carries no placement and would still
be refused. The caller would have to learn to write `?order=display_name.nullslast` to sort by a
name.

Once the boundary can express *any* placement, there is nothing left for the guard to refuse: the
order over nulls is total and known for every `AlvoSort` the port can construct. So
`AlvoQuery.EnsureSortKeysCanBePaged` is **deleted**, along with its API-layer wrapper
`QueryViolations.UnpageableSortKey` and the `unpageable-sort-key` violation code. A public API
removal, recorded in `PublicApi.MMLib.Alvo.Abstractions.verified.txt`; Alvo is pre-1.0 and this
member's whole purpose was to refuse something now supported. Keeping it as a no-op would publish a
guard that guards nothing, and every implementer of `IAlvoData` would keep calling it forever.

`AlvoQuery.EnsurePagingWindowIsSane` stays exactly as it is; the two were siblings only in where
they lived.

### The cost, stated

`SortSqlRenderer`'s `CASE WHEN … IS NULL` rank is the one index-defeating construct in this data
path. F3 restricted it to unpaged reads precisely because on a paged read it was provably inert —
the guard had already refused the only case that could make it matter — and that restriction was
argued as protecting §2.1's *p95 < 50 ms on an indexed column*. **This change makes it
load-bearing on a paged read**, so a paged sort over a nullable column now pays it. That is not a
regression to hide: the alternative is the current behaviour, which is to refuse the query. Two
things follow, and both are recorded rather than fixed here:

- Sorting by a **required** column — the fixture pattern F3 grew, and what a keyset page should
  use when latency matters — is untouched: no nullable key, no rank term, same plan as today.
- The real fix for the nullable case is a per-dialect native `NULLS FIRST`/`NULLS LAST` behind
  `IAlvoSqlDialect`, which both shipped engines support and which an index can serve. That is the
  same seam `docs/architecture/data-path.md` already names for this criterion (beside the
  row-value-constructor question, **#100**). Filed as a follow-up; deliberately not in this PR,
  because it is a public port member with three implementations and a contract fact, and it would
  put an undesigned change through a review that is about something else.

---

## Part 2 — #110: `Prefer: count=exact`, filling `AlvoPage.TotalCount`

### The port models the capability; the wire models the preference

`AlvoQuery` gains one additive member:

```csharp
public bool IncludeTotalCount { get; init; }
```

**Not** an `AlvoCountMode { Exact, Planned, Estimated }`. The issue anticipates the question and
answers it the same way: `planned`/`estimated` mean reading the planner's row estimate, which
PostgreSQL can do through `EXPLAIN` and SQLite has no equivalent for worth the name. Modelling all
three on the port would publish a distinction **no driver honours**, on a port whose own
documentation already records that exact mistake for a projection member ("adding it now would
publish a port member both shipped drivers and the in-memory reference silently ignore, so a caller
reaching the port directly would ask for two fields and receive every one, with nothing raised").
§0 principle 3 says the *behaviour* is identical across engines; a mode that is real on one engine
and a lie on the other is the violation, not the omission.

So: the port can count exactly, or not count. The three RFC 7240 spellings are an **HTTP**
vocabulary and the degradation is an **HTTP** decision, taken where the header is read and reported
in the header RFC 7240 provides for exactly this. When a driver one day gains a real estimate, the
port grows a mode and `AlvoPage` grows the applied one — additively, at the point where the
distinction becomes true.

### The count is over the policy-filtered set, and that is provable

`ReadStatementComposer` gains a `ComposeCount`, sharing the **same** term-composition method as
`Compose`, so the `USING` predicate, the synthesized tenant scope and the caller's filter are the
identical terms in the identical order. It differs in exactly three ways, each of which is the
point:

- the projection is `COUNT(*)` rather than the masked field list — nothing is projected, so nothing
  can leak, and `ReadProjection`/`QueryFieldGuard.EnsureMaskable` are not reached;
- the **keyset anchor is not composed**, because the count is of the whole filtered set, not of the
  rows after the cursor;
- no `ORDER BY`, no `LIMIT`/`OFFSET`, no row lock — a count has no order and is not truncated by
  the page it accompanies.

`AlvoDataStatementTests` gains an inherited fact that a counted list emits **two** statements and
that the second binds the `alvo_u`/`alvo_t` prefixes in its own `WHERE`. That is the same
criterion, and the same proof, F3 established for the page itself: an implementation that counted
the table and subtracted returns a plausible number and passes every outcome-level test.

### How the count executes

Through EF (`Database.SqlQueryRaw<long>(sql, parameters).ToListAsync()`), not a raw ADO command on
the context's connection. Three reasons, and the third is the one that decides it:

1. the same `PredicateParameterBinder` binds the same values through the same column mappings;
2. no second execution path to keep in step with the first;
3. **`SqlCapture` observes EF's own `DiagnosticListener`**, so a raw command would be invisible to
   the statement suite — and "the count carries the policy predicate" is precisely a claim no
   returned number can carry.

`SqlQueryRaw<T>` used as a query root with no LINQ composed over it emits the text verbatim; the
`AS Value` alias EF's documentation requires applies only when composing, so the composed SQL stays
`SELECT COUNT(*) FROM …` with no EF artifact in it. `.ToListAsync()` is used rather than
`.SingleAsync()` for that reason — the latter composes, and would wrap the statement in a subquery.

`COUNT(*)` is `bigint` on PostgreSQL and a 64-bit `INTEGER` on SQLite, so `long` is the type on both
and `AlvoPage.TotalCount` is already `long?`.

### One deviation, stated: the count is not atomic with the page

PostgREST computes its count in the same statement, with `COUNT(*) OVER ()`. Alvo cannot: that
window is evaluated after `WHERE`, and Alvo's `WHERE` carries the **keyset boundary** — so on any
page but the first it would count the rows after the cursor, not the set. (It would work for
offset paging, which is exactly how you get two shapes and one of them wrong.)

So it is a second statement, on the same connection, in no transaction — and a write interleaving
the two can make the number disagree with the rows by one. `exact` in RFC 7240's sense means *not
an estimate*, not *atomically consistent with the page*, and read-committed would not deliver the
latter anyway without escalating every counted list to `REPEATABLE READ`. Recorded in
`docs/architecture/data-api.md` and in the `count` member's own published description; not fixed.

### The wire

**Request.** `Prefer: count=exact|planned|estimated`, parsed per RFC 7240 §2: a comma-separated
list of preferences, `token[=value]`, other preferences in the same header ignored, the *first*
`count` preference honoured if the header repeats it. `planned` and `estimated` are accepted and
**degrade to exact**.

**Response.** `Preference-Applied: count=exact` (RFC 7240 §3) whenever a count was actually
computed — so a caller who sent `count=estimated` is told, in the standard's own channel, that they
received an exact count instead.

**A deliberate departure from this codebase's own "refuse, never ignore" rule, and the reason.**
Everywhere in the query string, an unrecognised key or modifier is refused, because an ignored
`?oder=name` answers with unsorted data and the sender cannot tell. `Prefer` is different **by
definition**: RFC 7240 §2 says a server that does not recognise or cannot satisfy a preference
*MUST* ignore it, and gives `Preference-Applied` as the detection channel. So `Prefer: count=exakt`
is ignored, no count is computed, and `Preference-Applied` is absent — which is exactly how the
standard says a client learns its preference was not applied. Inventing a variant of a standard is
a defect, not a shortcut (`CLAUDE.md`); the detection the house rule protects is present, just in
the standard's place rather than ours.

**No `Vary: Prefer`.** RFC 7240 suggests it where a response varies by the header. Every generated
response already carries `Cache-Control: no-store` from `NoStoreResponseFilter`, so no cache — shared
or private — may store the representation, and a `Vary` has no addressee. Stated so it is not read
as an oversight.

**The envelope gains a third member, always present:**

```json
{ "items": [ … ], "next": null, "count": 42 }
```

`count` is `null` when none was asked for. Always-present-and-nullable rather than omitted, because
that is the rule the envelope already states for `next` ("`null` on the last page rather than
omitted, which is why the published schema marks both `required` — a statement about the bytes, not
an aspiration"). A second rule for the third member is how an envelope becomes something a client
has to probe. `SchemaComponentBuilder` adds it to `required` with the other two.

**OpenAPI.** A shared `prefer` request-header parameter on the list operation, a
`Preference-Applied` response-header component on its 200, and the `count` property on the page
envelope — all three in the files that are already the single authority for each
(`DataApiParameters`, `DataApiHeaders`, `SchemaComponentBuilder`). The document snapshot moves.

### Cost, and why it stays opt-in

An exact count is a second full scan of the filtered set on every page. As a default it would make
every list roughly twice the work for a number most callers never read — §2.1 requires it to be
opt-in and the analysis names `count(*)` on large tables as the specific expense. Nothing changes
for a caller who sends no `Prefer` header: no second statement is composed and none is executed.

---

## What this PR does not do

- **No native `NULLS FIRST`/`NULLS LAST`** per dialect (the index-friendly fix for Part 1's cost) —
  a public `IAlvoSqlDialect` member with three implementations and a contract fact; filed as a
  follow-up.
- **No planner-estimate count** — `planned`/`estimated` degrade, and the port gains no mode for
  them until an engine can honour one.
- **No `#175`** (T-SQL `nvarchar(n)` vs. `maxLength` in code points) — deliberately deferred, open
  question about `nvarchar(max)`, and unrelated to this surface.
- **No projection push-down** (`#117`) — `select` remains a response-side narrowing.

## Files this touches

| File | Why |
|---|---|
| `Abstractions/Data/AlvoQuery.cs` | delete `EnsureSortKeysCanBePaged`; add `IncludeTotalCount`; rewrite the `Sort` remarks |
| `Abstractions/Data/AlvoPage.cs` | `TotalCount` is no longer "always null in F3" |
| `Abstractions/Data/IAlvoData.cs` | `QueryAsync` remarks: the count contract |
| `Data.EntityFrameworkCore/Internal/KeysetSqlRenderer.cs` | the four shapes |
| `Data.EntityFrameworkCore/Internal/SortSqlRenderer.cs` | remarks only — the rank is now load-bearing on a paged read |
| `Data.EntityFrameworkCore/Internal/ReadStatementComposer.cs` | shared term composition + `ComposeCount` |
| `Data.EntityFrameworkCore/Internal/EfAlvoData.cs` | drop the guard call; count when asked |
| `Testing/Data/InMemoryAlvoData.cs` | drop the guard call; count the filtered set |
| `Api/Internal/QueryStringParser.cs` | drop the guard call; read `Prefer` |
| `Api/Internal/QueryViolations.cs` | delete `UnpageableSortKey` |
| `Api/Internal/PreferHeader.cs` *(new)* | RFC 7240 parse |
| `Api/Internal/DataApiPage.cs` | `count` |
| `Api/Internal/DataApiEndpoints.cs` | wire `Prefer` in, `Preference-Applied` out |
| `Api/Internal/DataApiParameters.cs`, `DataApiHeaders.cs`, `DataApiDocumentation.cs`, `SchemaComponentBuilder.cs` | the published contract |
| `docs/architecture/data-api.md`, `data-path.md` | both records are now wrong in the same two places |
