# F2 — Schema Foundation — Design

**Date:** 2026-07-20
**Milestone:** F2 — Schema (foundation), [milestone #3](https://github.com/Burgyn/MMLib.Alvo/milestone/3)
**Issues:** #16 (descriptor JSON Schema — finalization), #17 (testing against the
schema), #57 (dynamic-entities accommodation)
**Branch:** `feat/descriptor-schema-v1`

## Goal

Finalize the descriptor JSON Schema as the product's first real API and stand
up the four-type test mechanism that everything else is measured against —
without foreclosing the dynamic (second) schema-registry driver. The schema is
the one artifact every path shares (mount / CLI / Management API /
`FromDescriptor()` / admin export), so the decisions below are the ones that
are expensive or impossible to retrofit: document identity, entity/field
identity, layout, extensibility, and the dynamic-entities touchpoints.

## Research base

Three research passes inform every decision here (full reports in the session,
key citations inline):

1. **Prior art** — PocketBase, Supabase, Hasura v2/v3, Directus, Strapi,
   Appwrite, Amplify Gen 2, Dataverse, Salesforce, Kubernetes CRDs.
2. **JSON Schema 2020-12 practice** — OpenAPI 3.1 meta-schema conventions,
   SchemaStore, VS Code behavior, .NET validator landscape.
3. **Schema evolution & dynamic-entity architectures** — Atlas, Prisma, EF
   Core, Terraform, kubectl apply; Dataverse elastic/virtual tables,
   Salesforce MT_Data/pivot tables, modern JSONB practice.

