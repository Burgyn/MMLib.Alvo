# Vertical slice — organization inside a package

> How a feature is laid out *inside* a package (almost always the core
> `MMLib.Alvo`). The counterpart to [`package-boundary.md`](./package-boundary.md):
> that rule decides *what becomes a separate NuGet package*; this one decides
> *how the code inside one is organized*. Spec §0 principle 9.

**Goal: agent-first delivery.** A change arrives as *one feature* — "add record
CRUD", "dispatch a webhook", "apply a schema change". This rule exists so that
feature is **one folder**: an agent (the primary user, §0 principle 4) opens it, sees
the endpoint, the logic, its validation and its model together, and ships it —
instead of threading the change through `Controllers/`, `Services/`,
`Validators/` and a shared handler base. Everything below serves that: less
ceremony, a smaller change surface, a bounded blast radius.

**Scope.** This governs organization *within* a package — where a feature's
endpoint, logic, validation, model and events live, and how features stay
isolated from each other. It does **not** decide package splits (that is
`package-boundary.md`) and it never justifies one: a vertical slice is a
namespace, not a project.

**VSA (the REPR pattern) governs request-triggered operations — not every
file.** A slice is the shape for code *triggered* through a single entry (an
HTTP route, a message, a schedule): the Management API operations, automation
and webhook dispatch, the custom endpoints an embedded host adds. Framework
**mechanisms** — engines, ports, registries, mappers, the data pipeline — are
not triggered operations; they are organized by **capability/subsystem** (a
feature namespace with a public contract + `Internal/`, the .NET-framework
style, e.g. EF Core's `Migrations`/`Metadata`/`Storage`), *not* sliced
per-operation. Decision rule: triggered through one entry → slice; a mechanism
other code calls → capability namespace. How such a mechanism *attaches* to the
framework (its builder/DI/options/provider wiring) is
[`extensibility.md`](./extensibility.md).

**Why this, and why now.** Alvo is greenfield — there is no legacy layer to
migrate and no dominant in-house pattern to unlearn. The risk is the opposite:
the default .NET tutorial muscle-memory is *layer-by-type*
(`Controllers/`+`Services/`+`Validators/`), and an agent copying the ambient
style will reproduce it. Layering organizes by *technical concern*, but change
arrives by *feature* — so the folder structure works against the way the code
actually changes, and touching one use-case means editing across four folders.
A vertical slice inverts it: one use-case is one folder. The first real slice
lands at **F3 (project → table → CRUD API)**; this rule fixes the shape *before*
that code exists, so the first slice sets the precedent instead of inheriting an
accident.

This is not a home-grown bet: vertical slice architecture (and the
[REPR](https://deviq.com/design-patterns/repr-design-pattern) endpoint pattern
it builds on) is an established approach — Jimmy Bogard, Ardalis, FastEndpoints —
for exactly this problem.

## The shape (target state)

**One shape: a vertical slice per operation.** Every custom, hand-written
operation is a self-contained slice; everything it needs lives together, nothing
leaks into a shared technical layer.

```
MMLib.Alvo/<Feature>/<Operation>/
  <Operation>Endpoint.cs      # static minimal-API adapter — the thing that gets mapped (Response nested)
  <Operation>Handler.cs       # the logic: instance class, ONE public HandleAsync
                              #   + Command/Query nested in the handler; <Operation>Result beside it
  <Operation>Validator.cs     # boundary validation — when the input needs rules the schema can't express
MMLib.Alvo/<Feature>/
  Setup.cs                    # Add<Feature>() (DI) + Map<Feature>() (RouteGroupBuilder + each Map)
  Contracts/                  # OPTIONAL public front door for OTHER features (interfaces + DTOs)
  Internal/                   # OPTIONAL feature-internal types (never public — see encapsulation)
```

**File count is guidance; class roles are the contract.** A rich slice uses all
of the files above; a trivial one collapses them. A read that just projects the
schema registry is a single `<Operation>Endpoint.cs`; a small write with no
extra validation keeps its `Command` + `Handler` in the endpoint's file. The
class-role rules (static endpoint without business logic, instance handler with
one `HandleAsync`) hold regardless of file count.

### Generated endpoints are the exception, not the rule to break

Alvo's **Data API is generated from the schema** (§0 principle 8 — an
endpoint-as-delegate generates from schema better than a reflected controller).
There is **no folder-per-entity slice** for CRUD: the "slice" for generated CRUD
is the **generator plus the pipeline it drives** (schema registry → route
mapping → data port → rule engine → event backbone), and *that* generator lives
as one feature organized by this rule. Hand-written slices are for everything
the schema does not generate: **Management API** operations (applying a schema
change, upserting a rule, reading logs, …), automation/webhook dispatch, and the
custom endpoints an embedded host adds in Mode 2. When in doubt: if the endpoint
is produced *from a descriptor*, it is generated; if a developer or agent
*writes* it, it is a slice.

### Feature and slice — the two levels

- **Feature** — a cohesive capability area inside the package (schema registry,
  data API, rule engine, events, automation, webhooks, auth, the Management API,
  …). It owns a route group (where it has endpoints), a `Setup.cs`, and its
  `Internal/` helpers. A feature is a *namespace* under `MMLib.Alvo`, never a
  separate project (that is `package-boundary.md`'s call).
- **Slice** — one externally-triggered use-case inside a feature, plus everything
  unique to it.

**Granularity heuristic — "is this its own slice?":** *can a client, agent, or
event trigger it independently through a single entry (one route / message /
schedule)?* If yes, it is its own slice. Reads and writes are separate slices;
two operations differing in intent (`UpsertRule` vs `SimulatePolicy`) are
separate slices even over the same entity; one use-case with several entry points
(HTTP + a queued worker) is still **one** slice.

### The slice, concretely

The endpoint is a **static minimal-API adapter** (§0 principle 8 — minimal API +
`RouteGroupBuilder`, never MVC); the logic is an **instance handler** with exactly
one public method. The endpoint's route is relative (the feature group supplies
the prefix), the `Command` is bound directly from the request body, the handler
arrives through DI parameter binding, and errors map to RFC 7807 problem details
with a fix suggestion (§0 principle 4). Both classes are `internal` — nothing in
a slice is published API unless it is genuinely a contract (see the encapsulation
rule below).

```csharp
// DispatchWebhookEndpoint.cs
internal static class DispatchWebhookEndpoint
{
    public sealed record Response(Guid DeliveryId);

    public static void MapDispatchWebhook(this IEndpointRouteBuilder app) =>
        app.MapPost("/", Handle);

    private static async Task<Results<Accepted<Response>, ProblemHttpResult>> Handle(
        DispatchWebhookHandler.Command command,
        DispatchWebhookHandler handler,
        CancellationToken ct)
    {
        DispatchWebhookResult result = await handler.HandleAsync(command, ct);
        return result.Match(
            ok  => TypedResults.Accepted($"/deliveries/{ok.DeliveryId}", new Response(ok.DeliveryId)),
            err => TypedResults.Problem(err.ToProblemDetails()));
    }
}

// DispatchWebhookHandler.cs
internal sealed class DispatchWebhookHandler(IAlvoData data, IEventPublisher events)
{
    public sealed record Command(string Endpoint, string EventPattern);

    public async Task<DispatchWebhookResult> HandleAsync(Command command, CancellationToken ct)
    {
        // …
        await events.PublishAsync(new WebhookDispatched(id), ct);
        return DispatchWebhookResult.Ok(id);
    }
}
```

The handler writes through the shared ports (`IAlvoData`, the event backbone) —
never around them — and publishes its event explicitly.

| | Class | Method | Static? | Mapped? | Called by |
|---|---|---|---|---|---|
| **Endpoint** | `<Operation>Endpoint` | `Handle` | **static** | yes (`Map…`) | ASP.NET |
| **Handler** | `<Operation>Handler` | `HandleAsync` | **instance** (DI ctor) | no | endpoint **and** any other entry point |

**Handler over service (no god classes).** The unit of logic is a
per-operation `…Handler` with exactly one public `HandleAsync` — REPR without an
in-process bus. A `…Service` is a god-class magnet and is **not** the default; it
is allowed only as a stateless helper used *within a single slice*, never as a
per-entity facade and never shared across slices.

**One contract per slice.** The slice has one input contract — the `Command` (or
`Query`): a plain record with no ASP.NET dependency, nested in the handler
(`DispatchWebhookHandler.Command`). The endpoint binds it directly and hands it to
the handler; any second entry point (e.g. a queued worker) maps its input to the
*same* `Command` — MCP is not one: it reaches a slice only through the Management
API endpoint it adapts. Introduce a separate `Request` DTO only when the HTTP
shape genuinely differs (an id from the route combined with a body, or an
externally-versioned wire shape kept stable for compatibility).

### Wiring: `Setup.cs`, groups, DI

Each **feature** owns one `Setup.cs` with `Add<Feature>` (DI) and `Map<Feature>`
(routing). `Map<Feature>` creates the feature's `MapGroup` — route prefix, tags,
OpenAPI metadata — and chains each slice's `Map<Operation>`, each defined in its
own endpoint file. (Authentication needs nothing here: the host-level fallback
policy already locks every endpoint — see *Cross-cutting*.)

```csharp
public static class Setup
{
    public static IServiceCollection AddWebhooks(this IServiceCollection services)
    {
        services.AddScoped<DispatchWebhookHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapWebhooks(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/webhooks").WithTags("Webhooks");
        group.MapDispatchWebhook();
        return app;
    }
}
```

Features are wired **explicitly** — no assembly scanning / auto-discovery. The
live feature set is greppable and shows up in the diff; there is no startup
reflection to reason about. The cost — one line per feature — is small and local.

### No in-process request bus for new code

Slices invoke the handler **directly**. **MediatR is banned** — commercial since
April 2025, which is disqualifying inside an Apache-2.0 core (§0 principle 9,
the `alvo-dotnet-conventions` licensing bans), and a direct call is
F12-navigable where `mediator.Send(...)` is
not. If a genuine multi-handler pipeline ever appears, **Wolverine** (MIT) is the
sanctioned option — but it is an exception with a documented reason, not the
shape.

### Events

In-process reactions use Alvo's own **event backbone** (brief §3 — publish/subscribe
over changes, transactional outbox), not a foreign mediator's notifications. The
hard guarantee holds: an event is published **in the same transaction** as the
data change ("no change without an event, no event without a change"). A slice
publishes explicitly; other features react from their own folder. See
*Cross-feature interaction*.

