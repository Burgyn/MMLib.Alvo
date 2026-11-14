# S5 — Tokens for integrators: personal and service tokens (`auth.accessTokens`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> Commit this file as `docs/superpowers/plans/2026-11-13-accounts-s5-tokens.md` in Task 1's commit (master plan's
> plan-file name).

**Goal:** A signed-in person mints **personal tokens** that act as them — in one tenant or none, with their tenant roles
read live from the current membership — and a tenant owner mints **service tokens** that act as the tenant with fixed
roles; both travel on the existing `X-Alvo-Api-Key: keyId.secret` channel, over a persisted API-key store on SQLite and
PostgreSQL, are shown once, stored as SHA-256 only, die when their reason dies (expiry, revocation, membership,
ownership, a disabled account), and never reach the Management API or a self-service route.

**Architecture:** One more framework table (`{prefix}_api_keys`) in the EF adapter behind a new internal port
`IAppTokenStore` (Abstractions, `MMLib.Alvo.Auth`), its SQL in `Internal/AppTokenTables.cs`; the S3/S4 member writes and
S2's `SetDisabledAsync` revoke the affected tokens in their own transactions. The core gains the compiled descriptor
policy (`AppTokenPolicy` on `PolicyCatalog`), the key format (`AppTokenSecret`), a routing `IApiKeyStore`
(`ApiKeyStores`: dev keys in memory, `alvo_pat_`/`alvo_svc_` ids in the registry), the two app-token resolutions inside
`ApiKeyContextResolver` (personal through `IMembershipResolution`, the S3 honoured-membership predicate; service from its
stored roles ∩ `assignableRoles`), the Management refusal S1 D8 deferred here, and three routes beside S3/S4's
(`{prefix}/tokens`, mapped by `SelfServiceEndpoints.Map` under the auth prefix). `ApiKeyRecord` is widened compatibly with
`Kind` and `MintedBy` (master plan's fixed shape).

**Tech Stack:** .NET 10, minimal APIs, EF Core relational connections (SQLite + PostgreSQL), `System.Security.Cryptography`
(`RandomNumberGenerator`, `SHA256`, `CryptographicOperations.FixedTimeEquals`), xUnit v3 on MTP, Shouldly, NSubstitute,
Verify, `Microsoft.AspNetCore.TestHost`, `Microsoft.Extensions.TimeProvider.Testing`, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md` — read §3.1
(`auth.accessTokens`, the `service`-without-`selfService` refusal), §4 (the PAT row: owner + expiry + permissions + a
recognisable prefix), §5.7 (the table: Management refuses a personal or service token; membership resolution is a core
service so a personal token reaches tenants exactly as a session does), §6.2 (where tenant roles count), §6.4 (the eighth
bullet: removing a membership refuses every personal token minted for it), §7.1–§7.3, §8 (token secrets never logged),
§11 (S5 row), §11.1, §14 AC3–AC7, §16 B4. Master plan `docs/superpowers/plans/2026-11-13-accounts-00-master.md` (fixed:
`ApiKeyRecord.Kind` = `ApiKeyKind { Configured, Personal, Service }`, `_api_keys`, `auth.accessTokens`, branch
`accounts/s5-tokens`, Review Focus 1). S1 plan D8 (Management's refusal deferred to S5). S3 plan D1 (routes under the auth
prefix), D3 (account-row lock, tenant `version` bump), D4/D5 (`MembershipRules`, effective roles recomputed per request),
D7 (person gate; non-member 404 identical to unknown), D10b (delivery narrowing of a `User` actor), D14 (the `Create`
budget "S5 adds token minting to"). S4 plan D2 (code format, unsalted SHA-256 of a high-entropy secret), D6 (sweep in the
ownership transaction, creation serialised by the bump), D11 (shown-once response, list shape, `status`). Contributor
view: `docs/architecture/accounts.md`. Existing path read for this plan: `src/MMLib.Alvo/Auth/**` (`ApiKeyContextResolver`,
`InMemoryApiKeyStore`, `ApiKeyHash`, `ScopeGate`, `TenantResolver`, `CallerResolution`, `MembershipResolution`),
`src/MMLib.Alvo.Abstractions/Auth/{ApiKeyRecord,ApiKeyScope,IApiKeyStore,AlvoPrincipal}.cs`,
`src/MMLib.Alvo/Management/Internal/ManagementCallerFilter.cs`, `src/MMLib.Alvo/Tenants/SelfService/**`,
`src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs` (the `ActorKind is not User` guard). Prior art read: GitHub
fine-grained personal access tokens (owner, mandatory expiry, per-resource permissions, `github_pat_` prefix, shown once,
"last used", revoked when the owner loses access to the organisation); GitHub secret scanning (prefix-identifiable
tokens); Stripe restricted keys (scoped, shown once); OWASP *Secrets Management* / *Authentication* cheat sheets (store
only a hash of a high-entropy secret; one answer for every failed lookup); the CloudEvents `authcontext` extension
(`authtype` `user` vs `api_key`).

## Decisions (read before Task 1)

- **D1 — Routes under the auth prefix, mapped by `SelfServiceEndpoints.Map` (S3 D1, carried).**
  `POST {AlvoAccountsOptions.Prefix}/tokens`, `GET {prefix}/tokens`, `DELETE {prefix}/tokens/{keyId}` — default
  `/auth/v1/tokens`, tag `tokens`, operation ids `tokens.create`, `tokens.list`, `tokens.revoke`. The same **recorded
  deviation** from §7.2's `{prefix}` as S3/S4: `/api/tokens` would collide with an entity named `tokens`, and minting needs a
  person from a session, which exists only where Accounts is mapped. **No TeaPie path pin moves.**
- **D2 — Names.** S1 already calls a personal or service token an **app token** (an *app* credential, never a
  project-management one); the code uses that word. `MMLib.Alvo.Accounts` already has `AccessTokens` (the JWT issuer), so no
  new type is named `AccessToken*`; only the descriptor key keeps the spec's `auth.accessTokens`. Public:
  `enum ApiKeyKind { Configured, Personal, Service }` and `ApiKeyRecord.Kind { get; init; }` (default `Configured`, so every
  existing record and every host-built one keeps its meaning) and `ApiKeyRecord.MintedBy { get; init; }` (`UserId?`, `null`
  for `Configured`) — spec §7.1 "widened compatibly with its kind and minter". `PrintMembers` prints both, never the hash.
- **D3 — The credential.** Key id = `alvo_pat_` | `alvo_svc_` + base64url (no padding) of 16 bytes from
  `RandomNumberGenerator` (22 chars; not secret, listed by `GET /tokens`). Secret = base64url of 32 bytes (43 chars,
  256 bits). Presented as `keyId.secret` — the existing `X-Alvo-Api-Key` format, so the whole credential starts with the
  scannable prefix (GitHub's precedent, spec §7.1). Stored as `ApiKeyHash.Compute(secret)` — the existing base64 SHA-256
  every `ApiKeyRecord.Sha256Hash` holds; unsalted is right for a 256-bit random secret (S4 D2's reasoning, restated).
  **The prefixes are reserved:** `AlvoAuthOptionsValidator` refuses a configured dev key whose id starts with either, at
  startup, so *prefix ⇔ persisted app token* holds everywhere (D4, D9 rely on it). `AppTokenSecret.ToString()` and every
  record carrying the token print `alvo_pat_***` / `alvo_svc_***`.
- **D4 — One `IApiKeyStore`, two homes.** `ApiKeyStores : IApiKeyStore` (core, internal) routes a prefixed id to
  `IAppTokenStore` (registered by the EF provider, `TryAddSingleton`, the `ITenantStore` precedent) and every other id to
  `InMemoryApiKeyStore` — the dev keys stay the fallback, and no persisted row can shadow a configured key or vice versa.
  `AddAlvoAuth` registers `ApiKeyStores` with `TryAddSingleton<IApiKeyStore>`, so a host that registers its own store owns
  key resolution entirely (documented in `host.md`; such a host's app tokens resolve only if its store delegates).
  **`TouchAsync` is throttled for app tokens:** at most once per key per replica per 60 s (an in-memory high-water mark in
  `ApiKeyStores`), so a token costs one read per request, not a write; `lastUsedAt` is therefore minute-grained — said in
  the route documentation.
- **D5 — Resolution, in `ApiKeyContextResolver`, in this order:** split → find → verify (constant time; an unknown id still
  compares against the dummy hash) → usable (`RevokedAt` null, `ExpiresAt` > now; an app token always has an expiry) →
  **the kind is honoured by the applied descriptor** (D11) → tenant by `TenantResolver` (the header only confirms, spec
  §6.6, unchanged) → the kind's principal (`AppTokenPrincipals`, internal):
  - **Personal:** `User` = the person, `Roles = {authenticated}` (a session's project roles, S2 D15), `Scopes` = the
    record's. Token tenant `null` → no tenant and **no membership read** (a tenantless token never acts "in the personal
    tenant by default" — the tenant is fixed at mint, spec §7.1 "in one tenant or none"). Token tenant set →
    `IMembershipResolution.ResolveAsync(person, tenant)` — `RegistryMembershipResolution`, i.e. `MembershipRules` with the
    honoured-membership predicate and the effective roles of S3 D5 — `Refused` → refuse the principal; else
    `TenantRoles` = its roles, **read live on every request** (AC3; nothing cached).
  - **Service:** `User` = the token's own `UserId` (fresh per token, fills `created_by`), `Roles = {authenticated}`,
    `Tenant` = the token's (always set), `TenantRoles = SelfServicePolicy.Effective(record.RoleNames)` — the stored names
    ∩ the **current** `assignableRoles`, so a role dropped from the descriptor is gone on the next request. Never a
    project role: a service token's roles are tenant roles and count only where §6.2 lets them.
  - **Configured:** exactly today's path.
  Every refusal is `null` → the existing 401. A personal token in a tenant costs two reads (token row, membership); spec
  §14 AC2's one round trip is about sessions — recorded, not hidden.
- **D6 — A personal token's principal is `AlvoActorKind.User`; a service token's is `ApiKey`.** A personal token *acts as
  the person* (§7.1): `@user.id` is theirs, and an event it causes reports `authtype user`, `authid` the person — so S3
  D10b's delivery narrowing (the `ActorKind is not User` guard in `DeliveryRunner`) applies to it unchanged: a queued
  `RunAs.EventActor` delivery of a person removed since loses their tenant roles whether they acted by session or by
  token. **Recorded deviation** from the master plan's description of `User` ("a principal resolved from a person's
  session"): the self-service routes never relied on it — they admit by *channel* (`SelfServicePerson`: only a Bearer is a
  person; an `X-Alvo-Api-Key` is 403 before any resolver runs) — and Management refuses app tokens by D9, not by kind.
  A service token's queued deliveries keep the tenant roles recorded at the event (bounded by `assignableRoles`, S3 D10a),
  exactly as an API key's do today — documented.
- **D7 — Who mints, and with what** (§7.1, §7.2). `POST /tokens` admits a **person** through `SelfServiceCall.EnterAsync`
  (S3 D7): an app token — or any key — presented as `X-Alvo-Api-Key` is **403** (spec: "never a token"); a key beside a
  Bearer is 401 ambiguous. Then per kind:
  - **personal** — any person. `tenantId` optional; given, it must be a membership the descriptor honours
    (`SelfServiceCall.MembershipAsync`) — else 404, byte-identical to an unknown tenant (S3 D7). `roles` must be absent
    (422 at `/roles`: a personal token's roles are its person's, read on every request).
  - **service** — `tenantId` required; the person must be a member (404 otherwise) and an **owner** (403); the tenant must
    be **shared** (409 `conflict`, D8); `roles` required, 1+, unique, each in `assignableRoles` (422 at `/roles/{i}`), and
    each held by the minter in that tenant at mint — judged by the store under the tenant lock (`RolesNotHeld` → 422 at
    `/roles`, "you can only give a token roles you hold in this tenant"). Ownership itself is never a role and never
    carried (§16 B4).
- **D8 — Service tokens are for shared tenants only, and `service` requires `createTenants`.** A personal tenant has
  exactly one member, its owner, forever (§6.1) — a service token's fresh `UserId` would be a second actor in it, and its
  owner never leaves, which is the only thing a service token outlives; a personal token serves the same integration.
  So minting one for a personal tenant is 409, and apply refuses `accessTokens.service: true` without
  `tenancy.selfService.createTenants` (pointer `/auth/accessTokens/service`) — S3's `invitations`-without-`createTenants`
  shape. **Recorded deviation**: stricter than §3.1's "Requires `selfService`" (which this refusal implies).
- **D9 — Management refuses app tokens (S1 D8, closed here).** `ManagementCallerFilter`, after admission: a principal
  whose key id carries an app-token prefix (`AppTokenSecret.IsAppTokenId`, sound by D3's reservation) → **403**
  `forbidden`, fixed detail "Personal and service tokens are app credentials and never manage a project. Use an operator
  session or a configured API key." Resolved first, so an unusable token is the ordinary 401 of any key. `AlvoPrincipal`
  stays unchanged (no public growth for a flag the prefix already carries). In-process `IAlvoManagement` callers publish
  their own principal, as today. The Data API, the commands and `.WithAlvoCaller()` accept app tokens.
- **D10 — How a token dies; every sweep in the transaction that kills its reason** (§7.1, §6.4 eighth bullet, B4):
  - a member **removed or leaving** (`EfCoreTenantStore.RemovedAsync`) → their personal tokens for that tenant, and the
    service tokens they minted for it;
  - an owner **demoted** (`UpdatedAsync`, `IsOwner` true → false) → the service tokens they minted for that tenant;
  - an account **disabled** (`EfCoreAccountStore` `SetDisabledAsync(…, true, …)`) → every token it minted, personal and
    service. **Recorded deviation** for the service half (§7.1 lists only removal and demotion): a banned owner who kept a
    secret is exactly B4's case. Enabling revives nothing (S2's clean-slate ruling for sessions).
  A swept token is `revoked_at`-stamped — dead for good, so a person who rejoins does not get their old tokens back.
  **Mints serialise with every sweep** (S3 D3, S4 D6): lock the minter's account row (zero rows → disabled/unknown), then,
  for a token with a tenant, bump the tenant's `version` and only then read the membership — so a mint racing a removal,
  a demotion or a ban either sees the change or is swept by it. The live checks stay as the second line: a personal
  token's membership is read per request (D5), and expiry is enforced on every resolution.
- **D11 — Default-deny, decided live.** `AppTokenPolicy(bool Personal, bool Service, int MaxLifetimeDays)` compiled with
  the catalog; `None` when `auth.accessTokens` is absent; `Service` only with `tenancy.selfService.createTenants`.
  Routes: `accessTokens` absent → every token route 404 `feature-disabled`, `descriptorKey` `auth.accessTokens`; the
  requested kind off → 404 `feature-disabled`, `descriptorKey` `auth.accessTokens.personal` / `.service` (judged after the
  body names the kind). Resolution refuses a kind the applied descriptor does not enable; re-enabling it revives
  unexpired, unrevoked tokens — a descriptor decision, not a token's (S3 D5's precedent for memberships).
- **D12 — Scopes** (§7.1, §7.2): an array of `"<entity>:<read|write>"` (the `ApiKeyScope.TryParse` form), at least one,
  unique; every entity must exist in the applied schema; **`*` is refused** — a wildcard would grant entities added
  after the mint (the fine-grained-PAT precedent; the person's own session is the unscoped credential). A refusal at
  `/scopes/{i}` lists the descriptor's entity names and echoes nothing sent. An entity removed later simply matches no
  scope.
- **D13 — Bounds.** `name` 1–100 characters under `SelfServiceName`'s rules (one set of rules for every name a person
  types). `expiresAt` required, RFC 3339 with an offset, `> now`, `≤ now + maxLifetimeDays` days — refused at
  `/expiresAt`, never clamped. Lowering `maxLifetimeDays` bounds new mints only; an existing token keeps its expiry
  (recorded). **At most 100 live tokens per minting account** (unexpired, unrevoked; counted under the account lock) →
  409 `conflict` — a builder's bound (§15), because the 10/min budget alone lets an open sign-up grow the table without
  limit (spec D10's sweep concern, for this table).
- **D14 — Responses** (S4 D11 precedent). Create → **201** `{ keyId, token, header, kind, name, tenantId, roles, scopes,
  expiresAt, createdAt }` — the **only** answer that carries `token` (`keyId.secret`); `header` is the configured
  `AlvoAuthOptions.HeaderName`, so an agent knows where to put it; no `Location`. List → 200, newest first: the person's
  personal tokens plus the service tokens of tenants they own (a membership the descriptor honours), each
  `{ keyId, kind, name, tenantId, roles, scopes, mintedBy: { id, displayName }, createdAt, expiresAt, lastUsedAt,
  revokedAt, status }` with `status` = `revoked` > `expired` > `active` — never `token`, never a hash. Revoke → 204,
  idempotent (first `revoked_at` kept); an unknown key id, another person's personal token, and a service token of a
  tenant the caller does not own (or minted, while still its minter) are one identical 404. All `Cache-Control: no-store`.
- **D15 — Rate limit.** `POST /tokens` is charged `AlvoRateLimit.Create` (§7.3 "tenant creation and token minting
  10/min", §11.2's single `Create` key; S3 D14 reserved it) — one budget shared with `POST /tenants`, recorded. List and
  revoke are not charged (S3's reads precedent). No new option.
- **D16 — Public growth, exactly:** Abstractions — `ApiKeyKind` (3 members), `ApiKeyRecord.Kind`, `ApiKeyRecord.MintedBy`,
  `Auth.AccessTokens`, `AuthAccessTokens` (`Personal`, `Service`, `MaxLifetimeDays`). Core — nothing. No new problem slug
  (`feature-disabled`, `conflict`, `forbidden`, `not-found`, `validation` exist). Ports and records `internal`; no new
  `InternalsVisibleTo` direction.

## Global Constraints

- English in everything committed. CRLF + BOM in every new `.cs`/`.csproj` (copy a neighbour's bytes; re-normalise after
  any Bash/Python edit). Short single-purpose methods (~25 lines); XML docs on public/internal members; no inline comments
  by default (`alvo-dotnet-conventions`).
- Public API is the contract (`alvo-architecture-rules`): only D16's symbols, each justified in its commit body.
- Default-deny (D11): no `auth.accessTokens` → no route mints, no app token resolves.
- Tables only through `AlvoFrameworkTables` (`ApiKeysSuffix = "_api_keys"`, `NamesFor`); one DDL text for both engines
  (TEXT ids/instants in `StoredInstant.Text` form, roles and scopes as JSON arrays, no foreign keys — S2/S3 precedent);
  every value a bound parameter.
- **No secret in a URL, a log, a `ToString`, a refusal or a list** (§8). The store receives hashes only; the secret
  exists in memory from mint to the 201 body.
- **Test discipline:** inner loop = the affected project filtered to the class,
  `dotnet test --project <proj> -- --filter-class '*<Class>*'`; ring0 only through the pre-commit hook; ring1 and ring2
  once, in Task 10. Docker for the PostgreSQL legs. Branch `accounts/s5-tokens` from `accounts/s4-invitations`; never push
  to `main`; the controller opens the PR (targeting `accounts/s4-invitations`).
- A moved baseline is accepted only in the task that moves it, judged by `alvo-snapshot-judge` when the Stop hook asks.
- **Release analyzers:** CI builds Release, where CA rules are errors the Debug rings never see — write analyzer-clean code
  (`RandomNumberGenerator.Fill` on a stackalloc'd span, `Convert.ToBase64String` + `TrimEnd`/`Replace` or
  `Base64Url` from `System.Buffers.Text`; `string.Create` over concatenation in loops; no `Count()` where `Length`/`Count`
  exists).
- **Mutation carry-over:** every new EF file (`EfCoreAppTokenStore.cs`, `Internal/AppTokenTables.cs`) goes into
  `stryker-config.accounts-store.json`'s `mutate` and is excluded (`!…`) from `stryker-config.data-ef-rest.json` in the task
  that creates it (its facts compile into Data.Sqlite.Tests, never into EntityFrameworkCore.Tests — `mutation.yml`'s
  header). Every new **route** file (`TokenRoutes.cs`, `TokenResponse.cs`, `TokenListItem.cs`) goes into
  `stryker-config.self-service.json` and is excluded from `stryker-config.json` (the core leg cannot map them; S3's
  reasoning). The pure files (`TokenRequest.cs`, `AppTokenPolicy.cs`, `AppTokenSecret.cs`, `ApiKeyStores.cs`,
  `AppTokenPrincipals.cs`) stay on the core leg, whose suite (`MMLib.Alvo.Tests`) tests them directly. Each task updates
  `mutation.yml`'s comment and `mutants:` estimate (code lines × 0.16, "still an estimate").
- **Prototype fixtures:** any schema change is followed by `scripts/gen-prototype-fixtures --check` (regenerate and commit
  if stale) in the same task.
- Do not touch `examples/field-service`, its TeaPie stacks, the website or the `alvo-descriptor-*` skills (S6).

## Review Focus

1. **A person's membership ends while their integration runs** (master plan Review Focus 1). The personal token's next
   request in that tenant is 401; it stays dead after the person is invited back; a demoted member's personal token loses
   exactly the roles they lost, on the next request. Pinned by Task 4 (store, both engines) and Task 9 (HTTP, both engines).
2. **A mint racing the loss of the minter's power** — a service-token mint vs. its minter's demotion or removal, a
   personal mint vs. the person's removal, any mint vs. a ban: no live token outlives the change. Pinned by Task 4
   (20 iterations, PostgreSQL red-proved).
3. **A token reaching for more than a token** — minting a token, a self-service route, the Management API, a tenant other
   than its own via `X-Alvo-Tenant`, an entity outside its scopes. Pinned by Tasks 5, 6, 7 and 9.
4. **A service token's tenant role on global data** — `POST /api/regions` and the `regions.update` relation into scoped
   `jobs` with a `dispatcher` service token → 403; Management → 403. Pinned by Task 9 (spec §6.2's table through a token).
5. **The descriptor moves under live tokens** — a role dropped from `assignableRoles`, `accessTokens.service` switched off,
   `selfService` removed: the next request reflects it, both kinds. Pinned by Tasks 5 and 9.

---

### Task 1: `auth.accessTokens` in the descriptor, its refusal, and the app-token policy

**Files:**
- Modify: `schema/project.schema.json` (`auth.accessTokens`)
- Modify: `src/MMLib.Alvo.Abstractions/Descriptor/DescriptorBlocks.cs` (`Auth.AccessTokens`, `AuthAccessTokens`)
- Create: `src/MMLib.Alvo/Descriptor/Internal/AccessTokensCheck.cs`; Modify: `DescriptorValidator.cs` (beside
  `SelfServiceCheck.Check`)
- Create: `src/MMLib.Alvo/Auth/Internal/AppTokenPolicy.cs`; Modify: `src/MMLib.Alvo/Rules/PolicyCatalog.cs`
  (`internal AppTokenPolicy AppTokens { get; init; } = AppTokenPolicy.None;`), `src/MMLib.Alvo/Rules/Internal/PolicyCatalogBuilder.cs`
- Create: `examples/_negative/service-tokens-without-shared-tenants.json`; Modify: `examples/_negative/expectations.json`
- Regenerate if stale: `docs/design/f5-admin/generated/*` via `scripts/gen-prototype-fixtures`
- Test: `test/MMLib.Alvo.Tests/Descriptor/AccessTokensCheckTests.cs`, `test/MMLib.Alvo.Tests/Auth/AppTokenPolicyTests.cs`,
  the schema suite per the `alvo-schema-testing` skill

**Interfaces:**
- Schema: `auth.accessTokens` = object, `additionalProperties: false`: `personal` boolean (default `false`), `service`
  boolean (default `false`), `maxLifetimeDays` integer 1–365 (default 90). Descriptions copy §3.1's table, plus D8's
  sentence on `service`. **No cross-key rule in the schema** (the `SignUpCheck` double-report lesson).
- Abstractions (public, D16): `Auth.AccessTokens { get; init; }` of `public sealed record AuthAccessTokens { bool? Personal;
  bool? Service; int? MaxLifetimeDays; }`.
- `AccessTokensCheck.Check(AlvoDescriptor) → IReadOnlyList<DescriptorValidationError>`: `service: true` without
  `tenancy.selfService` → `/auth/accessTokens/service`, `"Service tokens act as a shared tenant, and without tenancy.selfService no tenant has an owner to mint one."`,
  fix `"Add \"tenancy\": { \"enabled\": true, \"selfService\": { \"createTenants\": { … }, \"assignableRoles\": [ … ] } }, or set service to false."`;
  with `selfService` but no `createTenants` → same pointer, `"Service tokens act as a shared tenant, and without createTenants no shared tenant can exist."`,
  fix `"Add \"createTenants\": { \"creatorRoles\": [ … ] } to tenancy.selfService, or set service to false."` (D8).
- `internal sealed record AppTokenPolicy(bool Personal, bool Service, int MaxLifetimeDays)` with `const int
  DefaultMaxLifetimeDays = 90`, `static AppTokenPolicy None` (`false, false, 90`), `bool Any => Personal || Service`,
  `bool Allows(ApiKeyKind kind)` (`Configured` → `true`), `static AppTokenPolicy From(AlvoDescriptor descriptor)` —
  `Service` = declared `&& selfService?.CreateTenants is not null` (belt and braces to the refusal). Consumers read
  `IPolicyCatalogProvider.Current?.AppTokens ?? AppTokenPolicy.None`. (`ApiKeyKind` lands in Task 2; until then `Allows`
  is added in Task 2.)

- [ ] **Step 1: Write the failing facts.**
  `AccessTokensCheckTests`: `Service_tokens_without_self_service_are_refused`;
  `Service_tokens_without_create_tenants_are_refused`; `Personal_tokens_need_nothing_else` (level-1 descriptor +
  `personal: true` accepted); `The_spec_level_three_descriptor_is_accepted` (§3's example with `accessTokens`).
  `AppTokenPolicyTests`: `Absent_is_none`; `Max_lifetime_defaults_to_ninety`;
  `Service_is_off_without_create_tenants_even_if_declared` (policy built from a descriptor the check would refuse);
  `Both_kinds_carry_through`.
  Schema: positive with all three keys; `maxLifetimeDays` 0 and 366 refused; an unknown key under `accessTokens` refused;
  the negative file (expectation pointer `/auth/accessTokens/service`).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AccessTokensCheckTests*' --filter-class '*AppTokenPolicyTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement**; then `scripts/gen-prototype-fixtures --check` (regenerate + include if stale).
- [ ] **Step 4: Run** the Step 2 command, `dotnet test --project test/MMLib.Alvo.Schema.Tests` and
  `dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-class '*PublicApiApprovalTests*'`. Expected: PASS
  after accepting exactly the Abstractions PublicApi (`Auth.AccessTokens`, `AuthAccessTokens` and its three properties),
  `negative-error-output.verified.txt` (the new case) and any schema-description snapshot the schema suite holds.
- [ ] **Step 5: Commit** (with this plan file) — `feat(descriptor): auth.accessTokens, and service tokens need a shared tenant to act as`

### Task 2: The record widened, the credential format, and reserved prefixes

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Auth/ApiKeyKind.cs`; Modify: `src/MMLib.Alvo.Abstractions/Auth/ApiKeyRecord.cs`
  (`Kind`, `MintedBy`, `PrintMembers`)
- Create: `src/MMLib.Alvo/Auth/Internal/AppTokenSecret.cs`
- Modify: `src/MMLib.Alvo/Auth/Internal/AlvoAuthOptionsValidator.cs` (reserved prefixes), `AppTokenPolicy.cs` (`Allows`)
- Test: `test/MMLib.Alvo.Tests/Auth/AppTokenSecretTests.cs`, `test/MMLib.Alvo.Tests/Auth/AlvoAuthOptionsValidatorTests.cs`,
  `test/MMLib.Alvo.Tests/Auth/ApiKeyRecordTests.cs` (create if absent), `AppTokenPolicyTests`

**Interfaces:**
```csharp
public enum ApiKeyKind { Configured, Personal, Service }        // Abstractions, MMLib.Alvo.Auth; doc per member
// ApiKeyRecord gains:
public ApiKeyKind Kind { get; init; }                           // default Configured
public UserId? MintedBy { get; init; }                          // the person who minted it; null for Configured

internal readonly struct AppTokenSecret                        // core, Auth/Internal
{
    internal const string PersonalPrefix = "alvo_pat_";
    internal const string ServicePrefix = "alvo_svc_";
    internal static AppTokenSecret New(ApiKeyKind kind);        // Personal | Service; throws for Configured
    internal string KeyId { get; }                              // prefix + base64url(16 bytes)
    internal string Presented { get; }                          // KeyId + "." + base64url(32 bytes) — the only accessor yielding the secret
    internal string Hash { get; }                               // ApiKeyHash.Compute(secret part)
    internal static bool IsAppTokenId(string keyId);            // either prefix, ordinal
    internal static ApiKeyKind KindOf(string keyId);            // Configured when no prefix
    public override string ToString();                          // KeyId's prefix + "***"
}
```
- Validator: a dev key whose `KeyId` starts with either prefix → the existing failure shape,
  `"Alvo:Auth:DevKeys:{i}:KeyId starts with '{prefix}', which Alvo reserves for personal and service tokens. Rename the key."`.

- [ ] **Step 1: Write the failing facts.** `AppTokenSecretTests`: `A_personal_and_a_service_credential_carry_their_prefixes`
  (key id length 31, secret 43, base64url alphabet, one `.`); `A_thousand_credentials_are_distinct`;
  `The_hash_is_the_api_key_hash_of_the_secret_part` (`ApiKeyHash.Matches`); `A_credential_never_prints_its_secret`
  (`ToString`, interpolation); `Configured_is_not_mintable`; theory `Kind_of_a_key_id` (prefixed, unprefixed,
  `alvo_patx`, empty). `AlvoAuthOptionsValidatorTests`: theory `A_dev_key_with_a_reserved_prefix_is_refused` (both).
  `ApiKeyRecordTests`: `A_record_defaults_to_configured_with_no_minter`; `Printing_a_record_shows_kind_and_minter_never_the_hash`.
  `AppTokenPolicyTests`: `Allows_configured_always_and_each_kind_only_when_on`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*AppTokenSecretTests*' --filter-class '*AlvoAuthOptionsValidatorTests*' --filter-class '*ApiKeyRecordTests*' --filter-class '*AppTokenPolicyTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command, `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*ApiKeyContextResolverTests*' --filter-class '*InMemoryApiKeyStoreTests*'`
  (unchanged behaviour) and the Abstractions `*PublicApiApprovalTests*`. Expected: PASS after accepting exactly
  `ApiKeyKind` (3 members), `ApiKeyRecord.Kind`, `ApiKeyRecord.MintedBy` — justified in the commit body against
  *public is the contract*: `ApiKeyRecord` is the public port's record, the master plan fixed `Kind` on it, and a host's own
  `IApiKeyStore` must be able to read and write both.
- [ ] **Step 5: Commit** — `feat(auth): api keys know their kind and minter, and alvo_pat_/alvo_svc_ are reserved`

### Task 3: The app-token store I — table, finding, touching, minting, listing, revoking

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs` (`ApiKeysSuffix`; `NamesFor`)
- Create: `src/MMLib.Alvo.Abstractions/Auth/IAppTokenStore.cs` (port + records, `internal`, namespace `MMLib.Alvo.Auth`)
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AppTokenTables.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreAppTokenStore.cs`
- Modify: `Internal/SystemSchemaInitializer.cs` (table + indexes), `AlvoEfCoreProvider.cs` (`TryAddSingleton<IAppTokenStore>`)
- Modify: `stryker-config.accounts-store.json`, `stryker-config.data-ef-rest.json`, `.github/workflows/mutation.yml`
  (header paragraph + `accounts-store` `mutants` estimate)
- Create: `test/_shared/ef/AppTokenStoreFacts.cs` (abstract), `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAppTokenStoreTests.cs`,
  `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAppTokenStoreTests.cs` (link the shared file in both csproj —
  the `InvitationStoreFacts` precedent)

**Interfaces (all `internal`):**
```csharp
interface IAppTokenStore
{
    ValueTask<ApiKeyRecord?> FindAsync(string keyId, CancellationToken ct);           // any state; the resolver judges
    ValueTask TouchAsync(string keyId, DateTimeOffset usedAt, CancellationToken ct);
    Task<MintTokenOutcome> MintAsync(MintTokenRequest request, CancellationToken ct);
    Task<IReadOnlyList<AppTokenView>> ListAsync(UserId person, CancellationToken ct); // newest first; D14's set, unfiltered by policy
    Task<RevokeTokenStatus> RevokeAsync(TokenRevocation revocation, CancellationToken ct);
}
sealed record MintTokenRequest(
    string KeyId, ApiKeyKind Kind, string SecretHash, string Name, UserId Minter, UserId Actor, TenantId? Tenant,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Scopes, DateTimeOffset ExpiresAt, int MaxLive, DateTimeOffset Now);
enum MintTokenStatus { Minted, NoAccount, NotFound, NotOwner, PersonalTenant, RolesNotHeld, LimitReached }
readonly record struct MintTokenOutcome(MintTokenStatus Status, AppTokenView? Token);
sealed record AppTokenView(
    string KeyId, ApiKeyKind Kind, string Name, UserId MintedBy, string? MinterName, TenantId? Tenant,
    IReadOnlyList<string> Roles, IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);
sealed record TokenRevocation(string KeyId, UserId Actor, DateTimeOffset Now);
enum RevokeTokenStatus { Revoked, NotFound }
```
- DDL (prefix `p`): `p_api_keys(key_id TEXT PK, kind TEXT NOT NULL, secret_hash TEXT NOT NULL, name TEXT NOT NULL,
  user_id TEXT NOT NULL, minted_by TEXT NOT NULL, tenant_id TEXT NULL, roles TEXT NOT NULL, scopes TEXT NOT NULL,
  created_at TEXT NOT NULL, expires_at TEXT NOT NULL, revoked_at TEXT NULL, last_used_at TEXT NULL)` + indexes
  `minted_by`, `tenant_id`. `kind` stored `personal`/`service`. `user_id` = the person (personal) or `Actor` (service, a
  fresh `UserId.New()` the core passes).
- `FindAsync` maps a row to `ApiKeyRecord` (`Kind`, `MintedBy`, `User`, `Tenant`, `RoleNames` from `roles`, `Scopes` parsed
  with `ApiKeyScope.TryParse` — a row with an unparsable scope maps to `null`, never to fewer scopes, the
  `InMemoryApiKeyStore` rule).
- `MintAsync`, one transaction (D10): lock the minter's account (S3 D3 `LockAccountSql`, zero rows → `NoAccount`); count
  live tokens `minted_by = @minter AND revoked_at IS NULL AND expires_at > @now` `>= MaxLive` → `LimitReached`; with a
  tenant: bump its `version` (S3 `MemberWriteAsync`'s first statement, through `TenantTables`), read the minter's
  membership — none → `NotFound`; service: personal tenant → `PersonalTenant`, not owner → `NotOwner`, a requested role
  not among the membership's stored role names → `RolesNotHeld`; insert → `Minted` with the view. The store stores what it
  is given (D12/D13 bounds are the core's).
- `ListAsync`: `kind = 'personal' AND minted_by = @p` ∪ `kind = 'service' AND tenant_id IN (owner memberships of @p in
  shared tenants)`, left-joined to `{auth_accounts}` for `MinterName`, `ORDER BY created_at DESC, key_id`.
- `RevokeAsync`: `UPDATE … SET revoked_at = COALESCE(revoked_at, @now) WHERE key_id = @id AND ((kind = 'personal' AND
  minted_by = @actor) OR (kind = 'service' AND (minted_by = @actor OR EXISTS (owner membership of @actor in tenant_id))))`
  — zero rows → `NotFound` (unknown and not-yours are one answer), else `Revoked` (idempotent).
- `TouchAsync`: `UPDATE … SET last_used_at = @at WHERE key_id = @id` (throttling is the core's, D4).

- [ ] **Step 1: Write the failing facts** in `AppTokenStoreFacts` (accounts via `IAccountStore.SignInAsync`, tenants via
  `CreateSharedAsync`, members via `TenantSeed`):
  `A_personal_token_round_trips_as_an_api_key_record` (kind, minter, user, tenant, no roles, scopes, expiry; no secret
  column holds anything but the hash); `A_tenantless_personal_token_needs_only_an_enabled_account`;
  `A_personal_token_for_a_tenant_needs_a_membership_of_it` (`NotFound` for another tenant and an unknown one);
  `A_service_token_needs_an_owner_of_a_shared_tenant` (`NotOwner`; `PersonalTenant`; `Minted` with `user_id` = the actor,
  not the minter); `A_service_token_carries_only_roles_its_minter_holds` (`RolesNotHeld`, no row);
  `A_disabled_or_unknown_minter_mints_nothing` (`NoAccount`); `The_live_cap_counts_only_unexpired_unrevoked_tokens`
  (cap 2: expired and revoked ones free a slot);
  `The_list_holds_my_personal_tokens_and_service_tokens_of_tenants_I_own` (a member who is not an owner sees no service
  token; newest first; `MinterName`); `Revoking_is_the_minter_s_or_any_owner_s_and_idempotent` (personal: only its
  minter; service: its minter and a second owner; a non-owner member and a stranger → `NotFound`; the second revoke keeps
  the first instant); `Touch_records_the_last_use`;
  `Concurrent_mints_at_the_cap_never_exceed_it` (five via `Task.WhenAll`, cap 2, 20 iterations → exactly two `Minted`);
  `The_api_keys_table_is_a_reserved_framework_name`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteAppTokenStoreTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement**, including the two stryker configs and the `mutation.yml` comment.
- [ ] **Step 4: Run** the Step 2 command,
  `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlAppTokenStoreTests*'`
  and `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*Introspect*'`. Expected:
  PASS. **Prove the lock matters:** drop the account-row lock → the cap fact goes red on PostgreSQL; restore; record it in
  the commit body.
- [ ] **Step 5: Commit** — `feat(data-ef): a persisted api-key registry that mints, lists and revokes app tokens`

### Task 4: The app-token store II — a token dies with its reason, in the same transaction

**Files:**
- Modify: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AppTokenTables.cs` (`RevokeForMemberSql`,
  `RevokeMintedServiceSql`, `RevokeMintedByAccountSql`), `EfCoreTenantStore.Members.cs` (`RemovedAsync`, `UpdatedAsync`),
  `EfCoreAccountStore.Sessions.cs` (`DisabledAsync`), `EfCoreTenantStore.cs` / `EfCoreAccountStore.cs` (`EnsureTablesAsync`
  creates the `_api_keys` table too, so a sweep never meets a missing table)
- Modify: `mutation.yml` comment only if line counts moved materially
- Test: `test/_shared/ef/AppTokenStoreFacts.cs`, `test/_shared/ef/TenantStoreFacts.cs` (unchanged facts keep passing),
  `test/_shared/ef/AccountStoreFacts.cs`

**Interfaces:**
- `RevokeForMemberSql`: `UPDATE {api_keys} SET revoked_at = @now WHERE tenant_id = @tenant_id AND revoked_at IS NULL AND
  ((kind = 'personal' AND user_id = @member) OR (kind = 'service' AND minted_by = @member))` — in `RemovedAsync` after
  the delete (removal and leaving alike), beside S4's invitation sweep.
- `RevokeMintedServiceSql`: `… WHERE tenant_id = @tenant_id AND kind = 'service' AND minted_by = @member AND revoked_at IS
  NULL` — in `UpdatedAsync` when `update.IsOwner == false && member.IsOwner`, beside S4's sweep.
- `RevokeMintedByAccountSql`: `… WHERE minted_by = @account_id AND revoked_at IS NULL` — in `DisabledAsync`, beside the
  session revocation; `EnableAccount` touches no token (D10).
- A refused write (`LastOwner`) rolls the sweep back with it — the sweeps run after the judgement, inside the same
  transaction.

- [ ] **Step 1: Write the failing facts** in `AppTokenStoreFacts`:
  `Removing_a_member_revokes_their_personal_tokens_for_that_tenant_only` (their tenantless and other-tenant tokens live);
  `A_member_leaving_revokes_the_same`; `Removing_an_owner_revokes_the_service_tokens_they_minted_and_no_one_else_s`;
  `Demoting_an_owner_revokes_their_service_tokens_and_leaves_their_personal_tokens`;
  `A_refused_demotion_revokes_nothing` (`LastOwner`); `Changing_a_member_s_roles_revokes_nothing` (personal roles are
  live, service roles fixed — §7.1 "then");
  `Disabling_an_account_revokes_every_token_it_minted_and_enabling_revives_none`;
  `A_rejoined_member_s_old_personal_token_stays_revoked` (remove → `TenantSeed.AddMemberAsync` again → `RevokedAt` set);
  **Review Focus 2** (20 iterations each, fresh accounts):
  `A_service_mint_racing_its_minter_s_demotion_never_outlives_it` (owners A, B; `Task.WhenAll(A mints, B demotes A)` →
  `NotOwner` and no row, or a row with `revoked_at` set);
  `A_personal_mint_racing_the_person_s_removal_never_outlives_it`;
  `A_mint_racing_a_ban_never_outlives_it` (`SetDisabledAsync` vs `MintAsync` → `NoAccount`, or a revoked row).
  `AccountStoreFacts`: `Disabling_revokes_sessions_and_tokens_together` (one fact pinning both effects of one call).
- [ ] **Step 2: Run to see them fail** — the Task 3 SQLite command plus
  `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteAccountStoreTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 commands, their PostgreSQL counterparts (`*PostgreSqlAppTokenStoreTests*`,
  `*PostgreSqlAccountStoreTests*`, `*PostgreSqlTenantStoreTests*`, `*PostgreSqlInvitationStoreTests*`) and
  `--filter-class '*SqliteTenantStoreTests*' --filter-class '*SqliteInvitationStoreTests*'` (S3/S4 facts unchanged).
  Expected: PASS. **Prove the serialisation matters:** route the service mint around the tenant bump → the demotion race
  goes red on PostgreSQL; restore; record it in the commit body.
- [ ] **Step 5: Commit** — `feat(data-ef): a token dies with its minter's membership, ownership or account, in that transaction`

### Task 5: Resolving app tokens — routing store, live membership, fixed service roles

**Files:**
- Create: `src/MMLib.Alvo/Auth/Internal/ApiKeyStores.cs`, `src/MMLib.Alvo/Auth/Internal/AppTokenPrincipals.cs`
- Modify: `src/MMLib.Alvo/Auth/Internal/ApiKeyContextResolver.cs` (D5's kind step), `src/MMLib.Alvo/Auth/Setup.cs`
  (`InMemoryApiKeyStore` registered as itself; `IApiKeyStore` → `ApiKeyStores`, both `TryAdd`), the remarks of
  `MembershipResolution.cs` / `RegistryMembershipResolution.cs` ("personal tokens later" → now),
  `src/MMLib.Alvo.Abstractions/Identity/AlvoActorKind.cs` (`User`'s doc: "a signed-in person, by session or by a personal
  token", D6), `src/MMLib.Alvo/Events/Internal/DeliveryRunner.cs` (remarks only: D10b covers personal tokens)
- Test: `test/MMLib.Alvo.Tests/Auth/ApiKeyStoresTests.cs`, `test/MMLib.Alvo.Tests/Auth/AppTokenResolutionTests.cs`,
  `test/MMLib.Alvo.Tests/Auth/ApiKeyContextResolverTests.cs` (existing facts unchanged)

**Interfaces:**
```csharp
internal sealed class ApiKeyStores(InMemoryApiKeyStore devKeys, IAppTokenStore? appTokens, TimeProvider clock) : IApiKeyStore
{
    internal static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(60);
    // FindAsync: IsAppTokenId → appTokens?.FindAsync (null store → null); else devKeys.
    // TouchAsync: app token → only when the last touch from this replica is >= TouchInterval old.
}
internal sealed class AppTokenPrincipals(IMembershipResolution memberships, IPolicyCatalogProvider catalogs)
{
    // null = refuse. Configured never reaches it.
    internal ValueTask<AlvoPrincipal?> ResolveAsync(ApiKeyRecord token, TenantId? tenant, CancellationToken ct);
}
```
- `ApiKeyContextResolver` gains `AppTokenPrincipals` (DI, internal constructor). After `IsUsable`: `token.Kind is not
  Configured` → `catalogs.Current?.AppTokens ?? None` must `Allow` the kind (service also needs `SelfService.Enabled`) →
  `TenantResolver.TryResolve` → `AppTokenPrincipals.ResolveAsync` → touch → principal. `Configured` → the unchanged path.
- Personal principal: `Context { User = token.User, Roles = {authenticated}, Tenant, TenantRoles, ActorKind = User }`,
  `Scopes = token.Scopes`, `KeyId = token.KeyId`. Service principal: `ActorKind = ApiKey`, `TenantRoles =
  selfService.Effective(token.RoleNames)`.

- [ ] **Step 1: Write the failing facts** (NSubstitute `IAppTokenStore`, `FakeTimeProvider`, a real
  `RegistryMembershipResolution` over a substituted `ITenantStore`, policies built from descriptors):
  `ApiKeyStoresTests`: `Dev_keys_resolve_from_configuration_and_app_tokens_from_the_registry`;
  `A_prefixed_id_never_reaches_the_dev_keys_and_an_unprefixed_one_never_the_registry`;
  `Without_a_registry_an_app_token_is_unknown`; `App_token_touches_are_throttled_to_one_a_minute` (three touches in 59 s →
  one store call; at 60 s → a second); `Dev_key_touches_are_unthrottled`.
  `AppTokenResolutionTests`: `A_personal_token_acts_as_its_person_with_live_tenant_roles` (membership roles change between
  two resolutions → the second sees the change; `ActorKind.User`; `Roles` = `{authenticated}`);
  `A_tenantless_personal_token_acts_in_no_tenant_and_reads_no_membership` (even with `personalTenant` on — no
  `FindMembershipAsync` call); `A_personal_token_whose_membership_ended_is_refused`;
  `A_personal_token_in_a_personal_tenant_holds_the_personal_roles`;
  `A_service_token_acts_as_itself_with_its_roles_still_assignable` (a role dropped from `assignableRoles` is gone;
  `ActorKind.ApiKey`; `User` = the token's own id; no project role but `authenticated`);
  `A_header_naming_another_tenant_refuses_either_kind` and `A_header_naming_its_own_tenant_confirms_it`;
  theory `A_kind_the_descriptor_does_not_enable_is_refused` (no `accessTokens`; `personal: false`; `service: false`;
  `selfService` removed under a service token) and `Re_enabling_revives_an_unexpired_token`;
  `An_expired_or_revoked_token_is_refused_before_any_membership_read`;
  `A_wrong_secret_is_refused_like_an_unknown_id` (the dummy-hash path is taken; no membership read).
  `ApiKeyContextResolverTests`: the existing facts unchanged.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*ApiKeyStoresTests*' --filter-class '*AppTokenResolutionTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command, `--filter-class '*ApiKeyContextResolverTests*' --filter-class '*InMemoryApiKeyStoreTests*'`,
  `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*DeliveryRunner*'` (D6: narrowing unchanged),
  `dotnet test --project test/MMLib.Alvo.Api.Tests` (every key path unchanged) and
  `dotnet test --project test/MMLib.Alvo.Identity.Tests -- --filter-class '*AlvoIdentityRegistrationTests*'`. Expected:
  PASS. **Prove liveness:** cache the membership read for 60 s in `AppTokenPrincipals` → the live-roles fact goes red;
  restore; record it.
- [ ] **Step 5: Commit** — `feat(auth): personal tokens act as their person with live tenant roles, service tokens as themselves`

### Task 6: The Management API refuses app tokens (S1 D8)

**Files:**
- Modify: `src/MMLib.Alvo/Management/Internal/ManagementCallerFilter.cs` (the refusal + remarks),
  `src/MMLib.Alvo/Auth/Internal/CallerResolution.cs` (the remarks table's API-key row: "a personal or service token is
  refused by the Management filter"), `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs` (an `AppTokenForbidden()`
  using the existing `forbidden` type, fixed detail per D9)
- Test: `test/MMLib.Alvo.Api.Tests/AppTokenManagementTests.cs` (a substituted `IAppTokenStore` registered in the Api world;
  the token minted with `AppTokenSecret.New`)

- [ ] **Step 1: Write the failing facts:**
  `A_personal_token_is_refused_by_every_management_route` and `A_service_token_is_refused_by_every_management_route`
  (a descriptor whose `access.viewer` is `"'authenticated' in @user.roles"` — the token would otherwise pass — → 403 with
  D9's detail, on a read and a write route); `An_unusable_app_token_is_401_like_any_key` (wrong secret, expired);
  `A_configured_key_still_manages_as_before`; `A_bearer_beside_an_app_token_on_management_is_judged_by_the_token`
  (Management ignores `Authorization`, §5.7 → 403); `The_same_token_reads_the_data_api` (200 within its scope — the refusal
  is Management's only).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*AppTokenManagementTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command and `--filter-class '*CredentialReadingTests*' --filter-class '*TenantRoleManagementTests*' --filter-class '*PersonCredentialTests*'`.
  Expected: PASS.
- [ ] **Step 5: Commit** — `feat(management): personal and service tokens never manage a project`

### Task 7: `POST /tokens` — a person mints, the secret is shown once

**Files:**
- Create: `src/MMLib.Alvo/Tenants/SelfService/TokenRequest.cs` (the body reader, pure), `TokenRoutes.cs`,
  `TokenResponse.cs`, `TokenProblems.cs` (feature keys `auth.accessTokens`, `.personal`, `.service`; status → problem)
- Modify: `SelfServiceEndpoints.cs` (`MapTokens`, tag `tokens`, `limits.For(AlvoRateLimit.Create)` on the POST)
- Modify: `src/MMLib.Alvo.Accounts/Internal/AccountsDocumentation.Tokens.cs` (new), `AccountsSchemas.cs`,
  `AccountsDocumentTransformer.cs`
- Modify: `stryker-config.self-service.json` (+ `TokenRoutes.cs`, `TokenResponse.cs`, `TokenProblems.cs`),
  `stryker-config.json` (their `!…`), `mutation.yml` (`self-service` comment + estimate)
- Modify: `test/_shared/accounts/descriptors/team-tracker.alvo.json` (`"accessTokens": { "personal": true, "service": true, "maxLifetimeDays": 30 }`),
  `time-tracker.alvo.json` (`"accessTokens": { "personal": true }`), `test/_shared/accounts/AccountsWorld.Tenants.cs`
  (`MintAsync(string access, object body)`, `AsTokenAsync(HttpMethod, string path, string token, string? tenant = null, object? body = null)`)
- Test: `test/MMLib.Alvo.Tests/Tenants/TokenRequestTests.cs`, `test/MMLib.Alvo.Accounts.Tests/TokenRoutesTests.cs`,
  `test/MMLib.Alvo.Accounts.Tests/AccountsOpenApiTests.cs`

**Interfaces:**
- `internal sealed record TokenRequest(string Name, ApiKeyKind Kind, TenantId? Tenant, IReadOnlyList<string> Roles,
  IReadOnlyList<ApiKeyScope> Scopes, DateTimeOffset ExpiresAt)`;
  `TokenRequest.ReadAsync(HttpRequest, AlvoApiOptions, TokenRules rules, CancellationToken) → (TokenRequest?, IReadOnlyList<AlvoViolation>)`
  through `SelfServiceBody`, where `internal sealed record TokenRules(AppTokenPolicy Tokens, SelfServicePolicy SelfService,
  IReadOnlySet<string> Entities, DateTimeOffset Now)`. Members: `name` (required), `kind` (`"personal"`|`"service"`),
  `tenantId` (uuid string; required for service), `roles` (service: required 1+, unique, each assignable; personal:
  must be absent), `scopes` (D12), `expiresAt` (D13); an unknown member → a violation at its pointer. Fixed sentences only.
- Order (S3 Task 7's gate contract): CORS → no-store → `Create` budget → `SelfServiceCall.EnterAsync(http, _ =>
  TokenProblems.Off(tokens))` (person, then `auth.accessTokens`) → JSON content type → body (422) → the kind's feature
  (404 `auth.accessTokens.personal`/`.service`) → personal + `tenantId`: `call.MembershipAsync` (404) → mint through the
  store with `AppTokenSecret.New(kind)` (only `Hash` goes down; `Actor` = the person for personal, `UserId.New()` for
  service; `MaxLive` 100) → status → problem (`NoAccount` → 401 `invalid_token`, `NotFound` → `NoSuchTenant()`,
  `NotOwner` → 403, `PersonalTenant` → 409 `conflict`, `RolesNotHeld` → 422 `/roles`, `LimitReached` → 409 `conflict`) →
  201 `TokenResponse` (D14). `TokenResponse` overrides `PrintMembers`: `Token = alvo_pat_***`.

- [ ] **Step 1: Write the failing facts.**
  `TokenRequestTests` (pure): `A_personal_request_needs_no_tenant_and_no_roles`; theory
  `Each_bound_is_refused_at_its_pointer` (`name` missing/empty/101 chars/control char; `kind` missing/`"owner"`;
  `tenantId` not a uuid; service without `tenantId`; `roles` on personal → `/roles`; service `roles` missing/empty/
  duplicate/`admin`/undeclared/not assignable → `/roles` or `/roles/{i}`; `scopes` missing/empty/`"jobs"`/`"jobs:READ"`/
  `"*:read"`/an unknown entity/duplicate → `/scopes` or `/scopes/{i}`; `expiresAt` missing/past/no offset/
  now + maxLifetimeDays + 1 s → `/expiresAt`; an unknown member → its pointer);
  `An_unknown_entity_is_refused_with_the_descriptor_s_entities` (message lists `jobs, notes, regions`, never the sent
  name); `No_violation_echoes_what_was_sent`; `Exactly_max_lifetime_is_accepted`.
  `TokenRoutesTests` (`AccountsWorld`, `team-tracker`): `A_person_mints_a_personal_token_and_sees_it_once` (201, `token`
  starts `alvo_pat_`, `header` `X-Alvo-Api-Key`, `Cache-Control: no-store`; the token reads `jobs` in its tenant —
  200 — and nothing else (403 on `notes` outside its scopes));
  `An_owner_mints_a_service_token_that_acts_as_the_tenant` (201 `alvo_svc_`; a row it creates carries `created_by` = a
  user id that is neither the owner's nor any account's);
  `A_token_cannot_mint_a_token` (**Review Focus 3**: personal and service tokens as `X-Alvo-Api-Key` → 403; a key beside
  a Bearer → 401); `No_credential_is_a_bearer_challenge`;
  `A_member_who_is_not_an_owner_cannot_mint_a_service_token` (403);
  `A_stranger_s_and_an_unknown_tenant_are_the_same_404` (both kinds, byte-identical);
  `A_personal_tenant_takes_no_service_token` (409); `A_service_token_carries_only_roles_its_owner_holds` (owner with
  `dispatcher` asks for `technician` → 422 `/roles`);
  `Without_access_tokens_every_route_is_404_naming_its_key` and `A_kind_switched_off_is_404_naming_its_key` (both kinds);
  `Minting_answers_429_after_the_create_budget_and_shares_it_with_tenant_creation` (`Create = 2`: one tenant + one mint,
  then 429 with `Retry-After`); `The_live_cap_is_409` (seeded through the store with `MaxLive` reached);
  `A_non_json_body_is_415`; `An_allowed_origin_gets_cors_headers_on_the_token_routes`.
  `AccountsOpenApiTests`: `tokens.create` appears once; its 201 schema is the only one with a `token` property.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*TokenRequestTests*'` and
  `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*TokenRoutesTests*' --filter-class '*AccountsOpenApiTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 commands, every S3/S4 class that loads `team-tracker` (`--filter-class '*TenantRoutesTests*'
  --filter-class '*MemberRoutesTests*' --filter-class '*InvitationRoutesTests*' --filter-class '*PersonResolverTests*'`), then
  `scripts/lint-api`. Expected: PASS after accepting the Accounts OpenAPI snapshot (one operation, judged); `[lint-api] OK`
  (a Vacuum finding is fixed in the transformer, never muted).
- [ ] **Step 5: Commit** — `feat(tokens): a person mints personal tokens, an owner service tokens, and the secret is shown once`

### Task 8: `GET /tokens` and `DELETE /tokens/{keyId}`

**Files:**
- Create: `src/MMLib.Alvo/Tenants/SelfService/TokenListItem.cs`; Modify: `TokenRoutes.cs`, `SelfServiceEndpoints.cs`,
  `AccountsDocumentation.Tokens.cs`, `stryker-config.self-service.json` / `stryker-config.json` (`TokenListItem.cs`)
- Test: `test/MMLib.Alvo.Accounts.Tests/TokenRoutesTests.cs`, `AccountsOpenApiTests.cs`

**Interfaces:**
- `GET {prefix}/tokens` (`tokens.list`): CORS → no-store → `SelfServiceCall.EnterAsync(http, _ => TokenProblems.Off(tokens))`
  → `ListAsync(person)` → drop service tokens whose tenant membership the descriptor does not honour (`selfService` off →
  none; S3's "every listing honours what a request honours") → 200 array of `TokenListItem` (D14), `status` at `call.Now`.
- `DELETE {prefix}/tokens/{keyId}` (`tokens.revoke`): same gates → `RevokeAsync` → `Revoked` → 204, `NotFound` →
  `ProblemResultFactory.NotFound()`. `{keyId}` is a plain string segment (not secret; never the `.secret` part — a path
  containing `.` beyond the key id is simply an unknown id).

- [ ] **Step 1: Write the failing facts:**
  `The_list_shows_my_tokens_and_my_tenants_service_tokens_never_a_secret` (no `token` member, no hash; `mintedBy` with
  display name; newest first; a second owner sees the first owner's service token; a plain member sees none);
  `The_list_shows_each_state` (active; expired via `Clock.Advance`; revoked); `Last_used_is_recorded_within_a_minute`
  (use the token, list → `lastUsedAt` set; use again 10 s later → unchanged);
  `Revoking_kills_the_token_on_its_next_request` (204, then the token → 401); `Revoking_twice_is_204`;
  `Any_owner_revokes_a_service_token_and_nobody_else_s_personal_one` (second owner → 204 on the service token; the same
  owner on the minter's personal token → 404 byte-identical to an unknown id);
  `A_member_s_list_after_self_service_is_switched_off_shows_only_personal_tokens`;
  `Without_access_tokens_list_and_revoke_are_404_naming_its_key`; `A_token_cannot_list_or_revoke` (403).
  `AccountsOpenApiTests`: `tokens.list` and `tokens.revoke` appear once each; no token operation takes a secret in its path
  or query.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*TokenRoutesTests*' --filter-class '*AccountsOpenApiTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command and `scripts/lint-api`; Expected: PASS after accepting the Accounts OpenAPI
  snapshot (two operations, judged); `[lint-api] OK`.
- [ ] **Step 5: Commit** — `feat(tokens): people list the tokens they answer for and revoke them, never seeing a secret again`

### Task 9: Tokens end to end on both engines, no secret in any log, and the docs

**Files:**
- Create: `test/_shared/accounts/AppTokenEngineFacts.cs` (abstract, `Engine`-parameterised, `team-tracker` +
  `time-tracker`), `test/MMLib.Alvo.Accounts.Tests/SqliteAppTokenEngineTests.cs`,
  `test/MMLib.Alvo.Api.Tests.Integration/PostgresAppTokenEngineTests.cs` (the csproj already links `../_shared/accounts/*.cs`)
- Modify: `test/MMLib.Alvo.Accounts.Tests/SecretsNeverLoggedTests.cs`
- Modify: `docs/architecture/accounts.md` (an S5 section after S4's: what you can build — an integration that reads a
  tenant's jobs with a personal token, a billing system with a service token — with the descriptor and the curl lines; the
  table; the resolution order of D5; the routes; the invariants table below, each row naming its fact; D1–D16 as recorded
  decisions/deviations; *Not in S5*: token scopes beyond entity × read/write, a per-token IP allow-list, OAuth client
  credentials (spec D3), a sweep of revoked/expired rows (spec D10's #527 grows by this table)), `docs/architecture/data-api.md`
  (`X-Alvo-Api-Key` also carries persisted app tokens; the tenant header still only confirms),
  `docs/architecture/management-api.md` (app tokens refused, D9), `docs/architecture/host.md` (`IApiKeyStore` routing and a
  host's own store, D4; the shared `Create` budget, D15), `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md`
  (§3.1 and §7.1 record D8 and D10's service half as S5 deviations; §7.2's prefix as D1), `CHANGELOG.md` `[Unreleased]`,
  `docs/PLAN.md` (S5 done, `← YOU ARE HERE` to S6)

**Interfaces:** consumes everything above; produces nothing.

- [ ] **Step 1: Write the facts** in `AppTokenEngineFacts`:
  `Personal_token_end_to_end` (a dispatcher creates *Acme* → mints a personal token for it scoped `jobs:read`,
  `jobs:write` → creates a job with it (201, `created_by` = the person) → the owner demotes them to `technician` → the
  token's next create is 403 and its read 200 → they are removed → the token's next request is 401 → they are invited
  back and accept → the old token is still 401) (**Review Focus 1**);
  `Service_token_end_to_end` (an owner mints a `dispatcher` service token → it creates and reads jobs as the tenant →
  a second owner is added → the minter is demoted → the token's next request is 401 → the second owner mints a new one,
  which works);
  `Level_one_personal_token` (`time-tracker`: a tenantless personal token reads and writes only its person's
  `time_entries`; another person's rows are invisible);
  `A_banned_person_s_tokens_die_and_stay_dead` (Management `DisableAccount` → both kinds 401 → `EnableAccount` → still 401);
  `A_token_never_acts_outside_its_tenant` (`X-Alvo-Tenant` naming another tenant the person is also in → 401; its own →
  200; none → its own);
  `A_service_token_s_tenant_role_never_reaches_global_data_or_management` (**Review Focus 4**: `dispatcher` service
  token → `POST /api/regions` 403; `PATCH /api/regions/{id}` through `jobs.exists(...)` 403; a Management read 403);
  `The_descriptor_moves_under_live_tokens` (**Review Focus 5**: drop `dispatcher` from `assignableRoles` → the service
  token's create 403 next request; `service: false` → 401; back on → 200; `selfService` removed → the personal token in a
  tenant 401, a tenantless one 200).
  `SecretsNeverLoggedTests`: `No_app_token_secret_appears_in_any_log` (Trace capture; mint both kinds → use each on the
  Data API, a scope refusal, a 401 with a wrong secret, Management's 403, list, revoke, a 429 on mint, a 422 whose body is
  near-valid; scan for each presented credential whole, its secret part and its hash); extend
  `No_error_body_echoes_a_presented_token` with the Management 403, the 401 and a 422 — none contains the secret.
- [ ] **Step 2: Run them** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*SqliteAppTokenEngineTests*' --filter-class '*SecretsNeverLoggedTests*'` and
  `dotnet test --project test/MMLib.Alvo.Api.Tests.Integration -- --filter-class '*PostgresAppTokenEngineTests*'`.
  Expected: PASS. **Prove they can fail:** log the presented key at Debug in `ApiKeyContextResolver` → the log fact goes
  red; resolve a service token's roles into `Roles` instead of `TenantRoles` → Review Focus 4 goes red; drop the removal
  sweep → the rejoin step goes red on both engines; restore all three and record the red runs in the commit body.
- [ ] **Step 3: Docs.** Sweep for the claim, not the file:
  `grep -rn -i "tokens\? \(are\|arrive\|is\) S5\|until S5\|(S5)\|personal tokens later\|personal and service tokens" docs/architecture src --include='*.md' --include='*.cs'`
  — every claim that app tokens do not exist yet, or that Management cannot tell them apart, is updated (`accounts.md`'s
  *Not in S2*/*Not in S3*, `MembershipResolution`'s remarks, `CallerResolution`'s table);
  `grep -rn "auth/v1/tokens" test/teapie test/teapie-field-service test/teapie-field-service-host` → no hits (D1: no pin
  moves). `scripts/check-brief-freshness` → OK.
