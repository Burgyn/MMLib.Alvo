# PR-F — `select` reaches the database, and gains aliases

Closes **#117** (`select` costs the database nothing — push the projection into the port)
and **#111** (projection aliases: `select=name:full_name`). Records the answer to **#118**
that PR-D already measured, and says what this PR does with that issue rather than
re-deriving it.

This is the PR-D2 that `2026-09-02-f4-pr-d-measured-cost-design.md` §5 defers to, by that
document's own name for it.

## 0. What this document inherits, and what it must not re-decide

PR-D measured all four of the performance follow-ups and shipped three of them. Its §5 is
the reason this PR exists and is, in substance, its brief: it establishes the mechanism,
the wall the issue did not know about, and six constraints for whoever implements it.
**None of that is re-litigated here.** What this document adds is the part PR-D
deliberately left open — the shape of the public member, where each refusal lives and with
which status code, how #111's aliases interact with the push-down, and the proof plan.

Two findings of PR-D's that this design is built on, restated so a reader of this file
alone is not misled:

1. **A literally narrowed `SELECT` list is not available.** Reads run through
   `FromSqlRaw` over a property-bag shared-type entity mapping every schema field, and EF
   fails with *"The required column '…' was not present in the results of a 'FromSql'
   operation"* if a column is missing — identically on both engines
   (`ReadProjection.cs`, first remark). Abandoning that means abandoning EF's type
   mapping, which is the reason a `uuid` arrives as a `Guid` and a decimal as a `decimal`
   on both engines rather than as three strings (`RecordMaterializer.cs`).
2. **The mechanism that *is* available is the one `hidden` already uses**, is already
   proven on both engines, and keeps `IAlvoSqlDialect` and its four implementations
   entirely out of this change: render `dialect.RenderNullProjection(storeType) AS <col>`
   for a field the caller did not select, and drop its key when the `AlvoRecord` is
   assembled.

### 0.1 The maintainer decision this PR does not take

PR-D §3.2 declined #118's cache on a measurement: the memo would save **one
`PolicyDecision` allocation** per request — plus, only for entities declaring CEL-valued
`hidden`/`readOnly`, one `HashSet`, one `FrozenSet` and a few context-only tree walks —
on a request already making a database round trip. It also withdrew the stronger
staleness argument and recorded the one constraint that would make such a cache safe
(*key on the catalog reference, not only on `(entity, operation, context)`*).

That analysis stands and this PR adds nothing to it. What this PR owes #118 is narrower
and is discharged in §5: PR-D's own §3.3 states that *"a read by id resolves exactly once"*
is a number **expected to move**, and names this PR's two triggers by name — `select`
(#117) and `If-Match`. §5 records which way the number went and why.

## 1. `AlvoQuery.Select` — the member, and what makes it non-advisory

```csharp
public IReadOnlyList<string>? Select { get; init; }
```

`null` means every field the entity declares — the behaviour every existing caller gets,
unchanged. A **non-empty** list names the declared fields the caller wants.

`AlvoQuery`'s own remark pre-authorises this by name (*"a new optional member (e.g. a
future `Select` projection list) can be added here without breaking an existing caller or
provider"*), so the record's additive-by-construction promise covers it and no other
member moves.

### 1.1 An empty list is refused, not resolved

An empty `Select` is a read that can return no field — a request with no serviceable
reading, exactly like the `after`+`offset` pair `EnsurePagingWindowIsSane` already
refuses. It gets the same treatment and the same home: a static guard on the port's own
type, so a future implementation inherits the rule instead of writing another copy of it.
There are **two** call sites, not three — `EfAlvoData.cs:114` and `InMemoryAlvoData.cs:92`,
because both shipped drivers share `EfAlvoData`; "three implementations" is true of driver
surfaces and not of call sites.

```csharp
public static void EnsureProjectionIsSane(AlvoQuery query)
```

**Unreachable from HTTP by construction, and that is not a reason to omit it.**
`QueryStringParser.ReadSelect` already refuses `?select=` with `QueryViolations.EmptySelect()`
and leaves `_select` null, so no parse can produce an empty list. The guard exists for a
direct port caller — embedded mode, a test, `AlvoDataSeed` — which is the same caller
`EnsureWithinLimits` and `EnsurePagingWindowIsSane` are written for. It is **not** wired
into the parser's `EnsureWithinPortRules`: a second refusal for a condition the parser
already refuses would add a caller-facing code for a request no caller can send, and the
one belt code that does exist there (`FilterBeyondPortLimits`) is screened suite-wide
precisely because a belt reaching a response body means something upstream is broken.

