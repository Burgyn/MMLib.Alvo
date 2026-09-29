---
name: alvo-descriptor-traits-and-tenancy
description: Use when an Alvo descriptor change touches audit, softDelete, tenancy, storage or realtime on an entity, or project tenancy — the columns each trait adds and what this build honours.
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
In this repo: edit the descriptor file and run the validator (`dotnet test`), or the Management API.