The recurring failure modes the design guards against: no format version
(PocketBase v0.23 broke every export), name-as-identity renames (Prisma/EF/
Strapi rename = drop + add = data loss), deletion inferred from absence
(Strapi boot-time auto-sync), auth rules living outside the descriptor
(Strapi/Directus), observed state mixed into desired state (Appwrite), and a
dynamic driver that visibly degrades vs the physical one (Dataverse elastic
tables' long limitation lists).

## Decisions

### D1 — Document identity: `apiVersion` + versioned URL

- New **required** top-level field `apiVersion` with value `alvo.dev/v1`
  (schema-enforced via `const`). The K8s pattern: documents are
  self-describing because they get copied without `$schema`, stored as DB
  records, and the loader must dispatch a parser before fetching anything.
- The existing `version` integer (optimistic-concurrency counter of project
  content) is renamed to **`revision`** so format version and content revision
  can never be confused.
- `$schema` stays an allowed property in instances (editor convention).
- Evolution policy (binding): within v1 only additive changes — new optional
  properties, new enum values, loosened constraints. Breaking changes mean
  `/v2/` at a new URL; v1 stays published indefinitely. Anything removed is
  first marked `deprecated: true` with the replacement named in `description`
  for at least one minor cycle.

### D2 — Entity/field identity: names + explicit `renamedFrom`

- The entity/field **name stays the identity** (the object key). Renames are
  declared, never guessed: an optional `renamedFrom` string on an entity and
  on a field, with Terraform `moved`-block semantics — safe to leave in the
  file permanently (ignored when the source no longer exists), an error when
  source and target both exist as distinct things.
- The registry keeps **internal stable IDs** for its own bookkeeping; they
  never appear in the descriptor (no write-back friction, no ID noise in
  hand-/agent-written files, no merge conflicts over generated IDs).
- Binding constraint for the F4 diff engine (recorded now, implemented then):
  the differ never infers a rename heuristically; a drop without
  `renamedFrom` is a destructive change requiring explicit approval, and the
  structured error suggests `renamedFrom` as the fix. Every tool that guessed
  renames (EF Core, Prisma) shipped a data-loss footgun; every tool that
  separated identity from display (Dataverse, PocketBase, Terraform) did not.

### D3 — Layout: one canonical document + a split convention

- The descriptor **is** logically one JSON document. The published schema
  validates exactly that document; export and the Management API return
  exactly that document.
- Physically it may be split into a conventional directory:
  `project.json` + `entities/<name>.json` (file content = the entity object,
  file name = the entity key) + `automation/<name>.json` +
  `functions/*.csx`. The loader composes them deterministically; a duplicate
  definition is an **error**, never last-write-wins (the Hasura v2 lesson).
  Single-file stays fully supported (everything inline except `.csx`).
- The loader/composer is F3/F4 work; F2 records the convention and ships a
  split-layout example.

### D4 — Extensibility: `x-` keys, strictness everywhere else

- `patternProperties: "^x-"` (any JSON value) is allowed at the project,
  entity, field, and automation-rule levels. Alvo ignores these keys but
  **guarantees passthrough** through apply → export (the round-trip suite
  enforces it). Use cases: embedded-host UI hints, tool provenance.
- Everything else stays `additionalProperties: false` — a typo in a real
  keyword still fails loudly (it does not carry the `x-` prefix), which
  preserves the agent-first "fail loud, not silent" principle.

### D5 — Dynamic-entities touchpoints (#57)

- Project-level `dynamicEntities` object: `enabled` (bool, default false),
  `namePrefix` (identifier prefix reserved for runtime user-created entities —
  the Dataverse publisher-prefix / Salesforce `__c` collision guard against
  descriptor-defined entity names), and an optional `maxEntitiesPerTenant`
  integer quota. Further quota knobs are added additively when F7 shows they
  are needed.
- Entity-level `storage: "physical" | "dynamic"` (default `physical`). A
  descriptor-defined entity may live in the dynamic store — no DDL, instant
  apply — which makes the entity model driver-agnostic in the schema itself
  and lets the parity claim ("same entity, other driver") be exercised by the
  examples and snapshot suites from F3/F4 on.
- Parity by construction: the field type system, `computed`, `rollup`, `ref`
  (incl. `onDelete`), `unique`, and `index` are **not** conditioned on
  `storage` anywhere in the schema. Field-level `index` is already
  driver-neutral (physical index / generated column + JSON-path index).
- Binding F3 design constraint (AC of #57): no code path above the registry
  branches on "is this a real table?". The dynamic store itself (shared
  partitioned `entity_records`, edge-table referential integrity, JSON-path
  index implementation) remains F7 (#41).

### D6 — Finalization fixes to the draft schema

Defects and inconsistencies found in `docs/product/alvo-descriptor.schema.json`
during review, fixed as part of #16:

1. `rollup.field` is `required` while its description says "omit-able for
   count" — contradiction. Fix: `if op == count then field optional`.
2. The `eventPattern` regex rejects `entity.orders.*` (last segment is
   `[a-z]+`, so a trailing wildcard cannot match) and cannot express the
   batch shape `entity.orders.created.batch` — both are guaranteed by the
   design brief. Fix the regex; add `examples`.
3. `automation` is an array with a `name` property — inconsistent with
   `entities` / `webhooks` / `functions` (name-keyed maps) and duplicate
   names slip through schema validation. Fix: name-keyed map.
4. The `inboundWebhook` trigger references an "inbound endpoint name" that
   has no home in the schema (`webhooks.endpoints` are outbound). Decision:
   **drop the trigger from v1** — inbound webhooks are F7.1 and re-adding a
   trigger variant later is additive and cheap.
5. `decimal` only *allows* `precision`/`scale`; the spec demands a
   conditional **requirement** (deterministic DDL). Fix: required when
   `type: decimal`.
6. Authoring polish: `title` on `$defs` entries, `examples` on non-obvious
   properties, `oneOf` + `const` discriminators for trigger variants (actions
   already have them), descriptions kept on every property, no `$dynamicRef`.
7. `rollup.via` is missing from the `rollup` `$defs` (present only in prose) —
   add it (M4b).
8. `automationRule.name` is still `required` after the array→map conversion
   (D6.3) — the map key is the name; drop the redundant inner field.
9. `entity.update` action `payload` is a plain object with no statement on
   whether values may be expressions — align it with M1 (`literal | {$cel}`
   per value) and document `{{…}}` sugar there explicitly.
10. Reconcile `field.default`: the draft doc lists only `now()` /
    `gen_random_uuid()`, but M1 makes `default = literal | {$cel}`. State that
    the `$cel` default context includes `@user`/`@tenant` (so `created_by`
    audit defaults work) and is evaluated at insert time.

### D7 — Publishing

- The schema moves from `docs/product/alvo-descriptor.schema.json` to
  **`schema/project.schema.json`** (issue #16 "take over"); the old location
  is replaced by a pointer.
- `$id` stays `https://alvo.dev/schema/v1/project.json` as the canonical
  identifier (a JSON Schema `$id` is an identifier, not necessarily a
  fetchable URL). Physical hosting: a CI job publishes `schema/` to GitHub
  Pages on merge to main — `https://burgyn.github.io/MMLib.Alvo/schema/v1/project.json`
  for now. **Deferred decision:** the final domain (candidate
  `alvo.burgyn.online`, spec says `alvo.dev`) is chosen before v0.1; if it
  changes, `$id` changes with it — harmless pre-release. SchemaStore
  submission (`catalog.json` entry + `fileMatch`) happens after v0.1, not now.
- Hosting requirements recorded for the final home: HTTPS, `Access-Control-
  Allow-Origin: *`, `application/json`, short cache on the rolling `v1`
  alias.

### D8 — Validator and codegen: Corvus.JsonSchema

- **Corvus.JsonSchema** (Apache-2.0, endjin): full 2020-12 support, actively
  maintained, fast, and offers source-generated C# types from the schema —
  aligned with interface-first (the schema is the single source of truth).
- **JsonSchema.Net (json-everything) is excluded**: since 2026-02 its NuGet
  binaries carry an "Open Source Maintenance Fee" EULA — exactly the
  licensing ambiguity the `alvo-dotnet-conventions` rule exists to avoid.
  Newtonsoft Json.NET Schema (AGPL/commercial) and NJsonSchema (draft-4 core)
  are also out.

### D9 — Testing mechanism (#17): four types, two maturity levels

F2 has no apply/export and no generators (DB schema, OpenAPI) — those are
F3/F4. Each test type therefore defines what runs **now** and what it grows
into **later**; nothing pretends to test code that does not exist.

| Type | F2 (runs now) | F4+ (grows into) |
|---|---|---|
| 1. Meta-validation | schema valid against the 2020-12 meta-schema (Corvus) + authoring lint as tests: every property has a `description`, every `$defs` entry is referenced, no accidental open objects | unchanged |
| 2. Examples | positive corpus under `examples/` + negative corpus asserting the **expected error pointer** (`instanceLocation`), not just invalidity | demo descriptor joins the corpus |
| 3. Round-trip (CsCheck) | random valid descriptor → validates → parse → canonical serialize → structurally equals canonicalized input; mutation property: corrupt a valid descriptor → fails with a pointer at the mutation site | full `apply → export == input` once apply/export exist |
| 4. Snapshot (Verify) | structured validation-error output over the negative corpus (error-message regression) + canonical serialization of the examples | generated DB schema and OpenAPI snapshots |

**Positive example corpus (minimum):** minimal descriptor; the CRM example
(analysis §16, **adapted to v1** — tagged `$cel`, `tenancy`, `templates`,
`rollup.via`, `softDelete`/`audit`); a dynamic-entities example with
**governance** (`dynamicEntities.enabled` + `defaultRules` + `allowedFieldTypes`
+ quotas, plus a `storage: "dynamic"` entity with `index: true` and a
physical→dynamic `ref` — the #57 acceptance surface); a tenancy example with a
`global` reference-data entity; a batch-delivery automation example; a
field-level `hidden: {$cel}` (per-role masking) example; a `computed`-reads-
`rollup` example; a split-layout example; `renamedFrom` and `x-` examples.

**Canonical form (defined now, needed by F4 export):** structural comparison
ignores object member order; export emits a deterministic order (schema
declaration order); properties equal to their schema default are omitted on
export; `x-*` keys pass through verbatim.

**Infra:** new test project `test/MMLib.Alvo.Schema.Tests` (xUnit v3 on MTP,
CsCheck, Verify, Corvus.JsonSchema). All four F2-level types are fast and run
in ring0. The GitHub Pages publish job covers the #16 "published" DoD.

## Modeling power — content decisions (M1–M6)

The schema is not just a well-versioned contract — it must be able to *model
the product*. The CRM scenario (analysis §16), the vehicles demo, and the ERP
dynamic-entities scenario were walked line-by-line against the draft; six
modeling gaps must be fixed in v1 because they are semantic (not additive),
and a documented additive policy covers the rest.

### M1 — Literal vs expression: tagged `{"$cel": "…"}`

In value positions (before-hook `mutate` values, field `default`, email `to`,
`entity.update` payload values) a plain string was "value **or** CEL" —
indistinguishable, and exactly the class of silent-wrong-backend ambiguity
that kills agent reliability. Decision: **all plain JSON values are
literals; an expression is always the explicit object `{"$cel": "…"}`**
(schema: `oneOf` literal | cel-object). Positions that are purely
expression-typed today (rules, conditions, `computed`, `validation`) stay
bare strings — no ambiguity exists there. `{{…}}` interpolation remains only
as sugar in after-side text fields (`to`, template bodies), defined as sugar
over JSONata. Consequence: `default: now()` becomes
`default: {"$cel": "now()"}`.

### M2 — `users` is a reserved system entity

`ref` may target the reserved name `users` (the built-in auth entity) — the
§16 CRM example (`owner_id → users`) is otherwise unexpressible. Declaring an
entity named `users` in the descriptor is an error (reserved). Extending the
user profile with custom fields is additive later (`auth.users.fields`).

### M3 — Tenancy is descriptor-visible

Project-level `tenancy: { enabled: bool }` declares that the backend is
multi-tenant (definition, not infrastructure); per-entity
`tenancy: "scoped" | "global"` with default **scoped** when tenancy is
enabled — shared reference data (číselníky) is the exception and must opt
out explicitly, in the default-deny spirit. Tenant *resolution*
(subdomain/header/claim) stays host/env — the descriptor ≠ infra boundary.
Tenant profile fields (`@tenant.country`) are additive later
(`tenancy.fields`). This lands in v1 so the F3/F4 registry, CRUD, and
two-tenant adversarial suite design see the tenant dimension in the model
from the start (the #57 lesson applied to tenancy).

### M4 — `rollup.via`

Optional identifier of the FK field on the child entity; required (enforced
fail-fast at apply) only when the child has more than one `ref` to the
parent. Also fixes the `count` contradiction (D6.1).

### M4b — `rollup.via` is actually in the schema JSON

The as-designed `rollup.via` (indirect/disambiguating FK) must land in the
committed schema's `rollup` `$defs` — the draft's `rollup` has only
`from/op/field/where`. Without it, two-FK rollups (directional
follower/following counts, payment-via-allocation sums) are unbuildable. This
is a finalization fix, not a new idea.

### M5 — `delivery: "perItem" | "batch"` on automation rules

The brief guarantees per-rule batch coalescing ("a 10k-row import must not
emit 10k events"). Default `perItem`; `batch` delivers the actions an array
payload and matches the `entity.*.created.batch` event shape (regex fixed in
D6.2).

### M6 — `templates` section

Name-keyed map: `{ "subject": "…", "body": "…" }` with `{{…}}`
interpolation, or `bodyFile` (bundle-relative path, consistent with `.csx`).
Gives the `email` action's `template` reference a home; the action's `data`
(JSONata) remains the template input.

### M7 — Dynamic-entities governance (#57 hardening)

The stress test found the original `dynamicEntities` toggle
(`enabled`/`namePrefix`/`maxEntitiesPerTenant`) **forecloses secure-by-default**:
runtime user-created entities have no `entities.<name>` node and therefore no
`rules` block, so they are either unreachable (default-deny) or wide open, and
the host embedding Alvo cannot express any governance. `dynamicEntities` gains:
- `defaultRules`: { list,get,create,update,delete: CEL } — the default-deny
  bridge applied to every runtime-created entity (a virtual entity must carry
  policy exactly like a physical one carries `rules`).
- `allowedFieldTypes`: string[] — the type subset end-users may use.
- `maxFieldsPerEntity`, `maxRecordsPerEntity` — per-entity quotas.
- `defaultTenancy`: "scoped" | "global" (default scoped).
- `ref.entity` may name a dynamic/late-bound target so a physical entity can
  reference a runtime one (cross-driver ref), with `onDelete` documented as
  registry-level (app-enforced for the dynamic side), not a physical FK.

This turns the #57 acceptance from "a toggle exists" into "the host can express
the security policy over user-created entities" — the actual invariant.

### M8 — Security-semantic helpers pulled into v1

These are additive keys, but bolting them on *later* silently changes the
read/security semantics of existing descriptors, so they belong in v1:
- **`entity.softDelete: true`** — a framework-managed `deleted_at`;
  reads/`list`/`get` and `rollup` auto-exclude soft-deleted rows; a restore op.
  (Adding this later would change what every existing `list` returns.)
- **`entity.audit: true`** — injects managed `created_at/by`, `updated_at/by`.
- **`field.hidden` / `field.readOnly` widen from `bool` to `bool | CEL`** — a
  CEL expression over `@user` gives per-role field visibility/mutability (PHI
  masking, "only compliance sees SSN"). Widening `bool`→`bool|CEL` now is
  non-foreclosing; the full `rules.fields` map stays additive-later. This is
  the v0.1 answer to per-role field access (previously punted entirely).

### M9 — `computed` may reference `rollup`; ladder clarified

The stress test surfaced `order.total = sum(items) − discount + tax` — a derived
value mixing a rollup with same-row arithmetic. The ladder must state that
`computed` **may reference `rollup` fields** (a rollup materializes as a
maintained column; a computed reading it re-evaluates when the rollup changes),
and that where a target engine forbids generated-column-on-generated-column,
the framework maintains the value rather than emitting an invalid DDL. This is
a semantic clarification of the existing ladder, decided now to avoid an
under-specified `computed`.

### Additive policy (documented, deliberately not in v1)

Safe to add within v1 (new optional keys / new enum values), recorded so they
are designed *for*, not stumbled into: field types `float`, `bigint`, `file`
(F7.7 storage), array/multi-select; first-class many-to-many (v1 documents
the junction-entity pattern); the full per-role field-level `rules.fields` map
(the `hidden`/`readOnly` CEL widening in M8 is the v0.1 answer); RBAC `teams` +
`@user.claims.*`; partial/directional indexes; inbound webhooks (D6.4);
`rollup.groupBy` (per-vendor/per-rate subtotals); a `sequence` field kind
(gapless numbering); a `stateMachine`/`workflow` block; a `rank`/position
field; a `money` composite type; enum-with-metadata + entity `seed` data;
per-endpoint `subscribedEvents`/`paused`/`payloadMode`; automation action
types `entity.delete`, `push`, `sms`, `emit`, `delay`, and a per-action
`condition` + data-chain. Each is genuinely additive; none is foreclosed by v1.

## Binding constraints for F4/F7 (from the modeling stress test)

~42 hypothetical backends across 6 families (SaaS/collaboration, commerce/
fintech, content/social, ERP/dynamic, automation/integration, auth/RBAC) were
modeled against the v1 surface. The schema is strong for its declared v0.1
scope (single-owner physical CRUD, rollups, ownership rules, workflow gating,
tenant row-isolation — §16 invoicing, feature-flags, expense/budget are FULLY
expressible). Three capability ceilings recurred across families. **All three
are F4 rule-engine / pipeline capabilities carried *through* the descriptor as
opaque CEL/action content — not descriptor *shape* — so F2 does not build them;
it records them as binding so F4 cannot foreclose them, exactly as this spec
already does for the diff engine.**

- **C1 — Rule engine must reach across relations (ALL 6 families).** The single
  biggest finding: `rules.*` CEL currently sees only same-row columns +
  `@user`/`@tenant`. Relationship-based authorization (board/channel
  membership, org-scoped roles, per-document data-driven ACL, owner-via-
  relation, ticket-oversell, seat/quota) is the norm, not the exception. F4's
  CEL compiler **must** support a bounded `exists(<entity>, <alias>,
  <predicate>)` compiling to a correlated `EXISTS`/JOIN, and ref-path reads
  (`new.coupon.value`, `new.ticket_type.remaining`). Recursion (folder-tree
  inheritance, transitive manager→reports) is a deeper, separately-tracked
  hole (materialized-path pattern is the likely answer). The descriptor holds a
  CEL string, so this does not change v1 shape — but F2 asserts the schema must
  not assume single-row rules anywhere, and the parity/adversarial suites must
  exercise a relation-traversing rule.
- **C2 — Pipeline must allow in-transaction multi-entity / conditional writes
  (5/6 families).** Inventory decrement, double-entry ledger dual-post,
  reserve-N-rows, atomic conditional decrement (last unit), a before-hook
  calling a deterministic function (VAT). Today before-hooks are same-row CEL
  reject/mutate and the only cross-entity write is after-side (post-commit,
  at-least-once, single record). F4 **must** provide an in-transaction
  side-write (a before-side `entity.create`/`entity.mutate` scoped to a ref
  target, bounded, no network) and a whitelisted in-tx function call; the
  action catalog gains these slots additively.
- **C3 — Scheduled per-record set-processing + one-shot delay (5/6 families).**
  "On a schedule, for each row matching a filter, act" (SLA breach, STK-expires
  reminder, scheduled publishing, rent generation) and per-record one-shot
  delays (dunning retry, drip day 0/3/7, escalate-in-48h). The `schedule`
  trigger gains `entity` + `where` (feeding `perItem`/`batch` delivery), and a
  relative `delay`/`runAt` — additive shape, backed by Wolverine's native
  scheduled delivery. Recorded so F4 builds the scheduled path against a
  record-set, not a contextless cron.

**Correctly deferred to F7, confirmed additive-safe and non-foreclosing:**
storage end-to-end + `file` type (F7.7 — the single largest missing pillar; a
whole app family, DAM/file-sharing, is not v0.1-buildable, stated plainly);
inbound webhooks + custom app events + workflow chaining/branching (F7.1);
RBAC teams/org-scoped roles/claims (F7.5); realtime presence/broadcast/
subscription-authz config (F7.6); full auth flows — MFA/magic-link/invitations/
per-provider OIDC (F7.4); per-tenant config/branding/BYO-provider/lifecycle +
cross-tenant platform admin (F7.8). None requires a v1 shape change to add.

**Note:** the §16 CRM example in `baas-analyza.md` predates M1/M3 (bare CEL
strings in `mutate`, no tenancy section). The `examples/` corpus carries the
**adapted, canonical** CRM descriptor; updating the analysis text is a
follow-up outside F2 (it would trigger `alvo-regen-brief`).

## PR plan (final cut decided in the implementation plan)

- **PR1** — schema v1 (all D1–D8 + M1–M9 changes, incl. the 10 finalization
  fixes in D6) + `examples/` + test types 1–2 (they are the proof of
  finalization). Closes **#16 + #57**.
- **PR2** — canonical form + round-trip property + snapshot suites. Closes
  **#17**.
- `alvo-plan-guard` dispatched before each PR, per the hard rules.

## Out of scope (explicitly)

- The diff/apply engine, plan taxonomy (additive / data-dependent /
  destructive), three-way drift detection — F4 (recorded here only as binding
  constraints D2/D5 impose on it).
- The dynamic store implementation (`entity_records`, edge tables, JSON-path
  indexes) — F7 (#41).
- **The F4/F7 capability work behind C1–C3** (relation-reach CEL, in-tx
  multi-entity writes, scheduled set-processing) — implemented later; F2 only
  records them as binding and keeps the shape non-foreclosing.
- Storage + `file` type, inbound webhooks + custom events + workflow
  chaining, RBAC teams/claims, realtime presence/broadcast, full auth flows,
  per-tenant config/lifecycle — all F7, all additive-safe (see the deferral
  list in "Binding constraints").
- The additive helpers listed in the additive policy (`sequence`,
  `stateMachine`, `rank`, `money`, `rollup.groupBy`, enum metadata, `seed`,
  array type, extra action types) — added within v1 when a real need lands;
  none foreclosed.
- SchemaStore submission, final domain.

## Verification

- All four F2-level test types run red/green in CI; deliberately breaking the
  schema (meta), an example (type 2), canonicalization (type 3), or an error
  message (type 4) each turns exactly the right suite red.
- Every #57 acceptance criterion is satisfiable by pointing at: the
  `dynamicEntities` + `storage` schema surface, the dynamic-entities example
  in the corpus, D5's model constraint, and the F3 constraint sentence.
- Publishing (`$id` URL served over HTTPS/CORS) is a **tracked follow-up** —
  the domain is deferred (`alvo.burgyn.online` candidate), so the GitHub Pages
  workflow lands separately once the host is chosen. `$id` already carries the
  intended canonical URL; the schema is finalized and validated regardless.
- **Modeling-power proof:** the §16 CRM scenario, the vehicles demo, and the
  ERP dynamic-entities scenario are each fully expressible in v1; the adapted
  CRM descriptor validates as part of the examples corpus (type 2).
- **Stress-test binding constraints (C1–C3) are recorded**, so the F4
  rule-engine and pipeline design cannot ship a single-row-only engine, a
  before-side with no in-transaction side-write, or a contextless scheduler
  without failing this spec's stated constraints. The F7 deferrals are
  confirmed additive-safe (adding any of them is a new optional key / enum
  value, never a breaking change).