- [ ] **Step 4: Commit** — `test(tokens): personal and service tokens end to end on both engines, and no secret in any log`

### Task 10: Slice gate — Release build, fixtures, ring1, ring2, security-core checklist

**Files:** none new (fixes only, if a gate finds something).

- [ ] **Step 1: Release build of the whole solution** — `dotnet build MMLib.Alvo.slnx -c Release`; Expected: 0 warnings,
  0 errors (CA rules fire in Release only; if the local SDK differs from CI's, repeat inside
  `mcr.microsoft.com/dotnet/sdk:10.0` — the *Rings are Debug, CI is Release* lesson).
- [ ] **Step 2: Prototype fixtures** — `scripts/gen-prototype-fixtures --check`; Expected: exit 0 (Task 1 regenerated them
  if the schema moved them; any later schema edit must have been followed by a regeneration).
- [ ] **Step 3: ring1** — `scripts/test-ring1`; Expected: `[ring1] OK`. The only moved baselines are those Tasks 1, 2, 7
  and 8 accepted (Abstractions PublicApi: D16's symbols; `negative-error-output.verified.txt`; the Accounts OpenAPI
  snapshot; capability fixtures if Task 1 moved them); anything else is a defect.
- [ ] **Step 4: ring2** — `scripts/test-ring2` (Docker up); Expected: exit 0, including `[lint-api] OK` over both documents.
- [ ] **Step 5: `alvo-security-core-review` checklist**, item by item, recorded for the PR report:
  secrets — 256-bit secret from `RandomNumberGenerator`, only its SHA-256 stored, shown once (201 only), never in a URL
  (OpenAPI fact), a log (Trace scan), a `ToString`, a list or a refusal; constant-time verification, the dummy hash for an
  unknown id; SQL — every `_api_keys` statement binds parameters, the table name only from `AlvoFrameworkTables`;
  prefixes — reserved at startup, so the Management refusal by prefix is sound; provenance — a service token's roles are
  tenant roles only (never `Roles`), ⊆ the minter's at mint and ⊆ the current `assignableRoles` per request, `admin`
  never assignable, ownership never carried; a personal token's roles live through the S3 honoured-membership predicate,
  its tenant fixed at mint, the header only confirming; death — removal, leaving, demotion and a ban revoke in the same
  transaction, mints serialised with each (Review Focus 2, red-proved on PostgreSQL), a rejoined person's old token stays
  dead; reach — a token never mints a token or reaches a self-service route (403) or Management (403, S1 D8 closed),
  scopes are entity × read/write with no wildcard and only existing entities; default-deny — no `auth.accessTokens`, a
  kind switched off, or `selfService` removed refuses the token on its next request; abuse — minting on the `Create`
  budget (10/min), at most 100 live tokens per account, CORS without credentials. Close with **mark this change
  `needs-deep-review`**.
