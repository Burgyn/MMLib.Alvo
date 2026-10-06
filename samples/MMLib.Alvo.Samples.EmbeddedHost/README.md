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
| The CSRF guard | nothing to configure — the JSON `Content-Type` requirement is unconditional | #191 — and this host is exactly the context it exists for |
| Tenancy, by its absence | `AsCaller` sets no `Tenant` | `vehicles.alvo.json` declares none; a host over a **tenant-scoped** entity must set `AlvoContext.Tenant`, or every request is denied at the decision layer |
| A function of your own | `.AddCelFunction("normalizeVin", NormalizeVin, NormalizeVinSummary)` at the end of the `AddAlvo` chain | [`cel.md`, "Host functions"][cel-host] — pure, fast, thread-safe; listed by `cel/functions` as the host's. Walkthrough: [Registering a CEL function](#registering-a-cel-function) below |

**The demonstration worth reading twice** is `PATCH /app/vehicles/{id}`. The descriptor says
`vehicles.update: "'admin' in @user.roles || 'inspector' in @user.roles"`, and this sample contains no
authorization code at all — so a cookie user holding `inspector` repaints a vehicle and a plain
`authenticated` one gets a **404**, because the rule renders to a row-level `USING` predicate and for that
caller the row is *invisible* rather than forbidden.

### About `/app/login`

It is a **development-only** endpoint — mapped only when the environment is `Development`, and a test
pins that for `Production` *and* `Staging` — and it
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

## Registering a CEL function

A descriptor's hooks can call a function written in your own C#. Alvo ships built-in ones (`trim`, `replace`,
`upperAscii`, `math.round`, …); a host adds its own with `AddCelFunction`, and from then on a hook condition or a
before-hook `mutate` value can call it by name, exactly like a built-in. This sample registers one, `normalizeVin`,
which turns whatever a caller typed into the canonical form of a vehicle identification number.

### 1. Register the function

At the end of the `AddAlvo` chain in `SampleHost.CreateBuilder`:

```csharp
builder.Services.AddAlvo(alvo => alvo
    .UseSqlite($"Data Source={DatabasePath(builder)}")
    .FromDescriptor(DescriptorPath(builder))
    .AddDataApi(api => api.RoutePrefix = "/api/alvo")
    .AddCelFunction("normalizeVin", NormalizeVin, NormalizeVinSummary));
```

and the function itself, an ordinary static method:

```csharp
public const string NormalizeVinSummary =
    "Upper-cases a vehicle identification number and drops every character that is not a letter or a digit.";

private static string NormalizeVin(string vin) =>
    new string([.. vin.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]);
```

Alvo reads the signature off the delegate: the parameter's **name** (`vin`), its type (`string` is CEL `String`) and
whether it or the result may be null (from the nullable annotations). The summary is what the catalog and the dashboard
show. `AddCelFunction` validates all of it at the call, so a bad name or an unsupported parameter type is an
`ArgumentException` while the host is being built, never a surprise inside a write. A function may take up to four parameters
of `string`, `long`, `int`, `decimal`, `bool`, `DateTimeOffset` or `Guid` (and their nullable forms), and return one
of the same.

### 2. Call it from a hook

A descriptor then calls it from a before-hook. This one rewrites `vin` on every create:

```json
"vehicles": {
  "fields": { "vin": { "type": "string", "required": true, "unique": true, "maxLength": 17 } },
  "hooks": {
    "beforeCreate": [
      { "action": { "mutate": { "vin": { "$cel": "normalizeVin(new.vin)" } } } }
    ]
  }
}
```

The call works in a hook `condition` too — `"condition": "normalizeVin(new.vin) != new.vin"`. It is refused, when
the descriptor is applied, in `rules`, `computed` fields and the `access` block; the refusal carries the recipe:
store the value in a field with a `mutate`, then compare that field.

### 3. Try it

