# Field-service advanced demo — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep `examples/vehicle-registry` as the simple demo, and turn `examples/field-service` into the advanced
demo: a descriptor layer that runs in the standalone image (state machine, sequence, SLA timer, partial unique index,
CHECK validations, visits with an exclusion, a relation rule, a lookup check and a rollup, completion → billing
webhook) and an embedded host sample over the same file (dispatch command, signed inbound webhook, before-write hook,
event handlers, cron schedule, read narrowing, generated types) — both guarded by TeaPie inside the required
**Build & test** check.

**Architecture:** One domain, one descriptor file, two runtimes. PR-A changes only JSON, compose and TeaPie: the
standalone stack gains a WireMock sidecar that shares the Alvo container's network namespace, so the descriptor's
`http://localhost:9090` billing endpoint is genuinely loopback and the webhook egress guard stays on. PR-B adds
`samples/MMLib.Alvo.Samples.FieldService` (vertical slices over generated types, PostgreSQL), its `AlvoTestHost` unit
tests, its own Dockerfile + compose stack (same sidecar), and a third TeaPie suite; `scripts/test-e2e` drives all
three stacks.

**Tech Stack:** .NET 10 (C# 14), ASP.NET Core minimal APIs, MMLib.Alvo + MMLib.Alvo.Data.PostgreSql,
MMLib.Alvo.TestHost, xUnit v3 on MTP, Shouldly, `Microsoft.Extensions.TimeProvider.Testing`,
`Microsoft.AspNetCore.TestHost`, Docker Compose, PostgreSQL 16, `wiremock/wiremock:3.9.1`, TeaPie (`dotnet tool run
teapie`).

**Spec:** `docs/superpowers/specs/2026-11-13-field-service-advanced-demo-design.md` — §3 (D1–D13, R1–R7), §4 (H1–H9,
R8–R10), §5 (left out), §6 (test table — the authority on what each TeaPie file asserts), §7 (CI), §8 (risks).

## Global Constraints

- Worktree `/Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo-wt-demo`. **Two PRs, stacked:** PR-A on
  `demo/field-service-advanced` (Tasks A1–A9); PR-B on `demo/field-service-host` branched from PR-A's head (Tasks
  B1–B12), rebased onto `main` once PR-A merges. Never push to `main`; a human merges.
- Git hooks on (never `HUSKY=0`, never `--no-verify`); Conventional Commits; every commit body ends with the
  session's `Claude-Session:` line; no `Closes #N` keyword anywhere in a commit (memory: keywords fire from prose).
- `.cs` files: CRLF + UTF-8 BOM as the repo's `.editorconfig` demands — write them with the Write/Edit tools, not with
  shell heredocs, or the pre-commit `dotnet format` check fails.
- **No test-only code in product or sample code** (spec R8): timing comes from data (`respond_by`) and configuration
  (cron, notifier URL); the cron is driven by Management API *Run now*.
- Never invent API. Every C# shape below is copied from a compiled snippet under `website/src/snippets/extending/**`
  (file named next to it). Where a step says **verify**, read the named source first and adapt; do not guess.
- TeaPie conventions of `test/teapie-field-service/README.md` are binding: every `-test.csx` starts with
  `#load "../_shared/Rows.csx"`; assert parsed shapes (`BodyOf`, `PageSet`, `ProblemType`, `ViolationCodes`,
  `ViolationPointers`), exact sets, a control per claim; a case that hands ids on opens with a `Capture:` test; GUID
  environment values via `Constant(name)`; **every request is preceded by `###`**; no array indexing in request
  variables.
- The suite owns its database (fresh stack per run). New references in PR-A use the `WO-3xxx` range
  (`130` → `WO-31xx`, `140` → `WO-32xx…WO-34xx`, `150` → `WO-35xx`, `160` → `WO-36xx`); the host suite uses `WO-7xxx`.
- Local verification needs Docker for anything TeaPie (`scripts/test-e2e`); `dotnet test` for the rest. Rings:
  `scripts/test-ring0` after each task, `scripts/test-ring1` at the end of each PR's slice, `scripts/test-ring2`
  before each PR. `scripts/test-admin-e2e` and `scripts/test-prototype` are run on purpose in A8 (no ring).
- Remove any `.claude/worktrees` before running rings (memory: agent worktrees break ring0).

## Review Focus

1. **The existing suite still proves what it proved.** Only `090-Docs/001` and `080-Tenancy/002` change, and only by
   adding a field to a pinned set / to the fingerprint's excluded fields — no assertion is relaxed.
2. **The egress guard is not weakened.** No `WebhookAllowedNetworks`, no `http://` to a non-loopback host; the sidecar
   is the mechanism (spec R6).
3. **Atomicity claims are measured, not narrated:** the dispatch unit's rollback (job stays `new`, no visit) and the
   gapless sequence (refused create gives its number back) each have an assertion that would fail under the wrong
   behaviour.
4. **Blast radius handled honestly (A8):** each admin-e2e / snapshot / fixture change updates a pinned *shape*, and
   the snapshot goes through `alvo-snapshot-judge`.
5. **Host code is idiomatic and short** (≤ ~25-line methods, `alvo-dotnet-conventions`), slices own their files, and
   nothing in the sample is reachable without a rule.

---

# PR-A — descriptor layer + standalone TeaPie

## Task A1: Grow the descriptor and prove it applies

**Files:**
- Modify: `examples/field-service/field-service.alvo.json`

**Interfaces — Produces:** the descriptor of spec §3 (D1–D13). Later tasks read: `work_orders.{status:new, job_number,
respond_by, po_number, on_site_minutes}`, entity `visits`, automation `job-completed-to-billing`, endpoint `billing`,
`access.admin`.

