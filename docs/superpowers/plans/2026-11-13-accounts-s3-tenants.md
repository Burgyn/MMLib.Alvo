# S3 — Self-service tenants (levels 2–3, minus invitations) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A signed-in person gets a personal tenant (level 2), may create shared tenants and manage their members and
roles (level 3 without invitations), and picks the tenant a request acts in with `X-Alvo-Tenant` — the membership is
checked on every request in the same one store read as the session, on SQLite and PostgreSQL.

**Architecture:** A tenant registry (`{prefix}_tenants`, `{prefix}_tenant_members`) lives in the EF adapter behind an
Abstractions-internal port, `ITenantStore`, beside S2's `IAccountStore`; the personal tenant is written inside the
account store's sign-in and refresh transactions, and the person resolver's one read joins the membership onto the
session. The core gains what spec §11.1 puts there: `tenancy.selfService` (schema, model, apply refusals, the
global-write warning), the membership rules (one pure decision shared by the session read and the registry-reading
`IMembershipResolution`), the self-service routes and their person gate, the `create` rate limit, the conditional
default `IAlvoTenantCatalog`, and tenant-role hygiene for event actors. `MMLib.Alvo.Accounts` maps the routes beside its
own and passes the personal tenant into its grants.

**Tech Stack:** .NET 10, minimal APIs, EF Core relational connections (SQLite + PostgreSQL), xUnit v3 on MTP, Shouldly,
NSubstitute, Verify, `Microsoft.AspNetCore.TestHost`, `Microsoft.Extensions.TimeProvider.Testing`, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md` — read §3 (levels 2–3),
§3.1 (`tenancy.selfService`, the refusals, the warning), §4 (D1, D8), §5.7, §6.1–§6.6 (not invitations), §7.3, §11 (S3
row), §11.1, §14, §16. Master plan: `docs/superpowers/plans/2026-11-13-accounts-00-master.md` (fixed names: membership
resolution's signature, `_tenants` / `_tenant_members`, `tenancy.selfService`; Review Focus 1 and 4 are partly this
slice's). S1 plan (`…-accounts-s1-role-provenance.md`: the path rule, `CallerResolution`) and S2 plan
(`…-accounts-s2-sign-in.md`: D2 stores, D3 filters, D9 accounts per host, D13 the one read, D15 the principal). The
contributor view is `docs/architecture/accounts.md`. Prior art read for this plan: GitHub organisations (personal
namespace + orgs; a non-member gets 404 for a private org's resources, never 403), Clerk/WorkOS *active organisation*,
RFC 6750 (the Bearer challenge on the self-service routes), RFC 9110 §15.5.10 (409 for a state conflict).

## Decisions (read before Task 1)

- **D1 — The routes live in the core and are mapped by `MapAlvoAccounts()`, under the auth prefix.** Spec §6.3 says
  "under `{prefix}` (default `/api/alvo`)"; no such default exists — the Data API mounts every entity at
  `{AlvoApiOptions.RoutePrefix}/{entity}` (default `/api`), so `/api/tenants` would collide with any entity named
  `tenants` (and S4/S5's `invitations`, `tokens`), and reserving those names would refuse descriptors that are valid
  today. And every route requires a person from a session, which exists only where Accounts is registered. So the
  handlers, the person gate and the membership logic are core (`src/MMLib.Alvo/Tenants/SelfService/`, internal, spec
  §11.1 — a host-federated identity (#210) maps the same routes later), exposed as
  `SelfServiceEndpoints.Map(RouteGroupBuilder group, AlvoCors cors, AlvoRateLimits limits)`, which
  `AccountsEndpoints.Map` calls on its group: `{AlvoAccountsOptions.Prefix}/tenants…` (default `/auth/v1/tenants`).
  "Always mapped" (§6.3) therefore means: on every host that maps sign-in, whatever the descriptor says. A host
  without Accounts is unchanged (S2 D9) — **no TeaPie path pin moves**. S4's `/invitations` and S5's `/tokens` follow
  the same rule. **Recorded deviation** from §6.3's prefix.
- **D2 — Stores in the EF adapter behind `ITenantStore` (Abstractions, `internal`, namespace `MMLib.Alvo.Tenants`).**
  The S2 D2 precedent. `TenantTables` (EF `Internal/`) is shared by `EfCoreTenantStore` and `EfCoreAccountStore` (which
  writes the personal tenant inside its own transactions and reads the membership in its session read). One DDL text
  per table for both engines; no foreign keys. Roles are stored as a JSON array of names (`TEXT`, `System.Text.Json`).
- **D3 — Per-tenant and per-account serialisation, engine-agnostic.** Every membership write (rename, update, remove)
  first runs `UPDATE {tenants} SET version = version + 1 WHERE id = @tenant_id` — a row lock on PostgreSQL that
  serialises concurrent writers of one tenant until commit, and a no-op under SQLite's write gate — and only then reads
  owners and judges. Tenant creation first runs `UPDATE {auth_accounts} SET last_sign_in_at = last_sign_in_at WHERE id =
  @creator AND disabled_at IS NULL` — the creator's row lock, and zero rows means no such (enabled) account. Without
  these, two owners demoting each other or five parallel creates at the cap both pass a count read under READ
  COMMITTED. No `FOR UPDATE` (not SQLite) and no advisory locks (dialect-specific).
- **D4 — One store read for account + session + membership (spec §5.7 step 2, §14 AC2; S2 D13's S3 note).**
  `IAccountStore.IsSessionLiveAsync` is replaced by `ReadCallerAsync(CallerQuery)`: one statement joining session,
  account and — when a membership is asked for — `tenant_members ⨝ tenants` by the requested tenant id or by
  `tenants.personal_owner`. The decision on what that row means is the core's pure `MembershipRules`; the registry-reading
  `IMembershipResolution` (master plan's fixed shape) uses the same rules over `ITenantStore.FindMembershipAsync`, for
  callers with no session read (event actors here, personal tokens in S5).
- **D5 — Effective tenant roles are recomputed from the applied descriptor on every request.** A shared membership's
  roles are its stored names ∩ the current `assignableRoles` (resolved through the declared `RoleCatalog`); a personal
  membership's roles are the current `personalTenant.roles` and are **never stored** (its row holds `[]`) — "roles fixed
  by `personalTenant.roles`" read live, so a descriptor change applies on the next request (AC3) and no row outlives its
  reason. `selfService` absent → no membership is honoured (a named tenant refuses the principal; none → no tenant);
  `personalTenant` absent → personal memberships are not honoured (named → refused; none → no tenant). Default-deny.
- **D6 — The personal tenant.** Created in `SignInAsync`'s transaction (first sign-in and any later sign-in that finds
  none) and in `RefreshAsync`'s on `Rotated`/`Replayed`, when the applied descriptor has `personalTenant`;
  `INSERT … ON CONFLICT (personal_owner) DO NOTHING` then the owner membership `ON CONFLICT (tenant_id, user_id) DO
  NOTHING`, so concurrent first sign-ins converge on one. Its name is the account's display name at creation, else
  `"Personal"`. A Bearer request between applying `personalTenant` and that person's next exchange/refresh has no
  personal tenant (at most one access-token lifetime) — spec §6.5's "lazily".
- **D7 — Who may call.** A **person** is a request whose Bearer credential (`CallerResolution.Read`, Data surface) the
  keyed person resolver resolves (with `requestedTenant: null`; the route's path names the tenant, the tenant header is
  ignored). `X-Alvo-Api-Key` alone → **403** `forbidden` (spec §6.3: managing who is in a tenant is a person's act);
  nothing → 401 with the plain Bearer challenge; malformed/ambiguous → 401 `invalid_request`; a token that does not hold
  → 401 `invalid_token` (the S2 `UserEndpoints` answers). A tenant the person is not a member of answers **404
  `not-found`**, byte-identical to an unknown id (GitHub's rule: never confirm a tenant exists). A member who is not an
  owner on an owner route → 403 `forbidden`. **Nobody changes their own membership** (§14 AC4): `PATCH` on oneself → 403
  `forbidden`; leaving is `DELETE` on oneself.
- **D8 — Problem types.** Three new public slugs in `AlvoProblemTypes`: `feature-disabled` (404, extension member
  `descriptorKey` = `tenancy.selfService` or `tenancy.selfService.createTenants`), `last-owner` (409), `tenant-limit`
  (409, extension `maxPerAccount`). A change to a personal tenant's membership → 409 `conflict`. Roles outside
  `assignableRoles` → `ProblemResultFactory.Validation` at `/roles/{i}`.
- **D9 — `maxPerAccount` counts the shared tenants the account created** (`created_by`, kind `shared`) — a lifetime count
  while tenants cannot be deleted (spec §10), so handing a tenant to someone else and leaving frees nothing. Personal
  tenants never count.
- **D10 — Event actors.** (a) S1 carry-over: `EventActor.Of` and `DeliveryFanOut` keep a recorded tenant role only while
  it is in the applied `assignableRoles` (not merely declared). (b) S1 carry-over decision: a `RunAs.EventActor` delivery
  whose actor is a person (`AuthType` user) with tenant roles **narrows** them to the actor's current membership in the
  event's tenant through `IMembershipResolution` — removed → none, demoted → the intersection; never widened. Project
  roles stay as at the event (E8, unchanged). Reason: the anti-goal *roles that survive their reason*; cost: one
  registry read per such delivery, only in a `selfService` project.
- **D11 — `invitations` is accepted by the schema now and warned as unhonoured until S4.** The `selfService` block is
  frozen once (all four keys of §3.1), `invitations` without `createTenants` is refused with the other refusals, and
  `invitations: true` gets a row in `UnhonouredSubsystems.WithinBlocks` (and the dashboard's `DeclaredSlots` mirror):
  "owners cannot invite anyone in this build; invitations are S4".
- **D12 — The global-write warning** (§3.1): with `signUp.mode: open`, one warning per `create`/`update`/`delete` rule
  of a `tenancy: global` entity whose role literals (`PolicyCatalogBuilder.RoleLiterals`) include `authenticated`, at
  `/entities/{entity}/rules/{operation}`. `global` exists only with `tenancy.enabled`, so a level-1 app draws none (S2
  D17's noise concern). A rule that admits without naming a role (`created_by == @user.id`) is not listed — it admits
  each person to their own rows only. Recorded as the reading of "admits `authenticated`".
- **D13 — The default catalog is resolved, not registered.** The descriptor is applied at runtime, so "register a
  default only when `selfService` is on and the host registered none" (§6.6) is implemented as one internal resolver,
  `TenantCatalogs`: the host's `IAlvoTenantCatalog` if registered; else `RegistryTenantCatalog` (lists every
  `_tenants` id — self-service tenants only, said on the type and in the docs) when an `ITenantStore` is registered and
  the descriptor in question has `selfService`; else none. Every "is there a catalog" check asks it with the descriptor
  it is judging, so the boot refusal keeps firing wherever a descriptor exists to judge. **Recorded deviation (Task 9):**
  on a host with a registry and no catalog the refusal moved from the boot's catalog-free pass to the per-descriptor
  one, because a `selfService` descriptor cures it and every EF provider registers `ITenantStore` (its presence says
  nothing about intent) — kept in the catalog-free pass, a dashboard-first self-service host with such a schedule could
  never boot. So (a) a relational host with no descriptor now boots, the schedule unbound; (b) its first apply without
  `selfService` only warns; (c) the next restart stands down not-ready; (d) a stored-descriptor boot stands down instead
  of exiting. A file boot still exits with the same locator and fix; an apply that drops `selfService` under a bound
  schedule is refused as an unbinding (`TenantCatalogsTests.An_apply_dropping_self_service_under_a_bound_each_tenant_schedule_is_refused`);
  a host with no registry still refuses in the catalog-free pass. Spec §6.6 records the same.
- **D14 — Rate limit.** `AlvoRateLimitOptions.Create` (default 10/min per client, spec §7.3/§11.2's `Create`) and
  `AlvoRateLimit.Create`, charged on `POST {prefix}/tenants` (S5 adds token minting to the same budget).
- **D15 — Members arrive only through invitations (S4).** In S3 a shared tenant's only member is its creator; tests seed
  further members with a test-only helper (`test/_shared/ef/TenantSeed.cs`) that runs `TenantTables` statements — no
  port member is created ahead of its caller.

## Global Constraints

- English in everything committed. CRLF + BOM in every new `.cs`/`.csproj` (copy a neighbour's bytes; re-normalise after
  any Bash/Python edit). Short single-purpose methods (~25 lines); no inline comments by default (`alvo-dotnet-conventions`).
- Public API is the contract. Allowed public growth, each line justified in its commit body: Abstractions —
  `Tenancy.SelfService`, `TenancySelfService`, `SelfServicePersonalTenant`, `SelfServiceCreateTenants` (the descriptor is
  public, every block is); core — `AlvoRateLimitOptions.Create`, three `AlvoProblemTypes` consts (D8). Everything else
  `internal`. No new `InternalsVisibleTo` direction (Accounts → core and → Abstractions exist since S2).
- Default-deny: no `selfService` → no membership honoured; a named tenant that is not a membership refuses the **whole**
  principal (401), never a principal without a tenant.
- Tables only through `AlvoFrameworkTables` suffixes `_tenants`, `_tenant_members`; one DDL text per table for both
  engines (TEXT ids/instants in `StoredInstant.Text` form, INTEGER 0/1 booleans).
- Owner is the membership flag `is_owner`, read only by the self-service routes; it is never minted into `@user.roles`.
- Defaults from the spec: `maxPerAccount` 1–100, default 5; `personalTenant.roles` default `[]`; create limit 10/min.
- **Test discipline:** inner loop = the affected project filtered to the class,
  `dotnet test --project <proj> -- --filter-class '*<Class>*'`; ring0 only through the pre-commit hook; ring1 and ring2
  once, in Task 12. Docker for the PostgreSQL legs. Branch `accounts/s3-tenants` from `accounts/s2-sign-in`; never push
  to `main`; the controller opens the PR.
- A moved baseline is accepted only in the task that moves it, judged by `alvo-snapshot-judge` when the Stop hook asks
  (the pre-commit ring0 would otherwise block the commit).
- Do not touch `examples/field-service`, its TeaPie stacks, the website or the `alvo-descriptor-*` skills (S6).

## Review Focus

1. **A membership changes mid-session** (master Review Focus 1, S3 half): a removed member's next request naming the
   tenant is 401; a demoted member's next request lacks the role; a role dropped from `assignableRoles` or a changed
   `personalTenant.roles` takes effect on the next request; nothing is cached across requests. Pinned by Task 6
   (resolver) and Task 11 (both engines).
2. **Two owners demote or remove each other at the same instant** — exactly one change commits and the tenant is never
   left without an owner; the loser is told it is no longer an owner (403) or no longer a member (404), never
   `last-owner` (Task 4's recorded deviation). Pinned by Task 4 (store, concurrent, 20 iterations, both engines).
3. **A person fires `POST /tenants` in parallel at the cap** — never more than `maxPerAccount` shared tenants. Pinned by
   Task 3 (store, both engines).
4. **Two devices sign a new person in at once at level 2** — one account, one personal tenant, one owner membership.
   Pinned by Task 5 (store, both engines).
5. **A tenant role aimed at shared data or at management** (master Review Focus 4, S3 half): `access` naming an
   `assignableRoles` role is refused at apply; a self-made `dispatcher` cannot write a `global` entity, directly or
   through a relation term. Pinned by Task 2 (apply) and Task 11 (both engines).

---

### Task 1: `tenancy.selfService` in the descriptor, its refusals, and the self-service policy

**Files:**
- Modify: `schema/project.schema.json` (`tenancy.selfService`)
- Modify: `src/MMLib.Alvo.Abstractions/Descriptor/DescriptorBlocks.cs` (`Tenancy.SelfService` + three records)
- Create: `src/MMLib.Alvo/Descriptor/Internal/SelfServiceCheck.cs`; Modify: `DescriptorValidator.cs` (`TryReadModel`, beside `SignUpCheck.Check`)
- Create: `src/MMLib.Alvo/Tenants/SelfService/SelfServicePolicy.cs`; Modify: `src/MMLib.Alvo/Rules/PolicyCatalog.cs`,
  `src/MMLib.Alvo/Rules/Internal/PolicyCatalogBuilder.cs` (beside `SignIn = SignInPolicy.From(descriptor)`)
- Modify: `src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs` (`WithinBlocks` row, D11),
  `src/MMLib.Alvo.Admin/Components/Home/DeclaredSlots.cs` (mirror)
- Create: `examples/_negative/self-service-without-tenancy.json`, `…/self-service-admin-assignable.json`; Modify:
  `examples/_negative/expectations.json`
- Test: `test/MMLib.Alvo.Tests/Descriptor/SelfServiceCheckTests.cs`, `test/MMLib.Alvo.Tests/Tenants/SelfServicePolicyTests.cs`,
  `test/MMLib.Alvo.Tests/Descriptor/UnhonouredKeysTests.cs`, `test/MMLib.Alvo.Admin.Tests/Home/DeclaredLimitsTests.cs`, the
  schema suite per the `alvo-schema-testing` skill

**Interfaces:**
- Schema: `tenancy.selfService` = object, `additionalProperties: false`: `personalTenant` `{ roles: identifier[] (uniqueItems) }`
  (aP false); `createTenants` `{ creatorRoles: identifier[] (uniqueItems), maxPerAccount: integer 1–100, default 5 }` (aP
  false); `invitations` boolean; `assignableRoles` identifier[] (uniqueItems). Descriptions copy §3.1's table. **No
  cross-key rule in the schema** — every cross-key refusal is `SelfServiceCheck`'s, so each is reported once, with a fix
  (the `SignUpCheck` double-report lesson).
- Abstractions (public): `Tenancy.SelfService { get; init; }` of `public sealed record TenancySelfService
  { SelfServicePersonalTenant? PersonalTenant; SelfServiceCreateTenants? CreateTenants; bool? Invitations; IReadOnlyList<string>? AssignableRoles }`;
  `public sealed record SelfServicePersonalTenant { IReadOnlyList<string>? Roles }`;
  `public sealed record SelfServiceCreateTenants { IReadOnlyList<string>? CreatorRoles; int? MaxPerAccount }`.
- `SelfServiceCheck.Check(AlvoDescriptor) → IReadOnlyList<DescriptorValidationError>` (errors, all collected):
  - `selfService` present and `tenancy.enabled != true` → `/tenancy/selfService`, `"tenancy.selfService needs tenancy.enabled: true — self-service tenants are tenants."`,
    fix `"Set \"tenancy\": { \"enabled\": true } beside selfService, or remove selfService."`;
  - `invitations: true` without `createTenants` → `/tenancy/selfService/invitations`, `"invitations let owners invite people into shared tenants, and without createTenants no shared tenant can exist."`,
    fix `"Add \"createTenants\": { \"creatorRoles\": [ … ] }, or remove invitations."`;
  - an `assignableRoles` entry that is built-in (`anon`, `authenticated`, `admin`) → `/tenancy/selfService/assignableRoles/{i}`,
    `"'{role}' is a built-in role, and a tenant role must be an application role from auth.roles."`;
  - an `assignableRoles` entry not in `auth.roles` → same pointer, `"'{role}' is not declared in auth.roles."`, fix
    `"Add '{role}' to auth.roles, or remove it from assignableRoles."`;
  - a `personalTenant.roles` / `createTenants.creatorRoles` entry not in `assignableRoles` → its pointer
    (`/tenancy/selfService/personalTenant/roles/{i}`, `/tenancy/selfService/createTenants/creatorRoles/{i}`),
    `"'{role}' is not in tenancy.selfService.assignableRoles, and a membership can only ever carry a tenant role."`, fix
    `"Add '{role}' to assignableRoles, or remove it here."`.
- `internal sealed record SelfServicePolicy(bool Enabled, IReadOnlySet<Role> AssignableRoles, IReadOnlySet<Role>? PersonalTenantRoles, IReadOnlySet<Role>? CreatorRoles, int MaxPerAccount)`
  with `static SelfServicePolicy None`, `bool HasPersonalTenant => PersonalTenantRoles is not null`,
  `bool CreatesTenants => CreatorRoles is not null`, `IReadOnlySet<Role> Effective(IEnumerable<string> stored)` (stored
  names ∩ `AssignableRoles`, by name), `static SelfServicePolicy From(AlvoDescriptor descriptor, RoleCatalog roles)`
  (roles through `roles.TryGet`; `MaxPerAccount` default 5). `PolicyCatalog` gains `internal SelfServicePolicy SelfService { get; init; } = SelfServicePolicy.None;`.
  Consumers read `IPolicyCatalogProvider.Current?.SelfService ?? SelfServicePolicy.None`.
- `WithinBlocks` row: declared iff `tenancy.selfService.invitations == true`; consequence text `"owners cannot invite anyone in this build; invitations arrive with S4 of the accounts initiative"`.

- [ ] **Step 1: Write the failing facts.**
  `SelfServiceCheckTests`: `Self_service_without_tenancy_is_refused`; `Invitations_without_create_tenants_are_refused`;
  theory `A_built_in_assignable_role_is_refused` (`admin`, `authenticated`, `anon`); `An_undeclared_assignable_role_is_refused`;
  `A_personal_or_creator_role_outside_assignable_roles_is_refused` (both pointers); `Every_refusal_is_reported_together`
  (one descriptor with three faults → three errors); `Spec_level_two_and_level_three_descriptors_are_accepted` (§3's
  two examples verbatim, with `auth.roles` as written).
  `SelfServicePolicyTests`: `The_policy_carries_assignable_personal_and_creator_roles_and_the_cap`;
  `Max_per_account_defaults_to_five`; `Effective_roles_are_the_stored_names_still_assignable`;
  `Without_self_service_the_policy_is_none`.
  `UnhonouredKeysTests`: `Invitations_are_unhonoured_until_s4`. `DeclaredLimitsTests`: the mirror.
  Schema: positive example with all four keys; `maxPerAccount` 0 and 101 refused; an unknown key under `selfService`
  refused; the two negative files (expectation pointers `/tenancy/selfService`, `/tenancy/selfService/assignableRoles/0`).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*SelfServiceCheckTests*' --filter-class '*SelfServicePolicyTests*' --filter-class '*UnhonouredKeysTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command, then `dotnet test --project test/MMLib.Alvo.Schema.Tests`,
  `dotnet test --project test/MMLib.Alvo.Admin.Tests -- --filter-class '*DeclaredLimitsTests*'`,
  `dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-class '*PublicApiApprovalTests*'`. Expected: PASS
  after accepting exactly: the Abstractions PublicApi (the four symbols above), `negative-error-output.verified.txt`
  (the two new cases) and any schema-description snapshot the schema suite holds.
- [ ] **Step 5: Commit** — `feat(descriptor): tenancy.selfService and the refusals that keep a tenant role a tenant role`

### Task 2: A tenant role never manages the project; open sign-up names its global writes

**Files:**
- Modify: `src/MMLib.Alvo/Rules/Internal/AccessCatalogBuilder.cs` (`HasDeclaredRoles` → also refuses assignable roles)
- Create: `src/MMLib.Alvo/Tenants/SelfService/GlobalWriteWarnings.cs`; Modify: `src/MMLib.Alvo/Descriptor/Internal/DescriptorValidator.cs` (`RuleWarnings`)
- Create: `examples/_negative/assignable-role-in-access.json`; Modify: `examples/_negative/expectations.json`
- Test: `test/MMLib.Alvo.Tests/Management/AccessTenantRoleRefusalTests.cs`, `test/MMLib.Alvo.Tests/Tenants/GlobalWriteWarningsTests.cs`

**Interfaces:**
- Consumes: `TenancySelfService` (Task 1), `PolicyCatalogBuilder.RoleLiterals(CelNode)`.
- Access refusal: a role literal of any `access` level that is in `tenancy.selfService.assignableRoles` → error at
  `/access/{level}`, `"'{role}' is a tenant role (tenancy.selfService.assignableRoles), and a tenant role can never manage the project."`,
  fix `"Admit a project role here — one an operator or an API key grants — or remove '{role}' from assignableRoles."`.
  The level compiles no further (as for an undeclared role).
- `GlobalWriteWarnings.Warnings(AlvoDescriptor descriptor, SchemaModel schema, ICelCompiler compiler) → IReadOnlyList<DescriptorValidationError>`
  per D12, severity `Warning`, message `"'{entity}' is tenancy: global and its {operation} rule admits 'authenticated'; with auth.signUp.mode open, anyone who signs up can {operation} these shared rows."`,
  fix `"Admit a project role an operator grants instead, or make '{entity}' tenancy: scoped."`. Compiled through the same
  compiler and profile as the catalog (the `OwnerComparisonCheck` shape); a source that does not compile draws nothing.

- [ ] **Step 1: Write the failing facts.**
  `AccessTenantRoleRefusalTests`: theory `An_assignable_role_in_any_access_level_is_refused` (`admin`, `developer`,
  `viewer`; message names the role); `A_declared_role_that_is_not_assignable_still_manages` (`owner` in `auth.roles` and
  `access.admin`, not assignable → accepted — existing fixtures keep working, spec §6.2);
  `The_refusal_holds_when_the_role_is_reached_through_a_disjunction` (`"'x' in @user.roles || 'dispatcher' in @user.roles"`).
  `GlobalWriteWarningsTests`: `An_open_sign_up_lists_each_global_write_that_admits_authenticated` (two operations → two
  warnings at their pointers); `Invitation_only_sign_up_draws_none`; `A_scoped_entity_draws_none`;
  `A_level_one_descriptor_draws_none` (tenancy off); `A_read_rule_draws_none`;
  `An_owner_clause_without_a_role_draws_none` (`update: "created_by == @user.id"`).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AccessTenantRoleRefusalTests*' --filter-class '*GlobalWriteWarningsTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command and `dotnet test --project test/MMLib.Alvo.Schema.Tests`; Expected: PASS
  (accept the `negative-error-output.verified.txt` line for the new case).
- [ ] **Step 5: Commit** — `feat(rules): a tenant role in access is refused; open sign-up lists the global writes it opens`

### Task 3: The tenant store I — port, tables, creating a shared tenant, reading memberships

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Tenants/ITenantStore.cs` (port + records, namespace `MMLib.Alvo.Tenants`)
- Modify: `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs` (`TenantsSuffix = "_tenants"`, `TenantMembersSuffix =
  "_tenant_members"`; `NamesFor`)
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/TenantTables.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreTenantStore.cs`
- Modify: `SystemSchemaInitializer.cs` (both tables + indexes), `AlvoEfCoreProvider.cs` (`TryAddSingleton<ITenantStore>`)
- Create: `test/_shared/ef/TenantStoreFacts.cs` (abstract), `test/_shared/ef/TenantSeed.cs` (test-only, D15),
  `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteTenantStoreTests.cs`, `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlTenantStoreTests.cs`
  (link the shared files in both csproj — the `AccountStoreFacts` precedent)

**Interfaces (all `internal`):**
```csharp
interface ITenantStore
{
    Task<CreateTenantOutcome> CreateSharedAsync(CreateTenantRequest request, CancellationToken ct);
    Task<IReadOnlyList<StoredMembership>> ListMembershipsAsync(UserId user, CancellationToken ct);   // personal first, then joined_at, id
    Task<StoredMembership?> FindMembershipAsync(UserId user, TenantId? tenant, CancellationToken ct); // tenant null = the personal one
    Task<IReadOnlyList<TenantId>> ListTenantIdsAsync(CancellationToken ct);                           // by created_at, id
    // Task 4 adds: RenameAsync, ListMembersAsync, UpdateMemberAsync, RemoveMemberAsync
}
enum TenantKind { Personal, Shared }                      // stored "personal" / "shared"
sealed record StoredMembership(TenantId Tenant, TenantKind Kind, string Name, IReadOnlyList<string> Roles, bool IsOwner, DateTimeOffset JoinedAt);
sealed record CreateTenantRequest(TenantId Id, string Name, UserId Creator, IReadOnlyList<string> CreatorRoles, int MaxPerAccount, DateTimeOffset Now);
enum CreateTenantStatus { Created, LimitReached, NoAccount }
readonly record struct CreateTenantOutcome(CreateTenantStatus Status, StoredMembership? Membership);
sealed record PersonalTenantRequest(TenantId Id);         // consumed by Task 5
```
- DDL (prefix `p`): `p_tenants(id TEXT PK, kind TEXT NOT NULL, name TEXT NOT NULL, created_by TEXT NOT NULL, created_at
  TEXT NOT NULL, personal_owner TEXT NULL UNIQUE, version INTEGER NOT NULL)` + index `created_by`;
  `p_tenant_members(tenant_id TEXT NOT NULL, user_id TEXT NOT NULL, roles TEXT NOT NULL, is_owner INTEGER NOT NULL,
  joined_at TEXT NOT NULL, invitation_id TEXT NULL, PRIMARY KEY (tenant_id, user_id))` + index `user_id`.
  `personal_owner` is the account id for a personal tenant and `NULL` for a shared one — the constraint that a person has
  at most one personal tenant (D6). `invitation_id` stays `NULL` until S4 (spec D9's provenance column).
- `CreateSharedAsync`, one transaction (D3, D9): lock the creator's account row (zero rows → `NoAccount`); count
  `kind = 'shared' AND created_by = @creator`; `>= MaxPerAccount` → `LimitReached`; insert the tenant (`version` 0) and
  the owner membership (`is_owner` 1, roles = `CreatorRoles`) → `Created` with that membership.
- `TenantSeed` (test-only): `static Task AddMemberAsync(IServiceProvider services, TenantId tenant, UserId user, IReadOnlyList<string> roles, bool isOwner, DateTimeOffset now)`
  through `TenantTables.InsertMemberSql` on a connection from the fixture's `RelationalConnectionFactory`.

- [ ] **Step 1: Write the failing facts** in `TenantStoreFacts` (accounts created through `IAccountStore.SignInAsync`):
  `Creating_a_tenant_makes_the_creator_its_owner_with_the_creator_roles`;
  `Memberships_list_the_personal_tenant_first_then_by_joining` (seed a personal tenant with `TenantSeed`);
  `Find_answers_a_membership_only_to_its_member` (another user → `null`; an unknown tenant → `null`; `tenant: null` →
  the personal one or `null`);
  `The_cap_counts_the_shared_tenants_the_account_created` (cap 2: two `Created`, third `LimitReached`; a membership in
  someone else's tenant does not count);
  `Concurrent_creates_at_the_cap_never_exceed_it` (**Review Focus 3**: five `CreateSharedAsync` via `Task.WhenAll`, cap
  2, 20 iterations on fresh accounts → exactly two `Created` each time);
  `A_disabled_or_unknown_creator_creates_nothing`; `Tenant_ids_list_every_tenant_once`;
  `The_tables_are_reserved_framework_names`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteTenantStoreTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command, then
  `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlTenantStoreTests*'` and
  `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*Introspect*'`. Expected: PASS.
  **Prove the lock matters:** remove the account-row lock → the concurrency fact goes red on PostgreSQL; restore; record it
  in the commit body.