- [ ] **Step 6: Hand back** to the controller (plan-guard, PR report, PR targeting `accounts/s4-invitations`). Flag for the
  post-merge mutation run: `accounts-store` grows by two files and the sweeps, `self-service` by the token route files,
  the core leg by `ApiKeyStores`/`AppTokenPrincipals`/`TokenRequest` — every leg's `mutants` stays an estimate until the
  first real run re-probes it, and every other shard's declared counts with it (the *re-probe all shards* lesson).

## Self-review against the spec

| Spec requirement | Task |
|---|---|
| §3.1 `auth.accessTokens` `personal`, `service`, `maxLifetimeDays` 1–365 default 90; `service` without `selfService` refused (D8: without `createTenants`) | 1 |
| §7.1 personal: acts as the person, in one tenant or none, roles live, dies with membership/disable/expiry/revocation | 4, 5, 9 |
| §7.1 service: acts as the tenant, own `UserId` per token, roles fixed ⊆ minter's at mint, ≥ 1, dies when the minter is removed or demoted | 3, 4, 5, 9 |
| §7.1 persisted `IApiKeyStore` (EF, both engines), dev keys as fallback, `alvo_pat_`/`alvo_svc_` prefixes, `ApiKeyRecord` widened with kind and minter | 2, 3, 5 |
| §7.1 scopes entity × read/write, at least one; §7.2 only existing entities, refused with the list | 7 |
| §7.2 `POST` person/owner, never a token; `GET` my personal + my tenants' service tokens, never the secret; `DELETE` minter or any owner | 7, 8 |
| §7.2 `expiresAt` required ≤ `maxLifetimeDays`; the secret answered once, stored as SHA-256 | 2, 7 |
| §7.3 minting 10/min | 7 |
| §5.7 Management refuses a personal or service token (S1 D8) | 6, 9 |
| §6.2 / §14 AC4 a token's tenant role never reaches a global entity or Management | 5, 9 |
| §6.4 removing a membership refuses every personal token minted for it; §14 AC3 changes count on the next request | 4, 5, 9 |
| §8 / §14 AC6 no token secret in any log | 2, 7, 9 |
| §14 AC7 API keys and the operator cookie behave exactly as before | 5, 6 |
