# Field-service as the advanced demo — design

**Status:** proposed · **Date:** 2026-11-13 · **Branch:** `demo/field-service-advanced`
**Plan:** `docs/superpowers/plans/2026-11-13-field-service-advanced-demo.md` (two PRs, A and B)

## 1. Intent

The maintainer's ask: keep a **simple** demo that shows how little it takes to use Alvo, and add a demo that
uses the **sophisticated** constructs in a real use case — guarded by TeaPie, so it cannot rot.

- **Simple demo — already exists, unchanged.** `examples/vehicle-registry`, served by the root
  `docker-compose.yml` (`:8080`), guarded by `test/teapie` (health, owners, descriptor, docs, auth) inside
  `scripts/test-e2e`, which is the required **Build & test** check. Confirmed current on `origin/main`
  (45a7c5db): three entities, no tenancy, one dev key. Nothing in this design touches it.
- **Advanced demo — `examples/field-service`, grown in two layers over one domain** (multi-tenant
  field-service dispatch: regions, customers, work orders):
  1. **Descriptor layer** (PR-A) — what any Alvo deployment gets from JSON alone, so it runs in the
     standalone image and is asserted by `test/teapie-field-service`.
  2. **Host C# layer** (PR-B) — a new embedded host, `samples/MMLib.Alvo.Samples.FieldService`, over the
     *same* descriptor file, adding what only code can do; asserted by its unit tests and a new suite
     `test/teapie-field-service-host` against the host running in compose.

The story the demo tells, end to end: a call comes in (`new`), the dispatcher assigns a technician and a
slot in one atomic step (`dispatch`), the customer is told, the technician starts and finishes the job from
the mobile app (signed inbound webhook), billing is told the job is done (outbound webhook), an emergency
nobody scheduled escalates on its own (SLA timer), and every morning the desk gets the overdue list (cron).

## 2. Sources read

`schema/project.schema.json` (entity: `stateMachine`, `timers`, `indexes[].where/exclude`, `checks`,
`events.exclude`; field: `sequence`, `validation`, `rollup`; top level: `automation`, `webhooks`, `access`);
`website/src/content/docs/extending/{state-machines,integrity,relation-rules,automation,commands,
write-hooks,event-handlers,time,reads,generated-types,operate-deliveries,extension-catalog}.mdx` and their
compiled snippets under `website/src/snippets/extending/**`; `src/MMLib.Alvo/Events/Internal/
{ActionCompiler,WebhookEgressGuard}.cs`; the existing example, its README, its suite and
`scripts/test-e2e`; every consumer of `examples/field-service` (§8).

## 3. Layer 1 — the descriptor (PR-A, runs standalone)

Each construct below was checked against the schema and the extending page that documents its runtime
behaviour. All are honoured by the standalone image unless §5 says otherwise.

