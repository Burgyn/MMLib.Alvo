# field-service — the advanced demo

A multi-tenant field-service dispatch backend, and the fixture the end-to-end suite in
`test/teapie-field-service` drives. Unlike `../complex-crm`, which showcases the descriptor **format**
and is deliberately not appliable, **every construct in this one runs**: the stack boots, the
descriptor applies, and each feature below is measured by at least one test against a real container.

**Looking for the simple demo?** `../vehicle-registry` — three entities, no tenancy, no `audit`, no
`hidden`, one dev key — shows how little it takes to use Alvo, and proves the plumbing. This example is
the other end: the sophisticated constructs, in one real use case.

The story it tells: a call comes in (`new`), the dispatcher assigns a technician and a slot
(`scheduled`) and books the visit in the calendar, the technician starts and finishes the job
(`in_progress` → `completed`), and billing is told the job is done. An emergency nobody scheduled by its
`respond_by` time arms an SLA timer. Every job carries a gapless job number, and the database itself
refuses what must never be stored.

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

The stack has three containers: PostgreSQL, Alvo, and **`sink`** — a WireMock that plays the billing
system (see *The billing webhook* below). Its request journal is on `http://localhost:8083/__admin/requests`.

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
| `work_orders` | scoped | **yes** | The main entity. Audited, so `ETag`/`If-Match` work — which is the other half of the pair above. Its `status` moves through a state machine, and it carries a gapless `job_number`. |
| `visits` | scoped | yes | A technician's booked slot on a job — the dispatch calendar. A technician sees a visit **through the job it belongs to**, never through its own `technician_id`. |

## The lifecycle

`work_orders.stateMachine` guards every **move** of `status`. A create may store any state (there is no
`initial`, deliberately: a migration from a legacy dispatch system imports jobs that are already running
or done); new calls are created `new` by convention.

| Transition | From → to | Roles | Condition |
|---|---|---|---|
| `schedule` | `new` → `scheduled` | dispatcher, admin | `has(new.assigned_to) && has(new.scheduled_for)` |
| `start` | `scheduled` → `in_progress` | technician, dispatcher, admin | — |
| `complete` | `in_progress` → `completed` | technician, dispatcher, admin | `has(new.completed_on)` |
| `cancel` | `new`, `scheduled`, `in_progress` → `cancelled` | dispatcher, admin | — |
| `reopen` | `completed`, `cancelled` → `scheduled` | admin | `!has(new.completed_on)` — the admin clears the sign-off day in the same write, so completing the job again needs a new one |

A move no transition admits, or whose condition fails, is **422** with code `transition` at `/status`; a
move a transition matches but whose roles the caller lacks is **403**. A `PATCH` that does not name
`status` is no transition. The `sla_breach` timer (`at: respond_by`, while
`is_emergency == true && status == 'new'`) is armed, moved and cancelled in the write's own transaction
and publishes `work_orders.sla_breached`; descriptor automation cannot subscribe to a timer's event, so
in the standalone image nothing acts on it — the [embedded host](../../samples/MMLib.Alvo.Samples.FieldService/README.md)
does (see *The embedded host layer* below).

## What each construct is there to measure

| Construct | Where | The behaviour a test measures |
|---|---|---|
| `tenancy.enabled` + `tenancy: scoped` | `customers`, `work_orders`, `visits` | Tenant B cannot see, read, update, delete or infer the existence of tenant A's rows — every attempt is a 404, not a 403. |
| `tenancy: global` | `regions` | A global entity is visible to every tenant; it is what makes the scoped entities' isolation a *contrast* rather than an assumption. |
| `audit: true` | `work_orders`, `visits` | Mints `created_at`/`created_by`/`updated_at`/`updated_by`, and `updated_at` is the row version an `ETag` encodes. A stale `If-Match` is 412. |
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
| Nullable sort key | `scheduled_for` | Set on two of seven rows, so a *paged* read sorted by it walks both a valued anchor and a null-keyed one — the two shapes a keyset boundary over nulls has to get right, and the two that used to be refused outright. |
| Opt-in count | `Prefer: count=exact` | Seven rows behind a `limit=2` page, and a technician whose row rule shows fewer — so a count that was the page size, or one taken over the table, is a different number in each case. |
| Required sort key | `priority` | The paging key the suite walks four pages of, with deliberate ties so the `id` tie-breaker is what makes the order total. |
| `indexes` | `["status","priority"]`, `["assigned_to"]` | Cover exactly the fields the suite filters and orders on. |

### The advanced constructs

