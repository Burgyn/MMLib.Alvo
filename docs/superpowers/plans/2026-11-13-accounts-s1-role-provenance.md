# S1 — Role provenance and one credential reading Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A caller can carry *tenant roles* that count only inside the tenant-scoped entity's rules (and relation terms
into one), never on a global entity and never at Management; and the three places that read a credential read it one
way, with `Authorization: Bearer` belonging to Alvo only when a person resolver is registered. No user-visible feature;
every existing suite stays green.

**Architecture:** `AlvoContext.TenantRoles` is a second role set beside `Roles`. Nothing in the policy engine changes
shape: the split happens where `@user.roles` is *folded* — `SqlPredicateRenderer.RenderIn` and
`CelInterpreter.ResolveContext` — by the tenancy of the entity whose scope is being evaluated, through one internal
helper, `RoleProvenance`. The other readers of the role set (state-machine `roles`, Management `access`, the event
envelope) are decided one by one below. Credential reading moves into `CallerResolution.ReadAsync`, which all three
sites call.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs and authentication handlers (shared framework only), xUnit v3 on MTP,
Shouldly, NSubstitute, Verify (public-API baseline only), `Microsoft.AspNetCore.TestHost`.

**Spec:** `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md` §5.7, §6.2, §11 (S1
row), §16 (B1, B2, B3, B8). Master plan: `docs/superpowers/plans/2026-11-13-accounts-00-master.md` (cross-slice names are
fixed there: `AlvoContext.TenantRoles`, `CallerResolution`, person resolver key `"alvo-person"`). Prior art:
`docs/architecture/relation-rules.md` *Binding*; RFC 6750 §2.1/§3; RFC 7235 §2.1 (auth-scheme is case-insensitive).

## Decisions (read before Task 1)

- **D1 (amended 2026-11-13 after the Task 1+2 review):** the fold is keyed by the *path*, not one entity — tenant roles count only while every entity from the top-level decision down to the scope is scoped (`RenderScope`/`EvalState` carry the flag). The original text follows for history.
- **D1 — The split lives at the fold, keyed by the scope's entity.** The policy engine hands out trees; role literals
  fold per caller at render (`RelationBinder` remarks), and every render/evaluation level already knows its entity:
  `CompiledExpression.Entity` at the top, `relation.Related` inside a relation term (its body and its bound `list` rule
  both render in the related scope, `SqlPredicateRenderer.Relations.cs` `Into`/`AddScope`). So
  `RoleProvenance.Visible(caller, tenancy)` answers `Roles ∪ TenantRoles` only when `tenancy == Scoped`, the caller has
  a `Tenant`, and `TenantRoles` is non-empty; otherwise it returns `caller.Roles` itself (no allocation on any existing
  path). This is spec §6.2's "evaluation-time union, not a second compile". **Deviation from §11's wording** ("the split
  … in `PolicyEngine`, relation binding and the management evaluator"): `PolicyEngine` and `RelationBinder` change only
  in their remarks, because neither folds a role — putting the split there would need a per-caller decision cache, which
  is exactly what D2 avoids. The management evaluator does change (Task 4).
- **D2 — The relation cache key does not change, and that is correct.** `RelationBinder`'s key is (catalog, entity,
  operation, slot, depth, has-identity, has-tenant). A related decision's allow/deny never reads roles (no rule, tenant
  guard, required-context gate, depth); role nodes stay in the bound tree and fold per caller and per scope at render.
  A tenant-role caller and a caller without them therefore share a bound tree and never share a fold. Task 2 pins it
  from a warm cache in both orders. `PolicyDecision` carries no caller, and field masks are resolved per call.
- **D3 — Every reader of the role set, decided:**

  | Site | Role set | Why |
  |---|---|---|
  | rule `USING`/`WITH CHECK` (SQL and the in-memory remainder of a write probe), `hidden`/`readOnly` masks | by the expression's entity (D1) | spec §6.2 |
  | relation term body and its bound related `list` rule | only while **every** entity on the path is scoped (D1, amended after the Task 1+2 review) | spec §6.2 |
  | lookup checks | n/a | a check may not read `@user` (relation-rules.md) |
  | before-hooks, write extensions, lifecycle `when`, timer `when`, entity-bound command access | by the compiled entity (D1, via the interpreter) | they are that entity's rules |
  | state-machine transition `roles` list | by the entity's tenancy (Task 3) | a declared role gate of the entity, read outside CEL |
  | unbound command access, unbound handler/hook conditions (`<command>`, `handler.Id` schemas) | project only | compiled against a tenancy-less schema; not a rule of a scoped entity |
  | Management `access` levels | project only (Task 4) | `AccessCatalogBuilder.ProjectScope` has no tenancy, **and** the evaluator strips `TenantRoles` before a host-replaceable `IPredicateEvaluator` sees the context |
  | after-hooks, event handlers, descriptor actions (post-commit) | by the condition's entity, over the envelope's recorded actor (Task 5) | the envelope must carry what the write saw |
  | webhook templates' caller roles (`AlvoTemplate`) | project only, unchanged | a template renders the published `roles` claim; widening it is not this slice's call |
  | Management policy simulator (`ManagementSimulatedCaller`) | unchanged (no tenant roles) | a tenant-role input is public API; S3 decides it |
  | `AlvoContext.HasRole` | project only, unchanged | documented: a tenant role counts only where a scoped entity's rule reads it |

- **D4 — `TenantRoles` is `public { get; init; }`.** Justified against *public is the contract*: the public ports
  `IPredicateRenderer` and `IPredicateEvaluator` must be able to read it to fold `@user.roles` correctly, and a context is
  a host-constructible value whose every other member is `public init` — setting `TenantRoles` grants strictly less than
  setting `Roles` already can. The init refuses `null` and any built-in role (`anon`, `authenticated`, `admin`): spec
  §6.2 says built-ins are never tenant roles, so a buggy membership resolution cannot mint `admin` into a scoped rule.
  The Abstractions public-API baseline grows by exactly this one line.
