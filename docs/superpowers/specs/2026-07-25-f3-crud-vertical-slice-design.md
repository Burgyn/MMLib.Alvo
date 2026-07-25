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
(see the deferral checklist in issue #21).

**Issue #20 was closed as completed on 2026-07-25 with no implementing PR.** It
was reopened while writing this design; nothing of it exists in `src/`.

## The 9 principles this touches

1. **Interface-first** — every port below lands in `Abstractions` with a contract
   suite before an implementation exists; the adversarial suite is written red in
   PR1 and goes green in PR2.
2. **Provider model everywhere** — `IAlvoData`, `IOutboxStore`, `IEventDispatcher`
   and `IEmailSender` are ports; the EF implementation of the data port lives in
   `Data.EntityFrameworkCore`, never in the core.
3. **Engine-agnostic core** — the rule engine, outbox and hooks are application
   level and behave identically on SQLite and PostgreSQL; only emitted SQL differs
   and that difference is frozen by per-engine snapshots.
4. **Agent-first** — RFC 7807 with a violation list and a fix suggestion,
   idempotent writes via `Idempotency-Key`, an OpenAPI document generated from the
   same schema that generates the routes.
5. **Secure-by-default / default-deny** — a missing rule denies; a missing caller
   context denies; a `hidden` field never reaches the `SELECT` list.
6. **CEL for conditions, JSONata for transforms** — CEL only in this milestone;
   JSONata belongs to the after-side action payloads and is out of scope here.
7. **JSON, one descriptor format** — no new configuration surface is introduced.
8. **Minimal API, not MVC** — the Data API is generated as minimal-API delegates
   mapped onto a `RouteGroupBuilder`.
9. **Vertical slice inside packages** — see *Code organization*: this milestone is
   almost entirely mechanism code, so it is organized by capability, not sliced.

## Definition of Done (per issue)

- **`[15a]`** — a request carries an identity and a tenant; a request without one
  is denied, not silently anonymous.
- **#20** — the adversarial suite (two-user, two-tenant, default-deny) is green on
  SQLite and PostgreSQL; a property test confirms the translation never
  interpolates input; the Stryker score over `Expressions` + `Rules` is above
  threshold; the diff is read line by line.
- **#19** — CRUD works over a demo entity; validation returns RFC 7807 with a list
  of violations; tests green on SQLite + PostgreSQL; TeaPie tests against the
  docker-compose demo.
- **`[15b]`** — an OpenAPI 3.1 document is served and matches the live routes;
  Scalar renders it; the document is lintable by Vacuum (#26).
- **#22** — an outbox crash test (kill → no lost event); a before-hook can reject
  and mutate; an after-hook runs post-commit; an ECA rule + cron + email work.
- **#21** — `total = unit_price * amount` works as a generated column;
  `sum(items.line_total)` stays consistent under concurrent item changes.

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
    public TenantId? Tenant { get; init; }          // null denies on scoped entities
    public required IReadOnlySet<string> Roles { get; init; }
    public required IReadOnlyDictionary<string, string> Claims { get; init; }
}
```

`Guid` is the right underlying primitive because the schema mapper already emits
`id`, `tenant_id`, `created_by` and `updated_by` as `uuid`. The consequence, taken
deliberately: an **external** subject (an OIDC `sub`, an API key) is a string and
is therefore *mapped* to an internal `UserId`, never stored raw. That mapping is
needed anyway once #36 adds real providers.

Strong typing is only real if it is handled at every boundary. The checklist:

- a `JsonConverter` for each — otherwise they serialize as `{"Value":"…"}`;
- an EF value converter on `tenant_id` / `created_by` / `updated_by`;
- **parameter binding in the compiled CEL predicate** — `@user.id` must reach
  ADO.NET as a `Guid`, not as the wrapper, or the provider fails on an unknown
  type;
- `TryParse` for route and query-string binding.

Record `id` stays a plain `Guid`. Records are weakly-typed JSON payloads with no
per-entity types, so a generic `RecordId` would add a wrapper that catches
nothing.

### Ports (all in `Abstractions`, which stays ASP.NET-free)

| Namespace | Port / type | Guarantee |
|---|---|---|
| `MMLib.Alvo` | `AlvoContext`, `UserId`, `TenantId` | identity, roles/claims, tenant |
| `MMLib.Alvo.Expressions` | `ICelCompiler` → `CompiledPredicate` (SQL fragment + named parameters) / `CompiledDelegate` | fail-fast at save; input is never interpolated |
| `MMLib.Alvo.Rules` | `IPolicyEngine` | for (entity, operation, context) returns a predicate or deny; field-level `hidden` / `readOnly` |
| `MMLib.Alvo.Data` | `IAlvoData`, `AlvoQuery`, `AlvoRecord` | policy is applied **inside** the port |
| `MMLib.Alvo.Events` | `IEventPublisher`, `IOutboxStore`, `IEventDispatcher` | the event and the change commit together |
| `MMLib.Alvo.Hooks` | `IEntityHooks` | before is in-transaction and network-free; after is post-commit |
| `MMLib.Alvo.Auth` | `IAlvoContextResolver` | default-deny when the context is absent |

Implementations live in the core as capability namespaces: the CEL compiler, the
policy engine, the Data API generator (`MMLib.Alvo.Api`), the outbox and its
dispatcher, the hooks pipeline, automation. The EF implementation of `IAlvoData`
goes into `Data.EntityFrameworkCore`.

### The seam

```
HTTP → Api (schema-derived validation, filter parsing)
         → IAlvoData.QueryAsync(query, context)          ← context is a REQUIRED parameter
              → IPolicyEngine.Resolve(entity, op, context)
                   → EF: WHERE <policy predicate> AND <user filter>
                                                          ← never an in-memory post-filter
```

`AlvoContext` is an **explicit required parameter** of every `IAlvoData`
operation, not an ambient `AsyncLocal` or a scoped accessor. It is more verbose
for hand-written endpoints, and that is the point: there is no code path to data
without a context, which makes "unbypassable" a testable property rather than a
convention. An ambient context can be forgotten, and forgetting it would fail
silently and open.

## CEL: one parser, three profiles

One hand-written parser (adopting the CEL spec and syntax, not a library — a
decision already taken in the spec). What varies is the **profile**: an allow-list
of AST node kinds plus a compilation target. Anything outside the profile is a
structured error at `apply`, never a runtime surprise.

| Profile | Target | Allowed | Used by |
|---|---|---|---|
| **Rule** | SQL predicate (boolean) | row fields, `@user.*`, `@tenant.*`, `== != < <= > >=`, `&& \|\| !`, `in`, literals, parentheses | row-level authorization |
| **Computed** | SQL scalar expression | fields of the **same row**, arithmetic `+ - * /`, conditional via the ternary | `GENERATED ALWAYS AS (…) STORED` |
| **Condition** | in-memory delegate | the Rule profile plus `old` / `new` / `changed(field)` | hook and automation conditions |

Two restrictions fall out of this split, and both catch a class of otherwise
silent bug:

- **`old` / `new` / `changed()` are rejected in the Rule profile.** A row-level
  predicate runs against the *stored* row in a `WHERE` clause, where `new` does
  not exist. Without the restriction such a rule would either fail at request time
  or, worse, evaluate as NULL and quietly deny (or admit) the wrong rows.
- **`@user` / `@tenant` are rejected in the Computed profile.** A generated column
  must be deterministic. The database would refuse it anyway — but with an opaque
  engine error instead of ours with a fix suggestion.

This is also why **#21 sequences after the compiler exists**: `computed` is not a
separate feature, it is the third profile of this compiler.

### Default-deny, concretely

A missing `entity.rules.<operation>` denies. Allowing everyone must be written
out (`"true"`). The descriptor already carries the shape (`AccessRules` with five
nullable strings), so this is purely compilation semantics.

### Field-level `hidden` / `readOnly`

`FieldDescriptor.Hidden` and `FieldDescriptor.ReadOnly` are `BoolOrCel`. In F3
they are restricted to **context-only expressions — no row fields**. They are
evaluated once per request; a `hidden` field then never enters the `SELECT` list
at all. Row-dependent masking (`hidden: "owner_id != @user.id"`) would by
definition require post-processing the result set, which is precisely what the
"no in-memory post-filter" invariant forbids. It is rejected at apply with a
structured error and deferred.

### How the security properties are proved

- Identifiers are never taken from the expression text — they are resolved against
  `EntitySchema` to a quoted column name. An unknown field is an error at apply.
- Every literal leaves as a **named parameter**. A CsCheck property test asserts
  that for any generated CEL input, no literal from that input appears in the
  emitted SQL.
- Type checking against `EntitySchema` at apply (comparing a string field to a
  number errors there, not on the first request).
- Golden CEL→SQL snapshots per engine (Verify), the adversarial suite, and a
  Stryker gate over `Expressions` + `Rules`.

## Data API

**Query model.** `AlvoQuery` lives in `Abstractions` and has no ASP.NET
dependency: filters as `(field, operator, value)` triples over an **operator
allow-list** (`eq neq gt gte lt lte like ilike in is`), plus sort, projection and
paging. The HTTP layer parses PostgREST-compatible syntax
(`?status=eq.open&order=created_at.desc`) into that model. The parser is the only
place where text becomes structure, and both the field and the operator are
validated against `EntitySchema` *before* anything reaches SQL.

**Paging.** Keyset by default (`?after=<cursor>`), offset as an opt-in with a hard
ceiling. The cursor is opaque base64 over `(sort key, id)`. It is not signed, but
it is validated against the current sort so it cannot be used to smuggle a
different ordering.

**Schema-derived validation** before persistence: required, types, `maxLength`,
`precision`/`scale`, enum values, formats, foreign-key existence. The response is
RFC 7807 carrying **all** violations, not the first one, with an
`alvo.dev/errors/...` type.

**Idempotency.** An `alvo.idempotency` table keyed `(key, endpoint, request hash)`
storing the status and response body, written in the same transaction as the
operation. The same key with a different body is a 409, not a silent replay. It is
a fixed framework table and is created by the `SystemSchemaInitializer` runner,
like the outbox.

**Field-level at the HTTP boundary.** `hidden` fields drop out of the projection
(and therefore out of the `SELECT`); a write to a `readOnly` field is rejected
with 422 rather than silently ignored — for an agent, a silent drop is worse than
an error.

**OpenAPI 3.1** is emitted from the same `SchemaModel` that generates the routes,
so the document cannot drift from the implementation. This also unblocks #26
(Vacuum contract linting), which today has nothing to lint. Scalar is wired in
the host (PR4), because serving docs is a hosting concern.

## Events, hooks, automation

**Outbox.** `alvo.outbox` (`id`, `sequence`, `event_type`, `payload`,
`provenance_depth`, `claimed_at`, `claimed_by`, `attempts`, `dispatched_at`). The
row is written on the **same `DbTransaction`** as the data change — not a separate
`SaveChanges`, or the guarantee does not hold. The dispatcher is a
`BackgroundService`: claim a batch (`FOR UPDATE SKIP LOCKED` on PostgreSQL;
serialized on SQLite, whose single-writer cap is documented anyway), deliver, mark
dispatched. Delivery is **at-least-once**, so every after-side action must be
idempotent or deduplicated by event id — a property to be tested, not just
documented.

Alvo owns this outbox; the core takes no foreign dependency for it. `IEventDispatcher`
leaves Wolverine or an external bus available later as an adapter package.

Like `descriptor_versions`, `alvo.outbox` is a **fixed framework table**, not
something the declarative diff engine produces. It is created by the same
`SystemSchemaInitializer` runner #18 established (portable
`CREATE TABLE IF NOT EXISTS`, no per-engine branching) — extending that runner,
not adding a second mechanism.

**Payload** carries `record` + `old_record` + the list of changed columns, which
makes `changed(field)` in the Condition profile cheap. Subscriptions support
wildcards (`entity.orders.*`). **Bulk operations coalesce** — per-item versus batch
is declared per rule and a batch has its own event shape
(`entity.orders.created.batch`); importing 10k rows must not emit 10k events.

**Loop protection.** `provenance_depth` rides on the event, capped at ~5, plus
cycle detection and an alert. Not a blanket ban on chaining.

**Hooks.** A before-hook runs in-transaction under a time budget and can only
`reject` or `mutate`. On the declarative face this is **structural** —
`BeforeHookAction` in the descriptor has exactly those two properties, so a
network action cannot be expressed. On the C# face (`IEntityHooks`) it cannot be
enforced structurally; there it is an analyzer plus the budget, i.e.
defense-in-depth. This spec states the difference rather than claiming
impossibility for both. After-hooks run post-commit from the outbox, where the
network is allowed.

**Automation minimal.** ECA over outbox events (an `event` trigger + a CEL
condition + `webhook` / `email` / `entity.update` actions), a cron `schedule`
trigger in UTC, and an `IEmailSender` with a console dev provider and SMTP.
No HMAC, DLQ or redelivery UI — that is 7.1.

## Computed & rollup

`computed` unwinds the deferral checklist recorded on issue #21: the mapper and
the validator stop rejecting it, `FieldSchema.ComputedExpression` is repopulated
from **compiled, validated** SQL, and `DescriptorModelBuilder` emits
`GENERATED ALWAYS AS (…) STORED` from that compiled SQL — never from raw
descriptor text. The `complex-crm` fixtures become applyable rather than merely
schema-valid.

`rollup` is a transactionally consistent recompute of the parent aggregate inside
the write transaction that changed a child. The descriptor already carries the
shape (`Rollup` with `from` / `op` / `field` / `via`), so this issue supplies the
maintenance mechanics, not a new declaration. It is deliberately **not** a hook: a
parent-sum maintained by user code is the classic lost-update race. The race test
is the DoD.

This lands last because it needs the compiler (PR1) *and* the in-transaction write
pipeline that the hooks work establishes (PR5).

## Code organization

By the decision rule fixed in the #18 design and in
`docs/architecture/vertical-slice.md`: **triggered through one entry → VSA slice;
a mechanism other code calls → capability namespace with `Internal/`.**

This milestone is almost entirely mechanism code, so — as in #18 — there are
**no VSA slices**. The Data API is explicitly the documented exception: it is
*generated*, so the "slice" is the generator plus the pipeline it drives
(schema registry → route mapping → data port → rule engine → event backbone), and
that generator is one feature (`MMLib.Alvo.Api`), never a folder per entity. Each
feature owns a `Setup.cs` with `Add<Feature>` / `Map<Feature>` and is registered
explicitly — no assembly scanning.

## Package layout

Only one new project: **`MMLib.Alvo.Host`** (PR4) — the standalone host, earned as
a distribution artifact rather than a namespace. Everything else is a namespace in
the core.

The core gains `FrameworkReference Microsoft.AspNetCore.App`. The shared
architecture test permits this: it bans EF Core and Npgsql references and any
`MMLib.Alvo.*` reference other than `Abstractions`; ASP.NET Core is neither, and
`app.MapAlvo()` requires it. `Abstractions` stays on
`Microsoft.Extensions.DependencyInjection.Abstractions` alone — no port defined
here touches ASP.NET.

`docs/architecture/package-boundary.md` must be updated with `MMLib.Alvo.Host`
when PR4 lands (the doc asks to be kept current).

## Testing strategy

Same test types as #18 — nothing new is invented.

| Type | Covers |
|---|---|
| Contract tests per port + in-memory fakes | `IAlvoData`, `IPolicyEngine`, `IOutboxStore`, `IEntityHooks`; the fakes ship in `MMLib.Alvo.Testing` alongside `InMemorySchemaMigrator` |
| **Adversarial suite** | two-user, two-tenant, default-deny — written **red in PR1**, green in PR2, run on SQLite + PostgreSQL |
| Property test (CsCheck) | the translation never interpolates input |
| Golden snapshots (Verify) | CEL → SQL per engine; the OpenAPI document |
| Integration (Testcontainers) | the same CRUD suite against real PostgreSQL |
| Crash test | kill between commit and publish → the event is delivered after restart |
| Race test | concurrent child changes leave the parent rollup consistent |
| TeaPie E2E | black box over the running compose stack (PR4) |
| Stryker | `Expressions` + `Rules` namespaces |

`TeaPie.Tool` is installed as a local dotnet tool and its agent skill lives in
`.claude/skills/teapie/`.

## PR split

| PR | Content | Closes |
|---|---|---|
| **1** | `AlvoContext` + dev auth / tenant resolution · CEL parser + AST · the three profiles · `IPolicyEngine` · adversarial suite written red | `[15a]` |
| **2** | `IAlvoData` + EF implementation · policy in the `WHERE` clause · adversarial suite green on SQLite + PostgreSQL | #20 |
| **3** | generated HTTP Data API · filters / sort / paging · schema-derived validation · RFC 7807 · `Idempotency-Key` · OpenAPI 3.1 | — |
| **4** | `MMLib.Alvo.Host` + docker-compose + TeaPie + Scalar UI | #19, `[15b]` |
| **5** | outbox · events · hooks · minimal automation · crash test | #22 |
| **6** | `computed` (unwind the deferral) · `rollup` · race test | #21 |

PR4 starts #24 but does not close it — the published image and the full standalone
story stay in F4.

PRs 1, 2 and 5 touch the security core: `/security-review` plus the
`alvo-security-core-review` checklist, not only `/code-review`.

## New issues to open

The convention of a lettered suffix on the bracketed step number already exists in
the repo (`[1b]`, `[3b]`, `[3c]`, `[4b]`, `[21b]`).

- **`[15a]` Caller context + minimal dev auth (API key) + tenant resolution** → F3.
  Nobody owns this today, and both #20 and #19 are blocked without it.
- **`[15b]` OpenAPI 3.1 emission + Scalar docs** → F3. The design brief decided it
  ("publish OpenAPI 3.1 + Scalar docs"), no issue carries it, and #26 (Vacuum
  contract linting) silently assumes the document exists.

## Scope / YAGNI — explicitly out

Full auth flows (#36 — F3 gets an API key and nothing more); RBAC (#37); tenancy
beyond the row-level discriminator (#40 — the resolution port is minimal on
purpose); realtime (#38); the dynamic-entity store (#41 — the model accommodates
it, the store is not built); SQL Server (enabled after PostgreSQL is green, still
inside F4); JSONata (after-side transforms, 7.1); webhook HMAC / DLQ / redelivery
UI (7.1); csx scripting (#34); the Management API's HTTP endpoints; the published
Docker image (#24's back half); row-dependent field masking; signed pagination
cursors.

## Assumptions (veto candidates)

1. Own outbox in the core with an `IEventDispatcher` port, rather than taking
   Wolverine as a core dependency. **Decided with the maintainer**; the cost is
   hand-written claim / retry / poison-message handling.
2. Minimal dev auth *and* minimal tenant resolution land in F3 rather than being
   stubbed. **Decided with the maintainer** — the adversarial suite is
   unwritable without them.
3. `UserId` / `TenantId` wrap `Guid`, consistent with the `uuid` managed columns
   already emitted by the mapper.
4. The host and compose are pulled forward into F3 so #19 closes against its
   literal DoD. **Decided with the maintainer**; PLAN.md already says F4 runs in
   parallel with F3.
5. `AlvoContext` as a required parameter rather than an ambient accessor. The
   verbosity is accepted in exchange for a testable invariant.
6. One hand-written CEL parser with three profiles, rather than a separate
   expression language for computed fields.
7. Six PRs. PR1 closes no issue on its own; that is accepted, because splitting the
   security core out of the data port is the whole point of the ordering.

## Verification

- PR1: the adversarial suite exists and is red for the right reason; the property
  test passes against the compiler; unknown fields and out-of-profile nodes error
  at apply with a fix suggestion.
- PR2: the adversarial suite is green on SQLite and PostgreSQL; a query issued
  without a context throws; the generated SQL contains the policy predicate (a
  snapshot proves it is not a post-filter).
- PR3: CRUD over `vehicle-registry` on both engines; a validation failure returns
  every violation; a replayed `Idempotency-Key` does not duplicate; the OpenAPI
  document matches the mapped routes.
- PR4: `docker compose up` yields a working backend from the descriptor alone, and
  `teapie test` is green against it.
- PR5: the crash test loses no event; a before-hook rejects and mutates; an
  after-hook runs post-commit; the ECA rule + cron + email path works end to end.
- PR6: `total = unit_price * amount` is a real generated column on both engines;
  the rollup race test is green; every box on #21's deferral checklist is ticked.