| Construct | Where | The behaviour a test measures | Case |
|---|---|---|---|
| `stateMachine` | `work_orders.status` | Skipped steps and failed conditions are 422 `transition`; a transition the caller holds no role for is 403; a batch with one illegal move writes nothing. | `130-Lifecycle/001`, `/002` |
| `sequence` | `work_orders.job_number` (`JOB-{yyyy}-{n:5}`, per tenant, per year in Bratislava) | The seed is numbered 1..7 in north and 1 in south (each tenant counts on its own); a create refused by a CHECK gives its number back; a write that sends it is 403; it is `readOnly` in the document. | `140-Integrity/001`, `090-Docs/001` |
| `field.validation` (CHECK) | `priority` 1–5, `quoted_price >= 0`, `customers.service_opens_hour`/`service_closes_hour`, `visits.minutes_on_site` | 422 `check` at exactly the field to fix, for every writer — the database enforces it. | `140-Integrity/002` |
| Partial unique index | `["customer_id","po_number"]` `where: status != 'cancelled'` | A second open job on a customer's PO is 409 `unique` at `/customer_id` and `/po_number`; another customer, or a cancelled job, frees it. | `140-Integrity/003` |
| Exclusion | `visits`: `["technician_id"]` over `[starts_at, ends_at)` | A technician booked twice at once is 409 `exclusion` at `/starts_at`; back-to-back slots pass. | `140-Integrity/004` |
| Lookup check | `visits.checks` | A visit on a completed or cancelled job is 422 `check` at `/work_order_id`, with the descriptor's message. | `140-Integrity/004` |
| Rollup | `work_orders.on_site_minutes = sum(visits.minutes_on_site)` | Kept by the visit writes, in their transaction. | `140-Integrity/004` |
| Relation rule | `visits.rules.list/get`: `work_order_id.assigned_to == @user.id`; `update` adds being the booked technician | A technician lists exactly the visits of jobs assigned to them; a row failing it is 404. They write only a visit they can read — a booked technician whose job is not theirs gets 404 — and cannot re-point it at a job they cannot read. | `150-Relations/001` |
| `timers` | `work_orders.sla_breach` on `respond_by` | Armed in the write; its effect is host code (see *The lifecycle*). | `test/teapie-field-service-host` `050-Sla` |
| `automation` + `webhooks` | `job-completed-to-billing` → endpoint `billing` | A completed purchase-order job is posted to billing once, after the commit; a completion without a PO, or an update that completes nothing, sends nothing. | `160-Automation/001` |
| `events.exclude` | `work_orders`: `access_code`, `internal_notes` | Neither name nor value is in any delivery to billing. | `160-Automation/001` |
| `access` | `{ "developer": "'admin' in @user.roles" }` | A key holding the `admin` role may operate deliveries over the Management API (redeliver, replay, pause). Management access is **instance-wide**: it spans every tenant (a management `viewer` already lists every tenant's deliveries), so the key holding the `admin` role is the *instance operator*, and the tenant on that dev key only scopes its data-plane writes. `developer` is the least level that redelivers, replays and pauses deliveries (and runs a schedule, which the embedded host adds); `admin` (users, keys, switching an extension off) stays with the bootstrap administrator. | — |

## The embedded host layer

[`samples/MMLib.Alvo.Samples.FieldService`](../../samples/MMLib.Alvo.Samples.FieldService/README.md) serves **this same
file** from an embedded ASP.NET Core host and adds what this file has no construct for: the `dispatch` command (schedule
the job and book the visit in one unit), the technician app's signed inbound webhook, before-write hooks that keep jobs
and visits inside the customer's service hours (a time-zone-aware hour), the SLA timer's escalation (automation cannot
subscribe to a timer's event) and a per-tenant overdue report every morning. Telling the customer when the technician
comes would be an `automation` with an `email` action here — left out only because the demo runs no mail provider. `docker-compose.field-service-host.yml` runs it (`:8082`, sink `:8092`) and
`test/teapie-field-service-host` guards it inside `scripts/test-e2e`. Its README says, slice by slice, why each piece is
code.

## The billing webhook

The `billing` endpoint is `http://localhost:9090/billing/work-orders`. Plain `http` is admitted only for a
loopback host, and the webhook egress guard refuses every non-public address unless the URL itself
declares loopback — so in this stack the `sink` container (WireMock) runs with
`network_mode: "service:alvo"`: it shares the Alvo container's network namespace, `localhost:9090` really
is loopback, and the guard stays fully on. Nothing is allow-listed (`WebhookAllowedNetworks` stays empty).

A cancelled job's visits stay on the calendar: visits carry no status of their own and the exclusion has no
`where`, so delete a cancelled job's visits to free the technician's slots.

**Only a purchase-order job reaches billing** (`condition: changed(status) && new.status == 'completed'
&& has(new.po_number)`); a job without a PO is settled on site. That is a business rule, and it is also
what keeps the other places that run this file quiet: the quick-start image (`ALVO_DESCRIPTOR=…/field-service…`)
and the dashboard's end-to-end host have no billing sink, and nothing listens on `:9090` there (the load
stack layers on this compose file, so it has the sink). **If you complete a job that carries a `po_number` in such a stack, its billing delivery
fails and retries — visible on the dashboard's *Operations* page — and that is what an unreachable
endpoint looks like.** Run this compose file (or add a sink of your own) to see it succeed.

Deliveries are unsigned in this build (#120): the endpoint's `secretRef: billing-signing` is declared so
the descriptor is right the day signing lands, and its absence is an apply *warning*, not a refusal.

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

## Worth knowing before you write against this

- **A create on a tenant-scoped entity must carry `tenant_id` explicitly.** Nothing stamps it from the
  caller's context; the synthesized tenant scope's `WITH CHECK` then judges the value. Omit it and the
  answer is `403 The write was rejected by policy.` — correct, but it names no field.
- **`job_number` and `on_site_minutes` are Alvo's to write.** A create or update that sends either is
  refused `403`, with a detail that says why; read them back from the response.
- **The dashboard's field rename does not carry everything yet.** Renaming a field a transition's `when`, a timer,
  an index `where`/`exclude`, a lookup check, an automation condition or another entity's relation rule names
  (`work_orders.assigned_to`, `status`, `starts_at`, …) leaves those places unrewritten and reports no leftover, and
  Preview then refuses the descriptor — #513. Edit the descriptor file for those renames.
