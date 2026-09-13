# Runtime-apply boot: how a dashboard-first host starts (#83)

> Closes the half of #83 that blocks F5. Companion to
> `2026-08-02-startup-lifecycle-and-config-dx-design.md`, whose five stages this
> extends rather than replaces.

## Sources consulted

- `docs/product/baas-analyza.md` §2.14 (`:524`, `:533`, `:554`, `:557`) — the two
  primary modes, the four descriptor doors, the "one Management API" contract, and
  the 60-second acceptance criterion.
- `docs/product/baas-analyza.md` §2.13 (`:509`–`:512`) — one diff engine, two
  desired-state inputs; append-only descriptor versioning as git's runtime
  replacement.
- `docs/product/alvo-specifikacia.md` §0.5 (`:51`, `:92`, `:94`) — mode 1, the four
  doors, contract 4.
- `docs/PLAN.md` §4 — *"Two sources of truth, one format"*.
- `2026-08-02-startup-lifecycle-and-config-dx-design.md` — the five stages, the
  stage-0 contract, "Not foreclosing #141", the configuration surface.
- `src/MMLib.Alvo/Migrations/Internal/{AlvoBootService,DescriptorBootPlan}.cs`,
  `src/MMLib.Alvo/Migrations/RuntimeSchemaService.cs`,
  `src/MMLib.Alvo.Abstractions/Migrations/IDescriptorVersionStore.cs`.

## The measured starting point, which is not what #83 says

#83 describes the symptom as *"a runtime-applied project currently needs one apply
after every restart"* — an unprimed `IPolicyCatalogProvider` that `IPolicyEngine`
treats as deny-everything. **That is no longer true, and the truth is worse.**

`AddAlvo` registers `AlvoBootService` unconditionally. Its first stage is
`DescriptorBootPlan.LoadAsync`, which begins:

```csharp
var source = _source
    ?? throw new AlvoStartupRefusedException(NoDescriptorSourceMessage, NoDescriptorSourceFix);
```

`AlvoBootService.StartingAsync` rethrows `AlvoStartupRefusedException` untouched, so
it propagates out of `IHostedLifecycleService.StartingAsync` and **fails the host's
start**. The only `IDescriptorSource` in the tree is `internal sealed class
FileDescriptorSource`, reachable only through `FromDescriptor(path)`.

So a host that means to receive its descriptor at runtime has no source to attach,
therefore does not start, therefore never reaches the state #83 describes. Mode 1 of
the specification — *"stiahneš image, spustíš, otvoríš dashboard, vytvoríš projekt"*
— does not exist.

**Not pinned by anything.** `grep -rn NoDescriptorSource` matches three declarations
and one doc comment; no test asserts the refusal. A stated start-time behaviour with
no fact behind it is the first thing this design adds, independent of everything
else.

## The problem the two obvious fixes run into

#83 proposes *"a hosted service that primes from the applied-schema store on start"*.
There now **is** such a service, so the work is not adding one — it is giving stage 0
a second input. Both obvious shapes lose something:

1. **`DbDescriptorSource : IDescriptorSource` over `IDescriptorVersionStore`.**
   Smallest diff, nothing above stage 0 changes — but it puts a database read
   *inside* stage 0 and breaks its stated contract:

   > **The absence of a database is the contract, not an implementation detail.** …
   > So this type takes no migrator, no store and no introspector, and a fact that
   > gives it none is the proof.

   That contract is load-bearing: it is what lets a host ask to *serve* an
   already-migrated database without also asking to migrate it.

2. **Reorder the stages** so stage 1's store read comes first in runtime-apply mode.
   Keeps the contract true for code-first, but makes the stage *sequence* conditional
   on the mode, and the sequence is the design's main idea.

## The design

### Stage 0 keeps its contract, because the caller does the reading

The contract says `DescriptorBootPlan` takes no store. It does **not** say the
descriptor JSON must come from a file. Split the type's one method along that line:

