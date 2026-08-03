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
- `order=<field>[.asc|.desc][.nullsfirst|.nullslast][,…]`, `select=a,b`, `limit`, `offset`, `after`.

`select` is applied to the **response**, not to the `SELECT` list — the port has no projection member yet,
so `?select=id` costs the database exactly what a full read costs (**#117**).

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

One consequence worth knowing: a nullable field cannot be a sort key on a *paged* read, and every HTTP
list is paged — so `?order=<any nullable field>` is refused outright, and `nullsfirst`/`nullslast` parse
but are currently **unobservable**. Both are issue **#116**, and it will be hit on day one.

## Paging: keyset over an opaque cursor, and its real cost

The response is a JSON envelope, always both members:

```json
{ "items": [ … ], "next": "3q2-796tvE-cKTMlvKYbGw" }
```

`next` is `null` on the last page rather than omitted, which is why the published schema marks both
`required` — a statement about the bytes, not an aspiration.

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

The **OpenAPI document's path keys still have the original shape** — a document served under a path base
declares no `servers` entry, so a client resolving its paths against `/` is wrong by the same prefix. That is
deliberately not fixed here: `OpenApiDocumentTransformerContext` carries no `HttpContext` and the document is
cached per document name, so a request-derived `servers` entry is a decision about whether Alvo's document is
per-request at all. Filed as **#130**.

## The status and `type`-slug catalogue

Problem documents are RFC 9457, media type `application/problem+json`, with an Alvo `violations` array.
Every `type` is `https://alvo.dev/errors/<slug>`; the nine slugs are `AlvoProblemTypes.All`.

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
exists to converge on. That is the part of **#127** this happens to close; the rest of #127 is still open.

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

**One 500 *is* caller-reachable, and it costs ten write transactions to get there.** A **keyed** create
whose row violates one of the *caller's own* unique constraints is retried by
`EfAlvoData.ReplayableCreateAsync` — it cannot distinguish that violation from the idempotency table's own
insert race, which is exactly what the retry exists to absorb — so ten full write transactions run with a
linear backoff (~450 ms total) before the exception surfaces as the family-5 500 above. Not a regression:
an *unkeyed* create with the same violation also answers 500, just immediately. Worth knowing because it is
a caller-triggerable amplification of a caller's own mistake, and because the fix (asking the dialect
whether a constraint name is Alvo's own) belongs with the retry logic rather than here. Tracked in **#127**.

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
| the six `entity.hooks.*` points, refused **one per point** (**#114**) | a `before*` hook may reject or mutate in the write transaction, so a write the author believes is vetted is neither; an `after*` effect simply never happens |
| **declaring a framework-managed column name at all** | see below |

Hooks are refused per point precisely so PR5 can delete one entry per point it implements, rather than
facing an all-or-nothing switch.

**Warned about, not refused** — one line at apply naming each block it finds
(`Descriptor.Internal.UnhonouredSubsystems`): `dynamicEntities`, `automation`, `templates`, `webhooks`,
`functions` — one issue each, and `webhooks` earned a new one (**#120**) because nothing covered it.
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
