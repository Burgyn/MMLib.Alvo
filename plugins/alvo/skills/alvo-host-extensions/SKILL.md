---
name: alvo-host-extensions
description: Use when writing C# in an embedded Alvo host, only after the descriptor cannot express it — write hooks, event handlers, commands, functions, jobs, transactions, reads, typed entities, TestHost.
---

# C# extensions in an embedded Alvo host

## Rule 1: C# is the last resort

Solve it in the descriptor first. Host C# is code the operator cannot see, the policy simulator cannot show and the
dashboard assistant cannot change. Before writing any C#, stop at the first yes:

1. **Does a descriptor construct do it?** `rules` (relation rules and `checks` too), `computed`, rollups,
   before-hooks, field `validation`, `sequence`, partial and exclusion indexes, `stateMachine`, `timers`, after-hooks
   and `automation` with `webhook`, `email`, `function` or `entity.update` actions. Load the `alvo-descriptor-*`
   skills and read https://alvo.burgyn.online/extending/where-logic-goes/.
2. **Is only a CEL function missing?** A pure, synchronous calculation (VAT, working hours) is `AddCelFunction`; the
   descriptor keeps the rule.
3. **Only then** host code, at the stage whose guarantees you need:

| Need | Use | Stage |
|---|---|---|
| a check that calls a service (VIES, fraud) | `IValidateWriteHook<T>` | before the transaction; network; not atomic |
| atomic with the write, reads other rows | `IBeforeWriteHook<T>` | in the transaction; no network |
| atomic, needs the saved id | `IAfterWriteHook<T>` | in the transaction; no network |
| guard or follow a delete | `IBeforeDeleteHook<T>`, `IAfterDeleteHook<T>` | in the transaction |
| a side effect after the commit | `IAlvoEventHandler<TEvent>` | post-commit, at least once, retried |
| code the descriptor calls | `IAlvoFunction<TInput>` | post-commit, `{"type":"function"}` |
| not CRUD, or an inbound webhook | `MapAlvoCommand`, `MapAlvoWebhookReceiver` | your route |
| several writes as one unit | `IAlvoTransactions` | one transaction |
| at a time, regularly, once per revision | `IAlvoScheduler`, `IAlvoScheduledJob`, `IAlvoDataMigration` | outbox jobs |
| rows per caller, or a derived field on read | `IQueryNarrowing<T>`, `IRecordEnricher<T>` | the read |

## Registration

Group your extensions in an extension method on `IAlvoBuilder` and call it inside `AddAlvo`, beside
`FromDescriptor`; map the routes on the app (`MapAlvoDataApi`, your `MapAlvoCommand`s).

<!-- from: website/src/snippets/extending/write-hooks/OpeningDeskHooks.cs -->
```csharp
public static IAlvoBuilder AddOpeningDesk(this IAlvoBuilder alvo) => alvo
    .Entity<Booking>(bookings => bookings
        .Validate<VatIdIsRegistered>(
            "vat-in-vies",
            when: w => w.New.VatId != null,
            configure: o => o.Timeout = TimeSpan.FromSeconds(3))
        .BeforeWrite<WithinOpeningHours>("within-opening-hours")
```

`Entity<T>` takes a class marked `[AlvoEntity("bookings")]`; `Entity("bookings", …)` works over `AlvoRecord`. A typed
`when` lambda is translated to CEL once, at registration; a CEL string works too. Every id is stable: it names
deliveries and the catalog.

## Write hooks — https://alvo.burgyn.online/extending/write-hooks/

- Refuse on the context: `write.Invalid(code, message, field)` is 422, `write.Conflict(…)` 409, `write.Forbidden(code,
  message)` 403. A thrown `AlvoWriteRejectedException` is a 500 `hook-failed`; through `IAlvoData` a refusal surfaces
  as that exception.
- `write.Set(x => x.Field, value)` changes the row; it is judged again by the caller's rules and tenant.
- `write.Transaction` writes other rows as the caller, one hop deeper; `.AsSystem()` stays in the caller's tenant.
  Limits: 50 calls, chain depth 8 (508), no write to its own row, 250 ms (validate 5 s).
