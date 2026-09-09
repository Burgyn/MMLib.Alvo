# Alvo, embedded — the fleet desk sample

Somebody else's ASP.NET Core app that keeps its records in Alvo. This is the runnable answer to
*"how do I put Alvo inside my own host?"* — the second of Alvo's two distribution modes (spec §"Režim 2"),
and the half of [#24] that `docs/architecture/extensibility.md` documented but nothing demonstrated.

## Run it

```bash
dotnet user-secrets --project samples/MMLib.Alvo.Samples.EmbeddedHost \
  set "Alvo:Auth:DevKeys:0:Secret" "$(openssl rand -hex 16)"

dotnet run --project samples/MMLib.Alvo.Samples.EmbeddedHost
```

**The secret is required.** No credential ships with this sample: `appsettings.json` declares a dev API key
with no secret, and `AlvoAuthOptionsValidator` refuses to start without one. That is spec
§"Spoločné kontrakty" point 5 (*žiadne default credentials*) applied to a sample.

It listens on `http://localhost:5199` and keeps a SQLite file beside itself. `dotnet run` picks the
`Development` profile from `Properties/launchSettings.json`, which is what makes user-secrets load at all.

## Two surfaces, one backend

This is the thing to understand before reading any of the code.

| | Who | How they reach the data |
|---|---|---|
| `/app/*` | this app's own **cookie** users | the endpoints resolve `IAlvoData` and build an `AlvoContext` from the cookie's claims |
| `/api/alvo/*` | agents and machines, with **`X-Alvo-Api-Key`** | Alvo's generated Data API, mapped by `MapAlvoDataApi()` |
| `/health/live`, `/health/ready` | container probes | `MapAlvoHealth()` |

Both serve `examples/vehicle-registry/vehicles.alvo.json` — **the same descriptor the repository's root
`docker-compose.yml` mounts into the standalone image**. That is not decoration: it makes #24's Definition of
Done (*"both modes start up the same functional backend from the same descriptor"*) a check rather than a
claim, and `test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration` compares the two modes' generated route
sets for equality.

### Try both

```bash
KEY="agent.<the secret you generated>"

# The agent surface: Alvo's own credential, Alvo's own routes.
curl -s -XPOST localhost:5199/api/alvo/owners \
  -H "Content-Type: application/json" -H "X-Alvo-Api-Key: $KEY" \
  -d '{"name":"Fleet Desk Ltd"}'

# This app's surface: its own cookie, its own endpoints, Alvo's policy.
curl -s -c /tmp/fleet -XPOST localhost:5199/app/login \
  -H "Content-Type: application/json" -d '{"user":"inspector"}'
curl -s -b /tmp/fleet localhost:5199/app/vehicles
```

## What each part demonstrates

| Seam | What to look at | Where the rule lives |
|---|---|---|
| One entry point | `AddAlvo(alvo => alvo.UseSqlite(…).FromDescriptor(…).AddDataApi(…))` | `extensibility.md` rules 1 and 4 — `Use*` selects infrastructure, `From*` supplies domain input |
| No apply call | there is none | `AddAlvo` registers a hosted lifecycle service that brings the schema up before anything starts; `/health/ready` gates on it |
| Endpoints are a separate seam | `MapAlvoHealth()` **then** `MapAlvoDataApi()` | rule 10 — both halves stay public, and health maps first because `MapAlvoDataApi` refuses a host whose Data API services are absent |
| The convention builder | `MapAlvoDataApi().WithTags(…)` | #182 — one builder over Alvo's generated routes and nothing else, so a host attaches rate limiting or telemetry without the framework owning it |
| Mounting beside your own routes | `api.RoutePrefix = "/api/alvo"` | `AlvoApiOptions.RoutePrefix` |
| Your errors stay yours | `AddAlvoProblemDetails()` is **not** called | #119 — an embedded host owns its error rendering; the sample renders its own refusals in `Answered` |
| Your users, Alvo's rules | `AsCaller` → `IRoleCatalogProvider` → `RoleCatalog.Resolve` → `AlvoContext` | an application role can only be minted through the catalog the applied descriptor primed, so a typo is refused where it arrives |
| Your wire contract, Alvo's field map | `RepaintRequest` mapped to a field dictionary | a host owns its DTOs; a `Dictionary<string, object?>` bound straight from JSON carries `JsonElement` values `IAlvoData` has no field type for |
| The CSRF guard | `RequireJsonContentType` left at its default | #191 — and this host is exactly the context that default exists for |
| Tenancy, by its absence | `AsCaller` sets no `Tenant` | `vehicles.alvo.json` declares none; a host over a **tenant-scoped** entity must set `AlvoContext.Tenant`, or every request is denied at the decision layer |

**The demonstration worth reading twice** is `PATCH /app/vehicles/{id}`. The descriptor says
`vehicles.update: "'admin' in @user.roles || 'inspector' in @user.roles"`, and this sample contains no
authorization code at all — so a cookie user holding `inspector` repaints a vehicle and a plain
`authenticated` one gets a **404**, because the rule renders to a row-level `USING` predicate and for that
caller the row is *invisible* rather than forbidden.

### About `/app/login`

It is a **development-only** endpoint — mapped only outside production, and a test pins that — and it
issues a cookie with **no credential of any kind**. It takes a demo user's *name* (`inspector` or `clerk`)
and reads that user's roles from a fixed table inside the app.

**It deliberately does not take a role list from the request**, which is the shape it had first. A sign-in
that lets the caller name its own roles is an unauthenticated privilege-escalation endpoint, and "this app
writes no authorization logic of its own" — true, and the good half of this sample — is no comfort if its
*authentication* trusts whatever arrives. Replace this endpoint with your own identity provider; what has to
survive the replacement is that **the roles come from somewhere the caller does not control**.

The cookie's own options are set explicitly rather than defaulted, for the same reason: `SameSite=Lax` is
what stops a cross-site form from carrying it, `Secure` is unconditional outside development, and the
session has a finite lifetime.

## One thing embedded cannot do yet

**A host cannot publish its own principal to the generated Data API.** `AlvoContextFilter` is attached to
every generated route and publishes the principal *it* resolved from the credential header, clearing it
again afterwards — so middleware that sets `IAlvoContextAccessor.Principal` from a cookie has that value
discarded before the endpoint runs.

That is why this sample's cookie users go through `/app/*` and the generated `/api/alvo/*` routes are for
API-key callers. Federating a host's own identity into the generated routes needs a seam that does not
exist; it is filed for F7 and linked from `docs/architecture/extensibility.md`.

**And one thing you must not do to work around it.** `Alvo:Auth:HeaderName` is configuration, so pointing it
at `Cookie` and registering a custom `IAlvoContextResolver` *would* make the browser authenticate Alvo's
routes — and would make every one of them a cross-site-request-forgery target. It is refused at startup for
that reason (see `AlvoAuthOptionsValidator`).

## What the spec sketches and this sample does not use

Spec §"Režim 2" illustrates `AddModule<T>()`, `AddAuthorizationHandler<T>()`, `UseAdmin(…)`, `Hooks(…)`,
`.Embedded(e => e.SchemaPrefix("alvo"))` and `app.MapAlvo("/api/alvo")` with a path argument. **None of
those exists yet** — the spec's own preamble marks those blocks as *"nie kontrakt … ukazujú ambíciu
developer experience"*. This sample uses what is built; the route prefix comes from
`AlvoApiOptions.RoutePrefix`, which is the shipped shape.

[#24]: https://github.com/Burgyn/MMLib.Alvo/issues/24