## Cross-cutting is the port's job, not the slice's

This is where Alvo diverges most from a conventional slice layout. **Row- and
field-level authorization is not a per-slice concern at all.** It lives in the
**data port and the rule engine** and is **unbypassable** — policy is enforced
*inside* `IAlvoData`, not around it, so a slice that reads or writes through the
port gets default-deny authorization for free and *cannot* forget it or route
around it (brief §4: "policy is unbypassable"; even custom C# endpoints read via
`IAlvoData` + `AlvoContext`). There is no permission-marker-per-command to
remember, because there is no code path to data that skips the check.

A slice's only cross-cutting duties are therefore two, both at the boundary:

- **Endpoint authentication — secure by default.** The fallback policy requires
  an authenticated user, so an endpoint with no explicit policy is **locked**. A
  genuinely public endpoint opts out **explicitly** with `AllowAnonymous`.
  Forgetting fails **safe**, never open (§0 principle 5, default-deny).
- **Boundary validation.** Generated endpoints get **schema-derived validation**
  automatically (400 + RFC 7807 listing the violations, brief §4). A custom slice
  whose input needs rules the schema cannot express carries **one**
  `<Operation>Validator` at the endpoint boundary — never hand-rolled inside the
  handler, never doubled with schema validation.

## Slice isolation and cross-feature interaction

