# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed (breaking)

- **A descriptor may no longer name a field `order`, `limit`, `offset`, `after`, `select`,
  `or`, `and` or `not`.** The generated Data API's query string reserves each of these
  (`?limit=10`, `?or=(...)`, `?not.color=eq.red`), so a request could not tell a filter on
  such a field from the parameter itself. The descriptor is now **rejected when it is
  applied**, with an error naming the entity, the field, the full reserved list and
  `Rename the field`; previously such a descriptor applied and then failed when routes were
  mapped — or, for an embedded host that never maps the Data API, was never refused at all.
  `order` in particular is a plausible business field name (an `orders` entity with an
  `order` column is not exotic), so this will hit real descriptors. Rename the field; there
  is no opt-out, because the ambiguity has no correct per-request resolution.
  `schema/project.schema.json` documents the exclusion on the `fields` description — the
  JSON Schema pattern cannot express it, so it is stated there rather than validated.

- **A descriptor is now rejected at apply when it declares a feature this build does not honour**,
  rather than applying and silently dropping it. The rule: refuse what silently produces wrong data;
  tolerate what an author can observe the absence of. Each refusal names the entity, the field, the
  consequence and a fix.
  - `field.computed` — the expression is never evaluated, so the column stays null.
  - `field.rollup` — nothing maintains the aggregate, so it reads as permanently null *while looking
    like data*.
  - `field.validation` — the expression is not evaluated, so a value it forbids is accepted and the
    field is not constrained at all.
  - `field.default` — no column default is emitted and the value is dropped before any writer sees it,
    so the field is simply null. On a `required` field that is an INSERT of NULL into a NOT NULL
    column. This one has an immediate ergonomic cost and is the first thing to restore (#113).
  - `entity.softDelete` — a delete would remove the row outright and reads would not exclude it:
    irrecoverable data loss where the schema promises recoverability.
  - Each of the six `entity.hooks.*` points, refused **individually** so that implementing one lifts
    only its own refusal (#114) — a `before*` hook may reject or mutate inside the write transaction,
    so a write the author believes is vetted is neither; an `after*` effect simply never happens.

  Blocks that are **warned about instead of refused**, because their absence is observable:
  `dynamicEntities`, `automation`, `templates`, `webhooks`, `functions`. Applying a descriptor that
  declares any of them logs one warning naming each.

- **A descriptor may no longer declare a field named after a framework-managed column** — `id`,
  `tenant_id`, `created_at`, `created_by`, `updated_at`, `updated_by`, `deleted_at` — on an entity
  whose traits carry it. The refusal is trait-scoped, so an entity that does not declare `audit` may
  still have its own `created_at`. Previously a declaration won, and two defects came out of that:
  an audited entity declaring `updated_at` as `{"type":"string"}` applied cleanly and then **failed
  every create** with an internal parameter name in the response body; and one declaring `updated_at`
  as `hidden` applied cleanly and **switched optimistic concurrency off in silence**, because the mask
  drops the key from every returned record so no `ETag` is ever minted. This breaks a descriptor that
  declares `updated_at`, and it also removes one capability: **`readOnly` on `tenant_id` as a
  narrowing is now forbidden** along with the declaration. Express that intent as a `create` rule
  instead — the synthesized tenant scope's `WITH CHECK` is already evaluated over the candidate row,
  so a rule can answer "which tenant may this row be placed in" per caller and a field flag cannot.

### Changed

- **A format check that times out is now its own violation code, `format-not-evaluated`, and no
  longer reported as `format`.** A client branching on the `format` code will no longer see the
  pattern-timeout case. This is a fix for a fail-*wrong*, not a cosmetic split: the old behaviour told
  a caller their value did not match a pattern that had in fact never finished being evaluated, and it
  was reachable on perfectly valid input — a valid `email` address was refused as malformed once in
  nine full suite runs, purely because a loaded machine lost the match timeout to scheduling. "I could
  not decide" and "your value is wrong" are different things to tell a caller, and only one of them is
  about the value. Both still refuse the request, because an unevaluable check must fail closed; the
  difference is that the new code's fix suggestion is **retry the request**, which is the one action
  that can succeed when nothing about the value was wrong.

### Added

- **The HTTP Data API.** A host that calls `MapAlvoDataApi()` gets a REST API generated from its
  descriptor: five routes per declared entity (`GET` collection, `GET {id}`, `POST`, `PATCH`,
  `DELETE {id}`) under a configurable prefix, each one a minimal-API delegate gated by the entity's
  own rules. What comes with them:
  - **A PostgREST-shaped query string**, adopted rather than invented so an agent recognises it:
    ten operators (`eq neq gt gte lt lte like ilike in is`), `or=(…)`/`and=(…)` grouping, a `not.`
    prefix, `order=field.desc.nullslast`, `select=a,b`, and both paging modes — keyset via an opaque
    `after` cursor, plus `offset` as the opt-in second mode. Page size is server-enforced.
  - **Structured refusals.** Every error is an RFC 9457 problem document with an Alvo `type` slug
    (`https://alvo.dev/errors/…`) and a `violations` array carrying a JSON pointer, a machine-readable
    code, a message and a fix suggestion for *every* problem with the request — not just the first.
  - **Optimistic concurrency, on an entity that keeps a row version.** A single-row read and a write
    return a strong `ETag` over that version — **only where the entity declares `audit: true`**, which
    is what mints the version column; an entity without it gets no `ETag`, and a *list* never carries
    one. `If-Match` on a `PATCH`/`DELETE` is evaluated inside the write transaction against a
    row-locked pre-image. A precondition this API cannot evaluate is refused rather than ignored,
    because ignoring one is the lost update the header exists to prevent — and on a version-less
    entity the generated document does not offer `If-Match` at all, rather than inviting a header
    whose every value would be 412.
  - **`Idempotency-Key` on create.** A retried create returns the first one's result and never
    duplicates a row. The record stores the created row's id — never a rendered response — so a
    replay re-reads through the caller's *current* policy and can never hand back a representation
    that policy would no longer produce.
  - **`Cache-Control: no-store`** on every generated response. These are private, per-caller
    representations; the `ETag` exists for concurrency, not for a shared cache.
  - **An OpenAPI 3.1 document** enriched from the applied schema — per-entity request and response
    schemas, the query parameters with their real enforced bounds, the problem shape, and an API-key
    security scheme. §0 principle 4: the document *is* the contract an agent reads.

  Known limits, so the list is honest: no bulk operations, no `PUT`/upsert, no relation embedding, no
  aggregations or total count, and `Idempotency-Key` is ignored on `PATCH`/`DELETE`. Each is filed
  with its reason. `docs/architecture/data-api.md` records the decisions and the surprises — in
  particular that a *configured* rule which excludes a caller answers **200 with an empty page, not
  403**, because a rule compiles to a row-level predicate.

- Repository and solution skeleton: `MMLib.Alvo.Abstractions` (the interface-first
  root of the dependency graph) and its test project.
- Central Package Management, shared build settings, pinned .NET SDK, `.slnx` solution.
- First architectural guard-rail (NetArchTest): Abstractions depends on no other
  project in the solution.
- Apache-2.0 license and minimal pull-request CI (build + test).
- Contributor onboarding: `CONTRIBUTING.md` (build/test, PR process, transparent CLA
  explanation), Individual and Corporate CLAs (`docs/legal/`) based on the Project Harmony
  v1.0 templates that keep contributor copyright while allowing future relicensing, and a
  Contributor Covenant `CODE_OF_CONDUCT.md`.
- Central package management finished: shared assembly/NuGet metadata (author, product,
  license, repo link, tags, icon, readme), warnings-as-errors, deterministic builds, and
  SourceLink in `Directory.Build.props`; root `README.md` and package icon (`icon.png`,
  generated from `assets/alvo-logo.svg`).
- Repo tooling: CodeQL analysis, `Dependabot` version updates (NuGet + GitHub Actions),
  a Dependency Review check on pull requests (fails on moderate+ severity or
  non-allow-listed licenses), and a CodeRabbit config (`.coderabbit.yaml`) tuned to this
  project's conventions (Central Package Management, disallowed packages, XML doc and
  comment-style rules).
