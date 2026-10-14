# HX-01 — Event envelope attributes and provenance — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every Alvo event envelope the provenance the rest of the host-extensibility epic stands on — authcontext `authtype` values, `authclaims`, `tenantid`, Distributed Tracing, effective `chaindepth`/`causationid` — enforce one chain-depth limit at every write and publish entry, let an entity exclude fields from its event images, and lift the `@tenant.id`/`@user.roles` after-hook refusals (#153) while closing the after-hook phase hole (#325).

**Architecture:** Provenance becomes two public init properties on `AlvoContext` (`Provenance`, `ActorKind`) plus an internal `AsyncLocal` (`AlvoFlow`) in Abstractions; the public `AlvoEventProvenance.Effective` merges them (depth = max, ids = explicit else ambient) and an internal `AlvoEnvelopeStamp` is the one place both emit paths (EF driver, core `IAlvoEvents`) turn a context into envelope attributes. Depth is enforced by an internal `AlvoChainDepthLimit` (Abstractions/Data) that the core builds from `AlvoExtensionOptions` and the EF driver, `InMemoryAlvoData` and `AlvoEvents` call before anything is written; its refusal is the public `AlvoChainDepthExceededException`, mapped to `508 chain-depth-exceeded`. `events.exclude` is a descriptor slot mapped onto `EntitySchema.EventImageExclusions`, applied by `OutboxEventFactory`, and guarded at apply by the after-hook compiler.

**Tech Stack:** .NET 10, C#, xUnit v3 on Microsoft.Testing.Platform, Shouldly, Verify, NetArchTest, PublicApiGenerator, Testcontainers (PostgreSQL), Corvus JSON Schema validator, Blazor + MudBlazor (dashboard), Microsoft.Playwright (admin e2e), Astro/Starlight (docs).

**Spec:** `docs/superpowers/specs/2026-10-14-host-extensibility-design.md` — §4.6, §4.7 (incl. *HX-01 settled details*), §5 rows marked `01`, §6.2, §7 *HX-01*, Appendix A rows B2, R-M2, R-M3, R-M4, R-3/R-4/R-5, R-9, R-10, minors 14/21. Issue: **#378** (`[HX-01]`); closes **#153** and **#325**; repoints to **#372** (N-2).

## Global Constraints

- Branch `hx/01-envelope-provenance`, based on `main`; never push to `main`; one writer per worktree.
- Pre-release: attribute *values* change without a `payloadversion` bump (§4.7); `AlvoEvent.CurrentPayloadVersion` stays `1`.
- `authtype` values are exactly `api_key`, `user`, `system`, `unauthenticated`, `unknown`; `AlvoEventAuthType.Anonymous` is **removed** (so stale compiled comparisons fail at build).
- `AlvoContext.ActorKind` unset ⇒ derived from `User` exactly as `AuthTypeOf` derived it: anonymous user → `Anonymous`, system user → `System`, anything else → `ApiKey`; `Unknown` only when set explicitly.
- `AlvoEventJson.Read` normalises legacy `apikey` → `api_key`, `anon` → `unauthenticated`; the outbox row is never rewritten.
- `authclaims` is the JSON string `{"roles":[…]}` (ordinal order) and is **stripped from the default webhook body**.
- `tenantid` = the write's tenant; absent for global rows and tenantless system writes.
- Depth: effective depth = `max(explicit, ambient)`; a write or publish whose depth is **greater than** `MaxDepth` (default **8**, `Alvo:Extensions:MaxDepth`) is refused; depth 8 is allowed. Refusal = `AlvoChainDepthExceededException` → `508`, type `chain-depth-exceeded`, **non-retryable**.
- `AlvoFlow` has **no production setter** in HX-01; only unit tests set it (IVT). Architecture fact: only `MMLib.Alvo` and `MMLib.Alvo.Data.EntityFrameworkCore` may call `AlvoFlow.Enter`.
- `events.exclude`: `entities.<e>.events = { "exclude": [name, …] }`, `additionalProperties: false`, `uniqueItems`; every name a declared field; `id`, `tenant_id` and managed columns refused; `changed` keeps excluded names.
- Public surface added is exactly §5's HX-01 rows plus `EntityDescriptor.Events`/`EntityEvents` (this plan amends §5, Task 17). Everything else `internal`.
- C# style: `alvo-dotnet-conventions` — no inline comments, methods ≲ 25 lines, XML docs on public members, English only. `.cs` files are **UTF-8 with BOM + CRLF** (`.editorconfig`, `.gitattributes`); after creating or editing `.cs` files with any tool, run `dotnet format whitespace MMLib.Alvo.slnx --include <the files>` before committing. `.md`/`.mdx`/`.json` stay LF, no BOM.
- Single-test runs: `dotnet test --project <test project> --filter-class '*<ClassName>'` (MTP). Rings: `scripts/test-ring0` after every task, `scripts/test-ring1` after each slice marked **[slice end]**, `scripts/test-ring2` before the PR.
- A moved `*.verified.*` snapshot or a grown `PublicApi.*.verified.txt` trips the turn-review gate: dispatch `alvo-snapshot-judge`, and justify each added public symbol against `alvo-architecture-rules` (*public is the contract*) and spec §5.
- Commits: Conventional Commits, one per task, never `git add -A` (stage the task's files by path).

## Review Focus

1. **Outbox rows written before this PR** reach the dispatcher without `tenantid`/`authclaims`: a hook reading `@tenant.id` or `@user.roles` must select nothing (fail-closed), never compare against an empty value — test in Task 12.
2. **A malformed or foreign `authclaims` string** (hand-written, non-object, roles not strings) must read as "absent", never throw inside the dispatcher's selection — tests in Task 4 (`RolesIn`) and Task 12.
3. **An undefined `AlvoActorKind` value** cast from an integer, or a negative `AlvoProvenance.Depth`, must not produce a forged `authtype` or a depth that slips under the limit — tests in Task 1 and Task 4.
4. **Renaming or removing a field the entity excludes** in the dashboard must carry the exclusion (rename) or name it as a blocking reference (removal), otherwise the next apply is refused — tests in Task 16.
5. **Ambient flow leakage**: a nested `AlvoFlow.Enter` must restore the outer value on dispose, and an ambient value set inside a child task must not leak back to the caller — tests in Task 2.

---

## File structure

Created:

| File | Responsibility |
|---|---|
| `src/MMLib.Alvo.Abstractions/Identity/AlvoActorKind.cs` | public enum: how a caller authenticated |
| `src/MMLib.Alvo.Abstractions/Identity/AlvoProvenance.cs` | public record: correlation/causation/depth/trace |
| `src/MMLib.Alvo.Abstractions/Identity/AlvoFlow.cs` | internal ambient provenance (`AsyncLocal`) |
| `src/MMLib.Alvo.Abstractions/Data/AlvoChainDepthExceededException.cs` | public 7th `IAlvoData` exception family |
| `src/MMLib.Alvo.Abstractions/Data/AlvoChainDepthLimit.cs` | internal limit + `EnsureWithin` |
| `src/MMLib.Alvo.Abstractions/Events/AlvoEnvelopeStamp.cs` | internal: context → envelope attributes, shared by both emit paths |
| `src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs` | public options `Alvo:Extensions` |
| `src/MMLib.Alvo/Extensions/Setup.cs` | `AddAlvoExtensions()` registration |
| `src/MMLib.Alvo/Extensions/Internal/AlvoExtensionOptionsConfiguration.cs` | bind + validate |
| `src/MMLib.Alvo/Events/Internal/AfterImagePhase.cs` | #325 rule: which row images an after-point has |
| `src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Events.cs` | dashboard edit of `events.exclude` |
| `src/MMLib.Alvo.Testing/Events/AlvoEventTenancyTests.cs` | cross-tenant adversarial after-hook suite |
| tests listed per task | |
| `website/src/content/docs/extending/index.mdx`, `where-logic-goes.mdx`, `event-envelope.mdx` | docs |
| `website/src/snippets/extending/event-envelope/01-exclude.alvo.json` | docs snippet |
| `examples/_negative/events-exclude-typo.json`, `events-exclude-duplicate.json` | schema negatives |

Modified (main ones): `AlvoContext.cs`, `AlvoEvent.cs`, `AlvoEventAttributes.cs`, `AlvoEventJson.cs`, `AlvoEventProvenance.cs`, `EntitySchema.cs`, `EntityDescriptor.cs`, `IAlvoData.cs`, `IAlvoEvents.cs`, `Properties/AssemblyInfo.cs` (no change needed — grants already cover core, EF, Testing, test projects), `OutboxEventFactory.cs`, `EfAlvoData.cs`, `AlvoEfCoreProvider.cs`, `InMemoryAlvoData.cs`, `AlvoEvents.cs`, `EventActionExecutor.cs`, `EventSubscriptions.cs`, `EventLog.cs`, `AfterHookCompiler.cs`, `AlvoTemplate.cs`, `EnvelopeProvenance.cs`, `UnhonouredFeatures.cs`, `PolicyCatalog.cs`, `DescriptorToSchemaMapper.cs`, `DescriptorValidator.cs`, `AlvoProblemTypes.cs`, `ProblemResultFactory.cs`, `ProblemComponents.cs`, `AlvoServiceCollectionExtensions.cs`, `ApiKeyContextResolver.cs`, `AlvoIdentityContextResolver.cs`, `TemplateDraft.cs`, `FieldReferences.cs`, `FieldEditor.razor(.cs)`, `Entity.razor.cs`, `schema/project.schema.json`, `examples/complex-crm/crm.alvo.json`, `website/src/data/problem-types.json`, docs listed in Task 17.

---

## Task 1: Provenance model on `AlvoContext`

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Identity/AlvoActorKind.cs`
- Create: `src/MMLib.Alvo.Abstractions/Identity/AlvoProvenance.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Identity/AlvoContext.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Identity/AlvoContextProvenanceTests.cs`
- Baseline: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Produces: `public enum AlvoActorKind { Unknown, Anonymous, System, ApiKey, User }`; `public sealed record AlvoProvenance { string? CorrelationId; string? CausationId; int Depth; string? TraceParent; string? TraceState; static AlvoProvenance None }` (all `init`); `AlvoContext.ActorKind` (`AlvoActorKind`, init, derived when unset); `AlvoContext.Provenance` (`AlvoProvenance`, init, default `AlvoProvenance.None`).

- [ ] **Step 1: Write the failing test**

`test/MMLib.Alvo.Abstractions.Tests/Identity/AlvoContextProvenanceTests.cs`:

```csharp
namespace MMLib.Alvo.Abstractions.Tests.Identity;

/// <summary>
/// The two provenance properties an <see cref="AlvoContext"/> carries: how the caller authenticated, and which flow it
/// belongs to (spec 2026-10-14 §4.6, §4.7 <em>HX-01 settled details</em>).
/// </summary>
public class AlvoContextProvenanceTests
{
    [Fact]
    public void The_anonymous_caller_is_derived_as_anonymous()
        => AlvoContext.Anonymous.ActorKind.ShouldBe(AlvoActorKind.Anonymous);

    [Fact]
    public void The_system_caller_is_derived_as_system_whatever_its_tenant()
    {
        AlvoContext.System(tenant: null).ActorKind.ShouldBe(AlvoActorKind.System);
        AlvoContext.System(TenantId.New()).ActorKind.ShouldBe(AlvoActorKind.System);
    }

    [Fact]
    public void A_host_built_context_with_an_ordinary_user_is_derived_as_an_api_key()
        => new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } }
            .ActorKind.ShouldBe(AlvoActorKind.ApiKey, "host-built and third-party contexts keep today's meaning");

    [Fact]
    public void An_explicit_actor_kind_wins_over_the_derived_one()
        => new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated }, ActorKind = AlvoActorKind.User }
            .ActorKind.ShouldBe(AlvoActorKind.User);

    [Fact]
    public void Unknown_is_only_ever_set_explicitly()
        => (AlvoContext.Anonymous with { ActorKind = AlvoActorKind.Unknown }).ActorKind.ShouldBe(AlvoActorKind.Unknown);

    [Fact]
    public void Changing_the_user_of_a_derived_context_re_derives_its_kind()
        => (AlvoContext.Anonymous with { User = UserId.New() }).ActorKind.ShouldBe(AlvoActorKind.ApiKey);

    [Fact]
    public void A_context_carries_no_provenance_until_one_is_given()
    {
        var provenance = AlvoContext.Anonymous.Provenance;

        provenance.ShouldBeSameAs(AlvoProvenance.None);
        provenance.Depth.ShouldBe(0);
        provenance.CorrelationId.ShouldBeNull();
        provenance.CausationId.ShouldBeNull();
    }

    [Fact]
    public void A_negative_depth_is_refused_where_it_is_built()
        => Should.Throw<ArgumentOutOfRangeException>(() => new AlvoProvenance { Depth = -1 })
            .ParamName.ShouldBe(nameof(AlvoProvenance.Depth));

    [Fact]
    public void A_null_provenance_is_refused_rather_than_read_as_none()
        => Should.Throw<ArgumentNullException>(() => AlvoContext.Anonymous with { Provenance = null! });
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*AlvoContextProvenanceTests'`
Expected: build FAIL — `AlvoActorKind`, `AlvoProvenance`, `ActorKind`, `Provenance` do not exist.

- [ ] **Step 3: Implement**

`src/MMLib.Alvo.Abstractions/Identity/AlvoActorKind.cs`:

```csharp
namespace MMLib.Alvo;

/// <summary>
/// How the principal behind an <see cref="AlvoContext"/> authenticated — never what it may do. Each member maps onto
/// one value of the CloudEvents authcontext extension's <c>authtype</c>, which is how an event reports it.
/// </summary>
public enum AlvoActorKind
{
    /// <summary>Nothing says how the caller authenticated; reported as <c>unknown</c>. Only ever set explicitly.</summary>
    Unknown,

    /// <summary>The caller presented no credential; reported as <c>unauthenticated</c>.</summary>
    Anonymous,

    /// <summary>Alvo itself acted, as <see cref="AlvoContext.System"/>; reported as <c>system</c>.</summary>
    System,

    /// <summary>An API key authenticated the caller; reported as <c>api_key</c>.</summary>
    ApiKey,

    /// <summary>A signed-in person authenticated the caller; reported as <c>user</c>.</summary>
    User,
}
```

`src/MMLib.Alvo.Abstractions/Identity/AlvoProvenance.cs`:

```csharp
namespace MMLib.Alvo;

/// <summary>
/// Which flow a data operation belongs to: the id every step of one end-to-end flow shares, the immediate cause, how
/// many hops deep the causation chain is, and the W3C trace context it runs under.
/// </summary>
/// <remarks>
/// Carried explicitly on <see cref="AlvoContext.Provenance"/> and ambiently by the framework's own pumps; a port entry
/// merges the two with <c>AlvoEventProvenance.Effective</c>, where depth is the larger of the two, so code can raise
/// the depth it reports and never lower it.
/// </remarks>
public sealed record AlvoProvenance
{
    private readonly int _depth;

    /// <summary>Gets the id shared by everything in one end-to-end flow, or <see langword="null"/> when none is known.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Gets the id of the immediate cause (an event id), or <see langword="null"/> for a direct caller.</summary>
    public string? CausationId { get; init; }

    /// <summary>Gets how many hops deep the causation chain is; <c>0</c> for a caller acting directly.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Depth
    {
        get => _depth;
        init => _depth = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(Depth), value, "A causation chain is never less than zero hops deep; a direct caller is depth 0.");
    }

    /// <summary>Gets the W3C <c>traceparent</c> the flow runs under, or <see langword="null"/>.</summary>
    public string? TraceParent { get; init; }

    /// <summary>Gets the W3C <c>tracestate</c> that accompanies <see cref="TraceParent"/>, or <see langword="null"/>.</summary>
    public string? TraceState { get; init; }

    /// <summary>Gets the provenance of a caller nothing else is known about: depth 0 and no ids.</summary>
    public static AlvoProvenance None { get; } = new();
}
```

In `src/MMLib.Alvo.Abstractions/Identity/AlvoContext.cs`, add after `private readonly IReadOnlySet<Role> _roles = …;`:

```csharp
    private readonly AlvoActorKind? _actorKind;

    private readonly AlvoProvenance _provenance = AlvoProvenance.None;
```

and after the `Tenant` property:

```csharp
    /// <summary>
    /// Gets how the caller authenticated. Unless set, it is derived from <see cref="User"/>: the anonymous caller is
    /// <see cref="AlvoActorKind.Anonymous"/>, the system caller <see cref="AlvoActorKind.System"/>, and any other
    /// <see cref="AlvoActorKind.ApiKey"/> — so a context a host builds by hand keeps the meaning it always had.
    /// </summary>
    public AlvoActorKind ActorKind
    {
        get => _actorKind ?? DerivedActorKind(User);
        init => _actorKind = value;
    }

    /// <summary>Gets the flow this caller acts in; <see cref="AlvoProvenance.None"/> unless one is given.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public AlvoProvenance Provenance
    {
        get => _provenance;
        init => _provenance = value ?? throw new ArgumentNullException(nameof(Provenance));
    }
```

and before `HasRole`:

```csharp
    private static AlvoActorKind DerivedActorKind(UserId user) =>
        user == default ? AlvoActorKind.Anonymous
        : user == _systemUser ? AlvoActorKind.System
        : AlvoActorKind.ApiKey;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions/Identity test/MMLib.Alvo.Abstractions.Tests/Identity` then `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*AlvoContextProvenanceTests'`
Expected: PASS (9 tests).

- [ ] **Step 5: Accept the public API growth**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*PublicApiApprovalTests'` — it FAILS and writes `PublicApi.MMLib.Alvo.Abstractions.received.txt`. Check the diff adds only `AlvoActorKind`, `AlvoProvenance` (+`None`) and `AlvoContext.ActorKind`/`Provenance` (spec §5 row 1), then `mv test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.received.txt test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` and re-run: PASS.

- [ ] **Step 6: ring0 and commit**

Run: `scripts/test-ring0` — Expected: green.

```bash
git add src/MMLib.Alvo.Abstractions/Identity/AlvoActorKind.cs src/MMLib.Alvo.Abstractions/Identity/AlvoProvenance.cs src/MMLib.Alvo.Abstractions/Identity/AlvoContext.cs test/MMLib.Alvo.Abstractions.Tests/Identity/AlvoContextProvenanceTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(abstractions): carry actor kind and provenance on AlvoContext"
```

---

## Task 2: Ambient flow, effective provenance and the depth limit

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Identity/AlvoFlow.cs`
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoChainDepthExceededException.cs`
- Create: `src/MMLib.Alvo.Abstractions/Data/AlvoChainDepthLimit.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Events/AlvoEventProvenance.cs`
- Test: `test/MMLib.Alvo.Tests/Events/EffectiveProvenanceTests.cs`
- Baseline: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Consumes: Task 1's `AlvoProvenance`, `AlvoContext.Provenance`.
- Produces: `internal static class AlvoFlow { internal static AlvoProvenance? Provenance; internal static IDisposable Enter(AlvoProvenance) }` (namespace `MMLib.Alvo`); `public static AlvoProvenance AlvoEventProvenance.Effective(AlvoContext)`; `public sealed class AlvoChainDepthExceededException : Exception` (3 standard ctors + `internal static At(int depth, int maxDepth)`); `internal sealed class AlvoChainDepthLimit { const int DefaultMaxDepth = 8; static Default; int MaxDepth; int EnsureWithin(AlvoContext) }` (namespace `MMLib.Alvo.Data`).

- [ ] **Step 1: Write the failing test**

`test/MMLib.Alvo.Tests/Events/EffectiveProvenanceTests.cs` (this project holds the `InternalsVisibleTo` grant `AlvoFlow` needs):

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Explicit and ambient provenance, merged by <see cref="AlvoEventProvenance.Effective"/> (spec §4.6, HX-01 acceptance 5).
/// </summary>
/// <remarks>
/// Ambient provenance has no production setter in HX-01 — the delivery and timer pumps arrive in HX-03/HX-13 — so these
/// facts set it through the Abstractions grant, which is exactly the seam those pumps will use.
/// </remarks>
public class EffectiveProvenanceTests
{
    private static readonly AlvoContext _caller = new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
    };

    [Fact]
    public void With_nothing_ambient_the_explicit_provenance_is_the_effective_one()
    {
        var context = _caller with { Provenance = new AlvoProvenance { Depth = 2, CorrelationId = "c", CausationId = "e" } };

        AlvoEventProvenance.Effective(context).ShouldBe(context.Provenance);
    }

    [Fact]
    public void An_ambient_depth_beats_a_shallower_explicit_one()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 9 });

        AlvoEventProvenance.Effective(_caller).Depth.ShouldBe(9, "a fresh context cannot lower the depth of the flow it runs in");
    }

    [Fact]
    public void An_explicit_depth_beats_a_shallower_ambient_one()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 2 });

        AlvoEventProvenance.Effective(_caller with { Provenance = new AlvoProvenance { Depth = 5 } }).Depth.ShouldBe(5);
    }

    [Fact]
    public void Explicit_ids_win_and_ambient_ids_fill_what_is_missing()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { CorrelationId = "ambient-c", CausationId = "ambient-e" });

        var effective = AlvoEventProvenance.Effective(_caller with { Provenance = new AlvoProvenance { CorrelationId = "mine" } });

        effective.CorrelationId.ShouldBe("mine");
        effective.CausationId.ShouldBe("ambient-e");
    }

    [Fact]
    public void The_trace_context_travels_as_a_pair_from_whichever_side_names_a_parent()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { TraceParent = "00-a-b-01", TraceState = "ambient=1" });

        var explicitParent = _caller with { Provenance = new AlvoProvenance { TraceParent = "00-c-d-01" } };
        var effective = AlvoEventProvenance.Effective(explicitParent);

        (effective.TraceParent, effective.TraceState).ShouldBe(("00-c-d-01", null));
        AlvoEventProvenance.Effective(_caller).TraceState.ShouldBe("ambient=1");
    }

    [Fact]
    public void Explicit_depth_zero_under_an_ambient_nine_is_refused()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 9 });

        var refused = Should.Throw<AlvoChainDepthExceededException>(() => AlvoChainDepthLimit.Default.EnsureWithin(_caller));

        refused.Message.ShouldContain("9");
        refused.Message.ShouldContain("8");
    }

    [Fact]
    public void Explicit_depth_zero_under_an_ambient_eight_is_accepted()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 8 });

        AlvoChainDepthLimit.Default.EnsureWithin(_caller).ShouldBe(8);
    }

    [Fact]
    public void Leaving_a_nested_flow_restores_the_outer_one()
    {
        using (AlvoFlow.Enter(new AlvoProvenance { Depth = 1 }))
        {
            using (AlvoFlow.Enter(new AlvoProvenance { Depth = 4 }))
            {
                AlvoFlow.Provenance!.Depth.ShouldBe(4);
            }

            AlvoFlow.Provenance!.Depth.ShouldBe(1);
        }

        AlvoFlow.Provenance.ShouldBeNull();
    }

    [Fact]
    public async Task The_ambient_flow_follows_an_await_into_its_continuation()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 3 });

        var seen = await Task.Run(() => AlvoFlow.Provenance?.Depth);

        seen.ShouldBe(3);
    }

    [Fact]
    public async Task A_flow_entered_inside_a_child_task_does_not_leak_back_to_its_caller()
    {
        await Task.Run(() => AlvoFlow.Enter(new AlvoProvenance { Depth = 7 }));

        AlvoFlow.Provenance.ShouldBeNull();
    }

    [Fact]
    public void A_negative_maximum_is_refused()
        => Should.Throw<ArgumentOutOfRangeException>(() => new AlvoChainDepthLimit(-1));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*EffectiveProvenanceTests'`
Expected: build FAIL — `AlvoFlow`, `Effective`, `AlvoChainDepthLimit`, `AlvoChainDepthExceededException` missing.

- [ ] **Step 3: Implement**

`src/MMLib.Alvo.Abstractions/Identity/AlvoFlow.cs`:

```csharp
namespace MMLib.Alvo;

/// <summary>
/// The provenance of the flow the current code runs in, carried by the .NET execution context, so a fresh
/// <see cref="AlvoContext"/> built inside a delivery or a timer firing still reports the chain it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, and set only by the framework's own pumps and write unit</b> — <c>MMLib.Alvo</c> and the EF driver.
/// Every assembly Abstractions grants <c>InternalsVisibleTo</c> can reach <see cref="Enter"/>, so an architecture fact
/// in each other granted product assembly's suite holds that it never does (spec 2026-10-14 §4.6, R-9). HX-01 ships no
/// production caller of <see cref="Enter"/>; HX-03 (deliveries) and HX-07 (the write unit) add them.
/// </para>
/// <para>
/// <b>The limit, stated:</b> it follows the execution context. Work a host hands to its own queue or background loop,
/// or runs with flow suppressed, leaves its reach.
/// </para>
/// </remarks>
internal static class AlvoFlow
{
    private static readonly AsyncLocal<AlvoProvenance?> _provenance = new();

    /// <summary>The ambient provenance, or <see langword="null"/> outside any framework flow.</summary>
    internal static AlvoProvenance? Provenance => _provenance.Value;

    /// <summary>Makes <paramref name="provenance"/> ambient until the returned scope is disposed.</summary>
    /// <param name="provenance">The provenance of the flow being entered.</param>
    /// <returns>A scope whose disposal restores whatever was ambient before.</returns>
    internal static IDisposable Enter(AlvoProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var previous = _provenance.Value;
        _provenance.Value = provenance;

        return new Restore(previous);
    }

    private sealed class Restore(AlvoProvenance? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _provenance.Value = previous;
        }
    }
}
```

`src/MMLib.Alvo.Abstractions/Data/AlvoChainDepthExceededException.cs`:

```csharp
namespace MMLib.Alvo.Data;

/// <summary>
/// A write or a publish was refused because it is deeper in one causation chain than the configured limit allows — a
/// hook, handler or timer is writing in a loop. <c>IAlvoData</c>'s seventh exception family.
/// </summary>
/// <remarks>
/// Rendered as <c>508 Loop Detected</c> with problem type <c>chain-depth-exceeded</c> (RFC 5842's registered meaning)
/// and marked <b>non-retryable</b>: the same request arrives at the same depth, so a retry can only fail again.
/// </remarks>
public sealed class AlvoChainDepthExceededException : Exception
{
    private const string DefaultMessage =
        "This operation is deeper in one causation chain than the limit allows, so it was refused before anything was "
        + "written. Something is writing in a loop; retrying cannot succeed.";

    /// <summary>Initializes a new instance with the default message.</summary>
    public AlvoChainDepthExceededException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/>.</summary>
    /// <param name="message">The message.</param>
    public AlvoChainDepthExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with <paramref name="message"/> and its cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AlvoChainDepthExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal of an operation at <paramref name="depth"/> under a limit of <paramref name="maxDepth"/>.</summary>
    internal static AlvoChainDepthExceededException At(int depth, int maxDepth) => new(
        $"This operation is {depth} hops deep in one causation chain, past the limit of {maxDepth}, so it was refused "
        + "before anything was written. A hook, handler or timer is writing in a loop; retrying cannot succeed. Break "
        + "the loop, or raise Alvo:Extensions:MaxDepth only if a chain this long is intended.");
}
```

`src/MMLib.Alvo.Abstractions/Data/AlvoChainDepthLimit.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Data;

/// <summary>
/// The one causation-chain limit every write and publish entry enforces, built by the core from
/// <c>Alvo:Extensions:MaxDepth</c> and resolved by every driver that implements <c>IAlvoData</c>.
/// </summary>
/// <remarks>
/// Internal and in Abstractions because the EF driver must enforce a limit the core configures and may not reference
/// the core's options type; the grant to the driver already exists for <c>AlvoFrameworkTables</c>.
/// </remarks>
internal sealed class AlvoChainDepthLimit
{
    /// <summary>The default limit (spec 2026-10-14 X3): a legitimate hook→write→hook→event→handler→write chain uses 5.</summary>
    internal const int DefaultMaxDepth = 8;

    /// <param name="maxDepth">The deepest chain still allowed.</param>
    internal AlvoChainDepthLimit(int maxDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        MaxDepth = maxDepth;
    }

    /// <summary>The limit at its default, for stores no configuration reaches (<c>InMemoryAlvoData</c>).</summary>
    internal static AlvoChainDepthLimit Default { get; } = new(DefaultMaxDepth);

    /// <summary>The deepest chain still allowed; a depth equal to it is accepted.</summary>
    internal int MaxDepth { get; }

    /// <summary>Refuses <paramref name="context"/> when its effective depth is greater than <see cref="MaxDepth"/>.</summary>
    /// <param name="context">The caller, whose explicit provenance is merged with the ambient flow.</param>
    /// <returns>The effective depth, which the operation then reports.</returns>
    /// <exception cref="AlvoChainDepthExceededException">The effective depth is greater than <see cref="MaxDepth"/>.</exception>
    internal int EnsureWithin(AlvoContext context)
    {
        var depth = AlvoEventProvenance.Effective(context).Depth;

        return depth <= MaxDepth ? depth : throw AlvoChainDepthExceededException.At(depth, MaxDepth);
    }
}
```

In `src/MMLib.Alvo.Abstractions/Events/AlvoEventProvenance.cs` add after `CorrelationIdOf`:

```csharp
    /// <summary>
    /// The provenance an operation reports: <paramref name="context"/>'s own, merged with the ambient flow's.
    /// </summary>
    /// <param name="context">The caller.</param>
    /// <returns>
    /// Depth is the larger of the two, so code can raise the depth it reports and never lower it; correlation and
    /// causation are the explicit ones when set and the ambient ones otherwise; the trace context travels as a pair from
    /// whichever side names a <c>traceparent</c>.
    /// </returns>
    public static AlvoProvenance Effective(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stated = context.Provenance;
        var ambient = AlvoFlow.Provenance ?? AlvoProvenance.None;
        var trace = stated.TraceParent is not null ? stated : ambient;

        return new AlvoProvenance
        {
            CorrelationId = stated.CorrelationId ?? ambient.CorrelationId,
            CausationId = stated.CausationId ?? ambient.CausationId,
            Depth = Math.Max(stated.Depth, ambient.Depth),
            TraceParent = trace.TraceParent,
            TraceState = trace.TraceState,
        };
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo.Abstractions test/MMLib.Alvo.Tests/Events/EffectiveProvenanceTests.cs` then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*EffectiveProvenanceTests'`
Expected: PASS (11 tests).

- [ ] **Step 5: Accept the public API growth** — as Task 1 Step 5; the diff adds only `AlvoChainDepthExceededException` (3 ctors) and `AlvoEventProvenance.Effective` (§5 rows 4, 5).

- [ ] **Step 6: ring0 and commit**

Run: `scripts/test-ring0`.

```bash
git add src/MMLib.Alvo.Abstractions/Identity/AlvoFlow.cs src/MMLib.Alvo.Abstractions/Data/AlvoChainDepthExceededException.cs src/MMLib.Alvo.Abstractions/Data/AlvoChainDepthLimit.cs src/MMLib.Alvo.Abstractions/Events/AlvoEventProvenance.cs test/MMLib.Alvo.Tests/Events/EffectiveProvenanceTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(abstractions): merge explicit and ambient provenance and bound the chain depth"
```

---

## Task 3: `AlvoExtensionOptions` (`Alvo:Extensions:MaxDepth`)

**Files:**
- Create: `src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs`
- Create: `src/MMLib.Alvo/Extensions/Setup.cs`
- Create: `src/MMLib.Alvo/Extensions/Internal/AlvoExtensionOptionsConfiguration.cs`
- Modify: `src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs` (call `services.AddAlvoExtensions();` immediately before `services.AddAlvoEvents();`, and add `using MMLib.Alvo.Extensions;`)
- Modify: `test/MMLib.Alvo.DocsGen.Tests/Configuration/OptionsCatalogTests.cs`
- Test: `test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt`

**Interfaces:**
- Consumes: `AlvoChainDepthLimit` (Task 2).
- Produces: `public sealed class AlvoExtensionOptions { const string SectionName = "Alvo:Extensions"; int MaxDepth = 8 }` (namespace `MMLib.Alvo.Extensions`); DI singleton `AlvoChainDepthLimit` built from it; `internal static IServiceCollection ExtensionsSetup.AddAlvoExtensions(this IServiceCollection)`.

- [ ] **Step 1: Write the failing test**

`test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary><c>Alvo:Extensions</c>: bound from configuration, refused at start when unusable, and the one source of the depth limit.</summary>
public class AlvoExtensionOptionsTests
{
    [Fact]
    public void The_default_limit_is_eight()
    {
        using var services = Container(new());

        services.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value.MaxDepth.ShouldBe(8);
        services.GetRequiredService<AlvoChainDepthLimit>().MaxDepth.ShouldBe(8);
    }

    [Fact]
    public void The_limit_is_bound_from_its_section()
    {
        using var services = Container(new() { ["Alvo:Extensions:MaxDepth"] = "3" });

        services.GetRequiredService<AlvoChainDepthLimit>().MaxDepth.ShouldBe(3);
    }

    [Fact]
    public void A_limit_below_one_is_refused_naming_the_key_and_its_variable()
    {
        using var services = Container(new() { ["Alvo:Extensions:MaxDepth"] = "0" });

        var refused = Should.Throw<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value);

        refused.Message.ShouldContain("Alvo:Extensions:MaxDepth");
        refused.Message.ShouldContain("Alvo__Extensions__MaxDepth");
    }

    private static ServiceProvider Container(Dictionary<string, string?> settings) => new ServiceCollection()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build())
        .AddAlvoExtensions()
        .BuildServiceProvider();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoExtensionOptionsTests'`
Expected: build FAIL — `AlvoExtensionOptions`, `AddAlvoExtensions` missing.

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Extensions/AlvoExtensionOptions.cs`:

```csharp
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Extensions;

/// <summary>
/// How host code extending Alvo is bounded (<c>Alvo:Extensions</c>), validated when the host starts.
/// </summary>
public sealed class AlvoExtensionOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Alvo:Extensions";

    /// <summary>
    /// Gets or sets the deepest causation chain a write or publish may sit in; deeper is refused with
    /// <c>508 chain-depth-exceeded</c>. One counter spans nested writes and event hops; default <c>8</c>, at least <c>1</c>.
    /// </summary>
    public int MaxDepth { get; set; } = AlvoChainDepthLimit.DefaultMaxDepth;
}
```

`src/MMLib.Alvo/Extensions/Internal/AlvoExtensionOptionsConfiguration.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>Binds <see cref="AlvoExtensionOptions"/> from its section and refuses an unusable value at start.</summary>
/// <param name="configuration">The host's configuration, or <see langword="null"/> in a host that has none.</param>
internal sealed class AlvoExtensionOptionsConfiguration(IConfiguration? configuration)
    : IConfigureOptions<AlvoExtensionOptions>, IValidateOptions<AlvoExtensionOptions>
{
    internal const string MaxDepthKey = $"{AlvoExtensionOptions.SectionName}:{nameof(AlvoExtensionOptions.MaxDepth)}";

    private const int MinimumMaxDepth = 1;

    public void Configure(AlvoExtensionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        configuration?.GetSection(AlvoExtensionOptions.SectionName).Bind(options);
    }

    public ValidateOptionsResult Validate(string? name, AlvoExtensionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.MaxDepth >= MinimumMaxDepth
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(MaxDepthRefusal(options.MaxDepth));
    }

    private static string MaxDepthRefusal(int configured) =>
        $"'{configured}' is not a usable {MaxDepthKey}. Set it (as an environment variable, "
        + $"{MaxDepthKey.Replace(":", "__", StringComparison.Ordinal)}) to at least {MinimumMaxDepth}: below that every "
        + "write an event handler or a timer makes would be refused as a loop.";
}
```

`src/MMLib.Alvo/Extensions/Setup.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Extensions;

/// <summary>Registers the validated <see cref="AlvoExtensionOptions"/> and the depth limit every store resolves.</summary>
internal static class ExtensionsSetup
{
    internal static IServiceCollection AddAlvoExtensions(this IServiceCollection services)
    {
        services.AddOptions<AlvoExtensionOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<AlvoExtensionOptions>, AlvoExtensionOptionsConfiguration>(Create));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AlvoExtensionOptions>, AlvoExtensionOptionsConfiguration>(Create));
        services.TryAddSingleton(DepthLimit);

        return services;

        static AlvoExtensionOptionsConfiguration Create(IServiceProvider provider) =>
            new(provider.GetService<IConfiguration>());

        static AlvoChainDepthLimit DepthLimit(IServiceProvider provider) =>
            new(provider.GetRequiredService<IOptions<AlvoExtensionOptions>>().Value.MaxDepth);
    }
}
```

In `test/MMLib.Alvo.DocsGen.Tests/Configuration/OptionsCatalogTests.cs`, `The_sections_are_exactly_what_discovery_finds` becomes:

```csharp
            "Alvo", "Alvo:Admin", "Alvo:Admin:Dashboard", "Alvo:Ai", "Alvo:Api", "Alvo:Auth", "Alvo:Events", "Alvo:Extensions",
            "Alvo:Management", "Alvo:Schema", "Alvo:Secrets",
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include src/MMLib.Alvo/Extensions src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs test/MMLib.Alvo.Tests/Extensions` then `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoExtensionOptionsTests'` and `dotnet test --project test/MMLib.Alvo.DocsGen.Tests --filter-class '*OptionsCatalogTests'`
Expected: PASS. If `Each_section_says_who_binds_it` needs an `Alvo:Extensions` row, the section's scope must read `OptionsCatalog.CoreScope` — add `_sections.Single(s => s.Name == "Alvo:Extensions").Scope.ShouldBe(OptionsCatalog.CoreScope);` beside the `Alvo:Events` line.

- [ ] **Step 5: Accept the public API growth** — run `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*PublicApiApprovalTests'`; the received `PublicApi.MMLib.Alvo.received.txt` adds only `MMLib.Alvo.Extensions.AlvoExtensionOptions` (§5 row 6); move it over the verified file and re-run.

- [ ] **Step 6: ring0 and commit**

```bash
git add src/MMLib.Alvo/Extensions src/MMLib.Alvo/AlvoServiceCollectionExtensions.cs test/MMLib.Alvo.Tests/Extensions/AlvoExtensionOptionsTests.cs test/MMLib.Alvo.DocsGen.Tests/Configuration/OptionsCatalogTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt
git commit -m "feat(core): add Alvo:Extensions options with the chain depth limit"
```

---

## Task 4: Envelope attributes, authcontext values, legacy reader

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Events/AlvoEventAttributes.cs` (attributes + `AlvoEventAuthType`)
- Modify: `src/MMLib.Alvo.Abstractions/Events/AlvoEvent.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Events/AlvoEventJson.cs`
- Modify: `src/MMLib.Alvo.Abstractions/Events/AlvoEventProvenance.cs`
- Modify (compile fixes for the removed `Anonymous`): `src/MMLib.Alvo/Events/Internal/AlvoEvents.cs:97`, `src/MMLib.Alvo.Testing/Events/OutboxStoreContractTests.cs:297`, `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/OutboxEventFactoryTests.cs:142`, `test/MMLib.Alvo.Tests/Events/AlvoTemplateTests.cs:186` — each `AlvoEventAuthType.Anonymous` → `AlvoEventAuthType.Unauthenticated`
- Modify: `test/MMLib.Alvo.Abstractions.Tests/Events/SampleEvents.cs` (`Sample()` carries every optional attribute, so `Extensions_are_flat_top_level_members_never_a_nested_object` — which asserts every name in `Extensions` is on the wire — keeps holding)
- Test: `test/MMLib.Alvo.Abstractions.Tests/Events/AlvoEventJsonTests.cs`, new `test/MMLib.Alvo.Abstractions.Tests/Events/AlvoEventAuthTypeTests.cs`, new `test/MMLib.Alvo.Tests/Events/AuthClaimsTests.cs`, `test/MMLib.Alvo.Tests/Events/CloudEventsConformanceTests.cs` (+ its `.verified.txt`)
- Baseline: Abstractions PublicApi

**Interfaces:**
- Produces: `AlvoEventAttributes.TenantId = "tenantid"`, `.AuthClaims = "authclaims"`, `.TraceParent = "traceparent"`, `.TraceState = "tracestate"`; `Extensions` in wire order `[partitionkey, payloadversion, chaindepth, authtype, authid, authclaims, tenantid, correlationid, causationid, traceparent, tracestate]`; `AlvoEventAuthType.{ApiKey="api_key", User="user", System="system", Unauthenticated="unauthenticated", Unknown="unknown"}`; `AlvoEvent.{AuthClaims, TenantId, TraceParent, TraceState}` (`string?`, init); internal `AlvoEventProvenance.AuthClaimsOf(AlvoContext) : string` and `RolesIn(string?) : IReadOnlyList<string>?`.

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Abstractions.Tests/Events/AlvoEventAuthTypeTests.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Abstractions.Tests.Events;

/// <summary><c>authtype</c> speaks the CloudEvents authcontext vocabulary (spec 2026-10-14 C9, §4.7).</summary>
public class AlvoEventAuthTypeTests
{
    [Theory]
    [InlineData(AlvoActorKind.Unknown, "unknown")]
    [InlineData(AlvoActorKind.Anonymous, "unauthenticated")]
    [InlineData(AlvoActorKind.System, "system")]
    [InlineData(AlvoActorKind.ApiKey, "api_key")]
    [InlineData(AlvoActorKind.User, "user")]
    public void Each_actor_kind_is_reported_as_its_authcontext_value(AlvoActorKind kind, string expected)
        => AlvoEventProvenance.AuthTypeOf(AlvoContext.Anonymous with { ActorKind = kind }).ShouldBe(expected);

    [Fact]
    public void A_host_built_context_with_an_ordinary_user_still_reports_an_api_key()
        => AlvoEventProvenance.AuthTypeOf(new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } })
            .ShouldBe(AlvoEventAuthType.ApiKey);

    [Fact]
    public void An_actor_kind_no_member_names_is_reported_as_unknown_rather_than_guessed()
        => AlvoEventProvenance.AuthTypeOf(AlvoContext.Anonymous with { ActorKind = (AlvoActorKind)42 })
            .ShouldBe(AlvoEventAuthType.Unknown);

    [Fact]
    public void The_values_are_the_authcontext_extensions_own_spelling()
        => new[] { AlvoEventAuthType.ApiKey, AlvoEventAuthType.User, AlvoEventAuthType.System, AlvoEventAuthType.Unauthenticated, AlvoEventAuthType.Unknown }
            .ShouldBe(["api_key", "user", "system", "unauthenticated", "unknown"]);
}
```

Append to `test/MMLib.Alvo.Abstractions.Tests/Events/AlvoEventJsonTests.cs` (inside the class):

```csharp
    [Fact]
    public void Every_new_provenance_attribute_round_trips_as_a_flat_member()
    {
        var stamped = Sample() with
        {
            AuthClaims = """{"roles":["agent","authenticated"]}""",
            TenantId = "8d0c9f2e-1111-4c4c-9a9a-000000000001",
            TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            TraceState = "alvo=1",
        };

        var written = AlvoEventJson.Write(stamped);

        using var document = JsonDocument.Parse(written);
        foreach (var attribute in new[] { "authclaims", "tenantid", "traceparent", "tracestate" })
        {
            document.RootElement.TryGetProperty(attribute, out _).ShouldBeTrue(attribute);
        }

        AlvoEventJson.Read(written).ShouldBe(stamped);
    }

    [Theory]
    [InlineData("apikey", "api_key")]
    [InlineData("anon", "unauthenticated")]
    [InlineData("system", "system")]
    public void A_legacy_auth_type_reads_back_normalised(string written, string expected)
    {
        var legacy = AlvoEventJson.Write(Sample()).Replace("\"authtype\":\"api_key\"", $"\"authtype\":\"{written}\"", StringComparison.Ordinal);

        AlvoEventJson.Read(legacy).AuthType.ShouldBe(expected);
    }

    [Fact]
    public void An_envelope_written_before_the_new_attributes_reads_them_as_absent()
    {
        var legacy = Sample() with { AuthClaims = null, TenantId = null, TraceParent = null, TraceState = null };
        var read = AlvoEventJson.Read(AlvoEventJson.Write(legacy));

        (read.AuthClaims, read.TenantId, read.TraceParent, read.TraceState).ShouldBe((null, null, null, null));
    }
```

In `test/MMLib.Alvo.Abstractions.Tests/Events/SampleEvents.cs`, extend `Sample()`'s initializer after `CausationId`:

```csharp
        AuthClaims = """{"roles":["agent","authenticated"]}""",
        TenantId = "8d0c9f2e-1111-4c4c-9a9a-000000000001",
        TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
        TraceState = "alvo=1",
```

`test/MMLib.Alvo.Tests/Events/AuthClaimsTests.cs`:

```csharp
using MMLib.Alvo.Events;

namespace MMLib.Alvo.Tests.Events;

/// <summary><c>authclaims</c>: the caller's roles as the authcontext extension's JSON string, and its tolerant reader.</summary>
public class AuthClaimsTests
{
    [Fact]
    public void The_claims_name_every_role_in_ordinal_order()
        => AlvoEventProvenance.AuthClaimsOf(new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated, Role.Admin },
        }).ShouldBe("""{"roles":["admin","authenticated"]}""");

    [Fact]
    public void The_reader_returns_what_the_writer_wrote()
        => AlvoEventProvenance.RolesIn(AlvoEventProvenance.AuthClaimsOf(AlvoContext.System(tenant: null)))
            .ShouldBe(["admin"]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"teams":["a"]}""")]
    [InlineData("""{"roles":"admin"}""")]
    [InlineData("""{"roles":["admin",1]}""")]
    public void Anything_but_a_list_of_role_names_reads_as_absent(string? claims)
        => AlvoEventProvenance.RolesIn(claims).ShouldBeNull();
}
```

In `test/MMLib.Alvo.Tests/Events/CloudEventsConformanceTests.cs`, extend `SampleEnvelope()`'s initializer with:

```csharp
        ChainDepth = 2,
        CausationId = "019fc77e-be7b-72e8-b7fd-ffd6f6306e3d",
        AuthClaims = """{"roles":["agent","authenticated"]}""",
        TenantId = "8d0c9f2e-1111-4c4c-9a9a-000000000001",
        TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
        TraceState = "alvo=1",
```

and add the fact:

```csharp
    [Fact]
    public void The_envelope_now_carries_tenant_claims_and_trace_context_as_extensions()
        => AlvoEventAttributes.Extensions.ShouldBe(
            ["partitionkey", "payloadversion", "chaindepth", "authtype", "authid", "authclaims", "tenantid", "correlationid", "causationid", "traceparent", "tracestate"]);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*AlvoEventAuthTypeTests' --filter-class '*AlvoEventJsonTests'`
Expected: build FAIL (`AlvoEventAuthType.User`, `AlvoEvent.AuthClaims` … missing).

- [ ] **Step 3: Implement**

`AlvoEventAttributes.cs` — add after `CausationId`:

```csharp
    /// <summary>The caller's authorization claims as a JSON string, <c>{"roles":[…]}</c>; the authcontext extension's <c>authclaims</c>.</summary>
    public const string AuthClaims = "authclaims";

    /// <summary>The tenant the write was made in; absent for a global row or a tenantless system write.</summary>
    public const string TenantId = "tenantid";

    /// <summary>The W3C trace context the change ran under; the registered Distributed Tracing extension.</summary>
    public const string TraceParent = "traceparent";

    /// <summary>The vendor trace state that accompanies <see cref="TraceParent"/>.</summary>
    public const string TraceState = "tracestate";
```

and change `Extensions`:

```csharp
    public static IReadOnlyList<string> Extensions { get; } =
    [
        PartitionKey, PayloadVersion, ChainDepth, AuthType, AuthId, AuthClaims, TenantId, CorrelationId, CausationId,
        TraceParent, TraceState,
    ];
```

Replace the whole `AlvoEventAuthType` class:

```csharp
/// <summary>
/// The values <see cref="AlvoEvent.AuthType"/> takes — the CloudEvents authcontext extension's <c>authtype</c>
/// vocabulary, derived from <see cref="AlvoContext.ActorKind"/>.
/// </summary>
/// <remarks>
/// Authentication, never authorization: a role is not a value here. Envelopes written before HX-01 used
/// <c>apikey</c> and <c>anon</c>; <see cref="AlvoEventJson.Read"/> normalises them to <see cref="ApiKey"/> and
/// <see cref="Unauthenticated"/>.
/// </remarks>
public static class AlvoEventAuthType
{
    /// <summary>The caller presented an API key.</summary>
    public const string ApiKey = "api_key";

    /// <summary>A signed-in person made the change.</summary>
    public const string User = "user";

    /// <summary>Alvo itself made the change, running as the system rather than as a caller.</summary>
    public const string System = "system";

    /// <summary>The caller presented no credential at all.</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>Nothing recorded how the caller authenticated.</summary>
    public const string Unknown = "unknown";
}
```

`AlvoEvent.cs` — after `CausationId` add (and update the `AuthType` summary to "one of <see cref="AlvoEventAuthType"/>'s five values"; update `ChainDepth`/`CausationId` remarks to say they come from `AlvoEventProvenance.Effective`):

```csharp
    /// <summary>Gets the caller's authorization claims, <c>{"roles":[…]}</c>, or <see langword="null"/> on an envelope written before HX-01.</summary>
    /// <remarks>
    /// <b>Provenance.</b> The authcontext extension's <c>authclaims</c> (post-1.0.2). It lets an after-hook condition
    /// read <c>@user.roles</c> as the caller held them <em>at the event</em>. Stripped from the default webhook body.
    /// </remarks>
    public string? AuthClaims { get; init; }

    /// <summary>Gets the tenant the write was made in, or <see langword="null"/> for a global row or a tenantless system write.</summary>
    /// <remarks><b>Provenance.</b> Alvo's own extension, legal by the naming rule.</remarks>
    public string? TenantId { get; init; }

    /// <summary>Gets the W3C <c>traceparent</c> the change ran under, when one was ambient.</summary>
    /// <remarks><b>Provenance.</b> The Distributed Tracing extension, registered in v1.0.2.</remarks>
    public string? TraceParent { get; init; }

    /// <summary>Gets the W3C <c>tracestate</c> that accompanies <see cref="TraceParent"/>.</summary>
    public string? TraceState { get; init; }
```

`AlvoEventJson.cs` — `WriteExtensions` becomes:

```csharp
    private static void WriteExtensions(Utf8JsonWriter writer, AlvoEvent @event)
    {
        writer.WriteString(AlvoEventAttributes.PartitionKey, @event.PartitionKey);
        writer.WriteNumber(AlvoEventAttributes.PayloadVersion, @event.PayloadVersion);
        writer.WriteNumber(AlvoEventAttributes.ChainDepth, @event.ChainDepth);
        writer.WriteString(AlvoEventAttributes.AuthType, @event.AuthType);
        WriteOptional(writer, AlvoEventAttributes.AuthId, @event.AuthId);
        WriteOptional(writer, AlvoEventAttributes.AuthClaims, @event.AuthClaims);
        WriteOptional(writer, AlvoEventAttributes.TenantId, @event.TenantId);
        writer.WriteString(AlvoEventAttributes.CorrelationId, @event.CorrelationId);
        WriteOptional(writer, AlvoEventAttributes.CausationId, @event.CausationId);
        WriteOptional(writer, AlvoEventAttributes.TraceParent, @event.TraceParent);
        WriteOptional(writer, AlvoEventAttributes.TraceState, @event.TraceState);
    }
```

In `ReadEnvelope` replace the `AuthType =` line and add the four members:

```csharp
        AuthType = NormalisedAuthType(RequiredString(root, AlvoEventAttributes.AuthType)),
        …
        AuthClaims = OptionalString(root, AlvoEventAttributes.AuthClaims),
        TenantId = OptionalString(root, AlvoEventAttributes.TenantId),
        TraceParent = OptionalString(root, AlvoEventAttributes.TraceParent),
        TraceState = OptionalString(root, AlvoEventAttributes.TraceState),
```

and add:

```csharp
    private const string LegacyApiKey = "apikey";
    private const string LegacyAnonymous = "anon";

    /// <summary>
    /// The current spelling of an <c>authtype</c> an older build wrote, so a row already in the outbox reads as one
    /// written today; the row itself is never rewritten.
    /// </summary>
    private static string NormalisedAuthType(string written) => written switch
    {
        LegacyApiKey => AlvoEventAuthType.ApiKey,
        LegacyAnonymous => AlvoEventAuthType.Unauthenticated,
        _ => written,
    };
```

`AlvoEventProvenance.cs` — replace `AuthTypeOf`'s body and drop the now-unused `_anonymousUser`/`_systemUser` fields (and their `<inheritdoc>` remarks); update the class remarks' "carries no role list at all" sentence to "carries the role set only as `authclaims`, a snapshot for after-hook conditions":

```csharp
    public static string AuthTypeOf(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.ActorKind switch
        {
            AlvoActorKind.Anonymous => AlvoEventAuthType.Unauthenticated,
            AlvoActorKind.System => AlvoEventAuthType.System,
            AlvoActorKind.ApiKey => AlvoEventAuthType.ApiKey,
            AlvoActorKind.User => AlvoEventAuthType.User,
            _ => AlvoEventAuthType.Unknown,
        };
    }
```

`AuthIdOf` keeps its behaviour; write it against `default` instead of the removed field: `return context.User == default ? null : context.User.Value.ToString();`. Add (with `using System.Buffers; using System.Text; using System.Text.Json;`):

```csharp
    private const string RolesClaim = "roles";

    /// <summary>The caller's roles as the authcontext <c>authclaims</c> JSON string, in ordinal order.</summary>
    internal static string AuthClaimsOf(AlvoContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray(RolesClaim);
            foreach (var role in context.Roles.Select(role => role.Name).Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(role);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The role names an <c>authclaims</c> string carries, or <see langword="null"/> when it carries none readably.</summary>
    /// <remarks>Tolerant on purpose: a dispatcher must treat a foreign or damaged value as absent — fail-closed — never throw.</remarks>
    internal static IReadOnlyList<string>? RolesIn(string? authClaims)
    {
        if (string.IsNullOrWhiteSpace(authClaims))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(authClaims);
            return RoleNames(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string>? RoleNames(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(RolesClaim, out var roles)
        && roles.ValueKind == JsonValueKind.Array
        && roles.EnumerateArray().All(role => role.ValueKind == JsonValueKind.String)
            ? [.. roles.EnumerateArray().Select(role => role.GetString()!)]
            : null;
```

Apply the four `Anonymous` → `Unauthenticated` compile fixes listed under **Files**.

- [ ] **Step 4: Run tests, accept snapshots**

Run: `dotnet format whitespace MMLib.Alvo.slnx --include <every .cs file touched>`; then
`dotnet test --project test/MMLib.Alvo.Abstractions.Tests --filter-class '*AlvoEventAuthTypeTests' --filter-class '*AlvoEventJsonTests' --filter-class '*PublicApiApprovalTests'`,
`dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AuthClaimsTests' --filter-class '*CloudEventsConformanceTests'`.
Expected: all PASS except `One_real_envelope_is_pinned_verbatim` and `Public_api_has_not_changed`. Inspect `test/MMLib.Alvo.Tests/CloudEventsConformanceTests.One_real_envelope_is_pinned_verbatim.received.txt`: `authtype` is `api_key`, and `authclaims`, `tenantid`, `causationid`, `traceparent`, `tracestate` appear flat at top level; move it over the `.verified.txt` (LF). The Abstractions API diff must add only the four attributes, the four `AlvoEvent` members, the renamed/added auth-type consts and drop `Anonymous` (§5 rows 2, 3); accept it. Dispatch `alvo-snapshot-judge` for both baselines. Re-run: PASS (acceptance criterion 1 and 2a).

- [ ] **Step 5: ring0 and commit**

Run: `scripts/test-ring0`.

```bash
git add src/MMLib.Alvo.Abstractions/Events src/MMLib.Alvo/Events/Internal/AlvoEvents.cs src/MMLib.Alvo.Testing/Events/OutboxStoreContractTests.cs test/MMLib.Alvo.Abstractions.Tests/Events test/MMLib.Alvo.Tests/Events/AuthClaimsTests.cs test/MMLib.Alvo.Tests/Events/CloudEventsConformanceTests.cs test/MMLib.Alvo.Tests/CloudEventsConformanceTests.One_real_envelope_is_pinned_verbatim.verified.txt test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/OutboxEventFactoryTests.cs test/MMLib.Alvo.Tests/Events/AlvoTemplateTests.cs test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(events)!: speak authcontext authtype values and add tenant, claims and trace attributes"
```

---

## Task 5: Resolvers state their actor kind

