# The HTTP Data API

> What a host gets when it calls `MapAlvoDataApi()`: five generated minimal-API routes per declared
> entity, a PostgREST-shaped query string, RFC 9457 problem documents, `ETag`/`If-Match` optimistic
> concurrency and `Idempotency-Key` on create. This file records the decisions that outlive PR3's
> plan — the URL grammar and its allow-lists, the cursor's contract, what the framework treats as
> confidential and what it publishes, and the surprises a reader will otherwise rediscover. Spec §2.1
> (Data API), §0 principle 8 (minimal API, not MVC), §0 principle 5 (secure-by-default / default-deny).

## Position A: the declared schema shape is public; data and a `hidden` field's *name* are not

This is the framework's confidentiality position, and it has to be stated somewhere findable, because
two anti-enumeration claims have already been written in this repository and only a locatable statement
prevents a third.

**What Alvo treats as confidential:** row data, and — **on the read surface and in the published
document** — the name of a field the descriptor marks `hidden`, which there stays indistinguishable from
the name of a field that does not exist.

**That boundary stops at the write surface, and the scope is deliberate rather than an oversight.** A
caller who may write can tell a hidden field from an undeclared one: writing to a hidden field is accepted
(201/200) while an undeclared key is refused 422 `unknown-field`, so two requests answer "does this entity
have a field called X". Closing it would mean refusing the hidden write, which contradicts the bounded
exception below — a `required` hidden field must be writable or its create is impossible — so the
behaviour stays and the guarantee is scoped honestly instead. What a write-capable caller can learn is a
**name**, never a value: the read surface returns one `null` for hidden and undeclared alike, and no
response ever carries the field.

**What Alvo publishes:** the declared, non-hidden schema shape — which entities exist, and what
non-hidden fields each declares. Two shipped behaviours make that a design rather than a leak to be
closed later:

- `DataApiEndpoints.Map` maps the entity name as a **route literal** (never a `{entity}` parameter), so
  entity existence is disclosed by routing, *before* any authorization runs. A declared entity answers
  401/403; an undeclared one answers the router's 404.
- `AlvoDocumentTransformer` publishes the declared non-hidden field list of every entity to anyone who
  can read the OpenAPI document.

A framework cannot publish its schema shape and simultaneously treat that shape as confidential. So the
line is drawn at data and at the hidden-field set, and everything below is consistent with it: a filter
over a hidden field is refused **exactly** as one over an undeclared field is (`QueryFieldResolver`
returns one `null` for both), and `IPolicyEngine`'s deny reasons name neither the entity nor the row.

