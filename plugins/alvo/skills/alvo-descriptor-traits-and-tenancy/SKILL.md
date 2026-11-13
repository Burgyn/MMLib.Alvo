---
name: alvo-descriptor-traits-and-tenancy
description: Use when an Alvo descriptor change touches audit, softDelete, tenancy, storage, realtime, stateMachine or timers on an entity, or project tenancy — what each adds and does here.
---

# Entity traits and tenancy in an Alvo descriptor

A trait is an entity-level switch the framework implements for you. Never declare the columns a trait adds: the
framework adds them, and a declaration is refused.

<!-- gen:managed-columns -->
  - on every entity — `id`
  - on an entity whose `tenancy` is `scoped` — `tenant_id`
  - on an entity with `"audit": true` — `created_at`, `created_by`, `updated_at`, `updated_by`
  - on an entity with `"softDelete": true` — `deleted_at`
<!-- /gen:managed-columns -->

## Tenancy

An entity is `scoped` when it says `"tenancy": "scoped"`, or when the project enables tenancy
(`"tenancy": {"enabled": true}`) and the entity does not opt out with `"tenancy": "global"`. A `global` entity is
shared reference data every tenant sees.

- A scoped row belongs to one tenant, and a caller only ever sees its own tenant's rows.
- A `POST` create on a scoped entity must **echo** the caller's `tenant_id` in the body: the server checks it
  rather than filling it in. A replace, an upsert or an update must not send it; the upsert fills it in.
- A `unique` field or unique index on a scoped entity is unique per tenant: two tenants may hold the same value.

## Audit, soft delete, storage, realtime

- `"audit": true` adds the four audit columns and fills them on every write.
- `"softDelete": true` is refused at apply in this build: soft delete is not implemented yet, so a delete would
  remove the row outright. Do not propose it; say so, and quote the capability report.
- `"storage": "dynamic"` is not honoured by this build: the apply warns and drops the entity, which gets no table
  and no Data API route. `realtime` is not honoured either, and not even warned, since its default is `true`: nothing
  is published. Propose neither; see `alvo-descriptor-capabilities-and-limits`.

## State machine and timers

`stateMachine` names one stored `enum` field and its named `transitions`, each `{from, to, roles?, when?}`. `from`
lists the states it leaves (`["*"]` alone means any); `initial`, if set, is the state every create must store; a field
`default` is what a create sending none stores, and must equal `initial`. An update that changes the field must take a transition, else 422 code `transition`; a transition
whose `roles` the caller lacks is 403. `when` is CEL over `old`/`new`, and one that fails refuses. The system caller
holds `admin`, so a job needs a transition listing it. A host before-write hook may not set the field.

`timers` maps a name to `{at, when?, event?}`: `at` is a CEL timestamp over the row's own fields
(`now() + duration('24h')`), `when` a condition. A create arms it, an update moves it only when its due changes, an
inactive `when` or a delete cancels it, all in the write's transaction. It fires `event` (default `<entity>.<timer>`)
with `entity`, `id`, `timer`, `at`: a timer's event is handled in code by a host handler, never by descriptor
automation. The first apply that adds or changes a timer re-plans existing rows; a due already past is not armed. `timers` is refused on an entity with a cascading ref; an `onDelete: setNull` on a ref `at` reads leaves the old due until the row's next write. Neither member is allowed on `storage: "dynamic"`.

<!-- example: audit-rental-fleet -->
**Record who created and last changed each rental bike.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Turns on the audit trail for rental_fleet.",
 "operations": [{"op": "add", "path": "/entities/rental_fleet/audit", "value": true}]}
```

```json
{"valid": true, "changedPaths": ["/entities/rental_fleet/audit"]}
```

<!-- example: soft-delete-refused -->
**Soft delete is refused in this build.**

```json
{"tool": "check_change", "baseRevision": 1,
 "operations": [{"op": "add", "path": "/entities/rental_fleet/softDelete", "value": true}]}
```

```json
{"valid": false, "changedPaths": ["/entities/rental_fleet/softDelete"],
 "violations": [{"pointer": "/entities/rental_fleet/softDelete",
                 "message": "Soft delete is not supported yet", "severity": "error"}]}
```

In the dashboard: read with `get_descriptor`, quote `get_capabilities` for what is not honoured, then
`check_change` or `propose_change` the operations.
In this repo: dry-run the edited `*.alvo.json`: `PUT /management/projects/<project>/descriptor?dryRun=true` (Alvo repo: `scripts/test-ring0`).
