# S2 — Sign-in (level 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A person signs in with Google (an ID token exchanged per RFC 8693), receives an ES256 access token and a
rotating refresh token, and uses the Data API as `@user.id` with no tenant — level 1 of spec §3 — on SQLite and
PostgreSQL; an operator can disable the account.

**Architecture:** A new package, `MMLib.Alvo.Accounts`, owns sign-in: the token endpoint, sessions, keys, JWT, the
generic OIDC verifier with a Google preset, JWKS + RFC 8414 metadata, `/user`, `/logout`, and the person resolver it
registers under S1's `"alvo-person"` key. Its tables and their one-transaction writes live in the EF adapter behind an
Abstractions-internal port, `IAccountStore`, beside every other framework table. The core gains what spec §11.1 puts
there: the `auth.signUp` descriptor key, membership resolution (S2: "no tenant"), CORS and rate limits, the Bearer
security scheme in the OpenAPI document, and the `ManageAccounts` management route.

**Tech Stack:** .NET 10, minimal APIs, EF Core relational connections (SQLite + PostgreSQL), `Microsoft.IdentityModel.JsonWebTokens` +
`Microsoft.IdentityModel.Protocols.OpenIdConnect` (Accounts only), `System.Threading.RateLimiting` and
`Microsoft.AspNetCore.Cors` (shared framework), xUnit v3 on MTP, Shouldly, NSubstitute, Verify, `Microsoft.AspNetCore.TestHost`,
`Microsoft.Extensions.TimeProvider.Testing`, Testcontainers (PostgreSQL leg).

**Spec:** `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md` — §3.1 (`auth.signUp`
only), §4 (D2–D7), §5 whole, §7.3, §8, §11 (S2 row), §11.1, §11.2, §14. Master plan:
`docs/superpowers/plans/2026-11-13-accounts-00-master.md` (fixed names: `CallerResolution`, `"alvo-person"`,
`AlvoAccountsOptions.Prefix` default `/auth/v1`, the five `_auth_*` suffixes, membership resolution's signature; Review
Focus 2 and 5 are this slice's). S1 plan: `docs/superpowers/plans/2026-11-13-accounts-s1-role-provenance.md` (D6–D8).
Prior art read for this plan: RFC 6749 §3.2/§5.1/§5.2/§6, RFC 8693 §2, RFC 9068, RFC 7638, RFC 8414 §2/§3, RFC 8725,
RFC 9700 §4.14, RFC 6750, OIDC Core §3.1.3.7, Google's *Verify the Google ID token on your server side*, the CORS
protocol (Fetch standard).

## Decisions (read before Task 1)

- **D1 — The package, and the one new `InternalsVisibleTo` direction.** `MMLib.Alvo.Accounts` references Abstractions and
  the core (spec §11.1), plus the two IdentityModel packages. The core grants it `InternalsVisibleTo` — the first product
  assembly to see the core's internals — because what it consumes is core by the spec (`CallerResolution`,
  `ProblemResultFactory`, the rate-limit and CORS filters, membership resolution, `SignInPolicy`); publishing those would
  make five seams public for one in-family consumer (*public is the contract*). An architecture fact in the Accounts
  suite holds that it references none of the guarded internals the Abstractions grants already police
  (`test/_shared/ai/InternalMemberReferences.cs`, the Ai/Admin precedent). The Abstractions grant (for `IAccountStore`,
  `IAlvoAccountAdministration`, `AlvoFrameworkTables`) follows the Identity precedent.
- **D2 — Stores in the EF adapter, through `IAccountStore` (Abstractions, `internal`).** The ledger precedent
  (`ICommandLedger`): the adapter implements it, contract facts in `test/_shared/ef/` run on both engines. Tables are
  created by `SystemSchemaInitializer` at boot and by the store through `OnceDdl` (a composition that never booted). No
  foreign keys — no framework table carries one; correctness comes from insertion order inside one transaction. Every
  write the spec calls "one store call, one transaction" is one port member. Timestamps and ids are passed **in** by the
  caller, so the store is deterministic under `FakeTimeProvider`.
- **D3 — CORS and rate limits are endpoint filters, not middleware or `RequireCors`/`RequireRateLimiting` metadata.**
  Metadata needs `UseCors()`/`UseRateLimiter()` in the host's pipeline after routing — `EndpointMiddleware` even throws on
  CORS metadata without it — and an embedded host must get both without a pipeline step (spec §7.3). So: rate limits are
  a `PartitionedRateLimiter<HttpContext>` charged by a filter (the `AlvoAdminCredentialLimit` precedent: the endpoint
  charges itself); CORS is evaluated by the framework's own `ICorsService` against an Alvo-held `CorsPolicy` object
  (not a registered policy name, so no host policy can collide) from the **first** filter on each route, and preflights
  are answered by one `OPTIONS` catch-all mapped per Alvo route group **only when origins are configured**. Covered in
  S2: the Data API and the auth routes. Not covered: host-mapped commands (`MapAlvoCommand` — the host picks the path, so
  there is no group to own a preflight) — recorded in `data-api.md`.
- **D4 — Token-endpoint errors (spec D4).** `application/problem+json` with RFC 6749 `error` and `error_description`
  members. Four new public slugs in `AlvoProblemTypes`: `token-refused` (400; every RFC 6749 §5.2 / RFC 8693 §2.2.2 code:
  `invalid_request`, `invalid_grant`, `unsupported_grant_type`, `invalid_target`), `sign-up-refused` (403, `error:
  access_denied`, extension member `policy`: `invitationOnly` | `allowedEmailDomains`), `sign-in-refused` (403,
  `access_denied`, one fixed detail, no reason), `rate-limited` (429 + `Retry-After`). **Deviation:** `access_denied` is
  an authorization-endpoint code (RFC 6749 §4.1.2.1), not a §5.2 one; the spec chose it and a client treats an unknown
  code generically. **Interpretation of §5.2 step 6** ("same answer as an unknown one"): a disabled account answers
  `sign-in-refused`, which names no reason; on refresh and Bearer a disabled account is byte-identical to an unknown
  token. Only someone holding a valid ID token for that identity ever sees the exchange answer.
- **D5 — One clock for every lifetime check.** `JsonWebTokenHandler` keeps signature, `kid`, `iss`, `aud`, `alg` and
  `typ`; `ValidateLifetime = false`, and Alvo checks `exp`/`nbf` (ID token ± 2 min, access token ± 30 s) and `iat`
  freshness (≤ 5 min, not in the future beyond the skew) against the injected `TimeProvider` in `TokenTimes`. `iat`
  freshness is not a handler check anyway, and one clock is what makes the boundaries pinnable.
- **D6 — Google preset.** Issuers `https://accounts.google.com` **and** `accounts.google.com` (Google documents both);
  authority `https://accounts.google.com` (overridable); algorithms `RS256` only — `none` and every `HS*` refused for
  every provider (RFC 8725 §3.1, key confusion); email trusted iff `email_verified == true` and (address ends
  `@gmail.com`, case-insensitive, or the token carries `hd`). A generic OIDC provider trusts email iff its
  configuration sets `TrustEmail: true` **and** `email_verified == true`. The generic verifier's algorithms are the
  discovery document's `id_token_signing_alg_values_supported` minus `none` and `HS*`.
- **D7 — Metadata at two URLs.** Spec §5.4's `{prefix}/.well-known/oauth-authorization-server`, and RFC 8414 §3.1's
  path-inserted `/.well-known/oauth-authorization-server{prefix}` (where a generic RFC 8414 client looks). Same handler,
  same document; the second is `ExcludeFromDescription`. Recorded as a deviation-avoidance, not a second way to do a
  thing: one document, two discovery spellings.
