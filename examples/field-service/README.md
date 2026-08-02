# field-service — the runnable complex demo

A multi-tenant field-service dispatch backend, and the fixture the end-to-end suite in
`test/teapie-field-service` drives. Unlike `../complex-crm`, which showcases the descriptor **format**
and is deliberately not appliable, **every construct in this one runs**: the stack boots, the
descriptor applies, and each feature below is measured by at least one test against a real container.

`../vehicle-registry` is the smoke fixture — three entities, no tenancy, no `audit`, no `hidden`,
no role-differentiated rules. It proves the plumbing. This example exists to prove the *product*.

## Run it

```bash
# One secret per dev key. Never committed (§2.14); compose refuses to start without them.
export ALVO_FS_DISPATCHER_NORTH_SECRET=$(openssl rand -hex 16)
export ALVO_FS_TECH_NORTH_SECRET=$(openssl rand -hex 16)
export ALVO_FS_SPARE_NORTH_SECRET=$(openssl rand -hex 16)
export ALVO_FS_DISPATCHER_SOUTH_SECRET=$(openssl rand -hex 16)
export ALVO_FS_ADMIN_SECRET=$(openssl rand -hex 16)

docker compose --env-file examples/field-service/demo-identities.env \
  -f docker-compose.field-service.yml up --build --wait
```

Then `http://localhost:8081/scalar`, or `scripts/test-e2e`, which does all of the above with fresh
secrets and runs both TeaPie suites against both stacks.

The repo-root `docker-compose.yml` is untouched and still serves `vehicle-registry` on `:8080`. This
demo is a **second compose file** rather than a second service in that one because Compose interpolates
a whole file before selecting services — profiles do not defer it, measured — so five more `${…:?}`
secrets in `docker-compose.yml` would break the README quickstart for anyone who had exported only
`ALVO_DEMO_KEY_SECRET`.

## The domain

| Entity | Tenancy | Audited | Why it is shaped this way |
|---|---|---|---|
| `regions` | **global** | no | Shared reference data (*číselník*): both tenants read the same rows, and it is the non-scoped entity that makes "scoped" mean something. It has **no `update` and no `delete` rule at all**, which is the only way to reach a genuine *unconfigured-operation* 403. |
| `customers` | scoped | **no** | The non-audited entity. Its rows carry no version, so no `ETag` is ever minted and an `If-Match` naming one is **refused with 412 rather than ignored**. |
| `work_orders` | scoped | **yes** | The main entity. Audited, so `ETag`/`If-Match` work — which is the other half of the pair above. |

## What each construct is there to measure