A **repeated name** is deduped, not refused — `AddOnce` in the parser already does this
silently and a set of names has no ambiguity to resolve. Stated so the asymmetry with the
empty case reads as a decision. A repeated *key* over two different sources is a different
condition and is refused; see §2.3, item 4.

### 1.2 The port-side security check: `Select` joins `EnsureAvailable`

A caller-supplied name in `Select` is the same kind of string as one in a filter or a sort
key, and it earns the same refusal:

```csharp
QueryFieldGuard.EnsureAvailable(QueryFields(query), entity, decision.HiddenFields);
```

`QueryFields(query)` grows to include `query.Select`. A `Select` naming a `hidden` or
undeclared field therefore raises `AlvoAuthorizationException` with the identical
`QueryFieldUnavailable` message — so, at the port, "exists but hidden from you" stays
indistinguishable from "does not exist", which is the whole reason that message is shared.

**This is what makes the member non-advisory**, and it is the objection `ParsedListQuery`
recorded when it declined to publish `Select` in PR3: *"a caller reaching the port
directly would ask for two fields and receive every one, with nothing raised."* After
this change a direct caller either gets the projection or gets a refusal.

**No HTTP behaviour changes.** `QueryStringParser.ReadSelect` already refuses a hidden or
undeclared name with `QueryViolations.UnavailableField` (a 422), and it runs first, so the
port's 403 is not reachable through the Data API. The two refusals are for two different
callers, and the split is deliberate — see §3.

### 1.3 The trap PR-D named, and the shape that avoids it

PR-D's sixth constraint is the one an implementer is most likely to get wrong, because the
framing "reuse the mask mechanism" invites it:

> **`select` must not be fed through `ReadProjection.Compose`'s `hiddenFields`
> parameter.** […] That parameter is guarded by `QueryFieldGuard.EnsureMaskable`, which
> throws `AlvoAuthorizationException` — so merging the two sets would (a) make a caller
> *preference* and a *security control* indistinguishable at the one point the mask is
> enforced, and (b) turn a malformed `select` into a **403** where it must be a **400**.

(PR-D writes "400" for the second half; the code the Data API actually answers a malformed
query with is **422** — `ProblemResultFactory.MalformedQuery`. The distinction PR-D is
making is client-error versus authorization-error, and it is correct; the number is
restated here so this document does not contradict the suite.)

So the two sets travel separately and meet only at render time. `ReadProjection.Compose`
takes them as two parameters:

```csharp
internal static string Compose(
    EntitySchema entity,
    IReadOnlySet<string> hiddenFields,
    IReadOnlySet<string> unselectedFields,
    IAlvoSqlDialect dialect,
    IEntityType rows)
```

`EnsureMaskable(hiddenFields, rows)` keeps seeing the mask **alone**. The union decides
one thing: which columns render as `NULL AS col`.

**And it is not enough to keep the two sets apart at the signature** — the union flows on
into `ReadProjection.Project` → `StoreTypeOf`, which today ends in
`throw new AlvoAuthorizationException(QueryFieldGuard.UnmaskableFieldMessage)` when the
read model maps no property of that name (`ReadProjection.cs:47-49`). Only a *masked* field
can reach that line now; under this design every *unselected* field reaches it too, so the
constraint would be satisfied at the parameter list and broken two calls later — a caller's
`select` ending in a 403, which §3's table promises is unreachable.

So the throw is split with the sets: a **masked** field whose store type cannot be resolved
stays an `AlvoAuthorizationException` (a mask arriving from a source that never ran the
apply-time check is a security condition, and F7's dynamic-entity registry is the next such
source), while an **unselected** field whose store type cannot be resolved is an
`InvalidOperationException` — it is unreachable by construction, because `unselected` is
derived from `entity.Fields`, so reaching it means the read model and the applied schema
disagree. That is a bug in Alvo, and it must not be dressed as a decision about the caller.

The two sets are also different in kind, and naming them separately is what keeps that
visible:

| | `hiddenFields` | `unselectedFields` |
|---|---|---|
| Origin | resolved by `IPolicyEngine` per caller | derived from the caller's own request |
| Meaning | a security control | a caller preference |
| A bad value is | `AlvoAuthorizationException` (403) | a violation (422) raised upstream |
| Names the caller supplied | never | always |

### 1.4 What is derived, and where

The unselected set is computed at the one place that knows both the request and the
schema, and it is derived — never caller-named:

```
survivors  = Select ∪ AlvoManagedColumns.For(entity) ∪ { every sort key }
unselected = declared \ survivors
```

Three survivor groups, each for a different and independently sufficient reason.

**`AlvoManagedColumns.For(entity)` — the authority, not a hand-written pair.** An earlier
draft of this design wrote `{ id } ∪ { VersionColumn(entity) }` and was wrong twice over.
Wrong in substance: `DescriptorToSchemaMapper` injects every managed column into
`EntitySchema.Fields`, so that set would have NULLed `tenant_id`, `created_at`,
`created_by`, `updated_by` and `deleted_at` — and `IAlvoData`'s own remarks make the key
set a **contract**, naming `tenant_id` explicitly:

> A returned `AlvoRecord` carries every non-hidden field the schema declares for that
> entity, including framework-managed columns (`id`, and — on a tenant-scoped entity —
> `tenant_id`); masking removes only descriptor-declared `hidden` fields, never a framework
> column.

Wrong in kind, and this is the worse half: `AlvoManagedColumns` exists *because* two
hand-kept lists of framework columns drifted, and its own doc calls it "the one authority
for which columns the framework owns" and records that the drift was "the fourth time this
codebase has paid for the same defect". Writing a seventh two-name list is that defect
again. The survivor set calls the authority.

`id` in particular is load-bearing beyond the contract: the keyset cursor is minted from
the fetched row (`EfAlvoData.Paginated`, `(Guid)kept[^1][AlvoDataContext.IdColumn]`), so a
NULLed `id` breaks paging outright. `updated_at` arrives through `Audit`, so the version
column `RowVersionETag` needs is covered wherever it exists.

**The sort keys — measured, not assumed.** This is the defect that nearly shipped, and it
is why this subsection is long. `ReadProjection` renders the placeholder under the
column's own name (`NULL AS "label"`), while `SortSqlRenderer` emits bare quoted
identifiers (`ORDER BY "label" DESC, "id"`). **A bare identifier in `ORDER BY` resolves
against the output column names first**, so `?select=id&order=label.desc` would order by
the NULL and return rows in whatever sequence the scan produced — while the keyset
boundary in `WHERE` still describes the real sequence. That is not a mis-sort; it is the
"a page skips or repeats a row" failure `SortSqlRenderer`'s own remarks say is made
unrepresentable by rendering the order and the boundary from one seam.

Measured on both engines rather than reasoned about, because the SQL standard's answer and
each engine's are not obviously the same thing:

| Clause referencing a NULL-projected column | SQLite 3 | PostgreSQL 16 |
|---|---|---|
| `ORDER BY b DESC` (bare output name) | **resolves to the NULL alias** | **resolves to the NULL alias** |
| `ORDER BY (CASE WHEN b IS NULL …)`, `b DESC` | wrong order | wrong order |
| `WHERE b = 3` | correct — the table column wins | correct — the table column wins |
| `ORDER BY t.b DESC` (qualified) | correct | correct |

Two conclusions, and the second is as important as the first:

- **A sort key is never NULL-projected.** Sorting by a field the response does not carry
  stays legal — `?select=name&order=created_at.desc` fetches `created_at`, orders by it, and
  `Render` still hides it (§2.2). The alternative fix, table-qualifying every identifier
  the order and keyset renderers emit, is airtight and is recorded here as the route to
  take if a future change really needs to NULL a sort key — but it reaches into
  `SortSqlRenderer`, `KeysetSqlRenderer` and the dialect, which is exactly the blast radius
  PR-D's mechanism was chosen to avoid.
- **A filter field, a keyset anchor field and a field a policy or tenant predicate names
  need no exemption**, because every one of them appears in `WHERE`, where both engines
  resolve the table column and ignore the alias. This matters more than it looks: a
  compiled `USING` predicate's field references are not enumerable from a
  `CompiledExpression`, so had `WHERE` behaved like `ORDER BY` there would have been no
  conservative survivor set to compute and the feature would have had no safe shape at all.
  `!has(owner_id)` over a NULLed `owner_id` would have rendered `NOT("owner_id" IS NOT
  NULL)` → true, admitting every row — a bypass, not a mis-sort. The measurement above is
  what rules that out, so it is recorded in the design rather than left in a commit
  message.