```csharp
// unchanged in behaviour: reads the source, then plans.
internal async Task<BootPlan> LoadAsync(CancellationToken ct)
{
    var source = _source ?? throw new AlvoStartupRefusedException(...);
    return Plan(await source.LoadAsync(ct).ConfigureAwait(false));
}

// NEW: everything stage 0 actually does — validate, parse, map, compile, warn.
// Takes no source, no store, no database. The contract is unchanged and the fact
// that proves it still passes. Synchronous, because every step is CPU plus one
// already-materialised string: LoadAsync is async because the *source* is.
internal BootPlan Plan(string descriptorJson)
```

`AlvoBootService` then reads the stored descriptor itself — in **stage 1**, where the
store read already lives and is already unconditional — and hands the JSON to
`Plan`. Nothing about stage 0 becomes database-aware; the stage *order* is the
only thing that differs, and it differs in the direction stage 1 was always in.

This is deliberately neither of the two shapes above. It is what both were reaching
for.

### Which mode a boot is in is decided by what is configured

No flag, and no new mode enum. `IDescriptorSource` present → **code-first**, today's
path, byte-for-byte unchanged. Absent → **runtime-apply**. That matches how the rest
of the framework already decides things (a driver is registered or it is not) and it
means no existing host changes behaviour.

The refusal `DescriptorBootPlan.NoDescriptorSourceMessage` therefore stops being
reachable from the boot, and its fix suggestion changes: "call `FromDescriptor`" is
now one of *two* right answers. See *Configuration* below.

### Runtime-apply needs a project name, and that is a real decision

`IDescriptorVersionStore` is keyed by project (`GetCurrentAsync(string project, …)`),
and in runtime-apply mode there is no file to read the name from. Chicken and egg.

Two ways out, and the choice is not free:

- **A port member** — "the one project in this database". Semantically right, because
  standalone is *"jedna databáza per projekt"* (`baas-analyza.md:524`) — but it adds
  public surface to `Abstractions` that **#141** (one host, many projects) would have
  to break.
- **Configuration** — `Alvo:Schema:Project` / `Alvo__Schema__Project`, inside the
  existing `Alvo:Schema` section. No port change, explicit, and additive when #141
  lands: a host serving several projects names them elsewhere and this key is simply
  absent. It goes *in* that section rather than at the root so the boot's three
  settings keep one binder, one validator and one prefix an operator has to learn.

**Chosen: configuration.** It is also the one that discharges an obligation the
repository already recorded: `docs/architecture/host.md:652` says the operator-facing
`ALVO_*` vocabulary *"has to be settled before the image is published, because after
that the env names are a breaking change (deviation 39)"*. `Alvo__Schema__Project` is
the first member of that vocabulary added since, and is named here so the decision is
visible rather than incidental. **It is the one thing in this design worth renaming now if it is
going to be renamed at all.**

### The five stages, in runtime-apply mode

| stage | code-first (unchanged) | runtime-apply |
|---|---|---|
| 0 | load descriptor from source, validate/map/compile | — |
| 1 | create `alvo.*`, read the applied snapshot | create `alvo.*`, read the applied snapshot **and the latest `DescriptorVersion`** |
| 0′ | — | `Plan(version.DescriptorJson)` — validate/map/compile, no database |
| 2 | plan the diff, judge it | **nothing to diff**: the stored descriptor *is* what was applied |
| 3 | apply, prime the catalog | prime the catalog from stage 0′ |
| 4 | publish `Ready` | publish `Ready` |

Stage 2 collapsing is the point, and it is not a shortcut: the stored descriptor is
by construction the one `RuntimeSchemaService` last applied *in the same transaction*
that wrote the schema (`IRuntimeSchemaWriter.ApplyAndAppendAsync`). There is nothing
to converge on. A drift between them would mean the schema was changed by something
other than Alvo, which is #103/#145 territory, not this design's.

### An empty history starts and serves nothing

The third question #83 does not settle. **Decision: start, serve nothing, report
ready.**

`baas-analyza.md:557` is the binding criterion — *"`docker run mmlib/alvo` = funkčný
backend s dashboardom do 60 s bez akejkoľvek konfigurácie"*. Refusing would mean mode
1 does not exist, which is the state this design is fixing. Reporting **not** ready
would keep the process alive but take the pod out of rotation behind an ingress,
which is exactly where the first-run wizard has to be reachable.

