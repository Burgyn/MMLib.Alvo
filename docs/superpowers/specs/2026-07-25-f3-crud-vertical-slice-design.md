# F3 vertical slice (CRUD) — the rest of the milestone — Design

> Covers issues **#20** (rule engine), **#19** (Data API + validations), **#22**
> (events + hooks), **#21** (computed & rollup), plus two work items the
> milestone does not own today (`[15a]` caller context + dev auth, `[15b]`
> OpenAPI + Scalar) and the front half of **#24** (host + compose), which #19's
> own DoD reaches into.
>
> Companion to [`2026-07-22-f3-schema-registry-migrations-design.md`](./2026-07-22-f3-schema-registry-migrations-design.md),
> which designed #18 (schema registry + migrations) — everything here builds on
> the `SchemaModel` and the migration engine that shipped there.

## Sources consulted

Designed against the full sources, not the compressed brief (which is
deliberately lossy and, on its own, produced four outright errors in the first
draft of this document):

- `docs/product/baas-analyza.md` §2.1 (data layer + dynamic entities), §2.4
  (row-level authorization), §2.12 (API keys, telemetry), §3 (events, rule
  engine, webhooks), §4 (multi-tenancy) — the *what & why*, including each
  section's numeric acceptance criteria, which are carried into the DoD below.
- `docs/product/alvo-specifikacia.md` §4 (Fáza 4 — CRUD), §1.3 (DX intent).
- Frozen artifacts: `schema/project.schema.json`, the ports already in
  `src/MMLib.Alvo.Abstractions`, the #18 design doc.
- Prior art, adopted rather than reinvented: the **CEL language definition**
  (operators, macro set, error absorption, gradual typing), **PostgreSQL
  `CREATE POLICY`** (`USING` / `WITH CHECK` semantics), **PostgREST** URL query
  syntax (operators, nested `and`/`or`/`not`, `is.null`, `nullsfirst`),
  **CloudEvents**, **Standard Webhooks**, and the **SQLite generated-column**
  restrictions.

Deliberate deviations from a source recommendation are collected in
*Deviations from the sources* near the end, so a later reader can tell a decision
from an oversight.

## Goal