- [ ] **Step 1: Edit the JSON.** Keep every existing member; update the top-level `description` (drop "F3"; say
  "the advanced demo: …"). Add, exactly:

  Top level (after `formats`):
  ```json
  "access": { "admin": "'admin' in @user.roles" },
  "webhooks": {
    "endpoints": {
      "billing": {
        "url": "http://localhost:9090/billing/work-orders",
        "secretRef": "billing-signing",
        "description": "The billing system. In the demo stacks a WireMock sidecar shares the Alvo container's network namespace, so localhost:9090 is loopback and the webhook egress guard admits it; anywhere else nothing listens there and the delivery fails and retries visibly on Operations."
      }
    }
  },
  "automation": {
    "job-completed-to-billing": {
      "description": "Tells billing a job is done, once per completion, as its own retried delivery after the commit.",
      "trigger": { "event": "entity.work_orders.updated" },
      "condition": "changed(status) && new.status == 'completed'",
      "actions": [ { "type": "webhook", "endpoint": "billing" } ]
    }
  }
  ```

  `customers.fields` (append):
  ```json
  "service_opens_hour": {
    "type": "integer",
    "description": "The hour (0-23, the tenant's local time) from which a technician may be on site. Empty = no restriction. The embedded host's before-write hook schedules against it.",
    "validation": "value >= 0 && value <= 23"
  },
  "service_closes_hour": {
    "type": "integer",
    "description": "The hour (1-24) by which a visit must end; after service_opens_hour when both are set.",
    "validation": "value >= 1 && value <= 24 && (!has(service_opens_hour) || value > service_opens_hour)"
  }
  ```

  `work_orders.fields`: change `status.values` to `["new", "scheduled", "in_progress", "completed", "cancelled"]`
  and its description to name the machine; add `"validation": "value >= 1 && value <= 5"` to `priority` and
  `"validation": "value >= 0.0"` to `quoted_price`; append:
  ```json
  "job_number": {
    "type": "string",
    "description": "The gapless job number billing and the customer quote, assigned by Alvo on create, counted per tenant and per calendar year in Bratislava. Read-only: a write that sends it is refused.",
    "sequence": { "period": "year", "timeZone": "Europe/Bratislava", "format": "JOB-{yyyy}-{n:5}" }
  },
  "respond_by": {
    "type": "datetime",
    "description": "When an emergency must be scheduled by, from the customer's contract. Arms the sla_breach timer."
  },
  "po_number": {
    "type": "string",
    "description": "The customer's purchase order. One open job per customer and PO; a cancelled job releases it.",
    "maxLength": 40
  },
  "on_site_minutes": {
    "type": "integer",
    "description": "Minutes on site across the job's visits — what billing invoices. Maintained by Alvo.",
    "rollup": { "from": "visits", "op": "sum", "field": "minutes_on_site" }
  }
  ```
  `work_orders` members (beside `rules`/`indexes`):
  ```json
  "events": { "exclude": ["access_code", "internal_notes"] },
  "stateMachine": {
    "field": "status",
    "transitions": {
      "schedule": { "description": "A dispatcher books a technician and a time.", "from": ["new"], "to": "scheduled", "roles": ["dispatcher", "admin"], "when": "has(new.assigned_to) && has(new.scheduled_for)" },
      "start":    { "description": "The technician arrives.", "from": ["scheduled"], "to": "in_progress", "roles": ["technician", "dispatcher", "admin"] },
      "complete": { "description": "Signed off on site.", "from": ["in_progress"], "to": "completed", "roles": ["technician", "dispatcher", "admin"], "when": "has(new.completed_on)" },
      "cancel":   { "description": "The customer or the desk calls it off, even mid-visit.", "from": ["new", "scheduled", "in_progress"], "to": "cancelled", "roles": ["dispatcher", "admin"] },
      "reopen":   { "description": "Only an administrator reopens a closed job.", "from": ["completed", "cancelled"], "to": "scheduled", "roles": ["admin"] }
    }
  },
  "timers": {
    "sla_breach": { "description": "An emergency still unscheduled at respond_by escalates.", "at": "respond_by", "when": "is_emergency == true && status == 'new'", "event": "work_orders.sla_breached" }
  }
  ```
  and append to `work_orders.indexes`:
  `{ "fields": ["customer_id", "po_number"], "unique": true, "where": "status != 'cancelled'" }`.

  New entity `visits` (after `work_orders`):
  ```json
  "visits": {
    "description": "A technician's booked slot on a job: the dispatch calendar. Seen by a technician through the job it belongs to.",
    "tenancy": "scoped",
    "audit": true,
    "fields": {
      "work_order_id": { "type": "ref", "description": "The job this visit is for.", "entity": "work_orders", "required": true, "onDelete": "restrict" },
      "technician_id": { "type": "uuid", "description": "The user id of the technician booked.", "required": true },
      "starts_at": { "type": "datetime", "description": "When the slot starts (inclusive).", "required": true },
      "ends_at": { "type": "datetime", "description": "When the slot ends (exclusive).", "required": true },
      "minutes_on_site": { "type": "integer", "description": "Minutes actually spent, recorded at sign-off.", "validation": "value >= 0 && value <= 1440" },
      "notes": { "type": "text", "description": "What the technician wants the desk to know." }
    },
    "rules": {
      "list": "'dispatcher' in @user.roles || 'admin' in @user.roles || work_order_id.assigned_to == @user.id",
      "get": "'dispatcher' in @user.roles || 'admin' in @user.roles || work_order_id.assigned_to == @user.id",
      "create": "'dispatcher' in @user.roles || 'admin' in @user.roles",
      "update": "'dispatcher' in @user.roles || 'admin' in @user.roles || technician_id == @user.id",
      "delete": "'dispatcher' in @user.roles || 'admin' in @user.roles"
    },
    "checks": [
      { "lookup": "work_order_id", "expr": "work_order_id.status != 'completed' && work_order_id.status != 'cancelled'", "message": "A visit can only be booked on an open work order." }
    ],
    "indexes": [
      { "fields": ["technician_id"], "exclude": { "from": "starts_at", "to": "ends_at" } }
    ]
  }
  ```
- [ ] **Step 2: Schema + mapper tests.** `dotnet test test/MMLib.Alvo.Schema.Tests` (`ExamplesTests.Example_validates`)
  and `dotnet test test/MMLib.Alvo.Tests` (`DescriptorToSchemaMapperTests.Every_runnable_example_maps`). Expected:
  green. A refusal here names the pointer — fix the JSON, not the test.
- [ ] **Step 3: Prove a full apply** (CEL compile, `has()` in a `when`, sequence/exclusion/timer checks). Write a
  throwaway xUnit fact in a scratch copy of `test/MMLib.Alvo.DocsSnippets.Tests` **or** run the standalone host
  locally:
  ```bash
  export ALVO_FS_ADMIN_SECRET=$(openssl rand -hex 16)
  Alvo__DescriptorPath=examples/field-service/field-service.alvo.json Alvo__Database__Provider=sqlite \
  ConnectionStrings__Alvo="Data Source=$TMPDIR/fs-probe.db" \
  Alvo__Auth__DevKeys__0__KeyId=admin Alvo__Auth__DevKeys__0__Secret=$ALVO_FS_ADMIN_SECRET \
  Alvo__Auth__DevKeys__0__Roles__0=admin Alvo__Auth__DevKeys__0__Scopes__0='*:read' \
  dotnet run --project src/MMLib.Alvo.Host --urls http://127.0.0.1:5099 &
  curl -fsS http://127.0.0.1:5099/health/ready
  ```
  (verify the variable names against `docker-compose.field-service.yml` and `AlvoHostOptions`). Expected: 200. If
  `has(new.x)` is refused in a `when`, replace it with `new.x != null` in both transitions and record it in the spec
  §8 row. Kill the host; delete the probe db. Nothing from this step is committed.
- [ ] **Step 4: Commit** — `feat(examples): grow field-service into the advanced demo descriptor`.

## Task A2: Keep the existing suite honest

**Files:**
- Modify: `test/teapie-field-service/090-Docs/001-openapi-document-test.csx`
- Modify: `test/teapie-field-service/080-Tenancy/002-unique-value-oracle-test.csx`

**Interfaces — Consumes:** A1's field set.

Audit result (spec §8, do not redo — re-verify only if A1 changed the machine): every existing `status` write is
`scheduled → in_progress` by a dispatcher or the assigned technician (`070/001`, `100/001`, `100/002`, `100/005`,
`100/006`), a same-value write (`100/002` retry, `120/001` PUTs), a create (no transition — no `initial`, R1), or a
cross-tenant / unassigned write refused 404 before the machine (`070/001`, `080/001`, `100/005`, `100/006`).

- [ ] **Step 1:** `090-Docs/001`: add `"/api/visits", "/api/visits/batch", "/api/visits/query", "/api/visits/{id}"`
  to the path set (keep ordinal order); add `"job_number"`, `"on_site_minutes"`, `"po_number"`, `"respond_by"` to the
  `work_orders` property set; the enum assertion becomes `{ "cancelled", "completed", "in_progress", "new",
  "scheduled" }`; add one test: `job_number` and `on_site_minutes` are `readOnly: true` on `work_orders` and absent
  from `work_ordersCreate` and `work_ordersPatch` (mirror the existing `external_ref` test). **Verify** first what
  the document emits for a sequence/rollup field by reading it from a running stack; pin what it says, and if a
  maintained field is *not* marked `readOnly` that is a product finding to report, not to paper over.
- [ ] **Step 2:** `080-Tenancy/002`: `var excluded = new[] { "id", "reference", "job_number", "created_at",
  "updated_at" };` and extend the comment: "`job_number` is assigned per row by the tenant's own sequence, so two
  creates differ in it by construction — like `id`". The key-set comparison stays whole.
- [ ] **Step 3: Run** `scripts/test-e2e`. Expected: every existing case green (both suites). A red case outside
  these two files means the audit missed a write — fix the descriptor (spec R1/R5 reasoning), never the case.
- [ ] **Step 4: Commit** — `test(e2e): pin the field-service document's new shape`.

## Task A3: The billing sink in the standalone stack

**Files:**
- Create: `examples/field-service/sink/mappings/accept-everything.json`
- Modify: `docker-compose.field-service.yml`, `examples/field-service/demo-identities.env`, `scripts/test-e2e`,
  `test/teapie-field-service/env.example.json`

**Interfaces — Produces:** env `ALVO_FS_SINK_PORT=8091`; TeaPie variable `fsSinkUrl`
(`http://localhost:$ALVO_FS_SINK_PORT`). WireMock admin API: `POST {{fsSinkUrl}}/__admin/requests/find`
→ `{ "requests": [ { "url": "...", "body": "<string>" , ... } ] }`.

- [ ] **Step 1: Mapping** (`accept-everything.json`):
  ```json
  { "request": { "method": "POST", "urlPathPattern": "/.*" }, "response": { "status": 204 } }
  ```