| # | Construct | Where | Why a field-service business has it |
|---|---|---|---|
| D1 | `status` gains `new`; **`stateMachine`** `schedule` / `start` / `complete` / `cancel` / `reopen` | `work_orders` | A job cannot be completed before it is started, a technician cannot cancel a customer's job, and only an admin reopens a closed one. `schedule` requires `has(new.assigned_to) && has(new.scheduled_for)`; `complete` requires `has(new.completed_on)`. |
| D2 | **`sequence`** `JOB-{yyyy}-{n:5}`, `period: year`, `timeZone: Europe/Bratislava` | new `work_orders.job_number` | Billing and the customer quote a gapless, per-tenant job number; a refused create gives its number back. |
| D3 | **`timers.sla_breach`** `at: respond_by`, `when: is_emergency == true && status == 'new'`, event `work_orders.sla_breached` | new `work_orders.respond_by` | An emergency call-out nobody scheduled by its respond-by time must escalate. Armed/moved/cancelled in the write's own transaction. |
| D4 | **Partial unique index** `["customer_id","po_number"]` `where: status != 'cancelled'` | new `work_orders.po_number` | A customer's purchase order pays for one job; cancelling the job releases the PO. |
| D5 | **`field.validation`** (CHECK) | `work_orders.priority` 1–5, `quoted_price >= 0`; `customers.service_opens_hour` / `service_closes_hour` (0–24, close after open); `visits.minutes_on_site` 0–1440 | What the database must never hold, whoever writes — including a hook or a migration. |
| D6 | New entity **`visits`**: a technician's booked slot on a job (`work_order_id` ref, `technician_id`, `starts_at`, `ends_at`, `minutes_on_site`) | `visits` | The calendar the dispatcher books into. |
| D7 | **Exclusion** `["technician_id"]`, `[starts_at, ends_at)` | `visits.indexes` | A technician is never booked into two places at once — 409 `exclusion`. |
| D8 | **Relation rule** `work_order_id.assigned_to == @user.id` | `visits.rules.list/get` | A technician sees the visits of the jobs assigned to them, and nobody else's — through the work order's own `list` rule. |
| D9 | **Lookup check** `work_order_id.status != 'completed' && work_order_id.status != 'cancelled'` | `visits.checks` | Nobody books a visit on a closed job — 422 with the author's message at `/work_order_id`. |
| D10 | **Rollup** `on_site_minutes` = `sum(visits.minutes_on_site)` | `work_orders` | What billing invoices, kept transactionally by the visit writes. |
| D11 | **`automation.job-completed-to-billing`**: `entity.work_orders.updated`, `changed(status) && new.status == 'completed' && has(new.po_number)`, `webhook` → endpoint `billing` | top level + `webhooks.endpoints.billing` | Completing a purchase-order job tells billing, as its own retried delivery, after the commit; a job without a PO is settled on site (R6a). |
| D12 | **`events.exclude`** `["access_code","internal_notes"]` | `work_orders` | The site access code and dispatcher notes must never leave in an event or a webhook to billing. |
| D13 | **`access`** `{ "developer": "'admin' in @user.roles" }` | top level | Lets the demo's `admin` key operate deliveries and (PR-B) *Run now* a schedule over the Management API — both `developer` operations (`ManagementOperations`), so `developer` is the least level that does the job; `admin` stays with the bootstrap administrator. Management access is instance-wide: it spans every tenant (a management `viewer` already lists every tenant's deliveries), so the key holding the `admin` role is the *instance operator*, and the tenant on that dev key only scopes its data-plane writes. `developer` is the least level that redelivers, replays and pauses deliveries (and runs a schedule, which the embedded host adds); `admin` (users, keys, switching an extension off) stays with the bootstrap administrator. (Review round 1: was `admin`, with a wrong "a tenant role must not see every tenant's deliveries" argument.) |

### Rulings on the descriptor layer

- **R1 — no `initial` on the state machine.** `initial` binds *every* create, and the demo's seed (and any
  real migration from a legacy dispatch system) imports jobs that are already scheduled, running or done.
  New jobs are created `new` by convention; the machine guards every *move*. Recorded deviation from the
  "new → …" sketch in the brief.
- **R2 — the sequence is a new field, not `reference`.** `reference` stays caller-supplied: the existing
  suite seeds and filters by fixed references (`WO-1001…`), pins `/reference` in the unique and tenancy
  oracle cases, and the load harness writes `WO-<n>`. A sequence field is Alvo-written (a write that sends
  it is 403), so converting `reference` would rewrite a third of the suite and the load scenarios for no
  gain. `job_number` is the number the business reads aloud; `reference` is the dispatch desk's / the
  customer's ticket reference. Deviation from "sequence for the reference", recorded.
- **R3 — the SLA deadline is data, not a constant.** `at` reads `respond_by`, which the intake (dispatcher
  or host) sets from the customer's contract. That makes the timer testable in seconds by **sending a near
  `respond_by`** — no test hook, no shortened descriptor constant, no clock control in product code.
- **R4 — the region-scoped technician visibility from the brief is adapted, not taken.** Making
  `work_orders.list` admit a technician by region would widen `tech-north`'s list and break the suite's
  central claims (`070-Authorization`: *subset* vs *empty page*, measured by exact sets). The relation rule
  lives on the new `visits` entity instead (D8), where it is additive. Deviation, recorded.
