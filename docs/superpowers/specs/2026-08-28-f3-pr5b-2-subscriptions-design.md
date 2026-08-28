# PR5b-2 — wildcard subscriptions, the `Publish` namespace guard, and deviation 76's remainder

The last three things `docs/architecture/events.md`'s *What PR5b and F7 inherit* leaves open that are not
someone else's issue number. Written 2026-08-28, on `f3/pr5b-2-subscriptions`, branched from `main` after
PR5b-1 (#160) and PR6 (#163).

> **What this closes.** Deviation 76's three remaining `complex-crm` defects and the refusal-reason
> strengthening (deferred to here by deviation 83); the wildcard-subscription ruling; and `Publish`'s
> security ruling, which the addendum names in neither PR's Definition of Done. It does **not** close #22 —
> automation (`event`/`schedule` triggers, cron, `entity.update`) is still PR5b's open half.

## 1. The wildcard: refused at apply, and the branch was forced rather than chosen

`alvo-specifikacia.md:141` makes `entity.orders.*` a **hard** guarantee (*"subscribe podporuje wildcard
vzory"*). `baas-analyza.md:657` makes tenant isolation of rules a **watch-out** (*"tenant vidí a triggeruje
len svoje"*). `events.md` turns the pair into a two-branch instruction:

> PR5b either implements the matcher **with every subscription scoped to the envelope's tenant and a named
> adversarial cross-tenant fact**, or refuses `*` at apply until it exists.

**The first branch is unavailable, and that is measured rather than argued.** `AlvoEvent`
(`src/MMLib.Alvo.Abstractions/Events/AlvoEvent.cs`) carries `Id`, `Source`, `Type`, `Time`, `Subject`,
`PartitionKey`, `AuthType`, `AuthId`, `CorrelationId`, `PayloadVersion`, `ChainDepth`, `CausationId` and
`Data` — and **no tenant attribute**. `AlvoContext` does carry `Tenant`, so the tenant is known at *emit*;
it is dropped at the envelope boundary and is therefore unknowable at *delivery*, which is the only place a
subscription is evaluated. Giving it one is a public-API and wire-format change with a compatibility
question for the outbox payloads this build already wrote — **#153** owns exactly that, and `events.md`
says so.

So a wildcard matcher shipped today could not be tenant-scoped by anything, and the adversarial
cross-tenant fact the ruling requires could not be written at all: there is no tenant on either side of the
comparison. Shipping the matcher would mean shipping the fan-out the watch-out names, with a fact that
asserts nothing. **The second branch is taken.**

### What "refused at apply" means concretely

A `*` in either slot the frozen schema types as `$defs/eventPattern` — `automation.*.trigger.event` and
`functions.*.trigger.event` — is refused when the descriptor is applied, with the consequence and the fix
`UnhonouredFeatures`' entries all carry. A pattern with no `*` still applies and still only earns
`UnhonouredSubsystems`' warning, because nothing about it changed.

**One authority for the grammar.** `EventPattern` parses a pattern into its namespace, entity, operation and
`.batch` suffix, and answers `HasWildcard`. Both this refusal and §2's guard read the reserved namespace set
off it, and `EventPatternTests` asserts that set against `schema/project.schema.json`'s own
`$defs/eventPattern` regex — the same tie `UnhonouredSubsystemsTests.Every_unhonoured_subsystem_names_a_block_the_schema_declares`
uses, and for the same reason: a hand-copied alternation drifts from the schema silently.

### Deviation: this refuses a descriptor whose only defect is being ahead of the build

`UnhonouredSubsystems` states the repo's warn-versus-refuse line: refuse what **silently produces wrong
data**, warn what is **observably absent**. An automation rule never fires in this build, so a wildcard in
one is observably absent, and the line as written says *warn*.

It is refused anyway, and the reason is that the two halves of "observable" come apart here. The absence is
observable **today**; the fan-out is not observable **ever** — the day automation lands, a wildcard already
sitting in a descriptor becomes a cross-tenant delivery with nobody re-reading the file that declared it,
and a delivery that went to the wrong tenant is not an absence anybody notices. The descriptor is the
durable artifact, which is the argument *for* tolerating a descriptor ahead of the build in the general
case and the argument *against* it in this one: the artifact outlives the build that would have refused it.
Recorded as **deviation 86** rather than treated as a reading of the existing rule.

## 2. `Publish`, and the guard that is the reason it exists

`events.md`: *"`Publish` must **refuse** a name matching `^(entity|auth|storage)\.`, or a host can mint an
event indistinguishable from a real data change, and every descriptor rule and after-hook subscribing to
`entity.orders.updated` would fire on a forged one — with a `partitionkey` and provenance nobody wrote a row
for."*

**There was no `Publish` to guard.** No publish surface exists anywhere in `src/`: `IOutboxStore` has
`EnsureAsync`/`ClaimAsync`/`MarkDispatchedAsync`/`ReleaseAsync` and **no append**, because a data event is
appended by `OutboxTable.InsertAsync`, which is `internal` to the EF driver and runs on the caller's own
`DbTransaction`. So the guard needs its subject built with it, and this PR builds both.

### The shape

- **`IOutboxStore.AppendAsync(AlvoCustomEvent, CancellationToken)`** — the port grows one member, and it keeps
  the port's own rule: *one statement, autocommit, never a read followed by a write in one transaction*
  (spike Q5). It is the **custom-event** path only; a data event still never travels through it, which is
  what keeps `OutboxTable`'s transactional emit the single authority for "no lost and no phantom event".
- **`AlvoCustomEvent`** — the parameter type, and the reason the guard cannot be routed around. See
  *§2.1 The bypass two reviews found*.
- **`IAlvoEvents.PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data,
  AlvoContext context, CancellationToken)`** in `Abstractions`, beside `IAlvoData`, whose parameter order it
  copies exactly — `AlvoContext` explicit and never an ambient accessor.
- **`AlvoEvents`** (internal, core) implements it: guards the name, builds the envelope through the same
  `AlvoEventId.Create` and the same `AlvoEvent.DefaultSource` a data event uses, and appends.

### The guard

`AlvoEventName.EnsureCustom` (in **`Abstractions`**, beside `AlvoEvent`, because the reserved namespaces are
part of the *wire* contract) refuses, with `ArgumentException`, in this order:

1. a name that is null, empty or whitespace;
2. a name whose **first segment** is one of `EventPattern.ReservedNamespaces` (`entity`, `auth`, `storage`)
   — the guarantee, stated against the same authority §1 reads;
3. a name that is not two or more dot-separated `[a-z][a-z0-9_]*` segments.

**Ordinal, not case-insensitive, and the reason is what "indistinguishable" means.** Distinguishability is
decided by exactly one reader — `EventSubscriptions.TryReadSubscription`, which compares segment 0 against
`"entity"` with `StringComparison.Ordinal`. `Entity.orders.updated` selects no hook there, so it is not the
forgery the ruling is about. Rule 3 refuses it anyway, one rule later and for a different reason (the name
is not well-formed), so nothing turns on the guard being loose.

### 2.1 The bypass two reviews found, and the shape that closes it

**The first draft put the guard only in `AlvoEvents.PublishAsync` and let `AppendAsync` take a bare
`AlvoEvent`.** Both the plan guard and the C# review found the same hole independently, and the C# review
reproduced it: `IOutboxStore` is **public** and DI-registered (`AlvoEfCoreProvider.cs:83`), and `AlvoEvent` is
a public record with public `required` initializers — so a host could resolve the port and append
`entity.orders.updated` with `authtype: system` and a payload of its choosing, firing every after-hook
subscribed to the real name for a row nobody wrote. Precisely the forgery the guard exists to refuse,
reachable one layer below it. Worse: nothing before this PR could insert an arbitrary envelope at all, so the
PR that added the guard also added the primitive that bypassed it.

**The fix is the parameter type, not a second check.** `AppendAsync` now takes `AlvoCustomEvent`, whose only
door — `AlvoCustomEvent.Create` — runs `EnsureCustom` first. A check in the driver would have to be repeated
by every other driver and be silently absent from the one that forgot; a check on an interface cannot be
enforced at all. This is the house rule `IOutboxStore`'s own remarks state for the UUIDv7 id — *"the wrong
implementation is unavailable rather than merely discouraged"* — applied to the **caller**.

**An intermediate draft made the constructor `internal` instead, and that was wrong in both directions.** It
rested on `InternalsVisibleTo`, which names a published assembly and is forgeable — the caveat this repo
already documents — and it locked out the two callers that legitimately need to build one: a host assembling
its own envelope, and `OutboxStoreContractTests`, the **public** suite an *external* driver author inherits to
prove their `AppendAsync` works. That build failure is what surfaced the error: the invariant that matters is
not *"only the framework constructs one"* but **"none carries a reserved name"**, and a public guarded factory
holds exactly that, for everyone, with no forgeability caveat.

**`AlvoEventName` therefore lives in `Abstractions` and `EventPattern` kept only the wildcard half.** The
split is along the two contracts: reserved namespaces are the **wire** contract (which names Alvo emits, so
which a host may not mint) and must be enforceable at the layer the port lives in; wildcards are the
**descriptor** contract (what a rule may subscribe to) and belong next to the apply path that refuses them.

**Rule 3 is well-formedness, not the designed namespace.** `events.md` is explicit that the real fix is *"a
**designed** namespace, once — not a prefix bolted on under one PR's schedule"*, and this PR does not
design one. Rule 3 only keeps the outbox's `event_type` column to the shape the rest of the system already
reads.

### Deviation: a published custom event can be subscribed to by nothing

`$defs/eventPattern` is **frozen** to `^(entity|auth|storage)\.([a-z][a-z0-9_]*|\*)\.([a-z]+|\*)(\.batch)?$`.
So `order.approved` is unrepresentable as a subscription, and the guard forbids `Publish` from using the
three namespaces that *are* representable. The two rules together mean **every** custom event this API can
publish matches zero descriptor rules and zero after-hooks, today and until the designed namespace lands.

What ships is therefore a durable, inspectable, ordered outbox row and nothing downstream of it: the
dispatcher claims it, `EventSubscriptions.Matching` selects nothing (its type has no `entity.` prefix, which
is the fail-closed branch it already had), `alvo.events.filtered` is incremented and the entry is marked
dispatched. That is stated as **deviation 87** rather than discovered by the first host that publishes one
and waits for a webhook — and the XML doc on `PublishAsync` says it in the place an author reads before
calling it.

The guarantee is the point regardless: it is cheap now and unbolt-able later, and a guard added *after* a
host is already minting `entity.orders.updated` is a breaking change to that host rather than a rule it
never got to break.

### Deviation: a custom event is not transactional with anything

The spec's hard guarantee — *"event sa publikuje v tej istej transakcii ako dátová zmena"* — is about a
**data change**, and a custom application event has no data change to be atomic with. `AppendAsync` is one
autocommit statement, so a host that wants its custom event atomic with its own write does not get that from
this API. Named as **deviation 88** because an author reading "transactional outbox" in the spec will
otherwise assume it, and stated on `PublishAsync` itself.

## 3. Deviation 76's remainder

Three defects and one test, all deferred to here by deviation 83 (which deferred them for review coverage:
#157 carried 133 files and CodeRabbit skipped it entirely, so this PR stays under 100).

| `crm.alvo.json` | Defect | Fix |
|---|---|---|
| `:143` | `old.stage in ['won', 'lost']` — the `Condition` profile has no list literal | `(old.stage == 'won' \|\| old.stage == 'lost')` |
| `:147` | `new.stage in ['won', 'lost']` — same | `(new.stage == 'won' \|\| new.stage == 'lost')` |
| `:221` | `"to": "{{@user.email}}"` — `TemplatePlaceholder.Roots` is `new`/`old`/`event`/`@user`, and `@user` resolves `id` only | `"{{new.owner_id}}"` |

**And the strengthening the three fixes make load-bearing.**
`DescriptorToSchemaMapperTests.Every_example_marked_not_runnable_really_is_refused` asserts only
`Should.Throw<InvalidDataException>`, so a CEL syntax error stands in silently for the feature refusal the
marker claims. It now asserts the **reason**: the refusal must name the unhonoured feature, so the day
`default` lands the fact fails and the marker has to be deleted — which is the whole point of a marker.

`crm.alvo.json:82`'s `rollup.where` list literal stays untouched: PR6 shipped `RollupResolver`'s refusal for
`rollup.where`, so that line is refused by a structured unhonoured-feature error and is not a defect this PR
can fix by editing.

## 4. Every fact, and the mutation that killed it

Each mutation was applied to the source, compiled, and the whole fast suite run; every one is a **measured**
kill, not a predicted one.

| Fact | Mutation | Result |
|---|---|---|
| `A_wildcard_automation_trigger_is_refused_at_apply` | `EventPattern.HasWildcard` returns `false` unconditionally | killed |
| `A_wildcard_function_trigger_is_refused_at_apply` | the `functions` half of the apply walk is deleted | killed |
| `A_pattern_without_a_wildcard_still_applies` | `HasWildcard` returns `true` unconditionally | killed |
| `A_star_inside_a_segment_is_not_a_wildcard_segment` | `HasWildcard` becomes `pattern.Contains('*')` | killed |
| `The_reserved_namespaces_are_the_schema_s_own` | `storage` dropped from `ReservedNamespaces` | killed |
| `A_wildcard_trigger_is_reported_as_a_structured_error` | the validator's top-level pass is deleted | killed |
| `Publish_refuses_a_reserved_namespace` | `EnsureCustom` skips the reserved-prefix check | killed |
| `Publish_refuses_a_malformed_name` | `EnsureCustom` skips the grammar check | killed |
| `Publish_appends_one_entry_carrying_the_guarded_name` | the envelope's `partitionkey` becomes the type | killed |
| `An_event_a_host_published_selects_no_after_hook` | the prefix comparison in `TryReadSubscription` is inverted | killed |
| `Every_example_marked_not_runnable_really_is_refused` | the refusal message stops carrying the feature's fix | killed |
| `The_only_door_into_the_queue_refuses_a_reserved_name` | `AlvoCustomEvent.Create` stops calling the guard | killed |
| `A_malformed_automation_entry_is_reported_rather_than_thrown_on` | the validator drops its `ValueKind` check | killed |
| `Publish_appends_one_entry_carrying_the_guarded_name` | the partition key loses its type prefix | killed |
| `An_appended_custom_event_is_claimable_like_an_emitted_one` | `EfCoreOutboxStore.AppendAsync` drops the entry | killed (on the **real SQLite driver**) |

**One mutation survived first, and the fact was wrong rather than the mutation weak.** Inverting
`TryReadSubscription`'s prefix comparison left `An_event_a_host_published_selects_no_after_hook` green,
because the fact published `deals.approved` — **two** segments, so the segment-count check turned it away
before the prefix was ever compared, and the fact was pinning the wrong half of the reader. The published
name is now `crm.deals.updated`: three segments, an entity the catalog really has hooks on, a suffix the
reader really maps — everything a data event has except the reserved namespace, which is the nearest legal
forgery a host can attempt. The mutation is killed with the name corrected, and the reason is recorded on the
fact itself so nobody shortens it back.

**Two mutations are unrepresentable, which is worth recording rather than retrying.** "Return before
`AppendAsync`" does not compile (`CS9113`: the `outbox` parameter becomes unread) and "drop the payload" does
not compile (`IDE0060`: `data` becomes unused). The analyzer set makes those two defects unwritable rather
than merely untested.
