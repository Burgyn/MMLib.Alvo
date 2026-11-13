# MMLib.Alvo.Samples.FieldService — the advanced demo's host layer

An embedded Alvo host over **the same descriptor** the standalone stack serves,
`examples/field-service/field-service.alvo.json`. It is the second layer of the advanced demo. The first layer is pure
JSON, runs unchanged in the standalone image, and is documented in
[`examples/field-service/README.md`](../../examples/field-service/README.md): the state machine, gapless job numbers, the
SLA timer, CHECKs, the partial unique index, the visit calendar's exclusion, the relation rule, the lookup check, the
rollup and the billing webhook.

**C# is the last resort here, on purpose.** Host code is code the operator cannot see, the policy simulator cannot show
and the dashboard assistant cannot change. Each slice below says what exactly the descriptor lacks for it, and each
class's remarks say it again. Two slices that were drafted were left out because the descriptor *can* do them:

- **Telling the customer when the technician comes** is a descriptor `automation` job. The trigger is
  `entity.work_orders.updated`, the condition is `changed(status) && new.status == 'scheduled'`, and the action is an
  `email` (or a `webhook` with a `{{…}}` payload template). The demo leaves it out only because it runs no mail provider,
  not because the descriptor cannot say it.
- **A technician's "open jobs only" list** is a descriptor rule. `rules.list` and `rules.get` are separate, so a list
  filtered by status, with each job still readable by id, is a `list` rule. A read narrowing (`IQueryNarrowing`) is for
  a filter whose input only code has, such as a roster or a directory.

## Run it

```bash
# The five key secrets (see examples/field-service/README.md) and the technician app's signing secret.
export ALVO_FS_DISPATCHER_NORTH_SECRET=$(openssl rand -hex 16)
export ALVO_FS_TECH_NORTH_SECRET=$(openssl rand -hex 16)
export ALVO_FS_SPARE_NORTH_SECRET=$(openssl rand -hex 16)
export ALVO_FS_DISPATCHER_SOUTH_SECRET=$(openssl rand -hex 16)
export ALVO_FS_ADMIN_SECRET=$(openssl rand -hex 16)
export ALVO_FS_TECH_APP_SECRET="whsec_$(openssl rand -base64 32)"

docker compose --env-file examples/field-service/demo-identities.env \
  -f docker-compose.field-service-host.yml up --build --wait
```

Then try:

- `http://localhost:8082/api/work_orders` with `X-Alvo-Api-Key: dispatcher-north.$ALVO_FS_DISPATCHER_NORTH_SECRET`;
- the sink's journal, which holds billing, the on-call pages and the overdue reports, at
  `http://localhost:8092/__admin/requests`.

`scripts/test-e2e` does all of this with fresh secrets and runs `test/teapie-field-service-host` against it.

`dotnet run --project samples/MMLib.Alvo.Samples.FieldService` works too. It needs:

- `ConnectionStrings__FieldService`, a PostgreSQL database;
- `FieldService__NotificationsUrl`;
- the dev keys under `Alvo:Auth`;
- `Alvo:Secrets:Values:technician_app`.

The host runs on PostgreSQL only; the unit tests use `MMLib.Alvo.TestHost` (SQLite). The dev keys are demo
authentication: a real embedded host plugs in its own.

## The slices

The slices are vertical, over the types the build generates from the descriptor (`AdditionalFiles …
AlvoDescriptor="true"`): `WorkOrder`, `WorkOrderStatus`, `WorkOrderPatch`, `Visit`, `Customer`, `FieldServiceRoles`.
A renamed field breaks the build, and the boot check warns about a class that no longer fits the live schema.

| Slice | Extension | What exactly the descriptor lacks | Unit tests | TeaPie |
|---|---|---|---|---|
| `Dispatch/` | command `work_orders.dispatch` | A transaction over two entities: the job's `schedule` move and the visit commit together. A Data API request or batch writes one entity. | `Dispatch/DispatchCommandTests` | `020-Dispatch` |
| `Mobile/` | inbound Standard Webhook `/hooks/technician-app` | Inbound webhooks. The descriptor declares what Alvo sends; a signed, deduplicated inbound call is a host route. | `Mobile/JobCompletedReceiverTests` | `030-Mobile` |
| `Scheduling/` | before-write hooks `within-service-hours` (work orders) and `visit-within-service-hours` (visits) | A time-zone-aware hour. Alvo's CEL has no local-hour function, and rules, CHECKs and lookup checks compile to SQL. Comparing a slot with another row's hours in Bratislava time is code. | `Scheduling/*Tests` | `020-Dispatch` |
| `Sla/` | handler `escalate-sla-breach` on `work_orders.sla_breached` | A subscriber for a timer's event. The descriptor arms the timer, but automation cannot subscribe to the event it publishes. | `Sla/EscalateSlaBreachTests` | `040-Sla` |
| `Reports/`, `Tenancy/` | schedule `overdue-report` (`EachTenant`) + `IAlvoTenantCatalog` | A per-tenant scheduled job. A cron automation takes no condition and runs per tenant only with a tenant catalog, which the standalone image does not register. | `Reports/OverdueReportTests` | `050-Reports` |