Concretely: a new `SchemaStartupOutcome.Awaiting`, `AlvoBootState.Ready(project,
revision: null)`, and **no policy catalog published at all**.

That last part was going to be "publish an *empty* catalog, because empty and
unprimed both deny but say different things to an operator". **The frozen schema
forbids it**: `schema/project.schema.json` puts `minProperties: 1` on `entities`, so a
zero-entity descriptor is not a valid descriptor and an empty catalog is not a thing
this system can express. Leaving the provider unprimed is therefore not a shortcut —
it is the only representable state, and it is the safe one.

It is also harmless here, which is the part worth stating: nothing is served because
nothing is *declared*. Zero entities means zero Data API routes, so a data request
404s at routing, before authorization is consulted at all. The deny-everything an
unprimed provider produces has nothing to deny.

### What a stored descriptor is, and is not, trusted to be

`Plan` runs `EnsureValid` — the `IDescriptorValidator` port — before parsing, exactly as the
file path does, and compiles the policy through the same `PolicyCatalog.Build`. There is no
step the file path takes that this one skips.

**What validation is not is an authenticity gate, and in this mode that matters more than in
the other one.** In dashboard-first mode the versions table *is* the policy source of truth
with no file to contradict it: anyone who can `INSERT` into `alvo_descriptor_versions`
authors the authorization policy the next boot compiles and serves — over the *existing*
physical tables, with no schema change needed to make it take effect, because stage 2 is a
no-op. On the code-first path a rogue row can at worst confuse a diff; here it is the input.

