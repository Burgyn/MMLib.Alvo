# Extensibility — builder, DI, options, provider model

> How every `MMLib.Alvo.*` package plugs into the framework. The companion to
> [`package-boundary.md`](./package-boundary.md) (what becomes a package) and
> [`vertical-slice.md`](./vertical-slice.md) (how code is organized inside one):
> this doc is *how a package attaches itself to the running framework*. Spec §0
> principle 2 (provider model everywhere) and §1.2 (ports). Grounded in
> Microsoft's own patterns — the options-pattern library-author guidance, the
> `AuthenticationBuilder`/`IHttpClientBuilder` builder-object pattern, the
> Framework Design Guidelines, and the `Add{Group}` + `TryAdd*` registration
> convention.

Alvo will grow dozens of extending packages (data, secrets, storage, cache,
email/sms/push, identity, AI, functions, telemetry). The single entry point is
`AddAlvo()`, and its shape is a **public API** — expensive to change later
("you can't grow a fluent API"). The *extensibility seam* is therefore fixed
here, once, so every future package attaches the same way and an agent never
has to guess.

## The seam

```csharp
// MMLib.Alvo.Abstractions — namespace MMLib.Alvo
public interface IAlvoBuilder { IServiceCollection Services { get; } }

// MMLib.Alvo (core) — namespace Microsoft.Extensions.DependencyInjection
public static IAlvoBuilder AddAlvo(this IServiceCollection services, Action<IAlvoBuilder>? configure = null);

// a provider package — namespace Microsoft.Extensions.DependencyInjection
public static IAlvoBuilder UsePostgreSql(this IAlvoBuilder builder, string connectionString);
```

`IAlvoBuilder` is an interface with a **real member** (`Services`) — not a marker
interface (the Framework Design Guidelines say avoid those). Providers attach via
**extension methods** on it (the `AuthenticationBuilder`/`IHttpClientBuilder`
pattern), each registering its own services into `builder.Services`. **Core never
references a provider** — this is the Open/Closed seam: a new capability is a new
extension method in its own package, never an edit to `AddAlvo`/`IAlvoBuilder`.

## Strict rules

1. **One entry:** `AddAlvo(this IServiceCollection, Action<IAlvoBuilder>?)` returns
   `IAlvoBuilder`. Extension classes live in namespace
   `Microsoft.Extensions.DependencyInjection` (ambient `using`, like
   `AddDbContext`/`AddHttpClient`).
2. **`IAlvoBuilder`** exposes only `IServiceCollection Services`. The concrete
   builder is `internal sealed`. Everything else flows through options + DI, never
   by accreting members onto the interface.
3. **Providers self-register** via `Use*`/`Add*`/`Enable*` extensions using
   `TryAdd*` — core never enumerates providers.
4. **Fixed verb taxonomy** (so nothing is guessed):
   - `Use{Provider|Infra}` — infra selection (`UseSqlite`, `UsePostgreSql`,
     `UseSchemaPrefix`).
   - `Add{Thing}` — additive registration.
   - `Enable{Feature}` — a toggle (`EnableDynamicEntities`).
   - `From{Source}` — a descriptor source (`FromDescriptor`).
   - Fluent methods return `IAlvoBuilder`.
5. **Config via the options pattern, validated at startup.** Infrastructure config
   is typed options (`AlvoOptions`, and per-provider options), bound and validated
   with `ValidateDataAnnotations().ValidateOnStart()` / `IValidateOptions<T>` →
   fail-fast with a structured error + fix suggestion (agent-first, §0 principle 4).
6. **Descriptor ≠ options (hard).** The project descriptor is domain input via
   `IDescriptorSource` (a file, or a DB record) — never the options pattern.
   Options carry infrastructure only. Upholds the invariant "descriptor ≠ infra
   config".
7. **Idempotent registration** — `TryAdd*` everywhere; a provider selected twice is
   not a duplicate.
8. **Fail-fast on a missing/ambiguous provider** — a startup `IValidateOptions`
   asserts required ports have a provider and rejects invalid combinations, with a
   structured error ("register a database provider: call UseSqlite() or
   UsePostgreSql()"). No silent provider default in core (core must not drag a
   provider); a zero-config default is supplied by the standalone Host, not core.
9. **Explicit, documented lifetimes** for every port registration (thread-safe
   singletons per the DI guidelines).
10. **Endpoints are a separate seam** — `MapAlvo(this IEndpointRouteBuilder)` is
    orthogonal to `AddAlvo`. Adding endpoints never changes the DI seam.
11. **The builder surface is under public-API approval** (`IAlvoBuilder`,
    `AddAlvo`, `AlvoOptions`, the verb taxonomy); any change is a conscious SemVer
    act. Concrete builder + registrations are `internal`.

## Scaling to many providers

- **Follow `AuthenticationBuilder`/`IHttpClientBuilder`, NOT EF Core's
  `IDbContextOptionsExtension` machinery.** EF's immutable options-extension +
  `ExtensionInfo` (`GetServiceProviderHashCode`/`ShouldUseSameServiceProvider`)
  exists to cache EF's *internal* service provider per configuration. Alvo
  registers into the *host* container and has no internal cached provider, so that
  machinery would be cargo-culted complexity. Borrow only EF's *disciplines*:
  immutable options and a centralized fail-fast selection check.
- **Multiple implementations of one port → keyed services** (.NET 8+
  `AddKeyedSingleton<IPort, Impl>(key)` + `[FromKeyedServices(key)]`), with a
  fixed provider-key convention defined when the first multi-impl port lands.
  Single-impl ports use plain `TryAdd`.
- **Options are per-feature and immutable after startup** — no god-options.
- **Deliberate options interface:** `IOptions<T>` for start-fixed infra (default);
  `IOptionsMonitor<T>` only where live reload is a real requirement;
  `IOptionsSnapshot<T>` avoided on hot paths (scoped, slow).
- **Capability model** (§1.2): a provider may declare capabilities (transactional
  outbox, presigned upload, …); the framework degrades gracefully when one is
  absent. The named pattern for provider feature-detection.

## Pitfalls (banned)

- **Never call `BuildServiceProvider()` during registration** — it builds a second
  container, duplicates singletons and leaks. Config that needs other services uses
  deferred `IConfigureOptions<T>` / `OptionsBuilder.Configure<TDep>(...)`.
- **No unvalidated config** silently defaulting to null/empty — hence mandatory
  `ValidateOnStart`.
- **Registration-order traps:** `TryAdd` is first-wins for defaults; a deliberate
  override is explicit, never a matter of `using` order.
- **`reloadOnChange` does not auto-propagate** unless consumed via
  `IOptionsMonitor<T>`.
