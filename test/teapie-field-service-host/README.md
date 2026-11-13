# test/teapie-field-service-host — does the same descriptor plus host code behave as the sample claims?

The third TeaPie suite in `scripts/test-e2e`, against `docker-compose.field-service-host.yml` (`:8082`, sink `:8092`):
`samples/MMLib.Alvo.Samples.FieldService` — an embedded host over the **same** `examples/field-service` descriptor the
standalone stack serves — on PostgreSQL, with a WireMock sink in its network namespace that receives both the
descriptor's billing webhook and the host's notifications.

`test/teapie-field-service` proves what the descriptor alone does. This suite proves only what the host adds, and that
the descriptor still judges every write the host makes.

## Run it

`scripts/test-e2e` — it builds the sample's image, starts all three stacks with fresh secrets (including the technician
app's `whsec_` signing secret, `ALVO_FS_TECH_APP_SECRET`), and writes the TeaPie environment from the same values
(`fshBaseUrl`, `fshSinkUrl`, `techAppSecret`, the five keys, the tenant and user ids). `env.example.json` shows the
shape for a hand run against a stack you started yourself.

## The groups

| Group | Asserts | Host code under test |
|---|---|---|
| `010-Seed` | Region, customers (north with service hours 07–19), jobs `WO-7001…7006`; job numbers per tenant. | — (the descriptor) |
| `020-Dispatch` | `dispatch` schedules, assigns and books one visit; the `Idempotency-Key` retry is the first answer byte for byte and books nothing; a technician who can read the job is 403; 21:00 Bratislava is 422 `outside-service-hours` at `/scheduled_for`; a double-booked technician is 409 `exclusion`; both refusals leave the job `new`, unscheduled, with **no** visit. | `Dispatch/DispatchCommand`, `Scheduling/WithinServiceHours` |
| `030-Mobile` | A forged signature is 401 and changes nothing; the signed "job completed" starts and completes a scheduled job, sets `completed_on`, records the minutes on the visit and (through the descriptor's rollup) on the job; the same delivery again changes nothing (`updated_at`). | `Mobile/JobCompletedReceiver` |
| `040-Notifications` | The customer of WO-7001 was told once, with the job number, contact and slot; the refused dispatches told nobody. | `Notifications/NotifyCustomerOfVisit` |
| `050-Sla` | An emergency with `respond_by` 8 s ahead reaches priority 1 on its own and on-call is paged once; the control dispatched in time keeps priority 3 and pages nobody. | `Sla/EscalateSlaBreach` |
| `060-Reports` | A dispatcher's *Run now* is 403; the admin's starts one run per tenant; north's report is exactly `WO-7006, WO-7101`, south's exactly `WO-7005`. | `Reports/OverdueReportJob`, `Tenancy/ConfiguredTenantCatalog` |
| `070-Reads` | tech-north lists exactly their open jobs (`WO-7002`, `WO-7006`); the dispatcher still sees completed WO-7001; tech-north's GET of it is 404. | `Reads/TechnicianOpenJobs` |
| `080-Catalog` | The extension catalog lists the command, the hook, both handlers, the schedule and the narrowing. | the registration |

The groups run in order and build on each other: 020 dispatches WO-7001, 030 completes it, 060 reports what 050 left
overdue, 070 reads what 030 completed.

## Nothing here is a test switch

Timing comes from data and configuration, never from code the host carries for the suite (spec ruling R8): the SLA
deadline is the job's own `respond_by`, sent a few seconds ahead; the overdue report keeps its production cron and is
started with the Management API's *Run now*; notifications go to whatever `FieldService:NotificationsUrl` names.

## The bar, and the TeaPie behaviours

The same as `test/teapie-field-service/README.md` — parsed shapes, exact sets, a control for every claim, a `Capture:`
test first where a case hands ids on, `Constant(name)` for GUID-shaped environment values, `###` before every request,
and a `RETRY-UNTIL-TEST-PASS` request read through `tp.Response` as its file's last request. `_shared/Rows.csx` is a copy
of that suite's, not a `#load` across collections.

One more: an `-init.csx` runs **before** the requests, which is what `030-Mobile` uses to build the exact body the
request sends and sign those bytes with the per-run secret.