- [ ] **Step 5: Commit** — `feat(data-ef): a tenant registry, and a creator never passes the cap`

### Task 4: The tenant store II — renaming, members, and the last owner

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Tenants/ITenantStore.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/TenantTables.cs`,
  `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreTenantStore.cs`
- Test: `test/_shared/ef/TenantStoreFacts.cs`

**Interfaces (added to the port):**
```csharp
Task<MemberChangeStatus> RenameAsync(TenantId tenant, UserId actor, string name, CancellationToken ct);
Task<IReadOnlyList<MemberView>?> ListMembersAsync(TenantId tenant, UserId actor, CancellationToken ct); // null: actor is no member
Task<MemberChangeStatus> UpdateMemberAsync(MemberUpdate update, CancellationToken ct);
Task<MemberChangeStatus> RemoveMemberAsync(MemberRemoval removal, CancellationToken ct);

sealed record MemberView(UserId User, string? DisplayName, string? AvatarUrl, IReadOnlyList<string> Roles, bool IsOwner, DateTimeOffset JoinedAt);
sealed record MemberUpdate(TenantId Tenant, UserId Actor, UserId Member, IReadOnlyList<string>? Roles, bool? IsOwner, DateTimeOffset Now);
sealed record MemberRemoval(TenantId Tenant, UserId Actor, UserId Member, DateTimeOffset Now);
enum MemberChangeStatus { Changed, NotFound, NotOwner, NoSuchMember, LastOwner, PersonalTenant, Self }
```
- Every write, one transaction, in this order (D3): bump `version` (zero rows → `NotFound`); read the actor's membership
  (none → `NotFound` — a non-member learns nothing); then judge:
  - `RenameAsync`: actor not owner → `NotOwner`; else rename → `Changed` (a personal tenant may be renamed by its owner).
  - `UpdateMemberAsync`: `Actor == Member` → `Self` (AC4); actor not owner → `NotOwner`; personal → `PersonalTenant`;
    member absent → `NoSuchMember`; `IsOwner == false` on the only owner → `LastOwner`; else write roles (when given) and
    `is_owner` (when given) → `Changed`. The store stores names verbatim; `assignableRoles` is the core's check (Task 8).
  - `RemoveMemberAsync`: personal → `PersonalTenant`; `Actor != Member` and actor not owner → `NotOwner`; member absent →
    `NoSuchMember`; member is the only owner → `LastOwner`; else delete → `Changed`.
- `ListMembersAsync`: members ⨝ `{prefix}_auth_accounts` for display name and avatar (left join: a member row never
  hides for a missing account), by `joined_at`, `user_id`.

- [ ] **Step 1: Write the failing facts** (members seeded with `TenantSeed`):
  `An_owner_renames_and_a_member_cannot`; `A_non_member_gets_not_found_for_every_write_and_null_for_the_list`;
  `An_owner_changes_a_member_s_roles_and_ownership`; `Nobody_changes_their_own_membership`;
  `The_last_owner_cannot_be_demoted_removed_or_leave` (rows unchanged; **recorded deviation, as implemented**: only
  leaving answers `LastOwner` — demoting oneself is `Self`, judged first per AC4, and demoting or removing the sole owner
  can come only from a non-owner, `NotOwner`; the last-owner check still runs on every demotion and removal, as a second
  line of defence);
  `A_second_owner_makes_leaving_possible`; `A_member_leaves_and_a_non_owner_cannot_remove_another`;
  `A_personal_tenant_refuses_every_member_change` (update another id → `PersonalTenant`, remove self → `PersonalTenant`);
  `Members_list_display_names_and_avatars_from_the_accounts`;
  `Two_owners_demoting_each_other_at_once_leave_exactly_one_owner` (**Review Focus 2**: two owners, two
  `UpdateMemberAsync(IsOwner: false)` aimed at each other via `Task.WhenAll`, 20 iterations → statuses
  `{Changed, NotOwner}`, one owner remains);
  `Two_owners_removing_each_other_at_once_leave_exactly_one_owner` (same shape with `RemoveMemberAsync` → statuses
  `{Changed, NotFound}`). **Recorded deviation, as implemented:** the plan first wrote `{Changed, LastOwner}` for both;
  under this task's own judgment order the serialised loser re-reads its actor after the winner committed — demoted, it
  is no longer an owner (`NotOwner`, 403); removed, it is no longer a member (`NotFound`, 404) — so `LastOwner` is never
  the loser's answer. The invariant (exactly one owner remains) is what both facts assert.
- [ ] **Step 2: Run to see them fail** — the Task 3 SQLite command; Expected: FAIL (compile).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the SQLite and PostgreSQL commands of Task 3; Expected: PASS. **Prove the bump matters:** drop
  the `version` bump → a Review Focus 2 fact goes red on PostgreSQL; restore; record it in the commit body.
- [ ] **Step 5: Commit** — `feat(data-ef): owners manage members, and a tenant never loses its last owner`

### Task 5: The personal tenant in the sign-in and refresh transactions, and the one caller read

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Accounts/IAccountStore.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreAccountStore.cs`,
  `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AuthTables.cs`, `…/Internal/TenantTables.cs`