### 1.5 Only the page path narrows

`ReadStatementComposer.ReadStatementOptions` grows one member beside the existing
`Unmasked`:

```csharp
internal IReadOnlySet<string> Unselected { get; init; } = FrozenSet<string>.Empty;
```

Set by `ReadOptions(query, anchor)` — the one options record a list read composes from —
and by nothing else. PR-D's second constraint: the narrowing applies to the page path
only, never to `PolicyRoot`, `SingleAsync`, `AnchorAsync` or `ComposeCount`. How each of
those four avoids it is **not** uniform, and the differences matter to whoever implements
this:

- `PolicyRoot` (`EfAlvoData.cs:1129`) and `SingleAsync` (`:1193`) each build their own
  `ReadStatementOptions` and therefore get the empty default. No edit.
- `AnchorAsync` builds none — it calls `SingleAsync` (`:1215`), so it inherits that default.
  No edit.
- **`ComposeCount` does receive the page's own record, `Unselected` included.** It is
  handed the very value `ReadOptions` produced (`:122` → `:174`), because
  `ReadStatementComposer`'s own remark makes that deliberate: *"the caller passes the whole
  record rather than a narrowed copy so that a term added to the read cannot be silently
  missed here."* So this is not a term the count is shielded from by construction — it is a
  term the count **ignores**, exactly as it already ignores `Anchor`, `Sort`, `Limit` and
  `Offset`.

That the count is nonetheless unaffected is a property of `ComposeCount`, not of the
plumbing: it composes no projection at all — `COUNT(*)` reads no column, which is why
`ReadProjection` is never reached from it (`ReadStatementComposer.cs:139-141`). Recorded
this way because the plumbing reading is the intuitive one and it is wrong: an implementer
who "protects" the count by narrowing the record it is handed would be undoing the drift
guard that remark exists for.

### 1.6 Dropping the key: `RecordMaterializer`

`RecordMaterializer.ToRecord(row, hiddenFields)` is what makes a masked field
indistinguishable from an undeclared one, and an unselected field needs the same. It takes
the second set separately, for the same reason `Compose` does, and drops a key present in
either:

```csharp
internal static AlvoRecord ToRecord(
    IDictionary<string, object> row,
    IReadOnlySet<string> hiddenFields,
    IReadOnlySet<string>? unselectedFields = null)
```

**Defaulted here, required on `Compose` — a deliberate asymmetry.** `Compose` has exactly
one production caller (`ReadStatementComposer.cs:118`), so requiring both sets costs one
edit and makes the pair explicit at the single point where the union is formed.
`ToRecord` has **seven** (`EfAlvoData.cs:137, 219, 260, 536, 605, 821, 1377`), six of them
on write paths where "unselected" has no meaning: `null` there says *nothing was
unselected*, which is exactly what a write means, and keeps six unrelated call sites out
of this diff. The default can only ever widen what is returned to a value the caller was
already entitled to see, so it fails in the safe direction — unlike a defaulted
`hiddenFields`, which is why that one stays required.

The `Paginated` split runs on the raw property-bag rows **before** `ToRecord`, so the
cursor still finds `id` there whatever the caller selected. Stated because it is the one
ordering in this path that is load-bearing.

### 1.7 The in-memory reference

`InMemoryAlvoData.QueryAsync` honours `Select` in the same change or the differential suite
diverges — `ParsedListQuery`'s constraint is satisfied only by all three implementations
at once. It has no `SELECT` list, so for it the projection is exactly what the mask is:
`Mask(row, decision.HiddenFields)` becomes a drop over the union, and
`EnsureQueryFieldsAvailable` grows `Select` the way `EnsureAvailable` does.

The reference cannot prove the push-down (there is no statement), and it is not asked to.
It proves the **observable** half: the same request returns the same keys on all three
implementations.

### 1.8 `DataApiPage.Project` is deleted

PR-D's fifth constraint: *"Then `DataApiPage.Project` is deleted rather than left as a
second projection."* It is — see §2.2 for what replaces it and why the replacement is not
the same thing under a new name.