- **D5 — The envelope records tenant roles as an `authclaims` member `tenantRoles`, written only when non-empty**, so
  no stored envelope, CloudEvents payload or snapshot moves for any existing caller. `EventActor` keeps the still-declared
  ones (built-ins dropped, empty allowed — unlike `roles`, which falls back to `anon`).
- **D6 — The person resolver key.** `internal const string PersonResolverKey = "alvo-person"` on `CallerResolution`
  (no public growth; S2 decides whether `MMLib.Alvo.Accounts` publishes it, as `AlvoIdentity.ResolverKey` is). "Registered"
  is `IServiceProviderIsKeyedService.IsKeyedService(typeof(IAlvoContextResolver), key)`, read once; a container without
  that service counts as *not registered* — today's behaviour, fail-safe. The instance is resolved per request from
  `HttpContext.RequestServices`, so a scoped person resolver works (the keyed-scoped `alvo-identity` precedent).
- **D7 — The reading rules (spec §5.7, plus what it leaves open):**
  - Data API, commands and `WithAlvoCaller` endpoints share one surface (`CredentialSurface.Data`); Management is
    `CredentialSurface.Management` and **never reads `Authorization`**.
  - `Authorization` is read only when a person resolver is registered **and** `Alvo:Auth:HeaderName` is not
    `Authorization` (case-insensitive). When the API-key header *is* `Authorization` (allowed today, data-api.md
    §Requiring a JSON `Content-Type`), the API-key channel owns it exactly as before; S2's startup check should refuse
    that combination.
  - The scheme is the text before the first space, compared ordinal-ignore-case (RFC 7235 §2.1). A non-Bearer scheme is
    ignored. A Bearer value whose token is empty or contains whitespace, or more than one `Authorization` field line
    when any of them is Bearer, is `MalformedBearer` (401) — the repeated-header rule `Presented` already applies to
    the API key, never "take the first".
  - API key **and** an Alvo-owned Bearer → `Ambiguous` (401), resolving neither.
  - 401 shapes: a Bearer the resolver refuses → `WWW-Authenticate: Bearer error="invalid_token"`; `MalformedBearer` →
    `Bearer error="invalid_request"`; `Ambiguous` → both challenges. **Deviation:** RFC 6750 §3.1 answers
    `invalid_request` with 400; Alvo keeps every *presented-but-unusable* credential on 401, the one diagnosis the three
    sites already give (`DataApiAuthTests`) and agents branch on. Problem type stays `unauthenticated`.
- **D8 — Not in S1, recorded:** Management's refusal of personal and service tokens needs `ApiKeyRecord.Kind` (S5).
  Every apply refusal of spec §3.1 — including "an `assignableRoles` role named in `access`" — needs
  `tenancy.selfService`, i.e. S3. A Bearer security scheme in the OpenAPI document is S2's (no pin moves here). The
  person principal's scopes are S2's call; the test double issues `*:read`/`*:write`, because `ScopeGate` denies an
  empty scope set.
- **D9 — The "host with its own Bearer" fact uses a hand-rolled `AuthenticationHandler`** on a scheme named `Bearer`,
  from the shared framework, not the `JwtBearer` package (no new dependency, no license review). The property under
  test is that Alvo does not read the header, not how the host validates it.

## Global Constraints

- English in everything committed. CRLF + BOM in every `.cs` (re-normalise after any Bash/Python edit). Short
  single-purpose methods (~25-line ceiling); no inline comments by default (`alvo-dotnet-conventions`).
- Public API is the contract: every new type and member is `internal`, except `AlvoContext.TenantRoles` (D4). Only
  `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt` may move, by one line. No other
  `*.verified.*` may move; the Stop hook will demand `alvo-snapshot-judge` and the grown-baseline justification.
- Default-deny: an absent person resolver, an unknown container, a malformed header — each falls to today's behaviour
  or to 401, never to a wider caller.
- No secret in a log or `ToString`: the bearer token is never logged, never echoed in a problem `detail`.
- No new project or package. `IAlvoContextResolver` is unchanged.
- **Test discipline:** inner loop = the affected project filtered to the class,
  `dotnet test --project <proj> -- --filter-class '*<Class>*'`; ring0 only through the pre-commit hook; ring1 and ring2
  once, in Task 10. Branch: create `accounts/s1-role-provenance` from `accounts/s0-scoped-ref-tenant` before Task 1
  (`git switch -c accounts/s1-role-provenance`); never push to `main`; the controller opens the PR.

## Review Focus

1. **A host that already uses `Authorization` for its own scheme (Bearer or Basic), no person resolver** — every Alvo
   route answers exactly as before: anonymous or by API key, never 401; the host's own endpoint still sees its user.
   Pinned by Task 7.
2. **`Alvo:Auth:HeaderName = Authorization` with a person resolver registered** — the API-key channel keeps the header;
   the person resolver is never called. Pinned by Task 6 (reading fact) and Task 7 (HTTP fact).
3. **A warm relation cache** — a tenant-role caller resolved first, a caller without tenant roles second (and the
   reverse), over a scoped outer entity with relations into a scoped and a global entity: the second caller's fold never
   reflects the first's roles, and the global relation never admits a tenant role. Pinned by Task 2.