- Modify (call sites only, behaviour unchanged): `src/MMLib.Alvo.Accounts/Internal/PersonSessions.cs` (`IsLiveAsync` over
  `ReadCallerAsync` with no membership), `ExchangeGrant.cs`, `RefreshGrant.cs` (pass `PersonalTenant: null` — Task 6
  wires the policy)
- Test: `test/_shared/ef/AccountStoreFacts.cs`

**Interfaces:**
- Consumes: `PersonalTenantRequest`, `StoredMembership`, `TenantTables` (Task 3).
- `SignInRequest` and `RefreshRequest` each gain a last positional parameter `PersonalTenantRequest? PersonalTenant`
  (`null` = no personal tenants). `SignInAsync`: after the session starts, when `PersonalTenant` is set, ensure the
  account's personal tenant (D6); a refusal still writes nothing. `RefreshAsync`: on `Rotated`/`Replayed`, the same.
- Replaces `IsSessionLiveAsync`:
```csharp
Task<CallerRead> ReadCallerAsync(CallerQuery query, CancellationToken ct);

sealed record CallerQuery(UserId Account, Guid Session, DateTimeOffset Now, MembershipQuery? Membership);
sealed record MembershipQuery(TenantId? Tenant);                       // Tenant null = the personal one
readonly record struct CallerRead(bool Live, StoredMembership? Membership);
```
  One statement per shape (no membership / by tenant id / personal): `sessions ⨝ accounts` filtered as S2's
  `LiveSessionSql`, `LEFT JOIN (tenant_members m JOIN tenants t ON t.id = m.tenant_id)` on `m.user_id = s.account_id`
  and `t.id = @tenant_id` or `t.personal_owner = s.account_id`. No row → `Live: false`.