## 2. #111 — the API renames, the port never sees an alias

`select=name:full_name` returns the value of `full_name` under the key `name`. PostgREST's
own spelling (`alias:column`), adopted rather than invented, so an agent recognises it
from training data.

### 2.1 The alias never reaches the port

`QueryStringParser` splits `alias:source` and resolves the **source** through the existing
`QueryFieldResolver` — unchanged, so a hidden or undeclared source is refused exactly as
it is today, and the alias is not a way to reach a field the caller cannot read (#111's
first constraint, discharged by construction rather than by a check).

`AlvoQuery.Select` therefore carries **source names only**. The port has no idea aliases
exist, and its contract — that these are the entity's declared field names — stays
literally true. This is also what keeps §1.2's `EnsureAvailable` check meaningful: it
compares declared names against declared names.

`ParsedListQuery` carries the pairing:

```csharp
internal sealed record ProjectedField(string Key, string Source);
internal sealed record ParsedListQuery(AlvoQuery Query, IReadOnlyList<ProjectedField>? Select);
```

For a field named without an alias, `Key == Source`.

### 2.2 What replaces `Project`

`DataApiPage.Project` did three jobs: **drop** unselected keys, **order** the kept keys as
the request named them, and **skip** a selected key the row does not carry. The port now
does the dropping. What is left is a renderer:

```
foreach (Key, Source) in projection: emit Key ← row[Source]
```

It renames, it orders, and it still emits nothing for a source the row does not carry.
It is a different contract under a different name (`Render`), not `Project` with a
smaller body — and the doc comment says so, including the part a reader will otherwise
find suspicious: **the port must return every framework-managed column, and every sort
key, whatever the caller selected (§1.4), and the response must not show any of them unless
the caller asked.** So the
renderer emits exactly the requested keys, from a record carrying those plus one or two
managed columns. That is not a second projection; it is the response's own key list, and
it is the only layer that can hold it, because the alias is an HTTP concern the port was
deliberately not told about.

Existing wire behaviour is preserved exactly: `?select=name` returned only `name` before
this PR and returns only `name` after it.

### 2.3 What an alias is refused for

Four refusals, each with a violation code and a fix suggestion, all pointing at `select`
(a fifth, the amplification bound, is §2.4):

1. **A malformed pair** — an empty alias or an empty source (`select=:name`, `select=name:`).
2. **An alias outside the field-name grammar.** An alias must match
   `^[a-z][a-z0-9_]{0,62}$` — the same pattern `schema/project.schema.json` requires of a
   field name (line 518). *A deliberate narrowing of PostgREST, which admits an arbitrary
   alias.* The reason is that an alias is a field name **in the response**: an agent
   reading the body should not have to tell a real field from caller-supplied text, and an
   unbounded alias is caller-controlled bytes in a response key for no gain. Recorded here
   as a deviation so a later reader can tell it from an oversight.
3. **A reserved name as an alias.** `select=limit:name` is refused. A reserved name is
   already refused as a *field* name at apply time (`ReservedQueryKeys.EnsureNoneIsShadowed`
   and `DescriptorValidator`) because `?limit=10` would be ambiguous. An alias creates no
   such ambiguity — it is never a query key — so this one is consistency, not necessity,
   and is recorded as such: a response key that no descriptor is allowed to declare should
   not be reachable by renaming.
4. **A colliding key.** Two selected fields resolving to the same response key over
   **different** sources (`select=name,name:other`, `select=a:x,a:y`), or an alias
   colliding with a managed column that survives the projection (`select=id:name`, which
   would otherwise put two different values under `id`). Refused rather than resolved: two
   sources for one response key is a request with no correct answer.

   `select=name,name` is **not** this condition: the key and the source are both the same,
   there is nothing to resolve, and it dedupes exactly as it does today (§1.1). The rule is
   *one key, one source* — a second source for a key already taken is what is refused.

   **Not "the same rule the parser applies to a repeated sort key", and the difference is
   worth stating.** `SortParser` refuses *any* repetition, an identical one included
   (`RepeatedSortKey`), on the reasoning that a repeated key is inert and admitting it lets
   a caller make the server compose an unbounded `ORDER BY`. `select` deduped silently
   before this PR, that is published behaviour, and changing it would 422 requests that
   work today — so the dedupe stays and the unboundedness worry is answered where it
   actually bites, in §2.4.