- **D8 — Display name.** Set from the identity at sign-up; afterwards only `PATCH /user` writes it. Later sign-ins
  refresh `email`, `email_trusted`, `avatar_url`, `last_sign_in_at` — **not** the name (deviation from §5.2 step 5's
  "refresh profile fields": a refresh would silently undo the person's own edit).
- **D9 — Accounts are on per host, by configuration.** `AddAlvoAccounts` is the switch for an embedded host; the
  standalone host calls it only when `Alvo:Accounts:Providers` has at least one provider section. So an existing
  deployment without accounts keeps starting without a key file, its OpenAPI document gains no path, operation or
  security scheme, and **no TeaPie path pin moves in S2** (both stacks run without accounts; S6 turns them on in
  field-service). The Bearer security scheme is published only when a person resolver is registered. What the document
  of a host without accounts does gain is the four problem-type values D4 adds, in the `problemDetails.type` enum:
  that enum is the whole `AlvoProblemTypes` catalogue, and Management-only slugs were already in it. The change is
  additive, and such a host never emits those types.
- **D10 — The descriptor is the switch for sign-in.** The exchange admits only a provider the applied descriptor lists
  **and** the host configured. A live session whose `idp` the applied descriptor no longer lists is refused at its next
  request (401) and refresh (`invalid_grant`) — default-deny.
- **D11 — `google` honoured.** The `auth.providers` unhonoured row (core `UnhonouredSubsystems.WithinBlocks` and the
  dashboard's mirror `DeclaredSlots`) stops naming `google`; `microsoft`, `github`, `apple`, `oidc` stay warned. The
  host-dependent half is a separate, **boot-only** warning in the core: the applied descriptor lists `google` and no
  person resolver is registered (`CallerResolvers.HasPerson == false`). It is part of `DescriptorBootPlan.Plan`, the
  same single emission point as the unhonoured-block warning, so a runtime apply that adds `google` on a host without
  Accounts does not repeat it until the next boot. Keeps the capability report and the dashboard descriptor-level.
- **D12 — No key rotation in S2.** One row per purpose (`signing`, `refresh`), generation 1, `retires_at` null; JWKS
  publishes every unretired signing key. The table's `(purpose, generation)` uniqueness is what makes first boot on two
  replicas converge (Review Focus 5) and what a later rotation increments.
- **D13 — Membership resolution (master plan's fixed signature) is consulted before the store read.** S2's
  implementation refuses a requested tenant with no I/O, so the one store read is account + session (spec §14 AC2 holds).
  **S3 note:** reading memberships through a separate service would be a second round trip; S3 must fold the membership
  into the session read and hand it to membership resolution.
- **D14 — `invitationOnly` in S2** refuses every **new** identity (`sign-up-refused`, `policy: invitationOnly`);
  existing accounts sign in. `invitation_code` is not read until S4.
- **D15 — The person principal.** `User` = account id, `Roles` = `{authenticated}`, `TenantRoles` empty, `Tenant` null,
  `ActorKind = User`, scopes `*:read`/`*:write` (S1 D8: a session is not scope-limited; rules decide),
  `KeyId = "session:" + sessionId.ToString("N")` (not a secret; scopes idempotency per session).
- **D16 — Not RFC 7009.** `POST {prefix}/logout` ends the bearer's session; token revocation by value is not offered.
- **D17 — Not in S2:** §3.1's global-write warning (spec §11 gives it to S3, and at level 1 every entity is global, so it
  would fire on every level-1 app); every `selfService` refusal (S3); the `invitations`/`create` rate limits (S3/S4); key
  rotation (D12). Startup refusal of `Alvo:Auth:HeaderName = Authorization` lives in Accounts' option validation
  (S1 carry-over); the `AnyKey` hole is closed by S1's fix wave.

## Global Constraints

- English in everything committed. CRLF + BOM in every new `.cs`/`.csproj` (copy a neighbour's bytes; re-normalise
  after any Bash/Python edit). Short single-purpose methods (~25 lines); no inline comments by default
  (`alvo-dotnet-conventions`).
- Public API is the contract. Allowed public growth, each line justified in its commit body:
  Abstractions — `Auth.SignUp`, `AuthSignUp`, `SignUpMode` (the descriptor is public, every block is);
  core — `AlvoApiOptions.Cors`/`.RateLimits`, `AlvoCorsOptions`, `AlvoRateLimitOptions` (bound from `Alvo:Api`),
  four `AlvoProblemTypes` consts (D4); Accounts (new baseline) — `AddAlvoAccounts`, `MapAlvoAccounts`,
  `AlvoAccountsOptions`, `AlvoOidcProviderOptions`, `AlvoAccounts.ConfigurationSection`. Everything else `internal`.
- Default-deny: no descriptor provider, no configured provider, no key file, unknown `kid`, malformed form — each refuses.
- No secret in a log, a `ToString`, a problem `detail`, or a URL: ID tokens, access tokens, refresh tokens, HMAC and
  private key material. Records carrying one override `PrintMembers` (the `ApiKeyRecord` precedent).
  `IdentityModelEventSource.ShowPII` is never set.
- Tables only through `AlvoFrameworkTables` suffixes: `_auth_accounts`, `_auth_identities`, `_auth_sessions`,
  `_auth_refresh_tokens`, `_auth_keys`. One DDL text per table for both engines (TEXT ids/timestamps in the fixed UTC
  format `TimerTable` writes; INTEGER 0/1 booleans).
- Defaults copied from spec §5 and §11.2: access token 15 min (1–60), refresh idle 30 days, session 180 days, grace
  10 s (0 allowed), ID-token skew 2 min, `iat` ≤ 5 min, access-token skew 30 s, JWKS refresh on unknown `kid` at most
  once per 5 min, token rate limit 30/min per client IP.
- **Test discipline:** inner loop = the affected project filtered to the class,
  `dotnet test --project <proj> -- --filter-class '*<Class>*'`; ring0 only through the pre-commit hook; ring1 and ring2
  once, in Task 14. Docker for the PostgreSQL legs. Branch `accounts/s2-sign-in` from `accounts/s1-role-provenance`
  (`git switch -c accounts/s2-sign-in`) before Task 1; never push to `main`; the controller opens the PR.
- Do not touch `examples/field-service` or its TeaPie stacks (S6).

## Review Focus

1. **Two tabs refreshing at once** — both get the same child refresh token, the session survives, and the parent
   presented after the window kills every token of the session. Pinned by Task 3 (store, both engines, concurrent) and
   Task 8 (HTTP, concurrent).
2. **Two replicas signing first on a fresh database** — exactly one signing key and one HMAC key exist, and a token
   minted by either validates on the other. Pinned by Task 3 (concurrent insert-if-absent, both engines) and Task 5
   (two `AccountKeys` over one store).
3. **The descriptor drops `google` while sessions are live** — the next Bearer request is 401 and the next refresh
   `invalid_grant`; the exchange refuses. Pinned by Task 9 (D10).
4. **An existing host that never configured accounts** — starts without a key file, maps no `/auth` route, publishes no
   Bearer scheme, ignores `Authorization` exactly as S1 left it; and a host whose `Alvo:Auth:HeaderName` is
   `Authorization` is refused at startup once Accounts is added. Pinned by Tasks 4 and 12.
5. **Clock boundaries** — an ID token whose `iat` is exactly 5 min old is accepted and 5 min + 1 s refused; `exp` 2 min
   in the past accepted, 2 min + 1 s refused; an access token 30 s past `exp` accepted, 31 s refused; a refresh 10 s
   after rotation gets the child, 11 s revokes. Pinned by Tasks 6, 5 and 8.

---

### Task 1: `auth.signUp` in the descriptor, the sign-in policy, and `google` honoured

**Files:**
- Modify: `schema/project.schema.json` (`auth.signUp`)
- Modify: `src/MMLib.Alvo.Abstractions/Descriptor/DescriptorBlocks.cs` (`Auth.SignUp`), `DescriptorEnums.cs` (`SignUpMode`)
- Create: `src/MMLib.Alvo/Descriptor/Internal/SignUpCheck.cs`; wire it where the other cross-key checks run
  (`SequenceFieldCheck`'s caller)
- Create: `src/MMLib.Alvo/Auth/Internal/SignInPolicy.cs`; Modify: `src/MMLib.Alvo/Rules/PolicyCatalog.cs` (+ builder)
- Modify: `src/MMLib.Alvo/Descriptor/Internal/UnhonouredSubsystems.cs`, `src/MMLib.Alvo/Migrations/Internal/DescriptorBootPlan.cs`,
  `src/MMLib.Alvo.Admin/Components/Home/DeclaredSlots.cs`
- Modify: `examples/complex-crm/crm.alvo.json` (+ `"signUp": { "mode": "invitationOnly" }`)
- Create: `examples/_negative/google-without-sign-up.json`; Modify: `examples/_negative/expectations.json`
- Test: `test/MMLib.Alvo.Tests/Descriptor/SignUpCheckTests.cs`, `test/MMLib.Alvo.Tests/Auth/SignInPolicyTests.cs`,
  `test/MMLib.Alvo.Tests/Descriptor/UnhonouredKeysTests.cs`, `test/MMLib.Alvo.Admin.Tests/Home/DeclaredLimitsTests.cs`,
  the schema suite per the `alvo-schema-testing` skill

**Interfaces:**
- Schema: `auth.signUp` = `{ "mode": "open" | "invitationOnly" (required), "allowedEmailDomains": [hostname, …] }`,
  `additionalProperties: false`; domains `uniqueItems`, `minItems: 1`, lowercase ASCII hostname pattern
  `^(?=.{1,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$`. Descriptions copy §3.1's table.
- Abstractions (public): `Auth.SignUp { get; init; }` of `public sealed record AuthSignUp { SignUpMode Mode; IReadOnlyList<string>? AllowedEmailDomains }`;
  `public enum SignUpMode { Open, InvitationOnly }` serialised `open`/`invitationOnly` like the other descriptor enums.
- `SignUpCheck`: `auth.providers` contains `google` and `auth.signUp` is absent → error at `/auth/signUp`, message
  `"auth.providers lists google, which lets people sign up, but auth.signUp says nothing about who may."`, fix
  `"Add \"signUp\": { \"mode\": \"open\" } to let anyone with a Google account create one, or \"signUp\": { \"mode\": \"invitationOnly\" } to admit only people an owner invited."`.
- `internal sealed record SignInPolicy(string ProjectName, IReadOnlySet<AuthProvider> Providers, SignUpMode? Mode, IReadOnlyList<string> AllowedEmailDomains)`
  with `static SignInPolicy None` and `bool Lists(AuthProvider provider)`. `PolicyCatalog` gains
  `internal SignInPolicy SignIn { get; init; }` built from the descriptor (domains lower-cased). Accounts reads it as
  `IPolicyCatalogProvider.Current?.SignIn ?? SignInPolicy.None`.
- `auth.providers` row predicate: any provider not `Local` and not `Google`; consequence text names
  microsoft, github, apple and oidc. `DeclaredSlots.Declares(…, "auth.providers")` mirrors it.
- `DescriptorBootPlan`: after `WarnAboutUnhonouredBlocks`, when the descriptor lists `google` and
  `CallerResolvers.HasPerson` is false, one warning: `"auth.providers lists google, but this host registered no end-user sign-in (MMLib.Alvo.Accounts: AddAlvoAccounts and MapAlvoAccounts), so nobody can sign in with Google here."`

- [ ] **Step 1: Write the failing facts.**
  `SignUpCheckTests`: `Google_without_sign_up_is_refused_with_both_choices` (pointer `/auth/signUp`, fix contains
  `"open"` and `"invitationOnly"`); `Google_with_either_mode_is_accepted` (theory); `Local_alone_needs_no_sign_up`;
  `Sign_up_without_an_end_user_provider_is_accepted`.
  `SignInPolicyTests`: `The_policy_carries_the_descriptor_name_providers_mode_and_lowercased_domains`;
  `A_descriptor_without_auth_has_no_providers_and_no_mode`.
  `UnhonouredKeysTests`: every fact that used `google` as the unhonoured example uses `microsoft`;
  `Google_beside_local_names_no_provider_slot`; `A_descriptor_listing_google_on_a_host_without_a_person_resolver_warns_once`.
  `DeclaredLimitsTests`: the provider theory gains `["local", "google"] → false`, `["google", "apple"] → true`.
  Schema: positive example with both keys; negative `google-without-sign-up.json` (expectation pointer `#/auth`,
  `messageContains: "signUp"`); a domain with an upper-case letter and a `*` refused by the schema.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*SignUpCheckTests*' --filter-class '*SignInPolicyTests*' --filter-class '*UnhonouredKeysTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement.** Then sweep: `grep -rn '"google"' test examples src --include='*.cs' --include='*.json'`
  — each hit either keeps working or is updated in this task.
- [ ] **Step 4: Run** the Step 2 command, then `dotnet test --project test/MMLib.Alvo.Schema.Tests`,
  `dotnet test --project test/MMLib.Alvo.Admin.Tests -- --filter-class '*DeclaredLimitsTests*'`,
  `dotnet test --project test/MMLib.Alvo.Abstractions.Tests -- --filter-class '*PublicApiApprovalTests*'`.
  Expected: PASS after accepting exactly three baseline moves — the Abstractions PublicApi (the three types/member),
  `canonical-complex-crm.verified.txt` (the `signUp` line), `negative-error-output.verified.txt` (the new case).
  Dispatch `alvo-snapshot-judge` when the Stop hook asks.
- [ ] **Step 5: Commit** — `feat(descriptor): auth.signUp decides who may create an account; google is honoured`

### Task 2: The account store I — port, tables, and the sign-in transaction

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Accounts/IAccountStore.cs` (port + records, namespace `MMLib.Alvo.Accounts`)
- Modify: `src/MMLib.Alvo.Abstractions/AlvoFrameworkTables.cs` (five suffixes; `NamesFor`), `Properties/AssemblyInfo.cs`
  (grants `MMLib.Alvo.Accounts`, `MMLib.Alvo.Accounts.Tests`, each with a reason paragraph)
- Create: `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AuthTables.cs` (names, DDL, statements),
  `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreAccountStore.cs`
- Modify: `SystemSchemaInitializer.cs` (creates the five tables and their indexes), `AlvoEfCoreProvider.cs`
  (`TryAddSingleton<IAccountStore>`, constructed with `Cipher(provider)` and `TimeProvider`)
- Test: `test/_shared/ef/AccountStoreFacts.cs` (abstract), `test/MMLib.Alvo.Data.Sqlite.Tests/SqliteAccountStoreTests.cs`,
  `test/MMLib.Alvo.Data.PostgreSql.Tests.Integration/PostgreSqlAccountStoreTests.cs` (link the facts in both csproj —
  the `LedgerFacts` precedent); `AlvoFrameworkTables` facts that already enumerate `NamesFor`

**Interfaces (all `internal`):**
```csharp
interface IAccountStore
{
    Task<SignInOutcome> SignInAsync(SignInRequest request, CancellationToken ct);
    Task<bool> IsSessionLiveAsync(UserId account, Guid session, DateTimeOffset now, CancellationToken ct);
    Task<AccountView?> GetAccountAsync(UserId account, CancellationToken ct);
    Task<bool> SetDisplayNameAsync(UserId account, string name, CancellationToken ct);
    // Task 3 adds: RefreshAsync, RevokeSessionAsync, SetDisabledAsync, EnsureKeyAsync, ReadKeysAsync
}
sealed record VerifiedIdentity(string Provider, string Subject, string? Email, bool EmailTrusted, string? Name, string? Picture);
sealed record SignInRequest(VerifiedIdentity Identity, bool MayCreate, string SubjectTokenId, Guid SessionId,
    Guid RefreshTokenId, DateTimeOffset Now, DateTimeOffset SessionExpiresAt);
enum SignInStatus { SignedIn, Created, SignUpRefused, Disabled, SubjectTokenReplayed }
readonly record struct SignInOutcome(SignInStatus Status, UserId? Account);
sealed record AccountView(UserId Id, string? Email, bool EmailTrusted, string? DisplayName, string? AvatarUrl,
    IReadOnlyList<string> Providers, DateTimeOffset CreatedAt);
```
- DDL (prefix `p`): `p_auth_accounts(id PK, email, email_trusted, display_name, avatar_url, created_at, last_sign_in_at, disabled_at)`;
  `p_auth_identities(provider, subject, account_id NOT NULL, email, email_trusted, created_at, PRIMARY KEY(provider, subject))` + index `account_id`;
  `p_auth_sessions(id PK, account_id, provider, subject_token_id NOT NULL UNIQUE, created_at, last_refreshed_at, expires_at, revoked_at, revoked_reason)` + index `account_id`;
  `p_auth_refresh_tokens(id PK, session_id NOT NULL, parent_id UNIQUE NULL, created_at, rotated_at)` + index `session_id`
  (both engines allow many NULLs under UNIQUE — that is what makes "one child per parent" a constraint);
  `p_auth_keys(kid PK, purpose NOT NULL, generation INTEGER NOT NULL, material NOT NULL, created_at, retires_at, UNIQUE(purpose, generation))`.
- `SignInAsync`, one transaction, in this order: read the identity; absent and `!MayCreate` → `SignUpRefused`;
  absent → `INSERT identity … ON CONFLICT (provider, subject) DO NOTHING` and, only if it inserted, insert the account
  (display name from the identity), else re-read (two concurrent first sign-ins converge on one account); account
  disabled → `Disabled`; `INSERT session … ON CONFLICT (subject_token_id) DO NOTHING`, nothing inserted →
  `SubjectTokenReplayed`; insert the first refresh-token row (no parent); refresh `email`, `email_trusted`,
  `avatar_url`, `last_sign_in_at` (never `display_name`, D8); commit. Every refusal rolls back. Transactions open the
  way `EfCoreTimerStore` opens its writes (the write gate on SQLite).
- `IsSessionLiveAsync`: one statement joining session and account — true iff the session belongs to the account, is
  not revoked, `expires_at > now`, and the account is not disabled.

- [ ] **Step 1: Write the failing facts** in `AccountStoreFacts` (store resolved from the fixture's container, as
  `LedgerFacts` does):
  `A_first_sign_in_creates_one_account_with_its_identity_and_a_live_session` (status `Created`; `GetAccountAsync`
  shows provider `google`, display name from the identity; `IsSessionLiveAsync` true);
  `A_second_sign_in_finds_the_account_by_provider_and_subject_never_by_email` (same subject, other email → same account,
  email refreshed; another subject with the first email → a second account);
  `A_later_sign_in_never_overwrites_the_display_name` (D8);
  `A_refused_sign_up_writes_nothing` (`MayCreate: false` → `SignUpRefused`, no rows in any of the three tables);
  `A_replayed_subject_token_starts_no_second_session_and_creates_no_account` (second call, same `SubjectTokenId`,
  new identity → `SubjectTokenReplayed`, account count unchanged);
  `Two_concurrent_first_sign_ins_of_one_identity_create_one_account` (two `SignInAsync` with distinct token ids via
  `Task.WhenAll`, repeated 20 times → one account, two live sessions on it);
  `A_session_is_dead_after_its_expiry_and_for_another_account` (`now = expires_at` → false; other `UserId` → false);
  `The_tables_are_reserved_framework_names` (`AlvoFrameworkTables.NamesFor("alvo")` contains the five).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Data.Sqlite.Tests -- --filter-class '*SqliteAccountStoreTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the Step 2 command, then
  `dotnet test --project test/MMLib.Alvo.Data.PostgreSql.Tests.Integration -- --filter-class '*PostgreSqlAccountStoreTests*'`
  and `dotnet test --project test/MMLib.Alvo.Data.EntityFrameworkCore.Tests -- --filter-class '*Introspect*'` (the five
  tables are excluded from introspection and refused as entity names). Expected: PASS.
- [ ] **Step 5: Commit** — `feat(data-ef): the account store signs a person in in one transaction`

### Task 3: The account store II — refresh rotation, sign-out, disabling, signing keys

**Files:**
- Modify: `src/MMLib.Alvo.Abstractions/Accounts/IAccountStore.cs`, `src/MMLib.Alvo.Data.EntityFrameworkCore/Internal/AuthTables.cs`,
  `src/MMLib.Alvo.Data.EntityFrameworkCore/EfCoreAccountStore.cs`
- Test: `test/_shared/ef/AccountStoreFacts.cs`

**Interfaces (added to the port):**
```csharp
Task<RefreshOutcome> RefreshAsync(RefreshRequest request, CancellationToken ct);
Task<bool> RevokeSessionAsync(Guid session, string reason, DateTimeOffset now, CancellationToken ct); // idempotent; true if it existed
Task<bool> SetDisabledAsync(UserId account, bool disabled, DateTimeOffset now, CancellationToken ct); // false = no such account
Task<StoredKey> EnsureKeyAsync(KeyCandidate candidate, CancellationToken ct);
Task<IReadOnlyList<StoredKey>> ReadKeysAsync(string purpose, DateTimeOffset now, CancellationToken ct);

sealed record RefreshRequest(Guid TokenId, Guid ChildId, DateTimeOffset Now, TimeSpan Grace, TimeSpan Idle);
enum RefreshStatus { Rotated, Replayed, Reused, Refused }
readonly record struct RefreshOutcome(RefreshStatus Status, Guid? ChildId, UserId? Account, Guid? Session, string? Provider);
sealed record KeyCandidate(string Purpose, int Generation, string Kid, string Material, DateTimeOffset Now); // PrintMembers redacts Material
sealed record StoredKey(string Purpose, int Generation, string Kid, string Material, DateTimeOffset CreatedAt, DateTimeOffset? RetiresAt); // same
static class AccountKeyPurposes { const string Signing = "signing"; const string Refresh = "refresh"; }
static class SessionRevocations { const string Logout = "logout"; const string Reuse = "reuse"; const string Disabled = "disabled"; }
```
- `RefreshAsync`, one transaction (spec §5.5, D6): read token ⨝ session ⨝ account; none, session revoked or expired, or
  account disabled → `Refused`. Unrotated: `created_at + Idle <= Now` → `Refused`; else
  `UPDATE … SET rotated_at = @now WHERE id = @id AND rotated_at IS NULL` — one row → insert the child
  (`parent_id = @id`), stamp `last_refreshed_at` → `Rotated(ChildId)`; zero rows (a concurrent winner) → fall through.
  Rotated: `Now - rotated_at <= Grace` → `Replayed(<the existing child's id>)`; else revoke the session with
  `reuse` → `Reused`. `Rotated`/`Replayed` carry account, session and the session's provider. On PostgreSQL the losing
  `UPDATE` waits on the winner's row lock and its re-read sees the committed child (READ COMMITTED); on SQLite the
  write gate serialises; the `parent_id` UNIQUE is the backstop.
- `SetDisabledAsync(true)`: stamp `disabled_at` (if null) and revoke every live session with `disabled`, one
  transaction; `false` clears `disabled_at` and leaves sessions revoked.
- Keys: `material` is stored as `SecretCipher.Protect(candidate.Material)` and returned unprotected; the store refuses
  key operations with `InvalidOperationException` naming `Alvo:Secrets:EncryptionKeyFile` when it has no cipher.
  `EnsureKeyAsync` = `INSERT … ON CONFLICT (purpose, generation) DO NOTHING` then read that `(purpose, generation)`
  row — whoever inserted first wins, everyone reads the winner. `ReadKeysAsync`: unretired (`retires_at` null or in the
  future) rows of the purpose.

- [ ] **Step 1: Write the failing facts** (seed a session through `SignInAsync`; `Grace = 10 s`, `Idle = 30 days`):
  `A_fresh_refresh_token_rotates_into_one_child`; `A_rotated_token_inside_the_grace_window_answers_the_same_child`
  (twice → same `ChildId`, one child row); `A_rotated_token_after_the_window_revokes_the_session`
  (`Now = rotated + 11 s` → `Reused`; then the child → `Refused`; `IsSessionLiveAsync` false);
  `The_window_boundary_is_inclusive` (`rotated + 10 s` → `Replayed`); `A_zero_grace_window_revokes_on_any_reuse`;
  `Two_concurrent_refreshes_of_one_token_get_the_same_child` (**Review Focus 1**: two `RefreshAsync` with different
  `ChildId`s via `Task.WhenAll`, 20 iterations → both outcomes carry one `ChildId`, statuses `{Rotated, Replayed}`,
  one child row, session live); `An_idle_token_is_refused` (`created + 30 days` → `Refused`);
  `A_disabled_account_s_tokens_are_refused_and_its_sessions_revoked`;
  `Enabling_an_account_leaves_its_old_sessions_revoked`; `Signing_out_twice_is_the_same_as_once`;
  `Two_concurrent_first_keys_converge_on_one` (**Review Focus 5**: two `EnsureKeyAsync` with different `Kid`/`Material`
  for (`signing`, 1) via `Task.WhenAll`, 20 iterations on fresh purposes → both return the same `Kid` and `Material`,
  one row); `A_key_s_material_is_stored_encrypted` (raw `SELECT material` ≠ plaintext; `ReadKeysAsync` returns it);
  `Key_records_never_print_their_material` (`ToString()` of both records lacks the material).
- [ ] **Step 2: Run to see them fail** — the Task 2 SQLite command; Expected: FAIL (compile).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the SQLite and PostgreSQL commands of Task 2; Expected: PASS.
- [ ] **Step 5: Commit** — `feat(data-ef): refresh tokens rotate without forking, and the first signing key wins`

### Task 4: The `MMLib.Alvo.Accounts` package, its options, and its startup refusals

**Files:**
- Create: `src/MMLib.Alvo.Accounts/MMLib.Alvo.Accounts.csproj`, `AlvoAccounts.cs`, `AlvoAccountsOptions.cs`,
  `AlvoOidcProviderOptions.cs`, `AlvoAccountsServiceCollectionExtensions.cs`, `AlvoAccountsEndpointRouteBuilderExtensions.cs`,
  `Internal/AlvoAccountsOptionsValidation.cs`, `Internal/AccountsStartupWarnings.cs`, `Properties/AssemblyInfo.cs`
- Create: `test/MMLib.Alvo.Accounts.Tests/MMLib.Alvo.Accounts.Tests.csproj`, `AccountsOptionsTests.cs`,
  `AccountsArchitectureTests.cs`, `PublicApi.MMLib.Alvo.Accounts.verified.txt`
- Modify: `Directory.Packages.props`, `MMLib.Alvo.slnx`, `src/MMLib.Alvo/Properties/AssemblyInfo.cs` (D1 grant +
  reason), `docs/architecture/package-boundary.md` (the package, rule (a) + (b), its two dependencies)

**Interfaces:**
- CPM: `Microsoft.IdentityModel.JsonWebTokens` and `Microsoft.IdentityModel.Protocols.OpenIdConnect`, **one** version,
  the current stable 8.x; verify `MIT` on each nuspec and say so in the comment (house style). Nothing in the graph
  reaches IdentityModel today (no `project.assets.json` names it), so the transitive pin moves no other project.
- csproj: `Description` "End-user sign-in for Alvo: the OAuth token endpoint, sessions, signing keys and the OIDC
  ID-token verifier."; references Abstractions + core; the two packages; `FrameworkReference Microsoft.AspNetCore.App`.
  Test project per `alvo-new-package` (references Accounts, core, `Data.Sqlite`; `TestHost`, `TimeProvider.Testing`,
  `Verify.XunitV3`, `NSubstitute`; links `../_shared/api/*.cs` exactly as `MMLib.Alvo.Api.Tests` does, `../_shared/accounts/*.cs`
  and `../_shared/ai/InternalMemberReferences.cs`).
- Public: `public static class AlvoAccounts { public const string ConfigurationSection = "Alvo:Accounts"; }`;
  `AlvoAccountsOptions { string Prefix = "/auth/v1"; string? Issuer; string? Audience; int AccessTokenMinutes = 15; int RefreshIdleDays = 30; int SessionLifetimeDays = 180; int RefreshReuseGraceSeconds = 10; Dictionary<string, AlvoOidcProviderOptions> Providers (OrdinalIgnoreCase) }`;
  `AlvoOidcProviderOptions { List<string> ClientIds; string? Authority; bool AllowHttpMetadata }` (**amended by the
  final review, M6:** `bool TrustEmail` was public here but nothing could configure a generic provider in S2, so it bound
  and did nothing; it is a parameter of the internal `OidcProvider.Generic` until the slice that ships a configurable
  generic provider makes it public);
  `IServiceCollection AddAlvoAccounts(this IServiceCollection services, Action<AlvoAccountsOptions>? configure = null)`;
  `IEndpointRouteBuilder MapAlvoAccounts(this IEndpointRouteBuilder endpoints)` (maps nothing until Task 7).
- Validation (`ValidateOnStart`, every failure names its key, all collected): `Issuer` required outside Development
  (`IHostEnvironment`), absolute `https` URI; `Prefix` starts with `/`; `AccessTokenMinutes` 1–60; `RefreshIdleDays`
  ≥ 1; `SessionLifetimeDays` ≥ `RefreshIdleDays`; grace 0–60; at least one provider; provider names ∈ {`Google`} in S2
  (others: "not available in this build"); `ClientIds` non-empty; `Authority` absolute, `https` unless
  `AllowHttpMetadata`; `Alvo:Secrets:EncryptionKeyFile` set and the file exists (spec §5.1, named in the message);
  `Alvo:Auth:HeaderName` is not `Authorization` (ordinal-ignore-case; message says the Bearer channel needs it);
  an `IAccountStore` is registered (message: "Accounts need a relational data provider: UseSqlite or UsePostgreSql").
- `AccountsStartupWarnings : IHostedService` logs, on every start, one warning per provider with `AllowHttpMetadata`.

- [ ] **Step 1: Write the failing facts** (`AccountsOptionsTests`, a bare host built with `AddAlvo` + SQLite +
  `AddAlvoAccounts`, started and expecting `OptionsValidationException`):
  `A_host_without_a_secrets_key_file_is_refused_naming_the_setting`;
  `An_issuer_is_required_outside_development` (Production → refused naming `Alvo:Accounts:Issuer`; Development → starts);
  `An_http_authority_is_refused_unless_allowed_and_then_warned_on_every_start`;
  `An_api_key_header_named_authorization_is_refused_beside_accounts` (**Review Focus 4**);
  `Out_of_range_lifetimes_are_refused` (theory: 0 and 61 minutes, grace 61, session shorter than idle);
  `A_provider_this_build_does_not_offer_is_refused` (`Microsoft`); `A_host_without_a_relational_store_is_refused`.
  `AccountsArchitectureTests`: references only Abstractions, core and `Microsoft.IdentityModel.*` among Alvo/third-party
  assemblies; the shared `InternalMemberReferences` guard (the Ai/Admin fact's shape).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*AccountsOptionsTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement**, register in the `.slnx`, write the PublicApi baseline from the first run's `.received.txt`
  (exactly the public types above).
- [ ] **Step 4: Run** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests`, then
  `dotnet test --project test/MMLib.Alvo.Conventions.Tests`; Expected: PASS.
- [ ] **Step 5: Commit** — `feat(accounts): the MMLib.Alvo.Accounts package and its startup refusals`

### Task 5: Signing keys, access tokens and the refresh-token format

**Files:**
- Create: `src/MMLib.Alvo.Accounts/Internal/AccountKeys.cs`, `AccessTokens.cs`, `RefreshTokenFormat.cs`, `TokenTimes.cs`,
  `AccountsIssuer.cs`
- Test: `test/MMLib.Alvo.Accounts.Tests/AccountKeysTests.cs`, `AccessTokensTests.cs`, `RefreshTokenFormatTests.cs`

**Interfaces:**
- Consumes: `IAccountStore.EnsureKeyAsync`/`ReadKeysAsync`, `AccountKeyPurposes` (Task 3).
- `internal sealed record SigningKey(string Kid, ECDsaSecurityKey Key)`.
- `internal sealed class AccountKeys(IAccountStore store, TimeProvider clock)`: `ValueTask<SigningKey> SigningAsync(CancellationToken)`
  (ensures generation 1: candidate = a fresh P-256 key, `Kid` = its RFC 7638 JWK thumbprint, base64url;
  `Material` = base64 PKCS#8), `ValueTask<byte[]> RefreshMacKeyAsync(CancellationToken)` (32 random bytes, base64),
  `ValueTask<IReadOnlyList<SigningKey>> PublishedAsync(CancellationToken)`, `ValueTask<SigningKey?> FindAsync(string kid, CancellationToken)`
  — a miss reloads from the store at most once per 5 minutes. All cached after the first read.
- `internal static class TokenTimes`: `bool Live(DateTimeOffset? exp, DateTimeOffset? nbf, DateTimeOffset now, TimeSpan skew)`,
  `bool Fresh(DateTimeOffset? iat, DateTimeOffset now, TimeSpan maxAge, TimeSpan skew)`.
- `internal sealed record AccessTokenSubject(UserId Account, Guid Session, string Provider)`.
- `internal sealed class AccessTokens(AccountKeys keys, IOptions<AlvoAccountsOptions> options, TimeProvider clock)`:
  `ValueTask<string> MintAsync(AccessTokenSubject subject, string issuer, string audience, CancellationToken)` — RFC 9068:
  header `typ: at+jwt`, `alg: ES256`, `kid`; claims `iss`, `aud`, `sub`, `sid` (N-format), `iat`, `exp` (+ `AccessTokenMinutes`),
  `jti` (fresh uuid), `client_id: "alvo"`, `idp`; **nothing else** (D5 of the spec);
  `ValueTask<AccessTokenSubject?> ValidateAsync(string token, string issuer, string audience, CancellationToken)` —
  `ValidTypes = ["at+jwt"]`, `ValidAlgorithms = ["ES256"]`, key by `kid` only (`TryAllIssuerSigningKeys = false`),
  `ValidateLifetime = false` + `TokenTimes.Live(exp, nbf, now, 30 s)` (D5); no I/O on a known `kid`.
- `internal static class AccountsIssuer`: `string IssuerFor(HttpRequest request, AlvoAccountsOptions options)` —
  `options.Issuer` or, in Development only, `{scheme}://{host}{pathBase}{prefix}`; `string AudienceFor(AlvoAccountsOptions, SignInPolicy)`
  — `options.Audience ?? policy.ProjectName`.
- `internal static class RefreshTokenFormat`: `const string Prefix = "alvo_rt_"`;
  `string Format(Guid id, ReadOnlySpan<byte> key)` → `alvo_rt_<id:N>_<base64url(HMAC-SHA256(key, id:N))>`;
  `bool TryParse(string? value, ReadOnlySpan<byte> key, out Guid id)` — fixed positions (prefix 8, id 32 hex, `_`,
  43-char MAC; base64url contains `_`, so never split on it), `CryptographicOperations.FixedTimeEquals`.

- [ ] **Step 1: Write the failing facts:**
  `AccessTokensTests`: `A_token_carries_exactly_the_rfc_9068_claims_and_header` (decode with `JsonWebToken`: claim set
  equals `{iss, aud, sub, sid, iat, exp, jti, client_id, idp}`, `typ` `at+jwt`, `alg` `ES256`, `kid` present,
  `exp - iat = 900`); `A_token_validates_within_30_seconds_past_exp_and_not_31` (**Review Focus 5**, `FakeTimeProvider`);
  theory `A_token_is_refused_for` — wrong `typ` (`JWT`), wrong `iss`, wrong `aud`, unknown `kid`, `alg: none`
  (hand-built), an HS256 token signed with the public key bytes, a flipped signature byte;
  `Validation_of_a_known_kid_does_no_store_read` (NSubstitute store: after the first `SigningAsync`, 100 validations →
  `ReadKeysAsync`/`EnsureKeyAsync` received no further calls).
  `AccountKeysTests`: `Two_replicas_over_one_store_mint_tokens_each_other_validates` (**Review Focus 5**: two
  `AccountKeys` + `AccessTokens` pairs over one real SQLite `IAccountStore`, first calls concurrent; A's token validates
  at B and B's at A; one signing row); `The_kid_is_the_rfc_7638_thumbprint`;
  `An_unknown_kid_reloads_the_store_at_most_once_per_five_minutes`.
  `RefreshTokenFormatTests`: round trip; a MAC whose first char is `_` parses; theory of refusals (wrong prefix, short,
  upper-case hex, one MAC char changed, another key, trailing space, `null`).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*AccessTokensTests*' --filter-class '*AccountKeysTests*' --filter-class '*RefreshTokenFormatTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — same command; Expected: PASS.
- [ ] **Step 5: Commit** — `feat(accounts): ES256 access tokens per RFC 9068 and derivable refresh tokens`

### Task 6: The OIDC ID-token verifier, the Google preset, and the test issuer

**Files:**
- Create: `src/MMLib.Alvo.Accounts/Internal/IIdentityVerifier.cs`, `OidcProvider.cs`, `OidcProviders.cs`,
  `OidcIdTokenVerifier.cs`, `GooglePreset.cs`
- Create: `test/_shared/accounts/TestOidcIssuer.cs`
- Test: `test/MMLib.Alvo.Accounts.Tests/OidcIdTokenVerifierTests.cs`

**Interfaces:**
- `internal enum IdTokenRefusal { Malformed, UnknownIssuer, Signature, Algorithm, Audience, Expired, NotYetValid, Stale, Unreachable }`.
- `internal sealed record IdentityVerification(VerifiedIdentity? Identity, string? SubjectTokenId, IdTokenRefusal? Refusal)` —
  `SubjectTokenId` = base64url(SHA-256(`iss` + "\n" + (`jti` ?? base64url(SHA-256(token))))); `PrintMembers` prints no token.
- `internal interface IIdentityVerifier { ValueTask<IdentityVerification> VerifyAsync(string subjectToken, CancellationToken ct); }`
  (the §5.8 seam; internal until a second implementation exists).
- `internal sealed record OidcProvider(string Name, AuthProvider Provider, IReadOnlySet<string> Issuers, string Authority,
  IReadOnlyList<string> ClientIds, IReadOnlySet<string>? Algorithms, Func<JsonWebToken, bool> EmailTrusted, bool AllowHttpMetadata)`;
  `GooglePreset.From(AlvoOidcProviderOptions)` per D6; `OidcProviders.TryFindByIssuer(string iss, out OidcProvider)`.
- `OidcIdTokenVerifier(OidcProviders, IHttpClientFactory, TimeProvider)`: read `iss` unvalidated → provider (none →
  `UnknownIssuer`, step 1) → validate with `JsonWebTokenHandler` and a per-provider
  `ConfigurationManager<OpenIdConnectConfiguration>` over `HttpDocumentRetriever(httpClient) { RequireHttps = !AllowHttpMetadata }`,
  `RefreshInterval = 5 min` (unknown `kid` → at most one JWKS refetch per 5 min), `ValidIssuers`, `ValidAudiences =
  ClientIds`, `ValidAlgorithms` (D6); then `TokenTimes.Live(exp, nbf, now, 2 min)` and `TokenTimes.Fresh(iat, now, 5 min, 2 min)`
  (D5); then the identity: `sub`, `email`, `EmailTrusted(token)`, `name`, `picture`. Named `HttpClient`
  `"alvo-accounts-oidc"`. Discovery failure → `Unreachable` (logged without the token).
- Test-only `TestOidcIssuer` (`test/_shared/accounts/`): owns an RSA-2048 key (`kid` "test-1");
  `const string Authority = "https://issuer.test"`; `HttpMessageHandler Handler` serving
  `/.well-known/openid-configuration` and `/jwks`; `int JwksFetches`; `void RotateKey(string kid)`;
  `string Mint(string subject, string? email = "ada@gmail.com", bool emailVerified = true, string? hd = null,
  string audience = "client-1", DateTimeOffset? issuedAt = null, TimeSpan? lifetime = null, string? jti = null, string? alg = null, string? kid = null)`;
  `void UseIn(IServiceCollection services)` — points the `"alvo-accounts-oidc"` client's primary handler at `Handler`.
  The Google preset under test sets `Authority = TestOidcIssuer.Authority` and `Issuers = {Authority}`.

- [ ] **Step 1: Write the failing facts** (`FakeTimeProvider` at a fixed instant; one row per §5.2 step 1–3 and §5.3):
  `A_valid_id_token_yields_the_identity_and_a_subject_token_id_without_the_token`;
  theory `An_id_token_is_refused` → expected `IdTokenRefusal`: issuer not configured (`UnknownIssuer`), audience
  another app's client id (`Audience`), signature by another key (`Signature`), `alg: none` and `HS256`
  (`Algorithm`), `exp` 2 min + 1 s ago (`Expired`), `nbf` 2 min + 1 s ahead (`NotYetValid`), `iat` 5 min + 1 s ago
  (`Stale`), not a JWT (`Malformed`);
  `The_boundaries_are_accepted` (**Review Focus 5**: `iat` exactly 5 min old, `exp` exactly 2 min ago);
  `Both_google_issuer_spellings_are_google`;
  `An_unknown_kid_refetches_the_jwks_once_within_five_minutes` (rotate the issuer's key; first token with the new
  `kid` → valid, `JwksFetches` +1; a token with a third unknown `kid` within 5 min → `Signature`, no further fetch);
  theory `Google_trusts_an_email_only_when_verified_and_gmail_or_hosted` (verified+gmail → true, verified+`hd` → true,
  verified+other domain without `hd` → false, unverified gmail → false, `Ada@GMAIL.com` verified → true);
  `A_generic_provider_trusts_email_only_when_configured_and_verified`;
  `An_http_authority_is_fetched_only_when_allowed`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*OidcIdTokenVerifierTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — same command; Expected: PASS.
- [ ] **Step 5: Commit** — `feat(accounts): a generic OIDC ID-token verifier with a Google preset`

### Task 7: The token endpoint — signing in by token exchange

**Files:**
- Create: `src/MMLib.Alvo.Accounts/Internal/TokenEndpoint.cs`, `ExchangeGrant.cs`, `SignUpDecision.cs`,
  `TokenResponse.cs`, `TokenProblems.cs`, `AccountsEndpoints.cs`
- Modify: `src/MMLib.Alvo/Api/AlvoProblemTypes.cs` (four slugs, D4), `src/MMLib.Alvo/Api/Internal/ProblemResultFactory.cs`
  (an `internal static IResult OAuthError(int status, string slug, string error, string description, IReadOnlyDictionary<string, object?>? extensions = null)`),
  `AlvoAccountsEndpointRouteBuilderExtensions.cs`
- Create: `test/_shared/accounts/AccountsWorld.cs`, `test/_shared/accounts/descriptors/time-tracker.alvo.json`
- Test: `test/MMLib.Alvo.Accounts.Tests/TokenExchangeTests.cs`, `test/MMLib.Alvo.Accounts.Tests/SignUpDecisionTests.cs`

**Interfaces:**
- `POST {prefix}/token`, operationId `auth.token`, tag `auth`. Refusals before parsing: content type not
  `application/x-www-form-urlencoded` or body > 16 KiB → `invalid_request`; a parameter sent twice → `invalid_request`
  (RFC 6749 §3.2). `grant_type`: token-exchange → `ExchangeGrant`; `refresh_token` → Task 8; else
  `unsupported_grant_type`.
- Exchange: `subject_token` required; `subject_token_type` must be `urn:ietf:params:oauth:token-type:id_token`;
  `requested_token_type` absent or `urn:ietf:params:oauth:token-type:access_token`; `audience`/`resource` present →
  `invalid_target`; any verifier refusal → `invalid_request` (RFC 8693 §2.2.2) with one fixed description
  `"The subject token is not a valid, fresh ID token from a provider this backend accepts."`; provider not listed by
  `SignInPolicy` → the same `invalid_request` (D10).
- `internal enum SignUpVerdict { Admitted, InvitationOnly, DomainNotAllowed }`;
  `static SignUpVerdict SignUpDecision.Decide(SignInPolicy policy, VerifiedIdentity identity)` — `InvitationOnly` when
  `Mode == InvitationOnly` (D14); `DomainNotAllowed` when domains are set and the email is untrusted, absent, or its
  domain (after the last `@`, ordinal-ignore-case, exact — no subdomains) is not listed. `MayCreate = verdict == Admitted`.
- Store outcome → answer: `Created`/`SignedIn` → 200; `SignUpRefused` → 403 `sign-up-refused`, `access_denied`,
  `policy` = `invitationOnly` | `allowedEmailDomains`; `Disabled` → 403 `sign-in-refused`, `access_denied`;
  `SubjectTokenReplayed` → `invalid_request` (the fixed description).
- `internal sealed record TokenResponse(string AccessToken, string IssuedTokenType, string TokenType, int ExpiresIn, string RefreshToken)`
  — snake_case JSON, `issued_token_type` omitted on refresh responses (Task 8), `PrintMembers` redacts both tokens.
  Every token-endpoint answer carries `Cache-Control: no-store` and `Pragma: no-cache` (RFC 6749 §5.1).
- Session: `SessionExpiresAt = now + SessionLifetimeDays`; ids from `Guid.NewGuid()`; refresh value from
  `RefreshTokenFormat.Format(refreshId, await keys.RefreshMacKeyAsync())`.
- `AccountsWorld` (test-only): `static Task<AccountsWorld> StartAsync(AlvoApiEngine engine, Action<AlvoAccountsOptions>? configure = null,
  string descriptor = "time-tracker.alvo.json", Action<AlvoApiOptions>? api = null)` over `AlvoApiWorld.FromDescriptorAsync`
  with: a temp 32-byte key file as `AlvoSecretOptions.EncryptionKeyFile`, `TestOidcIssuer`, Google preset at the issuer,
  `ClientIds = ["client-1"]`, `Issuer = "https://alvo.test/auth/v1"`, a `FakeTimeProvider` replacing `TimeProvider`,
  a capturing `ILoggerProvider` at Trace (`Logs`), `MapAlvoAccounts()`; helpers `ExchangeAsync(string idToken)`,
  `SignInAsync(string subject, …) → (string Access, string Refresh)`, `RefreshAsync(string refresh)`.
  `time-tracker.alvo.json` is spec §3's level-1 descriptor verbatim (name `time-tracker`).

- [ ] **Step 1: Write the failing facts** (`TokenExchangeTests`, SQLite world):
  `A_first_exchange_creates_the_account_and_answers_rfc_8693` (200; JSON members exactly `access_token`,
  `issued_token_type` = `…:access_token`, `token_type` = `Bearer`, `expires_in` = 900, `refresh_token` starting
  `alvo_rt_`; `Cache-Control: no-store`; the access token's `aud` is `time-tracker`, `idp` `google`);
  `A_later_exchange_signs_into_the_same_account`; `A_replayed_id_token_starts_no_second_session` (400
  `invalid_request`, type `…/token-refused`);
  theory `A_malformed_request_is_an_rfc_6749_error` (JSON body; wrong `subject_token_type`; missing `subject_token`;
  `grant_type=password` → `unsupported_grant_type`; `subject_token` twice; `audience=x` → `invalid_target`; 17 KiB body)
  — each a problem document with `error` and `error_description`, never echoing the token;
  `An_issuer_the_host_did_not_configure_is_invalid_request`; `A_provider_the_descriptor_does_not_list_is_invalid_request`
  (descriptor without `providers`);
  `Invitation_only_refuses_a_new_person_and_admits_an_existing_one` (D14: sign in under `open`, re-apply with
  `invitationOnly`; existing subject → 200; new subject → 403 `sign-up-refused`, `policy` `invitationOnly`, no account row);
  theory `Allowed_domains_admit_only_a_trusted_address_in_a_listed_domain` (`kros.sk` listed: verified gmail? no →
  refused; `ada@kros.sk` with `hd` → 200; `ada@KROS.SK` with `hd` → 200; `ada@sub.kros.sk` → refused;
  `ada@kros.sk` unverified → refused, `policy` `allowedEmailDomains`).
  `SignUpDecisionTests`: the pure table of the same cases.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*TokenExchangeTests*' --filter-class '*SignUpDecisionTests*'`; Expected: FAIL (compile).
- [ ] **Step 3: Implement.** Accept the core PublicApi baseline growth (four consts) — Task 11 adds the rest.
- [ ] **Step 4: Run** — same command, then `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*PublicApiApprovalTests*'` and
  `dotnet test --project test/MMLib.Alvo.DocsGen.Tests -- --filter-class '*ProblemType*'` (add the notes it asks for).
  Expected: PASS.
- [ ] **Step 5: Commit** — `feat(accounts): sign in by exchanging an ID token (RFC 8693)`

### Task 8: The refresh grant — rotation, the grace window, reuse

**Files:**
- Create: `src/MMLib.Alvo.Accounts/Internal/RefreshGrant.cs`; Modify: `TokenEndpoint.cs`
- Test: `test/MMLib.Alvo.Accounts.Tests/RefreshGrantTests.cs`

**Interfaces:**
- Consumes: `IAccountStore.RefreshAsync` (Task 3), `RefreshTokenFormat` (Task 5), `SignInPolicy.Lists` (Task 1).
- `grant_type=refresh_token&refresh_token=…`: unparsable or MAC-invalid → `invalid_grant` without a store call;
  `RefreshAsync(new(id, Guid.NewGuid(), now, grace, idle))`; `Rotated`/`Replayed` → 200 with a fresh access token and
  `refresh_token = Format(ChildId)` (no `issued_token_type`), **unless** the outcome's `Provider` is no longer listed
  → `invalid_grant` (D10); `Reused`/`Refused` → 400 `invalid_grant` with one fixed description
  `"The refresh token is not valid. Sign in again."` — identical for unknown, idle, revoked, expired, disabled and reused.

- [ ] **Step 1: Write the failing facts:**
  `A_refresh_rotates_and_the_child_refreshes_again`;
  `Two_concurrent_refreshes_get_the_same_child_and_the_session_survives` (**Review Focus 1**: two parallel
  `POST /token` with one refresh token, 10 iterations → both 200, identical `refresh_token`, distinct `access_token`;
  the child then refreshes; a Data API `GET /api/time_entries` with either access token → 200);
  `A_reuse_after_the_window_kills_every_token_of_the_session_before_answering` (advance 11 s; parent → 400
  `invalid_grant`; then the child → `invalid_grant`, and the access token minted last → 401 on the Data API);
  `A_reuse_at_exactly_ten_seconds_answers_the_same_child` (**Review Focus 5**);
  `A_zero_grace_window_revokes_on_any_reuse`;
  `An_idle_refresh_token_and_an_expired_session_are_invalid_grant` (advance 30 days; separately 180 days with refreshes
  every 20 days);
  theory `Every_dead_refresh_token_answers_byte_identically` (unknown id with valid MAC, bad MAC, revoked by logout,
  reused, idle, disabled account → same status, `error`, `error_description`, `type`).
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*RefreshGrantTests*'`; Expected: FAIL. (The Data API assertions need Task 9's resolver: write them now, mark them `Skip = "Task 9"`, and un-skip in Task 9.)
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — same command; Expected: PASS (the skipped ones skipped).
- [ ] **Step 5: Commit** — `feat(accounts): refresh tokens rotate, replay inside the window, and kill the session after it`

### Task 9: Who is calling — the person resolver, membership resolution, `/user` and `/logout`

**Files:**
- Create: `src/MMLib.Alvo/Auth/Internal/MembershipResolution.cs`; Modify: `src/MMLib.Alvo/Auth/Setup.cs` (`TryAddSingleton<IMembershipResolution, NoTenantMembershipResolution>()`)
- Create: `src/MMLib.Alvo.Accounts/Internal/PersonSessions.cs`, `PersonResolver.cs`, `UserEndpoints.cs`
- Test: `test/MMLib.Alvo.Tests/Auth/MembershipResolutionTests.cs`, `test/MMLib.Alvo.Accounts.Tests/PersonResolverTests.cs`,
  `test/MMLib.Alvo.Accounts.Tests/UserEndpointsTests.cs`

**Interfaces:**
- Core (master plan's fixed shape): `internal interface IMembershipResolution { ValueTask<MembershipResolution> ResolveAsync(UserId user, TenantId? requested, CancellationToken ct); }`;
  `internal sealed record MembershipResolution(bool Refused, TenantId? Tenant, IReadOnlySet<Role> TenantRoles)` with
  `static Refusal` and `static NoTenant`. `NoTenantMembershipResolution`: `requested` set → `Refusal`, else `NoTenant`;
  no I/O (D13). S3 replaces the registration.
- `internal sealed record PersonSession(UserId Account, Guid Session, string Provider)`.
- `internal sealed class PersonSessions(AccessTokens tokens, IAccountStore store, IPolicyCatalogProvider catalog, IOptions<AlvoAccountsOptions> options, TimeProvider clock)`:
  `ValueTask<PersonSession?> AuthenticateAsync(string accessToken, HttpRequest request, CancellationToken ct)` —
  validate in memory (Task 5); provider not listed → null (D10); `IsSessionLiveAsync` → null when dead.
  `ValueTask<PersonSession?> IdentifyAsync(string accessToken, HttpRequest request, CancellationToken ct)` — signature,
  claims and lifetime only (for `/logout`'s idempotency).
- `internal sealed class PersonResolver(PersonSessions sessions, IMembershipResolution memberships, IHttpContextAccessor http) : IAlvoContextResolver`,
  registered `AddKeyedScoped<IAlvoContextResolver, PersonResolver>(CallerResolution.PersonResolverKey)`:
  JWT in memory → requested tenant (empty → none; unparsable → refuse) → `ResolveAsync` (refused → null) → session read
  → the principal of D15. `IHttpContextAccessor` (or a scoped request holder) supplies the request for the issuer.
- Routes (Bearer only, read through `CallerResolution.Read(request, authOptions, CredentialSurface.Data, personRegistered: true)`;
  anything but a Bearer → 401 `BearerUnauthenticated("invalid_request")` when malformed/ambiguous, plain Bearer
  challenge when absent): `GET {prefix}/user` (`auth.getUser`) → `{ id, email, emailTrusted, name, avatarUrl, providers, memberships: [] }`;
  `PATCH {prefix}/user` (`auth.updateUser`) JSON `{ "name": string }`, trimmed 1–100 chars, no control characters,
  else 400 `validation` at `/name`; non-JSON → 415 `unsupported-media-type` → 200 with the user;
  `POST {prefix}/logout` (`auth.logout`) → `IdentifyAsync` then `RevokeSessionAsync(session, "logout")` → 204, also for
  an already-revoked session; an invalid token → 401. All answers `Cache-Control: no-store`.

- [ ] **Step 1: Write the failing facts:**
  `MembershipResolutionTests`: `No_requested_tenant_is_no_tenant`; `Any_requested_tenant_is_refused_in_this_build`.
  `PersonResolverTests` (HTTP, `AccountsWorld`): `A_person_writes_and_reads_their_own_rows_with_a_bearer_token`
  (POST `/api/time_entries` → 201, row's `created_by` = the account id from `/user`; GET → 1 item);
  `Another_person_sees_none_of_them` (level 1's rules);
  `A_tenant_header_refuses_the_whole_principal` (`X-Alvo-Tenant: <uuid>` → 401 `invalid_token`, also an unparsable value);
  `The_principal_is_a_user_with_only_authenticated` (published principal: `ActorKind.User`, roles `{authenticated}`,
  no tenant, `KeyId` `session:<sid>`); `A_signed_out_session_is_401_on_its_next_request`;
  `A_descriptor_that_drops_google_refuses_live_sessions_and_refreshes` (**Review Focus 3**: re-apply without
  `providers` → Data API 401, refresh `invalid_grant`, exchange `invalid_request`);
  `An_access_token_is_validated_without_reading_keys_again` (counting decorator over `IAccountStore`: 20 requests → 20
  `IsSessionLiveAsync`, 0 key reads after the first).
  `UserEndpointsTests`: `User_answers_the_account_its_providers_and_no_memberships`;
  `Patching_the_name_changes_only_the_name_and_survives_the_next_sign_in` (D8);
  theory `A_bad_name_is_a_validation_problem` (`""`, 101 chars, `"a\u0007b"`);
  `Logout_is_idempotent_and_kills_the_refresh_token`; `User_without_a_bearer_is_401_with_a_bearer_challenge`;
  `An_api_key_is_not_a_person_here` (dev key only → 401).
  Un-skip Task 8's Data API assertions.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*MembershipResolutionTests*'` and
  `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*PersonResolverTests*' --filter-class '*UserEndpointsTests*' --filter-class '*RefreshGrantTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — same commands; Expected: PASS.
- [ ] **Step 5: Commit** — `feat(accounts): a bearer token is a person, with no tenant until S3`

### Task 10: Discovery and the document — RFC 8414 metadata, JWKS, OpenAPI

**Files:**
- Create: `src/MMLib.Alvo.Accounts/Internal/DiscoveryEndpoints.cs`, `AccountsDocumentTransformer.cs`
- Modify: `src/MMLib.Alvo/Api/Internal/AlvoDocumentTransformer.cs` (`SecurityOf`, `AddCredential`),
  `src/MMLib.Alvo/Api/Internal/CommandDocumentTransformer.cs`, `scripts/lint-api`
- Test: `test/MMLib.Alvo.Accounts.Tests/DiscoveryTests.cs`, `test/MMLib.Alvo.Accounts.Tests/AccountsOpenApiTests.cs`
  (+ `AccountsOpenApiTests.The_document_with_sign_in_is_stable.verified.txt`), `test/MMLib.Alvo.Api.Tests/OpenApiDocumentTests.cs`

**Interfaces:**
- `GET {prefix}/.well-known/oauth-authorization-server` (`auth.metadata`) and its RFC 8414 §3.1 twin (D7):
  `{ issuer, token_endpoint, jwks_uri, grant_types_supported: ["urn:ietf:params:oauth:grant-type:token-exchange", "refresh_token"],
  token_endpoint_auth_methods_supported: ["none"], response_types_supported: [] }` — URLs from `AccountsIssuer`;
  `Cache-Control: public, max-age=300`.
- `GET {prefix}/.well-known/jwks.json` (`auth.jwks`) → `{ "keys": [{ kty: "EC", crv: "P-256", x, y, kid, use: "sig", alg: "ES256" }] }`
  for every published signing key; never `d`; `Cache-Control: public, max-age=300`.
- Core: when `CallerResolvers.HasPerson`, the document gains component `alvoBearer` (`http`, scheme `bearer`,
  `bearerFormat` `JWT`, description: Alvo access token from `{prefix}/token`), and every Data API and command
  operation's security becomes `[ {}, {alvoApiKey}, {alvoBearer} ]`; without a person resolver the document gains no
  path, operation or security scheme (`OpenApiDocumentTests.The_document_is_stable` moves only by D4's four problem-type
  enum values, as D9 records).
- `AccountsDocumentTransformer` (registered by `AddAlvoAccounts` the way `AlvoOpenApiSetup` registers the core's):
  operations of the auth routes carry summaries, tag `auth`, operationIds as above, the form request body of
  `auth.token`, every response documenting `Cache-Control`, every error response `application/problem+json`, and
  `{alvoBearer}` security on `auth.getUser`/`auth.updateUser`/`auth.logout`.
- `scripts/lint-api` lints both snapshots (loop over the two paths; each missing file is a FAIL).

- [ ] **Step 1: Write the failing facts:**
  `DiscoveryTests`: `The_metadata_names_the_configured_issuer_and_its_endpoints`; `Both_metadata_urls_answer_the_same_document`;
  `The_jwks_publishes_the_signing_key_and_no_private_part`; `A_token_validates_against_the_published_jwks`
  (an independent `JsonWebTokenHandler` with only the JWKS document's keys validates a minted access token);
  `In_development_without_an_issuer_the_metadata_follows_the_request` (Development, no `Issuer`, request host
  `api.dev.test` → `issuer` `http://api.dev.test/auth/v1`).
  `AccountsOpenApiTests`: `The_document_with_sign_in_is_stable` (Verify, `MapOpenApiDocument: true`);
  `Every_auth_route_is_in_the_document_once` (paths `/auth/v1/token`, `/auth/v1/user`, `/auth/v1/logout`,
  `/auth/v1/.well-known/oauth-authorization-server`, `/auth/v1/.well-known/jwks.json` — and not the RFC 8414 twin);
  `Data_api_operations_accept_a_bearer_token`.
  `OpenApiDocumentTests`: `Without_a_person_resolver_there_is_no_bearer_scheme`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*DiscoveryTests*' --filter-class '*AccountsOpenApiTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement**; accept the new snapshot (judge it: it is new, not moved).
- [ ] **Step 4: Run** — same command, `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*OpenApiDocumentTests*'`
  (no `.received.*`), then `scripts/lint-api`; Expected: PASS, `[lint-api] OK`. A Vacuum finding is fixed in the
  transformer, never muted.
- [ ] **Step 5: Commit** — `feat(accounts): RFC 8414 metadata, a JWKS, and the bearer scheme in the document`

### Task 11: CORS and rate limits in the core

**Files:**
- Create: `src/MMLib.Alvo/Api/AlvoCorsOptions.cs`, `src/MMLib.Alvo/Api/AlvoRateLimitOptions.cs`,
  `src/MMLib.Alvo/Api/Internal/AlvoCors.cs`, `src/MMLib.Alvo/Api/Internal/AlvoRateLimits.cs`
- Modify: `src/MMLib.Alvo/Api/AlvoApiOptions.cs`, its validator, `src/MMLib.Alvo/Api/Setup.cs`,
  `src/MMLib.Alvo/Api/Internal/DataApiEndpoints.cs` (`Protect`: CORS filter first), `AlvoDataApiEndpointRouteBuilderExtensions.cs`
  (the preflight endpoint), `src/MMLib.Alvo.Accounts/Internal/AccountsEndpoints.cs` (CORS on every auth route + preflight;
  rate limit on `/token`), `docs/architecture/data-api.md` (a *CORS and rate limits* section with D3)
- Test: `test/MMLib.Alvo.Api.Tests/CorsTests.cs`, `test/MMLib.Alvo.Api.Tests/AlvoApiOptionsTests.cs` (or the existing
  options suite), `test/MMLib.Alvo.Accounts.Tests/TokenRateLimitTests.cs`

**Interfaces:**
- Public: `AlvoApiOptions.Cors { get; set; } = new()` of `AlvoCorsOptions { List<string> Origins }`;
  `AlvoApiOptions.RateLimits { get; set; } = new()` of `AlvoRateLimitOptions { int Token = 30 }` (S3/S4 add
  `Invitations`, `Create`).
- Validation: every origin an absolute `http`/`https` URI with no path (other than `/`), query or fragment; `*` refused
  with `"Alvo:Api:Cors:Origins may not contain '*': Alvo's routes take credentials in headers, so name each origin."`;
  `Token` ≥ 1.
- `internal sealed class AlvoCors(IOptions<AlvoApiOptions> api, IOptions<AlvoAuthOptions> auth, ICorsService cors)`:
  `bool Enabled`; `CorsPolicy Policy` (origins as configured, methods GET/POST/PUT/PATCH/DELETE, headers
  `Authorization`, `Content-Type`, the configured API-key and tenant headers, `Idempotency-Key`, `If-Match`; exposed
  `ETag`, `Location`, `WWW-Authenticate`, `Retry-After`; `PreflightMaxAge` 10 min; never credentials);
  `IEndpointFilter Filter` (evaluate + apply before `next`); `void MapPreflight(RouteGroupBuilder group)` —
  `OPTIONS /{**path}`, excluded from description, answers 204 with the applied result; mapped only when `Enabled`.
  `services.AddCors()` is called by `AddAlvo` (idempotent).
- `internal enum AlvoRateLimit { Token }`; `internal sealed class AlvoRateLimits(IOptions<AlvoApiOptions> options) : IDisposable`:
  `IEndpointFilter For(AlvoRateLimit limit)` — a `PartitionedRateLimiter<HttpContext>` of fixed one-minute windows,
  `QueueLimit = 0`, partitioned by `Connection.RemoteIpAddress` (IPv4-mapped → IPv4, IPv6 → its /64; `null` → one
  shared partition); refused → 429 `rate-limited`, `Retry-After` in seconds from the lease metadata (60 if absent).

- [ ] **Step 1: Write the failing facts:**
  `CorsTests` (Api.Tests world): `Without_origins_no_cors_header_is_ever_sent` (GET with `Origin` → no
  `Access-Control-*`; `OPTIONS` preflight → 405 as today); `An_allowed_origin_gets_its_headers_on_success_and_on_401_and_403`;
  `A_preflight_from_an_allowed_origin_names_the_bearer_and_api_key_headers`; `Another_origin_gets_nothing`;
  `Credentials_are_never_allowed`.
  `AlvoApiOptionsTests`: theory refusing `*`, `https://a.test/path`, `not-a-uri`, `Token = 0`.
  `TokenRateLimitTests`: `The_token_endpoint_answers_429_after_its_limit` (`Token = 2`: third request in the window →
  429 `rate-limited` with `Retry-After`); `The_limit_does_not_touch_the_data_api`;
  `An_auth_route_from_an_allowed_origin_carries_cors_headers_and_answers_its_preflight`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*CorsTests*' --filter-class '*AlvoApiOptions*'` and
  `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*TokenRateLimitTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement.** Accept the core PublicApi growth (two properties, two types).
- [ ] **Step 4: Run** — same commands, then `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*DataApiAuthTests*' --filter-class '*OpenApiDocumentTests*'`
  (unchanged: the preflight endpoint is excluded from the description and absent without origins). Expected: PASS.
- [ ] **Step 5: Commit** — `feat(api): CORS and rate limits Alvo applies to its own routes`

### Task 12: Disabling an account (Management) and the standalone host

**Files:**
- Create: `src/MMLib.Alvo.Abstractions/Accounts/IAlvoAccountAdministration.cs` (internal),
  `src/MMLib.Alvo.Accounts/Internal/AccountAdministration.cs`
- Modify: `src/MMLib.Alvo/Management/Access/ManagementOperation.cs` (`ManageAccounts`), `ManagementOperations.cs`
  (→ `Admin`), `src/MMLib.Alvo/Management/Internal/ManagementEndpoints.cs` (`MapAccounts`), `AlvoAccountsServiceCollectionExtensions.cs`
- Modify: `src/MMLib.Alvo.Host/AlvoHost.cs`, `src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj` (reference Accounts, with the
  hosting-decision comment its neighbours carry)
- Modify: `docs/architecture/management-api.md` (route count, a third table, *Disabling an end user*),
  `docs/architecture/host.md` (§Configuration: `Alvo:Accounts`, `Alvo:Api:Cors`, `Alvo:Api:RateLimits`)
- Test: `test/MMLib.Alvo.Accounts.Tests/AccountAdministrationTests.cs`, `test/MMLib.Alvo.Api.Tests/` the management
  contract counterpart, `test/MMLib.Alvo.Host.Tests/HostAccountsTests.cs`

**Interfaces:**
- `internal interface IAlvoAccountAdministration { Task<bool> SetDisabledAsync(UserId account, bool disabled, CancellationToken ct); }`
  — false = no such account. `AccountAdministration` delegates to `IAccountStore.SetDisabledAsync(…, clock.GetUtcNow())`.
  Internal, so its only caller is the gated route; if it ever becomes public (a dashboard view, spec §10) it takes the
  `GuardedUserAdministration` keyed-decorator shape — say so in its remarks.
- Route: `PUT {m}/projects/{project}/accounts/{account:guid}/disabled`, body `ManagementDisabledFlag`, operation
  `ManageAccounts` (Admin), `ManagementRoute(nameof(IAlvoAccountAdministration.SetDisabledAsync), …)`; success answers
  as the users' `/disabled` route does; false → `ManagementNotFound`. Mapped only when
  `IServiceProviderIsService.IsService(typeof(IAlvoAccountAdministration))` (the `MapUsers` precedent).
- Host: when `Alvo:Accounts:Providers` has a child section, `AddAlvoAccounts(a => section.Bind(a))` and, after `MapAlvo`,
  `MapAlvoAccounts()` (D9); `Alvo:Api` already binds into `AlvoApiOptions`, so `Cors`/`RateLimits` come for free.

- [ ] **Step 1: Write the failing facts:**
  `AccountAdministrationTests` (`MapManagementApi: true`, an admin dev key, a developer-level key):
  `Disabling_an_account_ends_its_requests_refreshes_and_sign_ins` (204; the person's access token → 401; refresh →
  `invalid_grant`; exchange → 403 `sign-in-refused`); `Enabling_lets_them_sign_in_again_and_old_sessions_stay_dead`;
  `A_developer_cannot_disable_an_account` (403); `An_unknown_account_is_404`;
  `A_bearer_token_never_reaches_the_route` (the person's own token on the route → anonymous answer, S1).
  Management contract counterpart (`AccountAdministrationRouteTests` in Api.Tests, the `UserAdministrationRouteTests`
  shape): every member has one route, every route names an operation the level table lists, no route in the OpenAPI
  document; without Accounts registered the route is 404.
  `HostAccountsTests`: `A_host_without_accounts_configuration_maps_no_auth_route_and_needs_no_key_file`
  (**Review Focus 4**: `/auth/v1/token` 404, OpenAPI has no `/auth` path and no `alvoBearer`, a request with
  `Authorization: Bearer x` is anonymous); `A_host_with_a_google_provider_maps_sign_in` (key file + Issuer → metadata 200);
  `A_host_with_a_provider_but_no_key_file_refuses_to_start`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*AccountAdministrationTests*'`,
  `dotnet test --project test/MMLib.Alvo.Api.Tests -- --filter-class '*AccountAdministrationRouteTests*' --filter-class '*ManagementContractTests*'`,
  `dotnet test --project test/MMLib.Alvo.Host.Tests -- --filter-class '*HostAccountsTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement**, then confirm no TeaPie pin moves:
  `grep -rn "auth/v1\|alvoBearer" test/teapie test/teapie-field-service test/teapie-field-service-host` → no hits (D9).
- [ ] **Step 4: Run** — the Step 2 commands; Expected: PASS.
- [ ] **Step 5: Commit** — `feat(management): an operator disables an end user's account; the host wires sign-in`

### Task 13: Level 1 end to end on both engines, and no secret in any log

**Files:**
- Create: `test/_shared/accounts/AccountsEngineFacts.cs` (abstract, `Engine`-parameterised);
  `test/MMLib.Alvo.Accounts.Tests/SqliteAccountsEngineTests.cs`;
  `test/MMLib.Alvo.Api.Tests.Integration/PostgresAccountsEngineTests.cs` (+ csproj: reference Accounts, link
  `../_shared/accounts/*.cs`); `src/MMLib.Alvo.Accounts/Properties/AssemblyInfo.cs` grants `MMLib.Alvo.Api.Tests.Integration`
- Create: `test/MMLib.Alvo.Accounts.Tests/SecretsNeverLoggedTests.cs`
- Modify: `docs/architecture/data-api.md` (the credential section: Bearer is real once Accounts is registered; the
  `HeaderName = Authorization` refusal), `CHANGELOG.md` `[Unreleased]`, `docs/PLAN.md` (S2 done, `← YOU ARE HERE` to S3)

**Interfaces:** consumes everything above; produces nothing.

- [ ] **Step 1: Write the facts.**
  `AccountsEngineFacts`:
  `Level_one_end_to_end` (spec §14 AC1: exchange → `POST /api/time_entries` with Bearer → 201 → `GET` 1 item →
  refresh → `GET` with the new access token → 200 → logout → the new access token 401 → the child refresh token
  `invalid_grant`);
  `Reuse_inside_the_window_returns_the_same_child_and_after_it_kills_the_session` (AC1's other half);
  `Two_people_never_see_each_other_s_rows`; `An_api_key_beside_sign_in_behaves_exactly_as_before` (AC7: the world's
  dev key creates and reads rows; with a Bearer too → 401 ambiguous);
  `A_disabled_person_is_refused_on_the_next_request` (AC3 for the account half).
  `SecretsNeverLoggedTests` (AC6, `AccountsWorld.Logs` at Trace, message + exception `ToString` + scope values):
  `No_token_or_key_appears_in_any_log` — run a successful exchange, each Task 7 refusal, two refreshes, a reuse, a
  logout, `/user`, a Data API request with and without a valid token, a disable; collect every ID token, its signature
  segment, every access token and its signature segment, every refresh token and its MAC part, and assert none is a
  substring of any captured line; `No_error_body_echoes_a_presented_token`;
  `Records_that_carry_secrets_print_none` (`TokenResponse`, `IdentityVerification`, `KeyCandidate`, `StoredKey`);
  `IdentityModel_pii_logging_stays_off`.
- [ ] **Step 2: Run them** —
  `dotnet test --project test/MMLib.Alvo.Accounts.Tests -- --filter-class '*SqliteAccountsEngineTests*' --filter-class '*SecretsNeverLoggedTests*'` and
  `dotnet test --project test/MMLib.Alvo.Api.Tests.Integration -- --filter-class '*PostgresAccountsEngineTests*'`.
  Expected: PASS. **Prove they can fail:** log the refresh token at Debug in `RefreshGrant` → the log fact goes red;
  make `RefreshAsync` skip the session revoke on `Reused` → the end-to-end reuse fact goes red on both engines; restore
  both and record the red runs in the commit body.
- [ ] **Step 3: Docs.** Sweep for the claim, not the file:
  `grep -rn -i "authorization.*ignored\|bearer\|google" docs/architecture --include='*.md'` — every contributor-doc
  claim that Bearer is never Alvo's or that `google` is unhonoured is updated; user-facing guides and skills stay for S6.
  `scripts/check-brief-freshness` → OK.
- [ ] **Step 4: Commit** — `test(accounts): level 1 end to end on both engines, and no secret in any log`

### Task 14: Slice gate — Release build, ring1, ring2, security-core checklist

**Files:** none new (fixes only, if a gate finds something).

- [ ] **Step 1: Release build of the whole solution** — `dotnet build MMLib.Alvo.slnx -c Release`; Expected: 0 warnings,
  0 errors (the rings build Debug and CI builds Release; the CA1875 lesson from S0). If the local SDK differs from CI's,
  repeat inside `mcr.microsoft.com/dotnet/sdk:10.0`.
- [ ] **Step 2: ring1** — `scripts/test-ring1`; Expected: `[ring1] OK`. The only moved baselines are those Tasks 1, 7,
  10 and 11 accepted; anything else is a defect, not a baseline.
- [ ] **Step 3: ring2** — `scripts/test-ring2` (Docker up); Expected: exit 0, including `[lint-api] OK` over both
  documents.
- [ ] **Step 4: `alvo-security-core-review` checklist**, item by item, recorded for the PR report: SQL — every account
  statement binds parameters, table names only from `AlvoFrameworkTables`; tokens — JWT per RFC 8725 (explicit `typ`,
  algorithm allow-lists, no `none`/`HS*`, key by `kid`), ID tokens per OIDC Core §3.1.3.7 with `iat` freshness and
  single use; refresh — derivable, MAC compared in constant time, rotation atomic on both engines, grace never forks
  (Review Focus 1), reuse revokes before answering; keys — encrypted with the deployment's cipher, insert-if-absent
  (Review Focus 5), startup refuses without a key file; caller — Bearer only with a person resolver, tenant header
  refuses the principal (no tenant in S2), a person never reaches Management, roles exactly `{authenticated}`;
  default-deny — provider must be listed and configured, a dropped provider ends sessions (Review Focus 3); secrets —
  the log scan (Task 13); CORS — no credentials, no wildcard, absent without origins; rate limit on the token endpoint
  (and its proxy caveat: behind a proxy it partitions by the proxy unless forwarded headers are enabled — stated in
  `host.md`). Close with **mark this change `needs-deep-review`**.
- [ ] **Step 5: Hand back** to the controller (plan-guard, PR report, PR targeting `accounts/s1-role-provenance`). Flag
  for the post-merge mutation run: the EF adapter grows by `EfCoreAccountStore`, so the `data-ef` shards' declared
  counts go stale (they are the shards nearest their ceiling).
