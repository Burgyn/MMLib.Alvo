# The standalone host

> The surviving detailed record for `MMLib.Alvo.Host`, in the same role
> `data-path.md` plays for the port and `data-api.md` for the HTTP layer. PR4's
> Superpowers plan is discarded once merged; what outlives it is here, and the
> deviations it introduced are in the F3 design doc's *Deviations added by PR4*.

## What the host is, and is not

It is a `WebApplication` over the core's public seams and nothing more: configuration
binding, one driver registration, the code-first apply, `MapAlvoDataApi`, liveness, and
a docs UI. It is **not** the full standalone story — the dashboard, the Management API,
the CLI and the published image are #24's remainder, in F4.

## The order in `BuildAsync` is load-bearing

`MapAlvoDataApi` reads entity-name **literals** off the applied schema, so the apply must
precede the mapping or the host maps nothing at all. The apply also primes the policy
catalog, and an unprimed catalog denies every operation. Liveness is mapped before the
apply so the route exists on the endpoint table either way, but the server does not listen
until `RunAsync`, which is *after* `BuildAsync` returned — so **answering liveness proves
the descriptor applied**. A host whose apply throws never listens, and the container exits
non-zero. That is deliberate: a container reporting healthy with no schema is worse than
one that fails to start.

## Configuration

The framework's options (`AlvoOptions`, `AlvoApiOptions`, `AlvoAuthOptions`) are bound from
`Alvo:*`, `Alvo:Api:*` and `Alvo:Auth:*`; the host's own decisions live in
`AlvoHostOptions` (`Alvo:DescriptorPath`, `Alvo:Database:*`, `Alvo:PathBase`, `Alvo:Docs:*`).
The container form is the standard .NET double-underscore spelling
(`Alvo__Database__Provider`), not the `ALVO_*` names spec §X.1 sketches — see the design's
*Deviations added by PR4*.

`AlvoDevApiKey`'s collection members (`DevKeys`, `Roles`, `Scopes`) are getter-only, and
`ConfigurationBinder` populates them anyway — it binds into an existing non-null
`ICollection<T>` rather than needing a setter. Measured, not assumed: the boot facts
configure the whole credential through `Alvo:Auth:DevKeys:0:*` and a create that needs
`'admin' in @user.roles` succeeds, which is only possible if the getter-only `Roles` list
was filled from configuration. So no shape had to change to make the container's
environment a usable credential source.

The database is chosen by name, and an unknown name is refused rather than defaulted
(`AlvoDatabaseSelector`). A missing connection string is defaulted **only** for SQLite: a
PostgreSQL host with none must fail, because the alternative is quietly writing rows to a
container-local file that vanishes with the container.

**No default credential.** §2.14's acceptance criterion is that the image never ships a
preset login, so the host seeds no API key. A host with none configured still starts and
still refuses every operation, because an anonymous caller is judged by the same
default-deny policy as any other (deviation 23). Two facts hold that line: an anonymous
*write* is refused (a *read* would be an honest 200 with zero rows and would prove nothing),
and the host's own `appsettings.json` is asserted to declare no `Alvo:Auth` section — the
realistic way a preset login reaches an operator is a dev key added there for convenience,
which no runtime fact can tell apart from one the deployment configured.

## Health

Liveness only (`/health/live`). §2.12 asks for readiness with database, cache and message-bus
reachability; none of those probes exists as a port today, and inventing one is a port
widening PR4 has no mandate for. Recorded as a deviation with an issue rather than
approximated.

## A 500 is Alvo's own refusal here (#119)

The host calls `AddAlvoProblemDetails()` and `UseExceptionHandler()`, so an unhandled failure is logged with
its stack trace and answered with `type: https://alvo.dev/errors/internal` and a **constant** detail. Nothing
about the exception reaches the caller. Embedded hosts register neither and keep answering their own way,
which is why the registration is opt-in rather than part of `AddAlvo`.

`UseExceptionHandler` is the **first** middleware in `BuildAsync`, before liveness and before the Data API: a
middleware only sees what runs after it, and a failure that got past that line would be rendered by the
framework with an RFC 9110 status-code URI in `type`.

The handler itself lives in the core, not here — `ProblemResultFactory` is `internal`, so a Host-side one
would be a second hand-written copy of the problem-document shape. Recorded as deviation D1; `data-api.md`
carries the mode-by-mode table.
