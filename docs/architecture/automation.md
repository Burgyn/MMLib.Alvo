# Functions and automation

How a descriptor calls host C# and how the `automation` block runs (HX-15; spec
`docs/superpowers/specs/2026-10-14-host-extensibility-design.md` §4.11 and its *HX-15 settled details*). One rule:
**nothing new runs beside the delivery engine.** Every declarative action — an after-hook's or an automation rule's —
is a delivery in `alvo_deliveries`, claimed, retried, dead-lettered and operated exactly like a host event handler's.

> **Sibling records:** [`events.md`](./events.md) owns the outbox and the compile-time half of after-hooks;
> [`extensibility.md`](./extensibility.md) owns the extension catalog and binding; [`steps.md`](./steps.md) owns the
> delivery engine's steps, sleep and limits; [`time.md`](./time.md) owns the schedule pump a cron rule rides.

## Synthetic bindings

`ExtensionBindingCompiler` builds, per policy catalog, one **synthetic `BoundHandler`** per compiled after-hook action
and per automation action (`DescriptorActionBindings`), next to the host's registered handlers. Each has default
settings — timeout 30 s (the internal `AlvoDeliveryOptions.ActionTimeout` knob), `PerRecord` ordering,
`AlvoRetryPolicy.Default` — and runs as `AlvoContext.System(event tenant)`. They are not catalog entries: the
extension catalog lists registrations, and a function appears there as `AlvoExtensionKind.Function` with its
`InputSchema`, not its call sites.

The `OutboxDispatcher` no longer runs actions inline. It only **fans out**: one delivery request per bound subscriber
whose source and condition select the event. The `DeliveryRunner` then claims and runs each:

| Action | Runner | Kind |
|---|---|---|
| `webhook`, `email` | the executors that ran inline before, now per delivery | `action` |
| `function` | `FunctionInvocation`: binds the input, resolves the keyed function, calls it | `function` |
| `entity.update` | `EntityUpdateEffect`: ledger claim, write and commit in one unit | `action` |

## Subscriber ids

A descriptor action's subscriber id is its JSON pointer, `#`, and eight lower-case hex characters of a SHA-256 over
the canonical JSON of trigger, condition and action (`DescriptorActionIds`):

```
/entities/orders/hooks/afterCreate#1f0c9a2e      an after-hook action
/automation/notify-sales/actions#77ab03d4        an automation action
/automation/nightly-close#c01d5e9a               a cron rule's trigger-only schedule
```

The parts are serialized separately through the descriptor's typed `JsonTypeInfo` with a compact writer and joined by
U+001F, so the id is stable across processes and platforms. There is no sibling index: inserting a hook above another
does not rename it. Exact duplicates take an ordinal `-2`. An **edit makes a new subscriber**: the old one's pending
rows are never claimed again, and `ExtensionStateGuard` ignores ids starting with `/`, so they never refuse a boot.
They stay visible on Operations until a sweep cancels them (a follow-up).

The Operations page filters by an id prefix (`?subscriber=/automation/<rule>/actions#`), which is how the
Automations page links a rule to its deliveries.

## `entity.update`: the Idempotent Consumer

`entity.update` is exactly-once per (subscriber, event) through the **command ledger** — the processed-messages table
of the Idempotent Consumer pattern:

1. Claim `CommandKey("alvo.action", "<subscriber id>/<event id N>", fingerprint = subscriber id)`. `Answered` or
   `Unanswered` → done, write nothing; another live claim → retry later.
2. Plan the write before any unit opens: resolve the target, render `recordId`, evaluate the `$cel` values over the
   triggering row (`ActionValues`, inside a host-function budget pass), measure every value against the target field's
   facets (`PayloadFacets`, the before-hook `MutationTarget`) — a violation is dead `write-refused`.
3. `TransactAsync(System(event tenant))`: update or create, then `IAlvoCommandUnit.CommitCommandAsync(key)` in the same
   unit. A crash after the commit leaves the key committed; the retry finds it and writes nothing.
