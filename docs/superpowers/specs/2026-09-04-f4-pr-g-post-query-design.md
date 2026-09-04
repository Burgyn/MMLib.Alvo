# PR-G — `POST {prefix}/{entity}/query`, for a filter the URL cannot carry

Closes **#107**.

One new route per entity, gated as `list`, answering the same page envelope from the same
parser. Nothing about the port changes and nothing about the grammar changes; what changes
is which side of the request the parameters arrive on — and, because the transport stops
bounding four caller-supplied lengths, the budgets the URL was silently providing become
explicit (§2.5, §2.6).

## 0. The problem, restated from the source rather than from the issue title

`docs/product/baas-analyza.md` §2.1 "pozor na" names it in one line:

> **Dlhé URL filtre** — komplexné filtre presiahnú URL limity; treba aj POST-based query
> endpoint (`/query` s JSON telom).

The failure is not that Alvo refuses a large filter. Alvo's own budgets are deliberately
generous — 256 filter terms, 1000 `in` candidates, a 512-character cursor — and the
transport gives out first. A single `?id=in.(…)` near its limit is 1000 UUIDs and about
37 KB; a request line commonly dies at ~8 KB, at a proxy Alvo does not control. So the
caller gets a **414 with no `violations` array and no fix suggestion** — the one refusal
shape §0 principle 4 exists to eliminate — for a request Alvo would have served.

"Fetch these 400 rows by id" is the ordinary shape of that request, not an adversarial one.

## 1. The one constraint that decides the whole design

Issue #107's first constraint is the design:

> **It must be the same parser.** A second grammar for the body is how the two come to
> disagree; the body carries the same query string, or a JSON shape that maps onto
> `AlvoQuery` through the one existing parser.

`QueryStringParser.TryParse` takes an `IQueryCollection` — a multi-valued string map. So
"the same parser" is achievable literally, not approximately, provided the body can be
turned into that map without interpreting anything.

**The body is a JSON object whose members are the query-string parameters.** A member's
name is a query key; its value is the same `<operator>.<operand>` text the query string
would have carried; an array is a repeated parameter.

```http
POST /api/vehicles/query
Content-Type: application/json

{
  "id":     "in.(3f2a…,7c11…, … 400 more …)",
  "year":   "gte.2020",
  "or":     ["(color.eq.red,color.eq.blue)"],
  "select": "id,label:make,year",
  "order":  "year.desc.nullslast",
  "limit":  100
}
```

### 1.1 What "the same" means precisely, because it is not "the same bytes"

The body carries **decoded** parameter values; a query string carries **percent-encoded**
ones. So the equivalence this design claims — and §5 tests — is stated against the
collection, not against the text:

> For every query string *q*, `QueryBodyReader` applied to the JSON transposition of
> `QueryHelpers.ParseQuery(q)` produces a collection that `QueryStringParser` parses into
> the same `AlvoQuery` and the same violations as it parses `ParseQuery(q)` into.

That is the strongest available form, and it is true by construction rather than by
coincidence, because the transposition is defined as *the already-parsed collection,
written out as JSON*. It has three visible consequences, all of them deliberate:

- **`+` is a space in a query string and a plus in JSON.** ASP.NET Core rewrites `+` to a
  space before unescaping, so `?name=eq.a+b` filters on `a b` while `{"name":"eq.a+b"}`
  filters on `a+b`. The JSON value is the value; there is nothing between the caller and
  the operand.
- **Percent escapes are literal in JSON.** `{"make":"like.100%"}` is the filter a caller
  means, where the URL form needs `like.100%25`.
- **This is the point, not a wart.** The endpoint exists because hand-assembling a very
  long query string is where callers go wrong, and percent-encoding is most of that.

### 1.2 Key comparison follows `QueryCollection`, not JSON

ASP.NET Core accumulates query keys into an **`OrdinalIgnoreCase`** dictionary, so
`?limit=1&LIMIT=2` is one key carrying two values and is refused as `repeated-parameter`.
`QueryBodyReader` builds its collection with the same comparer and accumulates the same
way, so `{"limit": 1, "LIMIT": 2}` earns the same refusal.

This is not a rule the reader implements; it is a comparer it chooses, and every refusal
then falls out of the existing readers. Choosing `Ordinal` instead would have made the two
surfaces answer *different refusals* for one request — which is the divergence the whole
design exists to prevent, and the only place the transposition could have introduced one.

### 1.3 Why a JSON object and not the raw query string — the prior art, and the deviation