Every slice is registered in `FieldServiceSetup`, in three steps:

- `AddFieldService`, inside `AddAlvo`;
- `AddFieldServiceServices`, for the host's own services;
- `MapFieldService`, for its routes.

The Management API's extension catalog lists them all (`060-Catalog`).

**Service hours** are checked on both the job and the visit:

- On the job, `scheduled_for` has no end, so only its start is checked.
- On the visit, the whole slot is checked on every write: the dispatch's booking, and a dispatcher's own `POST` or
  `PATCH` of `/api/visits`.
- A visit must start at or after the customer's opening hour (else 422 at `/starts_at`). It must end by the closing hour,
  or by midnight when only an opening hour is set (else 422 at `/ends_at`).
- The local clock decides, so daylight saving moves the verdict.

**The dispatch command checks its input before it writes anything:**

- a missing technician, start or duration, or a duration outside 15–720 minutes, is a 422 naming the field;
- a job that is not `new` any more is a 409 conflict;
- a job in another tenant is 404, because the row is read as the caller.

## Trust: one app secret for every tenant

One `technician_app` secret signs the mobile app's deliveries for **every** tenant. Whoever holds it can complete a job
in any tenant by naming that tenant in the signed body. That is platform-wide trust in the app's backend, and it is fine
when one backend serves every tenant.

A notice naming tenant A still cannot touch tenant B's job. The job is read under tenant A, so the answer is 404.

A notice naming no tenant, or the all-zero one, resolves to no tenant, and every tenant-scoped entity refuses that. A
notice missing any field is 422.

Where each tenant's app signs for itself, give each tenant its own secret and its own receiver (`options.Tenant` per
route, or a route group per tenant). That is the stronger option.

## Configuration, and why nothing is test-only

| Key | Default | What it is |
|---|---|---|
| `ConnectionStrings:FieldService` | — (required) | The PostgreSQL database. |
| `FieldService:DescriptorPath` | the repository's `examples/field-service/field-service.alvo.json` | The descriptor; `/alvo/descriptor.json` in the image. |
| `FieldService:TimeZone` | `Europe/Bratislava` (validated on start) | The zone service hours and the overdue report's cron are read in. |
| `FieldService:NotificationsUrl` | — (required, validated on start) | Where on-call pages and overdue reports are posted. |
| `FieldService:OverdueReportCron` | `0 6 * * *` | When the overdue report runs. |
| `FieldService:Tenants:N` | — | The tenants the per-tenant report runs for. |
| `Alvo:Secrets:Values:technician_app` | — | The technician app's `whsec_` signing secret. |

The end-to-end suite changes none of these for its own sake:

- The SLA deadline is data: a job's `respond_by`, sent a few seconds ahead.
- The overdue report is started with the Management API's *Run now*:
  `POST /management/projects/field-service/schedules/overdue-report/run`. That needs the `developer` level, which the
  descriptor's `access` grants the `admin` role.
- There is no debug route, no shortened constant and no clock control.

A before-write hook has a 250 ms wall-clock budget, and that budget includes the first run's JIT. On a heavily loaded
machine, the first write after a cold start can exceed it (#519). The hooks here are kept cheap for that reason: a read
by id, and a time zone loaded at start.

## The sink, and the egress guard

In the compose stack, the WireMock `sink` shares the Alvo container's network namespace, so `http://localhost:9090` is
loopback. The descriptor's billing webhook passes the egress guard with nothing allow-listed, and the host's notifier
posts to the same sink under `/notifications/…`.

A real deployment points `FieldService:NotificationsUrl` at its gateway. The notifier throws on a failed post, so the
handler's or job's delivery retries and finally dead-letters on *Operations*, rather than losing the message.