4. **A tenant role named in a global entity's rule or in an `access` level** — the global rule never admits it, on every
   engine and through a relation; no Management level matches it, even in-process behind a host's evaluator. Pinned by
   Tasks 2, 4 and 8 (the apply refusal is S3's).
5. **A malformed principal: `TenantRoles` set but no `Tenant`** — tenant roles count nowhere. Pinned by Task 2.

---

### Task 1: `AlvoContext.TenantRoles`

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Identity/AlvoContext.cs`
- Test: `test/MMLib.Alvo.Abstractions.Tests/Identity/AlvoContextTests.cs`
- Baseline: `test/MMLib.Alvo.Abstractions.Tests/PublicApi.MMLib.Alvo.Abstractions.verified.txt`

**Interfaces:**
- Produces: `public IReadOnlySet<Role> TenantRoles { get; init; }` — default an empty set (one shared static instance);
  `init` throws `ArgumentNullException` for `null` and `ArgumentException` (param `TenantRoles`) for a set containing
  `Role.Anon`, `Role.Authenticated` or `Role.Admin`, with the message
  `"A tenant role is never a built-in role; anon, authenticated and admin are project roles only."`.
  `Anonymous` and `System(...)` carry it empty. XML docs say what D1/D3 say in two sentences, and `HasRole`'s doc gains
  "project roles only; a tenant role counts only where a scoped entity's rule reads `@user.roles`".

- [ ] **Step 1: Write the failing facts** in `AlvoContextTests`:

```csharp
[Fact]
public void A_context_carries_no_tenant_roles_unless_given_some()
{
    AlvoContext.Anonymous.TenantRoles.ShouldBeEmpty();
    AlvoContext.System(TenantId.New()).TenantRoles.ShouldBeEmpty();
    new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } }.TenantRoles.ShouldBeEmpty();
}

[Theory]
[InlineData("anon"), InlineData("authenticated"), InlineData("admin")]
public void A_built_in_role_is_never_a_tenant_role(string builtIn)
// RoleCatalog.BuiltInOnly.Resolve([builtIn]) as the TenantRoles of a new context → ArgumentException, ParamName "TenantRoles";
// the same through `AlvoContext.Anonymous with { TenantRoles = … }`.

[Fact]
public void Null_tenant_roles_are_refused() // ArgumentNullException

[Fact]
public void A_with_copy_keeps_the_tenant_roles()
// var dispatcher = RoleCatalog.Create(["dispatcher"]).Resolve(["dispatcher"]);
// (context with { Tenant = TenantId.New() }).TenantRoles.ShouldBeSameAs(dispatcher); HasRole(dispatcher role) is false.
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-class '*AlvoContextTests*'`
Expected: FAIL — compile error, `TenantRoles` does not exist.

- [ ] **Step 3: Implement** the property (backing field + validating `init`, the `Roles` pattern).

- [ ] **Step 4: Run to see them pass** — the Step 2 command; Expected: PASS.

- [ ] **Step 5: Accept the public-API baseline**

Run: `dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-class '*PublicApiApprovalTests*'`
Expected: FAIL with a `.received.txt` adding exactly
`public System.Collections.Generic.IReadOnlySet<MMLib.Alvo.Role> TenantRoles { get; init; }` under `AlvoContext`, nothing
else. Move it over the `.verified.txt`, rerun → PASS. Put D4's justification in the commit body; dispatch
`alvo-snapshot-judge` when the Stop hook asks.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo.Abstractions/Identity/AlvoContext.cs test/MMLib.Alvo.Abstractions.Tests
git commit -m "feat(abstractions): AlvoContext carries the tenant roles of its tenant"
```

### Task 2: `@user.roles` folds by the scope's tenancy (rules, relation terms, masks, conditions)

**Files:**
- Create: `src/MMLib.Alvo/Rules/Internal/RoleProvenance.cs`
- Modify: `src/MMLib.Alvo/Expressions/Internal/SqlPredicateRenderer.cs` (`RenderIn`, ~384–405)
- Modify: `src/MMLib.Alvo/Expressions/Internal/CelInterpreter.cs` (`EvalState` ~886, `ResolveContext` ~422, the five
  entry points that build an `EvalState`)
- Modify (remarks only): `src/MMLib.Alvo/Rules/Internal/RelationBinder.cs` — D2's sentence: "tenant roles are not in the
  key for the same reason; the fold reads them per scope"
- Modify: `test/MMLib.Alvo.Tests/Expressions/RelationRenderWorld.cs` — `internal static RelationRenderWorld Over(string json)` over the existing private constructor
- Test (new): `test/MMLib.Alvo.Tests/Rules/RoleProvenanceTests.cs`, `test/MMLib.Alvo.Tests/Expressions/TenantRoleFoldTests.cs`

**Interfaces:**
- Consumes: `AlvoContext.TenantRoles` (Task 1).
- Produces: `internal static class RoleProvenance` with
  `static IReadOnlySet<Role> Visible(AlvoContext caller, TenancyMode? tenancy)` (D1; returns `caller.Roles` by reference
  unless all three conditions hold). Tasks 3–5 call it.
- `CelInterpreter.EvalState` gains `TenancyMode? Tenancy`, set from `expression.Entity.Tenancy` at every entry point that
  passes a context; `ResolveContext(CelContextRef, in EvalState)` answers `UserRoles` from `RoleProvenance.Visible`.
- `RenderIn` folds over `RoleProvenance.Visible(caller, scope.Entity.Tenancy)` in both its branches (literal and
  field-operand `IN (...)`).

- [ ] **Step 1: Write the failing facts.** Fixtures: `dispatcher = RoleCatalog.Create(["dispatcher"]).Resolve(["dispatcher"])`;
  `person = CelFixtures.Alice with { Roles = {Authenticated}, TenantRoles = dispatcher }`;
  `plain = CelFixtures.Alice with { Roles = {Authenticated} }`; `projectDispatcher = plain with { Roles = {Authenticated} ∪ dispatcher }`.

  `RoleProvenanceTests`:

```csharp
[Fact] public void A_scoped_entity_sees_project_and_tenant_roles()
// Visible(person, Scoped) == {authenticated, dispatcher}
[Theory, InlineData(TenancyMode.Global), InlineData(null)]
public void A_global_or_untenanted_scope_sees_project_roles_only(TenancyMode? tenancy)
// Visible(person, tenancy).ShouldBeSameAs(person.Roles)
[Fact] public void Tenant_roles_without_a_tenant_count_nowhere()            // Review Focus 5
// Visible(person with { Tenant = null }, Scoped).ShouldBeSameAs(person.Roles)
[Fact] public void A_caller_without_tenant_roles_allocates_nothing()
// Visible(plain, Scoped).ShouldBeSameAs(plain.Roles)
```

  `TenantRoleFoldTests` (renderer and interpreter must agree on every row):