A slice is self-contained. Types in one feature's slice **must not** reference
another feature's internals. The only cross-feature reference allowed is into the
other feature's **`Contracts/`** folder.

```
✅ allowed
MMLib.Alvo.Webhooks.DispatchWebhook.DispatchWebhookHandler
   → MMLib.Alvo.Webhooks.Internal.*        (shared WITHIN the feature)
   → MMLib.Alvo.Rules.Contracts.*          (another feature's PUBLIC contract)
   → the shared base: IAlvoData, the rule engine, the event backbone, a port

❌ forbidden
MMLib.Alvo.Webhooks.DispatchWebhook.DispatchWebhookHandler
   → MMLib.Alvo.Rules.UpsertRule.UpsertRuleHandler   (a type from ANOTHER feature's slice)
   → MMLib.Alvo.Rules.Internal.*                      (another feature's internals)
```

Features legitimately need to make something happen in another feature — that is
*interaction across a boundary*, not *sharing code*. Pick the loosest mechanism:

1. **Event (reaction).** When B's work is a *consequence* of A, A publishes a
   domain event on the backbone and B reacts from its own folder. A does not know
   B exists. The default for "when X happens, also do Y". The event type is a
   shared contract, not a type inside A's slice.
2. **Public feature contract (synchronous).** When A needs a *synchronous result*
   from B's own capability, B exposes a small interface in
   `MMLib.Alvo.<B>/Contracts/` — the only part of B another feature may reference.
   This is the provider model (§0 principle 2) applied in-process: the providing
   feature owns its contract; A injects the interface, never B's slice types.