| Construct | Where | The behaviour a test measures |
|---|---|---|
| `tenancy.enabled` + `tenancy: scoped` | `customers`, `work_orders` | Tenant B cannot see, read, update, delete or infer the existence of tenant A's rows — every attempt is a 404, not a 403. |
| `tenancy: global` | `regions` | A global entity is visible to every tenant; it is what makes the scoped entities' isolation a *contrast* rather than an assumption. |
| `audit: true` | `work_orders` | Mints `created_at`/`created_by`/`updated_at`/`updated_by`, and `updated_at` is the row version an `ETag` encodes. A stale `If-Match` is 412. |
| `audit` **absent** | `customers` | No version column, so **no `ETag` at all** and any `If-Match` naming a version is refused. Both directions matter; one entity cannot show both. |
| `hidden: true`, optional | `work_orders.internal_notes` | Never in any response, and **its name appears nowhere in `/openapi/v1.json`**. A filter over it is refused *byte-identically* to a filter over an undeclared field — otherwise the refusal answers "does this field exist". |
| `hidden: true`, **`required`** | `work_orders.access_code` | The one case a hidden field's name is published: in `work_ordersCreate`/`work_ordersPatch` only, never in a response schema. A mandatory field a caller was never told about could not be supplied. |
| `readOnly: true` | `work_orders.external_ref` | Present in read schemas, absent from write schemas, and writing it is a **422 `read-only-field`** rather than a silent drop. |
| Role-differentiated rules | `work_orders`, `customers` | See the authorization table below. |
| Unconfigured operation | `regions` (no `update`/`delete`) | A real **403** that is distinguishable from the two shapes below. |
| Row-level predicate | `work_orders.assigned_to == @user.id` | A technician's `list` is a **200 with a subset**, and a `get` of a row they are not assigned is a **404** — never a 403. |
| Caller-only predicate | `customers.list` | A technician's `list` is a **200 with an empty page**, because a rule compiles to a row filter and a caller who fails it matches no rows. This is the single most misunderstood behaviour in the product. |
| `ref` + `onDelete: restrict` | `work_orders.customer_id`, `.region_id` | Foreign-key existence is validated **as the caller**, so an id in another tenant is `unresolved-reference`, not a cross-tenant existence oracle. |
| Field types | `work_orders` | `string`, `text`, `integer`, `decimal(10,2)`, `boolean`, `date`, `datetime`, `uuid`, `json`, `enum`, `ref` — one of each, so the OpenAPI wire-shape mapping is exercised end to end. |
| Built-in `format` | `contact_email` (email), `customers.phone` (phone) | A format violation is a reachable, per-field 422. |
| Declared `format` | `work-order-ref` | A descriptor-declared pattern publishes as `pattern` (not `format`) and is **anchored by Alvo**, not by the author — the pattern is written without `^…$` on purpose. |
| Nullable sort key | `scheduled_for` | A *paged* read sorted by it is refused (`unpageable-sort-key`): a keyset cursor cannot express where nulls sort. |
| Required sort key | `priority` | The paging key the suite walks four pages of, with deliberate ties so the `id` tie-breaker is what makes the order total. |
| `indexes` | `["status","priority"]`, `["assigned_to"]` | Cover exactly the fields the suite filters and orders on. |

## Authorization, all three shapes in one descriptor

The three are constantly confused, so this descriptor makes each one reachable and the suite proves
they are distinguishable **in the same system state**:

| Shape | Reach it with | Answer |
|---|---|---|
| The operation has **no rule** | `DELETE /api/regions/{id}` as anybody | **403** `forbidden` — "No policy allows 'delete' on this entity." |
| A rule exists and the caller **fails it** | `GET /api/customers` as `tech-north` | **200** with `{"items":[],"next":null}` — a rule is a row filter, not a gate |
| A rule exists and **one row** fails it | `GET /api/work_orders/{someone-elses}` as `tech-north` | **404** — indistinguishable from a row that never existed |

## The dev keys

Five, differing only in role and tenant, so any difference in what they can see is attributable.

| Key id | Roles | Tenant | Why it exists |
|---|---|---|---|
| `dispatcher-north` | `dispatcher` | north | The privileged caller inside a tenant. |
| `tech-north` | `technician` | north | Restricted, **with** rows assigned: its list is a subset. |
| `spare-north` | `technician` | north | Restricted, with **nothing** assigned: its list is an empty page. Same role, same tenant, same scopes as `tech-north` — so an empty page is the row predicate and can be nothing else. |
| `dispatcher-south` | `dispatcher` | south | Identical rights to `dispatcher-north`, other tenant. Anything it cannot see is tenancy, not policy. |
| `admin` | `admin` | north | The only caller the descriptor lets create a `region` or delete a `customer`. |

Tenant ids and user ids live in `demo-identities.env` and are **not** secrets: a create on a scoped
entity has to carry `tenant_id`, and `assigned_to` names a technician by user id, so no test can be
written without them. The five key secrets are credentials and are never committed.

## Two behaviours worth knowing before you write against this

- **A create on a tenant-scoped entity must carry `tenant_id` explicitly.** Nothing stamps it from the
  caller's context; the synthesized tenant scope's `WITH CHECK` then judges the value. Omit it and the
  answer is `403 The write was rejected by policy.` — correct, but it names no field.
- **A duplicate value on a `unique` field is answered `500`, not `409`.** The framework validates
  `required`, `maxLength`, `enum`, `format`, `precision`/`scale` and `ref` existence, but a `unique`
  constraint is enforced only by the database and its violation is not mapped. `test/teapie-field-service`
  pins the current behaviour so the case turns red the day it is fixed. See that suite's README.