```csharp
public static TheoryData<TenancyMode?, bool> Tenancies() => new() { { TenancyMode.Scoped, true }, { TenancyMode.Global, false }, { null, false } };

[Theory, MemberData(nameof(Tenancies))]
public void A_role_literal_folds_by_the_entity_the_rule_belongs_to(TenancyMode? tenancy, bool admitted)
{
    var rule = CelFixtures.Compiler.Compile("'dispatcher' in @user.roles", CelProfile.Rule, CelFixtures.Orders with { Tenancy = tenancy }).Expression!;
    new SqlPredicateRenderer().Render(rule, person, new TestFieldSqlRenderer(), "p").Sql.ShouldBe(admitted ? "TRUE" : "FALSE");
    CelInterpreter.EvaluatePredicate(rule, AlvoRecord.Empty, null, person).ShouldBe(admitted);
}

[Theory, MemberData(nameof(Tenancies))]
public void A_field_operand_in_the_role_set_folds_by_the_same_rule(TenancyMode? tenancy, bool admitted)
// "status in @user.roles": scoped → the IN list carries two parameters (authenticated, dispatcher); otherwise one.

[Theory, MemberData(nameof(Tenancies))]
public void A_mask_and_a_condition_fold_by_the_same_rule(TenancyMode? tenancy, bool admitted)
// CelInterpreter.EvaluateMask(Rule "!('dispatcher' in @user.roles)") is !admitted;
// CelInterpreter.EvaluateCondition(Condition "'dispatcher' in @user.roles", …, changed: []) is admitted.
```

  Relation facts over `RelationRenderWorld.Over(json)` with a descriptor built from `RelationDescriptors` helpers:
  `regions` **global** (field `code` string; `list: "'dispatcher' in @user.roles"`), `orders` **scoped** (field `title` string; same list rule), `jobs` **scoped**
  with refs `region_id → regions`, `order_id → orders`; `auth.roles: ["dispatcher"]`. Render through
  `IPredicateRenderer.Render(bound, caller, new TestFieldSqlRenderer(), "alvo_u", RelationSqlRenderTests.ReadOf("test", "jobs"))`.

```csharp
[Fact]
public void A_relation_into_a_scoped_entity_sees_the_tenant_role_and_one_into_a_global_entity_does_not()
// person: "order_id.title == 'x'" renders an EXISTS whose related list fold is TRUE;
//         "region_id.code == 'N'" renders an EXISTS whose related list fold is FALSE.
// projectDispatcher: both TRUE (the rule does admit dispatcher at all).

[Theory, InlineData(true), InlineData(false)]
public void A_warm_cache_never_lends_one_caller_s_fold_to_another(bool personFirst)   // Review Focus 3
// Bind and render "order_id.title == 'x' && region_id.code == 'N'" for person and plain in the given order, same world:
// plain's SQL never folds dispatcher TRUE; person's folds TRUE inside the orders EXISTS and FALSE inside the regions EXISTS;
// both bound trees are the same instance (D2: shared tree, separate fold).
```

  Assert on the fold, not on whole SQL text: the related list term renders as the literal `TRUE`/`FALSE` predicate inside
  its `EXISTS` (find it as `RelationSqlRenderTests` does with its `Markers`/substring checks).

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*RoleProvenanceTests*' --filter-class '*TenantRoleFoldTests*'`
Expected: FAIL — `RoleProvenance` does not exist.

- [ ] **Step 3: Implement** `RoleProvenance.Visible`, the `EvalState.Tenancy` field, and the two fold sites. Nothing
  else in the renderer or interpreter changes; `RoleMembership.RequireUserRolesOperand` stays first in both.

- [ ] **Step 4: Run to see them pass, and that nothing near them moved**

Run: the Step 2 command, then
`dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*PolicyEngineRelationTests*' --filter-class '*RelationSqlRenderTests*' --filter-class '*RoleMembershipTests*' --filter-class '*SqlPredicateRendererTests*' --filter-class '*SqlPredicateRendererInvariantTests*'`
Expected: PASS, no `.received.*` written.

- [ ] **Step 5: Commit**

```bash
git add src/MMLib.Alvo/Rules/Internal src/MMLib.Alvo/Expressions/Internal test/MMLib.Alvo.Tests
git commit -m "feat(rules): @user.roles folds tenant roles only in a scoped entity's scope"
```

### Task 3: State-machine transition roles read the entity's set

**Files:**
- Modify: `src/MMLib.Alvo/Lifecycle/EntityLifecycle.cs:23-24` (`CompiledStateMachine` gains a trailing `TenancyMode? Tenancy = null`)
- Modify: `src/MMLib.Alvo/Lifecycle/LifecycleCompiler.cs:45` (passes `scope.Schema.Tenancy`)
- Modify: `src/MMLib.Alvo/Lifecycle/StateMachineGuard.cs:51`, `src/MMLib.Alvo/Lifecycle/TransitionSimulator.cs:44-47`
- Test: `test/MMLib.Alvo.Tests/Lifecycle/StateMachineGuardTests.cs`, `test/MMLib.Alvo.Tests/Lifecycle/TransitionSimulatorTests.cs`

**Interfaces:**
- Consumes: `RoleProvenance.Visible` (Task 2).
- `StateMachineGuard.Ensure` and `TransitionSimulator.Verdict` test `transition.Roles.Overlaps(RoleProvenance.Visible(caller, machine.Tenancy))`.
  The default `null` keeps a machine built without a tenancy on project roles (fail-closed).