**Files:**
- Modify: `src/MMLib.Alvo/Auth/Internal/ApiKeyContextResolver.cs` (`BuildPrincipal`: add `ActorKind = AlvoActorKind.ApiKey,` to the `AlvoContext` initializer)
- Modify: `src/MMLib.Alvo.Identity/Internal/AlvoIdentityContextResolver.cs` (`Principal`: add `ActorKind = AlvoActorKind.User,`)
- Test: `test/MMLib.Alvo.Tests/Auth/ApiKeyContextResolverTests.cs`, `test/MMLib.Alvo.Identity.Tests/AlvoIdentityContextResolverTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `AlvoIdentityContextResolverTests` (uses its existing `Resolver`/`User` helpers):

```csharp
    [Fact]
    public async Task A_signed_in_person_is_reported_as_a_user_on_every_event()
    {
        var resolver = Resolver(stored: User(_eva), declared: RoleCatalog.Create(["editor"]));

        var context = (await resolver.ResolveAsync(_eva.ToString(), null, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Context;

        context.ActorKind.ShouldBe(AlvoActorKind.User);
        MMLib.Alvo.Events.AlvoEventProvenance.AuthTypeOf(context).ShouldBe("user");
    }
```

Append to `ApiKeyContextResolverTests`:

```csharp
    [Fact]
    public async Task A_key_is_reported_as_an_api_key_on_every_event()
    {
        var context = (await Resolver().ResolveAsync(PresentedKey, requestedTenant: null, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Context;

        context.ActorKind.ShouldBe(AlvoActorKind.ApiKey);
        MMLib.Alvo.Events.AlvoEventProvenance.AuthTypeOf(context).ShouldBe("api_key");
        (context with { User = AlvoContext.System(tenant: null).User }).ActorKind.ShouldBe(
            AlvoActorKind.ApiKey, "only an explicitly set kind survives a change of user; a derived one would read System");
    }
```

- [ ] **Step 2: Run to verify the Identity fact fails**

Run: `dotnet test --project test/MMLib.Alvo.Identity.Tests --filter-class '*AlvoIdentityContextResolverTests'`
Run also `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*ApiKeyContextResolverTests'`.
Expected: both new facts FAIL — the Identity context derives `ApiKey` (expected `User`), and the API-key context, carrying no explicit kind, re-derives `System` once its user is swapped.

- [ ] **Step 3: Implement** the two one-line initializer additions above.

- [ ] **Step 4: Run** both classes (`--filter-class '*ApiKeyContextResolverTests'` on `test/MMLib.Alvo.Tests`): PASS (acceptance criterion 2b, 2c). `dotnet format whitespace …` on the four files.

- [ ] **Step 5: ring0 and commit** — `git commit -m "feat(auth): resolvers state the caller's actor kind"` with the four files staged.

**[slice end]** Run `scripts/test-ring1`.

---

## Task 6: `events.exclude` — schema, model, mapper, validator, four schema test types

**Files:**
- Modify: `schema/project.schema.json` (`$defs/entity/properties`, after `hooks`)
- Modify: `src/MMLib.Alvo.Abstractions/Descriptor/EntityDescriptor.cs` (add `Events`, new record `EntityEvents`)
- Modify: `src/MMLib.Alvo.Abstractions/Schema/EntitySchema.cs` (add `EventImageExclusions`)
- Modify: `src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs` (`MapEntity` initializer)
- Modify: `src/MMLib.Alvo/Descriptor/Internal/DescriptorValidator.cs` (`EntitySemanticErrors`)
- Modify: `examples/complex-crm/crm.alvo.json` (`deals`: `"events": { "exclude": ["commission_note"] }`)
- Create: `examples/_negative/events-exclude-typo.json`, `examples/_negative/events-exclude-duplicate.json`; Modify `examples/_negative/expectations.json`
- Modify: `test/MMLib.Alvo.Schema.Tests/DescriptorRoundTripTests.cs`; snapshots `canonical-complex-crm.verified.txt`, `negative-error-output.verified.txt`, and `test/MMLib.Alvo.Tests/DescriptorToSchemaMapperTests.Complex_crm_without_its_unhonoured_features_maps_to_a_stable_model.verified.txt` (`ComplexCrmWithout` does not strip `events`, so `deals` gains `EventImageExclusions`)
- Test: `test/MMLib.Alvo.Tests/Descriptor/EventImageExclusionTests.cs`
- Baseline: Abstractions PublicApi

**Interfaces:**
- Produces: `EntityDescriptor.Events : EntityEvents?`; `public sealed record EntityEvents { IReadOnlyList<string>? Exclude }`; `EntitySchema.EventImageExclusions : IReadOnlyList<string>` (init, default `[]`, null → `[]`).

- [ ] **Step 1: Write the failing tests**

`test/MMLib.Alvo.Tests/Descriptor/EventImageExclusionTests.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary><c>entities.&lt;e&gt;.events.exclude</c>: mapped onto the applied schema and refused when it names what it cannot.</summary>
public class EventImageExclusionTests
{
    private readonly DescriptorValidator _validator = new();

    [Fact]
    public void The_exclusions_reach_the_applied_schema()
        => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(Notes("""["salary"]""")))
            .Entities.Single(entity => entity.Name == "notes").EventImageExclusions.ShouldBe(["salary"]);

    [Fact]
    public void An_entity_that_excludes_nothing_carries_an_empty_list()
        => DescriptorToSchemaMapper.Map(AlvoDescriptor.Parse(Notes(exclude: null)))
            .Entities.Single(entity => entity.Name == "notes").EventImageExclusions.ShouldBeEmpty();

    [Fact]
    public void Excluding_a_declared_field_applies()
        => _validator.Validate(Notes("""["salary"]""")).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("id")]
    [InlineData("tenant_id")]
    public void The_columns_that_key_an_event_cannot_be_excluded(string column)
    {
        var refusal = _validator.Validate(Notes($"""["{column}"]""")).Errors.ShouldHaveSingleItem();

        refusal.Path.ShouldBe("/entities/notes/events/exclude/0");
        refusal.Message.ShouldContain(column);
        refusal.FixSuggestion.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void An_ordinary_field_named_like_an_audit_column_can_be_excluded_on_an_entity_without_audit()
        => _validator.Validate(Notes("""["created_at"]""").Replace(
                "\"salary\": { \"type\": \"integer\" }", "\"salary\": { \"type\": \"integer\" }, \"created_at\": { \"type\": \"datetime\" }",
                StringComparison.Ordinal))
            .IsValid.ShouldBeTrue("created_at is managed only on an entity with audit");

    [Fact]
    public void A_name_no_field_carries_is_refused_at_its_own_pointer()
    {
        var refusal = _validator.Validate(Notes("""["salary", "nope"]""")).Errors.ShouldHaveSingleItem();

        refusal.Path.ShouldBe("/entities/notes/events/exclude/1");
        refusal.Message.ShouldContain("nope");
    }

    private static string Notes(string? exclude) => $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "exclusions",
          "entities": {
            "notes": {
              "fields": { "title": { "type": "string" }, "salary": { "type": "integer" } }{{(exclude is null ? string.Empty : $", \"events\": {{ \"exclude\": {exclude} }}")}}
            }
          }
        }
        """;
}
```

In `DescriptorRoundTripTests.Complex_crm_exercises_the_full_surface_and_preserves_it`, add `AssertEventExclusionsSurvive(descriptor);` and:

```csharp
    private static void AssertEventExclusionsSurvive(AlvoDescriptor descriptor)
        => descriptor.Entities["deals"].Events.ShouldNotBeNull().Exclude.ShouldBe(["commission_note"]);
```

`examples/_negative/events-exclude-typo.json`:

```json
{
  "apiVersion": "alvo.dev/v1",
  "name": "events-exclude-typo",
  "entities": {
    "notes": {
      "fields": { "title": { "type": "string" } },
      "events": { "exclud": ["title"] }
    }
  }
}
```

`examples/_negative/events-exclude-duplicate.json`:

```json
{
  "apiVersion": "alvo.dev/v1",
  "name": "events-exclude-duplicate",
  "entities": {
    "notes": {
      "fields": { "title": { "type": "string" } },
      "events": { "exclude": ["title", "title"] }
    }
  }
}
```

Add to `examples/_negative/expectations.json`:

```json
  "events-exclude-duplicate.json": { "pointer": "#/entities/notes/events/exclude" },
  "events-exclude-typo.json": { "pointer": "#/entities/notes/events/exclud" },
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*EventImageExclusionTests'` — build FAIL (`EventImageExclusions` missing). `dotnet test --project test/MMLib.Alvo.Schema.Tests` — the two negatives FAIL (the schema still accepts `events`? no — `additionalProperties: false` on `entity` already rejects `events`, so `events-exclude-duplicate.json` fails at the wrong pointer; the positive `complex-crm` fails once Step 3's example edit lands).

- [ ] **Step 3: Implement**

`schema/project.schema.json`, inside `$defs.entity.properties` after `"hooks": {…}`:

```json
        "events": {
          "type": "object",
          "additionalProperties": false,
          "description": "What this entity's events carry. Every write appends one CloudEvents 1.0.2 event with the row after (`data.record`) and before (`data.old_record`) the change. `exclude` leaves the named fields out of both images — personal data, large columns — while `data.changed` still names them. `id`, `tenant_id` and the framework-managed columns key the event and cannot be excluded. Apply refuses an after-hook condition or template that reads an excluded field.",
          "properties": {
            "exclude": {
              "type": "array",
              "uniqueItems": true,
              "items": { "type": "string", "pattern": "^[a-z][a-z0-9_]{0,62}$" },
              "description": "Declared fields to leave out of both row images of every event this entity emits."
            }
          }
        },
```

`EntityDescriptor.cs` — after `Hooks`:

```csharp
    /// <summary>What the entity's events carry; <see langword="null"/> when it declares nothing.</summary>
    public EntityEvents? Events { get; init; }
```

and after `EntityIndex`:

```csharp
/// <summary>What an entity's events carry (schema <c>entity.events</c>).</summary>
public sealed record EntityEvents
{
    /// <summary>Declared fields left out of both row images of every event the entity emits; <c>changed</c> still names them.</summary>
    public IReadOnlyList<string>? Exclude { get; init; }
}
```

`EntitySchema.cs` — add a backing field and the member after `Indexes`:

```csharp
    private readonly IReadOnlyList<string> _eventImageExclusions = [];

    /// <summary>
    /// Gets the fields left out of <c>data.record</c> and <c>data.old_record</c> of every event this entity emits
    /// (descriptor <c>events.exclude</c>); <c>data.changed</c> still names them. Empty unless declared.
    /// </summary>
    public IReadOnlyList<string> EventImageExclusions
    {
        get => _eventImageExclusions;
        init => _eventImageExclusions = value ?? [];
    }
```

`DescriptorToSchemaMapper.MapEntity` initializer — after `Indexes = indexes,` add `EventImageExclusions = e.Events?.Exclude ?? [],`.

`DescriptorValidator.EntitySemanticErrors` — after `var managed = ManagedColumnsOf(entity.Value, tenancyEnabled);` add:

```csharp
        foreach (var error in EventExclusionErrors(entity, fields, managed))
        {
            yield return error;
        }
```

and the helpers:

```csharp
    private static IEnumerable<DescriptorValidationError> EventExclusionErrors(
        JsonProperty entity, JsonElement fields, IReadOnlySet<string> managed)
    {
        if (!entity.Value.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Object
            || !events.TryGetProperty("exclude", out var exclude) || exclude.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var index = 0;
        foreach (var name in exclude.EnumerateArray())
        {
            var path = $"/entities/{entity.Name}/events/exclude/{index++}";
            if (name.ValueKind == JsonValueKind.String && ExclusionRefusal(entity.Name, name.GetString()!, fields, managed, path) is { } refusal)
            {
                yield return refusal;
            }
        }
    }

    private static DescriptorValidationError? ExclusionRefusal(
        string entity, string name, JsonElement fields, IReadOnlySet<string> managed, string path) =>
        IsEventKey(name, managed) ? KeysTheEvent(path, name)
        : fields.TryGetProperty(name, out _) ? null
        : ExcludesNothing(path, entity, name);

    /// <remarks>
    /// The entity's <em>own</em> managed set (its traits), never the union: an entity without <c>audit</c> may declare an
    /// ordinary <c>created_at</c> field (see <see cref="ManagedColumnsOf"/>), and excluding that field is legitimate.
    /// </remarks>
    private static bool IsEventKey(string name, IReadOnlySet<string> managed) =>
        name == AlvoManagedColumns.Id || managed.Contains(name);

    private static DescriptorValidationError KeysTheEvent(string path, string name) => new(
        path,
        $"'{name}' is a framework-managed column that keys the event — its subject, partition key or audit trail — so it "
        + "cannot be left out of the event's images.",
        $"Remove '{name}' from events.exclude.",
        DescriptorValidationSeverity.Error);

    private static DescriptorValidationError ExcludesNothing(string path, string entity, string name) => new(
        path,
        $"'{name}' is not a field of '{entity}', so leaving it out of the event's images would exclude nothing.",
        $"Name a field '{entity}' declares, or remove the entry.",
        DescriptorValidationSeverity.Error);
```

(`managed` is the set `EntitySemanticErrors` already computes at `:551`. On a global entity `tenant_id` is not managed, so it falls through to `ExcludesNothing` at the same pointer — the theory below still holds, its message names the column either way.)

Edit `examples/complex-crm/crm.alvo.json`: in `entities.deals` add `"events": { "exclude": ["commission_note"] },` after `"hooks": {…}`.

- [ ] **Step 4: Run tests, accept snapshots**

Run: `dotnet format whitespace …` on the touched `.cs` files; `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*EventImageExclusionTests' --filter-class '*DescriptorValidatorTests' --filter-class '*DescriptorToSchemaMapperTests'`; `dotnet test --project test/MMLib.Alvo.Schema.Tests` (meta-validation, examples, round-trip, snapshots = the four schema test types).
Expected: all PASS except three snapshots — `Canonical_complex_crm`, `Negative_error_output` and `DescriptorToSchemaMapperTests.Complex_crm_without_its_unhonoured_features_maps_to_a_stable_model`. Inspect: the mapper snapshot gains `EventImageExclusions: [commission_note]` on `deals` only (Verify omits empty collections elsewhere); canonical gains only the `events` member; negative output gains the two new files with the expected pointers. If Corvus reports the duplicate at a different pointer than `#/entities/notes/events/exclude`, use the pointer the received file shows in `expectations.json` (it is the validator's own, and the fixture is rejected for the intended reason only if the pointer is inside `events`). Accept all three snapshots, dispatch `alvo-snapshot-judge`. Accept the Abstractions API growth (`EntityDescriptor.Events`, `EntityEvents`, `EntitySchema.EventImageExclusions`; `EntityDescriptor.Events`/`EntityEvents` are the §5 amendment Task 17 records). Run `dotnet test --project test/MMLib.Alvo.DocsGen.Tests` (schema reference pages) — PASS. Acceptance criterion 5c (the `id`/`tenant_id` half).

- [ ] **Step 5: ring0 and commit**

```bash
git add schema/project.schema.json src/MMLib.Alvo.Abstractions/Descriptor/EntityDescriptor.cs src/MMLib.Alvo.Abstractions/Schema/EntitySchema.cs src/MMLib.Alvo/Descriptor/DescriptorToSchemaMapper.cs src/MMLib.Alvo/Descriptor/Internal/DescriptorValidator.cs examples/complex-crm/crm.alvo.json examples/_negative test/MMLib.Alvo.Tests/Descriptor/EventImageExclusionTests.cs test/MMLib.Alvo.Tests/DescriptorToSchemaMapperTests.Complex_crm_without_its_unhonoured_features_maps_to_a_stable_model.verified.txt test/MMLib.Alvo.Schema.Tests test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt
git commit -m "feat(schema): declare events.exclude and map it onto the applied schema" \
  -m "Deviation from spec §4.7: exclude items use the field-name grammar ^[a-z][a-z0-9_]{0,62}\$, not \$defs/identifier, whose '-' no field can hold. Recorded in the spec amendment (Task 17)."
```

---

## Task 7: Stamp provenance on both emit paths; strip `authclaims` from webhook bodies

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Events/AlvoEnvelopeStamp.cs`
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OutboxEventFactory.cs`
- Modify: `src/MMLib.Alvo/Events/Internal/AlvoEvents.cs` (`Envelope`)
- Modify: `src/MMLib.Alvo/Events/Internal/EventActionExecutor.cs` (`BodyOf`)
- Test: `test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/OutboxEventFactoryTests.cs`, `test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs`, `test/MMLib.Alvo.Tests/Events/EventActionExecutorTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 4, 6.
- Produces: `internal sealed record AlvoEnvelopeStamp(string AuthType, string? AuthId, string? AuthClaims, string? TenantId, string CorrelationId, string? CausationId, int ChainDepth, string? TraceParent, string? TraceState)` with `internal static AlvoEnvelopeStamp Of(AlvoContext context, Guid eventId, TenantId? tenant)` and `internal AlvoEvent Onto(AlvoEvent @event)`.

- [ ] **Step 1: Write the failing tests**

Append to `OutboxEventFactoryTests` (update the `Callers` theory data: `AlvoEventAuthType.Unauthenticated` for anonymous and add `{ Caller with { ActorKind = AlvoActorKind.User }, AlvoEventAuthType.User }`):

```csharp
    [Fact]
    public void A_scoped_write_carries_the_callers_tenant_and_a_global_one_carries_none()
    {
        var tenant = TenantId.New();
        var caller = Caller with { Tenant = tenant };

        OutboxEventFactory.For(Vehicles with { Tenancy = TenancyMode.Scoped }, OutboxOperation.Created, caller, Now, PostImage, null)
            .TenantId.ShouldBe(tenant.ToString());
        OutboxEventFactory.For(Vehicles, OutboxOperation.Created, caller, Now, PostImage, null).TenantId.ShouldBeNull();
        OutboxEventFactory.For(Vehicles with { Tenancy = TenancyMode.Scoped }, OutboxOperation.Created, AlvoContext.System(tenant: null), Now, PostImage, null)
            .TenantId.ShouldBeNull("a tenantless system write has no tenant to report");
    }

    [Fact]
    public void Excluded_fields_leave_both_images_and_stay_named_in_changed()
    {
        var @event = OutboxEventFactory.For(
            Vehicles with { EventImageExclusions = ["color"] }, OutboxOperation.Updated, Caller, Now, PostImage, PreImage);

        @event.Data.Record!.Values.ShouldNotContainKey("color");
        @event.Data.OldRecord!.Values.ShouldNotContainKey("color");
        @event.Data.Record!["make"].ShouldBe("vw");
        @event.Data.Changed.ShouldContain("color");
    }

    [Fact]
    public void The_effective_provenance_is_stamped_on_the_event()
    {
        var caller = Caller with { Provenance = new AlvoProvenance { Depth = 3, CorrelationId = "flow-1", CausationId = "cause-1" } };

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, caller, Now, PostImage, null);

        (@event.ChainDepth, @event.CorrelationId, @event.CausationId).ShouldBe((3, "flow-1", "cause-1"));
    }

    [Fact]
    public void An_ambient_flow_deeper_than_the_caller_sets_the_events_depth()
    {
        using var _ = AlvoFlow.Enter(new AlvoProvenance { Depth = 4, CausationId = "event-7" });

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, Caller, Now, PostImage, null);

        (@event.ChainDepth, @event.CausationId).ShouldBe((4, "event-7"));
    }

    [Fact]
    public void The_callers_roles_travel_as_auth_claims()
        => OutboxEventFactory.For(Vehicles, OutboxOperation.Created, Caller, Now, PostImage, null)
            .AuthClaims.ShouldBe(MMLib.Alvo.Events.AlvoEventProvenance.AuthClaimsOf(Caller));

    [Fact]
    public void The_ambient_w3c_activity_is_the_events_trace_context()
    {
        using var activity = new System.Diagnostics.Activity("write").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start();
        activity.TraceStateString = "alvo=1";

        var @event = OutboxEventFactory.For(Vehicles, OutboxOperation.Created, Caller, Now, PostImage, null);

        (@event.TraceParent, @event.TraceState).ShouldBe((activity.Id, "alvo=1"));
        @event.CorrelationId.ShouldBe(activity.TraceId.ToString());
    }
```

Append to `AlvoEventsTests` (its private `RecordingOutboxStore` captures `customEvent.Envelope`):

```csharp
    [Fact]
    public async Task A_published_event_carries_the_callers_tenant_claims_and_provenance()
    {
        var store = new RecordingOutboxStore();
        var tenant = TenantId.New();
        var caller = Caller with { Tenant = tenant, Provenance = new AlvoProvenance { Depth = 2, CausationId = "cause" } };

        await Publisher(store).PublishAsync("orders.approved", "orders/1", null, caller, TestContext.Current.CancellationToken);

        var appended = store.Appended.ShouldHaveSingleItem();
        appended.TenantId.ShouldBe(tenant.ToString());
        appended.AuthClaims.ShouldBe(AlvoEventProvenance.AuthClaimsOf(caller));
        (appended.ChainDepth, appended.CausationId).ShouldBe((2, "cause"));
    }
```

Append to `EventActionExecutorTests` (same shape as `A_webhook_action_posts_the_canonical_envelope_when_no_payload_is_declared`):

```csharp
    [Fact]
    public async Task The_default_webhook_body_never_carries_the_callers_claims()
    {
        var receiver = new RecordingWebhookReceiver();
        var @event = SampleEvent(Record(("title", "Big deal"))) with { AuthClaims = """{"roles":["admin"]}""" };

        await Subject(receiver).ExecuteAsync(WebhookHook(), @event, Cancellation);

        var body = receiver.Bodies.ShouldHaveSingleItem();
        body.ShouldNotContain("authclaims");
        AlvoEventJson.Read(body).ShouldBe(@event with { AuthClaims = null });
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests --filter-class '*OutboxEventFactoryTests'` and `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AlvoEventsTests' --filter-class '*EventActionExecutorTests'`
Expected: the new facts FAIL (no tenant, images unchanged, depth 0, body still carries claims).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo.Abstractions/Events/AlvoEnvelopeStamp.cs`:

```csharp
using System.Diagnostics;

namespace MMLib.Alvo.Events;

/// <summary>
/// Every provenance attribute one envelope carries, derived from the caller once — the single place both emit paths
/// (the EF driver's data events, the core's custom events) turn an <see cref="AlvoContext"/> into envelope attributes.
/// </summary>
/// <remarks>
/// Internal: two copies of this derivation are how a system-made change comes to be reported differently on one path
/// than on the other, which is the reason <see cref="AlvoEventProvenance"/> exists at all.
/// </remarks>
internal sealed record AlvoEnvelopeStamp(
    string AuthType,
    string? AuthId,
    string? AuthClaims,
    string? TenantId,
    string CorrelationId,
    string? CausationId,
    int ChainDepth,
    string? TraceParent,
    string? TraceState)
{
    /// <summary>The stamp for one event.</summary>
    /// <param name="context">The caller the event is emitted for.</param>
    /// <param name="eventId">The event's own id, the correlation of last resort.</param>
    /// <param name="tenant">The tenant the event reports, or <see langword="null"/> for a global row or a tenantless write.</param>
    internal static AlvoEnvelopeStamp Of(AlvoContext context, Guid eventId, TenantId? tenant)
    {
        var provenance = AlvoEventProvenance.Effective(context);
        var activity = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current : null;
        var fromActivity = provenance.TraceParent is null && activity is not null;

        return new AlvoEnvelopeStamp(
            AlvoEventProvenance.AuthTypeOf(context),
            AlvoEventProvenance.AuthIdOf(context),
            AlvoEventProvenance.AuthClaimsOf(context),
            tenant?.ToString(),
            provenance.CorrelationId ?? AlvoEventProvenance.CorrelationIdOf(eventId),
            provenance.CausationId,
            provenance.Depth,
            fromActivity ? activity!.Id : provenance.TraceParent,
            fromActivity ? activity!.TraceStateString : provenance.TraceState);
    }

    /// <summary><paramref name="event"/> with every attribute of this stamp that is not a required member.</summary>
    internal AlvoEvent Onto(AlvoEvent @event) => @event with
    {
        AuthId = AuthId,
        AuthClaims = AuthClaims,
        TenantId = TenantId,
        CausationId = CausationId,
        ChainDepth = ChainDepth,
        TraceParent = TraceParent,
        TraceState = TraceState,
    };
}
```

`OutboxEventFactory.For` — replace the `return new AlvoEvent { … }` with:

```csharp
        var stamp = AlvoEnvelopeStamp.Of(context, id, TenantOf(entity, context));

        return stamp.Onto(new AlvoEvent
        {
            Id = id,
            Source = AlvoEvent.DefaultSource,
            Type = $"entity.{entity.Name}.{Suffix(operation)}",
            Time = now,
            Subject = $"{entity.Name}/{rowId}",
            PartitionKey = PartitionKeyFor(entity.Name, rowId),
            AuthType = stamp.AuthType,
            CorrelationId = stamp.CorrelationId,
            Data = new AlvoEventData
            {
                Record = Without(postImage, entity.EventImageExclusions),
                OldRecord = Without(preImage, entity.EventImageExclusions),
                Changed = ChangedFields(postImage, preImage),
            },
        });
```

and add:

```csharp
    /// <summary>The tenant a data event reports: the caller's, on a tenant-scoped entity only.</summary>
    private static TenantId? TenantOf(EntitySchema entity, AlvoContext context) =>
        entity.Tenancy == TenancyMode.Scoped ? context.Tenant : null;

    /// <summary><paramref name="image"/> without the fields the entity excludes from its events (<c>events.exclude</c>).</summary>
    private static AlvoRecord? Without(AlvoRecord? image, IReadOnlyList<string> excluded) =>
        image is null || excluded.Count == 0
            ? image
            : new AlvoRecord(image.Values
                .Where(field => !excluded.Contains(field.Key, StringComparer.Ordinal))
                .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal));
```

Update `For`'s `<remarks>` about unmasked images: "minus the entity's `events.exclude` fields; `changed` is computed from the full images, so it still names them".

`AlvoEvents.Envelope` — same shape:

```csharp
        var now = clock.GetUtcNow();
        var id = AlvoEventId.Create(now);
        var stamp = AlvoEnvelopeStamp.Of(context, id, context.Tenant);

        return stamp.Onto(new AlvoEvent
        {
            Id = id,
            Source = AlvoEvent.DefaultSource,
            Type = type,
            Time = now,
            Subject = subject,
            PartitionKey = PartitionKeyFor(subject),
            AuthType = stamp.AuthType,
            CorrelationId = stamp.CorrelationId,
            Data = new AlvoEventData { Record = data is null ? null : new AlvoRecord(data) },
        });
```

`EventActionExecutor.BodyOf` — first arm becomes `AlvoEventJson.Write(WithoutClaims(@event))` and add:

```csharp
    /// <summary>
    /// The envelope a receiver gets by default: everything the outbox stored except <c>authclaims</c>, which exists for
    /// after-hook conditions and is the caller's authorization — not something to post to a third party.
    /// </summary>
    private static AlvoEvent WithoutClaims(AlvoEvent @event) => @event with { AuthClaims = null };
```

- [ ] **Step 4: Run tests**

`dotnet format whitespace …`; re-run Step 2's commands. Expected: PASS.

- [ ] **Step 5: ring0 and commit**

```bash
git add src/MMLib.Alvo.Abstractions/Events/AlvoEnvelopeStamp.cs src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/OutboxEventFactory.cs src/MMLib.Alvo/Events/Internal/AlvoEvents.cs src/MMLib.Alvo/Events/Internal/EventActionExecutor.cs test/MMLib.Alvo.Data.EntityFrameworkCore.Tests/OutboxEventFactoryTests.cs test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs test/MMLib.Alvo.Tests/Events/EventActionExecutorTests.cs
git commit -m "feat(events): stamp tenant, claims, trace and effective provenance on every envelope"
```

---

## Task 8: Outbox contract on both engines — tenant, exclusions, provenance

**Files:**
- Modify: `src/MMLib.Alvo.Testing/Data/AlvoDataOutboxTests.cs`
- Baseline: `test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt`
- Runs through: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoDataOutboxTests.cs` (ring0) and `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoDataOutboxTests.cs` (ring2)

- [ ] **Step 1: Write the failing tests**

In `AlvoDataOutboxTests`: add an entity `TenantNotes = "tenant_notes"` (scoped) to `Descriptor` and `Schema`, an `internal_memo` field to `vehicles` that the schema excludes, and the facts.

Descriptor entries (inside `Entities`):

```csharp
            [TenantNotes] = new()
            {
                Tenancy = EntityTenancy.Scoped,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String },
                },
                Rules = new AccessRules
                {
                    List = AuthenticatedOnly, Get = AuthenticatedOnly, Create = AuthenticatedOnly,
                    Update = AuthenticatedOnly, Delete = AuthenticatedOnly,
                },
            },
```

and `["internal_memo"] = new() { Type = DescField.String },` in `vehicles`' fields. Schema: in `vehicles` add `new FieldSchema { Name = "internal_memo", Type = SchemaField.String, Nullable = true },` before the managed columns and `EventImageExclusions = ["internal_memo"],` on the entity; add:

```csharp
        new EntitySchema
        {
            Name = TenantNotes,
            Tenancy = TenancyMode.Scoped,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true },
                new FieldSchema { Name = AlvoManagedColumns.TenantId, Type = SchemaField.Uuid, Required = true, Indexed = true },
            ],
        },
```

Facts and helpers:

```csharp
    [Fact]
    public async Task A_scoped_write_reports_its_tenant_and_a_global_write_reports_none()
    {
        var world = await VehiclesWorldAsync();

        await world.Data.CreateAsync(
            TenantNotes, new Dictionary<string, object?> { ["title"] = "t", ["tenant_id"] = SuiteTenant.Value }, TenantCaller, cancellationToken: Ct);
        await CreateVehicleAsync(world, make: "vw");

        var events = await world.EventsAsync();
        events.Single(queued => queued.Type == $"entity.{TenantNotes}.created").TenantId.ShouldBe(SuiteTenant.ToString());
        events.Single(queued => queued.Type == Created).TenantId.ShouldBeNull();
    }

    [Fact]
    public async Task An_excluded_field_is_in_neither_image_and_still_named_as_changed()
    {
        var world = await VehiclesWorldAsync();
        var created = await world.Data.CreateAsync(
            Vehicles, new Dictionary<string, object?>(Payload("vw", vin: null)) { ["internal_memo"] = "before" }, Caller, cancellationToken: Ct);

        await world.Data.UpdateAsync(Vehicles, IdOf(created), Patch(("internal_memo", "after")), Caller, cancellationToken: Ct);

        var updated = (await world.EventsAsync())[^1];
        updated.Data.Record!.Values.ShouldNotContainKey("internal_memo");
        updated.Data.OldRecord!.Values.ShouldNotContainKey("internal_memo");
        updated.Data.Changed.ShouldContain("internal_memo");
    }

    [Fact]
    public async Task A_write_reports_the_chain_it_was_made_in()
    {
        var world = await VehiclesWorldAsync();
        var inChain = Caller with { Provenance = new AlvoProvenance { Depth = 3, CorrelationId = "flow-1", CausationId = "cause-1" } };

        await world.Data.CreateAsync(Vehicles, Payload("vw", vin: null), inChain, cancellationToken: Ct);

        var queued = (await world.EventsAsync()).ShouldHaveSingleItem();
        (queued.ChainDepth, queued.CorrelationId, queued.CausationId).ShouldBe((3, "flow-1", "cause-1"));
    }

    private const string TenantNotes = "tenant_notes";

    private static TenantId SuiteTenant { get; } = new(Guid.Parse("cccccccc-0000-0000-0000-000000000001"));

    private static AlvoContext TenantCaller => Caller with { Tenant = SuiteTenant };
```

`TenantCaller` is expression-bodied because `Caller` is a static auto-property declared further down the file (`:469`); an initializer above it would read `null`. Place `SuiteTenant` and `TenantCaller` below `Caller`.

In `Every_event_carries_the_caller_the_write_was_performed_as` add `queued.AuthClaims.ShouldBe("""{"roles":["authenticated"]}""");`.

- [ ] **Step 2: Run on SQLite to see them fail first, then pass**

The implementation already landed in Task 7, so run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteAlvoDataOutboxTests'` — Expected: PASS (if any fails, the EF write path does not hand the applied `EntitySchema` to `OutboxEventFactory.For` at `EfAlvoData.cs:2207` — fix there). To prove the facts bite, temporarily revert `TenantOf` to `null` in `OutboxEventFactory`, watch `A_scoped_write_reports_its_tenant…` FAIL, restore.

- [ ] **Step 3: PostgreSQL leg**

Run: `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlAlvoDataOutboxTests'` (Docker). Expected: PASS — acceptance criterion 3 on both engines.

- [ ] **Step 4: Accept the Testing API growth** (`dotnet test --project test/MMLib.Alvo.Tests --filter-class '*TestingLibraryPublicApiTests'`; three new public fact methods only), ring0, commit:

```bash
git add src/MMLib.Alvo.Testing/Data/AlvoDataOutboxTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "test(contract): outbox events carry tenant, exclusions and provenance on every engine"
```

---

## Task 9: Enforce the depth limit at every write and publish entry

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs` (field, ctor, `CreateAsync`, `UpdateAsync`, `ReplaceAsync`, `DeleteAsync`, `BatchDecision`)
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs` (`CreateData`)
- Modify: `src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs` (same five places)
- Modify: `src/MMLib.Alvo/Events/Internal/AlvoEvents.cs` (ctor + `PublishAsync`)
- Modify: `src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs` (family table), `src/MMLib.Alvo.Abstractions/Events/IAlvoEvents.cs` (`<exception>`)
- Modify: `src/MMLib.Alvo.Testing/Data/AlvoDataAdversarialTests.cs` (provider-contract rows)
- Modify tests constructing `AlvoEvents`: `test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs` (`Publisher`), `test/MMLib.Alvo.Tests/Events/EventSubscriptionsTests.cs:338`
- Baselines: Testing PublicApi

**Interfaces:**
- Consumes: `AlvoChainDepthLimit` (Task 2) resolved from DI (Task 3).
- Produces: `EfAlvoData(…, AlvoOptions options, AlvoChainDepthLimit depthLimit)`; `AlvoEvents(IOutboxStore outbox, TimeProvider clock, AlvoChainDepthLimit depthLimit)`.

- [ ] **Step 1: Write the failing contract rows**

Append to `AlvoDataAdversarialTests` (runs on `InMemoryAlvoDataAdversarialTests`, `SqliteAlvoDataAdversarialTests`, `PostgreSqlAlvoDataAdversarialTests`):

```csharp
    /// <summary>
    /// The seventh exception family: a write deeper in one causation chain than the limit (8) is refused on every write
    /// face before anything is written — acceptance 4 and 5b of spec 2026-10-14 HX-01.
    /// </summary>
    [Fact]
    public async Task Every_write_face_refuses_a_write_deeper_than_the_chain_limit_and_writes_nothing()
    {
        var fixture = await NotesFixtureAsync();
        var deep = fixture.Alice with { Provenance = new AlvoProvenance { Depth = 9 } };
        var row = NotePayload(fixture);

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => fixture.Data.CreateAsync("notes", row, deep));
        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => fixture.Data.UpdateAsync("notes", fixture.AliceRow1Id, Title("x"), deep));
        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => fixture.Data.ReplaceAsync("notes", fixture.AliceRow1Id, row, deep));
        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => fixture.Data.DeleteAsync("notes", fixture.AliceRow1Id, deep));
        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => fixture.Data.CreateManyAsync("notes", [row], deep));
        await Should.ThrowAsync<AlvoChainDepthExceededException>(
            () => fixture.Data.UpdateManyAsync("notes", [new AlvoRowPatch(fixture.AliceRow1Id, Title("x"))], deep));
        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => fixture.Data.DeleteManyAsync("notes", [fixture.AliceRow1Id], deep));

        var titles = (await fixture.Data.QueryAsync(new AlvoQuery { Entity = "notes" }, fixture.Alice)).Items.Select(note => note["title"]);
        titles.ShouldBe(["Alice-1", "Alice-2"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_write_at_the_chain_limit_is_accepted()
    {
        var fixture = await NotesFixtureAsync();

        var created = await fixture.Data.CreateAsync(
            "notes", NotePayload(fixture), fixture.Alice with { Provenance = new AlvoProvenance { Depth = 8 } });

        created["title"].ShouldBe("deep but allowed");
    }

    private static Dictionary<string, object?> NotePayload(NotesFixture fixture) => new()
    {
        ["owner_id"] = fixture.Alice.User.Value,
        ["tenant_id"] = fixture.Tenant.Value,
        ["title"] = "deep but allowed",
    };

    private static Dictionary<string, object?> Title(string title) => new() { ["title"] = title };
```

(If `ReplaceAsync` on the seeded row requires `owner_id` and `tenant_id`, `NotePayload` already carries them.)

Append to `AlvoEventsTests`:

```csharp
    [Fact]
    public async Task Publishing_deeper_than_the_chain_limit_is_refused_and_appends_nothing()
    {
        var store = new RecordingOutboxStore();

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => Publisher(store).PublishAsync(
            "orders.approved", "orders/1", null, Caller with { Provenance = new AlvoProvenance { Depth = 9 } }, TestContext.Current.CancellationToken));

        store.Appended.ShouldBeEmpty();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*InMemoryAlvoDataAdversarialTests' --filter-class '*AlvoEventsTests'` and `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteAlvoDataAdversarialTests'`
Expected: the new facts FAIL (writes succeed at depth 9).

- [ ] **Step 3: Implement**

`EfAlvoData`: add `private readonly AlvoChainDepthLimit _depthLimit;`, a trailing ctor parameter `AlvoChainDepthLimit depthLimit` (`ArgumentNullException.ThrowIfNull(depthLimit); _depthLimit = depthLimit;`), and in `CreateAsync`, `UpdateAsync`, `ReplaceAsync`, `DeleteAsync` immediately after `AlvoIdempotency.EnsureUsableToken(idempotency, context);` and in `BatchDecision` immediately after its `EnsureUsableToken` line:

```csharp
        _depthLimit.EnsureWithin(context);
```

`AlvoEfCoreProvider.CreateData`: append `services.GetRequiredService<AlvoChainDepthLimit>()` as the last argument (add `using MMLib.Alvo.Data;` if needed).

`InMemoryAlvoData`: the same five insertions, as `AlvoChainDepthLimit.Default.EnsureWithin(context);` (the double is not configurable; record that in its class remarks: "enforces the default chain-depth limit of 8").

`AlvoEvents`: primary constructor `AlvoEvents(IOutboxStore outbox, TimeProvider clock, AlvoChainDepthLimit depthLimit)`; in `PublishAsync`, after `EnsureWritable(data);`:

```csharp
        depthLimit.EnsureWithin(context);
```

Update its remarks: "the name guard runs first, then the depth refusal, then the clock".

Test helpers: `Publisher(IOutboxStore store) => new(store, TimeProvider.System, AlvoChainDepthLimit.Default);` and the same third argument at `EventSubscriptionsTests.cs:338`.

`IAlvoData.cs`: "**Six exception families**" → "**Seven exception families**", and insert before the `InvalidOperationException` item:

```csharp
///   <item>
///     <term><see cref="AlvoChainDepthExceededException"/></term>
///     <description>
///     <b>The operation is deeper in one causation chain than the configured limit allows</b> — a hook, handler or
///     timer writing in a loop. Decided from the effective provenance (<c>AlvoEventProvenance.Effective</c>) before any
///     row is read or written, on every write face and batch. Neither the payload nor the caller's permissions is at
///     fault and the same request fails again at the same depth. Render 508 <c>chain-depth-exceeded</c>, non-retryable.
///     </description>
///   </item>
```

`IAlvoEvents.PublishAsync`: add `/// <exception cref="Data.AlvoChainDepthExceededException">The effective chain depth of <paramref name="context"/> is greater than the configured limit.</exception>`.

- [ ] **Step 4: Run tests**

`dotnet format whitespace …`; re-run Step 2's commands — PASS. Then the PostgreSQL leg: `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlAlvoDataAdversarialTests'` — PASS. Acceptance 4 (engine half), 5a, 5b.

- [ ] **Step 5: Accept the Testing API growth, ring0, commit**

```bash
git add src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/EfAlvoData.cs src/MMLib.Alvo.Data.EntityFrameworkCore/AlvoEfCoreProvider.cs src/MMLib.Alvo.Testing/Data/InMemoryAlvoData.cs src/MMLib.Alvo.Testing/Data/AlvoDataAdversarialTests.cs src/MMLib.Alvo/Events/Internal/AlvoEvents.cs src/MMLib.Alvo.Abstractions/Data/IAlvoData.cs src/MMLib.Alvo.Abstractions/Events/IAlvoEvents.cs test/MMLib.Alvo.Tests/Events/AlvoEventsTests.cs test/MMLib.Alvo.Tests/Events/EventSubscriptionsTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "feat(data): refuse writes and publishes deeper than the chain limit"
```

---

## Task 10: `508 chain-depth-exceeded`, non-retryable

**Files:**
- Modify: `src/MMLib.Alvo/Api/AlvoProblemTypes.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` (`GuardAsync` arm + the `ChainDepthExceeded` entry point)
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs` (a 508 arm for a refusal escaping an Alvo route, spec §4.6)
- Modify: `src/MMLib.Alvo/Api/Internal/ProblemComponents.cs` (`retryable` property)
- Modify: `test/MMLib.Alvo.Api.Tests/ProblemDetailsTests.cs` (`EveryFactoryResult`; a real Data API probe in `Only_the_slugs_awaiting_a_later_task_are_unreachable_over_http`; `PendingUntilALaterTask` stays empty)
- Modify: `test/MMLib.Alvo.Api.Tests/AlvoExceptionHandlerTests.cs`
- Modify: `website/src/data/problem-types.json`; `website/src/content/docs/guides/handle-errors.mdx`
- Test: `test/MMLib.Alvo.Api.Tests/ChainDepthProblemTests.cs`; snapshot `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt`
- Baseline: core PublicApi

**Interfaces:**
- Produces: `AlvoProblemTypes.ChainDepthExceeded = "chain-depth-exceeded"` (in `All`); a 508 problem with extension member `"retryable": false`.

- [ ] **Step 1: Write the failing test**

`test/MMLib.Alvo.Api.Tests/ChainDepthProblemTests.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Data;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests;

/// <summary>A chain-depth refusal is RFC 5842's 508, and says it must not be retried (spec 2026-10-14 X17, HX-01 acceptance 4).</summary>
public class ChainDepthProblemTests
{
    [Fact]
    public async Task A_chain_depth_refusal_is_508_with_its_own_type_and_marked_non_retryable()
    {
        var result = await ProblemResultFactory.GuardAsync(() => throw new AlvoChainDepthExceededException("too deep"));

        var (status, document) = await WrittenAsync(result);

        status.ShouldBe(StatusCodes.Status508LoopDetected);
        document["type"]!.GetValue<string>().ShouldBe(AlvoProblemTypes.UriOf(AlvoProblemTypes.ChainDepthExceeded));
        document["retryable"]!.GetValue<bool>().ShouldBeFalse();
        document["detail"]!.GetValue<string>().ShouldBe("too deep");
    }

    [Fact]
    public async Task No_other_refusal_claims_to_be_non_retryable()
    {
        var (_, document) = await WrittenAsync(
            await ProblemResultFactory.GuardAsync(() => throw new AlvoPreconditionFailedException("stale")));

        document.ContainsKey("retryable").ShouldBeFalse();
    }

    private static async Task<(int Status, JsonObject Document)> WrittenAsync(IResult result)
    {
        await using var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Position = 0;
        var document = await JsonSerializer.DeserializeAsync<JsonObject>(body, cancellationToken: TestContext.Current.CancellationToken);
        return (context.Response.StatusCode, document!);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ChainDepthProblemTests'` — build FAIL (`ChainDepthExceeded` missing).

- [ ] **Step 3: Implement**

`AlvoProblemTypes.cs` — after `FunctionFailed`:

```csharp
    /// <summary>The operation sits deeper in one causation chain than the configured limit allows — a loop (508).</summary>
    /// <remarks>
    /// RFC 5842's registered meaning for 508. The document carries <c>"retryable": false</c>: the same request arrives
    /// at the same depth, so an agent must not retry it. Raised from <c>AlvoChainDepthExceededException</c>; the fix is
    /// to break the loop, or raise <c>Alvo:Extensions:MaxDepth</c> when a chain that long is intended.
    /// </remarks>
    public const string ChainDepthExceeded = "chain-depth-exceeded";
```

and append `ChainDepthExceeded,` to `All` after `FunctionFailed`.

`ProblemResultFactory.GuardAsync` — insert before the `ArgumentException` arm:

```csharp
        catch (AlvoChainDepthExceededException exception)
        {
            return ChainDepthExceeded(exception.Message);
        }
```

and add beside `FunctionFailed(string detail)` (the one entry point both `GuardAsync` and `AlvoExceptionHandler` use):

```csharp
    /// <summary>
    /// A causation chain deeper than the limit (508): a loop. Carries <c>"retryable": false</c>, because the same request
    /// arrives at the same depth and is refused the same way every time.
    /// </summary>
    /// <param name="detail">The port's message, which names the depth and the limit and nothing caller-supplied.</param>
    internal static IResult ChainDepthExceeded(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status508LoopDetected,
            type: AlvoProblemTypes.UriOf(AlvoProblemTypes.ChainDepthExceeded),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { [RetryableMember] = false });

    private const string RetryableMember = "retryable";
```

`AlvoExceptionHandler.AnswerAsync` — after the `BadHttpRequestException` arm (a refusal raised outside `GuardAsync` on an Alvo route, e.g. by a host-registered endpoint filter calling `IAlvoEvents`; a host's own route is still the host's, per `FailedInsideAlvo`):

```csharp
        if (exception is AlvoChainDepthExceededException loop)
        {
            await ProblemResultFactory.ChainDepthExceeded(loop.Message).ExecuteAsync(httpContext).ConfigureAwait(false);
            return;
        }
```

(add `using MMLib.Alvo.Data;`). Append to `AlvoExceptionHandlerTests` (its `Handle`, `Context` and `_alvoEndpoint` helpers):

```csharp
    /// <summary>A chain-depth refusal escaping an Alvo route is a 508 the caller is told not to retry, never a 500.</summary>
    [Fact]
    public async Task A_chain_depth_refusal_escaping_an_alvo_route_is_508_and_non_retryable()
    {
        var logs = new RecordingLoggerProvider();
        var context = Context(aborted: false, _alvoEndpoint);

        await Handle(logs, context, new MMLib.Alvo.Data.AlvoChainDepthExceededException("too deep"));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status508LoopDetected);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        body.ShouldContain("chain-depth-exceeded");
        body.ShouldContain("\"retryable\":false");
        logs.Records.ShouldNotContain(LogLevel.Error, "a loop refusal is the caller's, not an operator's 500");
    }
```

`ProblemComponents.Document` — add to `Properties`:

```csharp
            ["retryable"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Boolean,
                Description =
                    "Present, and `false`, on a refusal the same request can never get past — such as "
                    + "`chain-depth-exceeded`. Absent everywhere else; absence makes no claim either way.",
            },
```

`ProblemDetailsTests`: add `Guarded(new AlvoChainDepthExceededException("too deep")),` and `ProblemResultFactory.ChainDepthExceeded("too deep"),` to `EveryFactoryResult()`. `PendingUntilALaterTask` **stays empty**: the slug is reachable today, because an explicit `AlvoContext.Provenance.Depth` is honoured and a host's own `IAlvoContextResolver` can carry one. In `Only_the_slugs_awaiting_a_later_task_are_unreachable_over_http`, after `reached.AddRange(await ManagementSlugsAnsweredByTheDescriptorWriteAsync());`, add `reached.Add(await ChainDepthSlugAnsweredByACallerDeepInAChainAsync());` and (with `using Microsoft.Extensions.DependencyInjection.Extensions; using MMLib.Alvo.Auth; using MMLib.Alvo.Auth.Internal;`):

```csharp
    /// <summary>
    /// The <c>chain-depth-exceeded</c> slug's probe: a host resolver that decorates the dev-key one and hands every caller
    /// an explicit depth of 9, then one ordinary Data API write — the real request path through <c>GuardAsync</c>, the
    /// store's entry check and the problem writer, so the arm's position before the malformed-request arm is measured.
    /// </summary>
    private static async Task<string> ChainDepthSlugAnsweredByACallerDeepInAChainAsync()
    {
        await using var world = await AlvoApiWorld.VehicleRegistryAsync(
            [_admin],
            new AlvoApiWorldSetup(ConfigureServicesAfterAlvo: services => services.Replace(
                ServiceDescriptor.Singleton<IAlvoContextResolver>(provider =>
                    new DeepInAChainResolver(ActivatorUtilities.CreateInstance<ApiKeyContextResolver>(provider))))));

        using var response = await world.SendAsync(
            HttpMethod.Post, "/api/owners", _admin, body: new JsonObject { ["name"] = "Ada" });

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status508LoopDetected, "or this probe reached some other refusal");
        var document = await response.ReadJsonObjectAsync();
        document["retryable"]!.GetValue<bool>().ShouldBeFalse();
        return document["type"]!.GetValue<string>()[AlvoProblemTypes.BaseUri.Length..];
    }

    /// <summary>Every caller the inner resolver admits, nine hops deep in one causation chain.</summary>
    private sealed class DeepInAChainResolver(IAlvoContextResolver inner) : IAlvoContextResolver
    {
        public async ValueTask<AlvoPrincipal?> ResolveAsync(
            string? presentedKey, string? requestedTenant, CancellationToken cancellationToken) =>
            await inner.ResolveAsync(presentedKey, requestedTenant, cancellationToken) is { } principal
                ? principal with { Context = principal.Context with { Provenance = new AlvoProvenance { Depth = 9 } } }
                : null;
    }
```

(If `owners` declares another required field, add it to the body — the probe must reach the store, not the validator; the status assertion says so if it does not.)

`website/src/data/problem-types.json` — add after `function-failed`:

```json
  "chain-depth-exceeded": {
    "causes": [
      "A write or a publish sat deeper in one causation chain than `Alvo:Extensions:MaxDepth` (default 8) allows: a hook, handler or timer is writing in a loop. Nothing was written."
    ],
    "fix": "Break the loop — a handler that writes the entity it reacts to needs a condition that stops it. Raise `Alvo:Extensions:MaxDepth` only when a chain that long is intended. The document carries `\"retryable\": false`: retrying cannot succeed.",
    "violationCodes": [],
    "guides": ["guides/handle-errors", "guides/after-hooks-and-webhooks"],
    "needsProblemDetails": false
  },
```

`guides/handle-errors.mdx`: in its problem-type table/list add one row/entry: `chain-depth-exceeded` — 508 — "a loop of hooks, handlers or timers; never retry it (`retryable: false`)".

- [ ] **Step 4: Run tests, accept snapshots**

`dotnet format whitespace …`; run `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ChainDepthProblemTests' --filter-class '*ProblemDetailsTests' --filter-class '*AlvoExceptionHandlerTests' --filter-class '*OpenApiDocumentTests'` and `dotnet test --project test/MMLib.Alvo.DocsGen.Tests --filter-class '*Problem*'`. Expected: PASS except `The_document_is_stable` — the received document differs only by the new enum URI and the `retryable` property; accept, dispatch `alvo-snapshot-judge`. Accept the core PublicApi (`AlvoProblemTypes.ChainDepthExceeded`, §5 row 8). Run `scripts/lint-api` (Vacuum) — PASS.

- [ ] **Step 5: ring0 and commit**

```bash
git add src/MMLib.Alvo/Api/AlvoProblemTypes.cs src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs src/MMLib.Alvo/Api/Internal/AlvoExceptionHandler.cs src/MMLib.Alvo/Api/Internal/ProblemComponents.cs test/MMLib.Alvo.Api.Tests/ChainDepthProblemTests.cs test/MMLib.Alvo.Api.Tests/ProblemDetailsTests.cs test/MMLib.Alvo.Api.Tests/AlvoExceptionHandlerTests.cs test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.The_document_is_stable.verified.txt test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.verified.txt website/src/data/problem-types.json website/src/content/docs/guides/handle-errors.mdx
git commit -m "feat(api): answer a chain-depth refusal with 508 chain-depth-exceeded, non-retryable"
```

**[slice end]** Run `scripts/test-ring1`.

---

## Task 11: After-hook phase refusal (#325)

**Files:**
- Create: `src/MMLib.Alvo/Events/Internal/AfterImagePhase.cs`
- Modify: `src/MMLib.Alvo/Events/Internal/AfterHookCompiler.cs` (`CompilePoint` → `CompileHook(hook, point, path, scope)` → `CompileCondition(source, point, path, scope)`)
- Modify: `test/MMLib.Alvo.Host.Tests/GuidedConditionConformanceTests.cs`
- Test: `test/MMLib.Alvo.Tests/Events/AfterHookCompilerTests.cs`

**Interfaces:**
- Produces: `internal readonly record struct AfterImages(bool Pre, bool Post) { static AfterImages At(string point) }`; `internal static IReadOnlyList<DescriptorValidationError> AfterImagePhase.Refusals(CelNode root, AfterImages images, string point, string path)` — HX-04's `ExtensionBindingCompiler` reuses it with its own images.

- [ ] **Step 1: Write the failing tests**

Append to `AfterHookCompilerTests`:

```csharp
    /// <summary>
    /// #325: an after-point refuses a reference to a row image its event does not carry — at apply, at the condition's
    /// pointer, with a fix — because the reference would read null and the filter would pass every event.
    /// </summary>
    [Theory]
    [InlineData("afterCreate", "old.stage == 'won'", "'old.<field>'")]
    [InlineData("afterCreate", "changed(stage)", "'changed(<field>)'")]
    [InlineData("afterDelete", "new.stage == 'won'", "'new.<field>'")]
    [InlineData("afterDelete", "stage == 'won'", "'new.<field>'")]
    [InlineData("afterDelete", "changed(stage)", "'changed(<field>)'")]
    public void An_after_point_refuses_an_image_its_event_does_not_carry(string point, string condition, string named)
    {
        var refusal = CompileErrors(At(point, Webhook(), condition)).ShouldHaveSingleItem();

        refusal.Path.ShouldBe($"/entities/deals/hooks/{point}/0/condition");
        refusal.Message.ShouldContain(named);
        refusal.FixSuggestion.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("afterCreate", "new.stage == 'won' && stage != 'lost'")]
    [InlineData("afterUpdate", "changed(stage) && old.stage != new.stage")]
    [InlineData("afterDelete", "old.stage == 'won'")]
    public void An_image_the_point_carries_still_applies(string point, string condition)
        => Point(Compile(At(point, Webhook(), condition)), point).ShouldHaveSingleItem().Condition.ShouldNotBeNull();
```

In `GuidedConditionConformanceTests`: move the four `#325` rows into `What_the_table_withholds_before_the_commit_or_from_an_envelope_apply_refuses`:

```csharp
    [InlineData("afterCreate", "old.text_opt == 'x'", "'old.<field>'")]
    [InlineData("afterDelete", "new.text_opt == 'x'", "'new.<field>'")]
    [InlineData("afterCreate", "changed(text_opt)", "'changed(<field>)'")]
    [InlineData("afterDelete", "changed(text_opt)", "'changed(<field>)'")]
```

and **delete** `What_the_table_withholds_after_the_commit_apply_still_accepts` (all four of its rows are refused now; a theory with no data fails). Task 12 re-adds it with the after-commit role rows. In the refusal theory's remark, and at `GuidedConditionConformanceTests.cs:110`, the #325 sentence becomes "apply refuses them since #325".

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AfterHookCompilerTests'` — the refusal theory FAILS (apply accepts).

- [ ] **Step 3: Implement**

`src/MMLib.Alvo/Events/Internal/AfterImagePhase.cs`:

```csharp
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Events.Internal;

/// <summary>Which row images the event behind an after-point carries.</summary>
/// <param name="Pre">Whether <c>old.</c> has a row to read.</param>
/// <param name="Post">Whether <c>new.</c> and a bare field have a row to read.</param>
internal readonly record struct AfterImages(bool Pre, bool Post)
{
    /// <summary>The images of a descriptor after-point: a create has no row before it, a delete none after it.</summary>
    internal static AfterImages At(string point) => point switch
    {
        "afterCreate" => new(Pre: false, Post: true),
        "afterDelete" => new(Pre: true, Post: false),
        _ => new(Pre: true, Post: true),
    };
}

/// <summary>
/// <b>#325</b>: refuses a post-commit condition that reads a row image its event does not carry. Such a reference reads
/// null, every comparison against it collapses to false — <c>!=</c> included — and a <c>changed()</c> on a delete is
/// true for every field that held a value, so a filter meant to narrow which events reach a webhook passes them all.
/// </summary>
/// <remarks>
/// One rule for every post-commit condition: <c>AfterHookCompiler</c> now, the extension binding compiler (HX-04) next.
/// <c>BeforeHookCompiler.HonoursThePhase</c> stays separate because a before-delete's bare field reads the row being
/// removed, while an after-delete's reads the event's absent post-image.
/// </remarks>
internal static class AfterImagePhase
{
    internal static IReadOnlyList<DescriptorValidationError> Refusals(CelNode root, AfterImages images, string point, string path) =>
        [.. Unanswerable(root, images, point).Select(refusal =>
            new DescriptorValidationError(path, refusal.Message, refusal.Fix, DescriptorValidationSeverity.Error))];

    private static IEnumerable<(string Message, string Fix)> Unanswerable(CelNode root, AfterImages images, string point)
    {
        if (!images.Pre && Reads(root, CelRecordState.Old))
        {
            yield return NoPreImage(point);
        }

        if (!images.Post && (Reads(root, CelRecordState.New) || Reads(root, CelRecordState.Current)))
        {
            yield return NoPostImage(point);
        }

        if (!(images.Pre && images.Post) && ReadsChanged(root))
        {
            yield return NoComparison(point);
        }
    }

    private static (string, string) NoPreImage(string point) => (
        $"This '{point}' condition reads 'old.<field>', which its event cannot answer: a create has no row before it, so "
        + "the reference reads null and every comparison against it — including '!=' — collapses to false, silently and "
        + "not always in the denying direction.",
        "Read 'new.<field>', which after a create is the row that was written.");

    private static (string, string) NoPostImage(string point) => (
        $"This '{point}' condition reads 'new.<field>' or a bare field name, which its event cannot answer: a delete "
        + "leaves no row, so the reference reads null and every comparison against it — including '!=' — collapses to "
        + "false, silently and not always in the denying direction.",
        "Read 'old.<field>', which after a delete is the row that was removed.");

    private static (string, string) NoComparison(string point) => (
        $"This '{point}' condition calls 'changed(<field>)', which compares the row before the write with the row after "
        + "it. This event carries only one of the two, so after a delete every field that held a value reads as changed.",
        "Compare the image this point has — 'new.<field>' after a create, 'old.<field>' after a delete — or move the "
        + "hook to 'afterUpdate', where a change exists.");

    private static bool Reads(CelNode node, CelRecordState state) =>
        (node is CelFieldRef reference && reference.State == state)
        || CelTree.Children(node).Any(child => Reads(child, state));

    private static bool ReadsChanged(CelNode node) =>
        node is CelChanged || CelTree.Children(node).Any(ReadsChanged);
}
```

`AfterHookCompiler`: `CompilePoint` calls `CompileHook(declared[index], point, $"{scope.EntityPath}/hooks/{point}/{index}", scope)`; `CompileHook(AfterHook hook, string point, string path, AfterHookScope scope)` passes `point` to `CompileCondition(hook.Condition, point, $"{path}/{ConditionSlot}", scope)`; in `CompileCondition` replace the last line with:

```csharp
        var expression = result.Expression!;
        var refusals = AfterImagePhase.Refusals(expression.Root, AfterImages.At(point), point, path);
        scope.Errors.AddRange(refusals);

        return HonoursTheEnvelope(expression, path, scope) && refusals.Count == 0 ? expression : null;
```

- [ ] **Step 4: Run tests**

`dotnet format whitespace …`; `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AfterHookCompilerTests'` and `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*GuidedConditionConformanceTests'` — PASS (acceptance 7). Also `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ExpressionCheckAgreementTests'` — PASS (check and apply agree because both run this compiler); update its comment "`old.` is accepted in an afterCreate condition" → "refused since #325".

- [ ] **Step 5: ring0 and commit** — `git commit -m "fix(hooks): refuse after-hook conditions that read an image the event lacks (#325)"` (stage the five files: `AfterImagePhase.cs`, `AfterHookCompiler.cs`, `AfterHookCompilerTests.cs`, `GuidedConditionConformanceTests.cs`, `ExpressionCheckAgreementTests.cs`). Keep the commit body free of the word "Closes" (the PR closes issues — memory *Closes keyword gotcha*).

---

## Task 12: Lift `@tenant.id` / `@user.roles` in after-hooks (#153), repoint to #372

**Files:**
- Modify: `src/MMLib.Alvo/Events/Internal/AfterHookCompiler.cs` (drop `HonoursTheEnvelope`, `_unanswerable`, `Unanswerable`, `UnanswerableReference`; `ActorRead` → `ProvenanceRead`; add `RolesRead`)
- Modify: `src/MMLib.Alvo/Rules/PolicyCatalog.cs` (`CompiledAfterHook` gains `bool ReadsRoles = false`)
- Modify: `src/MMLib.Alvo/Events/Internal/EventSubscriptions.cs` (`CallerOf`, `Selects`)
- Modify: `src/MMLib.Alvo/Events/Internal/EventLog.cs` (new `ConditionHasNoProvenanceToRead`)
- Modify: `src/MMLib.Alvo/Events/Internal/AlvoTemplate.cs` (`TemplatePlaceholder`)
- Modify: `src/MMLib.Alvo/Events/Internal/EnvelopeProvenance.cs`, `src/MMLib.Alvo/Descriptor/Internal/UnhonouredFeatures.cs` (`WildcardSubscription`)
- Modify: `src/MMLib.Alvo.Admin/Components/Integrations/TemplateDraft.cs`
- Modify tests: `test/MMLib.Alvo.Tests/Events/AfterHookCompilerTests.cs`, `AlvoTemplateTests.cs`, `EventSubscriptionsTests.cs`, `test/MMLib.Alvo.Host.Tests/GuidedConditionConformanceTests.cs`, `test/MMLib.Alvo.Admin.Tests/Integrations/TemplateDraftTests.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/TemplateDeclarationScenarios.cs`, `test/MMLib.Alvo.Tests/Descriptor/WildcardSubscriptionTests.cs` (new fact + remark), `test/MMLib.Alvo.Api.Tests/Management/ExpressionCheckAgreementTests.cs` (the `#153` rows flip)

**Interfaces:**
- Consumes: `AlvoEvent.TenantId`, `AlvoEvent.AuthClaims`, `AlvoEventProvenance.RolesIn` (Task 4).
- Produces: `CompiledAfterHook(string Path, CompiledExpression? Condition, RequiredContext Required, CompiledAction Action, bool ReadsRoles = false)`; `TemplatePlaceholder.Roots == ["new", "old", "event", "@user", "@tenant"]`; `EnvelopeProvenance.PatternSubscriptionsIssue == "#372"`.

- [ ] **Step 1: Write the failing tests**

`AfterHookCompilerTests` — replace `A_condition_naming_provenance_the_envelope_lacks_is_refused_at_apply` with:

```csharp
    /// <summary>
    /// #153: the envelope now carries <c>tenantid</c> and <c>authclaims</c>, so both references compile — and the hook
    /// records which ones it reads, so the dispatcher can refuse to select it for an envelope that lacks them.
    /// </summary>
    [Theory]
    [InlineData("new.tenant_id == @tenant.id", true, false)]
    [InlineData("!(new.tenant_id == @tenant.id)", true, false)]
    [InlineData("'admin' in @user.roles", false, true)]
    [InlineData("!('admin' in @user.roles)", false, true)]
    public void A_condition_reading_tenant_or_roles_applies_and_records_what_it_reads(string condition, bool tenant, bool roles)
    {
        var hook = Compile(AfterUpdate(Webhook(), condition)).AfterUpdate.ShouldHaveSingleItem();

        hook.Required.TenantId.ShouldBe(tenant);
        hook.ReadsRoles.ShouldBe(roles);
    }
```

`EventSubscriptionsTests` — add a catalog fixture and facts (extend `Entity(name)` with `new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Nullable = true },`; extend `Event(...)` with optional `string? tenantId = null, string? authClaims = null` assigned to `TenantId`/`AuthClaims`):

```csharp
    private static PolicyCatalog CatalogConditionedOnAnotherTenant => Catalog(new EntityHooks
    {
        AfterUpdate = [Hook("new.tenant_id != @tenant.id")],
    });

    private static PolicyCatalog CatalogConditionedOnAdmins => Catalog(new EntityHooks
    {
        AfterUpdate = [Hook("!('admin' in @user.roles)")],
    });

    private const string TenantA = "0b0a0000-0000-4000-8000-00000000000a";
    private const string TenantB = "0b0b0000-0000-4000-8000-00000000000b";

    [Theory]
    [InlineData(TenantA, TenantA, false)]
    [InlineData(TenantA, TenantB, true)]
    public void A_conditions_tenant_is_the_envelopes_tenant(string rowTenant, string envelopeTenant, bool selected)
        => Matching(CatalogConditionedOnAnotherTenant, Event(
                "entity.deals.updated",
                record: Record(("tenant_id", Guid.Parse(rowTenant))),
                oldRecord: Record(("tenant_id", Guid.Parse(rowTenant))),
                tenantId: envelopeTenant))
            .Count.ShouldBe(selected ? 1 : 0);

    [Fact]
    public void An_envelope_without_a_tenant_selects_no_hook_that_reads_one()
    {
        var @event = Event("entity.deals.updated", record: Record(("tenant_id", Guid.Parse(TenantA))), oldRecord: Record());

        Matching(CatalogConditionedOnAnotherTenant, @event).ShouldBeEmpty("a negated comparison against an absent tenant would otherwise pass every tenant");
        _logger.Entries.ShouldHaveSingleItem().Message.ShouldContain("@tenant.id");
    }

    [Theory]
    [InlineData("""{"roles":["admin","authenticated"]}""", false)]
    [InlineData("""{"roles":["authenticated"]}""", true)]
    public void A_conditions_roles_are_the_ones_the_caller_held_at_the_event(string claims, bool selected)
        => Matching(CatalogConditionedOnAdmins, Event("entity.deals.updated", record: Record(), oldRecord: Record(), authClaims: claims))
            .Count.ShouldBe(selected ? 1 : 0);

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    public void An_envelope_without_readable_claims_selects_no_hook_that_reads_roles(string? claims)
    {
        Matching(CatalogConditionedOnAdmins, Event("entity.deals.updated", record: Record(), oldRecord: Record(), authClaims: claims))
            .ShouldBeEmpty("'!(admin in roles)' against no roles at all would fire for every event, the admin's included");
        _logger.Entries.ShouldHaveSingleItem().Message.ShouldContain("@user.roles");
    }
```

`AlvoTemplateTests`: `The_roots_are_exactly_the_ones_the_envelope_can_answer` expects `["new", "old", "event", "@user", "@tenant"]` (remark: "@tenant joined with #153"); replace `The_tenant_root_is_refused_by_name_and_the_message_names_the_row_field_instead` with:

```csharp
    [Fact]
    public void The_tenant_and_roles_placeholders_resolve_and_render_from_the_envelope()
    {
        TemplatePlaceholder.TryResolve("@tenant.id", Deals, out var tenantRefusal).ShouldBeTrue(tenantRefusal);
        TemplatePlaceholder.TryResolve("@user.roles", Deals, out var rolesRefusal).ShouldBeTrue(rolesRefusal);

        AlvoTemplate.Parse("{{@tenant.id}}|{{@user.roles}}")
            .Render(SampleEvent() with { TenantId = "t-1", AuthClaims = """{"roles":["admin","authenticated"]}""" })
            .ShouldBe("t-1|admin,authenticated");
    }

    [Fact]
    public void An_envelope_written_before_hx_01_renders_tenant_and_roles_as_empty()
        => AlvoTemplate.Parse("[{{@tenant.id}}][{{@user.roles}}]").Render(SampleEvent()).ShouldBe("[][]");

    [Fact]
    public void Any_other_tenant_member_is_refused()
        => TemplatePlaceholder.TryResolve("@tenant.name", Deals, out _).ShouldBeFalse();
```

and in `Rendering_a_placeholder_apply_time_would_have_refused_throws_rather_than_renders_empty` use `"{{@tenant.name}}"`.

`GuidedConditionConformanceTests` — delete the four role rows from the refusal theory and re-add the narrower theory as:

```csharp
    /// <summary>
    /// Where the table is narrower than apply, recorded: a role row after the commit. Apply reads it from the envelope's
    /// <c>authclaims</c> since #153; the guided form still offers role rows before the commit only.
    /// </summary>
    [Theory]
    [InlineData("afterCreate", "'clerk' in @user.roles")]
    [InlineData("afterUpdate", "!('clerk' in @user.roles)")]
    [InlineData("afterDelete", "'clerk' in @user.roles")]
    [InlineData("afterDelete", "!('clerk' in @user.roles)")]
    public void What_the_table_withholds_after_the_commit_apply_accepts(string point, string condition)
    {
        Errors(point, condition).ShouldBeEmpty($"{condition} at {point}");
        ConditionText.Recognize(condition, point, Scope(point)).ShouldBeNull("the form offers no role row after the commit");
    }
```

Expression-bodied (`=>`) on purpose: `Catalog` reads `Schema`, a static auto-property declared further down, and a `{ get; } =` initializer placed above it would run first and see `null`.

`WildcardSubscriptionTests` — add (and replace the remark's "#153" with "#372"):

```csharp
    [Fact]
    public void The_wildcard_refusal_names_the_issue_that_owns_the_matcher_and_no_longer_the_envelope_gap()
    {
        var consequence = UnhonouredFeatures.WildcardSubscription.Consequence;

        consequence.ShouldContain(MMLib.Alvo.Events.Internal.EnvelopeProvenance.PatternSubscriptionsIssue);
        consequence.ShouldNotContain("#153");
    }
```

`ExpressionCheckAgreementTests.An_after_hook_call_reads_only_the_context_its_arguments_name` — the five `@tenant.id`/`@user.roles` rows become `null` (they apply since #153); keep the fact non-vacuous with two rows the after-hook still refuses, and assert the named reference rather than a prefix (the #325 refusal reads "This 'afterCreate' condition reads …"):

```csharp
    [InlineData("isMe(@tenant.id)", null)]
    [InlineData("yes(isMe(@tenant.id))", null)]
    [InlineData("yes('owner' in @user.roles)", null)]
    [InlineData("!yes('owner' in @user.roles)", null)]
    [InlineData("new.quantity > 1 && yes('owner' in @user.roles)", null)]
    [InlineData("old.quantity > 1", "old.<field>")]
    [InlineData("changed(quantity)", "changed(<field>)")]
```

and its last line becomes `reason.ShouldHaveSingleItem(messages).Message.ShouldContain($"'{refusedFor}'");`. Rewrite the corpus comment at `:51-53` to: "`old.` and `changed()` are refused in an afterCreate condition (#325); `@user.roles` and `@tenant.id` apply there (#153)." Update the `<summary>`: "pin what an after-hook refuses and why: a reference to a row image its event lacks." (`'owner'` is a declared role of this fixture — the rule rows already use it.)

`TemplateDraftTests`: the `@tenant.id` and `@user.roles` rows of `A_body_is_required_and_names_only_roots_an_email_can_resolve` flip to `true`; add `[InlineData("Hi {{@tenant.name}}", false)]`; replace `A_tenant_placeholder_is_refused_with_the_reason` with:

```csharp
    [Fact]
    public void A_tenant_member_other_than_id_is_refused_with_the_reason()
        => TemplateDraft.BodyRefusal("Hi {{@tenant.name}}").ShouldNotBeNull().ShouldContain("@tenant.id");
```

`TemplateDeclarationScenarios.A_template_with_a_broken_subject_or_a_tenant_placeholder_is_refused_at_its_fields` → rename `…_or_an_unknown_root_…`, fill the body with `"Hi {{customer.name}}"` and expect `"names no root"`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AfterHookCompilerTests' --filter-class '*EventSubscriptionsTests' --filter-class '*AlvoTemplateTests'` — build FAIL (`ReadsRoles`, `tenantId:` parameter) then FAIL on the refusals.

- [ ] **Step 3: Implement**

`PolicyCatalog.cs`: `internal sealed record CompiledAfterHook(string Path, CompiledExpression? Condition, RequiredContext Required, CompiledAction Action, bool ReadsRoles = false);` (document `ReadsRoles`: "whether the condition reads `@user.roles`, which only an envelope carrying `authclaims` can answer").

`AfterHookCompiler`: delete `HonoursTheEnvelope`, `Unanswerable(UnanswerableReference)`, `_unanswerable` and the `UnanswerableReference` record; `CompileCondition`'s last lines become `return refusals.Count == 0 ? expression : null;`; `CompileHook` builds `new CompiledAfterHook(path, condition, ProvenanceRead(condition), action, RolesRead(condition))`:

```csharp
    /// <summary>Which caller values the condition reads that an envelope may lack, so the dispatcher can gate on them.</summary>
    private static RequiredContext ProvenanceRead(CompiledExpression? condition) =>
        condition is null
            ? RequiredContext.None
            : new RequiredContext(
                TenantId: PolicyCatalogBuilder.ReferencesContextValue(condition.Root, CelContextValue.TenantId),
                UserId: PolicyCatalogBuilder.ReferencesContextValue(condition.Root, CelContextValue.UserId));

    private static bool RolesRead(CompiledExpression? condition) =>
        condition is not null && PolicyCatalogBuilder.ReferencesContextValue(condition.Root, CelContextValue.UserRoles);
```

Rewrite the class `<summary>`'s "refuses the two caller references the envelope cannot answer" → "records which caller references it reads".

`EventLog.cs` — after `ConditionHasNoActorToRead`:

```csharp
    /// <summary>A hook not selected because its condition reads provenance the event does not carry.</summary>
    /// <remarks>
    /// <c>@tenant.id</c> on an envelope without <c>tenantid</c> (a global row, a tenantless system write, a row written
    /// before HX-01) and <c>@user.roles</c> without readable <c>authclaims</c>. Refused upstream for the reason
    /// <see cref="ConditionHasNoActorToRead"/> gives: a negated comparison against an absent value passes every event.
    /// </remarks>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Alvo did not select after-hook {Hook} for event {EventId}: its condition reads '{Reference}' and "
            + "the event does not carry it, so the comparison has nothing to resolve against.")]
    internal static partial void ConditionHasNoProvenanceToRead(ILogger logger, string hook, string reference, Guid eventId);
```

`EventSubscriptions.cs` — replace `CallerOf` and the gate in `Selects`:

```csharp
    private static AlvoContext CallerOf(AlvoEvent @event)
    {
        var actor = UserId.TryParse(@event.AuthId, CultureInfo.InvariantCulture, out var user) ? user : default;
        TenantId? tenant = TenantId.TryParse(@event.TenantId, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        return new AlvoContext { User = actor, Roles = RolesOf(@event), Tenant = tenant };
    }

    private static IReadOnlySet<Role> RolesOf(AlvoEvent @event) =>
        AlvoEventProvenance.RolesIn(@event.AuthClaims) is { Count: > 0 } names
            ? RoleCatalog.Create(names).Resolve(names)
            : _noRoles;

    private static string? UnreadProvenance(CompiledAfterHook hook, AlvoEvent @event, AlvoContext caller) =>
        hook.Required.TenantIdMissingFrom(caller) ? TenantReference
        : hook.ReadsRoles && AlvoEventProvenance.RolesIn(@event.AuthClaims) is null ? RolesReference
        : null;

    private const string TenantReference = "@tenant.id";
    private const string RolesReference = "@user.roles";
```

In `Selects`, after the existing `UserIdMissingFrom`/`IsMissingFrom` block (narrow it to `hook.Required.UserIdMissingFrom(context)` keeping `ConditionHasNoActorToRead`), add:

```csharp
        if (UnreadProvenance(hook, @event, context) is { } reference)
        {
            EventLog.ConditionHasNoProvenanceToRead(logger, hook.Path, reference, @event.Id);
            return false;
        }
```

Rewrite the class remarks paragraph "A condition's `@user.id` is answered from the envelope" to cover `tenantid` and `authclaims` ("the caller *as at the event*"), and delete "The other two references cannot be answered…".

`AlvoTemplate.cs` (`TemplatePlaceholder`): `Roots { get; } = [NewRoot, OldRoot, EventRoot, UserRoot, TenantRoot];`; `RefusalFor`'s `TenantRoot =>` arm becomes `TenantRoot => member == IdMember ? null : UnknownTenantMember(placeholder),`; `UnknownUserMember`'s `RolesMember => RolesAreNotOnTheEnvelope(placeholder),` becomes `RolesMember => null,`; delete `RolesAreNotOnTheEnvelope`/`TenantIsNotOnTheEnvelope`; in `ValueOf` add arms before `_ =>`:

```csharp
            UserRoot when member == RolesMember => RolesText(@event),
            TenantRoot when member == IdMember => @event.TenantId,
```

and:

```csharp
    /// <summary>The roles the caller held at the event, comma-joined in ordinal order; empty on an envelope without claims.</summary>
    private static string? RolesText(AlvoEvent @event) =>
        AlvoEventProvenance.RolesIn(@event.AuthClaims) is { } roles ? string.Join(',', roles) : null;

    private static string UnknownTenantMember(string placeholder) =>
        $"{Quoted(placeholder)} cannot be resolved: '{TenantRoot}{RootSeparator}{IdMember}' is the one '{TenantRoot}' "
        + "member an event carries.";
```

Rewrite the `TemplatePlaceholder` remarks paragraph "Two names the design's own table promises and this build refuses" to: "`@tenant.id` and `@user.roles` resolve from the envelope's `tenantid` and `authclaims` (#153); an envelope written before HX-01 renders both empty, as `@user.id` renders for an anonymous write."

`EnvelopeProvenance.cs` — replace the body with the authority that remains:

```csharp
namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// What an event envelope can answer after the commit, and where the one remaining gap is tracked.
/// </summary>
/// <remarks>
/// Since #153 the envelope carries <c>tenantid</c> and <c>authclaims</c>, so after-hook conditions and templates resolve
/// <c>@tenant.id</c> and <c>@user.roles</c> as the caller held them at the event, and refuse to select a hook whose
/// envelope lacks them. What stays refused is a <em>wildcard</em> subscription: its matcher and the cross-tenant suite
/// it must pass are designed in <see cref="PatternSubscriptionsIssue"/>.
/// </remarks>
internal static class EnvelopeProvenance
{
    /// <summary>The issue that owns pattern subscriptions scoped by <c>tenantid</c> (N-2 of spec 2026-10-14).</summary>
    internal const string PatternSubscriptionsIssue = "#372";
}
```

`UnhonouredFeatures.WildcardSubscription` — consequence and remarks reworded around the matcher, not the envelope:

```csharp
    internal static UnhonouredSlot WildcardSubscription { get; } = new(
        "trigger.event",
        "A wildcard event subscription is not matched yet: no rule fires for it today, and a matcher that fires it must "
        + "scope every match to the envelope's tenant and pass a cross-tenant adversarial suite first — a fan-out across "
        + $"every tenant is the failure it would otherwise ship ({Events.Internal.EnvelopeProvenance.PatternSubscriptionsIssue}).",
        "Name the entity and the operation exactly — 'entity.orders.created' rather than 'entity.orders.*' — "
        + "and declare one rule per pair. Wildcards are refused rather than accepted-and-ignored because a "
        + "descriptor outlives the build that applied it: accepted today, it becomes the fan-out above on the "
        + "day matching lands, with nothing between it and delivery.");
```

and in its `<remarks>` replace "`AlvoEvent` carries no tenant attribute … Giving the envelope one is issue **#153**." with "The envelope now carries `tenantid`, but the scoped matcher and the adversarial fact the ruling demands are not built — **#372**." Update `WildcardSubscriptionTests`' remark the same way.

`TemplateDraft.cs`: `Roots { get; } = ["new", "old", "event", "@user", "@tenant"];` and in `PlaceholderRefusal` replace the `@tenant`/`@user.roles` branch with:

```csharp
            if (inner.StartsWith("@tenant", StringComparison.Ordinal) && inner != "@tenant.id")
            {
                return $"'{{{{{inner}}}}}' cannot be answered by an email: '@tenant.id' is the one tenant member an event carries.";
            }
```

and update the class remark "a root an event envelope cannot answer" → "a member an event envelope does not carry".

- [ ] **Step 4: Run tests**

`dotnet format whitespace …`; then `grep -rn "EnvelopeProvenance\.\(NoTenant\|NoRoles\|InsteadOf\)\|cref=\"EnvelopeProvenance\.\|#153" src/MMLib.Alvo src/MMLib.Alvo.Admin test/MMLib.Alvo.Tests/Events/AfterHookCompilerTests.cs` must print nothing that refers to the deleted members (a stale `<see cref>` is CS1574 under warnings-as-errors; the remark at `AfterHookCompilerTests.cs:270` names #153 and is rewritten to say the references now apply). Then run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*AfterHookCompilerTests' --filter-class '*EventSubscriptionsTests' --filter-class '*AlvoTemplateTests' --filter-class '*WildcardSubscriptionTests'`, `dotnet test --project test/MMLib.Alvo.Api.Tests --filter-class '*ExpressionCheckAgreementTests'`, `dotnet test --project test/MMLib.Alvo.Host.Tests --filter-class '*GuidedConditionConformanceTests' --filter-class '*HooksEditorAgreementTests'`, `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*TemplateDraftTests'`. Expected: PASS. (`HooksEditorAgreementTests` pins `TemplateDraft.Roots == TemplatePlaceholder.Roots`.)

- [ ] **Step 5: ring0 and commit** — stage every file listed; `git commit -m "feat(hooks): resolve @tenant.id and @user.roles from the envelope (#153)"`.

---

## Task 13: Apply refuses after-hooks that read an excluded field

**Files:**
- Modify: `src/MMLib.Alvo/Events/Internal/AfterHookCompiler.cs` (`CompileCondition`, `AddTemplate`)
- Test: `test/MMLib.Alvo.Tests/Descriptor/EventImageExclusionTests.cs` (append)

- [ ] **Step 1: Write the failing tests**

Append to `EventImageExclusionTests`:

```csharp
    [Fact]
    public void An_after_hook_condition_reading_an_excluded_field_is_refused_at_its_pointer()
    {
        var refusal = _validator.Validate(NotesWithHook("""{ "condition": "new.salary > 10", "action": { "type": "webhook", "endpoint": "crm" } }"""))
            .Errors.ShouldHaveSingleItem();

        refusal.Path.ShouldBe("/entities/notes/hooks/afterUpdate/0/condition");
        refusal.Message.ShouldContain("salary");
        refusal.FixSuggestion.ShouldNotBeNull().ShouldContain("events/exclude");
    }

    [Fact]
    public void Changed_still_names_an_excluded_field_so_testing_it_applies()
        => _validator.Validate(NotesWithHook("""{ "condition": "changed(salary)", "action": { "type": "webhook", "endpoint": "crm" } }"""))
            .IsValid.ShouldBeTrue();

    [Fact]
    public void An_email_recipient_template_reading_an_excluded_field_is_refused_at_its_pointer()
    {
        var refusal = _validator.Validate(NotesWithHook(
                """{ "action": { "type": "email", "template": "note-changed", "to": "{{new.salary}}@example.test" } }"""))
            .Errors.ShouldHaveSingleItem();

        refusal.Path.ShouldBe("/entities/notes/hooks/afterUpdate/0/action/to");
        refusal.Message.ShouldContain("salary");
    }

    private static string NotesWithHook(string hook) => $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "exclusions",
          "webhooks": { "endpoints": { "crm": { "url": "https://crm.example.test/hook", "secretRef": "crm-secret" } } },
          "templates": { "note-changed": { "subject": "Changed", "body": "A note changed." } },
          "entities": {
            "notes": {
              "fields": { "title": { "type": "string" }, "salary": { "type": "integer" } },
              "events": { "exclude": ["salary"] },
              "hooks": { "afterUpdate": [ {{hook}} ] }
            }
          }
        }
        """;
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests --filter-class '*EventImageExclusionTests'` — the two refusal facts FAIL (no error).

- [ ] **Step 3: Implement** in `AfterHookCompiler`:

In `CompileCondition`, before `scope.Errors.AddRange(refusals);`, change to:

```csharp
        IReadOnlyList<DescriptorValidationError> refusals =
        [
            .. AfterImagePhase.Refusals(expression.Root, AfterImages.At(point), point, path),
            .. ExcludedFieldReads(expression.Root, path, scope),
        ];
```

In `AddTemplate`, after the existing `Refusals(template!, scope.Schema)` block and before `templates[slot] = template!;`:

```csharp
        var excluded = ExcludedPlaceholders(template!, scope.Schema).ToList();
        if (excluded.Count > 0)
        {
            scope.Errors.AddRange(excluded.Select(field => Error(path, ExcludedPlaceholder(field), ExcludedFix(scope, field))));
            return false;
        }
```

and the helpers:

```csharp
    private static IEnumerable<DescriptorValidationError> ExcludedFieldReads(CelNode root, string path, AfterHookScope scope) =>
        FieldsRead(root)
            .Where(field => scope.Schema.EventImageExclusions.Contains(field, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(field => Error(path, ExcludedCondition(field), ExcludedFix(scope, field)));

    private static IEnumerable<string> FieldsRead(CelNode node) =>
        (node is CelFieldRef reference ? [reference.FieldName] : Enumerable.Empty<string>())
            .Concat(CelTree.Children(node).SelectMany(FieldsRead));

    private static IEnumerable<string> ExcludedPlaceholders(AlvoTemplate template, EntitySchema entity) =>
        template.Placeholders
            .Select(placeholder => placeholder.Split('.', 2))
            .Where(parts => parts.Length == 2 && (parts[0] is "new" or "old")
                && entity.EventImageExclusions.Contains(parts[1], StringComparer.Ordinal))
            .Select(parts => parts[1])
            .Distinct(StringComparer.Ordinal);

    private static string ExcludedCondition(string field) =>
        $"This after-hook condition reads '{field}', which this entity leaves out of its event images (events.exclude), "
        + "so the reference would read null on every event and the comparison would be decided by its absence — "
        + "silently, and not always in the denying direction.";

    private static string ExcludedPlaceholder(string field) =>
        $"This template reads '{field}', which this entity leaves out of its event images (events.exclude), so it would "
        + "render empty on every delivery.";

    private static string ExcludedFix(AfterHookScope scope, string field) =>
        $"Remove '{field}' from {scope.EntityPath}/events/exclude, or stop reading it here; 'changed({field})' still names "
        + "an excluded field.";
```

(`AfterHookCompiler` already imports `MMLib.Alvo.Expressions` and `MMLib.Alvo.Expressions.Internal`, which hold `CelNode`, `CelFieldRef` and `CelTree`.)

- [ ] **Step 4: Run tests**

`dotnet format whitespace …`; re-run Step 2 plus `--filter-class '*AfterHookCompilerTests'` — PASS (acceptance 5c, the after-hook half).

- [ ] **Step 5: ring0 and commit** — `git commit -m "feat(hooks): refuse after-hooks that read a field excluded from event images"`.

---

## Task 14: Adversarial — a cross-tenant condition never fires (both engines)

**Files:**
- Create: `src/MMLib.Alvo.Testing/Events/AlvoEventTenancyTests.cs`
- Modify: `src/MMLib.Alvo.Testing/Events/IAlvoEventWorld.cs` (add `IAlvoData Data { get; }`)
- Modify: `test/_shared/events/AlvoEventCriteriaWorld.cs` (`public IAlvoData Data => _data;`)
- Create: `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoEventTenancyTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoEventTenancyTests.cs`
- Baseline: Testing PublicApi

- [ ] **Step 1: Write the failing suite**

`src/MMLib.Alvo.Testing/Events/AlvoEventTenancyTests.cs`:

```csharp
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;

using Shouldly;

using Xunit;

using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Events;

/// <summary>
/// The two-tenant adversarial fact for after-hooks (spec 2026-10-14 §6.2, HX-01 acceptance 6): a condition comparing a
/// row's tenant with <c>@tenant.id</c> is decided by the envelope's own <c>tenantid</c>, on a real write through a real
/// dispatcher, so a hook meant to catch "another tenant's row" never fires for a tenant's own writes.
/// </summary>
public abstract class AlvoEventTenancyTests
{
    /// <summary>Builds a fresh backend over <paramref name="project"/>.</summary>
    protected abstract Task<IAlvoEventWorld> WorldAsync(AlvoEventProject project);

    [Fact]
    public async Task A_cross_tenant_condition_never_fires_and_its_mirror_fires_for_each_tenant()
    {
        await using var world = await WorldAsync(Project);
        var acme = Caller(TenantId.New());
        var globex = Caller(TenantId.New());

        await CreateAsync(world, acme);
        await CreateAsync(world, globex);
        await world.DrainAsync();

        world.Deliveries.Where(delivery => delivery.Url == LeakUrl).ShouldBeEmpty(
            "new.tenant_id != @tenant.id must be false for every write a tenant makes to its own rows");
        world.Deliveries.Count(delivery => delivery.Url == OwnUrl).ShouldBe(
            2, "the mirror condition proves @tenant.id resolved at all — without it 'never fires' is vacuous");
    }

    protected static AlvoEventProject Project => new(Schema, Descriptor, Caller(TenantId.New()), Tickets, "MMLib.Alvo.Events");

    private const string Tickets = "tickets";
    private const string OwnUrl = "https://receiver.test/own-tenant";
    private const string LeakUrl = "https://receiver.test/cross-tenant";
    private const string AuthenticatedOnly = "'authenticated' in @user.roles";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task CreateAsync(IAlvoEventWorld world, AlvoContext caller) =>
        world.Data.CreateAsync(Tickets, Ticket(caller.Tenant!.Value), caller, cancellationToken: Ct);

    private static Dictionary<string, object?> Ticket(TenantId tenant) => new(StringComparer.Ordinal)
    {
        ["title"] = "printer on fire",
        [AlvoManagedColumns.TenantId] = tenant.Value,
    };

    private static AlvoContext Caller(TenantId tenant) => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = tenant,
    };

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "event-tenancy-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Tickets] = new()
            {
                Tenancy = EntityTenancy.Scoped,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String },
                },
                Rules = new AccessRules
                {
                    List = AuthenticatedOnly, Get = AuthenticatedOnly, Create = AuthenticatedOnly,
                    Update = AuthenticatedOnly, Delete = AuthenticatedOnly,
                },
                Hooks = new EntityHooks
                {
                    AfterCreate =
                    [
                        new AfterHook { Condition = "new.tenant_id != @tenant.id", Action = new WebhookAction { Endpoint = "leak" } },
                        new AfterHook { Condition = "new.tenant_id == @tenant.id", Action = new WebhookAction { Endpoint = "own" } },
                    ],
                },
            },
        },
        Webhooks = new Webhooks
        {
            Endpoints = new Dictionary<string, WebhookEndpoint>(StringComparer.Ordinal)
            {
                ["leak"] = new() { Url = LeakUrl, SecretRef = "leak-secret" },
                ["own"] = new() { Url = OwnUrl, SecretRef = "own-secret" },
            },
        },
    };

    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Tickets,
            Tenancy = TenancyMode.Scoped,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true },
                new FieldSchema { Name = AlvoManagedColumns.TenantId, Type = SchemaField.Uuid, Required = true, Indexed = true },
            ],
        },
    ]);
}
```

`IAlvoEventWorld` — add `/// <summary>The data port the backend writes through, for a suite that writes as several callers.</summary> IAlvoData Data { get; }` (`using MMLib.Alvo.Data;`). `AlvoEventCriteriaWorld` — `public IAlvoData Data => _data;`.

`test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoEventTenancyTests.cs`:

```csharp
using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Events;

using Xunit;

namespace MMLib.Alvo.Data.Sqlite.Tests;

[Collection(DispatchedEventCollection.Name)]
public sealed class SqliteAlvoEventTenancyTests : AlvoEventTenancyTests, IAsyncDisposable
{
    private readonly SqliteAlvoDataFixture _fixture = new();

    protected override async Task<IAlvoEventWorld> WorldAsync(AlvoEventProject project) =>
        await AlvoEventCriteriaWorld.StartAsync(project, async install =>
        {
            var host = await _fixture.StartAsync(project.Schema, project.Descriptor, configure: install);

            return (host.Data, (IServiceProvider)host.Services);
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();
}
```

`test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoEventTenancyTests.cs` — the same with `PostgreSqlAlvoDataFixture`, `IAsyncLifetime`, `public ValueTask InitializeAsync() => _fixture.InitializeAsync();` (mirror `PostgreSqlAlvoEventCriteriaTests`).

- [ ] **Step 2: Prove the fact bites**

Run: `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests --filter-class '*SqliteAlvoEventTenancyTests'` — Expected PASS on this branch. Then temporarily make `EventSubscriptions.CallerOf` ignore `@event.TenantId` (tenant `null`): the run must FAIL on the `OwnUrl` count (0 instead of 2) — the mirror proves non-vacuity. Restore.

- [ ] **Step 3: PostgreSQL leg** — `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration --filter-class '*PostgreSqlAlvoEventTenancyTests'` — PASS.

- [ ] **Step 4: Accept Testing API growth, ring0, commit**

```bash
git add src/MMLib.Alvo.Testing/Events/AlvoEventTenancyTests.cs src/MMLib.Alvo.Testing/Events/IAlvoEventWorld.cs test/_shared/events/AlvoEventCriteriaWorld.cs test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAlvoEventTenancyTests.cs test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAlvoEventTenancyTests.cs test/MMLib.Alvo.Tests/PublicApi.MMLib.Alvo.Testing.verified.txt
git commit -m "test(security): a cross-tenant after-hook condition never fires on either engine"
```

**[slice end]** Run `scripts/test-ring1`.

---

## Task 15: Architecture fact — only the core and the EF driver set `AlvoFlow`

**Files:**
- Modify: `test/_shared/ai/InternalMemberReferences.cs` (add `FlowSetters`)
- Modify: `test/MMLib.Alvo.Ai.Tests/InternalGrantArchitectureTests.cs`, `test/MMLib.Alvo.Admin.Tests/InternalGrantArchitectureTests.cs`
- Create: `test/MMLib.Alvo.Identity.Tests/InternalGrantArchitectureTests.cs`, `test/MMLib.Alvo.Tests/TestingInternalGrantArchitectureTests.cs`
- Modify: `test/MMLib.Alvo.Identity.Tests/MMLib.Alvo.Identity.Tests.csproj`, `test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj` (link the shared file)

- [ ] **Step 1: Write the facts**

`InternalMemberReferences.cs` — add:

```csharp
    /// <summary>
    /// The setter of the ambient flow (spec 2026-10-14 §4.6, R-9): only <c>MMLib.Alvo</c> and the EF driver may call it,
    /// so no other granted product assembly can forge the chain depth or causation a write reports.
    /// </summary>
    internal static IReadOnlyList<string> FlowSetters { get; } = ["MMLib.Alvo.AlvoFlow::Enter"];
```

Ai and Admin `InternalGrantArchitectureTests` — add:

```csharp
    [Fact]
    public void It_never_sets_the_ambient_flow()
        => InternalMemberReferences.FlowSetters.ShouldAllBe(member => !_referenced.Contains(member));
```

`test/MMLib.Alvo.Identity.Tests/InternalGrantArchitectureTests.cs`:

```csharp
using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>The identity package's Abstractions grant is for the framework-table names, never for the ambient flow.</summary>
public sealed class InternalGrantArchitectureTests
{
    private static readonly IReadOnlySet<string> _referenced =
        InternalMemberReferences.Of(typeof(AlvoIdentityContextResolver).Assembly);

    /// <summary>The control: the scan sees a member reference this assembly certainly makes.</summary>
    [Fact]
    public void The_scan_sees_the_context_the_resolver_builds()
        => _referenced.ShouldContain("MMLib.Alvo.AlvoContext::set_User");

    [Fact]
    public void It_never_sets_the_ambient_flow()
        => InternalMemberReferences.FlowSetters.ShouldAllBe(member => !_referenced.Contains(member));

    [Theory]
    [InlineData("MMLib.Alvo.Rules.PolicyDecision::.ctor")]
    [InlineData("MMLib.Alvo.Expressions.CompiledExpression::.ctor")]
    public void It_never_reaches_a_security_core_member(string member)
        => _referenced.ShouldNotContain(member);
}
```

`test/MMLib.Alvo.Tests/TestingInternalGrantArchitectureTests.cs`:

```csharp
using MMLib.Alvo.Ai.Tests;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Tests;

/// <summary><c>MMLib.Alvo.Testing</c> holds an Abstractions grant for the reachability port; it never sets the ambient flow.</summary>
public sealed class TestingInternalGrantArchitectureTests
{
    private static readonly IReadOnlySet<string> _referenced = InternalMemberReferences.Of(typeof(InMemoryAlvoData).Assembly);

    [Fact]
    public void The_scan_sees_the_idempotency_guard_the_double_calls()
        => _referenced.ShouldContain("MMLib.Alvo.Data.AlvoIdempotency::EnsureUsableToken");

    [Fact]
    public void It_never_sets_the_ambient_flow()
        => InternalMemberReferences.FlowSetters.ShouldAllBe(member => !_referenced.Contains(member));
}
```

Link the shared file in both csproj files (as `MMLib.Alvo.Ai.Tests.csproj:24` does):

```xml
  <ItemGroup>
    <Compile Include="$(MSBuildThisFileDirectory)../_shared/ai/InternalMemberReferences.cs" Link="_shared/InternalMemberReferences.cs" />
  </ItemGroup>
```

- [ ] **Step 2: Run and prove non-vacuity**

Run: `dotnet test --project test/MMLib.Alvo.Identity.Tests --filter-class '*InternalGrantArchitectureTests'`, same for `test/MMLib.Alvo.Tests --filter-class '*TestingInternalGrantArchitectureTests'`, `test/MMLib.Alvo.Ai.Tests` and `test/MMLib.Alvo.Admin.Tests --filter-class '*InternalGrantArchitectureTests'` — PASS. To see the ban bite, temporarily add `using var _ = AlvoFlow.Enter(AlvoProvenance.None);` to any method of `InMemoryAlvoData`: `TestingInternalGrantArchitectureTests.It_never_sets_the_ambient_flow` FAILS. Remove it. If the control fact for Identity fails because the init accessor is emitted under another name, read the actual name with `_referenced.Where(m => m.StartsWith("MMLib.Alvo.AlvoContext::"))` and pin that.

- [ ] **Step 3: ring1 (arch tests) and commit**

```bash
git add test/_shared/ai/InternalMemberReferences.cs test/MMLib.Alvo.Ai.Tests/InternalGrantArchitectureTests.cs test/MMLib.Alvo.Admin.Tests/InternalGrantArchitectureTests.cs test/MMLib.Alvo.Identity.Tests test/MMLib.Alvo.Tests/TestingInternalGrantArchitectureTests.cs test/MMLib.Alvo.Tests/MMLib.Alvo.Tests.csproj
git commit -m "test(arch): only the core and the EF driver may set the ambient flow"
```

---

## Task 16: Dashboard — *Exclude from event images* toggle

**Files:**
- Create: `src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Events.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldReferences.cs` (`Walk`)
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor`, `FieldEditor.razor.cs`
- Modify: `src/MMLib.Alvo.Admin/Components/Schema/Entity.razor.cs` (`AddFieldAsync`)
- Test: `test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyEventExclusionTests.cs`, `test/MMLib.Alvo.Admin.Tests.EndToEnd/EventExclusionScenarios.cs`

**Interfaces:**
- Produces: `WorkingCopy.IsExcludedFromEvents(string entity, string field) : bool`; `WorkingCopy.ExcludeFromEvents(string entity, string field, bool excluded) : void`; `FieldEditor.NewField.ExcludedFromEvents` (`internal bool`, init) — internal, so `PublicApi.MMLib.Alvo.Admin` does not move.

- [ ] **Step 1: Write the failing unit tests**

`test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyEventExclusionTests.cs`:

```csharp
using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>What the field editor's <em>Exclude from event images</em> writes, asserted against the descriptor JSON.</summary>
public class WorkingCopyEventExclusionTests
{
    [Fact]
    public void Excluding_a_field_writes_the_schemas_own_shape()
    {
        var copy = Copy();

        copy.ExcludeFromEvents("work_orders", "status", excluded: true);

        Events(copy).ShouldBe("""{"exclude":["status"]}""");
        copy.IsExcludedFromEvents("work_orders", "status").ShouldBeTrue();
    }

    [Fact]
    public void Excluding_twice_lists_the_field_once()
    {
        var copy = Copy();

        copy.ExcludeFromEvents("work_orders", "status", excluded: true);
        copy.ExcludeFromEvents("work_orders", "status", excluded: true);

        Events(copy).ShouldBe("""{"exclude":["status"]}""");
    }

    [Fact]
    public void Putting_the_last_field_back_removes_the_block_rather_than_leaving_it_empty()
    {
        var copy = Copy();
        copy.ExcludeFromEvents("work_orders", "status", excluded: true);

        copy.ExcludeFromEvents("work_orders", "status", excluded: false);

        Entity(copy).ContainsKey("events").ShouldBeFalse();
        copy.IsDirty.ShouldBeFalse("undoing the only edit leaves nothing to preview");
    }

    [Fact]
    public void A_rename_carries_the_exclusion()
    {
        var copy = Copy();
        copy.ExcludeFromEvents("work_orders", "status", excluded: true);

        copy.RenameField("work_orders", "status", "state", out var uncarried).ShouldBeNull();

        Events(copy).ShouldBe("""{"exclude":["state"]}""");
        uncarried.ShouldBeEmpty();
    }

    [Fact]
    public void A_removal_names_the_exclusion_as_a_place_that_blocks_it()
    {
        var copy = Copy();
        copy.ExcludeFromEvents("work_orders", "status", excluded: true);

        copy.ReferencesToField("work_orders", "status")
            .ShouldContain(reference => reference.Place == "work_orders.events.exclude" && reference.Blocks);
    }

    private static JsonObject Entity(WorkingCopy copy) => (JsonObject)JsonNode.Parse(copy.Json)!["entities"]!["work_orders"]!;

    private static string Events(WorkingCopy copy) => Entity(copy)["events"]!.ToJsonString();

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(
            """
            {
              "apiVersion": "v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": {
                    "status": { "type": "string" },
                    "code": { "type": "string" }
                  }
                }
              }
            }
            """,
            revision: 3);

        return copy;
    }
}
```

(`IsDirty` and `Take(json, revision)` are the existing members `WorkingCopyIndexTests` uses; confirm `IsDirty` is reachable — it is referenced in `WorkingCopy.Fields.cs`'s remarks; if it is private, assert `copy.Json` equals `copy.AppliedJson` instead.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*WorkingCopyEventExclusionTests'` — build FAIL.

- [ ] **Step 3: Implement**

`src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Events.cs`:

```csharp
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* An entity's events.exclude: the fields its event images leave out (spec 2026-10-14 §4.7, HX-01). */
internal sealed partial class WorkingCopy
{
    private const string EventsKey = "events";
    private const string ExcludeKey = "exclude";

    /// <summary>Whether the working copy leaves the field out of the entity's event images.</summary>
    public bool IsExcludedFromEvents(string entity, string field)
        => Read(() => ExclusionsOf(WorkingEntityOf(entity))?.Any(name => IsName(name, field)) is true);

    /// <summary>Leaves the field out of the entity's event images, or puts it back; an empty block is removed, not kept.</summary>
    public void ExcludeFromEvents(string entity, string field, bool excluded) => Edit(_ =>
        WorkingEntityOf(entity) is { } declared && (excluded ? Exclude(declared, field) : Include(declared, field)));

    private static JsonArray? ExclusionsOf(JsonObject? declared) => declared?[EventsKey]?[ExcludeKey] as JsonArray;

    private static bool Exclude(JsonObject declared, string field)
    {
        if (ExclusionsOf(declared)?.Any(name => IsName(name, field)) is true)
        {
            return false;
        }

        var events = Ensure(declared, EventsKey);
        if (events[ExcludeKey] is not JsonArray names)
        {
            names = [];
            events[ExcludeKey] = names;
        }

        names.Add(field);
        return true;
    }

    private static bool Include(JsonObject declared, string field)
    {
        if (ExclusionsOf(declared) is not { } names || names.FirstOrDefault(name => IsName(name, field)) is not { } entry)
        {
            return false;
        }

        names.Remove(entry);
        DropIfEmpty(declared);
        return true;
    }

    private static void DropIfEmpty(JsonObject declared)
    {
        if (declared[EventsKey] is JsonObject events && ExclusionsOf(declared) is { Count: 0 })
        {
            events.Remove(ExcludeKey);
        }

        if (declared[EventsKey] is JsonObject { Count: 0 })
        {
            declared.Remove(EventsKey);
        }
    }

    private static bool IsName(JsonNode? node, string field)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && text == field;
}
```

`FieldReferences.Walk` — after `Indexes(declared, at);` add `EventExclusions(declared, at);` and:

```csharp
    /// <summary>The entity's <c>events.exclude</c>: carried by a rename, and blocking a removal (apply refuses a name that is no field).</summary>
    private static void EventExclusions(JsonObject declared, Site at)
    {
        if (declared["events"]?["exclude"] is JsonArray excluded && excluded.Any(name => Is(name, at.Field)))
        {
            Replace(excluded, at);
            at.Add($"{at.Entity}.events.exclude", blocks: true);
        }
    }
```

(Add the place to the class remarks' list of places.)

`FieldEditor.razor.cs`: add `private bool _excludedFromEvents;`; in `OnParametersSet`, inside the `IsEditing && _prefilled != Editing` branch after `_facets = FieldFacets.Prefill(...)`: `_excludedFromEvents = Copy?.IsExcludedFromEvents(Entity, Editing!) is true;` and in the `else if (!IsEditing)` branch `_excludedFromEvents = false;`; `Fingerprint()` appends `_excludedFromEvents` (`string.Join('|', _facets.Name, …, _excludedFromEvents)`); `StageAsync` raises `new NewField(_facets.Name, facets) { KeepOpen = keepOpen, ExcludedFromEvents = _excludedFromEvents }`; `NewField` gains:

```csharp
        /// <summary>Whether the operator left this field out of the entity's event images.</summary>
        /// <remarks>Internal for <see cref="KeepOpen"/>'s reason: it passes between two screens of this assembly.</remarks>
        internal bool ExcludedFromEvents { get; init; }
```

`FieldEditor.razor` — after the *Constraints* `a-field` block:

```razor
        <div class="a-field">
            <span class="a-label" id="field-events">Events</span>
            <div class="a-row" role="group" aria-labelledby="field-events">
                <MudCheckBox T="bool" Value="_excludedFromEvents" ValueChanged="on => _excludedFromEvents = on"
                             Label="exclude from event images" data-testid="field-exclude-events" />
            </div>
            <span class="a-hint">
                Left out of <code class="a-mono">record</code> and <code class="a-mono">old_record</code> in every event
                this entity emits — for personal data or large columns. <code class="a-mono">changed</code> still names it.
            </span>
        </div>
```

`Entity.razor.cs` `AddFieldAsync` — after the `SaveField` `if/else if` block add `Copy.ExcludeFromEvents(EntityName, added.Name, added.ExcludedFromEvents);`.

- [ ] **Step 4: Run unit tests**

`dotnet format whitespace …`; `dotnet test --project test/MMLib.Alvo.Admin.Tests --filter-class '*WorkingCopyEventExclusionTests' --filter-class '*WorkingCopyFieldReferenceTests' --filter-class '*PublicApiApprovalTests' --filter-class '*StylesheetHygieneTests' --filter-class '*EndToEndSelectorTests'` — PASS (Admin PublicApi unchanged; if `EndToEndSelectorTests` requires every `data-testid` to be used by a scenario, Step 5 adds the use).

- [ ] **Step 5: Admin e2e — the toggle persists through an apply (acceptance 8)**

`test/MMLib.Alvo.Admin.Tests.EndToEnd/EventExclusionScenarios.cs`:

```csharp
using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>The field editor's <em>Exclude from event images</em> survives an apply (spec 2026-10-14 HX-01 acceptance 8).</summary>
/// <remarks>Its own world, because it applies.</remarks>
/// <param name="world">The running host and browser.</param>
public sealed class EventExclusionScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private const string Toggle = "exclude from event images";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task Excluding_a_field_from_event_images_persists_through_an_apply()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("edit-field-name").ClickAsync();
        var sheet = session.Page.GetByTestId("field-sheet");
        await sheet.GetByRole(AriaRole.Checkbox, new() { Name = Toggle, Exact = true }).CheckAsync();
        await session.Page.GetByTestId("field-save").ClickAsync();

        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", "Keep region names out of events");
        await session.Button("Apply these changes").ClickAsync();
        await session.SnackbarAsync("Applied as revision");

        await session.GoAsync("/schema/regions");
        await session.Page.GetByTestId("edit-field-name").ClickAsync();
        (await session.Page.GetByTestId("field-sheet").GetByRole(AriaRole.Checkbox, new() { Name = Toggle, Exact = true }).IsCheckedAsync())
            .ShouldBeTrue("the applied descriptor's events.exclude must be read back into the editor");
        session.AssertConsoleClean();
    }
}
```

Run: `scripts/test-admin-e2e` (not in a ring; starts the real host + browser). Expected: PASS, including the updated `TemplateDeclarationScenarios` from Task 12.

- [ ] **Step 6: ring0 and commit**

```bash
git add src/MMLib.Alvo.Admin/Components/Schema/WorkingCopy.Events.cs src/MMLib.Alvo.Admin/Components/Schema/FieldReferences.cs src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor src/MMLib.Alvo.Admin/Components/Schema/FieldEditor.razor.cs src/MMLib.Alvo.Admin/Components/Schema/Entity.razor.cs test/MMLib.Alvo.Admin.Tests/Schema/WorkingCopyEventExclusionTests.cs test/MMLib.Alvo.Admin.Tests.EndToEnd/EventExclusionScenarios.cs
git commit -m "feat(admin): exclude a field from event images in the field editor"
```

---

## Task 17: Docs, architecture doc, CHANGELOG, PLAN, spec amendment

**Files:**
- Create: `website/src/content/docs/extending/index.mdx`, `extending/where-logic-goes.mdx`, `extending/event-envelope.mdx`; `website/src/snippets/extending/event-envelope/01-exclude.alvo.json`
- Modify: `website/astro.config.mjs` (sidebar), `website/src/content/docs/guides/after-hooks-and-webhooks.mdx`, `guides/multi-tenancy.mdx`
- Modify: `docs/architecture/events.md`, `CHANGELOG.md`, `docs/PLAN.md`, `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`

- [ ] **Step 1: Snippet (validated by `Docs_snippet_validates`)**

`website/src/snippets/extending/event-envelope/01-exclude.alvo.json`:

```json
{
  "$schema": "https://alvo.dev/schema/v1/project.json",
  "apiVersion": "alvo.dev/v1",
  "name": "help-desk",
  "auth": { "roles": ["agent"] },
  "tenancy": { "enabled": true },
  "webhooks": {
    "endpoints": {
      "ops-board": { "url": "https://ops.example.com/hooks/alvo", "secretRef": "ops-board-signing-key" }
    }
  },
  "entities": {
    "tickets": {
      "audit": true,
      "fields": {
        "title": { "type": "string", "required": true, "maxLength": 120 },
        "status": { "type": "enum", "values": ["open", "closed"], "default": "open" },
        "reporter_phone": { "type": "string", "maxLength": 40 }
      },
      "events": { "exclude": ["reporter_phone"] },
      "rules": {
        "list": "'authenticated' in @user.roles",
        "get": "'authenticated' in @user.roles",
        "create": "'agent' in @user.roles",
        "update": "'agent' in @user.roles"
      },
      "hooks": {
        "afterUpdate": [
          {
            "condition": "changed(status) && new.status == 'closed' && !('admin' in @user.roles)",
            "action": { "type": "webhook", "endpoint": "ops-board" }
          }
        ]
      }
    }
  }
}
```

Run: `dotnet test --project test/MMLib.Alvo.Schema.Tests --filter-class '*ExamplesTests'` — PASS. (If `tenancy.enabled` requires more keys per the schema, drop the `tenancy` block; the page's prose then says the snippet's entity is global.)

- [ ] **Step 2: Pages**

`website/src/content/docs/extending/index.mdx`:

```mdx
---
title: Extending in code
description: When the descriptor cannot say it, write C# in your host — through the same pipeline and guarantees the descriptor's own hooks use. What exists today, and what lands next.
sidebar:
  order: 0
---

Alvo's descriptor covers most backend logic: rules, computed fields, rollups, before- and after-hooks. When it cannot
express something, an embedded host writes C# — and that code joins the **same write pipeline, events and guarantees**
the descriptor's hooks use, and shows up in the dashboard as metadata.

Start with [Where logic goes](/extending/where-logic-goes/): the first question is not *how*, but *where*.

## What exists in this build

- [Custom CEL functions](/guides/custom-cel-functions/) — pure, synchronous, in the write's transaction.
- [Call Alvo from your endpoints](/guides/call-from-endpoints/) — `IAlvoData` and `IAlvoEvents.PublishAsync`.
- [The event envelope](/extending/event-envelope/) — what every event carries: who acted, in which tenant and flow,
  and which fields it leaves out.

## What lands next

Event handlers, write hooks in three phases, commands, timers and schedules arrive one release at a time; each page
appears here when its feature ships. The design is public:
[host extensibility](https://github.com/Burgyn/MMLib.Alvo/issues/370).

## One limit already enforced

Every write and every published event counts how deep it sits in one causation chain. Past
[`Alvo:Extensions:MaxDepth`](/reference/configuration/) (default 8) it is refused with
[`508 chain-depth-exceeded`](/reference/problem-types/#chain-depth-exceeded), which is never worth retrying.
```

`website/src/content/docs/extending/where-logic-goes.mdx` — the approved page's decision table, with a *Today* column so nothing unbuilt is presented as available:

```mdx
---
title: Where logic goes
description: Pick the first row that fits — descriptor first, then host code, each at the stage where its guarantees hold.
sidebar:
  order: 1
---

Read from the top and stop at the first row that fits. A row marked *planned* names the mechanism the design commits to;
until it ships, use the row above it or an after-hook webhook.

| Situation | Where | Why | Today |
|---|---|---|---|
| A condition or a calculation over one row; access; whether a field is visible | descriptor: `rules`, before-hook, `computed` | Compiled to SQL, shown by the policy simulator, written by an agent on its own. | available |
| CEL lacks a function (VAT, working hours) | `AddCelFunction` | A pure calculation: synchronous, in the transaction, no network. | available |
| Integrity across rows: stock, capacity, numbering, overlaps | descriptor: `CHECK`, `sequence`, `exclude` | The database enforces it atomically on every engine; code would be slower and race. | planned |
| Validation needs an external service (VIES, fraud, billing) | `IValidateWriteHook` | Before the transaction, with network and a timeout. Not atomic, so nothing integrity depends on. | planned |
| It must happen atomically with the write and depends on other rows | `IBeforeWriteHook` | The same transaction and locks; refuses as 409/422. No network. | planned |
| Atomic, but needs the id or the result of the write | `IAfterWriteHook` | Audit rows, children, a first sprint. Can still cancel the whole write. | planned |
| A side effect after a successful write: mail, Slack, ERP, payment, cache | `IAlvoEventHandler` | After the commit, retried on failure, never undoes the write. | planned — use an after-hook `webhook` |
| The effect has several steps (payment → invoice → ERP) | handler + `IAlvoSteps` | Each step runs once; a retry continues where it failed. | planned |
| The same effect must be visible and configurable in the descriptor | `IAlvoFunction` + `"type": "function"` | An agent wires it with a condition; the code is named and typed. | planned |
| Not CRUD, or an inbound webhook | `MapAlvoCommand` / minimal API | Your own route and contract; Alvo's rules and hooks still apply. A named command is visible to agents. | minimal API available |
| Hide a field, or add a derived one, on read | `IRecordEnricher` | Only hides or fills a declared virtual field; never rewrites rows. | planned |
| Rate limits, captcha, caching, headers, CORS | endpoint filter, convention | An HTTP concern; ASP.NET Core has the tool. | available |
| Something must happen at a time, or regularly | `IAlvoScheduler`, `AddSchedule` | Keyed timers and cron on the outbox, one run across replicas. | planned |
```

`website/src/content/docs/extending/event-envelope.mdx`:

```mdx
---
title: The event envelope
description: What every Alvo event carries — who acted and how, in which tenant and flow — and how to keep fields out of it.
sidebar:
  order: 2
---

import { Code } from '@astrojs/starlight/components';
import SourceExcerpt from '../../../components/SourceExcerpt.astro';
import envelope from '../../../../../test/MMLib.Alvo.Tests/CloudEventsConformanceTests.One_real_envelope_is_pinned_verbatim.verified.txt?raw';
import exclude from '../../../snippets/extending/event-envelope/01-exclude.alvo.json?raw';

Every write appends one [CloudEvents 1.0.2](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md)
event to the outbox, in the same transaction. After-hooks, webhooks and (soon) host handlers all read the same envelope.

<SourceExcerpt code={envelope} from="specversion" to="specversion" lang="json" title="An entity.vehicles.updated event" wrap />

## Who acted, and in which tenant

| Attribute | Holds |
|---|---|
| `authtype` | How the caller authenticated: `api_key`, `user` (a signed-in person), `system` (Alvo itself), `unauthenticated` or `unknown` — the CloudEvents authcontext values. |
| `authid` | The id of the user who acted; absent for an unauthenticated caller. |
| `authclaims` | The caller's roles at the moment of the write, as a JSON string: `{"roles":["agent","authenticated"]}`. Used by after-hook conditions; **never posted** in a default webhook body. |
| `tenantid` | The tenant the write was made in; absent on a global entity and for a system write with no tenant. |

An after-hook condition or template reads them as `@user.id`, `@user.roles` and `@tenant.id`. An event that does not
carry the value a condition reads — a global row's tenant, an event written before this release — selects no hook:
the comparison is never made against an empty value.

## Which flow it belongs to

| Attribute | Holds |
|---|---|
| `correlationid` | The id shared by one end-to-end flow: the request's W3C trace id, else the event's own id. |
| `causationid` | The event that caused this change, when one did. |
| `chaindepth` | How many hops deep the causation chain is; `0` for a caller acting directly. Past `Alvo:Extensions:MaxDepth` (8) the write is refused with `508 chain-depth-exceeded`. |
| `traceparent`, `tracestate` | The W3C trace context of the write, when one was active (the registered Distributed Tracing extension). |

## Keep fields out of the images

`data.record` and `data.old_record` hold the whole row — `hidden` fields included. `events.exclude` leaves named fields
out of both images, for personal data or large columns; `data.changed` still names them, so `changed(field)` keeps
working:

<Code code={exclude} lang="json" title="help-desk.alvo.json" mark={[20]} />

`id`, `tenant_id` and the framework-managed columns key the event and cannot be excluded. Apply refuses an after-hook
condition or template that reads an excluded field — it would read null on every event. In the dashboard, the field
editor's **Exclude from event images** sets the same list.

## Older envelopes

Events written before this release say `apikey` and `anon`; they are read as `api_key` and `unauthenticated`, and they
carry no `authclaims` or `tenantid`.
```

Sidebar (`website/astro.config.mjs`): rename the `'Extend in C#'` group to `'Extending in code'` and put first in its `items`:

```js
          page('Extending in code', 'extending'),
          page('Where logic goes', 'extending/where-logic-goes'),
          page('The event envelope', 'extending/event-envelope'),
```

`guides/after-hooks-and-webhooks.mdx`: the template bullet's placeholder list adds `@user.roles` (comma-joined roles) and `@tenant.id`; the condition bullet becomes "A condition reads `new.`, `old.`, `changed()`, `@user.id`, `@user.roles` and `@tenant.id` — the caller as at the event. `old.` on `afterCreate`, `new.` or a bare field on `afterDelete`, and `changed()` on either are refused at apply."; the envelope table's `authtype` row becomes "`api_key`, `user`, `system`, `unauthenticated` or `unknown`…", add rows for `authclaims` ("the caller's roles; not in the default webhook body"), `tenantid`, `traceparent`/`tracestate`, and change the `data.record` row to mention `events.exclude` with a link to `/extending/event-envelope/`.

`guides/multi-tenancy.mdx:137`: replace the bullet with "An after-hook's event carries the writer's `tenantid`, and a condition can compare against `@tenant.id`; the event queue still holds every tenant's complete rows with no retention ([#154](https://github.com/Burgyn/MMLib.Alvo/issues/154)). See [The event envelope](/extending/event-envelope/)."

- [ ] **Step 3: `docs/architecture/events.md`**

- The envelope sample (`:71-72`): `"authtype":"api_key"`, and add `"authclaims":"{\"roles\":[\"agent\",\"authenticated\"]}","tenantid":"…"` in the attribute order of `AlvoEventAttributes.Extensions`.
- The attribute table (`:102-121`): `chaindepth` → "the write's effective depth (`AlvoEventProvenance.Effective`); refused past `Alvo:Extensions:MaxDepth`"; `authtype` → "`api_key` \| `user` \| `system` \| `unauthenticated` \| `unknown`, from `AlvoContext.ActorKind`; legacy `apikey`/`anon` normalised on read"; `causationid` → "from effective provenance"; new rows `authclaims` (extension, `{"roles":[…]}`, post-1.0.2 authcontext, stripped from the default webhook body), `tenantid` (extension, Alvo's), `traceparent`/`tracestate` (extension, **registered in v1.0.2** — Distributed Tracing); `data.record`/`data.old_record` → "minus `events.exclude` fields".
- *Publishing a custom application event* `:980-983` ("It carries no tenant, and that quietly enlarges #153") → "It carries the caller's tenant as `tenantid`."
- *The wildcard ruling* `:1009-1022`: replace the paragraph "The first branch is unavailable…#153 owns exactly that." with "The envelope now carries `tenantid` (HX-01), but the scoped matcher and its adversarial cross-tenant suite are not built — **#372** owns both, and until then `*` stays refused."
- *What PR5b and F7 inherit* `:1125-1137`: replace the #153 bullet with "**`@tenant.id` and `@user.roles` resolve from the envelope** (`tenantid`, `authclaims`) since HX-01; a hook reading one that its envelope lacks is not selected." and the `AlvoContext and provenance` bullet with a pointer to spec 2026-10-14 §4.6 (`AlvoContext.Provenance`, `ActorKind`, `AlvoFlow`, one depth counter).
- Add a section **Chain depth** after *The dispatcher*: the limit, where it is enforced in HX-01 (EF write entry points, `InMemoryAlvoData`, `IAlvoEvents.PublishAsync`), the 508 mapping, and the stated limit that depth follows the execution context.
- Add to *Where the code lives*: `AlvoEnvelopeStamp`, `AfterImagePhase`, `AlvoChainDepthLimit`.

- [ ] **Step 4: CHANGELOG, PLAN, spec**

`CHANGELOG.md`, under `## [Unreleased]` → `### Added` (first entry):

```markdown
- **Event envelopes carry provenance** (HX-01, #378). Every event now carries `authclaims` (the caller's roles,
  `{"roles":[…]}`), `tenantid` (the write's tenant; absent for global rows and tenantless system writes), and
  `traceparent`/`tracestate` when a W3C trace is active; `chaindepth` and `causationid` come from the new
  `AlvoContext.Provenance`, merged with the framework's ambient flow by `AlvoEventProvenance.Effective`.
  After-hook conditions and templates resolve `@tenant.id` and `@user.roles` from the envelope (#153); a hook whose
  envelope lacks the value is not selected. An entity can leave fields out of its event images with
  `events.exclude` (descriptor, `EntitySchema.EventImageExclusions`, and the dashboard field editor's *Exclude from
  event images*); apply refuses an after-hook that reads an excluded field. A write or publish deeper than
  `Alvo:Extensions:MaxDepth` (default 8, new `AlvoExtensionOptions`) is refused with `AlvoChainDepthExceededException`,
  answered as `508` problem type `chain-depth-exceeded` with `"retryable": false`. Docs: *Extending in code*.
```

under `### Changed (breaking)` (first entry):

```markdown
- **`authtype` speaks the CloudEvents authcontext vocabulary** (HX-01). `AlvoEventAuthType.ApiKey` is now `api_key`
  (was `apikey`), `Unauthenticated` (`unauthenticated`) replaces the removed `Anonymous` (`anon`), and `User` (`user`)
  and `Unknown` (`unknown`) are new; a person signed in through `MMLib.Alvo.Identity` is now reported as `user`, not
  `apikey`. A webhook receiver or an `IOutboxStore` consumer comparing the old strings must update. Rows already in
  the outbox keep their bytes and are normalised when read. Code comparing `AlvoEventAuthType.Anonymous` no longer
  compiles — use `Unauthenticated`.
- **After-hook conditions refuse a row image their event lacks** (#325): `old.` in `afterCreate`, `new.` or a bare
  field in `afterDelete`, and `changed()` in either are refused at apply; such a condition used to pass every event.
  A descriptor already stored with one is refused the next time it is validated or applied: a standalone host whose
  `Alvo:DescriptorPath` file carries one refuses to start with the condition's JSON pointer and a fix, and a dashboard
  or Management API apply is refused the same way. Rewrite the condition with the image the point has.
- **`IAlvoData` has a seventh exception family**, `AlvoChainDepthExceededException`. An implementer of the port must
  refuse a write whose effective depth exceeds the limit (the provider contract suite now asserts it).
```

`docs/PLAN.md` — under the F7 bullet add:

```markdown
  **Host extensibility** — host C# in the write pipeline, events, commands, time and reads, as a linear stack
  HX-01…HX-19 ([epic #370](https://github.com/Burgyn/MMLib.Alvo/issues/370)), designed in
  `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`. HX-01 (envelope provenance) is the first PR.
```

Spec amendment (this plan's rulings) — in `docs/superpowers/specs/2026-10-14-host-extensibility-design.md`:
- §5, the `EntitySchema.EventImageExclusions` row: add "; descriptor model `EntityDescriptor.Events` + `EntityEvents { Exclude }` (Abstractions / Descriptor), init".
- §4.7 *HX-01 settled details*, append bullets:
  - "**508 mapping sites.** `ProblemResultFactory.GuardAsync` (where the Data API maps every `IAlvoData` family) and `AlvoExceptionHandler` (a refusal escaping an Alvo route) both answer through `ProblemResultFactory.ChainDepthExceeded`. A host's own route keeps its own 500, as for every other family (`FailedInsideAlvo`). The document carries `"retryable": false`; no other problem carries the member. The slug is reachable over HTTP today through an explicit `Provenance.Depth`."
  - "**Assistant awareness** of `events.exclude` and of `@tenant.id`/`@user.roles` in after-hooks (the `alvo-descriptor-*` skills) is HX-04's, with the catalog and the `alvo-descriptor-hooks` region it already owns (§4.13)."
  - "**Cross-tenant suite (HX-01 acceptance 6)** covers each tenant writing its own rows, with a mirror hook proving `@tenant.id` resolves; a tenantless system write cannot be added there, because the scoped entity's tenant guard refuses it before any event exists."
  - "**`events.exclude` items** use the field-name grammar `^[a-z][a-z0-9_]{0,62}$` (not `$defs/identifier`, whose `-` no field can hold)."
  - "**Templates**: `{{@tenant.id}}` renders `tenantid`; `{{@user.roles}}` renders the role names comma-joined in ordinal order; both render empty on an envelope without them, as `{{@user.id}}` does for an anonymous write."
  - "**`InMemoryAlvoData`** enforces the default limit (8); it has no configuration to read."

Run: `cd website && npm ci && npm run build` (DocsGen + Astro + link validation; `docs.yml` runs the same on the PR). Expected: build succeeds, no broken links.

- [ ] **Step 5: Commit**

```bash
git add website docs/architecture/events.md CHANGELOG.md docs/PLAN.md docs/superpowers/specs/2026-10-14-host-extensibility-design.md
git commit -m "docs: extending in code, the event envelope, and HX-01's record"
```

---

## Task 18: Security-core review, rings, pre-PR gates

- [ ] **Step 1: `alvo-security-core-review` checklist pass** — write the answers into the PR report's notes:
  - *SQL predicate never interpolated*: HX-01 adds no SQL; `events.exclude` filters a dictionary in memory after the read; the `tenantid` comparison runs in the CEL interpreter over typed values.
  - *Authorization in the SQL `WHERE`*: unchanged; after-hook selection is not authorization (it decides deliveries), and it now fails closed on absent `tenantid`/`authclaims` (Task 12 facts).
  - *Fail-fast compile*: #325 phase refusal and excluded-field reads refused at apply (Tasks 11, 13); `events.exclude` naming a non-field or a key column refused (Task 6).
  - *Cross-tenant isolation*: two-tenant adversarial fact on SQLite and PostgreSQL (Task 14); `tenantid` is the writer's tenant only on scoped entities (Task 7/8).
  - *Default-deny*: unchanged; `authclaims` stripped from default webhook bodies (Task 7); `AlvoFlow` setter confined by arch facts (Task 15).
  - *Dynamic entities*: not built (#41); nothing in HX-01 branches on storage.
  - *Before-hooks*: untouched (`IBeforeHookRunner` unchanged).
  - Close with: **mark this change `needs-deep-review`** (spec §6.2 lists HX-01).
- [ ] **Step 2: `/security-review`** — user-only command (memory *Review commands blocked*): dispatch a reviewer subagent with the security-review brief over `git diff main...HEAD` and label its report "substitute for /security-review". Fix findings, re-run ring0.
- [ ] **Step 3: Rings** — `scripts/test-ring0`, `scripts/test-ring1`, `scripts/test-ring2` (affected integration suites incl. both engines, API invariants, `scripts/lint-api`). `scripts/test-admin-e2e` once more. Expected: green.
- [ ] **Step 4: `/code-review medium`** (user-only → dispatch a reviewer subagent labelled as substitute), fix findings.
- [ ] **Step 5: `alvo-plan-guard`** — dispatch it last (read-only); then the `alvo-pr-report` skill builds the PR report; open the PR against `main` with the five-line pointer body carrying "Closes #378", "Closes #153" and "Closes #325" (one keyword per issue, each on its own line), and the `needs-deep-review` label. Verify the branch with `git branch --show-current` before pushing (memory *Subagents switch branches*).

---

## Acceptance criteria → tasks

| HX-01 criterion | Task(s) | Test |
|---|---|---|
| (1) `CloudEventsConformanceTests` pins the new envelope; oracle accepts every attribute | 4 | `Every_extension_name_is_one_the_cloudevents_sdk_itself_accepts`, `The_envelope_now_carries_…`, `One_real_envelope_is_pinned_verbatim` |
| (2) legacy `apikey`/`anon` normalised; host-built ctx → `api_key`; Identity → `user` | 4, 5 | `A_legacy_auth_type_reads_back_normalised`, `A_host_built_context_…_api_key`, `A_signed_in_person_is_reported_as_a_user…` |
| (3) `AlvoDataOutboxTests` both engines: `tenantid` scoped/global; exclusions | 7, 8 | `A_scoped_write_reports_its_tenant…`, `An_excluded_field_is_in_neither_image…` (SQLite + PostgreSQL) |
| (4) depth 9 → exception / 508 both engines, depth 8 accepted, non-retryable | 9, 10 | `Every_write_face_refuses_…`, `A_write_at_the_chain_limit_is_accepted`, `ChainDepthSlugAnsweredByACallerDeepInAChainAsync` (real Data API request), `A_chain_depth_refusal_is_508…`, `A_chain_depth_refusal_escaping_an_alvo_route_is_508_and_non_retryable` |
| (5) ambient beats explicit (unit) | 2 | `Explicit_depth_zero_under_an_ambient_nine_is_refused`, `…_eight_is_accepted` |
| (5a) `PublishAsync` at depth 9 refused | 9 | `Publishing_deeper_than_the_chain_limit_is_refused…` |
| (5b) provider-contract row: InMemory + both engines | 9 | `AlvoDataAdversarialTests` rows via InMemory/SQLite/PostgreSQL subclasses |
| (5c) apply refuses condition + `email.to` reading excluded field (pointer); `id`/`tenant_id` refused | 6, 13 | `EventImageExclusionTests` |
| (6) `new.tenant_id != @tenant.id` never fires across tenants (integration) | 14 | `A_cross_tenant_condition_never_fires…` (SQLite + PostgreSQL) |
| (7) #325 facts for `afterCreate`/`afterDelete` | 11 | `An_after_point_refuses_an_image_its_event_does_not_carry`, `GuidedConditionConformanceTests` |
| (8) Admin e2e: exclude toggle persists | 16 | `Excluding_a_field_from_event_images_persists_through_an_apply` |
| Scope: arch fact on `AlvoFlow` setters | 15 | `It_never_sets_the_ambient_flow` ×4 |
| Scope: #153 texts → N-2 (#372) | 12 | `The_wildcard_refusal_names_the_issue_that_owns_the_matcher_and_no_longer_the_envelope_gap` |
| Scope: `AlvoExtensionOptions`, PLAN line, docs, CHANGELOG | 3, 17 | `AlvoExtensionOptionsTests`, website build |
| Security-core checklist + deep review | 18 | — |