### 2.4 The bound aliases make necessary

Before this PR the projection was self-bounding: `select` named declared fields, duplicates
collapsed, so the response could never carry more keys than the entity has fields. **An
alias breaks that.** `select=a:name,b:name,c:name,…` names one column under arbitrarily
many distinct keys, every one of them legal by the rules above, and the response carries a
copy of that value per key. The only remaining limit is the URL length the transport
happens to allow — which is exactly the "a bound the caller controls" shape §2.1 of the
analysis warns about and which `AlvoFilter.MaxTerms` exists to close on the filter side.

The bound is **derived, not chosen**: a projection may name at most as many keys as the
entity declares fields.

```
projection key count ≤ entity.Fields.Count
```

A response with more keys than the entity has fields is a duplication request, not a
read — no caller has a use for it, and the number needs no judgement call, no configuration
knob and no per-engine measurement. It is generous by construction (a ten-field entity
admits ten aliases over one column) and it makes the amplification factor exactly one.

Refused with its own violation code, pointing at `select`.

**It also retires a pre-existing cost that has nothing to do with aliases.** `AddOnce`
dedupes with `List.Contains`, so today's uncapped `select` is an O(n²) scan over a
caller-controlled length — Kestrel's default 8 KB request line admits a few thousand
entries, which is millions of ordinal comparisons per request. The cap bounds it at the
field count, which is why this is filed as a bound rather than as a rewrite of the
dedupe.

## 3. Where each refusal lives, and with which status

One table, because the split is the part of this PR most likely to be "simplified" by a
later reader into a single check:

| Condition | Refused by | Caller sees |
|---|---|---|
| `?select=` empty | `QueryStringParser.ReadSelect` | 422, `EmptySelect` |
| `select` names a hidden or undeclared field | `QueryStringParser` via `QueryFieldResolver` | 422, `UnavailableField` — identical to an undeclared name |
| malformed alias pair, bad alias grammar, reserved alias, colliding key | `QueryStringParser` | 422, new codes |
| more projected keys than the entity declares fields | `QueryStringParser` (§2.4) | 422, new code |
| `AlvoQuery.Select` empty (direct port caller) | `AlvoQuery.EnsureProjectionIsSane` | `ArgumentException` |
| `AlvoQuery.Select` names a hidden or undeclared field (direct port caller) | `QueryFieldGuard.EnsureAvailable` | `AlvoAuthorizationException` → 403 |
| a resolved mask hides the row key | `QueryFieldGuard.EnsureMaskable` | `AlvoAuthorizationException` → 403 |

The 422 rows are the Data API's; the 403 rows are the port's. A caller preference never
produces a 403 and a security control never produces a 422 — §1.3's table is the same
statement one layer down.

## 4. How each claim is proved

The claims are of three different kinds and each needs its own suite. Listed with the ring
that gates it, because two of them are not free.

**That the unselected column leaves the statement** — `AlvoDataStatementTests`, which
exists on both engines for exactly this kind of claim. PR-D's third constraint bounds
what may be asserted: `NULL AS col` stops the engine reading the column, it does **not**
make the query proportionally cheaper — the win is real for a wide or TOASTed column and
near zero for a narrow int. So the fact asserts what is verifiable — the unselected
column does not appear in the emitted statement, and `id` does — and the doc comment
states the honest scope of the win rather than a throughput claim no statement can carry.
Note the asymmetry PR-D recorded: `SqliteAlvoDataStatementTests` is a **unit** project
(ring0), `PostgreSqlAlvoDataStatementTests` is **Testcontainers integration** (ring2).

**That all three implementations return the same keys** — the port contract suite in
`MMLib.Alvo.Testing`, so both shipped drivers and the in-memory reference run the same
cases: a projection returns the selected keys; an unselected key is absent rather than
null; a framework-managed column is present in the *record* whatever the caller selected
(the contract §1.4 quotes) while absent from the *response* unless selected; paging works
across a page boundary under a projection; and a `Select` naming a hidden field raises the
same refusal as one naming an undeclared field.

**That the wire shape and the grammar are right** — `QueryStringParser` unit tests plus
the existing `QueryStringParserPropertyTests` for the alias grammar, and the OpenAPI
document snapshot for `select`'s updated description.