- [ ] **Step 1: Write the failing facts** — in `StateMachineGuardTests`:
  `A_tenant_role_takes_a_role_gated_transition_of_a_scoped_entity` (machine `Tenancy = Scoped`, transition roles
  `{dispatcher}`, caller = Task 2's `person` → no exception) and
  `A_tenant_role_never_takes_a_role_gated_transition_of_a_global_entity` (`Tenancy = Global` → `AlvoAuthorizationException`);
  in `TransitionSimulatorTests`: `A_simulated_tenant_role_is_allowed_only_on_a_scoped_machine` (`Allowed` true / false).
  In `LifecycleCompilerTests`: `The_compiled_machine_carries_its_entity_s_tenancy`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*StateMachineGuardTests*' --filter-class '*TransitionSimulatorTests*' --filter-class '*LifecycleCompilerTests*'`; Expected: FAIL (compile: no `Tenancy` on the machine).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to see them pass** — same command; Expected: PASS.
- [ ] **Step 5: Commit** — `feat(lifecycle): a transition's roles count tenant roles only on a scoped entity`

### Task 4: Management `access` levels read project roles only

**Files:**
- Modify: `src/MMLib.Alvo/Management/Access/Internal/ManagementAccessEvaluator.cs` (`HighestMatch` or `Matches`)
- Test: `test/MMLib.Alvo.Tests/Management/ManagementAccessEvaluatorTests.cs`

**Interfaces:**
- `ManagementAccessEvaluator` evaluates every level against `context with { TenantRoles = <empty> }` (one private
  `static AlvoContext ProjectRolesOnly(AlvoContext context)`, returning `context` itself when already empty). Remarks
  gain spec §6.2's B1 sentence and D3's "two locks": `ProjectScope` has no tenancy, and the strip makes it hold for a host's
  own `IPredicateEvaluator` too.

- [ ] **Step 1: Write the failing facts** (the file's `Resolve`/`Levels` helpers):

```csharp
[Fact]
public void A_tenant_role_matches_no_access_level()
// caller: authenticated + TenantRoles {dispatcher}, Tenant set; Levels(admin: "'dispatcher' in @user.roles",
// developer: same, viewer: same) → ManagementLevel.None. Control: the same role as a project role → Admin.

[Fact]
public void A_host_evaluator_never_receives_tenant_roles()
// IPredicateEvaluator substitute returning true; resolve once; every received AlvoContext has TenantRoles empty
// and the original User, Roles and Tenant.
```

  Note: the first fact already passes after Task 2 (ProjectScope is tenancy-less); it is the regression pin. The second
  fails until this task.
- [ ] **Step 2: Run** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*ManagementAccessEvaluatorTests*'`; Expected: the host-evaluator fact FAILS.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — same command; Expected: PASS.
- [ ] **Step 5: Commit** — `feat(management): access levels never see a tenant role`

### Task 5: The event envelope carries the tenant roles a write saw

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Events/AlvoEventProvenance.cs` (`AuthClaimsOf` ~115; new `TenantRolesIn`)
- Modify: `src/MMLib.Alvo/Events/Internal/EnvelopeReading.cs:55-65` (`CallerOf`), `src/MMLib.Alvo/Events/Internal/EventActor.cs:19-36`
- Test: `test/MMLib.Alvo.Tests/Events/AuthClaimsTests.cs`, `test/MMLib.Alvo.Tests/Events/EventActorTests.cs`

**Interfaces:**
- `AuthClaimsOf` writes `"tenantRoles": [...]` (ordinal order) after `"roles"` **only when** `context.TenantRoles` is
  non-empty and `context.Tenant` is set; otherwise its output is byte-identical to today.
- `internal static IReadOnlyList<string>? TenantRolesIn(string? authClaims)` — same tolerance as `RolesIn` (foreign or
  damaged → `null`, never throws).
- `EnvelopeReading.CallerOf` sets `TenantRoles` from it, minus built-in names, only when the envelope names a tenant.
  `EventActor.Of` filters them to still-declared roles (empty allowed); an anonymous actor carries none.

- [ ] **Step 1: Write the failing facts:**
  `AuthClaimsTests.A_caller_without_tenant_roles_writes_exactly_today_s_claims` (string equality with
  `{"roles":["authenticated"]}`), `The_tenant_roles_round_trip_beside_the_roles`,
  `A_damaged_tenant_roles_member_reads_as_absent` (theory: `{"roles":[],"tenantRoles":"x"}`, `[1]`, a lone surrogate);
  `EventActorTests.The_actor_keeps_the_still_declared_tenant_roles_of_its_tenant`,
  `An_envelope_without_a_tenant_yields_no_tenant_roles`,
  `A_built_in_named_as_a_tenant_role_is_dropped_not_thrown`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AuthClaimsTests*' --filter-class '*EventActorTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — the Step 2 command, then
  `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*OutboxEventFactoryTests*'`;
  Expected: PASS, no snapshot moved.
- [ ] **Step 5: Commit** — `feat(events): the envelope records tenant roles beside the roles`

### Task 6: One credential reading for the three sites

**Files:**
- Modify: `src/MMLib.Alvo/Auth/Internal/CallerResolution.cs` (reading + admission; `CallerAdmission` moves here from `CallerGate.cs`)
- Create: `src/MMLib.Alvo/Auth/Internal/CallerResolvers.cs`, `src/MMLib.Alvo/Auth/Internal/PresentedCredential.cs`
- Modify: `src/MMLib.Alvo/Auth/Setup.cs` (`services.TryAddSingleton<CallerResolvers>()`)
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoContextFilter.cs` (filter + factory take `CallerResolvers`),
  `src/MMLib.Alvo/Api/Internal/CallerGate.cs`, `src/MMLib.Alvo/Management/Internal/ManagementCallerFilter.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` (two 401 factories)
- Test (new): `test/MMLib.Alvo.Api.Tests/CredentialReadingTests.cs`

**Interfaces:**
- `internal enum CredentialSurface { Data, Management }`
- `internal enum CredentialChannel { None, ApiKey, Bearer, Ambiguous, MalformedBearer }`
- `internal readonly record struct PresentedCredential(CredentialChannel Channel, string? Value)` — `Value` is the key or
  the bare token (no scheme), `null` for the other channels.
- `CallerResolution`:
  - `internal const string PersonResolverKey = "alvo-person";`
  - `internal static PresentedCredential Read(HttpRequest request, AlvoAuthOptions options, CredentialSurface surface, bool personRegistered)` — pure; D7 exactly.
  - `internal static ValueTask<CallerAdmission> AdmitAsync(HttpRequest request, CallerResolvers resolvers, AlvoAuthOptions options, CredentialSurface surface)` —
    `None` → `(null, null)`; `ApiKey` → today's `ResolveAsync` + `Unauthenticated(options.HeaderName)`; `Bearer` →
    `resolvers.Person(request.HttpContext).ResolveAsync(token, <tenant header>, RequestAborted)`, `null` →
    `BearerUnauthenticated("invalid_token")`; `MalformedBearer` → `BearerUnauthenticated("invalid_request")` without
    calling any resolver; `Ambiguous` → `AmbiguousCredential(options.HeaderName)` without calling any resolver.
  - `Presented` and `PublishingAsync` unchanged; the existing `ResolveAsync` becomes private to the file.
- `internal sealed class CallerResolvers(IAlvoContextResolver apiKeys, IServiceProvider services)` with
  `IAlvoContextResolver ApiKeys`, `bool HasPerson` (D6, read once), `IAlvoContextResolver Person(HttpContext http)`.
- `CallerAdmission(AlvoPrincipal? Principal, IResult? Refusal)` keeps its `Context` member. The three sites call
  `AdmitAsync` and keep their own after-steps: the Data filter and `CallerGate` the scope gate, Management nothing.
- `ProblemResultFactory`:
  - `internal static IResult BearerUnauthenticated(string error)` — 401, `unauthenticated`, challenge `Bearer error="<error>"`,
    detail `"The presented bearer token could not be used. Sign in again, or check that it has not expired or been revoked and that you belong to the tenant you requested."`
  - `internal static IResult AmbiguousCredential(string headerName)` — 401, `unauthenticated`, challenge
    `AlvoApiKey header="<headerName>", Bearer`, detail `"The request presents both <headerName> and a bearer token. Present one credential, never both."`

- [ ] **Step 1: Write the failing reading facts** over `new DefaultHttpContext().Request` with headers set by hand:

```csharp
public static TheoryData<CredentialSurface, bool, string?, string[], CredentialChannel, string?> Readings() => new()
{
    { Data, false, null, ["Bearer t"], None, null },                 // no person resolver: ignored
    { Data, false, "k", ["Bearer t"], ApiKey, "k" },
    { Data, true, null, ["Bearer t"], Bearer, "t" },
    { Data, true, null, ["bearer t"], Bearer, "t" },                 // RFC 7235 §2.1
    { Data, true, null, ["Basic dXNlcjpwYXNz"], None, null },        // other schemes: ignored
    { Data, true, "k", ["Basic dXNlcjpwYXNz"], ApiKey, "k" },
    { Data, true, "k", ["Bearer t"], Ambiguous, null },
    { Data, true, null, ["Bearer"], MalformedBearer, null },
    { Data, true, null, ["Bearer a b"], MalformedBearer, null },
    { Data, true, null, ["Bearer a", "Bearer b"], MalformedBearer, null },
    { Data, true, null, ["Basic x", "Bearer t"], MalformedBearer, null },
    { Data, true, null, [], None, null },
    { Management, true, null, ["Bearer t"], None, null },            // Management never reads Authorization
    { Management, true, "k", ["Bearer t"], ApiKey, "k" },
};

[Theory, MemberData(nameof(Readings))]
public void A_request_presents_exactly_one_reading(…)

[Fact]
public void An_api_key_header_named_authorization_keeps_the_header_from_the_person_channel()   // Review Focus 2
// options.HeaderName = "Authorization", personRegistered: true, Authorization "Bearer k" → (ApiKey, "Bearer k").

[Fact]
public void A_container_that_cannot_answer_keyed_questions_has_no_person_resolver()
// new CallerResolvers(apiKeys, <IServiceProvider whose GetService(typeof(IServiceProviderIsKeyedService)) is null>).HasPerson is false.
```

- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*CredentialReadingTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement** `Read`, `AdmitAsync`, `CallerResolvers`, the two problem factories; switch the three sites
  and `AlvoContextFilterFactory` to `CallerResolvers`; update `CallerResolution`'s class remarks (now three mechanics plus
  the reading table) and `ManagementCallerFilter`'s ("never reads `Authorization`; spec §5.7").
- [ ] **Step 4: Run it, and the suites of the three sites**
  `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*CredentialReadingTests*' --filter-class '*DataApiAuthTests*' --filter-class '*ManagementAccessTests*' --filter-class '*SqliteCommandTests*' --filter-class '*ProblemDetailsTests*'`
  Expected: PASS — the existing 401 copy and challenge for an API key are unchanged.
- [ ] **Step 5: Commit** — `feat(auth): one credential reading for the data, command and management filters`

### Task 7: Over HTTP — a host's own `Authorization` is untouched; the person channel is Alvo's only when registered

**Files:**
- Create: `test/_shared/api/TestPersonResolver.cs` (linked into both API test projects by the existing glob)
- Create: `test/MMLib.Alvo.Api.Tests/HostOwnAuthorizationTests.cs`, `test/MMLib.Alvo.Api.Tests/PersonCredentialTests.cs`

**Interfaces:**
- Produces (test-only): `internal sealed class TestPersonResolver : IAlvoContextResolver` with
  `internal const string Key = "alvo-person"` (spelled as a literal on purpose: it pins the master plan's fixed key);
  `TestPersonResolver(params (string Token, AlvoPrincipal Principal)[] people)`;
  `ConcurrentQueue<(string Presented, string? Tenant)> Calls`; `void Register(IServiceCollection services)` →
  `AddKeyedSingleton<IAlvoContextResolver>(Key, this)`; and
  `static AlvoPrincipal Person(string keyId, Guid? tenant, params string[] tenantRoles)` — `User` a fresh `UserId`,
  `Roles {authenticated}`, `ActorKind = AlvoActorKind.User`, scopes `*:read`/`*:write` (D8), tenant roles resolved
  through `RoleCatalog.Create(tenantRoles).Resolve(tenantRoles)`. Task 8 consumes all of it.
- `HostBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>` (private to `HostOwnAuthorizationTests`) on
  scheme `"Bearer"`: `Authorization: Bearer host-token` → a principal named `host-user`, anything else → `NoResult`.

- [ ] **Step 1: Write the facts.** World: `AlvoApiWorld.VehicleRegistryAsync([_admin], new AlvoApiWorldSetup(ConfigureServices: …, ConfigureApp: …, MapManagementApi: true))`.
  `HostOwnAuthorizationTests` (no person resolver; `ConfigureServices` adds the host scheme, `ConfigureApp` calls
  `UseAuthentication()` and maps `GET /host/me` answering the principal's name):

```csharp
[Theory]
[InlineData("Bearer host-token"), InlineData("Bearer not-a-token"), InlineData("Basic dXNlcjpwYXNz")]
public async Task A_host_authorization_header_leaves_every_alvo_route_as_it_was(string authorization)
// POST /api/owners with only that header → 403, type forbidden (the anonymous answer, never 401);
// the same plus _admin's key → 201;
// GET /management/info with only that header → the same status as with no header at all;
// POST /commands/… is not mapped in this world — the command surface is covered in PersonCredentialTests.

[Fact]
public async Task The_host_s_own_scheme_still_sees_its_user() // GET /host/me with "Bearer host-token" → "host-user"

[Fact]
public async Task A_keyed_resolver_under_another_key_does_not_claim_the_bearer_channel()
// services.AddKeyedSingleton<IAlvoContextResolver>("alvo-identity", <resolver that would admit anything>);
// POST /api/owners with "Bearer t" → 403 forbidden, and that resolver was never called.
```

  `PersonCredentialTests` (world: `AlvoApiWorld.FromDescriptorAsync("command-desk.alvo.json", [CommandWorld.Staff], …)`
  with `TestPersonResolver` registered through `ConfigureServices`, `MapManagementApi: true`, and `ConfigureApp` mapping
  `MapAlvoCommand("quote_stay", "/commands/quote", "'authenticated' in @user.roles", () => TypedResults.Ok())`;
  `"Bearer person-token"` → `TestPersonResolver.Person("person:p", tenant: null)`):

```csharp
[Fact] public async Task A_bearer_token_is_resolved_by_the_person_resolver_with_the_requested_tenant()
// GET /api/bookings with "Bearer person-token" and the tenant header "t-1" → 200; resolver.Calls == [("person-token", "t-1")];
// world.PublishedPrincipals contains the person's KeyId.
[Fact] public async Task A_bearer_token_the_resolver_refuses_is_401_with_a_bearer_challenge()
// "Bearer nobody" → 401, type unauthenticated, WWW-Authenticate contains "Bearer" and "invalid_token"; body never contains "nobody".
[Fact] public async Task A_malformed_bearer_is_401_and_reaches_no_resolver()      // "Bearer" alone; Calls empty; "invalid_request"
[Fact] public async Task An_api_key_and_a_bearer_token_together_are_401_and_neither_is_resolved()
// Staff key + "Bearer person-token" on GET /api/bookings → 401; Calls empty; WWW-Authenticate names both AlvoApiKey and Bearer;
// the same pair on POST /commands/quote → 401 (CallerGate).
[Fact] public async Task A_command_runs_as_the_person_a_bearer_token_names()     // POST /commands/quote with the bearer → 200
[Fact] public async Task The_management_api_never_reads_a_bearer_token()
// GET /management/info with "Bearer person-token" → the same status as with no credential; with the Staff key + bearer →
// the same status as with the key alone (never 401); Calls empty in both.
[Fact] public async Task An_api_key_header_named_authorization_keeps_the_header()  // Review Focus 2
// ConfigureServicesAfterAlvo: services.Configure<AlvoAuthOptions>(o => o.HeaderName = "Authorization") — the world then
// presents keys under that header; GET /api/bookings with the Staff key → 200; Calls empty.
[Fact] public async Task A_non_bearer_scheme_is_ignored_even_with_a_person_resolver()
// "Basic dXNlcjpwYXNz" → the same status as with no credential; Calls empty.
```

- [ ] **Step 2: Run them** — `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*HostOwnAuthorizationTests*' --filter-class '*PersonCredentialTests*'`.
  Expected: PASS (Task 6 built the behaviour). **Prove they can fail:** temporarily make `Read` treat
  `personRegistered` as always `true` → the host-header facts go red (401); restore. Then make `Management` read Bearer →
  the management fact goes red; restore. Record both red runs in the commit body.
- [ ] **Step 3: Commit** — `test(api): a host's own Authorization is untouched; Bearer is Alvo's only with a person resolver`

### Task 8: The evaluation split end to end, on every engine, and Management in-process

**Files:**
- Create: `test/_shared/api/descriptors/tenant-roles-desk.alvo.json`
- Create: `test/_shared/api/DataApiEngineTests.TenantRoles.cs` (partial; both engines inherit it)
- Create: `test/MMLib.Alvo.Api.Tests/TenantRoleManagementTests.cs`

**Interfaces:**
- Consumes: `TestPersonResolver` (Task 7); Tasks 2 and 4's behaviour. Produces nothing.
- Descriptor: `tenancy.enabled`; `auth.roles: ["dispatcher"]`; `access: { "viewer": "'dispatcher' in @user.roles" }`;
  `regions` **global** (`code` string ≤ 8, required); `work_orders` **scoped** (`title` string ≤ 80); `visits` **scoped**
  (`work_order_id` ref → `work_orders`); `stops` **scoped** (`region_id` ref → `regions`). Rules: `regions` and
  `work_orders` — `list`/`get`/`create` = `'dispatcher' in @user.roles`; `visits` — `list`/`get` =
  `work_order_id.title != ''`, `create` = `'dispatcher' in @user.roles`; `stops` — `list`/`get` = `region_id.code == 'N'`,
  `create` = `'dispatcher' in @user.roles`.
- Callers: `_projectDispatcher` = `TestApiKey("tr-dispatcher", ["dispatcher", "authenticated"], ["*:read", "*:write"], _acme)`;
  `"Bearer acme-dispatcher"` → `TestPersonResolver.Person("person:dispatcher", _acme, "dispatcher")`;
  `"Bearer acme-plain"` → `Person("person:plain", _acme)`.

- [ ] **Step 1: Write the facts** (each world via `AlvoApiWorld.FromDescriptorAsync("tenant-roles-desk.alvo.json", [_projectDispatcher], new AlvoApiWorldSetup(ConfigureServices: resolver.Register), Engine)`):

```csharp
[Fact] public async Task A_tenant_role_admits_the_rules_of_a_scoped_entity_on_every_engine()
// bearer acme-dispatcher: POST /api/work_orders → 201; GET → 1 item. bearer acme-plain: GET → 0 items, POST → 403.
[Fact] public async Task A_tenant_role_never_admits_the_rules_of_a_global_entity_on_every_engine()
// _projectDispatcher creates region "N" (201, the control); bearer acme-dispatcher: POST /api/regions → 403 forbidden,
// GET /api/regions → 0 items, GET /api/regions/{id} → 404.
[Fact] public async Task A_relation_term_sees_a_tenant_role_only_into_a_scoped_entity_on_every_engine()
// _projectDispatcher creates region N, a work order, a visit naming it and a stop naming the region;
// bearer acme-dispatcher: GET /api/visits → 1 item, GET /api/stops → 0 items; _projectDispatcher: GET /api/stops → 1 item.
```

  `TenantRoleManagementTests` (SQLite; `ConfigureApp` maps
  `GET /host/info` → `IAlvoManagement.GetInfoAsync`, answering 200, or 403 on `ManagementForbiddenException`,
  `.WithAlvoCaller()`):

```csharp
[Fact] public async Task A_tenant_role_never_reaches_a_management_access_level_in_process()
// bearer acme-dispatcher → 403; _projectDispatcher → 200 (the access level does admit dispatcher as a project role).
```

- [ ] **Step 2: Run them**

Run: `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*SqliteDataApiTests*' --filter-class '*TenantRoleManagementTests*'`
Run: `dotnet test --project test/MMLib.Alvo.Api.Tests.Integration -- --filter-class '*DataApiOnPostgresTests*'` (Docker)
Expected: PASS. **Prove they can fail:** make `RoleProvenance.Visible` return the union regardless of tenancy → the
global-entity, relation and (with Task 4's strip also reverted) management facts go red; restore. Record in the commit body.

- [ ] **Step 3: Commit** — `test(api): a tenant role reaches scoped rules only, on every engine and never at Management`

### Task 9: Say it where it is read

**Files:**
- Modify: `docs/architecture/relation-rules.md` — *Binding*: one paragraph — the fold reads `Roles ∪ TenantRoles` in a
  scoped entity's scope and `Roles` elsewhere; the cache key is unchanged and why (D2).
- Modify: `docs/architecture/data-api.md` — new section `## How a request becomes a caller: one credential reading`
  before *The status and `type`-slug catalogue*: spec §5.7's table, D7's extra rules, the RFC 6750 §3.1 deviation, and
  that S2 must refuse `HeaderName = Authorization` beside a person resolver. Update the `Authorization` bullet (~972) to
  point at it.
- Modify: `docs/architecture/management-api.md` — the caller section: never reads `Authorization`; levels read project
  roles only (D3 row).
- Modify: `CHANGELOG.md` `[Unreleased]` → *Added*: `AlvoContext.TenantRoles` (empty for every existing caller; counts only
  in scoped entities' rules); one line that the `Authorization` header is still ignored unless a person resolver is
  registered.

- [ ] **Step 1: Sweep for the claim, not the file**

Run: `grep -rn -i "@user.roles\|Authorization\` is\|AlvoContext.Roles\|one set" docs/architecture website/src/content plugins/alvo/skills --include='*.md' --include='*.mdx'`
Expected: every hit that says `@user.roles` is "the caller's roles" without qualification, in a contributor doc, gains the
scoped/global sentence; user-facing guides and skills are S6's and stay as written (tenant roles do not exist for users yet).

- [ ] **Step 2: Check the docs gate** — `scripts/check-brief-freshness`; Expected: OK.
- [ ] **Step 3: Commit** — `docs(rules): role provenance and the one credential reading`

### Task 10: Slice gate — ring1, ring2, security-core checklist

**Files:** none new (fixes only, if a gate finds something).

- [ ] **Step 1: ring1** — `scripts/test-ring1`; Expected: `[ring1] OK`. A moved `*.verified.*` other than Task 1's baseline
  is a defect in Tasks 2–6, not a baseline to accept.
- [ ] **Step 2: ring2** — `scripts/test-ring2`; Expected: exit 0 (integration, API invariants, Vacuum). Docker must be up.
- [ ] **Step 3: `alvo-security-core-review` checklist**, item by item, recorded for the PR report:
  SQL — role names still reach SQL only as parameters or folded constants (`RenderIn` unchanged in shape;
  `RelationNoInterpolationPropertyTests` green); authorization in the `WHERE` — unchanged, the fold is pre-SQL;
  fail-fast — no new descriptor surface (S3 owns the apply refusals); cross-tenant — a tenant role is honoured only with
  a tenant and only in a scoped scope, pinned two-engine by Task 8 and from a warm cache by Task 2; Management — two
  locks (D3), Task 4 and Task 8; credential reading — default-deny on every unknown (no resolver, no keyed-service
  support, malformed header), the ambiguous pair resolves neither (Task 7); secrets — no token in any `detail` or log
  (Task 7 asserts the body); dynamic entities — out of scope (no driver). Close with **mark this change
  `needs-deep-review`**.
- [ ] **Step 4: Hand back** to the controller (plan-guard, PR report, PR targeting `accounts/s0-scoped-ref-tenant`) — no
  push, no PR from this task.