4. On any failure the claim is released and the failure classified: not found → `record-not-found`; a constraint,
   write or authorization refusal → `write-refused`; a CEL evaluation failure → `action-input-invalid`; the chain-depth
   refusal is rethrown and judged `chain-depth-exceeded`; anything else retries.

The system caller holds `admin` and is held to the target's rules — there is no bypass. A scoped target from an event
with no tenant is dead `record-not-found`; apply refuses the tenancy mismatches it can see. The ledger sweeps after a
day, which bounds the exactly-once horizon (Decision 3).

## Functions

`AddFunction<TFunction>(name)` registers the function keyed by name and exports `TInput`'s JSON Schema once
(`FunctionInputSchema`, `JsonSchemaExporter`, one frozen `JsonSerializerOptions` shared with the binding: web
defaults, `RespectNullableAnnotations`, `RespectRequiredConstructorParameters`, strict numbers). Apply checks top-level
`properties`, `required` and the JSON `type` of literals (`FunctionInputCheck`); a `$cel` value compiles in the
`Mutate` profile over the triggering row and its result type must fit. At run time the values are evaluated, bound to
`TInput` through the same options, and a bind failure is dead `function-input-invalid` with neither the input nor the
row in the log. `AlvoFunctionSkippedException` ends the delivery succeeded with a `FunctionSkipped` line.

## Cron rules

A cron rule becomes a **trigger-only schedule** on HX-13's schedule pump (`SchedulePump` syncs them per catalog): its
id is `/automation/<rule>#<hash>`, its fire appends `alvo.automation.<rule>` with subject `schedules/<id>`, and that
event fans out to the rule's actions like any other. On a tenanted project the schedule's scope is `EachTenant`, so it
fires once per tenant (an `IAlvoTenantCatalog` is required, else apply refuses). Its actions compile over an empty row
at `afterCreate`, and a cron rule takes no condition.

## Secrets

`secretRef` and `headersSecretRef` are checked at apply: a malformed name is an error that never echoes the value, a
well-formed name the store does not list is a warning (names only, `ListNamesAsync`). `AddRequiredSecret(name)` is
checked at boot by reading each value only to test it for emptiness (`RequiredSecretsCheck`): code-first refuses; a
dashboard-first host with a writable store stands down — and `AlvoBootState.SecretStandDown` makes the apply path
refuse until a restart. A store that throws fails closed, naming its exception type.

## Decisions

1. Subscriber id = pointer + content hash; an edit makes a new subscriber, and the old one's pending rows are left unclaimed.
2. Actions run as synthetic `BoundHandler`s with default settings (30 s, `PerRecord`, default retry, system in the event's tenant); not catalog entries.
3. `entity.update` is exactly-once through the command ledger committed inside `TransactAsync`; the horizon is the ledger's one-day sweep.
4. Function `input` is validated by top-level `properties`, `required` and the JSON `type` of literals only; a nested typo dies at run time.
5. `$cel` values compile in the `Mutate` profile over the triggering row's images; no `@user`/`@tenant` there.
6. The JSONata string form of `input` stays refused; the object form is honoured.
7. `AlvoFunctionSkippedException` ends the delivery succeeded with a `FunctionSkipped` log line (span outcome `succeeded`, as built).
8. `secretRef`/`headersSecretRef`: grammar refused, existence warned.
9. The required-secret boot check reads each value; empty = missing; a throwing store refuses the boot.
10. Triggers honoured: entity created/updated/deleted and a five-field UTC cron; wildcard, `.batch`/`delivery: batch`, `auth.*`/`storage.*` and a condition on a cron rule refused.
11. A cron rule runs once globally without tenancy, once per tenant with it (an `IAlvoTenantCatalog` required).
12. `entity.update` into a tenant-scoped entity from an untenanted trigger is refused at apply; a `recordId` not found in the event's tenant is dead `record-not-found`.
13. `FunctionAction.Input` becomes `FunctionInput?` (breaking); `ManagementExtension.InputSchema` is added, `Secrets` is not.
14. The Automations page is read-only; `/admin/functions` stays *Not yet* and points at the hooks editor.

The rulings Q1–Q22 and the as-built deviations are in the spec's *HX-15 settled details*.