That is inherent to "two sources of truth, one format" rather than introduced here, and the
mitigation is the same one that protects the table itself: it is Alvo's own system schema, in
the project's own database, reachable only by whoever already holds the connection string.
But it is the argument for a checksum or signature on stored descriptors when the Management
API (#212) opens a second writer, and it should be read alongside the next paragraph.

**`IRuntimeSchemaWriter.ApplyAndAppendAsync` is the only sanctioned writer**, and the "stage 2
has nothing to converge on" claim rests on that rather than on the type system:
`IDescriptorVersionStore.AppendAsync` is a *public* port member, so a host — or a future
Management API — can append a version row without applying it, and the next boot would then
prime rules for a schema that was never applied. No production code does (the only other
callers are the testing fakes and the contract suite), and the boot now refuses a row whose
descriptor names a different project, but nothing structurally prevents it.

### What this does not do

- **No `DbDescriptorSource`.** #212 names "no DB-backed `IDescriptorSource`" as its
  blocker; this design answers the need without the type, so #212's dependency is
  satisfied without adding a public seam nobody has a second implementation for.
- **No Management API.** Applying at runtime still has no HTTP surface (#212).
- **No new routes at runtime, and for the empty-history case that bites on the *first*
  apply.** #103 is unchanged: the Data API's route table is built once, at first
  enumeration, with a `NullChangeToken`. For a populated history the descriptor is read at
  *boot*, so routes map normally and #103 bounds only the second apply. For an **empty**
  history the first apply is itself post-boot, so mode 1 — run the image, open the
  dashboard, create a project — still needs a **restart** before data is servable. That is
  fail-closed, and the `Awaiting` warning now says it rather than implying otherwise.
- **Part 2 of #83 stays open** — the unhonoured-subsystems warning still fires only
  on the boot path, so a descriptor applied through `RuntimeSchemaService` earns no
  line. PR #215 records that; closing it needs an `ILogger` on
  `RuntimeSchemaService`'s **public** six-parameter constructor, which moves the
  public-API baseline.

## Deviations

1. **`Alvo__Schema__Project` is a new operator-facing environment name**, and
   `host.md:652`/deviation 39 says the `ALVO_*` vocabulary must be settled before the
   image ships. This design settles its first member rather than deferring, because
   runtime-apply cannot boot without it. Renaming it later is breaking.
2. **Stage 2 does no work in runtime-apply mode.** The design's "five stages, each at
   its own risk level" is preserved in *order* but one stage is a no-op, which the
   original spec did not contemplate.
3. **`Ready` with a null revision is a new state.** `AlvoBootState.AppliedRevision`
   was `int?` already, so no shape changes — but "ready and serving nothing" is a
   combination no previous boot could produce.
4. **The stated `NoDescriptorSourceMessage` refusal becomes unreachable from the
   boot** and its fix suggestion gains a second answer. It is kept (a host may still
   ask for a plan without a source) and now has the test it never had.
5. **Stage-0 checks no longer run before anything is durable, in this mode.** The
   2026-08-02 design justifies putting the reserved-name and format checks in stage 0
   because they *"run before anything is durable and still fail the start"*. Here the
   stage-1 store read comes first, and its side effect is the driver creating the
   `alvo.*` system schema — DDL against the target database before any descriptor has
   been validated. Unavoidable (the descriptor lives in that database) and harmless
   (those tables are Alvo's own and are created idempotently), but it is a property the
   earlier design stated and this mode does not have.
6. **A wrong project name is indistinguishable from a first run.** Both produce
   `Awaiting`: ready, serving nothing. It fails closed and never open, but it is a pod
   that passes readiness behind an ingress because of one character. Telling the two
   apart would need a "does this store hold any project" port member — exactly the
   surface this design declined for #141's sake — so the mitigation is the warning line,
   which names the variable. The **outbox** consequence of the same typo is not
   cosmetic and is handled: see below.
7. **A stored descriptor this build refuses stands down rather than failing the start**,
   which is the opposite of the code-first answer. In code-first, failing fast is right
   because a human edits the file. Here the descriptor lives in the database and the
   only sanctioned editor is the dashboard, which is in this process — failing the start
   crash-loops the container and removes the one tool that could repair it. Reachable
   without anyone erring: an older image against a row a newer one wrote (deviation 55).
8. **`Ready` no longer implies a primed policy catalog, and one consumer had to change.**
   `OutboxDispatcher` gated its pump on `AlvoBootPhase.Ready` alone. Under `Awaiting`
   that pump claims entries, `Catalog` throws, the per-entry containment abandons the
   attempt, and `attempts` reaches `MaxAttempts` within about a minute — after which
   `ClaimAsync` excludes those entries permanently and this build has no DLQ. With a
   typo'd project name against a populated database, the entries burned are the real
   project's, because the outbox has no project column. The pump now also requires a
   primed catalog and stands down loudly otherwise. Two doc comments that asserted the
   old invariant (`OutboxDispatcher.Catalog`, `AlvoBootState`) are corrected.

## Ratification needed from the maintainer

- **`Alvo__Schema__Project` as the key name** (deviation 1). This is the breaking-once-shipped
  one.
- **Ready-with-nothing-served** (deviation 3) — confirmed in advance for this design,
  recorded here so the reasoning is reviewable rather than implicit.

## Definition of Done

- A host with `AddAlvo` + a driver + `Alvo__Schema__Project` and **no** `FromDescriptor`
  starts, primes from the stored descriptor, and serves its entities — pinned by a
  fact, not by a doc comment.
- The same host against an **empty** history starts, reports ready, serves no data
  route — asserted as a **404 from the router**, with a health request beside it as the
  non-vacuity control, rather than inferred from an empty registry — and logs one line
  naming the state and the restart the first apply needs.
- A pending outbox entry survives an unprimed boot untouched (`attempts` still 0), which
  is the regression deviation 8 describes.
- A stored descriptor this build refuses, and one whose own name does not match the
  configured project, both **stand down**: process alive, readiness `Failed`, nothing
  primed.
- A host with **neither** a descriptor source nor `Alvo__Schema__Project` still refuses, by
  name, with a fix suggestion that names both answers — the test
  `NoDescriptorSourceMessage` never had.
- Code-first boots are byte-for-byte unchanged: the existing boot suite passes
  untouched, and `DescriptorBootPlan` still takes no migrator, store or introspector
  (the existing fact still proves it).
- `docs/architecture/host.md` records `Alvo__Schema__Project` in the `ALVO_*` vocabulary
  bullet, and #83's remaining scope (part 2) is restated there.