The closest published standard for this exact problem is **OData 4.01 §11.2.6.1, "Passing
Query Options in the Request Body"**: `POST <resource>/$query` with `Content-Type:
text/plain`, the body being the query options exactly as they would have appeared after the
`?`. It is the same idea — same grammar, other side of the request — and it is what Alvo's
"adopt the known spec" rule would normally point at.

**Alvo deviates in two ways, both deliberate:**

1. **A JSON object, not `text/plain`.** Two reasons, and neither is about pointers — an
   earlier draft of this section argued that a `text/plain` body would lose the
   per-parameter location, and that was simply false: `AlvoViolation.Pointer` carries a
   *parameter role* (`filter`, `limit`, `select`, …) on the query surface, and it would have
   carried the same roles whichever side the parameters arrived on. The real reasons are:
   - **The source asks for it by name** (`baas-analyza` §2.1, quoted above).
   - **A `text/plain` body keeps the caller's encoding burden**, which is §1.1's point: an
     OData-style body is still a percent-encoded query string, so the 400-element `in.(…)`
     list is still hand-escaped. A JSON string is the value. And a `text/plain` body cannot
     be described by a schema, so the OpenAPI document could offer a client generator
     nothing but "a string".
2. **The segment is `query`, not `$query`.** `$` is OData's own escaping convention for
   system resources and means nothing outside it. Alvo's callers know PostgREST, not OData
   (`baas-analyza` §2.1: *"agenti ju poznajú z trénovacích dát"*), and the issue as filed
   spells it `/query`.

The grammar **inside** the values is untouched PostgREST, as everywhere else.

**Rejected: `application/x-www-form-urlencoded`.** It is literally the same octets as a
query string, so it would need no transposition at all. It was still refused: reading it
means `HttpRequest.Form`, which brings ASP.NET Core's own form machinery and its own
separate bounds (`FormOptions.ValueCountLimit`, `KeyLengthLimit`, `ValueLengthLimit`). That
is a second set of limits Alvo neither owns nor publishes, refusing requests with a
framework message rather than a `violations` array — which is the 414 problem one layer in.
It also keeps the encoding burden §1.1 removes.

**Rejected: a JSON query DSL** (`{"filter": {"and": [{"field": "year", "op": "gte", …}]}}`),
which is what Elasticsearch's `POST _search` does. That is precisely the second grammar
#107 forbids, and it would need its own parser, its own refusal catalogue and its own
allow-lists — three places for the two surfaces to disagree.

**Rejected: `X-HTTP-Method-Override: GET` on a POST.** It moves nothing: the filter would
still have to be in the query string.

## 2. The transposition, and the rules it needs

One new internal type, `QueryBodyReader`, does exactly one thing: turn a bounded JSON object
into an `IQueryCollection`, or into the violations that stopped it. It resolves no field
name, knows no operator, and has no opinion about paging. Everything downstream of it is
today's parser, unmodified.

### 2.1 A value is transposed, never interpreted

| JSON value | Query value |
|---|---|
| `"gte.2020"` | `gte.2020` — verbatim |
| `100` | `100` — the number's **raw JSON text** |
| `true` / `false` | `true` / `false` |
| `["(a.eq.1)", "(b.eq.2)"]` | the parameter, twice |
| `null` | refused |
| an object, or an array holding a non-scalar | refused |

**No percent-decoding, ever** — §1.1 is the whole of why.

**A number contributes its raw JSON text.** The body is parsed with `JsonDocument`, so
`JsonElement.GetRawText()` gives back exactly the literal the caller wrote. Round-tripping
through `decimal` or `double` would put a formatting decision — culture, exponent, trailing
zeros — between the two surfaces, and the parser reads `price=lt.1500.50` as text anyway.

Numbers and booleans are admitted at all because an agent writing `"limit": 50` is writing
what the OpenAPI schema tells it to write, and `"limit": "50"` reads as a mistake. The cost
is one `GetRawText()` call. Note the one sharp edge, published in §6: `{"limit": 100.0}` is
`100.0` as text, which `TryReadWholeNumber` refuses as `invalid-page-size` even though JSON
Schema's `type: integer` admits it.

### 2.2 A repeated parameter is an array; a repeated JSON *name* is refused

`?or=(a)&or=(b)` conjoins two groups, and `?limit=1&limit=2` is refused as
`repeated-parameter`. Both survive, because an array becomes a multi-valued `StringValues`
and the existing readers already distinguish the two cases.

A **duplicate property name** (`{"or": "(a)", "or": "(b)"}`) is refused, not collapsed —
`JsonPayloadReader`'s existing rule, reused: RFC 8259 §4 leaves duplicate names undefined,
so first-wins and last-wins are both a guess about what the caller meant. The array is the
spelling that says it. Two names differing only in case are **not** this refusal; they are
§1.2's `repeated-parameter`.

### 2.3 Where a refusal points, and the two conventions in one array

A refusal about a **reserved** key points at that key's own role — `limit`, `offset`,
`order`, `select`, `after`. A refusal about **any other** key points at `filter`, and never
at the key the caller sent. A refusal about the **body as a whole** points at `""`.

Those are two different conventions in one `violations` array — a bare role name and an RFC
6901 pointer — and that is the published contract rather than a slip: `AlvoViolation`'s own
parameter documentation says the pointer is *"a JSON Pointer (RFC 6901) into the request
body, **or the role of the query-string parameter** the refusal concerns"*. This endpoint is
the first place both can appear in one response, so it is worth stating that they can.

**The rule that resolves the two, stated where a caller reads it.** A `pointer` that is empty
or begins with `/` is an RFC 6901 pointer into the request body; any other value is the role of
a query parameter. That sentence goes on `AlvoViolation`'s own `pointer` documentation, because
this is the first endpoint where one response can carry both and an agent branching on the
field otherwise has to infer the rule.

The role discipline is load-bearing on this surface: a pointer naming a field would answer
"does this entity have a field called X" for exactly the caller most likely to be asking —
the bit the byte-identical `unavailable-field` refusal exists to withhold.

**The write path does the opposite on purpose**, and the asymmetry is worth stating so it
does not read as an oversight: `PayloadViolations.UnknownField` puts the key in the pointer,
because a *create* has already published which fields exist — a `required` field's name is
in the create schema, and a caller who cannot name the fields cannot perform the write. A
read has published no such thing.

### 2.4 The body-level refusals are the read's own, not the write's

`JsonPayloadReader`'s six body-level refusals exist with write-path fix suggestions —
*"names no field **to write**"*, *"**A write payload** is a flat map of the entity's
declared fields"*, *"Send only the fields **you are changing**"*, *"Only a **`json` field's
own value** legitimately nests"*. Shipping those four sentences from a read endpoint hands
an agent a fix for the wrong operation, which is what §0 principle 4 exists against.

So the mechanics are shared and the **prose is not**:

- `BoundedJsonBody` reads the body under the byte bound and scans its shape, and returns
  the buffer plus a `BodyRefusal?` — a six-valued enum (`NotAnObject`, `MalformedJson`,
  `TooLarge`, `TooDeep`, `TooManyKeys`, `DuplicateName`) and nothing else. It knows no
  entity, composes no message, and takes no delegate.
- `PayloadViolations` and `QueryViolations` each map that enum to their own violation, with
  the **same stable `code`** and their own fix suggestion. The codes live as constants in
  one place so the two catalogues cannot drift on the code while differing on the prose —
  the same split `DataApiDocumentation.SharedNarrowing` already makes between a shared
  refusal and one operation's wording.

What moves into `BoundedJsonBody` is exactly `ReadBoundedAsync`, `EnsureWithinShapeBounds`,
`ScanShape`, the `NamesByDepth` class and the `ReadChunkBytes` constant. Everything
entity-bound — `Payload`, `Bind`, `BindOne`, `DeclaredFields`, `TryBind`, `Convert` — stays.
The helper hands back a byte buffer, not an object: `JsonPayloadReader` then parses it with
`JsonNode.Parse` as it does today, and `QueryBodyReader` parses it with `JsonDocument.Parse`
because §2.1 needs `GetRawText()`.

### 2.5 The bounds — the three that transfer, and the one the transport was providing

**Transferred unchanged, with no new option:** `MaxRequestBodyBytes` (1 MiB),
`MaxPayloadDepth` (32), `MaxPayloadKeys` (512). Their justification transfers exactly — a
parser reachable by a caller who is authorized and still hostile, bounded *while* reading
rather than on a finished document — and a fourth knob would ask a host to configure the
same number twice. The options' own remarks say "a write endpoint" today and are corrected
to name this one too, as is `data-api.md`'s budget table, whose "Request body" row is
currently a write row.

`MaxPayloadDepth` can never fire on a well-formed query body — the shape is one level, two
with an array — and is applied anyway rather than argued away, because it costs nothing and
a bound that is checked is one nobody has to reason about.

**Not transferred, because it never existed here: a bound on a comma-separated list's
*entry count*.** Two of the parser's own remarks say the quiet part out loud:

> *"Kestrel's request-line limit caps it at some kilobytes in practice, which is a property
> of the transport rather than a decision this layer made."* — `QueryStringParser`,
> `MaxCursorLength`
>
> *"the entry count is bounded only by the transport's URL length, while the width bound
> caps the number of **distinct** keys"* — `QueryStringParser`, `_claimedKeys`

Three readers split before they charge, and all three are measured rather than assumed:

| Reader | What it does now | What a 1 MiB body buys |
|---|---|---|
| `QueryStringParser.ReadSelect` | `value.Split(',')`, then `ProjectionTooWide` caps only **distinct** keys | `select=id,id,id,…` dedupes to one key and never trips the cap: ~350 000 strings allocated, request served |
| `SortParser.TryParse` | `raw.Split(',')`, then refuses on the first bad entry; `TryAddKey` also does `token.Split('.')` on one entry | the array exists before the refusal, and one entry of 1 MiB of dots allocates on its own |
| `ParenthesisedList.SplitTopLevel` | builds the whole member list; `FilterTermParser` calls `TryChargeCandidates(candidates.Count)` **after** it returns | an `in.(…)` of a million candidates is materialised, then refused |

Under an ~8 KB URL each of these was a few hundred entries. That is the bound this endpoint
removes, and it is post-authorization — an authenticated caller's amplification, not an
anonymous one — which is why it is a fix rather than a blocker.

**The fix is to make each split lazy, so the refusal that already exists fires before the
allocation instead of after it.** Two of the three then need no new bound at all:

- **`ParenthesisedList`** stops at a caller-supplied maximum and reports which of the three
  outcomes it reached — split, unbalanced, or over the maximum — instead of a bool. Its two
  callers pass the bound they were already going to enforce a line later:
  the **remaining** node budget for a group's members and the remaining `in`-candidate
  allowance for an `in` list, reported with the existing `filter-too-wide` and
  `too-many-in-candidates`. **No new code and no new published bound** — the same refusals,
  reached earlier.

  *Remaining*, not the per-list maximum, and the difference is the whole of whether it works:
  the candidate budget is a running total across the query, so a splitter using
  `MaxInCandidates` alone would let 256 terms each build a full 1000-element list — the
  256 000 substrings that bound exists to keep out of a statement — before the total refused.
  The allowance is floored at one, because a charge that fails still spends: without the floor
  a second over-long list arrives with a negative allowance and a caller's over-wide filter
  becomes a 500 instead of the 422 it earns.
- **`SortParser`** enumerates the comma-separated entries over the source span and returns on
  the first violation, which it already does. `order` is self-bounding: once every readable
  field is named once, the next entry must repeat one and earn `repeated-sort-key`, so the
  work is capped by the entity's field count. `TryAddKey`'s inner split becomes
  `token.Split('.', 4)`, since a sort key is at most `field.direction.nulls` and a fourth
  part is already refused.
- **`ReadSelect`** is the one that needs a cap, because a repeated entry legitimately
  dedupes and therefore claims nothing. Entries are enumerated lazily and capped at
  `AlvoFilter.MaxTerms` (256), refused as a new `too-many-select-entries` pointing at
  `select`. The number is not chosen for this: it is the framework's one measured "how many
  of a thing may one request carry", already the filter's node budget.

Two properties this deliberately keeps:

- **A repeated identical entry still dedupes.** `?select=id,id,id,…` is many entries and
  one key, and stays a 200 — the behaviour PR-F recorded and tested. The cap is on
  entries, well above any real projection.
- **The GET surface is tightened too, and no legitimate URL can reach it.** 256 select
  entries of at least two characters is under 800 bytes.

### 2.6 The fourth channel: the operand that never splits

The three readers above all split on a comma, which is why making the split lazy fixes them.
There is a fourth channel that does not split at all, and the first draft of this design
missed it: **a single operand**. `FilterTermParser` bounds no operand's length —
`MaxCursorLength` bounds `after` and nothing else — so `{"make": "like.<a megabyte of
%_%_…>"}` reaches the engine as a `LIKE` pattern matched against every row. Under a request
line that was a few kilobytes; under a body it is a megabyte, and the claim below that *"this
endpoint cannot express a filter the GET could not"* was false while it stood.

**Only `like` and `ilike` are bounded, and the asymmetry is about cost rather than about
size.** Every other operand is a bound *value*: the engine compares it per row, the comparison
is linear in its length and short-circuits on the first differing byte, and the sum of all
operands is already capped by the body bound. A pattern is *matched* rather than compared, and
its cost is not linear in its length. Refusing a long `eq` operand would be a bound on the
caller's data; refusing a long pattern is a bound on the server's work.

`QueryStringParser.MaxPatternLength` is **512, and it is chosen rather than measured** —
recorded as chosen. It is `MaxCursorLength`'s number for the same kind of reason: far past
anything a real caller sends, and the length past which the string has stopped being plausibly
the thing it claims to be. A search pattern longer than a keyset cursor is not a search. The
refusal is `pattern-too-long`, its own code rather than `invalid-filter-value`'s, because
nothing is wrong with the value — a caller told their value was unrepresentable would go
looking for a type mistake.

### 2.7 One bound that was checked and is correct

The shape scan admits a body whose deepest token sits at `MaxPayloadDepth`, and both readers
then parse with `MaxDepth = MaxPayloadDepth` — which counts the outermost container as level
1 where the scan's `CurrentDepth` reports it as 0. That reads like an off-by-one that would
turn an accepted body into an uncaught `JsonException`. **It was measured rather than argued**:
at 32 levels the scan accepts and the parse succeeds, and at 33 the scan refuses first. The
scan is exactly one level stricter than the parse, on both readers, and §5 pins the boundary
so the relationship is held rather than rediscovered.

**With §2.6 in place, every other parser budget really does apply unchanged**, which is #107's fourth
constraint: `AlvoFilter.MaxTerms` charged while descending, `MaxInCandidates` per list and
per request, `MaxPageSize`, `MaxCursorLength`, and the port's own `EnsureWithinLimits` belt.
This endpoint cannot express a filter the GET could not.

## 3. Where the endpoint sits, and the one structural change it forces

### 3.1 It is a read, gated as `list`

The delegate resolves the decision **before reading a byte of the body**, exactly as the
create does, and for the same three reasons `EnsureOperationIsAllowed`'s own remarks give:
a denied caller must be told they are denied rather than that their body is malformed;
parsing up to 1 MiB for a caller who cannot succeed is the amplifier the bounds exist
against; and the allow decision's `HiddenFields` **is** the mask the parser needs, so the
resolve replaces the one the mask already required instead of adding to it.

A caller whose `list` is unconfigured therefore reaches this route exactly as they reach the
GET: 403, from the same engine, the same catalog and the same context. §5 pins it.

### 3.2 A sixth endpoint kind, because `DataOperation` is the policy vocabulary

Today `DataApiOperationMetadata` carries a `DataOperation`, and **six** things key off it in
`AlvoDocumentTransformer.Enrich` alone: `SummaryOf`, `DescriptionOf`, `OperationId`,
`DataApiParameters.For`, `BodyComponent`, and the response catalogue. A second endpoint
marked `List` would mint a **duplicate `operationId`** (`{entity}.{operation-wire-name}`) —
an invalid document — and would publish the GET list's prose, which describes query-string
parameters, for a body-shaped request.

So the API layer gains its own enum:

```csharp
internal enum DataApiEndpointKind { List, Query, Get, Create, Update, Delete }
```

`DataApiOperationMetadata` carries the **kind** and exposes `Operation` derived from it, so
the authorization side is untouched: the filter still takes a `DataOperation`.

**Everything the kind reaches, named — because three of them are not in
`AlvoDocumentTransformer` and were missed in the first draft of this design:**

| Member | Change |
|---|---|
| `DataApiEndpoints.Protect` | takes the **kind**, builds the filter from `kind.ToDataOperation()`, stamps the marker with the kind, and passes the kind to `Documenting` |
| `DataApiDocumentation.SummaryOf` / `DescriptionOf` / `ResponsesFor` | key on the kind |
| `DataApiParameters.For` / `Names` / `HeaderNames` / `AddressesOneRow` | key on the kind |
| `AlvoDocumentTransformer.Operations` | projects `(kind, entity)` rather than `(operation, entity)` |
| `AlvoDocumentTransformer.BodyComponent` / `OperationId` | key on the kind |
| `DataApiHeaders.AddTo` / `UsedIds` | take `(kind, entity)` pairs, because both call `ResponsesFor` |

The kind's wire name lives in the **API layer**, on `DataApiEndpointKind` itself, and
`Query`'s is `query`. It is deliberately not `DataOperationNames.ToWireName` — that is an
extension on `DataOperation` and lives in *Abstractions*, where a transport's spelling has no
business being. Every existing wire name is unchanged, so no `operationId` moves.

**Rejected: a sixth member on `DataOperation` itself.** That enum is the *policy* vocabulary
— a descriptor's `rules` name those operations and `PolicyCatalog` is keyed by them — so
adding `query` would let a descriptor configure a rule for a transport, and would make
"`list` is unconfigured" not answer for this route. `ToWireName`'s own
`ArgumentOutOfRangeException` default is what would have made that change loud. The whole
content of #107 is that this **is** a `list`.

### 3.3 The route literal, and the 405 it creates

`POST {prefix}/{entity}/query`. `query` is a literal on the collection path and cannot
collide with `{prefix}/{entity}/{id:guid}`: no POST is mapped there, and a literal never
satisfies a GUID constraint anyway. It needs no entry in `ReservedQueryKeys`, which is about
query-string *keys*; an entity named `query` simply gets `/api/query/query`.

**A second stated consequence, and it is not about status codes.** A host convention keyed on
the HTTP **verb** — "POST means a write", which is a common shape for rate limiting or audit
logging — now applies write shaping to a read, while a GET-keyed convention misses this route
entirely. Alvo cannot prevent that, and the fix is available: a convention should key on the
`DataApiOperationMetadata` marker, which is what it exists for. Recorded in `data-api.md`
beside the 405.

**A stated consequence:** `GET`, `PATCH` and `DELETE` on `{prefix}/{entity}/query` change
from **404 to 405**. The path now matches an endpoint, so routing answers method-not-allowed
before anything Alvo wrote runs — which means no problem document and no `Cache-Control:
no-store` on that one response. It is accepted rather than papered over: a 405 from routing
is the same class of answer as the 404 from routing that `An_entity_the_descriptor_does_not_declare_has_no_route_at_all`
already asserts carries no body, and it discloses nothing the published document does not.
§5 pins it so it is a recorded behaviour rather than a surprise.

Six routes per entity — and, less obviously, **three document paths** rather than two, since
`/query` is a new path and not a new verb on an existing one. §5 names both counts.

## 4. What the caller sees

**Identical to the GET list**, and that is the specification: the same `{ items, next, count }`
envelope under the same `DataApiJson` options, the same projection and alias rendering, the
same `Cache-Control: no-store`, the same 422 slug (`malformed-query`) with the same
`violations` shape, the same 403 and 401.

**Headers.**

- `Prefer: count=exact` is honoured and `Preference-Applied` echoed, exactly as on the list.
  The header is unaffected by URL length, so there was never a reason to treat it
  differently. The existing fact named
  `The_count_preference_is_documented_on_the_list_and_nowhere_else` becomes "on the list and
  the query" — a rename, not a weakening.
- `If-Match` and `If-None-Match` are **ignored**, as on the list: a page has no version of
  its own. Not refused — that is the write side's rule, and this is a read.
- `Idempotency-Key` is **accepted and ignored**, on the same terms
  `data-api.md`'s *"`Idempotency-Key` is ignored on `PATCH` and `DELETE`"* section already
  sets out, and cited to it rather than re-argued: accepted so the blanket-attach habit is
  not broken, not offered as a parameter, and declared in prose. It matters more here than
  anywhere, because `POST` is exactly the verb that triggers that habit and the caller is
  reaching a *read*: there is no second row to prevent and nothing a key could tell them
  that the response does not.

**A body is required.** `{}` is the empty query — every readable field, the default page —
and an absent or empty body is refused as `not-an-object`. A required body is what lets the
OpenAPI `requestBody` be `required: true` and be worth generating a client from.

**CSRF is not a concern here, and the reason is worth recording** so the absence of a token
reads as a decision. A POST-that-reads is a cross-site vector only where the credential is
ambient. Alvo's credential is an explicit request header (`AlvoAuthOptions.HeaderName`),
never a cookie, so a cross-site form POST carries no credential and is judged as anonymous —
which default-deny answers with the same 403 any credential-less caller gets. The response is
`no-store` and the endpoint writes nothing.

## 5. How each claim is proved, and every existing fact this moves

The centrepiece is an **equivalence** fact, not a set of parallel facts: a second suite that
re-asserted the grammar against the new route would be the second grammar this design exists
to avoid, one layer up.

### 5.1 New facts

| Claim | Fact |
|---|---|
| It is the same parser | §1.1's statement, driven over the corpus `QueryStringParserTests` already uses. The harness transposes by **parsing the query string with `QueryHelpers.ParseQuery` and writing that collection out as JSON** — which is what makes the claim about decoded values true by construction rather than by luck, and is the one detail the fact cannot leave implicit. |
| §1.1's three consequences are real and intended | `+`, `%20` and `%25` each asserted directly: the body value is the operand, the URL value is the unescaped one. |
| §1.2's comparer choice | `{"limit":1,"LIMIT":2}` earns `repeated-parameter`, the same as `?limit=1&LIMIT=2`. |
| End to end, the two surfaces answer identically | `GET /api/vehicles?<qs>` and `POST /api/vehicles/query` answer the same status and the same body bytes, over the shared `AlvoApiWorld`, for representative queries including one `select` alias and one `Prefer: count=exact`. |
| It solves the issue's actual case | A 400-element `in.(…)` list — past a common request-line limit as a URL — is answered 200 through the body, with the rows asked for. |
| It is a read, and default-deny holds | A key whose scopes exclude `vehicles:read` is 403; a descriptor with `list` unconfigured is 403 for everybody; an anonymous caller is 403. Asserted on the new route, not inherited. |
| The mask really is threaded in | Over `masked-notes.alvo.json`, whose `notes.secret` is genuinely `hidden` — **not** the vehicle registry, which declares no hidden field at all and would have made this fact pass while exercising nothing. A masked name, an undeclared name and the same masked name through the query string are one refusal, byte for byte. |
| The decision precedes the **read**, not merely the parse | A denied caller (`ledgers`, which configures no `list` rule) sending a body past `MaxRequestBodyBytes` is answered **403, never `body-too-large`**. No statement-count assertion can see this: a refusal after buffering touches no database either. |
| The 2.7 boundary holds | A body at exactly `MaxPayloadDepth` is accepted by both readers; one level deeper is `body-too-deep` and never an uncaught `JsonException`. |
| The body-level refusals are the read's own | Each of the six carries a fix suggestion that does not mention writing, under the same code the write path uses. |
| The bounds bound | Over `MaxRequestBodyBytes`, refused without buffering; over `MaxPayloadKeys`, refused mid-scan; a duplicate name, a `null`, an object and a nested array each refused at the right pointer. |
| §2.6's pattern bound | A `like` pattern one character past `MaxPatternLength` is `pattern-too-long`; an `eq` operand of the same length is **not** refused, because the bound is on matching cost rather than on value size. |
| §2.5's lazy splits | 257 `select` entries is `too-many-select-entries`; `select=id,id,id,id,id,id` on a five-field entity is still 200; an over-long `in` list is `too-many-in-candidates` and an over-wide group `filter-too-wide` — the codes they already earned, now reached before the split materialises. Each also fires on the **GET** surface. |
| No new way past the parser's budgets | 257 filter terms and 1001 `in` candidates are refused through the body with the same codes and fix suggestions as through the URL. |
| The 405 | `GET`/`PATCH`/`DELETE` on `{entity}/query` is 405 with an empty body — recorded, not incidental. |
| The document describes it | One operation per entity with a distinct `operationId`, a required JSON `requestBody`, and no duplicate ids. |

The suite-wide screen that `filter-beyond-port-limits` reaches no response body lives in
`AlvoApiWorld.EnsureNothingInternalLeakedAsync`, **not** in `QueryStringParserPropertyTests`
— which never goes through HTTP. It covers the new route exactly to the extent the new
end-to-end facts send requests through the world, which they do.

### 5.2 Existing facts this change moves, named so none is discovered by a red run

| Where | What moves |
|---|---|
| `DataApiRoutingTests.Every_entity_in_the_applied_schema_gets_five_routes` | five → six, with the new pattern spelled out; renamed |
| `DataApiRoutingTests` marker fact, `endpoints.Count.ShouldBe(_entities.Length * 5)` | → `* 6` |
| `DataApiRoutingTests.ExpectedOperation` | keyed on verb + `{id:guid}` today; needs the `/query` suffix as a third discriminator, or `POST …/query` resolves to `Create` |
| `LazyRouteMaterialisationTests.PathsPerEntity = 2` | → 3. **A new path, not a sixth verb on an existing one** — the one count the "six routes" framing hides |
| `OpenApiDocumentTests.RoutesPerEntity = 5` | → 6 |
| `OpenApiDocumentTests`, `documented.Count.ShouldBe(55)` | → 63 (four statuses × two entities), and `ProvokeEveryStatusAsync` must actually drive all four on the new route or `observed.ShouldBe(documented)` fails |
| `OpenApiDocumentTests`, `refusals.Count.ShouldBe(44)` | → 50 |
| `OpenApiDocumentTests.The_count_preference_is_documented_on_the_list_and_nowhere_else` | renamed and widened to the query operation |
| `OpenApiDocumentTests.The_document_is_stable.verified.txt` | snapshot moves |
| `OpenApiDocumentCostTests`, `Operations(document).ShouldBe(entities.Count * 5)` | → `* 6`, with the class prose |
| `ConcurrencyTests.Every_response_a_generated_endpoint_produces_is_no_store` | enumerates its probes explicitly, so the new route is otherwise unmeasured against §4's `no-store` promise |
| `LazyRouteMaterialisationTests` | also gains an assertion that the third path is *this* path, not only that there are three |
| Six `"five routes"` doc comments | `data-api.md` ×3, `DataApiEndpoints` ×2, `AlvoEndpointDataSource`, and two test files |
| `AlvoExceptionHandlerTests` and `AlvoExceptionHandlerScopeTests` | both construct `new DataApiOperationMetadata("owners", DataOperation.List)` positionally — a compile break when the record's parameter becomes a kind |

Unaffected, checked: `PolicyResolutionCountTests` (per-request), `DataApiConventionTests`
(predicates on marker presence), `OpenApiServersTests`, and every `PublicApi.*.verified.txt`
baseline — everything this PR adds is `internal`, and the options edits are XML doc only.
**The public surface does not grow.**

## 6. The published request-body schema, and one deliberate omission

Per entity, `{entity}Query`: an object whose properties are the **query-located** parameters
this entity's list operation already publishes, plus one per readable field.

Three things that are not obvious and were wrong in the first draft:

1. **It is built in `AlvoDocumentTransformer`, not in `SchemaComponentBuilder`.** `limit`'s
   `maximum` and `default` come from `AlvoApiOptions`, and `SchemaComponentBuilder` is
   constructed with a schema view and no options. Threading options into it to serve one
   component is worse than building the component where the options already are.
2. **The set is a filtered one.** `DataApiParameters.Names(List, …)` also yields the tenant
   header and `Prefer` — both headers — so the derivation filters on
   `In == ParameterLocation.Query`, and it maps component **ids** back to parameter **names**
   (`orGroup` → `or`, `andGroup` → `and`). `not` is not a property: it is only ever a prefix,
   which is why `ReservedQueryKeys.All.Except([Not])` is already what the document's own
   parameter fact asserts over.
3. **The properties carry each parameter's `schema`, not its long `description`.** A filter
   parameter's description is a per-field sentence, and copying every one of them into a body
   schema would put the same prose in the document twice per entity — the opposite of the
   "described once" discipline `DataApiHeaders` states and `OpenApiDocumentCostTests` guards.
   The five settings keep their one-line meaning, since they are not per-field; the field
   properties are `type: string` and the grammar is stated once on the operation, exactly as
   `DataApiParameters` already argues for the `not.` prefix.

So the shared source is *which* parameters exist and *what shape* each takes — the part that
could drift — while the prose stays where it is stated once.

`limit` and `offset` are published as integers. **`{"limit": 100.0}` is refused** as
`invalid-page-size`, because the raw text `100.0` is not a whole number, even though JSON
Schema's `type: integer` admits it; the operation says so in one sentence.

**`additionalProperties` is deliberately not set to `false`**, even though the statement
would be true. No other Alvo body component sets it — `{entity}Create` and `{entity}Patch`
refuse an unknown key and stay silent about it too — and the closed-world rule is stated once
in prose on the operation. Setting it here alone would make one component the place a caller
learns a rule the other two keep elsewhere.

## 7. Scope

**In:** the route, `QueryBodyReader`, `BoundedJsonBody`, the endpoint-kind split, §2.5's
lazy splits and entry cap, §2.6's pattern bound, the OpenAPI operation and body component, the prose, the facts
above, and the `data-api.md` edits — which are **not only the route table**: `:3` and `:577`
both say "five routes per entity", the URL-grammar block gains the sixth line, the budget
table's "Request body" row stops being a write row and gains the entry cap, and the status
catalogue records that the six body-shape codes are now reachable under `malformed-query`.

**Out, and each for a reason:**

- **No `POST {prefix}/{entity}/{id}/query`.** A single-row read addresses its row in the
  path; there is no filter to overflow.
- **No `$query` alias**, and no `X-HTTP-Method-Override`. One spelling.
- **No port change.** `IAlvoData` is untouched; this is entirely an HTTP-layer transposition.
- **No new configuration.** §2.5.
- **Nothing about #118.** PR-D measured it and declined the cache; PR-F recorded two facts
  against it. This PR adds no third.

## 8. Deviations from the sources, recorded

1. **From OData 4.01 §11.2.6.1** — a JSON object instead of a `text/plain` query string, and
   `query` instead of `$query`. §1.3.
2. **From PostgREST** — it has no POST-for-read at all (`POST /rpc/<fn>` is a function call,
   not a query), so there is nothing to adopt and nothing to deviate from. The *grammar*
   remains PostgREST's, which is the part an agent recognises.
3. **From `baas-analyza` §2.1** — none. The source asks for `/query` with a JSON body and
   that is what this ships.
4. **From issue #107 as filed** — none on the constraints. The issue left "the same query
   string, or a JSON shape" open; §1.3 chooses the second and says why.
5. **Two tightenings of Alvo's own published behaviour, recorded as such** — §2.5 adds
   `too-many-select-entries` and §2.6 adds `pattern-too-long`, and both apply to the existing
   GET surface too. No query string
   a proxy would carry can reach it, and the alternative was shipping an authenticated-caller
   allocation amplification that only the URL length had been preventing. The other two
   readers gain no bound: they gain the *timing* of a bound they already had.