**That nothing regressed on the security split** — three tests that would each pass with a
merged-set implementation and fail loudly on the specific defects §1.3 describes: a
malformed `select` answers 422 and not 403; an unresolvable *unselected* store type raises
`InvalidOperationException` while an unresolvable *masked* one raises
`AlvoAuthorizationException`; and `EnsureMaskable` still refuses a mask over the key when
the caller selected every field.

**That the response's key list and the port's `Select` cannot drift** — one test asserting
that for every parsed query, the set of `ProjectedField.Source` equals the set in
`AlvoQuery.Select`. §2.2 leaves two lists that must agree, and `Render`'s inherited "emit
nothing for a source the row does not carry" behaviour would *hide* a divergence rather
than fail on it; without this the renderer is a second key-set authority with nothing
pinning it to the first.

**That the sort defect §1.4 measures stays fixed** — and these are the cases the first
draft of this design had none of, so they are named individually: a sort over an
**unselected** field returns the same order as the same sort with no projection, on both
engines; the same query **paged across a boundary** returns each row exactly once (this is
the one that fails loudly under the defect, where a plain order assertion might not); a
filter over an unselected field matches the same rows as without the projection; and an
entity whose `USING` predicate names an unselected field admits exactly the rows it admits
unprojected — including the `!has(field)` shape, which is the one that would have inverted
into a bypass.

**Baselines that move — two, and no SQL snapshot among them.**

1. `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`
   enumerates every `AlvoQuery` member and `EnsurePagingWindowIsSane`, so `Select` and
   `EnsureProjectionIsSane` move it. This is the load-bearing one: a public-API baseline is
   the record of what the package promises.
2. `OpenApiDocumentTests.The_document_is_stable.verified.txt`, whose current `select`
   description asserts the thing this PR falsifies — *"It narrows the response only — the
   read still fetches the whole row … saves bandwidth to the caller and nothing at the
   database."*