- [ ] **Step 1: Write the failing facts** in `AccountStoreFacts`:
  `A_first_sign_in_with_a_personal_tenant_creates_it_with_its_owner_in_one_transaction` (status `Created`;
  `ITenantStore.FindMembershipAsync(account, null)` → kind `Personal`, owner, roles `[]`, name = the identity's name);
  `A_refused_sign_up_creates_no_tenant`; `A_later_sign_in_creates_a_missing_personal_tenant_once` (two later sign-ins →
  one tenant); `Two_concurrent_first_sign_ins_create_one_personal_tenant` (**Review Focus 4**: the S2 concurrent fact's
  shape with `PersonalTenant` set, 20 iterations → one account, one personal tenant, one membership);
  `A_refresh_creates_a_missing_personal_tenant_and_a_refused_one_does_not`;
  `The_caller_read_answers_the_session_and_the_named_membership` (live + membership; another tenant → live, `null`
  membership; personal; dead session → `Live: false` whatever the membership); rename the S2 liveness facts onto
  `ReadCallerAsync`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteAccountStoreTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command, the PostgreSQL `*PostgreSqlAccountStoreTests*` command, and
  `dotnet test --project test/MMLib.Alvo.Accounts.Tests` (call sites unchanged in behaviour). Expected: PASS.
- [ ] **Step 5: Commit** — `feat(data-ef): the personal tenant is born with the account, and one read answers session and membership`

### Task 6: Membership resolution reads the registry; a person acts in a tenant

**Files:**
- Create: `src/MMLib.Alvo/Tenants/SelfService/MembershipRules.cs`, `RegistryMembershipResolution.cs`, `SelfServiceMemberships.cs`,
  `MembershipResponse.cs`
- Modify: `src/MMLib.Alvo/Auth/Internal/MembershipResolution.cs` (remarks: S3), `src/MMLib.Alvo/Auth/Setup.cs` (the
  registration below)
- Modify: `src/MMLib.Alvo.Accounts/Internal/PersonResolver.cs`, `PersonSessions.cs` (`ReadAsync`), `ExchangeGrant.cs`,
  `RefreshGrant.cs`, `UserResponse.cs`, `UserEndpoints.cs`
- Create: `test/_shared/accounts/descriptors/personal-tracker.alvo.json` (spec §3 level 2, name `personal-tracker`),
  `test/_shared/accounts/descriptors/team-tracker.alvo.json` (below)
- Test: `test/MMLib.Alvo.Tests/Tenants/MembershipRulesTests.cs`, `test/MMLib.Alvo.Tests/Auth/MembershipResolutionTests.cs`,
  `test/MMLib.Alvo.Accounts.Tests/PersonResolverTests.cs`, `test/MMLib.Alvo.Accounts.Tests/PersonalTenantTests.cs`,
  `test/MMLib.Alvo.Accounts.Tests/UserEndpointsTests.cs`

**Interfaces:**
- Consumes: `SelfServicePolicy` (Task 1), `ITenantStore.FindMembershipAsync`/`ListMembershipsAsync` (Task 3),
  `ReadCallerAsync`, `MembershipQuery`, `PersonalTenantRequest` (Task 5).
- Core:
```csharp
internal enum MembershipPlanKind { Refuse, NoTenant, Read }
internal readonly record struct MembershipPlan(MembershipPlanKind Kind, MembershipQuery? Query);
internal static class MembershipRules
{
    internal static MembershipPlan Plan(TenantId? requested, SelfServicePolicy policy);
    internal static MembershipResolution Decide(TenantId? requested, StoredMembership? found, SelfServicePolicy policy);
}
```
  `Plan`: policy off → requested ? `Refuse` : `NoTenant`; on → requested ? `Read(requested)` : (`HasPersonalTenant` ?
  `Read(null)` : `NoTenant`). `Decide` (D5): `found` null → requested ? `Refusal` : `NoTenant`; personal and
  `!HasPersonalTenant` → the same; personal → (`found.Tenant`, `PersonalTenantRoles`); shared → (`found.Tenant`,
  `policy.Effective(found.Roles)`). Never a principal without a tenant when one was named.
- `RegistryMembershipResolution(ITenantStore store, IPolicyCatalogProvider catalogs) : IMembershipResolution` — `Plan`;
  `Refuse`/`NoTenant` with no I/O; `Read` → `FindMembershipAsync` → `Decide`. Registration in `Auth/Setup.cs`:
  `TryAddSingleton<IMembershipResolution>(sp => sp.GetService<ITenantStore>() is { } store ? new RegistryMembershipResolution(store, …) : new NoTenantMembershipResolution())`.
- `SelfServiceMemberships(IServiceProvider services, IPolicyCatalogProvider catalogs)`:
  `Task<IReadOnlyList<MembershipResponse>> ListAsync(UserId user, CancellationToken ct)` — policy off or no store → `[]`;
  personal memberships only while `HasPersonalTenant`; roles effective (D5).
  `internal sealed record MembershipResponse(Guid TenantId, string Name, string Kind, IReadOnlyList<string> Roles, bool IsOwner)`,
  JSON `tenantId`, `name`, `kind` (`personal` | `shared`), `roles` (ordinal order), `isOwner`; own serializer options (the
  `UserResponse` precedent).
- Accounts: `PersonSessions.ReadAsync(PersonSession session, MembershipQuery? membership, CancellationToken) → CallerRead`
  (provider still listed, else `Live: false` with no read; then `ReadCallerAsync`). `PersonResolver.ResolveAsync`:
  identify (no I/O) → requested tenant → `Plan` (`Refuse` → `null`, no I/O) → `ReadAsync` (**the one read**) → not live →
  `null` → `Decide` (`NoTenant` plan → `NoTenant`) → refused → `null` → the D15 principal with the tenant and tenant
  roles. `ExchangeGrant`/`RefreshGrant`: `PersonalTenant = policy.HasPersonalTenant ? new(TenantId.New()) : null`.
  `UserResponse.Memberships` becomes `IReadOnlyList<MembershipResponse>` from `SelfServiceMemberships`.
- `team-tracker.alvo.json` (level 3, name `team-tracker`): `auth` `{providers: [google], roles: [dispatcher,
  technician, owner], signUp: {mode: open}}`; `tenancy` `{enabled: true, selfService: {personalTenant: {roles: []},
  createTenants: {creatorRoles: [dispatcher], maxPerAccount: 2}, assignableRoles: [dispatcher, technician]}}`;
  `access` `{admin: "'owner' in @user.roles"}` (a non-assignable `owner`, spec §6.2's fixture shape); entities `regions`
  (`tenancy: global`; `list` `'authenticated' in @user.roles`; `create` `"'dispatcher' in @user.roles"`; `update`
  `"'dispatcher' in @user.roles || jobs.exists(j, true)"`), `jobs` (scoped; a `region` ref → `regions`; `list`/`get`
  `"'dispatcher' in @user.roles || 'technician' in @user.roles"` — tenant roles only, so the relation term from
  `regions` is false for a person and S1's path rule is what the fact observes; `create`/`update`/`delete`
  `'dispatcher' in @user.roles`), `notes` (scoped; `create`
  `"'owner' in @user.roles"`, `list` `'authenticated' in @user.roles`). The world's dev key carries the **project** role
  `dispatcher` (it seeds `regions`; the split in one fixture). Its apply draws no error, and Task 2's warning does not
  fire (no write rule names `authenticated`).

- [ ] **Step 1: Write the failing facts.**
  `MembershipRulesTests`: the full table of `Plan` × `Decide` (policy off/on; personal on/off; requested/none; found
  personal/shared/none; a stored role no longer assignable is dropped; personal roles come from the policy whatever the
  row holds).
  `MembershipResolutionTests`: `The_registry_resolution_reads_once_and_decides_by_the_rules`;
  `Without_a_tenant_store_the_no_tenant_resolution_stands`.
  `PersonalTenantTests` (`AccountsWorld`, `personal-tracker`): `A_first_exchange_creates_the_personal_tenant`;
  `Without_a_header_a_person_writes_into_their_personal_tenant` (`POST /api/time_entries` → row `tenant_id` = the personal
  tenant from `/user`); `Two_people_s_personal_tenants_never_see_each_other`;
  `A_personal_tenant_added_after_sign_up_appears_at_the_next_refresh` (sign in under `time-tracker`, re-apply
  `personal-tracker`, Data API without header → no tenant; refresh → `/user` lists it; next request writes into it).
  `PersonResolverTests` (`team-tracker`; shared tenant created through `ITenantStore`, members via `TenantSeed`):
  `A_header_naming_a_membership_acts_in_it_with_its_tenant_roles` (published principal: tenant, `TenantRoles =
  {dispatcher}`, `Roles = {authenticated}`); `A_header_naming_a_tenant_the_person_is_not_in_refuses_the_principal`
  (another person's shared tenant, another person's personal tenant, a random uuid → 401 `invalid_token`, identical
  bodies); `Without_self_service_a_header_still_refuses_the_principal`;
  `A_role_dropped_from_assignable_roles_is_gone_on_the_next_request` (**Review Focus 1**);
  `A_removed_member_is_refused_on_the_next_request` (**Review Focus 1**);
  `A_request_naming_a_tenant_is_one_store_read` (counting decorators over `IAccountStore` and `ITenantStore`: 20
  requests with a header → 20 `ReadCallerAsync`, 0 calls to anything else on either port — spec §14 AC2);
  `A_refused_tenant_costs_no_read_when_self_service_is_off`.
  `UserEndpointsTests`: `User_lists_the_memberships_with_kind_roles_and_ownership`; the S2 "no memberships" fact keeps
  holding under `time-tracker`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*MembershipRulesTests*' --filter-class '*MembershipResolutionTests*'` and
  `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*PersonalTenantTests*' --filter-class '*PersonResolverTests*' --filter-class '*UserEndpointsTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 commands, then `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*AccountsOpenApiTests*'`
  (accept the move if the `/user` memberships schema is documented; judged). Expected: PASS.
- [ ] **Step 5: Commit** — `feat(accounts): a person acts in a tenant they belong to, checked in the session's one read`

### Task 7: `/tenants` — the person gate, listing, creating, renaming

**Files:**
- Create: `src/MMLib.Alvo/Tenants/SelfService/SelfServiceEndpoints.cs`, `SelfServicePerson.cs`, `TenantRoutes.cs`,
  `SelfServiceProblems.cs`, `TenantName.cs`
- Modify: `src/MMLib.Alvo/Api/AlvoProblemTypes.cs` (three slugs, D8), `src/MMLib.Alvo/Api/AlvoRateLimitOptions.cs`
  (`Create = 10`), its validator (`Create >= 1`), `src/MMLib.Alvo/Api/Internal/AlvoRateLimits.cs` (`AlvoRateLimit.Create`)
- Modify: `src/MMLib.Alvo.Accounts/Internal/AccountsEndpoints.cs` (calls `SelfServiceEndpoints.Map`),
  `AccountsDocumentTransformer.cs`, `AccountsDocumentation.cs`/`AccountsSchemas.cs` (the three operations)
- Test: `test/MMLib.Alvo.Accounts.Tests/TenantRoutesTests.cs`, `test/MMLib.Alvo.Accounts.Tests/AccountsOpenApiTests.cs`,
  `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs`, the core options suite

**Interfaces:**
- Consumes: `SelfServicePolicy`, `ITenantStore` (Tasks 3–4), `SelfServiceMemberships`, `MembershipResponse` (Task 6),
  `AlvoCors`, `AlvoRateLimits`, `CallerResolution`, `ProblemResultFactory`.
- `SelfServicePerson.AdmitAsync(HttpContext http) → ValueTask<(UserId? Person, IResult? Refusal)>` per D7, reading
  through `CallerResolution.Read(request, auth, CredentialSurface.Data, personRegistered: CallerResolvers.HasPerson)` and the
  keyed resolver under `CallerResolution.PersonResolverKey`; requires `AlvoActorKind.User`.
- `SelfServiceEndpoints.Map(RouteGroupBuilder group, AlvoCors cors, AlvoRateLimits limits)`; every route: `cors.Filter`
  first, then `Cache-Control: no-store` (each answer is about a person), tag `tenants`:
  - `GET /tenants` (`tenants.list`) → 200 `MembershipResponse[]`; feature `tenancy.selfService`.
  - `POST /tenants` (`tenants.create`) `{ "name": string }` → 201, `Location: {prefix}/tenants/{id}`, body the creator's
    `MembershipResponse`; feature `tenancy.selfService.createTenants`; `limits.For(AlvoRateLimit.Create)` before the gate;
    `LimitReached` → 409 `tenant-limit` with `maxPerAccount`; `NoAccount` → 401 `invalid_token`.
  - `PATCH /tenants/{tenantId:guid}` (`tenants.rename`) `{ "name": string }` → 200 `MembershipResponse`; `NotFound` → 404
    `not-found`; `NotOwner` → 403 `forbidden`.
  - Order: CORS → no-store → (rate limit) → person gate → feature (`feature-disabled`, 404, `descriptorKey`) → JSON
    content type (415) → body (`TenantName.ReadAsync`: trimmed, 1–100 chars, no control characters or lone surrogates,
    through `BoundedJsonBody`; else 400 `validation` at `/name`) → store.

- [ ] **Step 1: Write the failing facts** (`TenantRoutesTests`, `AccountsWorld` with `team-tracker` unless stated):
  `Creating_a_tenant_answers_201_with_the_owner_membership` (Location, `kind` `shared`, `roles` `[dispatcher]`,
  `isOwner` true); `The_list_holds_the_personal_and_the_shared_tenants`; `An_owner_renames_a_tenant`;
  `A_member_who_is_not_an_owner_cannot_rename` (403); `A_non_member_and_an_unknown_tenant_get_the_same_404`
  (byte-identical bodies); `The_third_tenant_is_refused_at_a_cap_of_two` (409 `tenant-limit`, `maxPerAccount` 2);
  `Tenant_creation_answers_429_after_its_limit` (`Create = 2`; `Retry-After`; the Data API untouched);
  `Without_self_service_every_route_is_404_feature_disabled` (`time-tracker`; `descriptorKey` `tenancy.selfService`);
  `Without_create_tenants_creating_is_404_naming_its_key` (`personal-tracker`; `tenancy.selfService.createTenants`; the
  list still answers); `An_api_key_is_403_and_no_credential_is_a_bearer_challenge`;
  theory `A_bad_name_is_a_validation_problem` (`""`, 101 chars, `"a\u0007b"`, a lone surrogate); `A_non_json_body_is_415`;
  `An_allowed_origin_gets_cors_headers_on_the_tenant_routes`.
  `AccountsOpenApiTests`: `Every_tenant_route_is_in_the_document_once`. `OpenApiDocumentTests`: unchanged facts.
  Options: `Create = 0` refused.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*TenantRoutesTests*' --filter-class '*AccountsOpenApiTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command, `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*OpenApiDocumentTests*' --filter-class '*AlvoApiOptions*'`,
  `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*PublicApiApprovalTests*'`,
  `dotnet test --project test/MMLib.Alvo.DocsGen.Tests -- --filter-class '*ProblemType*'` (add the notes it asks for), then
  `scripts/lint-api`. Expected: PASS after accepting exactly: the core PublicApi (three consts, one property), the
  Accounts OpenAPI snapshot (three operations), the core `OpenApiDocumentTests` snapshot (only the three `problemDetails.type`
  enum values — S2 D9's amendment). `[lint-api] OK`; a Vacuum finding is fixed in the transformer, never muted.
- [ ] **Step 5: Commit** — `feat(tenancy): a person lists, creates and renames their tenants`

### Task 8: Members — listing, changing roles and ownership, removing and leaving

**Files:**
- Create: `src/MMLib.Alvo/Tenants/SelfService/MemberRoutes.cs`, `MemberPatch.cs`, `MemberResponse.cs`
- Modify: `SelfServiceEndpoints.cs`, `src/MMLib.Alvo.Accounts/Internal/AccountsDocumentTransformer.cs` (+ schemas)
- Test: `test/MMLib.Alvo.Accounts.Tests/MemberRoutesTests.cs`, `test/MMLib.Alvo.Accounts.Tests/AccountsOpenApiTests.cs`

**Interfaces:**
- Consumes: `ITenantStore` member operations (Task 4), the gate and problems (Task 7), `SelfServicePolicy.AssignableRoles`.
- `GET /tenants/{tenantId:guid}/members` (`tenants.listMembers`) → 200 `MemberResponse[]`
  (`userId`, `name`, `avatarUrl`, `roles` — effective per D5, `isOwner`); `null` from the store → 404.
- `PATCH /tenants/{tenantId:guid}/members/{userId:guid}` (`tenants.updateMember`) `{ "roles"?: string[], "isOwner"?: bool }`
  → 200 the member's `MemberResponse`. Body: at least one member, no unknown member, `roles` unique, each in
  `assignableRoles` — else 400 `validation` at `/roles/{i}` whose detail lists the assignable roles (so `admin` and any
  undeclared name are refused here, AC4).
- `DELETE /tenants/{tenantId:guid}/members/{userId:guid}` (`tenants.removeMember`) → 204.
- Status → answer: `NotFound` → 404 `not-found`; `NotOwner`, `Self` → 403 `forbidden` (Self's detail:
  `"Nobody changes their own membership; another owner can, and you can leave."`); `NoSuchMember` → 404 `not-found`;
  `LastOwner` → 409 `last-owner`; `PersonalTenant` → 409 `conflict` (`"A personal tenant has exactly one member, its owner."`).

- [ ] **Step 1: Write the failing facts** (`team-tracker`; a second person seeded into the creator's tenant):
  `An_owner_grants_a_role_and_the_member_s_next_request_carries_it` (technician → `TenantRoles` on the next Data API
  request; the change answered before);
  theory `A_role_outside_assignable_roles_is_refused` (`admin`, `owner`, `auditor` undeclared, a duplicate);
  `Nobody_patches_their_own_membership` (owner on self → 403, roles unchanged);
  `A_member_cannot_change_another_member` (403);
  `Removing_a_member_refuses_their_next_request_in_that_tenant` (204; their next request with the header → 401; without
  it → their personal tenant);
  `A_member_leaves_by_deleting_themself`; `The_last_owner_cannot_leave_be_removed_or_be_demoted` (leaving → 409
  `last-owner`; demoting oneself and a member removing or demoting the owner → 403 — Task 4's recorded deviation at the
  route);
  `Promoting_a_second_owner_lets_the_first_leave`;
  `A_personal_tenant_lists_one_member_and_refuses_changes` (GET → one row; DELETE self → 409 `conflict`; PATCH another
  id → 404);
  `Members_show_display_names_and_never_an_email`; `A_non_member_gets_404_on_every_member_route`.
  `AccountsOpenApiTests`: the three operations appear once each.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*MemberRoutesTests*' --filter-class '*AccountsOpenApiTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command, then `scripts/lint-api`; Expected: PASS after accepting the Accounts OpenAPI
  snapshot (three operations); `[lint-api] OK`.
- [ ] **Step 5: Commit** — `feat(tenancy): owners manage members within assignableRoles, and anyone can leave`

### Task 9: The default tenant catalog, only for self-service and only when the host has none

**Files:**
- Create: `src/MMLib.Alvo/Tenants/SelfService/RegistryTenantCatalog.cs`, `src/MMLib.Alvo/Scheduling/TenantCatalogs.cs`
- Modify: `src/MMLib.Alvo/Scheduling/ScheduleRuns.cs`, `ScheduleChecks.cs`, `Setup.cs`, `DataMigrationStarter.cs`,
  `DataMigrationBootCheck.cs`, `src/MMLib.Alvo/Rules/Internal/DescriptorHost.cs` (`HasTenantRegistry`),
  `src/MMLib.Alvo/Events/Internal/AutomationCompiler.cs` (the `HasTenantCatalog` check)
- Test: the existing suites of those types in `test/MMLib.Alvo.Tests/Scheduling/` (find with
  `grep -rln "ScheduleChecks\|ScheduleRuns\|DataMigrationBootCheck" test`) plus `test/MMLib.Alvo.Tests/Tenants/TenantCatalogsTests.cs`

**Interfaces:**
- `internal sealed class RegistryTenantCatalog(ITenantStore store) : IAlvoTenantCatalog` — `ListTenantIdsAsync`; remarks
  state it lists **self-service tenants only** (spec §6.6) and a deployment mixing API-key tenants registers its own.
- `internal sealed class TenantCatalogs(IServiceProvider services, IPolicyCatalogProvider catalogs)`:
  `IAlvoTenantCatalog? Current` (host's, else the registry's while the applied policy has `selfService` and an
  `ITenantStore` is registered, else `null`); `static bool Lists(DescriptorHost host, bool selfService)` =
  `host.HasTenantCatalog || (host.HasTenantRegistry && selfService)`.
- Every check that asked `IsService(typeof(IAlvoTenantCatalog))` or `HasTenantCatalog` asks `Lists` with the descriptor
  (or policy catalog) it is judging; `ScheduleRuns` lists through `TenantCatalogs.Current`. Sweep:
  `grep -rn "IAlvoTenantCatalog\|HasTenantCatalog" src --include='*.cs'` — every hit is either routed through
  `TenantCatalogs` or is documentation.

- [ ] **Step 1: Write the failing facts:** `An_each_tenant_schedule_without_a_catalog_still_refuses_boot`
  (no `selfService`, registry present → refused, message unchanged);
  `Self_service_lists_the_registry_s_tenants` (a `selfService` descriptor, no host catalog → the schedule boots and a
  run is made per registry tenant, personal and shared);
  `A_host_catalog_wins_over_the_registry`; `Dropping_self_service_stops_listing_the_registry` (re-apply without it →
  `Current` is `null`); `A_tenanted_automation_compiles_with_the_registry_only_under_self_service` (`AutomationCompiler`);
  `A_migration_on_a_scoped_schema_boots_with_the_registry_under_self_service`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*TenantCatalogsTests*' --filter-class '*ScheduleChecks*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command and every suite the sweep touched; Expected: PASS.
- [ ] **Step 5: Commit** — `feat(scheduling): self-service tenants are a tenant list, and only when the host has none`

### Task 10: Event actors keep only assignable tenant roles, and a removed member's deliveries lose theirs

**Files:**
- Modify: `src/MMLib.Alvo/Events/Internal/EventActor.cs`, `DeliveryFanOut.cs`, `DeliveryRunner.cs` (`CallerFor` →
  `CallerForAsync`)
- Modify: `docs/architecture/events.md` (the actor section: D10)
- Test: `test/MMLib.Alvo.Tests/Events/EventActorTests.cs`, `DeliveryFanOutTests.cs`, a `DeliveryRunner` actor fact beside
  the existing runner suite

**Interfaces:**
- Consumes: `SelfServicePolicy` (Task 1), `IMembershipResolution` (Task 6).
- `EventActor.Of(AlvoEvent @event, RoleCatalog declared, SelfServicePolicy selfService)` — tenant roles = recorded ∩
  declared ∩ `selfService.AssignableRoles` (D10a); `DeliveryFanOut` resolves subscription conditions against the same
  filtered set (one helper, `EventActor.AssignableTenantRoles`).
- `DeliveryRunner.CallerForAsync(AlvoEvent, AlvoRunAs, CancellationToken)`: for `EventActor` whose context is
  `ActorKind.User` with a tenant and non-empty tenant roles → `IMembershipResolution.ResolveAsync(user, tenant)`;
  refused → tenant roles ∅; else recorded ∩ resolved (D10b). Never widened; project roles untouched; no read otherwise.

- [ ] **Step 1: Write the failing facts:** `A_recorded_tenant_role_no_longer_assignable_is_not_held` (still declared,
  dropped from `assignableRoles`); `Subscription_conditions_see_only_assignable_tenant_roles`;
  `An_event_actor_delivery_of_a_removed_member_runs_without_tenant_roles`;
  `An_event_actor_delivery_of_a_demoted_member_keeps_only_the_roles_still_held`;
  `A_membership_widened_since_the_event_widens_nothing`;
  `An_api_key_actor_and_a_system_run_read_no_membership` (counting resolution: 0 calls).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*EventActorTests*' --filter-class '*DeliveryFanOutTests*' --filter-class '*DeliveryRunner*'`; Expected: FAIL.
- [ ] **Step 3: Implement**; update `events.md` (*an event actor's tenant roles*: assignable only, narrowed to the
  current membership at delivery, project roles as at the event).
- [ ] **Step 4: Run** the Step 2 command; Expected: PASS.
- [ ] **Step 5: Commit** — `fix(events): an event actor's tenant roles are assignable ones its membership still holds`

### Task 11: Levels 2–3 end to end on both engines, the two-tenant adversarial suite, and the docs

**Files:**
- Create: `test/_shared/accounts/TenancyEngineFacts.cs` (abstract, `Engine`-parameterised, `team-tracker`/`personal-tracker`),
  `test/MMLib.Alvo.Accounts.Tests/SqliteTenancyEngineTests.cs`,
  `test/MMLib.Alvo.Api.Tests.Integration/PostgresTenancyEngineTests.cs` (the csproj already links `../_shared/accounts/*.cs`
  since S2; add `../_shared/ef/TenantSeed.cs` if not linked)
- Modify: `docs/architecture/accounts.md` (an S3 section: what you can build — levels 2 and 3 with their descriptors;
  the two tables; the resolution pipeline with the folded read; the routes; the invariants table below; D1–D15 as
  recorded deviations; *Not in S3*: invitations, tokens), `docs/architecture/data-api.md` (a person's tenant header:
  selection among memberships, D8 of the spec), `docs/architecture/host.md` (`Alvo:Api:RateLimits:Create`), the
  scheduling section that documents `IAlvoTenantCatalog` (D13), `CHANGELOG.md` `[Unreleased]`, `docs/PLAN.md` (S3 done,
  `← YOU ARE HERE` to S4)

**Interfaces:** consumes everything above; produces nothing.

- [ ] **Step 1: Write the facts** in `TenancyEngineFacts`:
  `Level_two_end_to_end` (sign in → `/user` lists the personal tenant → `POST /api/time_entries` without a header → 201
  → the row's tenant is the personal one → a second person lists nothing);
  `Level_three_end_to_end` (create *Acme* → `POST /api/jobs` with the header → 201 → rename → seed a technician → the
  owner grants technician → the technician reads jobs and cannot create one (403) → the owner removes them → their next
  request is 401);
  `A_header_naming_a_tenant_you_are_not_in_refuses_the_whole_principal` (another's shared, another's personal, a random
  uuid, an unparsable value → 401 on the Data API);
  `A_tenant_role_from_one_tenant_is_absent_in_another` (dispatcher in A, technician in B: `POST /api/jobs` in A → 201,
  in B → 403);
  `Rows_written_in_one_tenant_are_invisible_in_another`;
  `A_self_made_dispatcher_cannot_write_global_regions` (**Review Focus 5**: the dev key seeds a region; the creator of A
  with header A → `POST /api/regions` 403; `PATCH` of that region 403 although a job in A references it — the
  `jobs.exists(…)` term sees project roles only past a global hop; without a header → 403; the dev key's project
  `dispatcher` → 200, so the rule itself is live);
  `The_owner_flag_is_never_a_role` (the owner of A → `POST /api/notes` 403 under `"'owner' in @user.roles"`);
  `A_tenant_role_never_reaches_management` (the dispatcher's Bearer with the header on a Management route → the
  anonymous answer, S1);
  `The_last_owner_cannot_leave_be_removed_or_be_demoted`; `A_personal_tenant_takes_no_members`;
  `Creating_tenants_stops_at_the_cap`;
  `An_api_key_beside_self_service_behaves_exactly_as_before` (the world's dev key and its tenant read their rows; the
  same key on `/auth/v1/tenants` → 403; key + Bearer → 401 ambiguous — spec §14 AC7).
- [ ] **Step 2: Run them** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*SqliteTenancyEngineTests*'` and
  `dotnet test --project test/MMLib.Alvo.Api.Tests.Integration -- --filter-class '*PostgresTenancyEngineTests*'`.
  Expected: PASS. **Prove they can fail:** make `MembershipRules.Decide` keep a stored role that is not assignable → a
  fact goes red; make `Decide` answer `NoTenant` for a requested non-membership → the 401 fact goes red on both engines;
  restore both and record the red runs in the commit body.
- [ ] **Step 3: Docs.** Sweep for the claim, not the file:
  `grep -rn -i "no tenant until S3\|membership.*S3\|memberships: \[\]\|no tenant\b" docs/architecture src --include='*.md' --include='*.cs'`
  — every claim that a person has no tenant, or that membership resolution is S2's stub, is updated;
  `grep -rn "auth/v1/tenants\|alvoBearer" test/teapie test/teapie-field-service test/teapie-field-service-host` → no hits
  (D1: no pin moves). `scripts/check-brief-freshness` → OK. The invariants table in `accounts.md` names, per row, the
  fact that holds it.
- [ ] **Step 4: Commit** — `test(tenancy): levels two and three end to end on both engines, tenant walls held`

### Task 12: Slice gate — Release build, ring1, ring2, security-core checklist

**Files:** none new (fixes only, if a gate finds something).

- [ ] **Step 1: Release build of the whole solution** — `dotnet build MMLib.Alvo.slnx -c Release`; Expected: 0 warnings,
  0 errors (CA rules fire in Release only; if the local SDK differs from CI's, repeat inside
  `mcr.microsoft.com/dotnet/sdk:10.0`).
- [ ] **Step 2: ring1** — `scripts/test-ring1`; Expected: `[ring1] OK`. The only moved baselines are those Tasks 1, 2, 6,
  7 and 8 accepted; anything else is a defect, not a baseline.
- [ ] **Step 3: ring2** — `scripts/test-ring2` (Docker up); Expected: exit 0, including `[lint-api] OK` over both documents.
- [ ] **Step 4: `alvo-security-core-review` checklist**, item by item, recorded for the PR report: SQL — every tenant
  statement binds parameters, table names only from `AlvoFrameworkTables`; the wall — a named tenant must be a
  membership or the whole principal is refused (no tenantless fallback), no header selects only the person's own
  personal tenant; one read per request (AC2); provenance — tenant roles are stored ∩ current `assignableRoles` and
  personal roles come from the descriptor, the S1 path rule untouched, `access` refuses assignable roles, owner never in
  `@user.roles`; invariants — last owner (atomic, per-tenant lock on PostgreSQL, Review Focus 2), cap (per-account lock,
  Review Focus 3), nobody changes their own membership, personal tenant has one member; routes — person only (API key
  403), non-member 404 identical to unknown, feature-disabled names its key, create rate-limited, CORS without
  credentials; events — assignable-only, narrowed at delivery; catalog — the boot refusal still fires without
  `selfService`. Close with **mark this change `needs-deep-review`**.
- [ ] **Step 5: Hand back** to the controller (plan-guard, PR report, PR targeting `accounts/s2-sign-in`). Flag for the
  post-merge mutation run: the EF adapter grows by `EfCoreTenantStore` and `EfCoreAccountStore` grows again, so the
  `data-ef` shards' declared counts go stale (they are the shards nearest their ceiling). The core half has its own
  answer since the final review (I1): the twelve self-service route files are off the core leg (`stryker-config.json`)
  and on a `self-service` leg against `MMLib.Alvo.Accounts.Tests`, whose counts and thresholds are ESTIMATED until its
  first run re-measures them.