- [ ] **Step 2: Compose.** In `docker-compose.field-service.yml` add to `alvo.ports`
  `- "127.0.0.1:${ALVO_FS_SINK_PORT:?set ALVO_FS_SINK_PORT (see examples/field-service/demo-identities.env)}:9090"`
  and a service:
  ```yaml
  # The billing system the descriptor's webhook endpoint names. It shares the alvo container's network
  # namespace, so `http://localhost:9090` is loopback for Alvo: ActionCompiler admits http only for a
  # loopback host, and WebhookEgressGuard refuses every non-public address unless the URL declares
  # loopback. The guard stays fully on; nothing is allow-listed. Its port is published on `alvo`,
  # because a `network_mode: service:` container cannot publish one.
  sink:
    image: wiremock/wiremock:3.9.1
    network_mode: "service:alvo"
    command: ["--port", "9090", "--disable-banner"]
    volumes:
      - ./examples/field-service/sink/mappings:/home/wiremock/mappings:ro
  ```
  Add `ALVO_FS_SINK_PORT=8091` to `demo-identities.env` (with a comment line). `scripts/test-load` uses the same
  compose and env file — it gets the sink for free (billing deliveries succeed under load instead of retrying).
- [ ] **Step 3: `scripts/test-e2e`.** In `write_teapie_environment` add `"fsSinkUrl": "http://localhost:$ALVO_FS_SINK_PORT",`;
  extend the header comment's field-service paragraph with "a WireMock billing sink". Mirror the key in
  `env.example.json`.
- [ ] **Step 4: Verify** — `docker compose --env-file examples/field-service/demo-identities.env -f
  docker-compose.field-service.yml up --build --wait` (secrets exported as the README says), then
  `curl -fsS -X POST localhost:8091/__admin/requests/find -d '{"method":"POST"}'` → `{"requests":[]...}`. Down
  with `--volumes`.
- [ ] **Step 5: Commit** — `build(e2e): add a loopback billing sink to the field-service stack`.

## Task A4: TeaPie — `130-Lifecycle`

**Files:**
- Create: `test/teapie-field-service/130-Lifecycle/001-transitions-req.http`, `…/001-transitions-test.csx`,
  `…/002-batch-transition-req.http`, `…/002-batch-transition-test.csx`

**Interfaces — Consumes:** `CustomerAcmeId`, `RegionNorthId` (010-Seed), env keys.

- [ ] **Step 1: Requests**, in the suite's style. Pattern (copy the header comments' voice of `070/001`):
  ```http
  ### A job is created `new`: no `initial` binds creates (spec R1), so this is convention, not enforcement
  # @name CreateNewJob
  ## TEST-EXPECT-STATUS: [201]
  POST {{fsBaseUrl}}/api/work_orders
  X-Alvo-Api-Key: {{keyDispatcherNorth}}
  Content-Type: application/json

  {
    "tenant_id": "{{tenantNorth}}",
    "reference": "WO-3101",
    "title": "Lifecycle: radiator leaking",
    "status": "new",
    "priority": 2,
    "access_code": "ACME-3101",
    "customer_id": "{{CustomerAcmeId}}",
    "region_id": "{{RegionNorthId}}"
  }

  ### `schedule` demands an assignee and a time: without them the move is refused, nothing stored
  # @name ScheduleWithoutAssignee
  ## TEST-EXPECT-STATUS: [422]
  PATCH {{fsBaseUrl}}/api/work_orders/{{CreateNewJob.response.body.$.id}}
  X-Alvo-Api-Key: {{keyDispatcherNorth}}
  Content-Type: application/json

  { "status": "scheduled" }
  ```
  Then, in order: `ScheduleProperly` (200; `status: scheduled`, `assigned_to: {{userTechNorth}}`, `scheduled_for`),
  `SkipToCompleted` (dispatcher, `completed` + `completed_on` → 422), `TechnicianCancels` (tech-north → 403),
  `TechnicianStarts` (tech-north → 200), `PatchNotNamingStatus` (dispatcher `{ "title": "…" }` → 200),
  `CompleteWithoutDate` (tech-north `{ "status": "completed" }` → 422), `CompleteWithDate` (tech-north, with
  `completed_on` → 200), `DispatcherReopens` (→ 403), `AdminReopens` (→ 200), `ReadBack` (dispatcher GET → 200).
- [ ] **Step 2: Tests.** `Capture:` the job id; then one `tp.Test` per claim, e.g.:
  ```csharp
  await tp.Test("Skipping a step is 422 `transition` at /status, and names neither state.", async () =>
  {
      var refused = tp.Responses["SkipToCompleted"];
      Equal("https://alvo.dev/errors/validation", await ProblemType(refused));
      Equal(new[] { "transition" }, await ViolationCodes(refused));
      Equal(new[] { "/status" }, await ViolationPointers(refused));
      // state-machines.mdx: "The message names neither the stored state nor the one sent."
      var message = (await BodyOf(refused)).GetProperty("violations")[0].GetProperty("message").GetString();
      DoesNotContain("scheduled", message);
      DoesNotContain("completed", message);
  });
  ```
  Claims: the 422s (`ScheduleWithoutAssignee`, `SkipToCompleted`, `CompleteWithoutDate`) carry `transition` at
  `/status`; `TechnicianCancels` and `DispatcherReopens` are 403 `forbidden` (and the control: the same technician's
  `TechnicianStarts` on the same row is 200, so 403 is the transition's roles, not the row rule); `ReadBack` ends
  `scheduled` (after admin reopen) with `completed_on` kept. (If the "names neither state" claim does not hold
  verbatim, drop that line — the page promises it, so a failure there is a product finding to report.)
- [ ] **Step 3: Batch file.** Create three `scheduled` jobs `WO-3111..3113` in one `POST /batch` (201), then a `PATCH
  /api/work_orders/batch` moving 3111 → `in_progress` and 3112 → `completed` (illegal); expect 422; the test asserts the
  refused index carries `transition` (read the body shape `110-Batch/001-batch-test.csx` asserts) and that a
  follow-up `GET …?reference=in.(WO-3111,WO-3112)&select=reference,status` shows both still `scheduled`.
- [ ] **Step 4: Run** `scripts/test-e2e`; green. **Commit** — `test(e2e): the work-order state machine, standalone`.

## Task A5: TeaPie — `140-Integrity` sequence, validation, partial unique

**Files:** Create `test/teapie-field-service/140-Integrity/{001-sequence,002-validation,003-partial-unique}-{req.http,test.csx}`

- [ ] **Step 1: `001-sequence`.** Requests: `ListSeededNorth` (dispatcher `GET /api/work_orders?reference=in.(WO-1001,
  WO-1002,WO-1003,WO-1004,WO-1005,WO-1006,WO-1007)&select=reference,job_number&order=reference.asc&limit=10`),
  `ReadSouthSeed` (dispatcher-south `?reference=eq.WO-5001&select=job_number`), `NumberedA` (201, `WO-3201`),
  `RefusedByCheck` (same body, `WO-3202`, `"priority": 9` → 422), `NumberedB` (201, `WO-3203`), `SendsJobNumber`
  (`WO-3204` with `"job_number": "JOB-1999-00001"` → 403). Tests:
  ```csharp
  string Year() => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
      TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava")).Year.ToString();

  await tp.Test("The seed is numbered 1..7 in creation order, per tenant, in Bratislava's year.", async () =>
  {
      var numbers = await PageColumn(tp.Responses["ListSeededNorth"], "job_number");
      Equal(Enumerable.Range(1, 7).Select(n => $"JOB-{Year()}-{n:00000}").ToArray(), numbers);
      Equal(new[] { $"JOB-{Year()}-00001" }, await PageColumn(tp.Responses["ReadSouthSeed"], "job_number"));
  });
  ```
  plus: `NumberedB` is exactly `NumberedA + 1` (parse the `{n}` part) — the refused create left no gap; `RefusedByCheck`
  is 422 `check` at `/priority`; `SendsJobNumber` is 403 (pin the problem type the product answers). The seed claim
  holds only if `010-Seed` is the first north/south create — true by folder order; say so in a comment.
- [ ] **Step 2: `002-validation`.** Customers (dispatcher-north, `tenant_id` north): `service_opens_hour: 25` → 422;
  `opens 18, closes 8` → 422 at `/service_closes_hour`; `opens 7, closes 19` → 201 (control); work order
  `"priority": 0` → 422 at `/priority`, `"quoted_price": -1.00` → 422 at `/quoted_price`; `"priority": 5` → 201
  (boundary control). Assert code `check` and the exact pointer each time.
- [ ] **Step 3: `003-partial-unique`.** `PoFirst` (`WO-3301`, Acme, `po_number: "PO-778"`, 201), `PoAgain` (`WO-3302`,
  same → 409), `PoOtherCustomer` (`WO-3303`, Borovica, same PO → 201), `CancelFirst` (PATCH 3301 `cancelled` → 200),
  `PoAfterCancel` (`WO-3304`, Acme, same PO → 201). Tests: `PoAgain` is `conflict` with code `unique` (pin the pointer
  the product names; the README must then state it); the three 201s are the controls that make the 409 the
  `where` and the `(customer, po)` key, not a plain unique on `po_number`.
- [ ] **Step 4: Run** `scripts/test-e2e`. **Commit** — `test(e2e): sequence, CHECK validations and a partial unique index`.

## Task A6: TeaPie — visits (`140/004`) and `150-Relations`

**Files:** Create `test/teapie-field-service/140-Integrity/004-visits-{req.http,test.csx}`,
`test/teapie-field-service/150-Relations/001-visits-through-the-job-{req.http,test.csx}`

- [ ] **Step 1: `004-visits`.** Setup jobs: `WO-3401` assigned tech-north `scheduled`; `WO-3402` assigned to nobody
  `scheduled`; reuse `Wo1005Id`-like completed seed? — no: capture a completed job by creating `WO-3403` with
  `status: completed`, `completed_on`. Visits (dispatcher, `tenant_id` north):
  `V1` tech-north 2026-12-01T10:00Z–11:00Z on 3401 (201, `minutes_on_site: 45`), `Overlap` tech-north 10:30–11:30
  (409), `BackToBack` tech-north 11:00–12:00 on 3401 (201, `minutes_on_site: 30`), `OtherTechSameSlot` spare-north
  10:00–11:00 on 3402 (201), `OnClosedJob` on 3403 (422), `ReadJob3401` (GET). Tests: `Overlap` is `conflict` code
  `exclusion` at `/starts_at`, body names neither the other visit's id nor its times; `OnClosedJob` is `validation`
  code `check` at `/work_order_id` and its `message` equals `"A visit can only be booked on an open work order."`;
  `ReadJob3401.on_site_minutes == 75`.
- [ ] **Step 2: `150-Relations`.** Reads only (rows from 004): `TechLists` (tech-north `GET /api/visits?order=starts_at.asc`),
  `SpareLists` (spare-north), `DispatcherLists`, `TechReadsOthersVisit` (tech-north GET the 3402 visit id → 404).
  Tests: tech-north's set of `work_order_id` is exactly `{3401}` (two visits); spare-north `items: []` even though
  spare-north **is** the `technician_id` of the 3402 visit — the rule is through the job's `assigned_to`, and 3402 is
  assigned to nobody (that is the point; say so); the dispatcher sees all three; the 404.
  > Note in the file: spare-north being the visit's technician yet not seeing it is deliberate — the relation, not
  > the `technician_id` column, decides visibility.
- [ ] **Step 3: Run** `scripts/test-e2e`. **Commit** — `test(e2e): visits — exclusion, lookup check, rollup and a relation rule`.

## Task A7: TeaPie — `160-Automation` (billing webhook)

**Files:** Create `test/teapie-field-service/160-Automation/001-billing-webhook-{req.http,test.csx}`

- [ ] **Step 1: Requests.** `CreateBillable` (`WO-3601`, `in_progress`, assigned tech-north, `access_code:
  "SECRET-3601"`), `CreateControl` (`WO-3602`, `in_progress`), `CompleteBillable` (tech-north, `completed` +
  `completed_on` → 200), `TouchControl` (dispatcher PATCH `{ "priority": 4 }` → 200), then:
  ```http
  ### Billing is told after the commit, by a delivery of its own, so it is polled — at most ~30 s
  # @name BillingJournal
  ## RETRY-UNTIL-TEST-PASS: Billing received exactly one completion for WO-3601.
  ## RETRY-MAX-ATTEMPTS: 30
  ## RETRY-BACKOFF-TYPE: Constant
  ## RETRY-MAX-DELAY: 00:00:01
  POST {{fsSinkUrl}}/__admin/requests/find
  Content-Type: application/json

  { "method": "POST", "urlPath": "/billing/work-orders" }
  ```
- [ ] **Step 2: Tests.**
  ```csharp
  async Task<System.Text.Json.JsonElement[]> Events(string reference) =>
      (await BodyOf(tp.Responses["BillingJournal"])).GetProperty("requests").EnumerateArray()
          .Select(r => System.Text.Json.JsonDocument.Parse(r.GetProperty("body").GetString()).RootElement.Clone())
          .Where(e => e.GetProperty("data").GetProperty("record").GetProperty("reference").GetString() == reference)
          .ToArray();

  await tp.Test("Billing received exactly one completion for WO-3601.", async () =>
  {
      var received = await Events("WO-3601");
      Single(received);
      Equal("completed", received[0].GetProperty("data").GetProperty("record").GetProperty("status").GetString());
  });
  ```
  plus: the delivered body contains neither `access_code`, `SECRET-3601` nor `internal_notes` (`events.exclude`, D12)
  — assert over the raw `body` string; and `Events("WO-3602")` is empty (control: a non-completing update sends
  nothing). **Verify** the CloudEvent body shape against `website/src/content/docs/extending/event-envelope.mdx` and
  pin `type` too once read from a real delivery. Keep the retried test the *first* test in the file (TeaPie's retry
  names it).
- [ ] **Step 3: Run** `scripts/test-e2e`. **Commit** — `test(e2e): a completed job reaches billing, without its secrets`.

## Task A8: Fallout across the repository's other consumers

**Files (expected, confirm by running):** `test/MMLib.Alvo.Analyzers.Tests/AlvoEntityGeneratorSnapshotTests.Generates_the_examples_example=field-service.verified.txt`,
`docs/design/f5-admin/**` generated fixtures, possibly `test/MMLib.Alvo.Admin.Tests/**`,
`test/MMLib.Alvo.Admin.Tests.EndToEnd/**`, `test/load/README.md`.

- [ ] **Step 1: ring0** — `scripts/test-ring0`. The generator snapshot will move (new `Visit`, `VisitPatch`,
  `WorkOrder.JobNumber`/`OnSiteMinutes`/…, `WorkOrderStatus.New`). Accept it only after reading the diff; the Stop
  hook will demand `alvo-snapshot-judge` — dispatch it with the descriptor diff as the justification. Fix each
  `MMLib.Alvo.Admin.Tests` failure by updating a pinned shape (count/list), never a behaviour.
- [ ] **Step 2: Prototype fixtures** — `python3 scripts/gen-prototype-fixtures` then `--check` (CI runs it) and
  `scripts/test-prototype`. Commit regenerated fixtures only.
- [ ] **Step 3: Admin e2e** — `scripts/test-admin-e2e` (real browser). Expected pressure points: `SystemMapScenarios`
  (entity list — now four), anything counting deliveries (a completion now queues a billing delivery that fails
  with nothing on `:9090`), lifecycle/automation screens that assumed field-service had none (`StateMachineWorld`
  overwrites `stateMachine` wholesale and keeps working). For each red scenario: update the pinned shape, or — only
  where a scenario needs "an entity with no machine" — point it at an entity that has none (`customers`), and say
  why in the scenario. Never switch the default world off the repository file.
- [ ] **Step 4: Load** — `scripts/test-load --tier gate` (advisory). Record the create-path delta caused by the
  per-tenant sequence lock in `test/load/README.md`'s findings section if it is outside the noise band; never edit a
  baseline to absorb it without `alvo-snapshot-judge`.
- [ ] **Step 5: Quick-start image** — `dotnet test test/MMLib.Alvo.Host.Tests --filter QuickStartImageTests`; green
  (the Dockerfile ships the same file).
- [ ] **Step 6: Commit(s)** — one per consumer, e.g. `test(admin-e2e): field-service has four entities now`.

## Task A9: Docs, rings, PR

**Files:** Modify `examples/field-service/README.md`, `test/teapie-field-service/README.md`, `CHANGELOG.md`
(Unreleased), `scripts/test-e2e` header comment.

- [ ] **Step 1: Example README.** Re-title to "the advanced demo"; add a section *The lifecycle* (the machine as a
  table: transition, from → to, roles, condition) and *Integrity* / *Automation* rows to the construct table (D1–D13,
  each with the TeaPie file that measures it); explain the loopback sidecar (R6) and what happens without it;
  **delete the stale "Two behaviours worth knowing" bullets** about unique → 500 and cross-tenant unique (fixed,
  #137/#138) and keep the `tenant_id`-on-create note. Point at `vehicle-registry` as the simple demo.
- [ ] **Step 2: Suite README** — add rows for `110-Batch`, `120-Replace` (currently missing from the table) and the
  four new groups; note the sink and `RETRY-UNTIL-TEST-PASS`.
- [ ] **Step 3: Rings** — `scripts/test-ring1`, `scripts/test-ring2`; both green.
- [ ] **Step 4: Gate** — dispatch `alvo-plan-guard` (read-only); address findings. Then the `alvo-pr-report` skill;
  then `gh pr create` (body = the five-line pointer; no closing keywords). Check the branch before pushing (memory:
  reviewers can switch branches).

---

# PR-B — the embedded host sample

## Task B1: Scaffold the host, its generated types and a boot test

**Files:**
- Create: `samples/MMLib.Alvo.Samples.FieldService/MMLib.Alvo.Samples.FieldService.csproj`, `Program.cs`,
  `FieldServiceHost.cs`, `FieldServiceOptions.cs`, `FieldServiceSetup.cs`, `appsettings.json`,
  `Properties/launchSettings.json`
- Create: `test/MMLib.Alvo.Samples.FieldService.Tests/MMLib.Alvo.Samples.FieldService.Tests.csproj`,
  `FieldServiceWorld.cs`, `BootTests.cs`
- Modify: `MMLib.Alvo.slnx`, `scripts/ring0-scope` (by-path row: the tests read `examples/field-service/`)

**Interfaces — Produces:**
```csharp
public sealed class FieldServiceOptions            // bound from "FieldService"
{
    public string DescriptorPath { get; set; } = "";
    public string TimeZone { get; set; } = "Europe/Bratislava";
    public string NotificationsUrl { get; set; } = "";       // http://localhost:9090/notifications in compose
    public string OverdueReportCron { get; set; } = "0 6 * * *";
    public List<Guid> Tenants { get; } = [];
}
public static class FieldServiceSetup
{
    public static IAlvoBuilder AddFieldService(this IAlvoBuilder alvo);          // every slice's registration
    public static IServiceCollection AddFieldServiceServices(this IServiceCollection services, IConfiguration configuration);
    public static WebApplication MapFieldService(this WebApplication app);       // command + receiver
}
public static class FieldServiceHost { CreateBuilder(string[] args), Build(WebApplicationBuilder), RunAsync(string[] args) }
```
Generated (namespace `MMLib.Alvo.Samples.FieldService.Model`): `WorkOrder`, `WorkOrderStatus`, `WorkOrderPatch`,
`Customer`, `Visit`, `VisitPatch`, `Region`, `FieldServiceRoles`.

- [ ] **Step 1: csproj** (Web SDK), copying the two seams `test/MMLib.Alvo.DocsSnippets.Tests` uses for the generator:
  ```xml
  <Project Sdk="Microsoft.NET.Sdk.Web">
    <Import Project="$(MSBuildThisFileDirectory)../../src/MMLib.Alvo/build/MMLib.Alvo.props" />
    <PropertyGroup>
      <RootNamespace>MMLib.Alvo.Samples.FieldService</RootNamespace>
    </PropertyGroup>
    <ItemGroup>
      <ProjectReference Include="../../src/MMLib.Alvo/MMLib.Alvo.csproj" />
      <ProjectReference Include="../../src/MMLib.Alvo.Data.PostgreSql/MMLib.Alvo.Data.PostgreSql.csproj" />
      <ProjectReference Include="../../src/MMLib.Alvo.Analyzers/MMLib.Alvo.Analyzers.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
    </ItemGroup>
    <ItemGroup>
      <!-- The SAME file the standalone stack mounts: one descriptor, two runtimes. -->
      <AdditionalFiles Include="../../examples/field-service/field-service.alvo.json" AlvoDescriptor="true" AlvoNamespace="MMLib.Alvo.Samples.FieldService.Model" />
    </ItemGroup>
  </Project>
  ```
  `samples/Directory.Build.props` already sets `IsPackable=false` and XML docs.
- [ ] **Step 2: Host composition** in `FieldServiceHost.CreateBuilder` (shape of `SampleHost.CreateBuilder`, minus the
  cookie app): `Configure<AlvoAuthOptions>(GetSection("Alvo:Auth"))`; `Configure<AlvoSecretOptions>(GetSection(
  AlvoSecretOptions.ConfigurationSection))` — **verify** whether `AddAlvo` already binds `Alvo:Secrets` (grep
  `ConfigurationSection` usages) and drop the line if it does; `builder.Services.AddAlvoProblemDetails()` (this host
  owns nothing else that renders errors); `AddFieldServiceServices(configuration)`;
  `AddAlvo(alvo => alvo.UsePostgreSql(connectionString).FromDescriptor(options.DescriptorPath).AddDataApi(_ => { })
  .AddFieldService())`. `Build`: `UseExceptionHandler()`, `app.MapAlvo()` (**verify** in
  `src/MMLib.Alvo/AlvoEndpointRouteBuilderExtensions.cs:70` that it maps health, Data API and `/management`; else add
  `MapAlvoManagement()`), `app.MapFieldService()`. Connection string `ConnectionStrings:FieldService`; refuse to start
  with a clear message when absent.
- [ ] **Step 3: Test world + boot test.** `FieldServiceWorld.StartAsync(FakeTimeProvider?)` wraps
  `AlvoTestHost.StartAsync(builder => { builder.FromDescriptor(Descriptor).AddFieldService(); builder.Services
  .AddFieldServiceServices(config).AddSingleton<IFieldServiceNotifier>(notifier); }, time, cancellationToken: ct)`
  with `Descriptor` copied to output via `<None Include="../../examples/field-service/field-service.alvo.json"
  CopyToOutputDirectory="PreserveNewest" />`. csproj: `AlvoSharedArchTests=false`, references the sample,
  `MMLib.Alvo.TestHost`, `Microsoft.AspNetCore.TestHost`, `FrameworkReference Microsoft.AspNetCore.App`. First fact:
  the descriptor boots, and a typed create round-trips `WorkOrder.Create(…)` with `JobNumber` assigned
  (`JOB-2026-00001` under a fake clock at 2026-11-13).
- [ ] **Step 4:** `dotnet build`, `dotnet test test/MMLib.Alvo.Samples.FieldService.Tests`; `scripts/test-ring0`.
  **Commit** — `feat(samples): scaffold the field-service embedded host over the shared descriptor`.

## Task B2: Before-write hook — scheduled within the customer's service hours

**Files:** Create `samples/…/Scheduling/WithinServiceHours.cs`, `test/…/Scheduling/WithinServiceHoursTests.cs`;
register in `FieldServiceSetup.AddFieldService`.

**Interfaces — Produces:** hook id `within-service-hours`; refusal code `outside-service-hours` at `/scheduled_for`.

- [ ] **Step 1: Failing tests** (shape of `OpeningDeskHooksTests`): a customer `opens 8, closes 17`; scheduling at
  09:00 Bratislava is stored; at 18:30 is refused `AlvoWriteRejectedException` `(Invalid, "outside-service-hours",
  "/scheduled_for")` and nothing is written; a customer with no hours accepts any time; an update that does not
  change `scheduled_for` does not query (assert with `alvo.Hooks.AssertRan` counts only where the snippet shows it).
- [ ] **Step 2: Implement** (snippet: `website/src/snippets/extending/write-hooks/OpeningDeskHooks.cs`):
  ```csharp
  public sealed class WithinServiceHours(IOptions<FieldServiceOptions> options) : IBeforeWriteHook<WorkOrder>
  {
      public async ValueTask BeforeWriteAsync(BeforeWrite<WorkOrder> write, CancellationToken cancellationToken)
      {
          if (write.New.ScheduledFor is not { } at || write.Old?.ScheduledFor == at)
          {
              return;
          }

          var customerId = write.New.CustomerId;
          var customers = await write.Transaction.AsSystem()
              .QueryAsync<Customer>(x => x.Id == customerId, limit: 1, cancellationToken);
          if (customers.Count == 1 && !ServiceHours.Admit(customers[0], at, options.Value.TimeZone))
          {
              write.Invalid("outside-service-hours", "The customer does not accept visits at that time.", "scheduled_for");
          }
      }
  }
  ```
  `ServiceHours.Admit` (static, same folder): convert to the zone, compare the local hour with
  `ServiceOpensHour`/`ServiceClosesHour` (null = unrestricted). Register `.Entity<WorkOrder>(o =>
  o.BeforeWrite<WithinServiceHours>("within-service-hours"))`.
- [ ] **Step 3:** tests green; ring0. **Commit** — `feat(samples): schedule only inside a customer's service hours`.

## Task B3: The `dispatch` command — assign and book in one unit

**Files:** Create `samples/…/Dispatch/DispatchCommand.cs`, `samples/…/Dispatch/DispatchRequest.cs`,
`test/…/Dispatch/DispatchCommandTests.cs`, `test/…/HttpWorld.cs` (TestServer world, shape of
`StayDeskCommandsTests.InitializeAsync`: SQLite in-memory shared cache, `UseTestServer`, dev keys,
`AlvoEventDispatchMode.Manual`).

**Interfaces — Produces:** `POST /commands/work-orders/{id:guid}/dispatch`, name `work_orders.dispatch`,
body `DispatchRequest(Guid TechnicianId, DateTimeOffset StartsAt, int DurationMinutes)`; answers 200
`{ workOrder, visit, jobNumber }`.

- [ ] **Step 1: Failing tests** (HTTP, `X-Alvo-Api-Key`): dispatcher dispatches a `new` job → 200, job `scheduled` with
  assignee and time, one visit; same `Idempotency-Key` again → identical body, still one visit; technician → 403 and
  nothing written; a job already `scheduled` → 403 (the rule reads `status`); a slot overlapping that technician's
  existing visit → 409 and the job is still `new` with no new visit (**the unit rolled back**); a slot outside
  service hours → 422 `outside-service-hours`, job still `new`, no visit.
- [ ] **Step 2: Implement** (snippet: `commands/StayDeskCommands.cs`):
  ```csharp
  public static WebApplication MapDispatch(this WebApplication app)
  {
      app.MapAlvoCommand<WorkOrder>(
          "work_orders.dispatch",
          "/commands/work-orders/{id:guid}/dispatch",
          (order, caller) => order.Status == WorkOrderStatus.New
              && (caller.HasRole(FieldServiceRoles.Dispatcher) || caller.HasRole(FieldServiceRoles.Admin)),
          DispatchAsync);
      return app;
  }

  private static async Task<IResult> DispatchAsync(Guid id, DispatchRequest request, AlvoCommandContext ctx, CancellationToken ct)
  {
      var order = ctx.Record!.As<WorkOrder>();
      Guid? visit = null;
      await ctx.TransactAsync(async (unit, token) =>
      {
          await unit.UpdateAsync(id, WorkOrder.Patch()
              .SetAssignedTo(request.TechnicianId)
              .SetScheduledFor(request.StartsAt)
              .SetStatus(WorkOrderStatus.Scheduled), cancellationToken: token);
          var created = await unit.CreateAsync("visits", Visits.Booking(order, request), token);
          visit = (Guid)created["id"]!;
      }, cancellationToken: ct);

      return TypedResults.Ok(new { workOrder = id, visit, jobNumber = order.JobNumber });
  }
  ```
  **Verify** the unit's `UpdateAsync` accepts a generated `IAlvoPatch<WorkOrder>` (the snippet passes
  `AlvoPatch.For<Stay>()…`; fall back to `AlvoPatch.For<WorkOrder>().Set(x => x.Status, …)` if not) and what
  `unit.CreateAsync` returns. `Visits.Booking` builds the dictionary (`tenant_id` = `order.TenantId`, `work_order_id`,
  `technician_id`, `starts_at`, `ends_at = starts_at + duration`). Put `[Description]` on `DispatchRequest` and its
  members (the contract lint). Map from `FieldServiceSetup.MapFieldService`.
- [ ] **Step 3:** tests green; ring0. **Commit** — `feat(samples): dispatch a job and book its visit atomically`.

## Task B4: Inbound Standard Webhook from the technician app

**Files:** Create `samples/…/Mobile/JobCompletedReceiver.cs`, `samples/…/Mobile/JobCompleted.cs`,
`test/…/Mobile/JobCompletedReceiverTests.cs`

**Interfaces — Produces:** `POST /hooks/technician-app`, secret name `technician_app`
(`Alvo:Secrets:Values:technician_app`), body `JobCompleted(Guid WorkOrderId, Guid TenantId, DateOnly CompletedOn,
int MinutesOnSite)`.

- [ ] **Step 1: Failing tests** (sign as `StayDeskCommandsTests.NotifyAsync` does): a signed completion of an
  `in_progress` job → 200, job `completed`, `completed_on` set, the visit's `minutes_on_site` set, the job's
  `on_site_minutes` equals it; of a `scheduled` job → also completed (started then completed: two legal moves);
  same `webhook-id` again → 200 and no second write (`updated_at` unchanged); already completed under a new id →
  200, unchanged; wrong secret → 401 and nothing written; a body naming the other tenant's job → the job is not found
  in that tenant → 404/handled per the receiver's contract (pin it).
- [ ] **Step 2: Implement** (snippet: `RecordPaymentAsync`):
  ```csharp
  app.MapAlvoWebhookReceiver("/hooks/technician-app", "technician_app", CompleteAsync,
      options => options.ResolveTenant = (_, body, _) =>
          ValueTask.FromResult<TenantId?>(new TenantId(JsonSerializer.Deserialize<JobCompleted>(body.Span, Json.Web)!.TenantId)));
  ```
  `CompleteAsync(JobCompleted notice, AlvoCaller caller, IAlvoTransactions transactions, CancellationToken ct)`:
  `transactions.TransactAsync(caller.Context, …)`; inside, read the job (`unit.GetAsync<WorkOrder>` — **verify** the
  unit's typed read API, else the untyped one and `.As<WorkOrder>()`); return when `Completed`; record minutes on the
  job's visit **first** (its lookup check reads the job's status), then move `Scheduled → InProgress` if needed, then
  `→ Completed` with `SetCompletedOn`. The tenant comes from the **signed body** only (commands.mdx: never path/query/
  headers). Keep each step a named private method (≤ 25 lines).
- [ ] **Step 3:** tests green; ring0. **Commit** — `feat(samples): complete a job from the technician app's signed webhook`.

## Task B5: Notifications — the customer, and the SLA escalation

**Files:** Create `samples/…/Notifications/IFieldServiceNotifier.cs`, `HttpFieldServiceNotifier.cs`,
`NotifyCustomerOfVisit.cs`; `samples/…/Sla/SlaBreached.cs`, `EscalateSlaBreach.cs`;
`test/…/Notifications/NotifyCustomerOfVisitTests.cs`, `test/…/Sla/EscalateSlaBreachTests.cs`,
`test/…/RecordingNotifier.cs`

**Interfaces — Produces:**
```csharp
public interface IFieldServiceNotifier
{
    Task NotifyCustomerAsync(VisitNotice notice, CancellationToken cancellationToken);   // POST {url}/customers
    Task PageOnCallAsync(SlaBreachNotice notice, CancellationToken cancellationToken);    // POST {url}/on-call
    Task SendOverdueReportAsync(OverdueReport report, CancellationToken cancellationToken); // POST {url}/reports/overdue
}
```
handler ids `notify-customer-visit`, `escalate-sla-breach`.

- [ ] **Step 1: Failing tests** (fake clock, `DrainEventsAsync`): scheduling a job sends one `VisitNotice` carrying
  the reference, the job number, `contact_email` and the slot; a non-status update sends none. An emergency `new`
  job with `respond_by = now + 2h`: after `Advance(1h)` + drain nothing; after `Advance(2h)` + drain the job's
  priority is 1 and one page was sent; a job scheduled before its deadline is never escalated (the timer's `when`
  went false — cancelled in the write); moving `respond_by` later moves the escalation (shape of
  `BookingLifecycleTests.A_confirmed_booking_is_reminded_a_day_before`).
- [ ] **Step 2: Implement.** Customer notice (snippet: `write-hooks/OpeningDeskHooks.cs`, `SendBookingConfirmation`):
  ```csharp
  .Entity<WorkOrder>(o => o.OnUpdated<NotifyCustomerOfVisit>(
      "notify-customer-visit",
      when: e => e.Changed(x => x.Status) && e.New.Status == WorkOrderStatus.Scheduled))
  ```
  Escalation (snippet: `time/BookingReminders.cs` `AddEventHandler<TEvent, THandler>` + `[AlvoEvent]`):
  `[AlvoEvent("work_orders.sla_breached")] public sealed record SlaBreached(string Entity, Guid Id, string Timer,
  DateTimeOffset At);` and `.AddEventHandler<SlaBreached, EscalateSlaBreach>("escalate-sla-breach")`; the handler
  updates `priority = 1` with `IAlvoData.UpdateAsync("work_orders", id, …, context.Caller, …)` (the system in the
  event's tenant, holding `admin`) and pages on-call. **Verify first** that a typed record binds a declarative
  timer's payload (the state-machines snippet reads it untyped, via `context.Event.Data.Record["timer"]`); if it does
  not, register the untyped delegate form from `state-machines/BookingLifecycleTests.cs` and resolve the handler
  class from `context`'s services — whichever the docs show — and note it in the sample README.
  `HttpFieldServiceNotifier` via `services.AddHttpClient<IFieldServiceNotifier, HttpFieldServiceNotifier>()`,
  base address `FieldService:NotificationsUrl`; `PostAsJsonAsync`, `EnsureSuccessStatusCode()` (a failure retries the
  delivery — that is the point of a handler).
- [ ] **Step 3:** tests green; ring0. **Commit** — `feat(samples): tell the customer, and escalate a breached SLA`.

## Task B6: The nightly overdue report — a per-tenant cron schedule

**Files:** Create `samples/…/Reports/OverdueReport.cs` (job + record), `samples/…/Tenancy/ConfiguredTenantCatalog.cs`,
`test/…/Reports/OverdueReportTests.cs`

**Interfaces — Produces:** schedule id `overdue-report`, scope `EachTenant`; `IAlvoTenantCatalog` listing
`FieldService:Tenants`.

- [ ] **Step 1: Failing test** (shape of `BookingRemindersTests`): two tenants, an overdue `scheduled` job in north
  (`scheduled_for` yesterday) and an on-time one, an overdue `new` emergency in south past `respond_by`; drain once,
  advance the fake clock to 06:00 Bratislava, drain twice → exactly two reports, north's names only its overdue
  reference, south's only its own.
- [ ] **Step 2: Implement** (snippet: `RollSlots`, `AddSchedule`, `AlvoScheduleOptions.Scope`):
  ```csharp
  .AddSchedule<OverdueReportJob>("overdue-report", cron, "Europe/Bratislava", o =>
  {
      o.Scope = AlvoScheduleScope.EachTenant;
      o.Description = "Every morning, per tenant, the open jobs past their slot or their respond-by time.";
  })
  ```
  The job queries open jobs with `context.Data.QueryAsync<WorkOrder>(x => x.Status == WorkOrderStatus.New ||
  x.Status == WorkOrderStatus.Scheduled, limit: 500, ct)` (**verify** the typed query extension on the job's session),
  filters overdue in memory against `context.ScheduledFor`, and sends one `OverdueReport(tenant, at, references)`.
  The cron string comes from `FieldServiceOptions.OverdueReportCron` — read it where `AddFieldService` is composed
  (pass options in, do not resolve services in the builder).
- [ ] **Step 3:** tests green; ring0. **Commit** — `feat(samples): a per-tenant overdue report every morning`.

## Task B7: A technician's list shows open jobs only

**Files:** Create `samples/…/Reads/TechnicianOpenJobs.cs`, `test/…/Reads/TechnicianOpenJobsTests.cs`

- [ ] **Step 1: Failing test:** a technician with one completed and one scheduled assigned job lists one; a dispatcher
  lists both (control); the technician's `GET` of the completed job by id is still 200 (a narrowing is a view, not a
  boundary — pin whatever the reads page promises for `get`).
- [ ] **Step 2: Implement** (snippet: `reads/ReadsDeskExtensions.cs`):
  ```csharp
  public sealed class TechnicianOpenJobs : IQueryNarrowing<WorkOrder>
  {
      public Expression<Func<WorkOrder, bool>>? Narrow(QueryNarrowing query) =>
          IsDesk(query.Caller) ? null
              : x => x.Status == WorkOrderStatus.New || x.Status == WorkOrderStatus.Scheduled || x.Status == WorkOrderStatus.InProgress;

      private static bool IsDesk(AlvoContext caller) =>
          caller.Roles.Any(r => r.Name == FieldServiceRoles.Dispatcher || r.Name == FieldServiceRoles.Admin);
  }
  ```
  Register `.Entity<WorkOrder>(o => o.Narrow<TechnicianOpenJobs>("technician-open-jobs"))`.
- [ ] **Step 3:** tests green; ring0. **Commit** — `feat(samples): narrow a technician's job list to open jobs`.

## Task B8: Image, compose stack, `scripts/test-e2e` wiring

**Files:** Create `samples/MMLib.Alvo.Samples.FieldService/Dockerfile`, `docker-compose.field-service-host.yml`,
`test/teapie-field-service-host/env.example.json`; Modify `examples/field-service/demo-identities.env`,
`scripts/test-e2e`, `.dockerignore` (only if it excludes `samples/`).

**Interfaces — Produces:** env `ALVO_FSH_PORT=8082`, `ALVO_FSH_SINK_PORT=8092`, per-run `ALVO_FS_TECH_APP_SECRET`;
TeaPie vars `fshBaseUrl`, `fshSinkUrl`, `techAppSecret`.

- [ ] **Step 1: Dockerfile** — the two-stage shape of `src/MMLib.Alvo.Host/Dockerfile`: copy `global.json
  Directory.Build.props Directory.Packages.props .editorconfig`, `schema/`, `src/`, `samples/Directory.Build.props`,
  the sample directory and `examples/field-service/field-service.alvo.json` (the generator reads it at build time);
  `dotnet restore` + `dotnet publish` the sample (`HUSKY=0` in the *image build* only, as the host Dockerfile does —
  that is the image, not a commit); final stage `aspnet:10.0-alpine` + `tzdata`, descriptor at
  `/alvo/descriptor.json`, `USER $APP_UID`, `ENTRYPOINT ["dotnet", "MMLib.Alvo.Samples.FieldService.dll"]`. Add
  `plugins/alvo/skills/` only if restore demands it.
- [ ] **Step 2: Compose** `docker-compose.field-service-host.yml` (`name: alvo-field-service-host`): `postgres`
  (db `alvo_field_service_host`), `alvo` (build the Dockerfile; `ConnectionStrings__FieldService`,
  `FieldService__DescriptorPath: /alvo/descriptor.json`, `FieldService__NotificationsUrl:
  http://localhost:9090/notifications`, `FieldService__Tenants__0/1` from the env file,
  `Alvo__Secrets__Values__technician_app: ${ALVO_FS_TECH_APP_SECRET:?…}`, the **same five dev keys** as
  `docker-compose.field-service.yml` (copy the block and its comments' intent), ports `127.0.0.1:${ALVO_FSH_PORT}:8080`
  and `127.0.0.1:${ALVO_FSH_SINK_PORT}:9090`, healthcheck on `/health/ready`), and the same `sink` sidecar as A3.
  Header comment: why a third file (the A3/README reasoning), how to run it.
- [ ] **Step 3: `scripts/test-e2e`.** Add `FIELD_SERVICE_HOST_COMPOSE=(--env-file …demo-identities.env -f
  …docker-compose.field-service-host.yml)`; `export ALVO_FS_TECH_APP_SECRET="${ALVO_FS_TECH_APP_SECRET:-whsec_$(openssl
  rand -base64 32)}"`; env JSON keys `fshBaseUrl`, `fshSinkUrl`, `techAppSecret`; `down`/`build`/`up --wait
  --wait-timeout 60`/logs-on-failure/teardown for the third stack exactly as for the second;
  `HOST_REPORT_FILE="$REPORT_DIR/report-field-service-host.xml"` and a third `teapie test
  test/teapie-field-service-host` invocation. Update the header comment: **three** stacks, what the third answers
  ("does the same descriptor plus host code behave as the sample claims?").
- [ ] **Step 4: Verify** — `scripts/test-e2e` with an empty host suite folder containing one smoke case
  (`001-Health/001-ready-req.http`, `GET {{fshBaseUrl}}/health/ready`, 200). Measure the whole run's wall time and
  compare with CI's `timeout-minutes: 20`; if a cold run on a CI-sized runner would exceed ~15 min, raise it in
  `.github/workflows/ci.yml` by the measured amount and say so in the PR.
- [ ] **Step 5: Commit** — `build(e2e): run the field-service host as a third stack`.

## Task B9: TeaPie host suite — seed, dispatch, mobile

**Files:** Create `test/teapie-field-service-host/{README.md,_shared/Rows.csx}` (copy the field-service one — same
helpers; a `#load` across suites would couple two collections), `010-Seed/…`, `020-Dispatch/001-dispatch-{req.http,test.csx}`,
`030-Mobile/001-job-completed-{init.csx,req.http,test.csx}`.

- [ ] **Step 1: Seed** — region `NORTH` (admin), customers `Kaviareň Dunaj` (`service_opens_hour: 7`,
  `service_closes_hour: 19`) in north and one in south, jobs `WO-7001…7004` `new` (one emergency without
  `respond_by`). Capture ids.
- [ ] **Step 2: Dispatch** (spec §6): requests `DispatchWo7001` with `Idempotency-Key: dispatch-7001`, the replay,
  `TechnicianDispatches` (403), `OutsideHours` (`StartsAt` 21:00 Bratislava → 422), `DoubleBooking` (another job, same
  technician, overlapping → 409), and reads proving no partial write:
  ```http
  ### The refused double booking left the job exactly as it was: the unit is all or nothing
  # @name ReadWo7003AfterRefusal
  ## TEST-EXPECT-STATUS: [200]
  GET {{fshBaseUrl}}/api/work_orders/{{Wo7003Id}}
  X-Alvo-Api-Key: {{keyDispatcherNorth}}
  ```
  plus `GET /api/visits?work_order_id=eq.{{Wo7003Id}}` → `items: []`. Tests assert status, assignee, visit count,
  the byte-identical replay, the problem types/codes.
- [ ] **Step 3: Mobile** — `001-job-completed-init.csx` builds and signs the body (TeaPie runs `-init.csx` before the
  requests):
  ```csharp
  using System.Security.Cryptography;
  using System.Text;

  var id = $"msg_{Guid.NewGuid():N}";
  var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
  var body = $"{{\"workOrderId\":\"{tp.GetVariable<string>("Wo7001Id")}\",\"tenantId\":\"{tp.GetVariable<Guid>("tenantNorth")}\",\"completedOn\":\"2026-11-13\",\"minutesOnSite\":95}}";
  var secret = tp.GetVariable<string>("techAppSecret");
  var key = Convert.FromBase64String(secret["whsec_".Length..]);
  var signature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
  tp.SetVariable("MobileId", id);
  tp.SetVariable("MobileTimestamp", timestamp);
  tp.SetVariable("MobileBody", body);
  tp.SetVariable("MobileSignature", $"v1,{signature}");
  ```
  (use `Constant`-style reading if `Wo7001Id` is a `Guid`; **verify** with `teapie compile`). Requests: `Complete`
  (`webhook-id: {{MobileId}}`, `webhook-timestamp`, `webhook-signature`, body exactly `{{MobileBody}}` on one line →
  200), `CompleteAgain` (same three headers → 200), `Forged` (`webhook-signature: v1,AAAA` → 401), `ReadBack`. Tests:
  `completed`, `completed_on`, `on_site_minutes == 95`, `updated_at` unchanged between the first and second read
  (idempotent), 401 changed nothing.
- [ ] **Step 4: Run** `scripts/test-e2e`. **Commit** — `test(e2e): the host's dispatch command and mobile webhook`.

## Task B10: TeaPie host suite — notifications, SLA, report, reads, catalog

**Files:** Create `040-Notifications/001-customer-told`, `050-Sla/001-emergency-escalates` (+ `-init.csx` setting
`RespondBy = UtcNow + 5 s`), `060-Reports/001-run-now`, `070-Reads/001-technician-open-jobs`,
`080-Catalog/001-extensions` — each `-req.http` + `-test.csx`.

- [ ] **Step 1: Notifications** — poll `POST {{fshSinkUrl}}/__admin/requests/find` `{ "method": "POST", "urlPath":
  "/notifications/customers" }` with `RETRY-UNTIL-TEST-PASS` (A7's directives); assert one notice for `WO-7001` with
  its `contact_email` and job number.
- [ ] **Step 2: SLA** — create `WO-7101` (`new`, `is_emergency: true`, `priority: 3`, `respond_by: {{RespondBy}}`) and
  control `WO-7102` (same, then dispatched before its deadline); poll `GET /api/work_orders/{{Wo7101Id}}` with
  `RETRY-UNTIL-TEST-PASS: WO-7101 escalated to priority 1.` (≤ 30 × 1 s); then assert the on-call page in the sink and
  that `WO-7102` is still priority 3.
- [ ] **Step 3: Report** — admin `POST {{fshBaseUrl}}/management/projects/field-service/schedules/overdue-report/run`
  (pin the status `ManagementEndpoints.Operations.cs:137` answers), dispatcher same → 403; poll the sink for
  `/notifications/reports/overdue`: two reports, north's lists the overdue north job (seed one with `scheduled_for`
  in the past via a dispatcher PATCH before the run) and no south reference.
- [ ] **Step 4: Reads** — tech-north lists `?select=reference,status`; `WO-7001` (completed in B9) absent; dispatcher
  list contains it (control).
- [ ] **Step 5: Catalog** — admin `GET /management/projects/field-service/extensions`; the `id` set ⊇ `{
  "work_orders.dispatch", "within-service-hours", "notify-customer-visit", "escalate-sla-breach", "overdue-report",
  "technician-open-jobs" }` (verify the command's listed id).
- [ ] **Step 6: Run** `scripts/test-e2e`. **Commit** — `test(e2e): the host's handlers, schedule, narrowing and catalog`.

## Task B11: Docs

**Files:** Create `samples/MMLib.Alvo.Samples.FieldService/README.md`,
`website/src/content/docs/guides/advanced-demo.mdx` (frontmatter + sidebar `order` consistent with its siblings —
read two of them first); Modify `examples/field-service/README.md` (link the host layer), `CHANGELOG.md`,
`test/teapie-field-service-host/README.md`.

- [ ] **Step 1:** Sample README: run it (compose), the slice map (H1–H8 → folder → TeaPie file), what is configuration
  (cron, notifier URL, tenants, time zone) and why nothing is test-only; the egress/sidecar note.
- [ ] **Step 2:** Docs page: the story of §1 in the spec, the two layers, links to the extending pages for each
  construct. Reference snippets only where `website/src/snippets` already holds them (DocsGen/snippet tests own that
  tree); otherwise prose + links. Run `cd website && npm run build` if Node is available (not a required check).
- [ ] **Step 3: Commit** — `docs: the field-service advanced demo, both layers`.

## Task B12: Rings, review, PR

- [ ] **Step 1:** `scripts/test-ring1`, `scripts/test-ring2` (the sample builds in the solution; its tests are ring0).
- [ ] **Step 2:** Code-review and security-review **substitutes** (the slash commands are user-only — dispatch reviewer
  subagents and label them as such): the receiver's tenant resolution, the command's rule, the notifier's outbound
  HTTP. Fix findings.
- [ ] **Step 3:** `alvo-plan-guard`; `alvo-pr-report`; `gh pr create` from `demo/field-service-host` (base: PR-A's
  branch until it merges, then `main`). No closing keywords.