- **R5 — the partial unique index is on `po_number`, not on emergencies or technicians.** "One open
  emergency per customer" collides with seeded `WO-1002`/`100-Scenarios/003`; "one in-progress job per
  technician" collides with `070`/`100-Scenarios/005`, which move `tech-north`'s rows to `in_progress`.
  `po_number` is nullable and new, so nothing existing collides.
- **R6 — the billing endpoint is a loopback sidecar.** `ActionCompiler` admits `http` only for a loopback
  host, and `WebhookEgressGuard` refuses every non-public address unless the URL *declares* loopback. The
  endpoint is therefore `http://localhost:9090/billing/work-orders`, and the demo stack runs a WireMock
  sidecar with `network_mode: "service:alvo"` — it shares the Alvo container's network namespace, so
  `localhost:9090` really is loopback, and the egress guard stays fully on. No `WebhookAllowedNetworks`
  opt-in, no TLS in the demo. Elsewhere (quickstart image, admin e2e host, load stack) nothing listens on
  `:9090`, so a completion's billing delivery fails and retries visibly on *Operations* — documented in
  the README as what an unreachable endpoint looks like.
- **R6a — only a purchase-order job is billed** (implementation ruling, PR-A). R6 left a completion's delivery
  failing in every stack without a sink. The maintainer ruled that the quick start and the dashboard must not
  show a permanently failing delivery to someone who just started the demo, and the descriptor format has no
  per-stack switch (an endpoint URL or a rule's `enabled` cannot come from configuration). The condition
  therefore adds `has(new.po_number)` — a real business distinction (PO jobs are invoiced, the rest settled on
  site) that an ordinary completion in those stacks does not meet. Completing a job that carries a PO there
  still leaves a retrying delivery; the example README says so. It also gives `160-Automation` a second control
  (a completion without a PO sends nothing).
- **R7 — deliveries are unsigned today** (#120), so the suite asserts what billing received and that it
  carries no excluded field, not a signature. The endpoint's `secretRef: billing-signing` is declared so the
  descriptor is correct the day signing lands; an absent secret is an apply *warning*, not a refusal.

### Example JSON (the shape PR-A adds; full text in the plan, Task A1)

```json
"stateMachine": { "field": "status", "transitions": {
  "schedule": { "from": ["new"], "to": "scheduled", "roles": ["dispatcher", "admin"],
                "when": "has(new.assigned_to) && has(new.scheduled_for)" },
  "start":    { "from": ["scheduled"], "to": "in_progress", "roles": ["technician", "dispatcher", "admin"] },
  "complete": { "from": ["in_progress"], "to": "completed", "roles": ["technician", "dispatcher", "admin"],
                "when": "has(new.completed_on)" },
  "cancel":   { "from": ["new", "scheduled", "in_progress"], "to": "cancelled", "roles": ["dispatcher", "admin"] },
  "reopen":   { "from": ["completed", "cancelled"], "to": "scheduled", "roles": ["admin"],
                "when": "!has(new.completed_on)" } } },
"timers": { "sla_breach": { "at": "respond_by", "when": "is_emergency == true && status == 'new'",
                            "event": "work_orders.sla_breached" } }
```

## 4. Layer 2 — the embedded host (PR-B)

`samples/MMLib.Alvo.Samples.FieldService` — a Web SDK project shaped like
`samples/MMLib.Alvo.Samples.EmbeddedHost` (static `CreateBuilder`/`Build`/`RunAsync`, project references,
`IsPackable=false` from `samples/Directory.Build.props`), organised by **vertical slice**: `Dispatch/`,
`Mobile/`, `Scheduling/`, `Notifications/`, `Sla/`, `Reports/`, `Reads/`, `Tenancy/`. It reads
`examples/field-service/field-service.alvo.json` — the same file the standalone stack mounts — on PostgreSQL.

| # | Construct (API, from the compiled snippets) | What it does in the business |
|---|---|---|
| H1 | **Generated types** — `AdditionalFiles … AlvoDescriptor="true"`; `WorkOrder`, `WorkOrderStatus`, `WorkOrderPatch`, `Customer`, `Visit`, `FieldServiceRoles` | Every slice below is typed against the descriptor; a renamed field breaks the build, and the boot check refuses a drifted schema. |
| H2 | **Command** `work_orders.dispatch` — `MapAlvoCommand<WorkOrder>(…, (o, c) => o.Status == WorkOrderStatus.New && (c.HasRole(dispatcher) \|\| c.HasRole(admin)), …)` | One `ctx.TransactAsync` unit: set `assigned_to`/`scheduled_for`/`status = scheduled` **and** create the `visits` row. A double-booked technician (D7) rolls the whole unit back: the job stays `new`. `Idempotency-Key` replays. |
| H3 | **Inbound Standard Webhook** — `MapAlvoWebhookReceiver("/hooks/technician-app", "technician-app", …, o => o.ResolveTenant = …)` | The mobile app posts "job completed" (`workOrderId`, `tenantId`, `completedOn`, `minutesOnSite`), signed `whsec_`. Runs as the system in the tenant the **signed body** names; completes the job (transition `complete` — the system holds `admin`) and records the minutes on the visit, in one unit. Idempotent by id and by state. |
| H4 | **Before-write hook** `WithinServiceHours : IBeforeWriteHook<WorkOrder>` | A job may only be scheduled inside the customer's service hours (D5 fields), read in `FieldService:TimeZone`; otherwise `write.Invalid("outside-service-hours", …, "scheduled_for")` → 422. Runs inside the dispatch unit too. |
| H5 | **Typed event handler** `NotifyCustomerOfVisit : IAlvoEventHandler<AlvoUpdated<WorkOrder>>`, `when: e => e.Changed(x => x.Status) && e.New.Status == WorkOrderStatus.Scheduled` | Tells the customer when the technician is coming, via `ICustomerNotifier` (an HTTP client to a configured URL — the WireMock sidecar in compose). |
| H6 | **Handler of the declarative timer's event** `AddEventHandler("escalate-sla-breach", "work_orders.sla_breached", …)` | Raises the job to priority 1 and pages the on-call dispatcher (notifier). Closes the loop D3 opens: descriptor automation cannot subscribe to a timer event, a host handler can. |
| H7 | **Cron schedule** `AddSchedule<OverdueReport>("overdue-report", cron, "Europe/Bratislava", o => o.Scope = AlvoScheduleScope.EachTenant)` + `IAlvoTenantCatalog` from configuration | Every morning, per tenant, the open jobs past their `scheduled_for`/`respond_by` go to the desk (notifier). Cron from `FieldService:OverdueReportCron` (default `0 6 * * *`). |
| H8 | **Read narrowing** `TechnicianOpenJobs : IQueryNarrowing<WorkOrder>` | A technician's job list shows only open jobs (`new`/`scheduled`/`in_progress`). A *view*, not a boundary — the rule still decides; documented as such. |
| H9 | **`MMLib.Alvo.TestHost` unit tests** — `test/MMLib.Alvo.Samples.FieldService.Tests` | Each slice with a `FakeTimeProvider` and `DrainEventsAsync`; command and receiver over `TestServer` as `StayDeskCommandsTests` does. |

Ruling **R8 — no test-only code in the host.** Short durations come from data (`respond_by`, R3) and host
configuration (cron, notifier URL); the cron is driven in e2e by the Management API's **Run now**
(`POST /management/projects/field-service/schedules/overdue-report/run`, `developer` level — D13's admin
holds it), never by a 1-minute cron or a debug endpoint. Ruling **R9 — PostgreSQL only** for the host; the
unit tests use `AlvoTestHost` (SQLite), which honours sequences, exclusions, timers and commands.
Ruling **R10 — the notifier is an HTTP client to a sink**, not an in-memory outbox with an inspection route:
an inspection route would be test code in the sample. In compose the sink is the same WireMock sidecar
(`http://localhost:9090/notifications/…`); a real deployment points the URL at its SMS/e-mail provider.

**Review round 1 (PR-B), deviations from the table above.** Rule 1 must be true in the sample, so:

- **H5 is dropped.** A customer notice is a descriptor `automation` on `entity.work_orders.updated` with an `email`
  action, or a `webhook` with a `{{…}}` payload. The demo leaves it out because it runs no mail provider, not because
  the descriptor cannot express it.
- **H8 is dropped.** A narrowing applies to list *and* get. A list-only variant (`QueryNarrowing.Operation`) would equal
  a `rules.list` change, because `list` and `get` are separate rules. A narrowing belongs to input only code has.
- **H4 now checks the whole visit**, on a second hook on `visits`: it must start at or after opening and end by
  closing, in local time. The first hook checks the job's start only.
- **H2 validates its input.** A bad field is 422 at that field. A job that is not `new` is 409, not 403: the access
  rule is the role only.
- **H3 refuses a notice missing a field or naming the empty tenant.** The README states the shared-secret trust
  model.
- **H6 re-reads the row before it escalates, and pages first, as a step.** It skips a job whose breach no longer
  holds, and a retry never pages twice.

## 5. What is deliberately left out, and what the standalone build cannot do

- **Timer effects in standalone.** The `sla_breach` timer is armed by the standalone image, but its event has
  no consumer there: automation cannot subscribe to a timer event (state-machines.mdx, *What is not
  covered*). It is asserted in PR-B (unit + host e2e). The standalone suite asserts only that the field
  and the transitions exist.
- **Cron automation in the descriptor.** A cron rule takes no condition and runs per tenant only with an
  `IAlvoTenantCatalog`, which the standalone image does not register — the overdue report is host code (H7).
- **`initial`** (R1), **transition commands** (not in this release), **dynamic entities** (neither member is
  supported on `storage: dynamic`), **JSONata webhook `payload`** (the plain CloudEvent is posted; no
  transformation needed), **signed outbound webhooks** (#120), **`http.call`** (refused by this build),
  **`function` actions** (refused by every host that does not register one — would make the example
  not runnable standalone).
- **Technician-by-region visibility** (R4) and a sequence on `reference` (R2).

## 6. Test strategy

**Standalone — `test/teapie-field-service` (PR-A).** New groups, same bar as the suite's README (parsed
shapes, exact sets, a control for every claim):

| File | Asserts |
|---|---|
| `130-Lifecycle/001-transitions` | Skipping a step (`scheduled → completed`) is 422 code `transition` at `/status`; `schedule` without an assignee, with only an assignee, or with only a time is 422 (so `&&` is distinguishable from `||`); a technician's `cancel` of their own job is 403 (the row is visible, the move is not theirs); `complete` without `completed_on` is 422 and with it 200; dispatcher `reopen` is 403, an admin `reopen` keeping `completed_on` is 422 and one clearing it is 200 (review round 1: `reopen.when: !has(new.completed_on)`); a `PATCH` that does not name `status` is no transition. |
| `130-Lifecycle/002-batch-transition` | A batch with one illegal move answers 422, the failing index carries `transition`, and **no** row moved. |
| `140-Integrity/001-sequence` | Seeded orders carry `JOB-<yyyy>-00001…00007` in creation order; south's first is `…-00001` (per tenant); a create refused by a CHECK (priority 9, 422 `check`) gives its number back — the next create is exactly +1; a write that sends `job_number` is 403. |
| `140-Integrity/002-validation` | `priority: 9`, `service_opens_hour: 25`, `service_closes_hour <= service_opens_hour` → 422, code `check`, pointer at the field; boundary values pass; a closing hour with no opening hour passes (the `!has(service_opens_hour)` arm). |
| `140-Integrity/003-partial-unique` | Second open order on the same `(customer, po_number)` → 409 `unique` at `/customer_id` or `/po_number` (pin what the product answers); cancel the first → the PO is free; another customer may reuse it. |
| `140-Integrity/004-visits` | Overlapping visit for one technician → 409 `exclusion` at `/starts_at`; back-to-back (`[10,11)`,`[11,12)`) passes; another technician at the same time passes; a visit on a completed or a cancelled job → 422 `check` at `/work_order_id` with the descriptor's message; `on_site_minutes` on the job equals the sum. |
| `150-Relations/001-visits-through-the-job` | `tech-north` lists exactly the visits of jobs assigned to them; `spare-north` an empty page; the dispatcher all; `tech-north` `GET` of another job's visit is 404; `visits.update` is the read rule plus being the booked technician, so `spare-north`'s `PATCH` of the unassigned job's visit is 404 and changes nothing, `tech-north`'s `PATCH` of their own is 200, and re-pointing it at a job they cannot read is 422 `unresolved-reference`. |
| `160-Automation/001-billing-webhook` | Completing a job delivers exactly one POST to the sink whose CloudEvent `data.record` names that job as `completed` and carries neither `access_code` nor `internal_notes`; a non-completing update and a completion without a PO deliver nothing (controls). Polls WireMock's journal with `## RETRY-UNTIL-TEST-PASS` until a sentinel PO completion, written last, has also arrived, and only then judges the counts. |
| `090-Docs/001` (edit) | Path set gains `/api/visits*`; `work_orders` property set gains `job_number`, `on_site_minutes`, `po_number`, `respond_by`; `status` enum gains `new`; `job_number` is `readOnly`. |
| `080-Tenancy/002` (edit) | `job_number` joins the fingerprint's excluded fields — it is assigned per row, like `id` (each tenant's count advances). |

**Embedded host — `test/teapie-field-service-host` (PR-B)**, against `docker-compose.field-service-host.yml`:

| File | Asserts |
|---|---|
| `010-Seed` | Regions, customers (with service hours), new jobs — by the same keys. |
| `020-Dispatch/001` | `dispatch` moves a `new` job to `scheduled`, assigns, books the visit; a retry with the same `Idempotency-Key` replays byte-identically and books nothing twice; a technician is 403; a slot outside service hours is 422 `outside-service-hours` and the job stays `new` with **no** visit; a double-booked technician is 409 `exclusion` and the job stays `new` (atomic unit). |
| `030-Mobile/001` | A signed "job completed" (signature computed in a `-init.csx` from the per-run `whsec_`) completes the job and records the minutes; the same `webhook-id` again is 200 and changes nothing; a bad signature is 401 and changes nothing. |
| `040-Notifications/001` | Dispatch notified the customer (sink journal names the job reference and the contact address). |
| `050-Sla/001` | An emergency `new` job with `respond_by = now + 5 s` reaches priority 1 within ~20 s and the on-call page is in the sink; a control job scheduled before its deadline keeps its priority. |
| `060-Reports/001` | *Run now* as `admin` → 202/200; the sink receives one report per tenant, north's naming its overdue job and not south's. A dispatcher's *Run now* is refused (403). |
| `070-Reads/001` | `tech-north`'s list omits their completed job; the dispatcher's list shows it (control). |
| `080-Catalog/001` | `GET /management/projects/field-service/extensions` lists the command, the hook, both handlers, the schedule and the narrowing — the host registered what this suite claims. |

Unit tests (H9) cover the same behaviours with a fake clock, plus the cases e2e cannot reach cheaply (the
cron firing at 06:00 Bratislava, the timer *moving* when `respond_by` changes, cancelled when scheduled).

## 7. CI wiring

`scripts/test-e2e` gains (A) a WireMock sidecar and a sink port in the field-service stack, (B) a third
stack `docker-compose.field-service-host.yml` (`:8082`, sink `:8092`), a per-run `ALVO_FS_TECH_APP_SECRET`
(`whsec_` + 32 random bytes, base64), a third TeaPie invocation with its own JUnit report, and teardown/logs
for the third stack. It stays the one script CI's required **Build & test → E2E** job runs, so both suites
gate every PR. The e2e job's `timeout-minutes: 20` is re-measured after PR-B adds a second image build;
raise it only by what the measurement shows. The sample builds in the solution (`MMLib.Alvo.slnx`), so
ring0–2 compile it; its unit tests are ring0 (`.Tests`), and `scripts/ring0-scope` gets a by-path row
because they read `examples/field-service/` by path.

## 8. Risks and rulings

| Risk | Ruling |
|---|---|
| **Blast radius of editing `examples/field-service`.** Consumers: admin e2e (`Descriptors.FieldService` is the default world of ~30 scenarios, plus `StateMachineWorld` which *adds* its own machine), `MMLib.Alvo.Admin.Tests` working-copy tests, the generator snapshot (`…example=field-service.verified.txt`), `scripts/gen-prototype-fixtures --check` (CI), the load harness (`seed.sql`, `scenarios.js`, `tools/MMLib.Alvo.Load.Host`), the quickstart image (ships the file). | Kept: the maintainer chose one domain, and those consumers read the repository file *on purpose* ("read from the repository, not copied"). PR-A carries an explicit fallout task: run ring0, `scripts/test-admin-e2e`, `gen-prototype-fixtures`, `test-prototype`; fix each failure by updating a pinned *shape* (a count, a list, a snapshot), never by weakening a behavioural claim; route the snapshot through `alvo-snapshot-judge`. `StateMachineWorld` overwrites `work_orders.stateMachine` wholesale, so it keeps working. |
| Existing TeaPie cases set `status` freely. | Audited every status write (§3 R1 table in plan Task A2): all are `scheduled → in_progress` by a dispatcher or the assigned technician, `→ cancelled` from `scheduled`/`in_progress` by a dispatcher, or refused earlier by tenancy/row rules (404). The machine admits all of them; `cancel` includes `in_progress` deliberately (a customer can call off a job mid-visit). Two edits only: `090-Docs/001` and `080-Tenancy/002` (§6). |
| Refusal order: does a cross-tenant `PATCH` of `status` still answer 404, not 422/403? | Expected (the row is read as the caller first), and pinned by the existing `080`/`100-006` cases that already PATCH `status` across tenants — they turn red if it ever changes. |
| `has(new.x)` in a transition `when`. | Used in before-hook conditions in shipped examples; the plan's first task proves it applies through `AlvoTestHost` before any TeaPie is written. If refused, fall back to `new.x != null`. |
| Load: `sequence` serialises concurrent creates per tenant; `seed.sql` inserts explicit columns. | New columns are nullable, so `seed.sql` keeps working. The write-latency cost is real product cost; the gate is advisory — run `scripts/test-load --tier gate` on PR-A and record the delta in the PR report rather than hiding it. |
| Timers/cron in e2e. | R3 (deadline is data) and R8 (*Run now*). Polling: `RETRY-UNTIL-TEST-PASS`, constant 1 s backoff, ≤ 30 attempts; the host's `Alvo:Events:PollInterval` stays at its 1 s default. |
| Inbound signature in TeaPie must sign the exact bytes sent. | The `-init.csx` builds the compact body, signs it, and sets both `{{MobileBody}}` and the headers; the `.http` body is exactly `{{MobileBody}}` on one line. |
| WireMock in a shared network namespace. | Image pinned (`wiremock/wiremock:3.9.1`), `--port 9090`; `ports` live on the `alvo` service (a `network_mode: service:` container cannot publish). Its stub mapping is a committed file under `examples/field-service/sink/`. |
| README of the example carries stale defect text (unique → 500, cross-tenant unique). | Rewritten in PR-A; both defects are fixed (#137, #138) per the suite's own README. |

## 9. Deviations from the request, in one place

R1 no `initial`; R2 sequence on a new `job_number`; R4 relation rule on `visits`, not region visibility on
work orders; R5 partial unique on `po_number`; the SLA timer's *effect* is host-only (§5); the keyed
`IAlvoScheduler` timer is not used — the declarative timer is the better tool for a per-record deadline,
and the time.mdx table says so.