**The one bounded exception**, which is deliberate and **needs the maintainer's ratification**: a
`hidden` field appears in a *request* schema only if the descriptor also marks it `required` **and the
caller may write it** (`SchemaComponentBuilder.Belongs`). Not an *if and only if*: `required` is necessary,
not sufficient — `Belongs` also excludes a `readOnly` field and a framework-managed name the caller cannot
write, so a field declared `required + hidden + readOnly` (a legal descriptor today, and one no create can
satisfy — issue **#124**) appears in no schema at all. The extra conditions only ever withhold more.

Excluding a `required` hidden field from the request schema too would document a create nobody could
perform, since a caller cannot supply a mandatory field it was never told exists. An **optional** hidden
field's name still appears nowhere. The cost, stated: a field that is hidden, writable and optional is
absent from the request schemas while a write to it is still accepted, so the document understates what a
create will take — the safe direction, since a caller following the document sends less than it may.

## A `hidden` field is writable, by design

`hidden` restricts **reading**; `readOnly` restricts **writing**. A write to a hidden field is accepted.

Validation that "helpfully" refused it would silently change `IAlvoData`'s contract, and — because a
`required` hidden field must be writable or its create is impossible — would make a legal descriptor
unusable over HTTP.

**What it would *not* buy is confidentiality, and an earlier version of this section claimed it would.**
That argument said refusing the write would "disclose that the field exists". It is backwards: refusing a
hidden write with its own error and refusing an undeclared key with 422 `unknown-field` are two
*distinguishable* answers, and so are accepting the one and refusing the other. Either way a write-capable
caller learns the field exists; only a refusal that was byte-identical to `unknown-field` would not
disclose it, and that would silently drop a write this API refuses to drop anywhere else. So the asymmetry
is load-bearing for the **contract**, not for the hidden-field set — see the scope stated at the top of
this document, which is where the guarantee's real boundary lives.

## The RLS surprise: a *configured* rule that excludes you is 200 with an empty page, not 403

**This is the single most confusing thing about the API, and every future "denied" test in this
repository has to know it.**

A configured rule compiles to a row-level `USING` predicate. A caller who fails that predicate gets an
**allow whose predicate matches nothing** — so:

```json
"rules": { "list": "'auditor' in @user.roles" }
```

denies nobody. A caller without the role gets **`200 OK` with `{"items": [], "next": null}`**. That is
Postgres RLS semantics and correct by design — a row-level filter is not an operation-level gate — but an
agent reading the API expects 403.

### The decision procedure: the four ways a caller actually gets 403

Confirmed from `Rules.Internal.PolicyEngine.Resolve`, in the order it checks them:

| # | Cause | Where |
|---|---|---|
| 1 | **Unknown entity** — the applied schema declares no such entity (or the name is blank) | `Resolve`, `catalog.TryGetEntity` |
| 2 | **The tenant guard** — the entity is `tenancy: scoped` and the caller carries no tenant, refused before any rule is consulted | `CheckTenantGuard` |
| 3 | **An unconfigured operation** — the rule this operation needs was never written. `IsUnconfigured` is `Using is null` for `list`/`get`/`delete`, `WithCheck is null` for `create`, and **either** for `update` | `IsUnconfigured` |
| 4 | **A missing required context value** — the operation's predicates read `@user.id` or `@tenant.id` and this caller has neither | `CheckRequiredContext` |

So: **if you wrote a rule and the caller is refused, it is one of these four, and it is not your
predicate.** If you wrote a rule and the caller gets an empty page, it *is* your predicate.

(A list request resolves this decision **once** above the port — `DataApiEndpoints.EnsureOperationIsAllowed`,
which refuses before the query string is parsed *and* hands the parser this caller's `hidden` mask — and the
port resolves it again as the authority. Both compile the per-caller mask expressions, so one request pays
that twice; the cost is **#118**. The `ScopeGate` that runs earlier is **not** a third resolution of this
decision: it tests the key's own scopes and never consults `IPolicyEngine`, and it is the *only* authority
for `out-of-scope`, because the port never sees scopes.)

Two notes on the boundaries of that table:

- `Resolve` has a fifth deny branch — no descriptor has been applied yet, so no policy is configured. It is
  the **first** check it makes, not the last; it is fifth only in this document's numbering, and
  `PolicyEngine`'s own summary calls it step 1 of five.
  **Not reachable over HTTP in the default wiring**, and the reason is worth stating precisely, because the
  obvious one is not sufficient: it is not merely that routes come from the applied schema, but that
  `ISchemaRegistry` *is* the `PolicyCatalogProvider` by default (`Rules/Setup.cs`), so an unprimed catalog
  reads as an empty model — zero entities, therefore zero routes, and the router's 404 arrives first. A host
  that registers its **own** `ISchemaRegistry` (an escape hatch `IPolicyCatalogProvider`'s remarks
  document) can map real route literals over an unprimed catalog, and then this branch does answer. It is
  listed here so a reader of `PolicyEngine` does not think the table is wrong.
- A row-level `USING` exclusion on `get`/`update`/`delete` is a **404**, not a 403 and not an empty page
  — one row, and "invisible to me" is deliberately indistinguishable from "not there"
  (`AlvoProblemTypes.NotFound` is one slug for both).

Alongside the 403s, `out-of-scope` is a *second* 403 with its own slug: a presented API key whose scopes
do not cover this entity and operation. It is a fact about the credential rather than about whether data
exists, and it has a different fix (grant the key a scope, rather than change a rule).

## The URL grammar, and the two allow-lists that bound it

PostgREST's syntax is adopted deliberately, so an agent recognises it from training data.

```
GET    {prefix}/{entity}
GET    {prefix}/{entity}/{id:guid}
POST   {prefix}/{entity}
PATCH  {prefix}/{entity}/{id:guid}
DELETE {prefix}/{entity}/{id:guid}
```

`{prefix}` is `AlvoApiOptions.RoutePrefix`, default `/api`; the empty string mounts at the root.

**Filters.** `?<field>=<operator>.<value>`, and every non-reserved query key **is** a field name — an
unrecognised key is refused, never ignored, because an ignored `?oder=name` answers with unsorted data
and the agent that sent it has no way to notice. Several parameters conjoin (`AND`), which is PostgREST's
own semantics and the only reading in which adding a term narrows the set.

- Groups: `or=(a.eq.1,b.eq.2)` / `and=(…)`, splitting on top-level commas with bracket nesting respected.
  **Deviation from PostgREST, stated:** a *nested* group is spelled with the `=` inside a group too
  (`or=(a.eq.1,and=(b.eq.2))`), where PostgREST writes `and(b.eq.2)`. One grammar in one place beat a
  parser that quietly accepts two dialects; widening to PostgREST's exact nested form later is additive.
- Negation: a single leading `not.` on a key or a group member. `not.not.` is not in the grammar.
- `order=<field>[.asc|.desc][.nullsfirst|.nullslast][,…]`, `select=a,b` or `select=alias:a`,
  `limit`, `offset`, `after`.

### Sorting over nulls

Where a `NULL` sorts is **never** left to the database: SQLite and PostgreSQL disagree on the default for a
given direction, so the placement is always explicit in the emitted statement — `nullslast` unless the key
says otherwise — and it is emitted as the portable `CASE WHEN <key> IS NULL THEN 0/1 ELSE 1/0 END` rank
(spike `Q3c`), always ascending, ahead of the value term the direction applies to.

**The keyset boundary compares that same pair**, which is what makes a nullable key pageable. It is not the
value comparison plus a special case: expanding `(rank, value) > (rank₀, value₀)` and folding away the arms
that are constant leaves four shapes, two of which are identical to the non-nullable form. The full
derivation is in `docs/architecture/data-path.md`; the property that matters here is that order and boundary
are two renderings of one fact, and the inherited paging walk — page a null-bearing set one row at a time and
compare with the unpaged read — is what holds them together.

**The cost is real and it is a reason to sort by a required column.** The `CASE` rank cannot be served by an
index on the sort key, so a paged sort over a nullable field is slower than one over a required field, which
emits no rank at all. The index-friendly fix is per-dialect native `NULLS FIRST`/`NULLS LAST` behind
`IAlvoSqlDialect` — both shipped engines support it — and it is **#178**, deliberately not bundled with
the change that made the read legal.

### `select` — the projection, and what it costs

`select` reaches the `SELECT` list, so `?select=id` stops the engine reading the columns it did not name
(**#117**, closed). It does **not** shorten the column list: reads run through `FromSqlRaw` over a
property-bag entity mapping every schema field, and EF fails if a mapped column is missing from the result
set — so an unselected column is rendered `NULL AS <col>` and its key is dropped when the record is
assembled. That is the mechanism `hidden` already used, proven on both engines, and it keeps
`IAlvoSqlDialect` out of the change entirely.

**The honest scope of the win:** the engine stops *reading* the column, which is real for a wide or
TOASTed value and near zero for a narrow int. It is not a proportional speed-up, and
`AlvoDataStatementTests` therefore asserts only what a statement can carry — that the column is not
fetched.

**Two groups of columns are read whatever the projection names**, and neither appears in the response
unless it was named:

- The framework-managed columns, through `AlvoManagedColumns.For(entity)`. `IAlvoData`'s returned-key-set
  contract requires it, and the keyset cursor is minted from the fetched row's `id` — a NULLed row key
  would not mis-sort a page, it would break paging.
- Every field named in `order`. **Measured on SQLite 3 and PostgreSQL 16 alike:** a bare identifier in
  `ORDER BY` resolves against the *output* column names first, so a NULLed sort key would order the page
  by the `NULL` while the keyset boundary in `WHERE` still described the real sequence — a page that skips
  or repeats a row. A filter term, the cursor anchor and the policy predicates need no such exemption,
  because both engines resolve the table column in `WHERE` and ignore the alias. That measurement is what
  makes the feature safe at all: a compiled `USING` predicate's field references are not enumerable, so
  had `WHERE` behaved like `ORDER BY`, `!has(owner_id)` over a NULLed column would have rendered
  `NOT("owner_id" IS NOT NULL)` → true and admitted every row.

**Aliases** (`select=label:make`, PostgREST's own spelling, **#111**) are a response concern and never
reach the port: `AlvoQuery.Select` carries source names, and the API renders the response's key list.
The source is resolved through the same resolver every other field name goes through, so an alias cannot
reach a field the caller may not read. Four refusals, all pointing at `select`: a malformed pair or an
alias outside the field-name grammar (`malformed-select-alias` — a deliberate narrowing of PostgREST,
which admits an arbitrary alias, because an alias is a field name *in the response*); a reserved name as
an alias (consistency with what a descriptor may declare, not necessity); a key claimed twice, whether by
two different sources or by an alias onto **any** framework-owned name — `AlvoManagedColumns.All`, not this
entity's own subset, because the caller is minting a name rather than resolving one
(`colliding-projection-key`); and more distinct keys than the caller has readable fields
(`projection-too-wide`).

**What the alias deliberately does not refuse:** a rename onto another declared field's name.
`?select=year:make` answers `{"year": "skoda"}` where the published schema declares `year` an integer.
PostgREST behaves the same way, the caller chose both halves, and the value is one they may read; refusing
it would make the alias useless for the renaming it exists for.

That last bound exists **because** of aliases. Before them the projection was self-bounding — every entry
resolved through `QueryFieldResolver` to a declared field and duplicates collapsed — so a response could
never carry more keys than the entity has fields. An alias can name one column under arbitrarily many keys,
leaving only the transport's URL limit in the way. It is charged per newly claimed *distinct* key rather
than on the entry count, which is what keeps `?select=id,id,id` deduping as it always has, and follows the
precedent `FilterParseScope.TryChargeNode` set: a budget spent after the parse does not bound the parse.

**The number is the caller's readable field count, not the entity's declared one**, and that is a
confidentiality decision rather than a tightening: the count is published in the refusal's fix suggestion,
and an unprojected list already tells the caller how many fields they can read — so publishing the declared
count would hand them the size of their own mask, the one bit the byte-identical `unavailable-field`
refusal exists to withhold.

### Allow-list 1: the ten operators, derived and not written out

`eq neq gt gte lt lte like ilike in is` — `FilterOperators.WireNames` is derived from the
`AlvoFilterOperator` enum by lower-casing, so an operator added to the port cannot ship unreachable over
HTTP or reachable under a spelling nobody chose. Resolution is ordinal: `EQ` is not an operator. An
unrecognised spelling is a **refusal, never a fallback to `eq`** — a mistyped operator that quietly became
equality would answer a different question than the caller asked, and §2.1 names the operator allow-list as
one of the two defences against injection through a filter.

### Allow-list 2: which operators apply to which field type

Enforced against `CelFieldType.Of(field)`, in three places rather than one — `FilterTermParser.IsApplicable`
carries the `like`/`ilike` and ordering rows, `TryReadIdentity` carries the `is` row, and `in`
short-circuits before `IsApplicable` is reached:

| Operators | Allowed on |
|---|---|
| `eq`, `neq`, `in` | every field type |
| `like`, `ilike` | `string`, `text`, `enum` only |
| `gt`, `gte`, `lt`, `lte` | `string`, `text`, `enum`, `integer`, `decimal`, `date`, `datetime` — **not** `boolean`, `uuid`, `ref`, `json` |
| `is` | `null` on any field; `true`/`false` only on `boolean` |

**Why ordering is narrowed** (issue **#95**): a rendered `uuid > @p` is a real comparison on PostgreSQL
while the in-memory reference evaluator resolves it to `UNKNOWN` and returns no row. §0 principle 3 is
that one filter behaves identically on every engine, so the ambiguous cases are closed at the parser
rather than diverging below it.

**The cost is real, and #95 needs an expiry rather than an open-ended "someday".** `id=gt.<uuid>` is a
*standard* keyset-pagination idiom — an agent that has paged any other API will reach for it — and Alvo
refuses it. The right resolution is per-engine ordering semantics for `uuid`, not a permanent narrowing,
and the issue should carry a milestone that says when.

### The reserved field names, and why the descriptor is refused rather than the request

Eight names are reserved: `order`, `limit`, `offset`, `after`, `select`, `or`, `and`, `not`. The
descriptor field-name grammar (`^[a-z][a-z0-9_]{0,62}$`) admits every one of them, so a field called
`limit` is a legal descriptor and `?limit=10` would be genuinely ambiguous — a request could not tell a
filter on such a field from the parameter itself.

The ambiguity has **no correct per-request resolution**, so it is refused **before the server listens**
instead — stage 0 of the boot, over the descriptor's mapped schema and with no database access — naming the
entity, the field and the full list. The explicit apply path refuses it too, and the data source keeps a
belt for a substituted `ISchemaRegistry` (see *Route generation happens at enumeration time*). `not` is
reserved even though it is only ever a prefix: inside
`or=(…)`, the member `not.eq.x` is either a negated term or a filter on a field called `not`, and nothing
in the grammar distinguishes them.

### The budgets, and where each number comes from

| Bound | Value | Owner |
|---|---|---|
| Filter depth | 32 (`AlvoFilter.MaxDepth`); the parser stops at 30, reserving two levels | port |
| Filter terms, per request | 256 (`AlvoFilter.MaxTerms`) | port |
| `in` candidates, per list **and** per request in total | 1000 (`AlvoFilter.MaxInCandidates`) | port |
| Cursor length | 512 chars (`QueryStringParser.MaxCursorLength`) | API |
| Page size | `limit` ≤ `MaxPageSize` (200); absent ⇒ `DefaultPageSize` (50) | API options |
| Request body | 1 MiB, depth 32, 512 keys | API options |
| `Idempotency-Key` | ≤ 255 UTF-8 **bytes**, and a host may only narrow that | port + options |

The two term/candidate numbers are measured rather than chosen: 900 filter terms answered in 14 ms and
1000 threw a raw `SqliteException`; 40 000 `in` candidates threw `too many SQL variables` on SQLite after
3.5 s where PostgreSQL answered in 0.27 s. The per-request candidate *total* exists because 256 terms each
carrying a maximum list is 256 000 bind parameters in one statement, past the 32 766 ceiling the per-list
bound was measured against.

Every parser refusal is a **422** with slug `malformed-query` and a `violations` array; refusals are
de-duplicated on `(code, pointer)` rather than capped at a count, so one repeated `filter-too-wide` can no
longer crowd out the `limit` mistake in the same request.

## The list endpoint always pages, and that is not a defect

Every HTTP list resolves a `limit`: the caller's, or `AlvoApiOptions.DefaultPageSize` when the request
names none. There is no way to ask this surface for an unpaged read.

That is **§2.1's requirement**, not an omission — a server-enforced maximum page size, because an
unbounded `limit` is a denial of service one query long.

**The port's unpaged read stays, and is for in-process callers.** `AlvoQuery.Limit` is `int?`, and `null`
means no explicit limit; the EF driver returns the whole visible set with no cursor. An embedded host
calling `IAlvoData` directly may still read a whole set. Both halves are stated here so that neither is
"fixed": the port keeps the capability, the HTTP surface deliberately does not expose it.

One consequence used to follow and no longer does: because every HTTP list is paged and a paged read over
a nullable sort key was refused, `?order=<any nullable field>` was a 422 and `nullsfirst`/`nullslast` were
unobservable — half the published sort grammar, unreachable. **#116** closed that: the keyset boundary now
compares the same *(where the null sorts, then the value)* pair the `ORDER BY` ranks by, so a nullable key
pages like any other. See *Sorting over nulls* below for what it costs.

## Paging: keyset over an opaque cursor, and its real cost

The response is a JSON envelope, always all three members:

```json
{ "items": [ … ], "next": "3q2-796tvE-cKTMlvKYbGw", "count": null }
```

`next` is `null` on the last page rather than omitted, which is why the published schema marks all three
`required` — a statement about the bytes, not an aspiration. `count` follows the same rule and is `null`
unless the request opted in; see *The count is opt-in* below.

### The cursor's contract, and why the API layer cannot mint one

The cursor is **opaque and provider-issued**. For the EF driver it is base64url over the anchor row's
primary key and nothing else (`KeysetCursor`, `internal` to that package). The API layer echoes it back
verbatim, bounded at 512 characters, and never decodes it.

The API layer *cannot* mint one, and this is why `QueryAsync` returns an `AlvoPage` rather than a list:

- The encoding is the provider's private business precisely so it stays free to change. A second encoder
  above the port would be a second copy of one fact.
- Only the provider can answer **"is there another page"** without a second round trip: `PageAsync`
  over-fetches by one row. A cursor derived from `Items.Count == limit` would be minted for a page that
  returned exactly `limit` rows because the visible set ended there — a bug that only appears when the row
  count is a multiple of the page size.
- A cursor carries **no data**, so it can leak none. The anchor's sort-key values are re-read under the
  same policy predicate as the page itself, so a stale, forged or cross-tenant cursor finds no anchor and
  yields an **empty page** rather than telling its holder anything.

### The cost, stated honestly: per-page cost grows with cursor depth (issue #100)

On a **multi-term sort**, the keyset predicate is a nested disjunction, and the per-page cost **grows with**
cursor depth rather than staying flat. Not *linear* in it, on the evidence: #100 measured rows-removed-by-filter
growing one-for-one with depth (280 001 at depth 280 000) while wall-clock grew 107× across a 28 000× depth
increase — so the row count is linear and the latency is sublinear but unbounded. "Grows with" is what the
measurement supports; say that rather than the tidier claim. What keyset paging buys here is **stability**, and that is what §2.1 asks
for and what is proven: correct and non-duplicating under concurrent writes, measured over 1 000 000 rows.

Two things this is **not**:

- It is **not** a claim of depth-independent paging cost. Do not write that here or anywhere else.
- It is **not** a §2.1 violation. §2.1 asks for keyset paging that is *stable* over 1M rows, which holds;
  its 50 ms budget is a **filtered-list** criterion that this does not touch.

The fix is a row-constructor comparison (`(a, b) > (@a, @b)`) where the engine supports it, tracked in
**#100**.

`offset` is the opt-in second mode. A request may not combine `after` and `offset` — they anchor the same
window two different ways, and answering with one would silently resolve an ambiguous request
(`AlvoQuery.EnsurePagingWindowIsSane`).

### The count is opt-in: `Prefer: count=exact` (#110)

`Prefer: count=exact` fills the envelope's `count` with **how many rows the query matches**, not how many
this page holds. `Preference-Applied: count=exact` reports what was done (RFC 7240 §3).

- **Opt-in, and the default is no count.** An exact count is a second full scan of the matching set on every
  page; as a default it would make every list roughly twice the work for a number most callers never read.
  §2.1 requires it to be opt-in and the analysis names `count(*)` over a large table as the expense. A
  request that sends no preference composes and executes no count statement at all.
- **`planned` and `estimated` are accepted and degrade to `exact`.** A planner estimate is engine-specific —
  PostgreSQL has `EXPLAIN`, SQLite has no equivalent worth the name — and §0 principle 3 makes identical
  behaviour the contract, so a mode real on one driver and fictional on the other belongs on neither.
  `Preference-Applied` is where the caller who asked for an estimate learns they received the real count.
- **The port models the capability, not the preference.** `AlvoQuery.IncludeTotalCount` is a `bool`. The
  three RFC 7240 spellings are HTTP vocabulary and the degradation is an HTTP decision, taken where the
  header is read. When a driver can honestly estimate, the port grows a mode and `AlvoPage` grows the applied
  one — additively, at the point the distinction becomes true.
- **The count is over the policy-filtered set.** It is composed by `ReadStatementComposer.ComposeCount` from
  the *same* `WHERE` terms as the page — the resolved `USING` predicate, the synthesized tenant scope, the
  caller's filter — with the projection, the ordering, the row window and the **cursor boundary** all
  dropped. A count over the bare table returns a plausible integer and passes every row-level test while
  telling a caller how many rows exist outside what they may read; `AlvoDataStatementTests` asserts the
  second statement carries the policy prefixes in its own `WHERE`.

**One deviation, stated.** PostgREST computes its count in the same statement, with `COUNT(*) OVER ()`. Alvo
cannot: that window is evaluated after `WHERE`, and Alvo's `WHERE` carries the keyset boundary, so on any
page but the first it would count the rows *after* the cursor rather than the set. (It would work for offset
paging, which is exactly how you end up with two shapes and one of them wrong.) So it is a second statement,
on the same connection, in no transaction — and a write interleaving the two can make the number disagree
with the rows by one. **`exact` means "not an estimate", not "atomically consistent with `items`"**; read
committed would not deliver the latter anyway without escalating every counted list to `REPEATABLE READ`.

**Unrecognised preferences are ignored, not refused** — the one deliberate departure from this API's own
"refuse, never ignore" rule. RFC 7240 §2 makes `Prefer` advisory and requires a server to ignore a preference
it does not recognise or cannot satisfy, and §3 gives `Preference-Applied` as the channel for saying so. So
`Prefer: count=exakt` yields no count and no `Preference-Applied`, which is precisely how the standard says
that is reported. Adopting a known spec and then tightening it into a variant is a defect, not a shortcut;
the detection the house rule protects is present, in the standard's place rather than ours.

**No `Vary: Prefer`.** RFC 7240 suggests it where a response varies by the header, and this one does — but
every generated response already carries `Cache-Control: no-store`, so no cache may store the representation
and a `Vary` has no addressee.

**A gap worth naming: the count is the client's opt-in, and the operator has no say (#179).** `MaxPageSize`
is the operator's control over the sibling concern — "an unbounded `limit` is a denial of service one query
long" — and it bounds the *rows* a request returns, not the work a `COUNT(*)` does. So any caller authorized
to `list` can roughly double the cost of every list request, and keep doing it on every page of a deep walk.
This is availability only: the count is composed over the caller's own policy-filtered set, so nothing
crosses a boundary. It is stated rather than fixed because the answer is a host-facing option (refuse the
preference, or degrade past a row threshold), and inventing one before an operator has asked for a shape
would be guessing at the shape.

## Optimistic concurrency: a strong `ETag` over the row version

`ETag` is `"<updated_at.UtcTicks>"` — a **strong** tag, minted from the stored row version
(`AlvoManagedColumns.VersionColumn`, i.e. the `audit: true` `updated_at` column). `If-Match` carries it
back into `AlvoPrecondition`, which the port evaluates **inside the write transaction** against its
row-locked pre-image.

**Strong, because RFC 9110 §13.1.1 compares `If-Match` with the strong comparison function** — a weak tag
would never match and the header would silently protect nothing.

**The cost, stated:** two callers whose policies mask different fields share one tag for one row version
even though their representations differ. That is tolerable only because these responses are private and
uncacheable by design — every generated endpoint sets `Cache-Control: no-store` — and the tag exists for
optimistic concurrency, not for a shared cache. Alvo pays an uncacheable response to get a comparable tag.

The tag encodes `UtcTicks` rather than a rendered timestamp because `AlvoPrecondition`'s comparison *is*
`DateTimeOffset` equality, and a rendered `"O"` timestamp would have to be re-parsed — a parse is a place
precision or an offset can move. It is always encoded from a value that came out of the database, never
from a clock: PostgreSQL keeps microseconds and SQLite keeps text, so a tag minted in memory would not
survive its own round trip.

An entity with no version column gets **no `ETag` at all**, rather than one that cannot be compared. A
`304` is therefore reachable only on read-one of an audited entity.

**Every precondition this API cannot evaluate is refused, never ignored** — ignoring one is exactly the
lost update `If-Match` exists to prevent, and the caller would read the `200` as proof it did not happen.
So: a multi-tag `If-Match` is 412 (the port carries one version, so the RFC's *any-of* disjunction cannot
be expressed); a weak tag is 412; `If-None-Match` on a **write** is 412; any precondition header on a
**create** is 412; and an `If-Match` naming a version on an entity with **no version column** is 412, since
there is nothing stored to compare it against. `*` yields no precondition, since it asks only that the row
exist, which every write already answers — so `If-Match: *` is accepted even on a version-less entity, and
is the one precondition that is.

Because a version-less write can never answer a named version, the generated document does **not** offer
`If-Match` as a parameter on that entity's update and delete at all: a parameter is an invitation to send a
value, and there is no value to send. That was a real defect until late in this PR — the document advertised
the header on every entity, so a client of a non-audited entity was being instructed into a permanent 412.

**Labelled deviation from RFC 9110 §13.1.2:** a non-matching `If-None-Match` on a write should simply
succeed, and only a matching one answer 412. Alvo refuses the header outright, matching or not, because it
cannot evaluate the negative form at all — so "it did not match" is not something this API knows, and a
conforming success would be indistinguishable from a precondition that was never checked.

On a **read**, `If-None-Match` is honoured (weak comparison, per the RFC) and `If-Match` is ignored. The
honest reason for the asymmetry is not cost — honouring it would be about three lines — but that it is
*pointless*: answering 412 instead of a body saves a caller nothing they cannot get by comparing the tag
they were sent. An unhonoured header on a read costs a body the caller said they already had; on a write it
costs somebody their change.

## `Idempotency-Key`: what is stored, and where it is honoured

Sent on **create**. The record's shape:

```sql
CREATE TABLE IF NOT EXISTS alvo_idempotency (
    idempotency_key TEXT NOT NULL,
    scope           TEXT NOT NULL,
    fingerprint     TEXT NOT NULL,
    row_id          TEXT NOT NULL,
    created_at      TEXT NOT NULL,
    PRIMARY KEY (idempotency_key, scope)
)
```

Three decisions in that table:

- **It stores the created row's `id`, never a rendered response.** On replay the row is **re-read through
  the caller's *current* `get` policy**, so a replay can never hand back a representation the policy would
  not produce today: a field that has since become `hidden` for them stays hidden, and a row they can no
  longer see is not resurrected from a cache. Storing the body would have made the idempotency table a
  policy bypass with a timestamp on it.
- **The scope is part of the primary key**, and is `tenant/user` (`AlvoIdempotency.IdentityOf`), so one
  caller's key can never reach another's record. It follows that an **anonymous caller cannot hold a key at
  all** — every anonymous caller carries the same reserved all-zero user id, so there is no identity to
  scope by.
- **The primary key is the concurrency control.** Two concurrent creates on one key race the record insert,
  and the loser retries into a replay.

Nothing prunes this table yet: growth is bounded by the writes its caller may already perform, but a
retention window and a sweep are **#115**.

A reused key with a **different** fingerprint is `409 idempotency-conflict`, refused before the row is
read: it is not a replay, and answering with the first request's row would report success for a create that
never happened.

### An anonymous caller sending `Idempotency-Key` gets 422, not 401

Nothing failed authentication — **no credential was presented and rejected**. A 401 would owe a
`WWW-Authenticate` challenge (RFC 7235 §3.1) for a request that never attempted to authenticate, and would
blur the anonymous-versus-unusable-credential line the auth filter keeps disjoint.

What the caller sent is a well-formed request asking for a facility that requires a stable identity to
scope by. That is the port's malformed-request family, and this layer's 422.

### A replay by a caller who may `create` but not `get` answers 201, not 403

Landed in 7g. When the replaying caller's `get` is denied **outright** — no policy allows it at all, so the
decision is a denial before any row is touched — the retry must not be worse than the create it replays. It
answers **`201` with the original `Location` and a body carrying only `id`**, taken from the record's own
`row_id` with **no row read performed**.

The safety argument rests on the scope above: the record is keyed on the key, the tenant and the acting
user, so a match **proves this caller created that row**. The id disclosed is exactly the id their own
original `201` already gave them, in the body and in `Location`, and nothing more is disclosed because no
field of the row is ever read. Note that an id-only record carries no version, so this one response has no
`ETag`.

**The sibling case is deliberately out of scope.** When `get` *is* configured but its own predicate
excludes the row — `USING (status == 'published')`, say — or the row has since been deleted, the replay
answers **404**, exactly as any other read of an unreachable row does. Telling "invisible to me" from
"genuinely gone since" would need a second, policy-free existence probe, and refusing to add one is the
more conservative of the two errors. Tracked in **#101**.

### `Idempotency-Key` is *ignored* on `PATCH` and `DELETE` — and that label must not overstate

It is accepted and does nothing. Neither operation lists it as a parameter in the OpenAPI document, because
a parameter is an invitation to send something; the prose says it is ignored.

**The row's end state is unaffected. The outcome the client observes is not** — and an earlier version of
this note said an ignored key "costs nothing" there, which is wrong in a way worth correcting precisely,
because the difference *is* the retry story:

> A caller sends `PATCH … If-Match: "v1"`. **The 200 is lost** — a dropped connection, a timeout — and they
> retry the identical request. The write landed, so the row is at `v2`, so the retry is **412 — and the
> caller cannot attribute it**: "my own write landed" and "somebody else changed the row" are the same
> answer. The usual resolution for a 412 is to re-read, re-merge and re-apply, which in the second case
> clobbers a genuinely concurrent change. A key would have answered "this is your own write, here is its
> result". `DELETE` has the same shape (404 or 412, indistinguishable from someone else's delete).

That is precisely §2.1's reason for wanting idempotency keys, so it is **a real hole in the retry story,
not a nicety**. It is ignored rather than refused because refusing would break the widespread client habit
— Stripe's SDKs among them — of attaching the header to every mutating request, and would refuse requests
that are perfectly serviceable. Honouring it needs a third widening of `IAlvoData` plus a stored
*replayable result* for an update. Tracked in **#102**.

## A 201's `Location` carries the request's path base (#121)

A `Location` is the request's `PathBase` plus **the matched endpoint's own route pattern**, written in one
place (`RecordResult` / `CreatedRow`) — which is also the only place any `Location` is written, so the
ordinary 201 and the idempotent replay (deviation 30) are fixed by the same line. Three request-time facts
the mapped literal does not carry, and all three used to be lost:

- **`app.UsePathBase("/alvo")`**, and a proxy that sets `X-Forwarded-Prefix` for a host told to trust it.
  Both land in `Request.PathBase`, which is prefixed here.
- **`app.MapGroup("/backend").MapAlvoDataApi()`**, a supported mount whose prefix belongs to the *route*
  rather than to the request. A grouped endpoint's `RoutePattern.RawText` is the combined pattern, so reading
  the collection path off `HttpContext.GetEndpoint()` is reading it from the router — there is no second
  place for the literal and the route to disagree. Not `LinkGenerator`: generating by name would mean naming
  all five routes per entity, and route names are process-global, so two `MapAlvoDataApi()` calls under two
  groups — the very shape this fixes — would collide at startup.
- **Encoding.** The header is `ToUriComponent()`, not `PathString.Value`. `Value` is decoded, and over
  Kestrel a non-ASCII path base (`/účty`) then throws while the response header is encoded as Latin-1 — a
  500 on a create whose row is already committed.

Where the 404 happens is worth stating, because it shapes what a test of this can claim. Under a path base,
in-process the host answers both URLs: `UsePathBase` *strips* a prefix when the request carries one rather
than requiring one, so following a prefix-less `Location` against the host itself still reaches the row. The
failure is at the edge, which the host never sees. `PathBaseTests` therefore pins the header **whole** rather
than by prefix, and `AlvoHostPathBaseTests` follows the forwarded-prefix case through a model of the proxy
that produced it. A **route group** is the harder failure and needs none of that care: it only lengthens the
route, so the unprefixed URL is mapped by nothing and a wrong header 404s in-process.

The **OpenAPI document's path keys keep their mapped shape, and the document names the origin they are
resolved against.** `Microsoft.AspNetCore.OpenApi` builds `servers[0].url` from the request's `Scheme`, `Host`
and **`PathBase`**, per request — measured, including the part that made #130 look unfixable: asking for the
document with a path base and then without it, in either order, returns the right origin each time, so nothing
is frozen by a first request. Alvo's transformer never touches `Servers`. Under `app.UsePathBase("/alvo")` the
origin is `http://localhost/alvo` and the keys stay `/api/owners`; under
`app.MapGroup("/backend").MapAlvoDataApi()` the origin stays bare and the prefix is in the key, because a group
prefix belongs to the *route*. `OpenApiServersTests` pins both, and `AlvoHostPathBaseTests` pins the
forwarded-prefix leg through a model of the proxy — which is where the 404 an unprefixed origin produces
actually happens.

**#130 closed with no production change.** What it was missing was any fact at all: the origin's scheme and
host halves were pinned by `AlvoHostForwardedOriginTests`, its path-base half by nothing, so removing `PathBase`
from the framework's own server-URL construction would have left the suite green while every path in the
document became wrong by the prefix. Those facts now also gate a bump of `Microsoft.AspNetCore.OpenApi`, which
is a virtue worth stating rather than a surprise worth discovering.

The docs UI's own document fetch under a path base is a separate question and stays **#134**.

## The status and `type`-slug catalogue

Problem documents are RFC 9457, media type `application/problem+json`, with an Alvo `violations` array.
Every `type` is `https://alvo.dev/errors/<slug>`; the slugs are exactly `AlvoProblemTypes.All`, and the
table below is that list. Two of them — `unreadable-request` and `internal` — are emitted only by
`AlvoExceptionHandler`, so only a host that called `AddAlvoProblemDetails()` can produce one, which is
why neither is documented on any operation.

| Status | Slug | Means |
|---|---|---|
| 200 | — | the row, the page, or the row as it now stands |
| 201 | — | created; `Location` (see below) and the row |
| 204 | — | deleted, no body |
| 304 | — | `If-None-Match` covers the current version (read-one of an audited entity only) |
| 401 | `unauthenticated` | a credential **was** presented and cannot be used |
| 403 | `forbidden` | a policy refused the operation — one slug for every policy refusal |
| 403 | `out-of-scope` | the presented key's scopes do not cover this entity and operation |
| 404 | `not-found` | the row is absent **or** the caller's policy excludes it, indistinguishably |
| 409 | `idempotency-conflict` | the key was reused for a different request |
| 409 | `conflict` | a constraint the database enforces refused the write — a `unique` value another record holds, or a `restrict`-ed reference |
| 412 | `precondition-failed` | a precondition this API cannot evaluate, or a version that does not match |
| 422 | `validation` | schema-derived validation refused the body |
| 422 | `malformed-query` | the query string or the body is malformed — the shape is wrong, nothing is hidden |
| 413, 408, 400 | `unreadable-request` | the **web server** refused the request before Alvo read it (a body over `MaxRequestBodySize`, one arriving too slowly, one whose framing broke) — same opt-in as `internal`, and likewise documented on no operation |
| 500 | `internal` | an invariant Alvo relies on is broken — **only** in a host that called `AddAlvoProblemDetails()`; no endpoint produces it and no operation documents it |

**Two 409s, and the split is the same rule `forbidden`/`out-of-scope` follows.** Both mean "the request
conflicts with what is already stored", and they have different fixes a caller can act on: an
`idempotency-conflict` is repaired with a fresh key and the same body, a `conflict` with a different value
(or by removing what stands in the way). One slug covers *both kinds* of constraint conflict — a `unique`
collision and a `restrict`-ed reference — because a slug keys on the refusal's kind and "your write collides
with stored state" is one kind; which constraint and which field is per-violation detail, carried in
`violations` with its own stable `code` (`unique`, `referenced`). OpenAPI keys a response by status, so the
document describes both under one `conflict` response and the problem `type` tells them apart.

`conflict` is listed on **create, update and delete** — unconditionally per operation, not narrowed per
entity. That is a deliberate imprecision, and it is worth naming: whether a *delete* can conflict depends on
whether some **other** entity references this one with `onDelete: "restrict"`, which is a property of the
whole applied schema, and `DataApiDocumentation.ResponsesFor` is handed one `EntitySchema`. Narrowing it
would mean threading the model through three layers (the endpoint metadata, the header catalogue and the
document transformer) for a documentation nicety; the cost of not doing it is that an entity nothing
references advertises a 409 on delete it cannot reach.

Which operation can answer what is one table, `DataApiDocumentation.ResponsesFor`, read both by the
endpoint metadata and by the OpenAPI transformer — so the document cannot advertise a status no delegate
produces. **401 and 403 are unconditional on every route**, because the same gate is attached to all five.

### How a database constraint violation reaches the caller (#138, fixed)

Every declared facet the framework can check itself — `required`, `maxLength`, `enum`, `format`,
`precision`/`scale`, `ref` existence — is validated by `RecordValidator` and answered with a per-field 422.
Two cannot be checked before the write, because only the engine knows: a `unique` value another record
already holds, and a `restrict`-ed reference. Both used to reach the host as the provider's own exception
and render as `500 internal` — *"an invariant Alvo itself relies on is broken"*, which neither is. Both are
now `409 conflict`:

| What the caller did | Answered | `violations` |
|---|---|---|
| Supplied a value another record holds on a `unique` field | `409 conflict` | one per field, pointer `/<field>`, code `unique` |
| Deleted a record a `ref` with `onDelete: restrict` still points at | `409 conflict` | one, pointer `""`, code `referenced` |

**Three costs the misclassification had, each a separate defect.** An agent could not repair the request —
no pointer, no field, no fix suggestion, in a framework whose principle 4 is structured errors *with* one. A
500 invites a retry that can never succeed. And the operator was paged, with a stack trace, for an ordinary
caller mistake; `AlvoHostProblemDetailsTests` now asserts that a duplicate logs **no** `Error` entry.

**Where the engine-specific part lives, and why it is not in a `catch`.** The kind is recovered by
`IAlvoSqlDialect.DecodeConstraintViolation`, one member per driver: PostgreSQL reads SQLSTATE `23505`/`23503`
and reports the violated constraint's *name*; SQLite reads the extended result code `2067`/`1555`/`787` and
names the *columns*, in its message and nowhere else — and names nothing at all for a foreign key. The
shared data path (`ConstraintViolationTranslator`) resolves either against the entity's own model and never
looks at a message, because a message match in the shared layer is a dependency on one provider's prose that
a caller-supplied value can end up quoted in. The member is **abstract**, not a default interface member:
`null` means "not a constraint violation", which is legitimate for every other failure, so an inherited
default would have a new driver silently answer 500 for every duplicate.

Two engine differences were **measured, not assumed** (#139), and both are absorbed behind that seam:
`Microsoft.Data.Sqlite` reports the extended result code only when the connection handle still agrees with
the return code, so `ExecuteDelete`'s foreign-key failure arrives as bare `SQLITE_CONSTRAINT` while
`SaveChanges`' unique failure carries `SQLITE_CONSTRAINT_UNIQUE`; and Npgsql withholds the `Detail` line
that would carry the columns unless `Include Error Detail` is on, which it should not be, because that line
quotes the caller's values. Both drivers are held to `AlvoDataConstraintTests`, inherited unchanged.

**What the refusal does not say.** Never the value the caller sent, never the engine's constraint or index
name, never its message. A `restrict` refusal names **no entity either**: which of the entities that may
reference this row actually holds one is a fact about data the caller may have no read access to, so the
refusal says only that some record still references it. A conflict confined to framework-managed columns
(`id`, `tenant_id`) is *not* translated — a caller cannot change one — and keeps propagating as the broken
invariant it is.

**A duplicate no longer costs ten transactions.** `EfAlvoData.ReplayableCreateAsync` retries any storage
write failure, so a duplicate in an idempotent create used to be re-attempted ten times before surfacing.
`AlvoConstraintViolationException` is not a `DbException`, so it leaves on the first attempt; the idempotency
record's own primary key is deliberately **not** translated, because losing that race is what the retry
exists to converge on. That closed **#127**'s stated defect; the attempt count is now asserted rather than
described, and the paths that legitimately still retry are set out under *the five failure families* below.

### A `unique` field on a tenant-scoped entity was a cross-tenant existence oracle (#137, fixed)

**A separate defect that shared a trigger with the one above, and the more serious of the two.**
`DescriptorModelBuilder` emitted `HasIndex(field.Name).IsUnique()` with no `tenant_id`, regardless of
`tenancy: "scoped"` — and the same for a *declared* unique index — so a `unique` field was unique across the
whole instance rather than within a tenant. Tenant B's create collided with a value only tenant A held, and
B learned that A held it, one request per candidate. That is precisely the inference the 404-everywhere rule
above is built to prevent, and it conflicted with §0's secure-by-default.

**Fixing the status did not fix this, which is why the two were separate issues.** `409`-versus-`201` is the
same one-bit signal to tenant B as `500`-versus-`201` was. The fix is a **tenant-scoped unique index** —
`(tenant_id, field)` on a scoped entity, unchanged on a non-scoped one — emitted *after* the field loop,
because `DescriptorToSchemaMapper` appends `tenant_id` after the declared fields and EF refuses to resolve it
mid-loop (measured: *"The property 'tenant_id' cannot be added … no property type was specified"* — a startup
failure, not a wrong index). A non-unique index enforces nothing and is left alone; a descriptor that already
named `tenant_id` keeps its own column order.

Three directions are pinned, because a fix satisfying one alone would be wrong: two tenants may hold one
value, one tenant still may not, and a non-scoped entity keeps instance-wide uniqueness. The first two are
proved against **both engines** by `AlvoDataConstraintTests`; the index shape itself by
`DescriptorModelBuilderTests`; and the HTTP-level indistinguishability by
`test/teapie-field-service/080-Tenancy/002`, which now asserts the two probes are **equal** where it used to
assert they differed.

**What this means for a database that already exists.** The emitted DDL changed: `IX_<table>_<field>` becomes
`IX_<table>_tenant_id_<field>` on a scoped entity, so the next apply drops one index and creates another —
`SchemaChangeKind.DropIndex`/`AddIndex`, neither classified destructive, so no `AllowDestructive` is needed.
It can still *fail*, and legitimately: if two tenants already hold one value the old instance-wide index
would have refused, nothing is affected — but an instance that has been running with the narrow index cannot
acquire rows the wide one forbids, so the create will succeed where it used to fail. The migration is
therefore always in the widening direction and no existing row can violate the new index. Nothing is
released, which is exactly why now was the cheap moment to change it.

**A slug keys on the refusal's *kind*, never on its *reason*.** RFC 9457 §3.1.1 makes `type` the
classification a client may branch on and `detail` prose that "ought not be parsed". A slug encoding *why*
policy refused would become the schema-and-data oracle every deny reason in the framework is worded to
avoid — `IPolicyEngine`'s reasons are deliberately free of the entity, the row, and whether it exists, and
a parseable classification beside them would hand back what the prose withholds.

**The endpoint layer still never catches a 500, and the slug for one is opt-in.** `IAlvoData`'s
`InvalidOperationException` family — an invariant the implementation itself relies on is broken — is never
caught here: swallowing it into a hand-made problem document would lose the stack trace the host's own
logging exists to record. It propagates, and what happens next is the *host's* decision, which is what #119
turned out to be about.

| Mode | What the host registers | What a 500 carries |
|---|---|---|
| embedded, declined (the default) | nothing — `AddAlvo` does **not** register the handler | whatever the host renders; Alvo writes no bytes and the exception reaches the host's own logging |
| standalone, or embedded opted in | `AddAlvoProblemDetails()` + `app.UseExceptionHandler()` | `type: alvo.dev/errors/internal`, `application/problem+json`, and a **constant** `detail` |

`AddAlvoProblemDetails()` registers `AlvoExceptionHandler`, which does both halves of #119's wording: it
logs the exception (as an exception, so the stack trace survives) and *then* renders
`ProblemResultFactory.Internal()`. The `detail` is a constant — not the exception's type, not its message,
not a frame — because a caller can act on none of it and an attacker can act on all of it. It is
deliberately **not** part of `AddAlvo`: an embedded host owns its own error rendering, and Alvo stealing the
exception is the defect #119 was filed to prevent, not the one it was filed to fix.

**The handler answers for Alvo's generated endpoints and declines everything else**, and both directions are
the same principle as the opt-in itself. It reads `DataApiOperationMetadata` off
`IExceptionHandlerFeature.Endpoint` — the endpoint the middleware captured before it cleared the
`HttpContext` — so:

| What failed | What Alvo does |
|---|---|
| family 5 on a generated route | logs `Error` with the stack trace, answers `500 internal` |
| `BadHttpRequestException` on a generated route | logs `Warning` **without** the exception, answers `unreadable-request` at the exception's own `StatusCode` |
| anything on the host's own route, or before routing matched | returns `false` and writes nothing |

The declining half is what keeps an `IExceptionHandler` the host registers *after* `AddAlvoProblemDetails()`
alive: the framework stops at the first handler that claims a failure, so a handler claiming all of them
deleted the host's error contract from its own 500s. The `BadHttpRequestException` half is the other side of
the same mistake in the other direction — a caller's oversized or truncated upload is not one of `IAlvoData`'s
five families, and answering it with `internal` told an agent that Alvo was broken and to retry a request
whose *size* is the thing that has to change, while paging an operator with a stack trace for a client-side
error.

The handler lives in the core rather than in `MMLib.Alvo.Host`, which #119's letter asked for, because
`ProblemResultFactory` is `internal`: a Host-side handler would be a second hand-written copy of the
problem-document shape (`type`, `title`, `status`, `detail`, `violations`, the media type). Recorded as
**deviation 36** in the F3 design's *Deviations added by PR4*.

**No operation documents a 500**, and that has not changed — `ResponsesFor` describes what a *delegate*
produces, and no delegate produces this one. The slug is in the published `problemDetails` schema's `type`
enum, because that enum is the catalogue a client branches on and `internal` is a value an Alvo pipeline can
really send.

**What an idempotent create still retries, now that the caller's own duplicate does not (#127).** A
**keyed** create whose row violates one of the *caller's own* unique constraints used to be
indistinguishable from the idempotency table's own insert race — which is what the retry exists to absorb —
so it burned ten full write transactions with a linear backoff (~450 ms) before surfacing. It no longer
does: the entity's insert goes through `ConstraintViolationTranslator`, the refusal is
`AlvoConstraintViolationException`, that is not a `DbException`, and `IsStorageWriteFailure` therefore does
not match it, so it leaves on the **first** attempt as a `409` naming the field. `SqliteIdempotentCreateFailureTests`
asserts the attempt *count*, not only the outcome — a build that retried ten times and then threw the same
exception passes every outcome assertion.

Two paths legitimately still cost up to ten attempts, and neither is a defect to be "fixed":

- The **idempotency record's own** primary-key failure is deliberately left untranslated. Losing that race
  is the entire reason the loop exists, and translating it would turn a converging race into a `409`.
- An **unrecognised** `DbException`/`DbUpdateException`. The dialect answers `null` when it does not
  recognise the code, when the constraint name matches no model index, or when the surviving columns are all
  framework-managed. That is the fail-safe direction: narrowing the catch far enough to stop this would let
  a genuine insert race escape as a 500.

So the amplification is a **per-dialect** property rather than something fixed once for every engine — a
dialect that honestly recognises nothing, as `TSqlSqlDialect` does, still burns all ten. The count is pinned
on SQLite; the PostgreSQL leg belongs to **#139**, which exists to demand constraint behaviour be verified
per engine.

## What a host may attach to the generated routes (#182)

`MapAlvoDataApi()` returns an **`IEndpointConventionBuilder`**, so a host attaches
`RequireRateLimiting`, an authorization policy, output caching or a telemetry tag to Alvo's generated
endpoints and to nothing else — the return type every other ASP.NET Core `Map*` over a *set* of endpoints
has. The conventions are applied in `DataApiEndpoints.Protect`, the same call that attaches the
authorization filter and the operation marker, so no generated route can be mapped without them, and they
are applied **last**, so a host's convention observes Alvo's own metadata.

Three properties of that seam are contract rather than implementation:

- **Conventions must be attached before the first request**, which is when the route table materialises.
  One attached after **throws**, naming the call to move. That is a deliberate deviation from the
  framework — which silently ignores late conventions — because Alvo's table is frozen once built and a
  dropped `RequireRateLimiting` is a rate limiter a host believes it has.
- **A convention that throws is its own diagnosis.** Conventions run while the endpoints are built, inside
  the data source's materialisation, where an `InvalidOperationException` already means "this applied
  schema cannot be routed". The consequence is identical and has to be — an exception escaping an
  `EndpointDataSource` enumeration takes down the composite every probe is matched through, liveness
  included — so a host's broken convention also ends in an empty table and readiness `Failed`, but its log
  record names `MapAlvoDataApi()` instead of blaming the descriptor.
- **`MapAlvo()` still returns the route builder, and `MapAlvoHealth()` is not chainable.** One convention
  builder over the probes *and* the Data API would let a host attach an authorization policy to
  `/health/live`, and a container probe presents no credential — that is a container killed and
  restart-looped by its own liveness gate. A host that wants conventions calls the parts.

What this does **not** claim is an authorization guarantee against host code. A convention receives the
`EndpointBuilder` and could clear its filter factories; so could `app.MapGroup("").MapAlvoDataApi()` plus
conventions on the group, which worked before this seam existed and is how the capability was measured, and
so could substituting `IPolicyEngine` in the host's own container. "A marked endpoint is a gated endpoint"
is a statement about *this framework's* construction. An embedded host owns its pipeline; treating its code
as an attacker is not this project's threat model.

It could nevertheless be made a *construction* guarantee again — an Alvo `Finally` convention that runs after
the host's and verifies its own filter factory survived — which would catch an *accidental* dismantling (a
convention that rebuilds `FilterFactories` rather than appending to it) without changing the threat model.
Whether that is worth the cost is **#184**, filed so the prose-only invariant is a recorded decision rather
than a caveat nobody weighed.

## Route generation happens at *enumeration* time — half of #103 is delivered

`MapAlvoDataApi` registers **one empty `AlvoEndpointDataSource`** and returns. `EntityRouteCatalog` reads
the applied schema from `ISchemaRegistry` on the **first enumeration** of that source — which is the first
request that builds the matcher, not `StartAsync` (design fact 1, measured). So the ordering obligation
"migrate before you map" is **gone**: the sequence is `register → map → boot primes → listen → first
request materialises the routes`, and a host that maps first no longer gets an empty API. An entity the
applied schema does not declare still has no route at all, so laziness costs nothing in the fail-closed
direction.

Three properties of that data source are requirements rather than implementation taste:

- **The endpoints are built through the real minimal-API `Map*` helpers** on a nested
  `IEndpointRouteBuilder`. Hand-assembled `RouteEndpointBuilder` endpoints route perfectly and are
  **invisible to ApiExplorer**, so the OpenAPI document empties while every routing fact stays green
  (design facts 4 and 5, measured — and pinned by
  `The_OpenApi_document_lists_every_mapped_entity_route`).
- **The table is built once and frozen**, and that is correctness, not economy: a source that rebuilt per
  enumeration would let the document — generated per request, enumerating afresh — advertise a
  runtime-applied entity that the matcher, cached behind an unfired change token, does not route.
- **`GetGroupedEndpoints` forwards to the nested sources** rather than using the base implementation,
  because `app.MapGroup(prefix).MapAlvoDataApi()` is supported and a created row's `Location` is read off
  the matched endpoint's combined pattern (#121).

**The reserved-query-key belt now fires at first enumeration, not at start** — a deliberate move, and the
one behavioural cost of laziness. Stage 0 of the boot still refuses a reserved field name coming from the
*descriptor*, at start, over the descriptor's mapped schema. The belt inside the data source guards the
different input: a host that **substituted `ISchemaRegistry`** and served entities Alvo never mapped from a
descriptor. Keeping a copy of the check at map time was considered and rejected as worse than nothing: at
map time the registry is the unprimed provider and returns an **empty** `SchemaModel`, so the check would
iterate zero entities and pass **vacuously in every real host** while still passing its own test, which
pre-registers a primed registry — a control no test can distinguish from a working one.

**What is left of #103, corrected.** The lazy half is delivered; the mutable half — invalidating so an
entity applied at *runtime* gains a route — is not, and `GetChangeToken` returns a token that never fires.
The remaining cost is **not** what this document used to claim:

| Resolution | Buys | Costs |
|---|---|---|
| A **mutable `EndpointDataSource`** with an `IChangeToken` | Every entity — physical or virtual — keeps a real route literal, and routing keeps answering "no such entity" before authorization | Rebuilding the endpoint table at runtime, a concurrency story for requests in flight across a rebuild, and — **measured, design fact 6** — the **OpenAPI document does not refresh**: routing and the document have independent caches, and invalidating the data source refreshes only routing. Document-cache invalidation is the real remaining work, and nothing has designed it |
| A **catch-all route for virtual entities only** | No endpoint-table surgery; virtual entities are late-bound by nature | Turns a routing question into a port question for that subset, and a catch-all cannot enumerate paths for the document. Correctly **refused for physical entities**, where a literal exists |

This document previously stated that resolution A "keeps the OpenAPI document able to list exactly what is
mapped". **That was measured false**: after `Invalidate()` a new entity routes (200) and is still absent
from the document. Corrected here rather than in the issue alone, because it was the reason A looked cheap.
Also, when the mutable half lands, the new change token must be published **before** the old one is
cancelled — the reverse order re-enters the invalidation and overflows the stack (aspnetcore#44392).

Still tracked in **#103**, narrowed to the runtime-apply case that F7's *evidencie* land on.

### `AddDataApi()` is configuration, and the Data API is registered by `AddAlvo`

`AddAlvo` has always called `AddAlvoApi()`, so the Data API's services were never opt-in. `AddDataApi` is
therefore **configuration only** — it contributes an `AlvoApiOptions` configure action and registers
nothing — additive, idempotent, and order-insensitive against `AddAlvo`. Nothing is reachable over HTTP
until `MapAlvoDataApi()` (or `MapAlvo()`) is called, which is a separate seam by design
(`extensibility.md` rule 10). Recorded because the startup-lifecycle design asked the maintainer to ratify
a breaking change here and there was none to ratify: the premise was measured false and the deviation
(56) is **withdrawn**.

## What the apply step refuses, and the line that was drawn

**The rule: refuse what silently produces wrong data; do not refuse a subsystem whose absence is
observable.** An ignored `default` stores NULL where a value was expected and nobody can see it from
outside; a webhook that never fires is a webhook that never fires.

**Refused at apply** (`Descriptor.Internal.UnhonouredFeatures`, plus `ManagedColumnNames`), each with the
consequence and the fix named in the error:

| Refusal | Why ignoring it is silent wrongness |
|---|---|
| `field.computed` | the expression is never evaluated, so the column stays null |
| `field.rollup` | nothing maintains the aggregate, so it reads as permanently null while looking like data |
| `field.validation` | the expression is not evaluated, so a value it forbids is accepted — the field is not constrained at all |
| `field.default` (**#113**) | no column default is emitted and the value is dropped, so the field is null — and on a `required` field that is an INSERT of NULL into a NOT NULL column |
| `entity.softDelete` | a delete would remove the row outright and reads would not exclude it — irrecoverable data loss where the schema promises recoverability |
| the three `before*` `entity.hooks.*` points, refused **one per point** (**#114**) | a `before*` hook may reject or mutate in the write transaction, so a write the author believes is vetted is neither |
| a raw **JSONata** expression in any `$defs/jsonata` action slot (**#149**) | the action still runs, with Alvo's canonical envelope instead of the declared transform — a delivery that succeeded carrying data the author did not declare |
| a template's **`bodyFile`**, per referencing after-hook | nothing reads a path inside a descriptor bundle, so the mail would go out with an empty body rather than fail |
| an **`entity.update`** / **`function`** / **`http.call`** action on an after-hook | each loses something different, so each names its own consequence |
| **declaring a framework-managed column name at all** | see below |

Hooks are refused per point precisely so PR5 could delete one entry per point it implements, rather than
facing an all-or-nothing switch — **and it has.** PR5a compiles `afterCreate`/`afterUpdate`/`afterDelete`
into the policy catalog and dispatches them from the outbox, so those three entries are gone and the three
`before*` points stay (a before-hook runs *in the write transaction*, and nothing in this build does). No
author of a `before*` hook saw a changed message, which is what "each one is lifted the day it starts
working" was written to buy. The refusals PR5a *added* are in the same table above, and the subsystem's own
record is [`events.md`](./events.md).

**Warned about, not refused** — one line at apply naming each block it finds
(`Descriptor.Internal.UnhonouredSubsystems`): `dynamicEntities`, `automation`, `templates`, `webhooks`,
`functions` — one issue each, and `webhooks` earned a new one (**#120**) because nothing covered it.
**`templates` and `webhooks` are now *partially* honoured, and the wording carries that rather than the
entry leaving:** an after-hook does render a template and does post to a declared endpoint, so "nothing
renders a template" and "no event is ever delivered" stopped being true — but both blocks are still dead
from `automation`, which is where most descriptors reference them, and a delivery that happens is
**unsigned** (`secretRef` unread, no Standard Webhooks HMAC header) and unprojected (**#152**). Deleting
either entry would have been the larger lie.
`branding` and `access` are parsed and consumed by nothing either, but both describe an
admin-dashboard surface that does not exist in this build, so there is no place their absence could be
observed yet.

`entity.realtime` is unhonoured too and is **deliberately not in that warning**: the schema declares it per
entity with a default of `true`, so it is unhonoured for *every* entity of *every* descriptor. Warning only
on an explicit `realtime: true` would stay silent for the entities equally affected; warning on all of them
would fire on every descriptor ever applied. Recorded on **#38**.

### Declaring a framework-managed column is refused, and it costs one capability

The framework owns seven names on the entities whose traits carry them — `id`, `tenant_id`, `created_at`,
`created_by`, `updated_at`, `updated_by`, `deleted_at` — and the refusal is **trait-scoped**: an entity that
does not declare `audit` may still have an ordinary field called `created_at`.

The rule is refuse-the-declaration-whatever-attributes-it-carries, because two measured defects came out of
letting a declaration win:

- An audited entity declaring `updated_at` as `{"type":"string"}` passed apply and then **every create
  answered 422 with an internal `(Parameter 'value')` in the body** — the audit stamp writes a
  `DateTimeOffset` into a column the schema says is text.
- The same entity declaring `updated_at` with `hidden` passed apply and silently switched **optimistic
  concurrency off**: the mask drops the key from every returned record, so no `ETag` is minted and the
  caller has nothing to send as `If-Match`. Nothing raised anywhere.

A narrower rule — refuse only `hidden` on a managed column — was tried first, and closed the second defect
while leaving the first, which is worse.

**The capability this removes, named because an author will hit it:** `readOnly` on `tenant_id` as a
narrowing is now forbidden along with the declaration. That intent belongs in a **policy rule** rather than
a field flag — the synthesized tenant scope's `WITH CHECK` is already evaluated over the candidate row, so
a `create` rule is where "which tenant may this row be placed in" belongs, and is the only place that can
answer it per caller.

## Three records that still need a human's eye

1. **A `hidden` field appears in a request schema when it is `required` and the caller may write it** —
   deviation **28**, and the ruling this PR most wants made. It trades a confidentiality property this same
   document asserts against documenting a create a caller can satisfy, and both alternatives foreclose
   something real, which is what makes it a product position rather than an implementation detail.
   **The fact a maintainer needs in order to rule:** what the document publishes is the field's **declared**
   `hidden` flag, not a per-caller mask — so a name's exposure depends on how the host serves the document,
   and in the common Scalar setup that is unauthenticated. Sibling hole: `required + hidden + readOnly` is
   legal and no create can satisfy it (**#124**).
2. **The two collation decisions — from PR2**, and the one of the two that lives in
   `docs/architecture/data-path.md` ("Collation belongs to the host — two rulings that need the maintainer's
   sign-off"). Repeated here so a reader finds them without editing that file. Still awaiting sign-off.
3. **Ordering narrowed to String/Int/Decimal/Timestamp — from this PR, not PR2**, and it lives in
   `FilterOperators.cs`'s `_orderable` set plus `FilterTermParser.IsApplicable`, **not** in `data-path.md`.
   Issue **#95**, and the expiry note above. #95 also covers `like`/`ilike` refused on a `json` field, for
   the same underlying reason.

## Alternatives rejected

- **A `Link: rel="next"` header duplicating `next`.** A cursor would have two homes, and an agent reading a
  JSON body would have to parse HTTP headers to keep paging. Deliberately not shipped so `next` has exactly
  one home; if a consumer asks, **#104**.
- **A bare array plus `Content-Range`** (PostgREST's own shape), for the same reason.
- **A `{entity}` catch-all route.** It would map a route for an entity the descriptor does not declare and
  answer it from the store — turning a routing question into a port question, and leaving the OpenAPI
  document unable to list real paths. With literals, "this entity does not exist" is a 404 routing produces
  before anything is resolved.
- **`PUT`, and PUT-as-upsert.** `UpdateAsync` is partial by contract — a field the dictionary does not
  mention keeps its stored value — so `PUT` would advertise whole-resource replacement the port does not
  perform, and upsert needs a port that can create-or-replace. `PATCH` only; upsert is **#105**.
- **Storing the response body in the idempotency record.** See above: a stored body would replay a
  representation the caller's policy would no longer produce.
- **A slug for the 500**, and **a slug encoding why policy refused**. Both above.
- **Refusing a write to a `hidden` field**, and **refusing `required` + `hidden` at apply.** A mandatory
  secret — a password, an API token the caller supplies and can never read back — is exactly
  `required: true` + `hidden: true`, and the frozen schema defines `hidden` as response-side.