3. **A port.** When the thing both features need is *infrastructure* (a store, a
   sender, a cache), it is a **port** with a provider — not "A calls B".

**Decision:** reaction → event; synchronous, B's own capability → public
contract; infrastructure → a port.

## Orchestrate the shared base — never verticalize it

A slice contains only what is **unique to its use-case**. Everything shared —
the schema registry, the data port, the rule engine, the event backbone,
multi-tenancy filters, the provider ports — is the **horizontal base**, and a
slice **orchestrates** it; it must never copy or fork it into the slice.
Verticalizing the base is the failure mode that turns vertical slices into
copy-paste duplication, and for Alvo it would break the deeper contract: *one*
schema registry (one model, two drivers), *one* rule engine that compiles CEL to
SQL identically everywhere, *one* outbox. The line: a slice **calls** the base,
it must not **fork** it. This is the one hard rule around slices.

**Resist sharing between slices.** Duplication across slices is acceptable at
first; extract into `Internal/`, the base, or a port **only when a real shared
*concept* emerges**, not from a DRY reflex — Bogard's rule, *"minimize coupling
between slices, maximize coupling within a slice."* The wrong shared abstraction
couples slices worse than a little duplication does. Extract late, on evidence.

## Standalone, embedded, and offloaded execution

The reusable unit is always the **handler**; where it physically lives follows
`package-boundary.md` and the function-runtime port (brief §3, isolation axis):

- **Both modes, in-process** (the common case) — the slice lives in the core;
  the standalone Host (`MMLib.Alvo.Host`) and an embedded host both map the same
  feature. Nothing special.
- **Offloaded to a queued/worker path** — the operation may be reached over HTTP
  *and* over the outbox/bus. Both are thin adapters to the **same handler**, in
  the **same slice**; the worker is another entry point, not a foreign feature.
- **A separate deployable with a foreign dependency** (e.g. a microVM executor) —
  that is a *package* decision (`package-boundary.md`): the shared handler stays
  in the core, the executor package depends on it, never the reverse.

## Naming vocabulary

| Unit | Pattern | Example |
|---|---|---|
| Feature namespace | `MMLib.Alvo.<Feature>` | `MMLib.Alvo.Webhooks` |
| Slice folder | `<Feature>/<Operation>/` | `Webhooks/DispatchWebhook/` |
| HTTP endpoint | `<Operation>Endpoint` (static) | `DispatchWebhookEndpoint` |
| Logic unit | `<Operation>Handler` (one `HandleAsync`) | `DispatchWebhookHandler` |
| Handler input | `Command` / `Query`, nested in the handler | `DispatchWebhookHandler.Command` |
| Handler result | `<Operation>Result`, beside the handler | `DispatchWebhookResult` |
| Validator | `<Operation>Validator` | `DispatchWebhookValidator` |
| Feature wiring | `Setup` (`Add<Feature>` / `Map<Feature>`) | `Setup.AddWebhooks` |
| Feature public contract | `<Feature>/Contracts/` (interfaces + DTOs) | `Rules/Contracts/IPolicySimulator` |
| Feature-internal helpers | `<Feature>/Internal/` (never public) | `Webhooks/Internal/DeliverySigner` |

## Rules

- Custom, hand-written request-handling code **MUST** be organized as vertical
  slices under `<Feature>/<Operation>/`, one slice per operation, **MUST NOT** be
  organized by technical layer (`Controllers/`, `Services/`, `Validators/`).
  Schema-generated Data API endpoints are exempt (they are generated, not written).
