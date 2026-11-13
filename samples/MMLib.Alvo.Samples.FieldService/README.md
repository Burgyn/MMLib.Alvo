# MMLib.Alvo.Samples.FieldService — the advanced demo's host layer

An embedded Alvo host over **the same descriptor** the standalone stack serves,
`examples/field-service/field-service.alvo.json`, adding only what that file cannot say. It is the second layer of the
advanced demo; the first — the state machine, gapless job numbers, the SLA timer, CHECKs, the partial unique index, the
visit calendar's exclusion, the relation rule, the lookup check, the rollup and the billing webhook — is pure JSON, runs
unchanged in the standalone image, and is documented in [`examples/field-service/README.md`](../../examples/field-service/README.md).

**C# is the last resort here, on purpose.** Host code is code the operator cannot see, the policy simulator cannot show
and the dashboard assistant cannot change. Every slice below exists because the descriptor has no construct for it, and
each class's remarks say why.

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

Then `http://localhost:8082/api/work_orders` with `X-Alvo-Api-Key: dispatcher-north.$ALVO_FS_DISPATCHER_NORTH_SECRET`,
and the sink's journal — billing and every notification — on `http://localhost:8092/__admin/requests`.
`scripts/test-e2e` does all of this with fresh secrets and runs `test/teapie-field-service-host` against it.

`dotnet run --project samples/MMLib.Alvo.Samples.FieldService` works too, given `ConnectionStrings__FieldService` (a
PostgreSQL database), `FieldService__NotificationsUrl`, the dev keys under `Alvo:Auth` and
`Alvo:Secrets:Values:technician_app`. It runs on PostgreSQL only; the unit tests use `MMLib.Alvo.TestHost` (SQLite).

## The slices

Vertical slices over the types the build generates from the descriptor (`AdditionalFiles … AlvoDescriptor="true"`):
`WorkOrder`, `WorkOrderStatus`, `WorkOrderPatch`, `Visit`, `Customer`, `FieldServiceRoles`. A renamed field breaks the
build, and the boot check warns about a class that no longer fits the live schema.

| Slice | Extension | Why the descriptor cannot do it | Unit tests | TeaPie |
|---|---|---|---|---|
| `Dispatch/` | command `work_orders.dispatch` | Two writes to two entities (the job's `schedule` move and the visit) must commit together; the Data API writes one row per request. | `Dispatch/DispatchCommandTests` | `020-Dispatch` |
| `Mobile/` | inbound Standard Webhook `/hooks/technician-app` | The descriptor describes what Alvo sends, not what it receives: a signed, deduplicated inbound call with the tenant in the signed body. | `Mobile/JobCompletedReceiverTests` | `030-Mobile` |
| `Scheduling/` | before-write hook `within-service-hours` | It reads another row (the customer's hours) and converts into a named time zone; rules, CHECKs and lookup checks compile to SQL and have neither. | `Scheduling/WithinServiceHoursTests` | `020-Dispatch` |
| `Notifications/` | handler `notify-customer-visit` | The message goes to an SMS or e-mail gateway with its own client, not to a webhook endpoint. | `Notifications/NotifyCustomerOfVisitTests` | `040-Notifications` |
| `Sla/` | handler `escalate-sla-breach` on `work_orders.sla_breached` | The descriptor arms the timer, but descriptor automation cannot subscribe to a timer's event. | `Sla/EscalateSlaBreachTests` | `050-Sla` |
| `Reports/`, `Tenancy/` | schedule `overdue-report` (`EachTenant`) + `IAlvoTenantCatalog` | A cron automation takes no condition and runs per tenant only with a tenant catalog, which the standalone image does not register. | `Reports/OverdueReportTests` | `060-Reports` |
| `Reads/` | narrowing `technician-open-jobs` | A view for the technician app, not a permission: in the `list` rule it would forbid a technician their own finished job. | `Reads/TechnicianOpenJobsTests` | `070-Reads` |

Every slice is registered in `FieldServiceSetup` — `AddFieldService` inside `AddAlvo`, `AddFieldServiceServices` for the
host's own services, `MapFieldService` for its routes — and the Management API's extension catalog lists them
(`080-Catalog`).

## Configuration, and why nothing is test-only

| Key | Default | What it is |
|---|---|---|
| `ConnectionStrings:FieldService` | — (required) | The PostgreSQL database. |
| `FieldService:DescriptorPath` | the repository's `examples/field-service/field-service.alvo.json` | The descriptor; `/alvo/descriptor.json` in the image. |
| `FieldService:TimeZone` | `Europe/Bratislava` | The zone service hours and the overdue report's cron are read in. |
| `FieldService:NotificationsUrl` | — (required, validated on start) | Where notices, pages and reports are posted. |
| `FieldService:OverdueReportCron` | `0 6 * * *` | When the overdue report runs. |
| `FieldService:Tenants:N` | — | The tenants the per-tenant report runs for. |
| `Alvo:Secrets:Values:technician_app` | — | The technician app's `whsec_` signing secret. |

The end-to-end suite changes none of these for its own sake. The SLA deadline is data — a job's `respond_by`, sent a
few seconds ahead — and the overdue report is started with the Management API's *Run now*
(`POST /management/projects/field-service/schedules/overdue-report/run`, the `developer` level, which the descriptor's
`access` grants the `admin` role). No debug route, no shortened constant, no clock control.

## The sink, and the egress guard

In the compose stack the WireMock `sink` shares the Alvo container's network namespace, so `http://localhost:9090` is
loopback: the descriptor's billing webhook passes the egress guard with nothing allow-listed, and the host's notifier
posts to the same sink under `/notifications/…`. A real deployment points `FieldService:NotificationsUrl` at its
gateway; the notifier throws on a failed post, so the handler's delivery retries and finally dead-letters on
*Operations* rather than losing the message.