Turn the schema registry into a **running backend**: a descriptor applies to a
database (done, #18), and then a client can read and write records over HTTP
under row- and field-level authorization, with validation, events and derived
fields. This is the first slice where Alvo does something a user can point at.

The milestone's ordering is dictated by one invariant, not by the issue numbers:

> **Policy is unbypassable — it is enforced *inside* `IAlvoData`, not around it.**

That makes the shape of the data port depend on the rule engine existing. Build
CRUD first and the port is born without its central guarantee, and the rule
engine gets retrofitted into a finished signature — exactly the failure this
invariant exists to prevent. So the **security core comes first** and the API is
built on top of it.

## Status going in

Shipped by #18 (PR-A + PR-B): descriptor model + parser + validator,
`DescriptorToSchemaMapper` (incl. framework-managed columns), `SchemaRegistry`
(physical driver), `ISchemaMigrator` / `ISchemaIntrospector` with EF
implementations for SQLite and PostgreSQL, destructive-change guardrail +
dry-run, append-only `descriptor_versions` with optimistic locking and rollback,
the `AddAlvo()` / `FromDescriptor()` builder skeleton, and the contract /
snapshot / mutation test scaffolding.

Not present at all: caller context, any authentication, CEL, the rule engine,
the data port, any HTTP endpoint, the outbox, hooks, automation. `computed` is
**actively rejected** by the mapper and the validator as a security guardrail
(see the deferral checklist on issue #21).

**Issue #20 was closed as completed on 2026-07-25 with no implementing PR.** It
was reopened while writing this design; nothing of it exists in `src/`.

## The 9 principles this touches

1. **Interface-first** — every port lands in `Abstractions` with a contract suite
   before an implementation exists; the adversarial suite ships green in PR1 against
   an in-memory reference implementation, and PR2's backends inherit it unchanged
   (*Deviations*, 6).
2. **Provider model everywhere** — `IAlvoData`, `IOutboxStore`, `IEventDispatcher`
   and `IEmailSender` are ports. Critically, **SQL rendering is a provider
   concern** (see *The core compiles, the provider renders*).
3. **Engine-agnostic core** — the rule engine, outbox and hooks are
   application-level and behave identically on SQLite and PostgreSQL; only the
   rendered SQL differs, and that difference is frozen by per-engine snapshots.
4. **Agent-first** — RFC 7807 with a violation list and a fix suggestion,
   idempotent writes, and an OpenAPI document anchored to the routes actually
   mapped.
5. **Secure-by-default / default-deny** — a missing rule denies; a missing caller
   context denies; a `hidden` field never reaches the `SELECT` list.
6. **CEL for conditions, JSONata for transforms** — CEL only in this milestone.
7. **JSON, one descriptor format** — no new configuration surface.
8. **Minimal API, not MVC** — the Data API is generated as minimal-API delegates
   on a `RouteGroupBuilder`.
9. **Vertical slice inside packages** — see *Code organization*: this milestone is
   almost entirely mechanism code, so it is organized by capability, not sliced.

## Definition of Done (per issue)

Numeric criteria are lifted from `baas-analyza.md` rather than invented.

- **`[15a]`** — a request carries an identity and a tenant; **a query with no
  tenant context fails rather than returning every tenant's rows** (§4).
- **#20** — the two-user / two-tenant / default-deny adversarial suite is green on
  SQLite and PostgreSQL; a rule referencing a nonexistent column **fails at save,
  not at request time**; a property test proves the translation never
  interpolates user input; the filter parser survives fuzzing without a crash and
  injection is attempted through **every** operator; the mutation score stays
  above the break threshold; the diff is read line by line.
- **#19** — CRUD works over a demo entity; validation returns RFC 7807 with a list
  of violations; **p95 of a filtered list over 100k rows on an indexed column
  < 50 ms locally**; **keyset pagination stable over 1M rows**; a repeated POST
  with the same `Idempotency-Key` returns the original result and creates no
  duplicate; tests green on SQLite + PostgreSQL; TeaPie tests against the
  docker-compose demo.
- **`[15b]`** — an OpenAPI 3.1 document is served, **is consistent with actual
  behaviour** (contract tests), and is lintable by Vacuum (#26); Scalar renders it.
- **#22** — a 10k-event chaos test loses no event; kill between commit and publish
  → delivered after restart; `changed(status) && new.status == 'approved'` fires
  **exactly once, at the transition**; a before-hook that exceeds its budget
  **rolls the transaction back cleanly with an RFC 7807 error**; a before-hook can
  reject and mutate; an after-hook runs post-commit; an ECA rule + cron + email
  work.
- **#21** — `total = unit_price * amount` is a real generated column on both
  engines; `sum(items.line_total)` stays consistent under concurrent child
  changes (race test); re-applying a descriptor with a `computed` field produces
  an **empty** migration plan on both engines (the SQLite introspection trap
  below).

## Architecture

### Caller context

`AlvoContext` is the currency of every data operation. Both identifiers are
**strongly typed**, not bare scalars:

```csharp
public readonly record struct UserId(Guid Value);
public readonly record struct TenantId(Guid Value);

public sealed record AlvoContext
{
    public required UserId User { get; init; }
    public required IReadOnlySet<Role> Roles { get; init; }   // never empty; anonymous = { Role.Anon }
    public TenantId? Tenant { get; init; }                    // null denies on scoped entities
}
```

That is the whole context — exactly the vocabulary the frozen F2 schema can
address, and nothing speculative (see *Claims are deliberately absent*).

`Guid` is the right underlying primitive because the schema mapper already emits
`id`, `tenant_id`, `created_by` and `updated_by` as `uuid`. The consequence, taken
deliberately: an **external** subject (an OIDC `sub`, an API key) is a string and
is therefore *mapped* to an internal `UserId`, never stored raw. That mapping is
needed anyway once #36 adds real providers.

Strong typing is only real if it is handled at every boundary:

- a `JsonConverter` for each — otherwise they serialize as `{"Value":"…"}`;
- an EF value converter on `tenant_id` / `created_by` / `updated_by`;
- **parameter binding in the compiled predicate** — `@user.id` must reach ADO.NET
  as a `Guid`, not as the wrapper, or the provider fails on an unknown type;
- `TryParse` for route and query-string binding.

Record `id` stays a plain `Guid`. Records are weakly-typed JSON payloads with no
per-entity types, so a generic `RecordId` would add a wrapper that catches
nothing.

#### Explicit parameter *and* an ambient accessor — both, for different reasons

`baas-analyza.md` §4 asks for the tenant context to be "available throughout the
request (ambient, via DI scope)". The brief requires every data operation to
*receive* the caller context. These are not in conflict once *availability* is
separated from *enforcement*:

- a **scoped accessor** resolves the context once per request and makes it
  available — the ergonomics §4 asks for;
- **`IAlvoData` takes it as a required parameter** — the enforcement.

The decisive argument for the parameter is not taste, it is the **post-commit
paths**. The outbox dispatcher, after-hooks and automation actions run with no
request scope at all, and they are exactly where a wrong or missing tenant is
catastrophic. An ambient accessor there returns empty — or worse, a leftover
scope. §3.3 independently requires those actions to *name* their identity ("data
actions with a defined identity: as system / as the originator", the Directus
`$full` / `$trigger` model). A required parameter forces that choice to be made
and reviewed at every call site; an ambient one lets it be forgotten.

#### Claims are deliberately absent

`AlvoContext` carries no claims dictionary. The reason is not YAGNI but the
fail-fast contract: rules compile **at apply**, so type-checking
`@user.claims['department_id'] == department_id` requires knowing that claim's
type at apply time. A runtime `IReadOnlyDictionary<string, string>` says nothing
at compile time, so it would either forfeit fail-fast or force a typed claim
declaration into the descriptor (`auth.claims`) — a change to the frozen F2 schema
made before we know what RBAC needs. The brief ties custom claims and
`@user.teams` to RBAC (#37); the declaration belongs there, with them.

### Roles are a set, and `Role` is not a string

**Decision: a caller holds a *set* of roles, and CEL exposes `@user.roles` with
membership via `in` (`'editor' in @user.roles`).**

The frozen F2 schema documents the singular `@user.role`. That prose is
documentation, not validation — `$defs/cel` is an unconstrained
`{"type":"string","minLength":1,"maxLength":2000}` with no pattern — so changing
the vocabulary invalidates no descriptor and alters no validation behaviour. Two
description strings in `schema/project.schema.json` (lines 145 and 689) are
updated in PR1.

Two reasons the singular reading is wrong:

1. **The built-in trio is not one axis.** `anon` / `authenticated` describe
   *whether the caller is logged in*; `admin` describes *privilege*. With a single
   slot an admin's role is `admin`, so `@user.role == 'authenticated'` is **false**
   for an admin — and "any logged-in user may read" is the most common rule anyone
   writes. This breaks the first descriptor someone authors.
2. **Multi-role is already on the plan.** #37 (RBAC) is scheduled and the brief
   names `@user.teams` — plural — beside roles. If `@user.role` shipped
   single-valued, the arrival of multi-role would leave it with no correct
   answer: guessing a "primary" role silently changes which rows existing rules
   match, and a silent change inside an authorization predicate is the worst
   failure mode this system has.

`==` is not bent into "contains": CEL is typed, so comparing a list to a string is
a type error. `@user.role` (singular) is **rejected at apply** with a fix
suggestion pointing at `'x' in @user.roles`, so anyone who read the old schema
prose gets an actionable message instead of a rule that appears to work.

`Role` is a value type, not a string:

```csharp
public readonly record struct Role
{
    private const string AnonName = "anon";
    private readonly string? _name;
    private Role(string name) => _name = name;

    /// default(Role) is anon — the least-privileged value, so a forgotten
    /// initialization fails safe instead of open.
    public string Name => _name ?? AnonName;

    public static readonly Role Anon = new(AnonName);
    public static readonly Role Authenticated = new("authenticated");
    public static readonly Role Admin = new("admin");

    internal static Role Application(string name) => new(name);
}
```

Two properties make this more than a wrapper: **`default(Role)` is `anon`**, so an
uninitialized field fails safe where a bare `string` would be `null`; and **an
undeclared role cannot be constructed** — there is no public constructor, and
application roles are minted only by a `RoleCatalog` built from the descriptor's
`auth.roles` plus the three built-ins, so a typo is rejected where it enters
rather than quietly matching no rule.

**Deferred:** when #36 adds external identity providers, a policy is needed for
*unknown* roles arriving in a token (reject the request versus ignore that role).
In F3 roles come from Alvo's own configuration, so rejecting loudly is correct.

**The role set in effect reaches authentication through `IRoleCatalogProvider`.**
The descriptor's `auth.roles` is the one source for both halves of authorization —
the roles a rule's literal is validated against, and the roles a credential may
mint — so a second, independently primed holder is ruled out: it could serve a role
set the rules were never compiled against. That single primed source is the compiled
policy catalog, but nothing above the rule engine reads it directly: the *provider*
of the policy catalog implements `IRoleCatalogProvider` (`IPolicyCatalogProvider`
derives from it, one instance registered as both), and `PolicyCatalog.Roles` stays
`internal`. Authentication therefore depends on a role-shaped port, not on the policy
catalog, which is what keeps #36's external identity source a matter of registering
another implementation rather than routing identity through the rule engine. See
deviation 10.

### Minimal dev auth

An API key maps to `(UserId, roles, tenant)`. Per the brief, **scopes are
mandatory even here** — "a PAT without scopes is the all-powerful `service_role`
anti-pattern renamed" — so a key carries a scope set that gates which operations
it may attempt, checked before the policy engine is consulted. Expiry, rotation
with an overlap window, last-used and immediate revocation (§2.12) are part of
the key model but their management surface is the Management API's problem, not
F3's.

**There is no service-role bypass.** §2.4 lists one as a must-have "with an audit
of every use" — and audit is #42, in F7. A bypass shipped before the audit that
is supposed to log it is precisely the footgun the same section warns about, so
it is deferred and explicitly tied to #42.

### Ports (in `Abstractions`, which stays ASP.NET-free — with one deliberate exception, deviation 11)

| Namespace | Port / type | Guarantee |
|---|---|---|
| `MMLib.Alvo` | `AlvoContext`, `UserId`, `TenantId`, `Role`, `RoleCatalog`, `IRoleCatalogProvider` | identity, roles, tenant; the role set in effect arrives through a port, so authentication never depends on who holds it |
| `MMLib.Alvo.Expressions` | `ICelCompiler` → `CompiledExpression` (a validated, typed tree) + `IPredicateRenderer` + `IPredicateEvaluator` + `IFieldSqlRenderer` (incl. `RenderComparableOperands`, deviation 13) + `CelFieldType` (deviation 12) | fail-fast at apply; input is never interpolated; the two Rule backends are symmetric ports — SQL for the stored row, rows for the candidate one |
| `MMLib.Alvo.Rules` | `IPolicyEngine` | for (entity, operation, context) returns a compiled predicate or deny; field-level `hidden` / `readOnly` |
| `MMLib.Alvo.Data` | `IAlvoData`, `AlvoQuery`, `AlvoRecord`, `AlvoManagedColumns`, `AlvoAuditStamp` (deviation 15) | policy is applied **inside** the port; the framework owns and writes its own columns |
| `MMLib.Alvo.Events` | `IEventPublisher`, `IOutboxStore`, `IEventDispatcher` | the event and the change commit together |
| `MMLib.Alvo.Hooks` | `IEntityHooks` | before is in-transaction and network-free; after is post-commit |
| `MMLib.Alvo.Auth` | `IAlvoContextResolver` | default-deny when the context is absent |
| **`MMLib.Alvo.Data.EntityFrameworkCore`** (not `Abstractions`) | `IAlvoSqlDialect`, `PreImageMutation` | statement shape — table source, column reference, typed-`NULL` projection, row lock, row limit; **deviation 11** says why it is not a port in `Abstractions` |

Implementations live in the core as capability namespaces: the CEL compiler, the
policy engine, the Data API generator (`MMLib.Alvo.Api`), the outbox and its
dispatcher, the hooks pipeline, automation. `IAlvoData` and the SQL renderers are
implemented in `Data.EntityFrameworkCore` and the per-engine packages.

### The seam

```
HTTP → Api (schema-derived validation, filter parsing)
         → IAlvoData.QueryAsync(query, context)          ← context is a REQUIRED parameter
              → IPolicyEngine.Resolve(entity, op, context)   → CompiledExpression
                   → provider renders: WHERE <policy> AND <user filter>
                                                          ← never an in-memory post-filter
```

## CEL: one parser, three profiles, two backends

One hand-written parser adopting the CEL spec and syntax (a decision already
taken: .NET ports are immature and none has a SQL backend). What varies is the
**profile** — an allow-list of AST node kinds plus a target. Anything outside the
profile is a structured error at `apply`, never a runtime surprise.

| Profile | Target | Allowed | Used by |
|---|---|---|---|
| **Rule** | SQL predicate **and** in-memory delegate | row fields, `@user.*`, `@tenant.*`, `== != < <= > >=`, `&& \|\| !`, `in`, `has()`, literals, parentheses | row-level authorization |
| **Computed** | SQL scalar expression | fields of the **same row**, arithmetic `+ - * /`, the ternary | `GENERATED ALWAYS AS (…) STORED` |
| **Condition** | in-memory delegate | the Rule profile plus `old` / `new` / `changed(field)` | hook and automation conditions |

Per the CEL spec the comprehension macros (`all`, `exists`, `map`, `filter`) are
**optional extensions, not core language** — excluding them from every profile is
spec-conformant, not a truncation. `has()` is included as the standard presence
test.

### Two backends, because `create` has no row to filter

The Rule profile compiles to **both** a SQL predicate and an in-memory delegate.
This is forced by the operations themselves, and the mapping is exactly
PostgreSQL's `CREATE POLICY` semantics — which is the gold standard for this and
is therefore adopted rather than re-derived:

| Rule | `USING` → SQL `WHERE` | `WITH CHECK` → in-memory over the resulting row |
|---|---|---|
| `list` | ✓ | — |
| `get` | ✓ | — |
| `create` | — (no stored row exists) | ✓ |
| `update` | ✓ | ✓ — **the same expression** |
| `delete` | ✓ | — |

Postgres, verbatim: *"If only a `USING` clause is specified, then that clause will
be used for both `USING` and `WITH CHECK` cases."* Alvo's descriptor carries one
string per operation, so `rules.update` reuses it for both — and that reuse is
not a convenience, it closes a hole. With `USING` alone, a rule
`owner_id == @user.id` permits an update whose payload sets `owner_id` to someone
else: the caller moves the row out of their own scope, legally. The post-image
check is what forbids it.

Conditions on the *new* value that are not authorization (`new.total > 0`) remain
`hooks.beforeUpdate` + `reject`, which the descriptor already supports. That is
the boundary: **`rules.*` answers "may this caller touch this row"; `hooks.before*`
answers "is this payload acceptable".**

### The core compiles, the provider renders

**`ICelCompiler` produces a validated, typed expression tree — never a SQL
string.** Rendering to SQL belongs to the storage provider, behind an
`IFieldSqlRenderer` supplied by the driver. Two independent reasons, either alone
sufficient:

1. **Dynamic entities (F7).** The same field is `"owner_id"` on a physical entity
   and `data->>'owner_id'` on a virtual one in the shared `entity_records` store.
   Baking bare column names into SQL emitted by the core would make F7 a rewrite
   of the security core, against PLAN.md's invariant ("never bake a
   physical-table assumption into the entity model") and against §2.1's
   acceptance criterion: *"the same adversarial and policy test suite passes
   identically over a physical and a virtual entity."*
2. **Dialect.** `ilike` is PostgreSQL; SQLite has no such operator. Rendering in
   the core would drag engine knowledge into the layer that is required to be
   engine-agnostic — and past the arch test that keeps the core EF-free.

### Null semantics: the SQL/CEL divergence, and how it is closed

This is the subtlest correctness issue in the milestone. CEL is **two-valued** and
absorbs errors in `&&`/`||` (the spec's "commutative absorption"). SQL is
**three-valued**. The same rule can therefore disagree with itself across the two
backends:

> Rule `!(owner_id == @user.id)` against a row where `owner_id IS NULL`.
> SQL: `NOT (NULL = 'x')` → `NULL` → the row is **not** returned (deny).
> CEL in-memory: `!(false)` → `true` → **allow**.

For `update`, that means `USING` denies while `WITH CHECK` permits — the two
halves of one rule contradicting each other. The fix is a rendering rule, not a
language rule:

> **The SQL renderer produces two-valued logic.** Every boolean subtree that can
> yield `UNKNOWN` — each comparison, and the whole predicate — is wrapped so that
> `NULL` becomes `FALSE`, matching CEL. Negation is rendered over the already
> collapsed value, never over a three-valued one.

PostgreSQL confirms the same choice from the other side: a `WITH CHECK` violation
is raised when the expression *"evaluates to false **or null**"*. Missing-field
semantics are therefore CEL's, uniformly, in both backends — and defined, which is
what §3.3 demands after naming Directus's surprising "missing field → reject
branch" behaviour as a defect. A differential property test (same rule, same row,
both backends, identical verdict) is the proof obligation.

### Default-deny, concretely

A missing `entity.rules.<operation>` denies. Allowing everyone must be written out
(`"true"`). The descriptor already carries the shape (`AccessRules` with five
nullable strings), so this is purely compilation semantics.

### Field-level `hidden` / `readOnly`

`FieldDescriptor.Hidden` and `FieldDescriptor.ReadOnly` are `BoolOrCel`. In F3
they are restricted to **context-only expressions — no row fields** — which is
also how the frozen schema describes them ("a CEL expression over `@user`"). They
are evaluated once per request; a `hidden` field then never enters the `SELECT`
list at all. Row-dependent masking (`hidden: "owner_id != @user.id"`) would by
definition require post-processing the result set, which is what the "no
in-memory post-filter" invariant forbids. It is rejected at apply with a
structured error and deferred.

### How the security properties are proved

- Identifiers are never taken from the expression text — they are resolved against
  `EntitySchema` and rendered by the driver. An unknown field errors at apply.
- Every literal leaves as a **named parameter**. A CsCheck property test asserts
  that for any generated CEL input, no literal from that input appears in the
  rendered SQL.
- Type checking against `EntitySchema` at apply, per CEL's gradual-typing model
  with a declared environment.
- The filter parser is **fuzzed**, and injection is attempted through every
  operator in the allow-list (§2.1 acceptance criterion).
- Golden CEL→SQL snapshots per engine, the adversarial suite, the differential
  two-backend test, and a Stryker gate over `Expressions` + `Rules`.

## Data API

### The query model is a tree, and it is modelled for the full target

`AlvoQuery` lives in `Abstractions` with no ASP.NET dependency. Its filter is a
**boolean tree**, not a flat list of triples — `and` / `or` / `not` with
parenthesised nesting, as PostgREST expresses it
(`?or=(age.eq.14,not.and(age.gte.11,age.lte.17))`). §2.1 is explicit that this
cannot be deferred: *"a badly designed query language can never be fixed without a
breaking change."*

The same reasoning applies beyond the filter. #19's stated scope is narrower than
§2.1's must-have list (which also requires bulk operations, relation embedding,
aggregations and a `POST /query` for filters too long for a URL). The resolution
is not to inflate #19, but to separate modelling from implementing:

> **`AlvoQuery` is designed for the full target shape — nested boolean filter,
> projection with aliases, relation embedding, aggregates, bulk — and F3
> implements the #19 subset.** That is what interface-first means here, and it is
> the only defence against the breaking change §2.1 warns about.

Operators follow PostgREST names so agents recognise them from training data:
`eq neq gt gte lt lte like ilike in is` in F3, with `contains` and the remainder
of the PostgREST set as later additions to the same allow-list. The HTTP layer
parses the URL into the model; the field and the operator are validated against
`EntitySchema` *before* anything reaches the renderer.

### Paging

Keyset by default (`?after=<cursor>`), offset as an opt-in with a server-enforced
maximum page size. Keyset correctness requires a **deterministic total order**, so
the cursor tuple always ends with the primary key, and a nullable sort column must
declare its null placement (`nullsfirst` / `nullslast`, PostgREST's spelling) or be
rejected — otherwise the row-value comparison that drives keyset paging skips or
repeats rows around the nulls. The cursor is opaque base64 over `(sort key, id)`,
unsigned but validated against the current sort so it cannot smuggle a different
ordering.

### Concurrency, validation, idempotency

- **Optimistic concurrency** (§2.1 must-have, missing from the first draft): `ETag`
  on reads and `If-Match` on writes, backed by `updated_at`. Without it "clients
  overwrite each other's data" — and a lost update is invisible in tests that do
  not look for it.
- **Schema-derived validation** before persistence: required, types, `maxLength`,
  `precision`/`scale`, enum values, formats, foreign-key existence. RFC 7807
  carrying **all** violations, not the first, with an `alvo.dev/errors/...` type.
- **Idempotency:** an `alvo.idempotency` table keyed `(key, endpoint, request
  hash)` storing status and response body, written in the same transaction as the
  operation. The same key with a different body is a 409, not a silent replay.

### Exposure and field-level behaviour

§2.1 requires "explicit expose, opt-in per table and per column — not everything
that is in the DB". Alvo satisfies this by construction rather than with a second
switch: **being declared in the descriptor is the explicit expose**, and `hidden`
is the per-column opt-out. Nothing is reachable without a rule anyway
(default-deny), so the RLS-off footgun §2.4 describes cannot occur.

A write to a `readOnly` field is rejected with 422 rather than silently ignored —
for an agent, a silent drop is worse than an error.

**Aggregations run over the policy-filtered set.** §2.4 names the leak directly:
`count` / `exists` over rows the caller cannot see reveals that those rows exist.
This constrains the design even though aggregates are not implemented in F3 —
they are modelled as part of the query that carries the policy predicate, never as
a separate unfiltered path.

### OpenAPI and Scalar

The document is generated by **`Microsoft.AspNetCore.OpenApi`** from the endpoints
actually mapped, and enriched by a **document transformer** that pulls
descriptions, formats, enum values and examples out of `SchemaModel`. .NET 10
emits **OpenAPI 3.1 with JSON Schema draft 2020-12** by default — the same draft as
the descriptor schema.

This corrects an earlier formulation of "emit the document from `SchemaModel`".
Emitting from the schema alone would document the *schema*, not the *routes*; a
bug in route mapping would not show up. §2.1's acceptance criterion is that the
document "is consistent with actual behaviour (contract tests)", which only the
endpoint-anchored form gives. Generated endpoints carry weakly-typed payloads, so
the transformer does the substantive work via `GetOrCreateSchemaAsync` /
`AddComponent`; a snapshot test freezes the result.

**Where the dependencies live:** `Microsoft.AspNetCore.OpenApi` is in the **core** —
it is first-party ASP.NET Core tooling for a capability that is a product promise
(agent-first: the document *is* the contract an agent reads), and embedded hosts
want their Alvo endpoints documented too. **Scalar is in `MMLib.Alvo.Host`** — it
is package-boundary rule (a): a foreign dependency most embedded consumers do not
want, and choosing a docs UI (Scalar, Swagger UI, Redoc) is a hosting decision an
embedded host makes for itself. Extracting an embedded Scalar package later is
cheap; a package invented now is not earned. #26 (Vacuum) lints the document, so
it depends on the core, not on Scalar.

## Events, hooks, automation

### Envelope and provenance

Events use the **CloudEvents** envelope (§3.2): `id` (the consumer's dedup key),
`source`, `type`, `time`, `subject`, `data` — one envelope for internal and
external delivery — plus a **payload version from the first day**, because the
payload schema will evolve. Provenance rides along: **the actor** (user / service /
which automation rule), a **correlation id**, and the **chain depth** for loop
protection. The first draft carried only the depth; without the actor a data
action cannot honour §3.3's "as system / as the originator" requirement, and
without the correlation id the end-to-end trace §2.12 requires (API → policy eval
→ DB → event → automation → webhook) cannot be stitched together later.

`data` carries `record` + `old_record` + the list of changed columns, which makes
`changed(field)` cheap.

### Outbox

`alvo.outbox` (`id`, `sequence`, `event_type`, `payload`, `actor`,
`correlation_id`, `provenance_depth`, `claimed_at`, `claimed_by`, `attempts`,
`dispatched_at`). The row is written on the **same `DbTransaction`** as the data
change — not a separate `SaveChanges`, or the guarantee does not hold. The
dispatcher is a `BackgroundService`: claim a batch (`FOR UPDATE SKIP LOCKED` on
PostgreSQL; serialized on SQLite, whose single-writer cap is documented anyway),
deliver, mark dispatched.

Like `descriptor_versions`, `alvo.outbox` and `alvo.idempotency` are **fixed
framework tables**, not products of the declarative diff engine. They are created
by the same `SystemSchemaInitializer` runner #18 established — extending that
runner, not adding a second mechanism.

**Ordering is a documented guarantee, not an accident:** no global ordering (§3.3
calls it expensive and brittle); **per-entity-key ordering**, partitioned by
primary key. Delivery is **at-least-once**, so every after-side action must be
idempotent or deduplicated by event id — including data actions, whose idempotency
key is derived from the event id.

Alvo owns this outbox; the core takes no foreign dependency for it, and
`IEventDispatcher` leaves Wolverine or an external bus available later as an
adapter package. This is a deliberate deviation — see *Deviations from the
sources*.

### Conditions are part of the subscription

A rule's condition is evaluated **before** the run starts, not as its first step.
§3.3 records the consequence of getting this wrong as a documented Directus
defect: thousands of log entries for runs that abort immediately on their
condition, making debugging impossible. Filtered-out events increment a counter
and produce no execution log. This is nearly free if designed in and awkward to
retrofit.

### Hooks

A before-hook runs in-transaction under a time budget and can only `reject` or
`mutate`. On the declarative face this is **structural** — `BeforeHookAction` in
the descriptor has exactly those two properties, so a network action cannot be
expressed. On the C# face (`IEntityHooks`) it cannot be enforced structurally;
there it is an analyzer plus the budget, i.e. defense-in-depth. This spec states
the difference rather than claiming impossibility for both. Exceeding the budget
rolls the transaction back cleanly with RFC 7807 (§3 acceptance criterion).
After-hooks run post-commit from the outbox, where the network is allowed.

### Automation minimal, and loop protection

ECA over outbox events (an `event` trigger + a CEL condition + `webhook` / `email`
/ `entity.update` actions), a cron `schedule` trigger in UTC, and an
`IEmailSender` with a console dev provider and SMTP. **Custom application events**
(`Publish("order.approved", payload)`) are included: §3.2 names their absence as
the reason Directus users end up listening to generic UPDATE events and filtering
thousands of false triggers. No HMAC, DLQ or redelivery UI — that is 7.1.

**Bulk coalescing:** per-item versus batch delivery is declared per rule, with a
batch event shape (`entity.orders.created.batch`); importing 10k rows must not
emit 10k events. **Loop protection:** the chain depth on the envelope, capped at
~5, plus cycle detection and an alert — not a blanket ban on chaining.

## Computed & rollup

`computed` unwinds the deferral checklist recorded on issue #21: the mapper and
the validator stop rejecting it, `FieldSchema.ComputedExpression` is repopulated
from **compiled, validated** SQL, and `DescriptorModelBuilder` emits
`GENERATED ALWAYS AS (…) STORED` from that compiled SQL — never from raw
descriptor text.

The SQLite generated-column rules make two of this issue's risks concrete, and
both must be tested rather than assumed:

- **`ALTER TABLE ADD COLUMN` cannot add a STORED generated column on SQLite.**
  Adding a `computed` field to an existing entity therefore requires the
  table-rebuild path. EF's SQLite provider does rebuild tables for unsupported
  alters, but that is a rebuild over live data, so it interacts with the
  destructive-change guardrail from #18 and needs an explicit test, not an
  assumption.
- **SQLite omits generated columns from `PRAGMA table_info`** (they appear only in
  `table_xinfo`). If introspection misses them, drift detection sees a missing
  column and re-adds it on every apply — a silent idempotency bug. Our
  introspector goes through EF's `IDatabaseModelFactory`, which may already handle
  this; the DoD above requires proving it with an empty second-apply plan.

SQLite also restricts the expression to "constant literals and columns within the
same row … only scalar deterministic functions … no subqueries", which is
independent confirmation of the Computed profile's allow-list and of why `rollup`
cannot be a generated column.

`rollup` is a transactionally consistent recompute of the parent aggregate inside
the write transaction that changed a child. The descriptor already carries the
shape (`Rollup` with `from` / `op` / `field` / `via`), so this issue supplies the
maintenance mechanics, not a new declaration. It is deliberately **not** a hook: a
parent-sum maintained by user code is the classic lost-update race, which is why
§2.1 requires it to be a first-class declarative concept. The race test is the
DoD.

This lands last because it needs the compiler (PR1) *and* the in-transaction write
pipeline that the hooks work establishes (PR5).

## Code organization

By the decision rule fixed in the #18 design and in
`docs/architecture/vertical-slice.md`: **triggered through one entry → VSA slice;
a mechanism other code calls → capability namespace with `Internal/`.**

This milestone is almost entirely mechanism code, so — as in #18 — there are **no
VSA slices**. The Data API is the documented exception: it is *generated*, so the
"slice" is the generator plus the pipeline it drives (schema registry → route
mapping → data port → rule engine → event backbone), and that generator is one
feature (`MMLib.Alvo.Api`), never a folder per entity. Each feature owns a
`Setup.cs` with `Add<Feature>` / `Map<Feature>` and is registered explicitly — no
assembly scanning.

## Package layout

One new project: **`MMLib.Alvo.Host`** (PR4) — the standalone host, plus Scalar.

The core gains `FrameworkReference Microsoft.AspNetCore.App` and a package
reference to `Microsoft.AspNetCore.OpenApi`. The shared architecture test permits
both: it bans EF Core and Npgsql references and any `MMLib.Alvo.*` reference other
than `Abstractions`. `Abstractions` stays on
`Microsoft.Extensions.DependencyInjection.Abstractions` alone — no port defined
here touches ASP.NET.

`docs/architecture/package-boundary.md` is updated with `MMLib.Alvo.Host` when PR4
lands (the doc asks to be kept current).

## Testing strategy

Same test types as #18 — nothing new is invented.

| Type | Covers |
|---|---|
| Contract tests per port + in-memory fakes | `IAlvoData`, `IPolicyEngine`, `IOutboxStore`, `IEntityHooks`; fakes ship in `MMLib.Alvo.Testing` beside `InMemorySchemaMigrator` |
| **Adversarial suite** | two-user, two-tenant, default-deny — **green in PR1** against an in-memory reference implementation, inherited unchanged by PR2's backends on SQLite + PostgreSQL (*Deviations*, 6) |
| **Differential backend test** | the same rule over the same row yields the same verdict in SQL and in-memory (the null-semantics proof) |
| Property tests (CsCheck) | the translation never interpolates input; the filter parser survives fuzzing |
| Golden snapshots (Verify) | CEL → SQL per engine; the OpenAPI document |
| Integration (Testcontainers) | the same CRUD suite against real PostgreSQL |
| Performance | p95 < 50 ms on a filtered 100k-row list (indexed); keyset stable over 1M rows |
| Crash test | kill between commit and publish → delivered after restart; 10k-event chaos run |
| Race test | concurrent child changes leave the parent rollup consistent |
| Transition test | `changed(status) && new.status == 'approved'` fires exactly once |
| TeaPie E2E | black box over the running compose stack (PR4) |
| Stryker | the whole core, so `Expressions` + `Rules` are covered with no config change — but it runs **post-merge on `main`**, not on the PR |

`TeaPie.Tool` is installed as a local dotnet tool and its agent skill lives in
`.claude/skills/teapie/`.

Two repo mechanisms this milestone has to work with, both added to `main` while
this design was being written:

- **Mutation is no longer a PR signal.** `stryker-config.json` already mutates all
  of `src/MMLib.Alvo`, so the new `Expressions` and `Rules` namespaces are covered
  automatically — but nothing blocks a merge on the score. For the three
  security-core PRs (1, 2, 5) the mutation run is therefore triggered **on demand
  via `workflow_dispatch` before merging**, exactly as `CLAUDE.md` prescribes for a
  risky core merge. Pure-wiring files we add (each feature's `Setup.cs`) belong on
  the config's exclusion list, like the existing builder/extension entries.
- **The snapshot-judge turn gate.** `.claude/hooks/turn-review-gate` blocks a turn
  whose `*.verified.*` baselines moved until `alvo-snapshot-judge` has reviewed
  them. This milestone adds a lot of Verify baselines (CEL→SQL per engine, the
  OpenAPI document), so expect the gate to fire routinely — a moved golden SQL
  snapshot is precisely the case it exists for, since a rule engine's test can be
  made green by accepting the wrong SQL.

## PR split

| PR | Content | Closes |
|---|---|---|
| **1** | `AlvoContext` (+ `Role` / `RoleCatalog`) + dev auth with scopes + tenant resolution · CEL parser, AST and type-checker · the three profiles · **both Rule backends** · the `IFieldSqlRenderer` contract · null-semantics rendering rule · `IPolicyEngine` · the two `@user.roles` strings in `schema/project.schema.json` · adversarial suite, green against an in-memory reference implementation | `[15a]` |
| **2** | **Spike first** (below) · `IAlvoData` + EF implementation · per-engine renderers · policy in the `WHERE` clause · adversarial + differential suites green on SQLite and PostgreSQL | #20 |
| **3** | generated HTTP Data API · filter tree + PostgREST parsing · keyset/offset paging · schema-derived validation · RFC 7807 · `Idempotency-Key` · `ETag`/`If-Match` · OpenAPI 3.1 + document transformer | — |
| **4** | `MMLib.Alvo.Host` + docker-compose + Scalar + TeaPie | #19, `[15b]` |
| **5** | CloudEvents envelope · outbox · dispatcher · hooks · minimal automation · crash test | #22 |
| **6** | `computed` (unwind the deferral, SQLite rebuild + introspection tests) · `rollup` · race test | #21 |

**PR2 opens with a de-risking spike**, mirroring how #18 handled its rename risk.
Records have no CLR types, so `IAlvoData` on EF is either (a) EF **property-bag
entity types** (`SharedTypeEntity<Dictionary<string, object>>`) over the runtime
`IModel` that `DescriptorModelBuilder` already builds, with the policy predicate
composed via `FromSql`, or (b) hand-built parameterized ADO.NET using EF's
`ISqlGenerationHelper` for identifier quoting. (a) reuses EF's dialect handling and
is preferred; the spike proves that raw-predicate composition works with property
bags on both engines before the rest of PR2 is built on it. If it fails, (b) is the
fallback and the `IAlvoData` port makes the swap non-breaking.

PR4 starts #24 but does not close it — the published image and the full standalone
story stay in F4. PRs 1, 2 and 5 touch the security core: `/security-review` plus
the `alvo-security-core-review` checklist, not only `/code-review`, and a
`workflow_dispatch` mutation run before the merge (mutation is post-merge now, so
it is no longer reached automatically on the PR).

## New issues to open

The lettered-suffix convention already exists in the repo (`[1b]`, `[3b]`, `[3c]`,
`[4b]`, `[21b]`).

- **`[15a]` Caller context + minimal dev auth (API key, scoped) + tenant
  resolution** → F3. Nobody owns this today, and both #20 and #19 are blocked
  without it.
- **`[15b]` OpenAPI 3.1 emission + Scalar docs** → F3. The brief decided it, no
  issue carries it, and #26 (Vacuum contract linting) silently assumes the
  document exists.

## Deviations from the sources

Recorded so a later reader can tell a decision from an oversight.

1. **Own outbox instead of Wolverine.** §3.3's ".NET building blocks" names
   Wolverine as "exactly the core of this section, ready-made". We write our own so
   the core stays free of a heavy foreign dependency that every embedded host would
   inherit, and so the outbox behaves identically on all engines. Cost: we own
   claim, retry and poison-message handling. `IEventDispatcher` keeps Wolverine
   available as an adapter package.
2. **No service-role bypass in F3.** §2.4 lists one as a must-have "with an audit
   of every use". Deferred until audit (#42) exists to log it; a bypass without its
   audit is the footgun the same section warns about.
3. **`@user.roles` replaces the singular `@user.role`** documented in the frozen F2
   schema prose. Rationale under *Roles are a set*; no descriptor is invalidated.
4. **§2.1 must-haves not implemented in F3** — bulk operations, relation embedding,
   aggregations, `POST /query`, `contains` and the remaining PostgREST operators.
   They are **modelled** in `AlvoQuery` so adding them is additive, but #19's stated
   scope is not inflated to cover them.
5. **Ambient tenant context (§4) is provided, but is not the enforcement
   mechanism.** Rationale under *Explicit parameter and an ambient accessor*.
6. **The adversarial suite ships green in PR1, not red.** This design had it written
   red in PR1 and going green in PR2 (principle 1, the PR table, the acceptance table
   and PR1's verification bullet all said so, and were corrected). PR1 ships the suite
   as an inherited base class in `MMLib.Alvo.Testing` **plus** an in-memory reference
   implementation of `IAlvoData` it runs green against. A suite that is red for a
   whole PR proves only that nothing implements the port yet; running it against a
   reference implementation proves the *facts themselves* discriminate, which is what
   makes them worth inheriting. The obligation the red suite was meant to create is
   kept by PR2's backends inheriting the same class, unchanged.
7. **PR1 ships the `IAlvoData` port, `like`/`ilike` and cursor semantics that this
   design assigns to PR3.** The port had to land in PR1 because the adversarial suite
   is written against it and the reference implementation above needs something to
   implement; once the port exists, the query shape it exposes — including
   `IFieldSqlRenderer.RenderCaseInsensitiveLike` and the opaque `After` cursor — is
   decided in PR1 rather than PR3. Cost, stated plainly: PR3's query-string layer
   inherits decisions it did not make, and `RenderCaseInsensitiveLike` ships with no
   consumer and an unresolved wildcard-escaping contract that every PR2 driver has to
   implement. Filed as a follow-up rather than guessed at here.
8. **No ASP.NET in PR1.** Dev auth "with scopes and tenant resolution" (the PR table's
   row 1) lands as ports plus an `IAlvoContextResolver` implementation and a
   `ScopeGate`; nothing is bound to an HTTP request, because PR1 adds no ASP.NET
   dependency at all. `ScopeGate`, `IAlvoContextAccessor` and
   `AlvoAuthOptions.HeaderName` therefore have no production consumer until PR3 wires
   the pipeline — deliberate, and the reason PR1 cannot satisfy #74's "a request
   carries an identity and a tenant" on its own.
9. **The two-valued collapse is rendered once in the core, not per driver.** *The core
   compiles, the provider renders* assigns every SQL fragment to the provider; the
   `NULL`-to-`FALSE` fold is instead composed once in the core's `SqlPredicateRenderer`,
   with only the *dialect's shape* of it delegated to `IFieldSqlRenderer` (three
   default interface members carrying the PostgreSQL/SQLite form). Rationale: the fold
   is the one rule both Rule backends must agree on, so a per-driver copy is how the
   SQL and in-memory verdicts silently diverge — the exact failure the differential
   test exists to catch. Cost: a dialect with different boolean handling (T-SQL) has to
   override three members rather than write its own predicate renderer.
10. **Identity roles are primed by the rule engine's own machinery, behind an identity
   port.** The Ports table assigns identity to `MMLib.Alvo` and rules to
   `MMLib.Alvo.Rules`, which reads as two independent lifecycles. They are not: the
   descriptor declares `auth.roles` once, and a role set authentication may mint from
   that the rules were never compiled against is exactly the inconsistency "one
   descriptor, one catalog, one guard" exists to prevent. So there is one primed source
   — the compiled `PolicyCatalog` — and the *provider* holding it implements
   `IRoleCatalogProvider`, the port `Auth` actually depends on. `PolicyCatalog.Roles`
   is `internal`: a public one would make the *policy* catalog the authoritative source
   of *identity* roles and foreclose roles arriving from anywhere else. Cost, stated
   plainly: `IPolicyCatalogProvider` derives from an identity port, so an implementer
   of the policy provider must also answer "which roles are in effect" — accepted,
   because the alternative (two registrations of one concrete type) lets a host replace
   the policy provider and silently keep the default one's roles. A host with an
   external identity source (#36) registers its own `IRoleCatalogProvider` and takes
   identity roles over; the descriptor still governs which literals a rule may name,
   and the two can then legitimately differ.

### Deviations added by PR2

PR2's own Superpowers plan is discarded once merged (`docs/PLAN.md` §1: the plan is "the
itinerary … discarded once merged"), so anything it decided that outlives it is recorded
here. The implementation-level *why* for each lives in `docs/architecture/data-path.md`,
which is the surviving detailed record; these entries exist so a reader of this design can
tell a decision from an oversight without reading that file.

11. **`IAlvoSqlDialect` is a port that deliberately does not live in `Abstractions`.** The
   *Ports* table's own heading said "all in `Abstractions`", and the driver's half of
   **statement** shape breaks it: it lives in `MMLib.Alvo.Data.EntityFrameworkCore`, beside
   the data path, because statement shape is a *relational* concern and `Abstractions` is
   required to stay free of one. The alternative considered and rejected was extending
   `IFieldSqlRenderer` with a table-rendering member — that port renders **expressions**, and
   every existing implementation (including the in-repo fakes) would have grown a member it has
   no table for. Cost, stated plainly: a driver author takes a dependency on the EF package to
   implement it, which is what every relational driver already does — and so does anyone who
   wants its contract suite, which is why that suite and the T-SQL fake live in a **companion**
   test-support project rather than in `MMLib.Alvo.Testing` (deviation 18).
12. **`CelFieldType` is a new public type in `Abstractions`.** Two layers must map a declared
   `FieldType` to the `CelValueType` a comparison over it is evaluated at, and neither can see
   the other's copy: the CEL type checker resolves a field reference's type, and a storage
   driver needs the same type for a **caller filter** or a keyset cursor, which are not CEL and
   therefore have no compiled expression to read a resolved type off. A second copy would not
   merely duplicate a table — a divergence changes *which comparisons get a dialect's value
   repair*, so a filter treating a decimal column as an integer compares it lexicographically
   on SQLite while the identical rule answers correctly. That is a fail-open reintroduced by
   drift, which is why an agreement test between two copies was judged insufficient.
13. **`IFieldSqlRenderer` gained `RenderComparableOperands`, a new member on a port PR1
   shipped.** SQLite has no decimal storage class, so a `decimal` field is a `TEXT` column and
   an unguarded `price > 100` becomes a *string* comparison — a rule gating access on an amount
   admits different rows per engine, which is fail-open on one of them and exactly what §0's
   engine-agnostic principle forbids. The member takes and returns **both** operands together
   rather than one: repairing one side alone does not merely approximate, it inverts, because
   SQLite orders every `TEXT` value above every `REAL` one. It also renders `ORDER BY`, so the
   repair must be order-preserving and not merely comparison-consistent — the page's order and
   its cursor boundary come from one member so they cannot drift. Cost: a driver must answer for
   every `CelValueType`, and it is a default interface member so no existing implementation
   broke.
14. **`IPolicyCatalogProvider` now derives from `ISchemaRegistry` as well as
   `IRoleCatalogProvider`.** Deviation 10 accepted the first derivation and its cost; this is
   the same argument applied to the schema. The data path must answer "how is this entity
   declared" for the *same* applied descriptor whose rules produced the verdict, and a second
   registration is how a host replaces the policy provider and silently keeps another
   component's stale schema. Cost, compounding deviation 10's: an implementer of the policy
   provider must now answer three questions — the catalog, the roles in effect, and the applied
   schema — and the three are one lifecycle by construction rather than by convention.
15. **The framework owns *and writes* the columns it injects.** `AlvoManagedColumns` (which
   columns the framework owns, answered from the entity's traits rather than a name list) and
   `AlvoAuditStamp` (who and when) are new public types in `Abstractions`, because there are two
   shipped `IAlvoData` implementations and F7 adds a third. A caller may never write one; the
   actor comes from `AlvoContext` and the instant from an injected `TimeProvider`, registered
   `TryAddSingleton(TimeProvider.System)` so a host can substitute one. Before this, the columns
   were injected, refused for only two of six, and **never populated** — so an audited create
   failed unless the caller forged its own audit trail.
16. **Three decisions on `IAlvoData` that are forever, taken in PR2 rather than deferred to
   PR3.** (a) The port's failure contract is exactly three exception families —
   `ArgumentException` for a malformed query or payload, `AlvoAuthorizationException` for a
   denial, `InvalidOperationException` for a broken invariant — stated on `IAlvoData`'s own
   remarks, because PR3 maps 422/403/500 by exception type and the two implementations disagreed
   on four malformed inputs. (b) `AlvoFilter` caps **breadth** as well as depth
   (`MaxTerms`, `MaxInCandidates`), engine-independently, because 1200 `AND` terms failed on
   SQLite and succeeded on PostgreSQL. (c) `softDelete` is **refused at apply time** rather than
   silently hard-deleting; the flag and the `deleted_at` answer stay so the implementation issue
   inherits a shape, and two `examples/*.json` were amended.
17. **`UseRelationalNulls(true)` is set by both drivers**, and its cost is a constraint on
   future code in these packages rather than a behaviour change today. See `data-path.md`; PR5,
   which adds LINQ here, is the first PR the constraint binds.
18. **`MMLib.Alvo.Testing` is split, and `MMLib.Alvo.Testing.EntityFrameworkCore` is a new
   project.** Deviation 11's port does not live in `Abstractions`, so its contract suite cannot
   either. Putting `AlvoSqlDialectContractTests` in `MMLib.Alvo.Testing` put an EF dependency on
   the *whole* test-support library, and that library's own remarks say it earns a package when
   **external provider authors** need the contract suites — so shipping it that way would hand EF
   to every consumer of the adversarial and differential suites, including an author whose store
   is not EF-backed at all. That forecloses the one audience the package exists for, and §0's
   provider model is precisely about not making a consumer adopt one infrastructure choice. It
   also had a local cost: `test/Directory.Build.props` references `MMLib.Alvo.Testing` from
   **every** test project, so all of them resolved `Microsoft.EntityFrameworkCore.Relational`
   transitively, including `MMLib.Alvo.Abstractions.Tests` and `MMLib.Alvo.Schema.Tests`, which
   deliberately have none.

   So `MMLib.Alvo.Testing` is Abstractions-only again and the relational half is a companion
   project. Per `docs/architecture/package-boundary.md` this one is **earned**: a real dependency
   boundary appeared, which is the trigger that rule describes, not a speculative split. Its types
   keep the `MMLib.Alvo.Testing.Data` **namespace**, so a consumer who adds the companion finds the
   dialect suite beside the data suites and nothing they already wrote moves.
   `EfDependencyBoundaryTests` (os A, reading the project files rather than loaded assemblies)
   asserts the boundary, because the family-wide runtime arch fact matches EF's types by *name* —
   deliberately, so it works in a project that cannot see EF — and therefore says nothing about
   who *can*.

### Deviations added by PR3

PR3's own Superpowers plan is discarded once merged, so anything it decided that outlives it is
recorded here. The implementation-level *why* for each lives in `docs/architecture/data-api.md`,
which is the surviving detailed record for the HTTP layer (`data-path.md` remains it for the port);
these entries exist so a reader of this design can tell a decision from an oversight without reading
either file.

19. **`QueryAsync` returns an `AlvoPage`, not a list, and `AlvoQuery` gains `Offset`.** Two of the
   three port widenings PR3 took, and both are cheaper before an HTTP layer exists than after —
   nothing is released, so a signature change costs a recompile of in-repo callers and nothing else.
   (a) The next-page cursor **cannot** be produced above the port: `KeysetCursor` is `internal` to
   the EF package on purpose, and only the provider can answer "is there another page" without a
   second round trip (it over-fetches by one row). A layer that re-encoded the cursor would be the
   second copy of one fact, which is the defect class PR2's review closed four times. (b) §2.1
   requires *both* paging modes, and `AlvoQuery`'s own remarks license exactly this — "a new optional
   member … can be added here without breaking an existing caller". Cost: `AlvoPage` adds a
   `TotalCount` that is always `null` in F3, which is a modelled member no shipped code fills;
   modelling it now is what keeps `Prefer: count=` additive later (#110).
20. **Two new exception families join the "three families" contract PR2 called settled.**
   `AlvoPreconditionFailedException` (412) and `AlvoIdempotencyConflictException` (409), and
   `IAlvoData`'s remarks table grows to five rows. Deviation 16(a) fixed the count at three
   deliberately, so growing it is a departure and not a footnote. The reason is that a request layer
   has nothing but the exception *type* to map a status from: folding either into `ArgumentException`
   would render 422 for a condition that is neither malformed nor the caller's mistake, and folding
   them together would lose the difference between "the row moved" and "you reused a key". Cost: a
   third-party `IAlvoData` implementation must now raise five families rather than three, and the
   port's contract suite is what tells it so.
21. **The precondition and the idempotency token enter the port rather than living above it** (#90).
   The third port widening. Both must be evaluated *inside* the write transaction — the precondition
   against PR2's row-locked pre-image, the idempotency record in the same commit as the row — so
   neither can live in the HTTP layer. Doing it from above would be a lost update and a duplicate row
   respectively, each invisible to a test that does not look for it. Cost, and it is a real one: the
   port now carries two concepts whose *names* come from HTTP (`If-Match`, `Idempotency-Key`), so
   `AlvoPrecondition` and `AlvoIdempotency` are deliberately spelled in the port's own vocabulary — a
   row version and a key plus a fingerprint — and the HTTP spellings are mapped at the edge.
22. **A write to a `readOnly` field is 422 from validation and 403 from the port — one behaviour per
   layer, not one behaviour.** The API's validator refuses it as a malformed request naming the field;
   the port refuses it as an authorization failure. Both are correct for where they stand: the
   validator holds the caller's resolved `readOnly` mask and can say *which* field, which is the
   answer an agent can act on, while the port cannot assume any layer ran first and must fail closed.
   Cost: the same descriptor mistake has two status codes depending on whether it arrived over HTTP,
   and a host calling `IAlvoData` directly gets the 403 form. Recorded rather than unified, because
   unifying would mean either the port trusting a caller's word or the API discarding the field name.
23. **An anonymous caller is a *context*, not a 401.** `AlvoContext.Anonymous` is what the endpoints
   see when the auth filter published no principal, and it is judged by the same default-deny policy
   as any other caller — so an anonymous request to an entity whose rules admit `anon` succeeds, and
   one to any other entity is refused by policy. 401 is reserved for a credential that **was**
   presented and cannot be used. It follows that `Idempotency-Key` from an anonymous caller is a
   **422, not a 401**: nothing failed authentication, so a 401 would owe a `WWW-Authenticate`
   challenge (RFC 7235 §3.1) for a request that never attempted it, and would blur the line this
   deviation keeps disjoint. Rationale under §2.1's default-deny reading; the anonymous fallback is
   also the fail-*closed* direction if an endpoint were ever mapped without the filter.
24. **PATCH-only partial update; no `PUT`.** `UpdateAsync` is partial by contract — "a field this
   dictionary does not mention keeps its stored value" — so a `PUT` would advertise whole-resource
   replacement the port does not perform. Cost: no upsert, and no create-with-a-caller-supplied-id,
   which is the shape a GitOps-style caller reaches for. Both need a port that can create-or-replace
   with `WITH CHECK` evaluated on the candidate row in both branches, and are deferred with a stated
   reason (#105) rather than approximated.
25. **A JSON envelope (`items` / `next`) rather than PostgREST's bare array plus `Content-Range`.**
   PostgREST is the syntax Alvo adopts for the *query string*, and this is the one place the response
   shape departs from it. The reason is that the alternatives put the cursor in a header, which gives
   it two homes and forces an agent reading a JSON body to parse HTTP headers to keep paging. `next`
   has exactly one home. Cost: a client written against PostgREST's response shape does not read
   Alvo's, and a `Link: rel="next"` header is deliberately not shipped (#104) so the two cannot
   disagree.
26. **`FieldClrType` is a new public type in `Abstractions`.** Deviation 12's precedent, one layer
   over: two layers must map a declared `FieldType` to the CLR type a value is carried as through
   `IAlvoData`, and neither can see the other's copy — a storage driver builds its read model and its
   bind parameters from it, and the HTTP layer binds a JSON request body with it. The
   "collapse onto `FieldClrTypeMap`" alternative was **impossible**, not merely unattractive: that
   type is `internal` to the EF package, and the core cannot reference it at all
   (`SharedArchitectureRules.Core_depends_only_on_Abstractions`). It belongs in the ports because it
   is not one backend's opinion — it is the contract `IAlvoData` publishes. PR3's first pass had two
   copies and **they already disagreed on failure mode**: the driver threw `NotSupportedException`
   for an unmapped type while the HTTP copy laundered the same condition into a client 422, telling a
   caller to fix a request that was fine.
27. **Position A: the declared, non-hidden schema shape is public; what is confidential is data and
   the *name* of a `hidden` field.** Not a new mechanism but a position that had never been written
   down, and it has to be, because the design already commits to it twice — route literals disclose
   entity existence *before* authorization, and the OpenAPI document publishes every entity's
   non-hidden field list. A framework cannot publish its schema shape and treat that shape as
   confidential. Stated so the third anti-enumeration claim does not get written; the full statement
   is `data-api.md`'s first section.
28. **A `hidden` field appears in a *request* schema if and only if it is `required`** —
   **this one needs the maintainer's ratification.** It is a deliberate, bounded confidentiality
   trade, on the same footing as PR2's two collation rulings. Excluding a hidden field from *every*
   schema would drop a mandatory field from the body a caller must send, since a required field a
   caller cannot see cannot be supplied at all; the rejected alternative — refuse `required` +
   `hidden` at apply — forecloses a real pattern, because a mandatory secret (a password, an API
   token the caller supplies and can never read back) is exactly that combination, and the frozen
   schema defines `hidden` as response-side. An **optional** hidden field's name still appears
   nowhere. Cost: a hidden, writable, optional field is absent from the request schemas while a write
   to it is accepted, so the document understates what a create will take — the safe direction, but a
   documented inaccuracy.
29. **Four decisions in the OpenAPI emission, none of which the brief covered.** (a) **Six files, not
   two** — the ~25-line method ceiling makes one transformer impossible, and `DataApiDocumentation` in
   particular earns its keep twice, being read by both the endpoint metadata and the transformer,
   which is what stops the document advertising a status no delegate emits. (b)
   **`.Produces(status, contentType)` rather than `.ProducesProblem(status)`**, because ApiExplorer's
   own `ProblemDetails` component omits the `violations` array every Alvo refusal carries, and an
   orphan schema missing it is strictly worse than none. (c) **`EntitySchema.Description` is carried
   onto the applied schema**, because the transformer cannot see the descriptor and a document
   describing every entity as nothing is a real loss; verified behaviour-free — `SchemaDiff.IsUnchanged`
   compares types and facets only, and `IsUnchangedReapply` compares serialized descriptor JSON, so
   the added record member plans no migration. (d) **An API-key security scheme plus reusable
   `responses`/`parameters`/`headers`**, since a documented 401 with nowhere to put a credential
   defeats §6; `security: [{}, {alvoApiKey: []}]` is correct rather than a hedge, because a descriptor
   may admit `anon` while the 401 is for a credential that was presented.
30. **A keyed-`POST` replay by a caller who may `create` but not `get` answers `201` with an id-only
   body, never 403.** When the replaying caller's `get` is denied outright, the retry must not be
   worse than the create it replays — so the answer is the original `Location` and a body carrying
   only `id`, taken from the idempotency record's own `row_id` with no row read performed. The safety
   argument is the record's identity: it is keyed on the key, the tenant *and* the acting user, so a
   match proves this caller created that row, and the id disclosed is the one their own original 201
   already gave them. Cost: `CreateAsync` now has one return shape that is not a full row, stated in
   its contract; and the sibling case — a *configured* `get` whose predicate excludes the row — stays
   a 404 (#101), because telling "invisible to me" from "deleted since" would need a policy-free
   existence probe.
31. **The core takes an ASP.NET Core dependency, so `package-boundary.md`'s "the core depends only on
   `Abstractions`" is now true only of project references.** §0 principle 8 makes every generated
   endpoint a minimal-API delegate, so `MMLib.Alvo` carries `FrameworkReference
   Microsoft.AspNetCore.App` plus `Microsoft.AspNetCore.OpenApi`. `Abstractions` deliberately stays
   free of both, and an arch test holds that line — the ports must stay implementable by a host that
   is not an ASP.NET application at all. Cost: an embedded consumer of the core is now an ASP.NET
   consumer whether or not it maps the Data API, and the framework reference silently supplies
   `Microsoft.Extensions.Options`, whose explicit `PackageReference`s had to be **removed** because
   NuGet's `NU1510` (raised as an error here) objects to a reference it will not prune.

## Assumptions (veto candidates)

1. Own outbox in the core with an `IEventDispatcher` port. **Decided with the
   maintainer.**
2. Minimal dev auth *and* minimal tenant resolution land in F3 rather than being
   stubbed. **Decided with the maintainer** — the adversarial suite is unwritable
   without them.
3. `UserId` / `TenantId` wrap `Guid`, consistent with the `uuid` managed columns.
   Relatedly, a caller holds a **set** of roles and CEL exposes `@user.roles`,
   superseding the singular form in the frozen schema prose. That prose edit is the
   part worth vetoing if the maintainer disagrees.
4. The host and compose are pulled forward into F3 so #19 closes against its
   literal DoD. **Decided with the maintainer.**
5. `AlvoContext` as a required parameter rather than an ambient accessor alone.
6. One hand-written CEL parser with three profiles, rather than a separate
   expression language for computed fields.
7. **The compiler emits a tree; the provider renders SQL.** The most consequential
   assumption here — it is what keeps F7's dynamic entities from becoming a rewrite
   of the security core.
8. **Two-valued rendering of the SQL predicate.** It makes Alvo's rules diverge
   from raw SQL intuition (`NULL` compares as false, not unknown) in exchange for
   the two backends agreeing. The alternative — three-valued in-memory evaluation —
   would mean re-implementing SQL's `UNKNOWN` in CEL, against the CEL spec.
9. Six PRs. PR1 closes no issue on its own; that is accepted, because splitting the
   security core out of the data port is the whole point of the ordering.

## Verification

- **PR1:** the adversarial suite exists and is green against the in-memory reference
  implementation, with every fact shown to discriminate (*Deviations*, 6); the
  property test passes against the compiler; unknown fields and out-of-profile
  nodes error at apply with a fix suggestion; the differential test harness runs
  both backends over the same rules.
- **PR2:** the spike's verdict is recorded before the rest is built; the adversarial
  and differential suites are green on SQLite and PostgreSQL; a query issued
  without a context throws; a snapshot proves the policy predicate is in the
  `WHERE` clause and not a post-filter.
- **PR3:** CRUD over `vehicle-registry` on both engines; a nested `and`/`or`/`not`
  filter round-trips; every violation is reported, not just the first; a replayed
  `Idempotency-Key` does not duplicate; a stale `If-Match` returns 412; the OpenAPI
  document matches the mapped routes and passes Vacuum.
- **PR4:** `docker compose up` yields a working backend from the descriptor alone;
  `teapie test` is green against it; Scalar renders the document.
- **PR5:** the 10k-event chaos test loses nothing; the transition test fires exactly
  once; a budget overrun rolls back cleanly with RFC 7807; the ECA rule + cron +
  email path works end to end.
- **PR6:** `total = unit_price * amount` is a real generated column on both engines;
  a second apply produces an empty plan; the rollup race test is green; every box on
  #21's deferral checklist is ticked.