**No Verify SQL baseline moves, contrary to what PR-D §5 predicted** (*"moves Verify SQL
baselines on two engines"*). `AlvoDataStatementTests` asserts with `ShouldContain` over
captured statements rather than through Verify, and the Verify SQL baselines that do exist
(`cel-to-sql-*.verified.txt`, the generated-DDL snapshots) cover predicate rendering and
DDL — not `SELECT` lists. Stated so nobody goes looking for a baseline that was never
there, and so PR-D's prediction is corrected rather than quietly inherited.

Both moves are baseline edits, so the turn gate will ask `alvo-snapshot-judge` for a
verdict — the intended path, not an obstacle.

## 5. What this PR records against #118

Nothing is implemented for #118. Two lines are added to it, both facts this PR establishes
or confirms:

1. **The count PR-D pinned did not move.** PR-D §3.3 wrote *"a read by id resolves exactly
   once"* as a number expected to move, and named `select` (#117) as one of its two
   triggers. It did not move: this PR keeps `select` on the list path only, so
   `MapGet` still interprets no caller input before the port call and still, correctly,
   carries no `EnsureOperationIsAllowed`. The trigger `DataApiEndpoints.cs` writes down
   for a future author remains armed and unspent — `select` on a single row, and `If-Match`
   on a read, both still owe that guard.
2. **The premise correction is confirmed independently.** `ScopeGate` is an API-key scope
   check with no `IPolicyEngine` reference; a list request resolves twice, not three
   times. Confirmed again while designing this PR, against the same files.

#118 stays open as a maintainer-scoped judgement (PR-D §8.1), and this PR does not take
it.

## 6. Scope

**In:** `AlvoQuery.Select` and its guard; `QueryFieldGuard.EnsureAvailable` covering it;
`ReadProjection`, `ReadStatementComposer`, `RecordMaterializer`, `EfAlvoData` (page path
only); `InMemoryAlvoData`; `QueryStringParser` (alias grammar and its refusals);
`ParsedListQuery`/`ProjectedField`; `DataApiPage` (`Project` → `Render`); the OpenAPI
`select` description; `docs/architecture/data-api.md` (`:177` is the sentence that becomes false — *"`select` is
applied to the response, not to the `SELECT` list — the port has no projection member yet,
so `?select=id` costs the database exactly what a full read costs (#117)"* — and `:155` is
the grammar row that gains the alias form); `CHANGELOG.md`.

Four edit sites that are easy to miss when scoping the above, listed because each one is a
file a naive reading leaves untouched:

- **`AlvoQuery`'s own type summary** says projection *"is deliberately **not** modelled
  here yet; they land in PR3."* That sentence becomes false with the member it describes.
- **`IAlvoData`'s returned-key-set contract** (§1.4) must be amended, not left to be
  falsified: the key set stays contract, and `AlvoQuery.Select` becomes the one thing that
  narrows it, with framework-managed columns explicitly exempt. A contract paragraph that
  a new member quietly makes untrue is worse than no paragraph, because the next reader
  budgets trust against it.
- **`ReadStatementComposer.cs:118`** is `ReadProjection.Compose`'s single production
  caller, and `Compose` is reached from three paths — `PageAsync`, `SingleAsync` and
  `PolicyRoot` — so the new parameter is threaded there once and the two non-page paths
  pass the empty set explicitly.
- **`ReadProjectionTests.cs`** calls `Compose` directly at six places; a required third
  parameter edits each.
- **`ReadStatementComposerTests.cs` and `TSqlDialectSeamTests.cs`** construct
  `ReadStatementOptions` at ~32 places, all through object initializers, so the defaulted
  `Unselected` member touches none of them. Verified rather than assumed, because that is
  the property the `init`-only shape is chosen for.

**Out, deliberately:**

- **`select` on `GET /{entity}/{id}`.** Adding it obliges `MapGet` to carry
  `EnsureOperationIsAllowed` — a change in the authorization path, for the oracle
  `DataApiEndpoints.cs:403` describes in full — and forces the same question about
  `CreateAsync`/`UpdateAsync`, which return a record too. It is a separate PR with a
  security review, and the trigger for it is already written down where the next author
  will read it.
- **#118's cache.** §0.1.
- **#109 aggregations, #104 `Link` header.** #109 is a leak-oracle design of its own;
  #104's issue says ship only when a consumer asks.
- **A literally shortened `SELECT` list.** §0, finding 1. If a future driver reads through
  something other than `FromSqlRaw` over a property-bag entity, that is where this
  reopens.

**Not in this PR and not filed as missing:** relation embedding (#108) and casting
(`col::type`). Both are `select` grammar in PostgREST; neither is in F4's scope and the
alias split above does not foreclose either — an embedded relation is a new node in the
parsed projection, not a change to the port member.

## 7. Deviations from the sources, recorded

| Deviation | Source | Why |
|---|---|---|
| The `SELECT` list keeps every column; unselected ones render `NULL AS col` | #117 as filed ("push the projection into the port", narrow the `SELECT` list) | EF `FromSql` requires every mapped property in the result set. §0, finding 1. The database still stops reading the column. |
| An alias must match the field-name grammar | PostgREST admits an arbitrary alias | §2.3, item 2 — an alias is a response key an agent reads as a field name. |
| A reserved name is refused as an alias | Nothing requires it; an alias is never a query key | §2.3, item 3 — consistency with what a descriptor may declare. Recorded as consistency, not necessity. |
| A projection may name at most `entity.Fields.Count` keys | PostgREST imposes no such cap | §2.4 — the alias is what makes the projection able to amplify, so the bound arrives with it. Derived from the schema, not chosen. |
| A repeated `select` name dedupes, while a repeated `order` key is refused | Internal consistency | §2.3, item 4 — the dedupe is published behaviour; refusing it now would 422 requests that work today, and the bound in §2.4 answers the reason `order` refuses. |
| `?select=name` still hides `id` from the response although the port now returns it | — | §2.2. Preserves the pre-PR wire shape exactly; the port's guarantee and the response's key list are two different lists. |
| A projected read returns fewer keys than *"every non-hidden field the schema declares"* | `IAlvoData`'s returned-key-set contract | The feature is the narrowing. The contract paragraph is amended in this PR to name `Select` as the one narrowing channel, with framework-managed columns exempt — §1.4. |
| A sort key survives the projection even when unselected | Nothing in #117 or PR-D anticipates it | §1.4 — measured: `ORDER BY` resolves a bare identifier against the output alias on both engines, which would break keyset paging. The alternative (qualifying every emitted identifier) was declined for blast radius, and is recorded as the route if this ever needs to change. |
| #118 gets two recorded facts and no code | #118 as filed (a request-scoped decision cache) | PR-D §3.2 declined it on a measurement. §0.1, §5. |