- A **feature** is a capability area; a **slice** is one externally-triggered
  use-case. A new use-case in an existing area **MUST** be a new slice; a distinct
  area **SHOULD** be a new feature namespace. A feature **MUST NOT** become a
  separate project on organization grounds alone — that is `package-boundary.md`.
- Each slice's logic **MUST** be a `<Operation>Handler` — an instance class with
  **exactly one** public `HandleAsync`. It **MUST NOT** be a multi-operation
  `…Service`; a `…Service` **MAY** exist only as a stateless in-slice helper. A
  trivial read **MAY** skip the handler and project the schema registry / query
  the data port directly in the endpoint.
- The HTTP endpoint **MUST** be a **static** minimal-API adapter (`Map<Operation>`
  + a static delegate), **MUST NOT** be an MVC controller, and **MUST NOT** hold
  business logic beyond a trivial read.
- A slice with a handler **MUST** have exactly one input contract — a
  `Command`/`Query` record nested in the handler. The endpoint **SHOULD** bind it
  directly; a separate `Request` DTO **MAY** be introduced only when the HTTP
  shape genuinely differs.
- The handler **MUST** take the `Command`/`Query`, **MUST NOT** take `HttpContext`,
  and **MUST NOT** depend on `Microsoft.AspNetCore.*`.
- New code **MUST NOT** use an in-process request bus. MediatR is **banned**
  (licensing + traceability); a bus **MAY** be introduced only with a documented
  reason (Wolverine, MIT) for a genuine multi-handler pipeline.
- Each feature **MUST** expose a `Setup.cs` (`Add<Feature>` + `Map<Feature>`) and
  the host **MUST** register it explicitly. Features **MUST NOT** be auto-discovered
  by assembly scanning.
- In-process reactions **MUST** use Alvo's event backbone (transactional outbox),
  **MUST NOT** use a foreign mediator's notifications.
- A slice **MUST** contain only what is unique to its use-case. The shared base
  (schema registry, data port, rule engine, event backbone, tenancy, provider
  ports) **MUST** be orchestrated from the horizontal base and **MUST NOT** be
  copied or verticalized into a slice.
- A slice **MUST NOT** reference another feature's internals; the only allowed
  cross-feature reference is the other feature's `Contracts/`. Cross-feature
  interaction **MUST** use a domain event, the target feature's public contract,
  or a port.
- Cross-slice duplication **MAY** be tolerated; shared code **MUST** be extracted
  into `Internal/`, the base, or a port **only when a genuine shared concept
  emerges**, never pre-emptively.
- Endpoint authentication **MUST** be secure-by-default (a fallback policy
  requiring an authenticated user); a public endpoint **MUST** opt out explicitly
  with `AllowAnonymous`. Row/field authorization **MUST** be left to the data port
  and rule engine — **MUST NOT** be re-implemented per slice.
- Validation **MUST** run at the endpoint boundary: schema-derived for generated
  endpoints, one `<Operation>Validator` for a custom slice whose input needs it —
  never hand-rolled inside a handler, never doubled with schema validation.
- Every `public` type in a slice is published API — mark `public` only what is
  genuinely a contract (a feature's `Setup` and, where it is one, a `Contracts/`
  interface); default to `internal` (the encapsulation rule in
  `alvo-architecture-rules`, gated by the public-API approval tests).

## References

- [`package-boundary.md`](./package-boundary.md) — the counterpart rule; it
  decides package splits, this decides internal organization. Neither justifies
  the other's answer.
- Spec §0 principles 2, 4, 5, 8, 9; `docs/design-brief.en.md` §1 (principles),
  §3 (ports & guarantees), §4 (hard invariants — policy unbypassable,
  default-deny, schema-derived validation), §6 (boundaries).
- Industry background: Jimmy Bogard,
  [*Vertical Slice Architecture*](https://www.jimmybogard.com/vertical-slice-architecture/)
  ("minimize coupling between slices, maximize coupling within a slice"); Milan
  Jovanović,
  [*Vertical Slice Architecture in .NET*](https://milanjovanovic.tech/blog/vertical-slice-architecture-dotnet).