- **ALVO1001** warns on a network call, a connection, a file or a second data port inside a before/after hook or an
  `AddCelFunction` delegate — https://alvo.burgyn.online/extending/analyzer-rules/.

## Event handlers — https://alvo.burgyn.online/extending/event-handlers/

`.OnCreated`, `.OnUpdated`, `.OnSaved`, `.OnDeleted` on an entity; `AddEventHandler<TMessage, THandler>(id)` for an
event your code publishes (`[AlvoEvent("type")]`). **At least once**: dedupe on `context.EventId`. Retried 5 s up to
6 h, dead after the seventh attempt; in order per record. Steps that must not repeat use
`context.Steps.RunAsync(name, …)` and `SleepUntilAsync` — https://alvo.burgyn.online/extending/steps/. A descriptor
`function` action calls `AddFunction<T>(name)` — https://alvo.burgyn.online/extending/functions/.

<!-- from: website/src/snippets/extending/write-hooks/OpeningDeskHooks.cs -->
```csharp
.OnUpdated<SendBookingConfirmation>(
    "send-confirmation",
    when: e => e.Changed(x => x.Status) && e.New.Status == "confirmed"));
```

## Commands, transactions, time, reads

- Commands — https://alvo.burgyn.online/extending/commands/: `app.MapAlvoCommand(name, pattern, access, handler)`,
  `AddCommand<T>(name, access)` for in-process calls, `MapAlvoWebhookReceiver(pattern, secretName, handler)` verifies
  Standard Webhooks. Make the external call first, then the writes in `ctx.TransactAsync`.
- Transactions — https://alvo.burgyn.online/extending/transactions/: `IAlvoTransactions.TransactAsync(caller, (unit,
  ct) => …)`; return commits, throw rolls back. `IAlvoData` is refused inside a unit; each call has its own savepoint.
- Time — https://alvo.burgyn.online/extending/time/: `IAlvoScheduler.ScheduleAsync(key, at, message)`,
  `AddSchedule<TJob>(id, cron, timeZone)`, `AddDataMigration<T>(id, afterRevision)`. They need a database driver.
- Reads — https://alvo.burgyn.online/extending/reads/: `.Narrow<T>(id)` is ANDed after the rules and never widens;
  `.Enrich<T>(id)` fills a declared virtual field.

## Typed entities — https://alvo.burgyn.online/extending/generated-types/

<!-- from: website/src/content/docs/extending/generated-types.mdx -->
```xml
<AdditionalFiles Include="booking.alvo.json" AlvoDescriptor="true" />
```

The build generates a class per entity, an enum per enum field, a typed patch and role constants (ALVO2001, ALVO2002).

## Evolving — https://alvo.burgyn.online/extending/extension-catalog/

Renaming or removing an id with unfinished deliveries refuses the start until you add
`AddRetiredExtension(id, AlvoRetirement.DeadLetter)` (or `Discard`). Apply refuses dropping a field a handler reads. A
secret your code needs is `AddRequiredSecret(name)`.

## Testing — https://alvo.burgyn.online/extending/testing/

<!-- from: website/src/snippets/extending/testing/HelpDeskTests.cs -->
```csharp
await using var alvo = await AlvoTestHost.StartAsync(
    b => b.FromDescriptor(Descriptor), webhooks: opsBoard, cancellationToken: ct);
await alvo.DrainEventsAsync(ct);
alvo.Hooks.AssertRan("/entities/tickets/hooks/afterCreate", times: 1);
```

`AlvoTestHost.Caller(roles)` is a caller; pass `time:` a `FakeTimeProvider` for timers and retries. The in-memory
`InMemoryAlvoData` runs no hooks: test behaviour through `AlvoTestHost`.

To check your work: `dotnet build` (ALVO1001 is a warning) and your TestHost tests. In the Alvo repository itself:
`scripts/test-ring0`.