**`vehicles.alvo.json` does not carry that hook, on purpose** (see [why](#why-the-shared-descriptor-stays-hook-free)
below), so add it to a copy and point the sample at the copy:

```bash
jq '.entities.vehicles.hooks = {"beforeCreate": [
      {"action": {"mutate": {"vin": {"$cel": "normalizeVin(new.vin)"}}}}]}' \
  examples/vehicle-registry/vehicles.alvo.json > /tmp/vehicles.normalized.alvo.json

dotnet run --project samples/MMLib.Alvo.Samples.EmbeddedHost -- \
  --FleetDesk:DescriptorPath /tmp/vehicles.normalized.alvo.json \
  --FleetDesk:DatabasePath /tmp/fleet-desk-normalized.db
```

Create an owner as in [Try both](#try-both), then a vehicle with a lower-case VIN:

```bash
curl -si -XPOST localhost:5199/api/alvo/vehicles \
  -H "Content-Type: application/json" -H "X-Alvo-Api-Key: $KEY" \
  -d '{"vin":"1hgcm82633a004352","plate":"BA-777AB","make":"Skoda","model":"Fabia",
       "year":2020,"owner_id":"<the owner id>"}'
```

```http
HTTP/1.1 201 Created
Location: /api/alvo/vehicles/7eb2fe34-6cd6-4898-8bf1-fb48c4f1a992

{"id":"7eb2fe34-…","vin":"1HGCM82633A004352","plate":"BA-777AB","make":"Skoda","model":"Fabia","year":2020,…}
```

The stored row holds what the hook computed, not what the caller sent. Two things worth knowing about that write:

- **The caller's payload is validated first, the hook's value after.** A VIN sent with spaces that makes it 19
  characters long is refused with **422** (`max-length` on `/vin`) before the hook runs, so a normaliser cannot rescue
  a value longer than the field. The value a hook writes is then measured against the same facets; if it breaks one,
  the write is refused as the hook's refusal (**403**), naming the hook and the field, never the value.
- **A function that throws fails the write closed.** The transaction rolls back and the Data API answers **500**
  `function-failed`, naming the function and carrying none of the exception's text. An in-process `IAlvoData` caller
  gets an exception instead.

### 4. Discover it

Every function a descriptor may call in this host is listed, built-ins and yours alike, with the provenance that
tells them apart. A host that maps the Management API (`MapAlvoManagementApi()`, which this sample does not) answers
`GET /management/projects/vehicle-registry/cel/functions`; in process the same list is
`IAlvoManagement.GetCelFunctionsAsync("vehicle-registry")`. The entry for this function:

```json
{
  "name": "normalizeVin",
  "parameters": [ { "name": "vin", "type": "String", "acceptsNull": false } ],
  "result": "String",
  "resultMayBeNull": false,
  "summary": "Upper-cases a vehicle identification number and drops every character that is not a letter or a digit.",
  "provenance": "Host",
  "profiles": [ "Condition", "Mutate" ]
}
```

**In the dashboard.** A host that mounts the admin dashboard (`AddAlvoAdmin` + `MapAlvoAdmin`, also not done here)
offers the function where a hook is written: on the entity's **On write** tab, under a mutate value in expression mode
or a condition in text mode, the disclosure **Functions you can call here** lists every callable function with its
signature and summary. `normalizeVin(vin: String) -> String` carries a **this host** badge, where a built-in carries
**built-in** — a host function exists only in the host that registered it, and a descriptor that calls it will not
import into another build. **Insert** writes the call into the box; in an empty mutate row for `vin` it writes the whole
`normalizeVin(new.vin)`, the row's own field as the argument.

The coding agent working on your descriptor learns the same list through the assistant's `get_cel_functions` tool.

### 5. The rules a host function must keep

Alvo cannot check these. They are your promise, and breaking one breaks writes, not just your function.

- **Pure.** The same arguments give the same result, with no side effects. The function runs inside the write's
  transaction, and a rolled-back write must leave nothing behind. A side effect — an email, a call to another
  service — belongs in an after-hook or a webhook, never here.
- **Fast.** It runs while the row's locks are held, with no time budget and no `CancellationToken`. A slow function
  is a slow write for everyone touching that row. No network calls, no unbounded loops.
- **Thread-safe.** One instance serves every request thread at once. A static method over its arguments, like
  `NormalizeVin`, is safe by construction. The delegate is a singleton closure, so it cannot use a scoped service.
- **Tenant-aware.** Alvo's tenant filter governs what *Alvo* reads; it does not reach inside your code. A function that
  reads stored data (a rate table, a lookup) must take the tenant as a parameter and filter by it. On a tenant-scoped
  entity pass the row's own tenant: `vatRate(new.tenant_id, new.category)`, which works in a condition and in a
  mutate alike. `normalizeVin` reads nothing but its argument, so it needs no tenant.
- **No caller data in exceptions.** What a function throws, message and stack trace, is logged at Error. Never put a
  field's value or an argument into an exception message: it lands in every log sink the host ships to. Better still,
  **prefer `null` to a throw** on input you cannot handle: every throw is a 500 and a log entry, once per request.
- **Versioned by name.** A descriptor stores names, not versions. If a function's meaning changes, register it under a
  new name (`vatRate2`) and keep the old one. Removing a function that a stored descriptor still calls makes the next
  boot refuse that descriptor.

The full contract — reserved names, overload resolution, how null and failure behave, and why a host function is
trusted code — is in [`docs/architecture/cel.md`, "Host functions"][cel-host]; the builder-side view is in
[`docs/architecture/extensibility.md`, "Registering a CEL function"](../../docs/architecture/extensibility.md#registering-a-cel-function).

### Why the shared descriptor stays hook-free

`vehicles.alvo.json` is the descriptor the standalone image serves too, and the image has no C# extension point: it
knows only the built-in functions. A hook calling `normalizeVin` would make the image refuse the file as calling an
unknown function, and the two modes would stop serving the same backend — the thing
`Both_modes_generate_the_same_routes` exists to pin. So the sample registers the function and the shared file never
calls it. `test/MMLib.Alvo.Samples.EmbeddedHost.Tests.Integration` adds the hook to a copy at run time, as step 3
does, and proves both halves: the catalog lists the function as the host's, and a Data API write stores its result.

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
[cel-host]: ../../docs/architecture/cel.md#host-functions
