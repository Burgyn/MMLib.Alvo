# End-user accounts: sign-in, sessions, the person resolver (S2), self-service tenants (S3), invitations (S4), integrator tokens (S5) and the field-service demo (S6)

> The contributor's view of `MMLib.Alvo.Accounts` and the parts of the core and the EF adapter it uses: the tables, the
> path a request takes, the invariants and the tests that hold them, and where Alvo deviates from the standards it
> adopts. The design is `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md`
> (§3–§7, §12–§14). Sign-in's decisions D1–D17 are in `docs/superpowers/plans/2026-11-13-accounts-s2-sign-in.md`; the
> tenants' D1–D15 in `docs/superpowers/plans/2026-11-13-accounts-s3-tenants.md`; the invitations' D1–D15 in
> `docs/superpowers/plans/2026-11-13-accounts-s4-invitations.md`; the tokens' D1–D16 in
> `docs/superpowers/plans/2026-11-13-accounts-s5-tokens.md`; the demo's and the docs' D1–D16 in
> `docs/superpowers/plans/2026-11-13-accounts-s6-demo-docs.md`. The first half of this page is sign-in (level 1);
> [*Tenants a person creates (S3)*](#tenants-a-person-creates-s3) is levels 2 and 3,
> [*Invitations and `invitationOnly` sign-up (S4)*](#invitations-and-invitationonly-sign-up-s4) is how a second person
> joins them, [*Tokens for integrators (S5)*](#tokens-for-integrators-personal-and-service-tokens-s5) is how a
> program acts as a person or as a tenant, and [*The demo (S6)*](#the-demo-s6) is all of it running in a real
> container.
>
> **Users read the guides, not this page.** *Sign-in and accounts*
> (`website/src/content/docs/guides/sign-in.mdx`, https://alvo.burgyn.online/guides/sign-in/), *Tenants, members and
> invitations* (`…/guides/tenants-and-invitations.mdx`, https://alvo.burgyn.online/guides/tenants-and-invitations/) and
> *Tokens for integrators* (`…/guides/tokens.mdx`, https://alvo.burgyn.online/guides/tokens/) say what an app author
> can build and how; the routes are the generated *Sign-in, tenants and tokens API* reference
> (https://alvo.burgyn.online/reference/accounts-api/). When a behaviour here changes, the guide that shows it changes
> in the same commit.

## What you can build with it

**Level 1 of the spec: per-person data, without tenants.** A person signs in with Google in your app, and from then on
your client calls the generated Data API as that person. The rules decide what they see, and `created_by` is
filled from who they are. The whole backend is one descriptor:

```json
{
  "name": "time-tracker",
  "auth": { "providers": ["google"], "signUp": { "mode": "open" } },
  "entities": {
    "time_entries": {
      "audit": true,
      "fields": { "minutes": { "type": "integer", "required": true } },
      "rules": {
        "list":   "created_by == @user.id",
        "get":    "created_by == @user.id",
        "create": "'authenticated' in @user.roles",
        "update": "created_by == @user.id",
        "delete": "created_by == @user.id"
      }
    }
  }
}
```

The host registers sign-in and maps its routes beside the Data API:

```csharp
builder.Services.AddAlvo(alvo => alvo.UseSqlite("Data Source=app.db").FromDescriptor("time-tracker.alvo.json").AddDataApi());
builder.Services.AddAlvoAccounts(accounts =>
{
    accounts.Issuer = "https://api.example.com/auth/v1";
    accounts.Providers["google"] = new AlvoOidcProviderOptions { ClientIds = { "1234-web.apps.googleusercontent.com" } };
});
// … plus Alvo:Secrets:EncryptionKeyFile, which encrypts the signing keys at rest.

app.MapAlvoDataApi();
app.MapAlvoAccounts();
```

The standalone host does the same when `Alvo:Accounts:Providers` has a provider section (`host.md`, *Configuration*).
The client then makes three calls:

```http
POST /auth/v1/token
Content-Type: application/x-www-form-urlencoded

grant_type=urn:ietf:params:oauth:grant-type:token-exchange
&subject_token=<the Google ID token>
&subject_token_type=urn:ietf:params:oauth:token-type:id_token

→ 200 { "access_token": "eyJ…", "issued_token_type": "…:access_token", "token_type": "Bearer",
        "expires_in": 900, "refresh_token": "alvo_rt_…" }

POST /api/time_entries            Authorization: Bearer eyJ…      { "minutes": 30 }   → 201, created_by = the person
POST /auth/v1/token               grant_type=refresh_token&refresh_token=alvo_rt_…    → 200, a new pair
```

`GET /auth/v1/user`, `PATCH /auth/v1/user` (the display name only) and `POST /auth/v1/logout` complete the surface.
Discovery is served at `/auth/v1/.well-known/oauth-authorization-server` and at RFC 8414's path-inserted twin
`/.well-known/oauth-authorization-server/auth/v1` (D7). Both point at `/auth/v1/.well-known/jwks.json`. An operator bans a person
with `PUT {management}/projects/{project}/accounts/{account}/disabled` (`management-api.md`, *Disabling an end user*).

`AccountsEngineFacts.Level_one_end_to_end` is this section as a test. It runs on SQLite and on PostgreSQL.

## Why it is shaped this way

- **Alvo issues its own tokens; Google only vouches for the person.** The ID token is exchanged once (RFC 8693) and
  then thrown away. From then on Alvo's own ES256 access token (RFC 9068) is what a request carries. Sessions,
  sign-out, disabling and the descriptor's say over providers then work the same whichever provider signed the
  person in. The provider is behind one internal seam, `IIdentityVerifier`. Google is a preset of a generic OIDC
  ID-token verifier, so the next ID-token provider is configuration plus a preset (spec §5.8).
- **The access token says who you are, never what you may do** (spec D5). It carries `iss aud sub sid iat exp jti
  client_id idp` and nothing else. Roles, tenants and the session's liveness are read per request. That is why a
  disable or a sign-out takes effect on the next request, not after 15 minutes.
- **The descriptor is the switch** (D10). A provider must be configured by the host *and* listed by the applied
  descriptor. Drop `google` from the descriptor and the next exchange, refresh and Bearer request are all refused.
- **Accounts are a package, and on per host** (D1, D9). A host that never calls `AddAlvoAccounts` behaves as the host
  it was:
  - no key file is required;
  - no `/auth` route is mapped;
  - the OpenAPI document gains no path or security scheme (only the shared problem-type enum lists the four new slugs);
  - `Authorization` is ignored.

  It is not untouched, though. On a relational provider the schema initializer creates the five `_auth_*` tables on
  every host, accounts or not, and they stay empty. `AddAlvoApi` calls `services.AddCors()` on every host, so the
  framework's CORS services are registered; Alvo sends no CORS header until `Alvo:Api:Cors:Origins` names an origin.

## The tables

Five framework tables, named only through `AlvoFrameworkTables` suffixes. Each table has one DDL text for both
engines: ids and instants are `TEXT` in the fixed UTC format the timer table writes, and booleans are `INTEGER` 0/1.
`SystemSchemaInitializer` creates them at boot, and the store creates them through `OnceDdl` when a composition never
booted. They have **no foreign keys**, as no framework table does. Each write happens in one transaction, and the
statements inside it run in insertion order. That order is what keeps the rows consistent.

| Table | Holds | Notable constraint |
|---|---|---|
| `{prefix}_auth_accounts` | `UserId`, email + `email_trusted`, display name, avatar, `created_at`, `last_sign_in_at`, `disabled_at` | none on email: accounts are never linked by email (spec §5.8) |
| `{prefix}_auth_identities` | `(provider, subject)` → account, the asserted email | `(provider, subject)` unique: the one way to find an account |
| `{prefix}_auth_sessions` | account, provider (`idp`), `created_at`, `last_refreshed_at`, `expires_at`, `revoked_at`, `revoked_reason`, `subject_token_id` | `subject_token_id` unique: an ID token starts at most one session |
| `{prefix}_auth_refresh_tokens` | id, session, `parent_id`, `created_at`, `rotated_at` — **no secret** | `parent_id` unique: a token has at most one child |
| `{prefix}_auth_keys` | `kid`, purpose (`signing`, `refresh`), generation, material **encrypted with the secret cipher**, `created_at`, `retires_at` | `(purpose, generation)` unique: two replicas booting at once converge on one key |

The store is `IAccountStore`, an internal port in Abstractions that the EF adapter implements (`EfCoreAccountStore`)
and Accounts consumes (D2, following the command-ledger precedent). Every write the spec calls "one store call, one
transaction" is one port member. The caller passes in every id and instant, so the store is deterministic under a fake
clock. Its contract facts are `test/_shared/ef/AccountStoreFacts.cs`, run by `SqliteAccountStoreTests` and
`PostgreSqlAccountStoreTests`.

A refresh token's value is `alvo_rt_<id>_<HMAC-SHA256(key, id)>` (D6). The row stores only the id, and the server
re-derives the child's value instead of storing it. That is what lets the grace window answer the *same* child twice.

## How a request becomes a person

```
Authorization: Bearer <jwt>
   │  CallerResolution.Read — the one credential reading shared by the Data API, the commands and Management
   │  (data-api.md, "How a request becomes a caller"); Bearer is Alvo's only while a person resolver is registered
   ▼
PersonResolver (keyed "alvo-person", registered by AddAlvoAccounts)
   1. PersonSessions.IdentifyAsync — in memory: no applied descriptor → nobody; signature by kid (ES256 only),
      typ at+jwt, iss, aud (Audience or the descriptor's name), exp/nbf ± 30 s on the injected clock
   2. the tenant header → MembershipRules.Plan, no I/O: refuse the principal, no tenant, or "read this membership"
      (S3; see "How a person acts in a tenant" below)
   3. PersonSessions.ReadAsync — the descriptor still lists the session's idp (no I/O), then ONE store read:
      session unrevoked, unexpired, account not disabled — and, in the same statement, the membership the plan named
   4. MembershipRules.Decide — the tenant and its roles under the applied descriptor, or the refusal of the principal
   ▼
AlvoPrincipal: User = account, Roles = {authenticated}, Tenant and TenantRoles = the membership's (or none),
               ActorKind = User, scopes *:read/*:write (the rules decide), KeyId = "session:<sid>"
```

Every refusal is the same 401 `Bearer error="invalid_token"`, so a forged token and an expired session look alike. The
order is the cost model (D13): a refused tenant costs no read, and a valid request costs exactly one.
`PersonResolverTests.An_access_token_is_validated_without_reading_keys_again` counts the reads.

The principal's roles are **project roles**. S1's role provenance decides where `@user.roles` can also see *tenant
roles*: only while every entity on a decision's path is `tenancy: scoped`, and never in a Management `access` level
(spec §6.2; `relation-rules.md`, *Binding*; `management-api.md`, *Who the Management API believes the caller is*).
Without `tenancy.selfService` a person has no tenant, so `TenantRoles` is empty and level 1 uses only `authenticated`
and `@user.id`. With it, the membership step fills the tenant and its roles, folded into the same one read.

The token endpoint follows the same pattern:

- **The exchange:**
  1. Read the form by hand: at most 16 KiB, every parameter at most once.
  2. Find the provider by the token's exact `iss`, and require that the descriptor lists it. This happens *before*
     anything is fetched.
  3. Verify the token per OIDC Core §3.1.3.7.
  4. Check `iat` freshness.
  5. Apply the sign-up policy.
  6. Run one store call that finds or creates the account and starts the session and its first refresh token.
- **The refresh:**
  1. Check the MAC before any read.
  2. Run one store call that rotates, replays the child inside the window, or revokes the session after it.
  3. Check that the descriptor still lists the session's provider.

## Invariants, and the tests that hold them

| Invariant | Held by |
|---|---|
| Level 1 end to end, on both engines: exchange → Data API → refresh → logout leaves every token of the session dead | `AccountsEngineFacts.Level_one_end_to_end` (`SqliteAccountsEngineTests`, `PostgresAccountsEngineTests`) |
| Reuse inside the grace window answers the same child; after it, the whole session dies before the response | `AccountsEngineFacts.Reuse_inside_the_window_returns_the_same_child_and_after_it_kills_the_session`; `RefreshGrantTests.A_reuse_after_the_window_kills_every_token_of_the_session_before_answering` |
| A reuse also revokes, in its transaction, every app token (personal or service) and every invitation the account created since the reused session started, and nothing older; a replay inside the window revokes nothing; a mint or an invitation in flight on the session either writes nothing or is swept (stack review A-I1) | `AccountStoreFacts.A_reuse_revokes_the_tokens_and_invitations_created_since_its_session_started`, `…A_replay_inside_the_window_revokes_nothing_the_person_minted` (both engines; red on both without the sweep); `…A_mint_or_an_invitation_naming_a_session_a_reuse_ended_writes_nothing`, `…A_mint_racing_a_reuse_of_its_session_never_outlives_it`, `…An_invitation_racing_a_reuse_of_its_session_never_outlives_it` (20 iterations, both engines; red on both without the session re-read); `CredentialWritesMidReuseTests` |
| Two tabs refreshing at once get one child, and the session survives (Review Focus 1) | `AccountStoreFacts.Two_concurrent_refreshes_of_one_token_get_the_same_child` (20 iterations, both engines); `AccountsEngineFacts.Two_concurrent_refreshes_get_the_same_child_and_the_session_survives` |
| A refresh racing a sign-out or a disable commits no child; a sign-in racing a disable starts no session | `PostgreSqlAccountStoreTests.A_refresh_racing_a_revocation_commits_no_child`, `…A_sign_in_racing_a_disable_starts_no_session`, both staged with a held row lock |
| Two replicas on a fresh database converge on one signing key and one HMAC key (Review Focus 2) | `AccountStoreFacts.Two_concurrent_first_keys_converge_on_one`; `AccountKeysTests.Two_replicas_over_one_store_mint_tokens_each_other_validates` |
| The descriptor drops `google` → the next Bearer request is 401, the next refresh `invalid_grant`, the exchange refused (Review Focus 3) | `PersonResolverTests.A_descriptor_that_drops_google_refuses_live_sessions_and_refreshes`; `RefreshGrantTests.A_session_whose_provider_the_descriptor_dropped_is_invalid_grant` |
| A provider the descriptor does not list costs no outbound fetch | `TokenExchangeTests.A_provider_the_descriptor_does_not_list_is_invalid_request`; `OidcIdTokenVerifierTests.A_provider_the_caller_does_not_list_is_refused_before_anything_is_fetched` |
| A host without accounts maps no `/auth` route, needs no key file and ignores `Authorization` (it does get the five empty `_auth_*` tables and the CORS services); `Alvo:Auth:HeaderName = Authorization` is refused beside accounts (Review Focus 4) | `HostAccountsTests`; `OpenApiDocumentTests.Without_a_person_resolver_there_is_no_bearer_scheme`; `AccountsOptionsTests.An_api_key_header_named_authorization_is_refused_beside_accounts` |
| An `http://` provider authority needs `AllowHttpMetadata`, and `AllowHttpMetadata` is refused at startup outside Development, exactly as an unset `Issuer` is, naming the setting and why; in Development it starts with a warning on every start. It exists for the end-to-end stack's test issuer only, and only Accounts reads the host environment, so that stack can run Development safely (spec §11.2) | `AccountsOptionsTests.Allowing_http_metadata_is_refused_outside_development_naming_the_setting`, `…An_http_authority_is_refused_unless_allowed_in_development_and_then_warned_on_every_start` |
| An API key beside sign-in works exactly as before; a key and a Bearer together are 401 ambiguous | `AccountsEngineFacts.An_api_key_beside_sign_in_behaves_exactly_as_before`; `CredentialReadingTests` |
| Clock boundaries: `iat` 5 min in, 5 min + 1 s out; ID-token `exp` ± 2 min; access token 30 s past `exp` in, 31 s out; reuse at 10 s replays, at 11 s kills (Review Focus 5) | `OidcIdTokenVerifierTests.The_boundaries_are_accepted`, `…An_id_token_is_refused`; `AccessTokensTests.A_token_validates_within_30_seconds_past_exp_and_not_31`; `RefreshGrantTests.A_reuse_at_exactly_ten_seconds_answers_the_same_child` |
| An ID token starts at most one session; an account is found by `(provider, subject)`, never by email | `TokenExchangeTests.A_replayed_id_token_starts_no_second_session`; `AccountStoreFacts.A_second_sign_in_finds_the_account_by_provider_and_subject_never_by_email` |
| Two people never see each other's rows | `AccountsEngineFacts.Two_people_never_see_each_other_s_rows` |
| A disabled person is refused on the next request, refresh and exchange; enabling a disabled account revokes every session it still has, so none from before it comes back — not even one that reached the table after the ban's revocation; enabling one that is not disabled logs nobody out | `AccountsEngineFacts.A_disabled_person_is_refused_on_the_next_request`; `AccountAdministrationTests`; `AccountStoreFacts.Enabling_an_account_revokes_a_session_the_ban_never_saw`, `…Enabling_an_account_that_is_not_disabled_keeps_its_sessions` (both engines); `PostgreSqlAccountStoreTests.A_sign_in_racing_a_ban_leaves_no_session_an_enable_revives`, staged with a held transaction |
| Every dead refresh token answers byte-identically | `RefreshGrantTests.Every_dead_refresh_token_answers_byte_identically` |
| A host with no applied descriptor identifies nobody | `PersonSessionsTests.Without_an_applied_descriptor_a_genuine_token_identifies_nobody` |
| No secret in a log, a problem body or a `ToString`; IdentityModel's PII switch stays off (spec §14 AC6) | `SecretsNeverLoggedTests` (logs captured at Trace with state values and scopes) |
| The JWKS publishes every verifying key and never `d`; a token validates against it with an independent handler | `DiscoveryTests.The_jwks_publishes_the_signing_key_and_no_private_part`, `…A_token_validates_against_the_published_jwks` |
| A Bearer token never reaches Management | `AccountAdministrationTests.A_bearer_token_never_reaches_the_route`; `TenantRoleManagementTests` |
| Accounts reaches no guarded core or Abstractions internal | `AccountsArchitectureTests` |

## Deviations from the standards adopted (spec §4)

The spec records D1–D9. The ones S2 builds on are listed here so that a reader can tell a decision from an oversight.
D1 (a tenant registry) and D8 (no tenant header means the personal tenant) are S3's, under
[*Tenants a person creates (S3)*](#tenants-a-person-creates-s3).

- **D2 — End users live apart from the operator store.** `MMLib.Alvo.Identity` holds the people who administer a
  project. Mixing the two populations would put every app user on the dashboard's access list. The tables and the
  package are separate, and Management never takes a Bearer token.
- **D3 — Not OpenIddict, yet.** Phase 1 issues tokens only to Alvo's own clients (`client_id: alvo`). Consent, client
  registration and `authorization_code` belong to the later OAuth slice. Tokens are minted with
  `Microsoft.IdentityModel.JsonWebTokens`, which OpenIddict itself builds on, so a later move changes the issuer, not
  the tokens.
- **D4 — Token-endpoint errors are problem documents that carry RFC 6749's `error` and `error_description`.** There
  are four slugs: `token-refused` (400), `sign-up-refused` and `sign-in-refused` (403, `error: access_denied`), and
  `rate-limited` (429).
  - `access_denied` is an authorization-endpoint code (RFC 6749 §4.1.2.1), not a §5.2 one. The spec chose it, and a
    client treats an unknown code generically.
  - A disabled account answers `sign-in-refused` with no reason. Only someone holding a valid ID token for that
    identity ever sees it, and on refresh and Bearer a disabled account looks exactly like an unknown token.
- **D5 — Roles are not in the access token.** They are read per request, which is the price of revocation on the next
  request.
- **D6 — A grace window that never forks.** A rotated token presented again within 10 s gets the same child, so the
  family keeps one tip. After the window, reuse revokes the session (RFC 9700 §4.14). The inclusive boundary and the
  zero setting (strict) are both facts. **Beyond RFC 9700**, which revokes the token family: since S4 and S5 a session
  can mint credentials that outlive it, so the reuse's transaction also revokes every app token and invitation the
  account created since the session started (stack review A-I1; [below](#the-stack-reviews-fixes)).
- **D7 — The refresh token travels in the body, not in an `httpOnly` cookie.** A cookie would make the token endpoint
  cookie-authenticated, which is the CSRF surface #191 removed. Rotation, reuse detection and a 15-minute access token
  carry the risk instead.

The S2 plan adds its own recorded deviations:

- **Two metadata URLs** for one document (D7).
- **`azp` is not checked.** Google's mobile flow carries the mobile client in `azp`; see `OidcIdTokenVerifier`.
- **Endpoint filters instead of CORS and rate-limit middleware** (D3; see `data-api.md`, *CORS and rate limits*).
- **The display name is not refreshed from later sign-ins** (D8).
- **`/logout` ends the bearer's session.** Alvo does not implement RFC 7009 revocation (D16).

## Not in S2

- Tenants, memberships and every `selfService` refusal — S3, below.
- Invitations (S4).
- Personal and service tokens — S5, [below](#tokens-for-integrators-personal-and-service-tokens-s5).
- Key rotation (D12): one key per purpose, generation 1. Scheduled and emergency rotation, and checking a Google ID
  token's `azp`: [#544](https://github.com/Burgyn/MMLib.Alvo/issues/544).
- Auth events and role-change audit (spec D9).
- A sweep of sessions and refresh tokens (spec D10). Nothing deletes a `_auth_sessions` or `_auth_refresh_tokens` row
  yet, so both tables only grow: a row per sign-in and per refresh. The sweep must keep a session's `subject_token_id`
  for the ID-token acceptance window, or single use breaks. Follow-up:
  [#527](https://github.com/Burgyn/MMLib.Alvo/issues/527).
- §3.1's global-write warning — S3, below.

## Tenants a person creates (S3)

### What you can build with it

**Level 2: everyone gets a private space.** Add one block to a level-1 descriptor and every person who signs in owns a
tenant of their own — their *personal tenant* — and every request they send acts in it. The tenant wall, not a
`created_by` rule, keeps one person's rows from another's:

```json
{
  "name": "personal-tracker",
  "auth": { "providers": ["google"], "signUp": { "mode": "open" } },
  "tenancy": { "enabled": true, "selfService": { "personalTenant": { "roles": [] } } },
  "entities": {
    "time_entries": {
      "fields": { "minutes": { "type": "integer", "required": true } },
      "rules": { "list": "'authenticated' in @user.roles", "create": "'authenticated' in @user.roles" }
    }
  }
}
```

The host code is level 1's, unchanged. The client reads the personal tenant's id once from `GET /auth/v1/user` and
sends it as the row's `tenant_id` — a scoped create always echoes its tenant (the Data API's contract); no header is
needed, because a person who names none acts in their personal tenant (spec D8):

```http
GET  /auth/v1/user            Authorization: Bearer eyJ…
→ 200 { …, "memberships": [ { "tenantId": "9f…", "name": "Ada Lovelace", "kind": "personal",
                              "roles": [], "isOwner": true } ] }

POST /api/time_entries        Authorization: Bearer eyJ…      { "minutes": 30, "tenant_id": "9f…" }   → 201
```

**Level 3: teams.** Let people create *shared* tenants, own them, and give the other members tenant roles. `jobs` is
scoped (each team sees its own), `regions` is global reference data:

```json
{
  "name": "team-tracker",
  "auth": { "providers": ["google"], "roles": ["dispatcher", "technician", "owner"], "signUp": { "mode": "open" } },
  "tenancy": {
    "enabled": true,
    "selfService": {
      "personalTenant": { "roles": [] },
      "createTenants": { "creatorRoles": ["dispatcher"], "maxPerAccount": 2 },
      "assignableRoles": ["dispatcher", "technician"]
    }
  },
  "entities": {
    "regions": { "tenancy": "global", "fields": { "code": { "type": "string", "maxLength": 8, "required": true } },
                 "rules": { "list": "'authenticated' in @user.roles", "create": "'dispatcher' in @user.roles" } },
    "jobs": { "tenancy": "scoped", "fields": { "title": { "type": "string", "maxLength": 80 } },
              "rules": { "list": "'dispatcher' in @user.roles || 'technician' in @user.roles",
                         "create": "'dispatcher' in @user.roles" } }
  }
}
```

A person creates a team, picks it with `X-Alvo-Tenant` (the Data API's tenant header), and manages who is in it:

```http
POST  /auth/v1/tenants                             { "name": "Acme" }
→ 201 Location: /auth/v1/tenants/3c…   { "tenantId": "3c…", "name": "Acme", "kind": "shared",
                                         "roles": ["dispatcher"], "isOwner": true }

POST  /api/jobs        X-Alvo-Tenant: 3c…          { "title": "Boiler service", "tenant_id": "3c…" }   → 201

GET   /auth/v1/tenants/3c…/members                                       → 200 [ { "userId", "name", "avatarUrl", "roles", "isOwner" } ]
PATCH /auth/v1/tenants/3c…/members/{userId}        { "roles": ["technician"] }   → 200, counts on their next request
DELETE /auth/v1/tenants/3c…/members/{userId}                             → 204, their next request in Acme is 401
```

A second member joins through an invitation (S4, [below](#invitations-and-invitationonly-sign-up-s4)). The S3 tests
predate that and seed the second member directly in the table (plan D15). `TenancyEngineFacts.Level_two_end_to_end` and
`…Level_three_end_to_end` are these two sections as tests, on SQLite and on PostgreSQL.

### Why it is shaped this way

- **A tenant is a membership, checked on every request, in the one read the session already costs** (spec §5.7, AC2).
  The person resolver reads the session and the membership the request names in one statement; nothing about a
  membership is cached, so a removal, a demotion or a descriptor change counts on the very next request.
- **A named tenant you are not in refuses the whole principal** (401), never a principal without a tenant. A request
  that silently fell back to "no tenant" would still reach every global entity; a 401 reaches nothing.
- **Roles are recomputed from the applied descriptor, not trusted from the row** (D5). A shared membership's roles are
  its stored names ∩ the current `assignableRoles`; a personal one's are `personalTenant.roles`, never stored. Drop a
  role from `assignableRoles` and nobody holds it on the next request.
- **A tenant role counts only in its tenant's scoped rows** (S1's path rule). Creating a tenant makes you its
  `dispatcher`, which is exactly why that role must never write `regions`, nor reach a Management `access` level — apply
  refuses an `access` rule that names an assignable role, and open sign-up warns about each global
  write it opens to `authenticated` (§3.1).
- **Owner is a flag, not a role.** `isOwner` decides who manages the membership list, and only the self-service routes
  read it. It is never minted into `@user.roles`, so a rule cannot accidentally say "owners may do X".
- **The routes live under the sign-in prefix, not the Data API's** (D1): `/api/tenants` would collide with an entity
  called `tenants`. They exist on every host that maps sign-in; the descriptor turns them on.
- **A stranger learns nothing.** A tenant you are not in answers 404, byte for byte the answer for an id that names
  nothing (GitHub's rule for private organisations).

### The two tables

Named through `AlvoFrameworkTables` (`_tenants`, `_tenant_members`), one DDL text for both engines (ids and instants
`TEXT`, booleans `INTEGER` 0/1), no foreign keys. The store is `ITenantStore` (internal to Abstractions, implemented by
`EfCoreTenantStore`); the account store writes the personal tenant inside its own sign-in and refresh transactions and
reads the membership in its caller read. Contract facts: `test/_shared/ef/TenantStoreFacts.cs`, both engines.

| Table | Holds | Notable constraint |
|---|---|---|
| `{prefix}_tenants` | `id`, `kind` (`personal`/`shared`), `name`, `created_by`, `created_at`, `personal_owner`, `version` | `personal_owner` unique: one personal tenant per account, so concurrent first sign-ins converge on one |
| `{prefix}_tenant_members` | `tenant_id`, `user_id`, `roles` (a JSON array of names), `is_owner`, `joined_at`, `invitation_id` (S4) | `(tenant_id, user_id)` primary key |

**Serialisation without dialect code** (D3). Every membership write first runs `UPDATE … SET version = version + 1` on
the tenant — a row lock on PostgreSQL, a no-op under SQLite's write gate — and only then reads the owners and judges, so
two owners demoting each other at once cannot both pass a count. Creating a tenant first touches the creator's account
row the same way, so parallel creates at the cap never pass `maxPerAccount`.

**The personal tenant** (D6) is created in the sign-in transaction (and in a refresh's, for a person who signed up
before `personalTenant` was applied), named after the account's display name at creation, else `"Personal"`.

### How a person acts in a tenant

```
X-Alvo-Tenant: <id> | (none)
   │  MembershipRules.Plan — pure, no I/O
   │    selfService off:  none → no tenant;  named → REFUSE (no read)
   │    selfService on:   named → read that membership;  none → read the personal one (if personalTenant) or no tenant
   ▼
ONE store read: session ⨝ account ⨝ (tenant_members ⨝ tenants) by the named id or by personal_owner
   ▼
MembershipRules.Decide
   found and honoured → Tenant = it; TenantRoles = stored ∩ assignableRoles (shared) or personalTenant.roles (personal)
   not found, named   → REFUSE the whole principal → 401 Bearer error="invalid_token"
   not found, unnamed → no tenant (global entities only)
```

A personal membership is honoured only while `personalTenant` is applied, and only its owner's. The same
`MembershipRules` serve `RegistryMembershipResolution`, which reads the registry for callers that have no session read
(event actors here, and personal tokens since S5 — see *How a token becomes a caller*).

### The routes

All under `{AlvoAccountsOptions.Prefix}` (default `/auth/v1`), Bearer only, `Cache-Control: no-store`, CORS like the
other auth routes. The gates run in this order, so a stranger learns nothing about the descriptor: the person
(an API key → 403 `forbidden`; none → 401 `Bearer`; malformed or ambiguous → 401 `invalid_request`; unusable → 401
`invalid_token`), then the feature (404 `feature-disabled` with `descriptorKey`), then the body (415, then 422
`validation`), then the caller's own membership (404 `not-found` when it is not one the descriptor honours), then the
registry.

| Route | Who | Answers |
|---|---|---|
| `GET /tenants` | any person | 200 the memberships the descriptor honours, personal first |
| `POST /tenants` `{name}` | any person, with `createTenants` | 201 + `Location`; 409 `tenant-limit` (`maxPerAccount`); 429 past `Alvo:Api:RateLimits:Create` |
| `PATCH /tenants/{id}` `{name}` | an owner | 200; 403 for a member; 404 for anyone else |
| `GET /tenants/{id}/members` | any member | 200 `[{userId, name, avatarUrl, roles, isOwner}]` — never an email |
| `PATCH /tenants/{id}/members/{userId}` `{roles?, isOwner?}` | an owner, on someone else | 200; 403 on yourself; 422 for a role outside `assignableRoles`; 409 `conflict` on a personal tenant |
| `DELETE /tenants/{id}/members/{userId}` | an owner; anyone on themself (leaving) | 204; 409 `last-owner`; 409 `conflict` on a personal tenant |

A name is trimmed, 1–100 code points, no control character — the display name's rules, one helper
(`SelfServiceName`).

### Invariants, and the tests that hold them

| Invariant | Held by |
|---|---|
| Levels 2 and 3 end to end, on both engines | `TenancyEngineFacts.Level_two_end_to_end`, `…Level_three_end_to_end` (`SqliteTenancyEngineTests`, `PostgresTenancyEngineTests`) |
| A named tenant that is not a membership refuses the whole principal — another's shared, another's personal, an unknown id, an unparsable value | `TenancyEngineFacts.A_header_naming_a_tenant_you_are_not_in_refuses_the_whole_principal`; `PersonResolverTests.A_header_naming_a_tenant_the_person_is_not_in_refuses_the_principal`, `…Without_self_service_a_header_still_refuses_the_principal`; `MembershipRulesTests` |
| Membership and session cost one store read; a refused tenant costs none (AC2) | `PersonResolverTests.A_request_naming_a_tenant_is_one_store_read`, `…A_refused_tenant_costs_no_read_when_self_service_is_off` |
| A membership change counts on the next request: removed → 401, demoted → role gone, role dropped from `assignableRoles` → gone (Review Focus 1) | `TenancyEngineFacts.Level_three_end_to_end`; `PersonResolverTests.A_removed_member_is_refused_on_the_next_request`, `…A_role_dropped_from_assignable_roles_is_gone_on_the_next_request`; `MemberRoutesTests.An_owner_grants_a_role_and_the_member_s_next_request_carries_it` |
| A tenant role from one tenant is absent in another; rows of one tenant are invisible in another | `TenancyEngineFacts.A_tenant_role_from_one_tenant_is_absent_in_another`, `…Rows_written_in_one_tenant_are_invisible_in_another` |
| A self-made `dispatcher` cannot write a global entity, directly or through a relation term (Review Focus 5) | `TenancyEngineFacts.A_self_made_dispatcher_cannot_write_global_regions`; `AccessTenantRoleRefusalTests`; `GlobalWriteWarningsTests` |
| A tenant role never reaches Management | `TenancyEngineFacts.A_tenant_role_never_reaches_management`; `AccessTenantRoleRefusalTests.An_assignable_role_in_any_access_level_is_refused` |
| Owner is never a role; a stored name outside `assignableRoles` is no role either | `TenancyEngineFacts.The_owner_flag_is_never_a_role`; `MembershipRulesTests.A_stored_role_no_longer_assignable_is_dropped`; `MemberRoutesTests.Members_show_display_names_and_never_an_email` |
| A tenant never loses its last owner, also when two owners act at once (Review Focus 2) | `TenantStoreFacts.Two_owners_demoting_each_other_at_once_leave_exactly_one_owner`, `…removing…`, `…leaving…` (20 iterations, both engines); `TenancyEngineFacts.The_last_owner_cannot_leave_be_removed_or_be_demoted` |
| Never more than `maxPerAccount` shared tenants, also in parallel (Review Focus 3) | `TenantStoreFacts.Concurrent_creates_at_the_cap_never_exceed_it`; `TenancyEngineFacts.Creating_tenants_stops_at_the_cap` (five at once over HTTP) |
| Two devices signing a new person in at once: one account, one personal tenant, one owner (Review Focus 4) | `AccountStoreFacts.Two_concurrent_first_sign_ins_create_one_personal_tenant` |
| Nobody changes their own membership (AC4); a personal tenant has exactly one member | `MemberRoutesTests.Nobody_patches_their_own_membership`; `TenancyEngineFacts.A_personal_tenant_takes_no_members`; `TenantStoreFacts.A_personal_tenant_refuses_every_member_change` |
| A non-member gets the unknown id's 404 on every route; a membership the descriptor does not honour is managed by no route | `MemberRoutesTests.A_non_member_gets_404_on_every_member_route`, `…An_unhonoured_personal_membership_is_managed_by_no_route`; `TenantRoutesTests.A_non_member_and_an_unknown_tenant_get_the_same_404` |
| Person only: an API key is 403, a key beside a Bearer 401; the key's own tenant and rows unchanged (AC7) | `TenantRoutesTests.An_api_key_is_403_and_no_credential_is_a_bearer_challenge`, `…A_key_beside_a_bearer_is_ambiguous_and_an_unusable_bearer_is_invalid_token`; `TenancyEngineFacts.An_api_key_beside_self_service_behaves_exactly_as_before` |
| `feature-disabled` names its key on all six routes; creating is rate-limited and only creating | `TenantRoutesTests.Without_self_service_every_route_is_404_feature_disabled`, `…Without_create_tenants_creating_is_404_naming_its_key`, `…Tenant_creation_answers_429_after_its_limit` |
| An event actor keeps only assignable tenant roles, narrowed at delivery to the membership it still holds (D10); a banned person holds none while banned (stack review B-M1) | `EventActorTests.A_recorded_tenant_role_no_longer_assignable_is_not_held`; `DeliveryRunnerActorTests`; `TenantStoreFacts.A_banned_person_s_memberships_are_not_found_until_the_account_is_enabled` (both engines; red on both without the account condition) |
| The registry is the tenant list only under `selfService` and only when the host registers none (D13) | `TenantCatalogsTests` |
| The default tenant list honours what a request honours: shared tenants, personal ones only while `personalTenant` is on — one predicate for a tenant, a membership and every listing | `TenantCatalogsTests.Without_personal_tenant_the_registry_lists_only_shared_tenants`, `…A_migration_starts_once_per_registry_tenant_only_at_a_self_service_revision`; `MembershipRulesTests.Honours_is_one_test_for_a_tenant_and_its_membership`, `…A_listing_drops_a_personal_row_that_is_not_its_owner_s` |

### Recorded deviations

From the spec, the plan's decisions (`2026-11-13-accounts-s3-tenants.md`, D1–D15) in one line each:

- **D1** — the routes live under the sign-in prefix (`/auth/v1/tenants`), not §6.3's `{prefix}`: the Data API's prefix
  would collide with an entity named `tenants`. No TeaPie path pin moves.
- **D2** — the registry is an internal port in Abstractions (`ITenantStore`), implemented in the EF adapter.
- **D3** — per-tenant and per-account serialisation by a version bump and a no-op account write, not `FOR UPDATE` or
  advisory locks, so one SQL text serves both engines.
- **D4** — one store read for account, session and membership; `IAccountStore.IsSessionLiveAsync` became
  `ReadCallerAsync`.
- **D5** — effective roles recomputed from the applied descriptor on every request; a personal membership's roles are
  never stored.
- **D6** — the personal tenant is created in the sign-in and refresh transactions; a Bearer request in the gap after
  `personalTenant` is applied (at most one access-token lifetime) has none.
- **D7** — only a person calls the routes (an API key is 403); a non-member is 404 like an unknown id; nobody changes
  their own membership.
- **D8** — three new problem types: `feature-disabled` (404, `descriptorKey`), `last-owner` (409), `tenant-limit` (409,
  `maxPerAccount`); a personal tenant's membership change is 409 `conflict`.
- **D9** — `maxPerAccount` counts the shared tenants the account *created*, for its lifetime: tenants cannot be deleted
  yet, so handing one over and leaving frees nothing.
- **D10** — an event actor keeps a recorded tenant role only while it is assignable, and a person actor's roles are
  narrowed at delivery to their current membership — never widened. A removed member's queued delivery still runs in
  the tenant, with project roles only (`events.md`). So does a banned person's: the registry read the delivery uses
  (`ITenantStore.FindMembershipAsync`) does not honour the membership of a disabled account (stack review B-M1).
- **D11** — `invitations` was accepted by the schema and warned as unhonoured in S3. S4 honours it (S4 D14).
- **D12** — the global-write warning lists each `create`/`update`/`delete` rule of a global entity whose role literals
  include `authenticated`; a rule that admits without naming a role is not listed.
- **D13** — the default tenant catalog is resolved per descriptor, not registered. On a host with a registry and no
  catalog of its own, an `EachTenant` schedule is judged per descriptor rather than in the boot's catalog-free pass, so
  a relational host with no descriptor boots and a stored-descriptor boot stands down instead of exiting (spec §6.6).
  The registry lists only the tenants the descriptor honours (`MembershipRules.Honours`, the test every membership
  passes): after `personalTenant` is dropped, personal tenants get no runs. **The cost it leaves, recorded rather than
  bounded:** under `signUp.mode: open` with `personalTenant`, every sign-up adds a tenant — and each account up to
  `maxPerAccount` more, none deletable yet — so every `EachTenant` schedule tick and every data migration on a scoped
  schema mints one more run per account, a per-tick cost any member of the public grows by signing up (bounded only by the
  token rate limit). It is a cost, not a wall breach: each run is the system in its own tenant. A host that wants it
  bounded registers its own `IAlvoTenantCatalog`, which always wins.
- **D14** — `Alvo:Api:RateLimits:Create`, 10 per client per minute, charged on `POST /tenants` only. **Superseded by
  S5 D15**: minting (`POST /tokens`) draws on the same budget (the S5 decisions, below).
- **D15** — members arrive only through invitations, which S4 added; the S3 tests seed them.

And where the implementation answered differently from the plan's first wording:

- **422, not 400, for a bad name or member patch** — `validation` is 422 everywhere in Alvo.
- **The last owner over HTTP:** only leaving reaches 409 `last-owner`. Demoting yourself is 403 (nobody changes their own
  membership, judged first), and anyone who could demote or remove the only owner is not an owner (403). Two owners
  racing: the loser is told it is no longer an owner (403) or no longer a member (404).
- **A personal tenant's `PATCH` naming another id is 409 `conflict`**, not 404: only the tenant's owner gets that far.
- **A refused `PATCH` on a global row is 404, not 403:** an `update` rule filters the rows a caller may change, the Data
  API's existing contract.
- **A refresh now commits a replay** (needed to create a missing personal tenant). Nothing else writes on that path today.

### Not in S3

- Invitations — the only way a second member joins a shared tenant (S4, below).
- Personal and service tokens, and with them `X-Alvo-Api-Key` carrying a person — S5, below.
- Deleting a tenant, and transferring a personal one.

## Invitations and `invitationOnly` sign-up (S4)

### What you can build with it

**Invite people into a team.** Add `"invitations": true` to a level-3 descriptor's `selfService` block (it needs
`createTenants`). An owner of a shared tenant creates an invitation; Alvo answers the secret code **once**; the app hands
it to the invitee however it likes — a link, a QR code, a chat message. **Alvo never sends an email.** The invitee
previews the code without signing in, signs in, and accepts it. On their very next request they act in the tenant with
the invitation's roles:

```json
"tenancy": {
  "enabled": true,
  "selfService": {
    "personalTenant": { "roles": [] },
    "createTenants": { "creatorRoles": ["dispatcher"], "maxPerAccount": 2 },
    "assignableRoles": ["dispatcher", "technician"],
    "invitations": true
  }
}
```

```http
POST /auth/v1/tenants/3c…/invitations      Authorization: Bearer <Ada>   { "roles": ["technician"] }
→ 201 { "id": "b1…", "code": "alvo_inv_Qm9…", "roles": ["technician"], "isOwner": false, "email": null,
        "maxUses": 1, "uses": 0, "expiresAt": "…+7 days", "createdBy": "…", "createdAt": "…", "status": "active",
        "revokedAt": null }

POST /auth/v1/invitations/preview           (no credential)              { "code": "alvo_inv_Qm9…" }
→ 200 { "tenantName": "Acme", "inviterName": "Ada Lovelace", "roles": ["technician"], "isOwner": false,
        "expiresAt": "…", "emailBound": false }

POST /auth/v1/invitations/accept            Authorization: Bearer <Carol> { "code": "alvo_inv_Qm9…" }
→ 200 { "tenantId": "3c…", "name": "Acme", "kind": "shared", "roles": ["technician"], "isOwner": false }

GET  /api/jobs        X-Alvo-Tenant: 3c…    Authorization: Bearer <Carol>                → 200, Acme's jobs
```

**An app nobody joins uninvited.** Set `"signUp": { "mode": "invitationOnly" }`. A person without an account can then
sign up only by sending the code with the token exchange, as the RFC 8693 extension parameter `invitation_code`. The
account, its identity, its session and the membership are written in one transaction, or nothing is:

```http
POST /auth/v1/token   application/x-www-form-urlencoded
  grant_type=urn:ietf:params:oauth:grant-type:token-exchange
  &subject_token=<Google ID token>&subject_token_type=urn:ietf:params:oauth:token-type:id_token
  &invitation_code=alvo_inv_Qm9…
→ 200 { "access_token": …, "refresh_token": … }       a member of Acme at once
→ 400 { "error": "invalid_grant", "error_description": "The invitation is not valid. Sign in without it, or ask for a new one." }
→ 403 { "error": "access_denied", "policy": "invitationOnly" }                         no code at all
```

The first owner has to come from somewhere: sign them in under `open`, then re-apply with `invitationOnly`. No route
or port creates an account for someone else, so an operator cannot seed one; an account exists only once its person
has signed in. `invitationOnly` without `tenancy.selfService.invitations` applies with a warning at
`/auth/signUp/mode` (`SignUpCheck.NobodyCanBeInvited`, stack review C-I6), since nobody new could ever sign up. The exchange takes `invitation_code` in `open` mode too, so "sign up and join" is one request
there as well. `InvitationEngineFacts.Level_three_with_invitations_end_to_end` and `…Invitation_only_end_to_end` are
these two sections as tests, on SQLite and on PostgreSQL.

### Why it is shaped this way

- **A code is a bearer secret, so it is handled like one.** It is `alvo_inv_` + 128 random bits (31 characters; the
  prefix is GitHub's secret-scanning precedent). Only its SHA-256 is stored. It is shown once, in the create answer,
  and travels only in request bodies — never in a URL, which access logs, browser history and `Referer` would keep.
- **Every dead code gets the same answer.** Unknown, malformed, expired, revoked, used up, bound to another (or an
  untrusted) email, into a personal tenant, or created by someone who is no longer an owner or is disabled: `preview` and `accept`
  answer the S3 routes' `not-found` 404, byte for byte, and the exchange answers one `invalid_grant`. A caller holding a
  code learns whether it works, and nothing about why it does not (the OWASP rule for failed secret lookups).
- **The use count is decided by one conditional `UPDATE`, never a read followed by a write.** Two people racing for a
  single-use code make exactly one member; the loser's `UPDATE` finds no row and answers dead.
- **An invitation is only as good as its creator's ownership — and their account.** Demoting, removing or losing an
  owner revokes their live invitations in the same transaction, and creating an invitation is serialised against that
  change. Disabling an account revokes every live invitation it created, in every tenant, in the ban's own transaction
  (D16); enabling it again revives none.
- **A preview needs no account.** Under `invitationOnly` the invitee has none yet, and the app wants to show "Ada
  invites you to Acme" before sign-in. So preview reads no credential; it cannot judge an email binding, so it says only
  whether one exists.
- **Signing up by invitation is all or nothing.** A dead code refuses the whole exchange — even for an existing account
  that would sign in without it, unless that account already is a member of the code's tenant, which signs in whatever
  the code's state (step 3 below). The client asked for both at once, and it can always sign in again without the code.

### The table

`{prefix}_tenant_invitations`, named through `AlvoFrameworkTables.TenantInvitationsSuffix`, one DDL text for both
engines, no foreign keys. The SQL is in `Internal/InvitationTables.cs`; the store members are on `ITenantStore`
(`EfCoreTenantStore.Invitations.cs`). Contract facts: `test/_shared/ef/InvitationStoreFacts.cs`, both engines.

| Column | Holds |
|---|---|
| `id`, `tenant_id` | the invitation and the shared tenant it invites into |
| `code_hash` | lowercase hex SHA-256 of the code, `UNIQUE` |
| `roles`, `grants_owner` | what accepting gives: role names (a JSON array, each in `assignableRoles` at create) and the owner flag |
| `email` | optional; lower-cased, matched only against a **trusted** email (spec §5.3) |
| `max_uses`, `uses` | default 1, at most 100; `uses` only ever grows through the conditional `UPDATE` |
| `expires_at`, `created_by`, `created_at`, `revoked_at` | default 7 days, at most 30; the creating owner; for the newest-first list; set by a revocation or by the ownership sweep |

An accepted membership records the invitation in `_tenant_members.invitation_id`; a creator's or a personal owner's is
`NULL`.

### The acceptance routine

One routine, `Internal/InvitationAcceptance.cs`, runs inside two transactions: the `accept` route's, and the sign-in's
when the exchange carries `invitation_code`. In order:

1. **Lock the account** (the S3 version-bump idiom). Concurrent accepts by one person queue here. No account →
   `NoAccount`.
2. **Find the invitation's tenant** by `code_hash`, in any state. No row → `Dead`.
3. **Already a member of that tenant?** → `AlreadyMember`, with roles unchanged and no use consumed, however dead the
   code is. A double-submitted accept therefore answers 200 twice.
4. **Read the invitation live**: not revoked, not expired, uses left, the tenant `shared`, its creator still an owner
   and not disabled.
   Otherwise → `Dead`.
5. **Check the email binding** against the account's email, only if trusted, ignoring case. Otherwise → `Dead`.
6. **Consume one use** with `UPDATE … SET uses = uses + 1 WHERE id = @id AND uses < max_uses AND revoked_at IS NULL AND
   expires_at > @now`. Zero rows → `Dead`, unless a re-read finds the person already a member (`AlreadyMember`) — defence
   in depth, since step 1's lock already serialises one person's accepts. Otherwise insert the membership with the invitation's roles, owner flag and
   id → `Joined`.

On PostgreSQL the loser of a race waits on the row lock of step 6 and re-evaluates the predicate after the winner
commits. On SQLite the write gate serialises the two outright. Steps 2 and 3 run before the liveness read so that a
member is never told their own tenant's invitation is dead. This makes an existing-but-dead code cost two more indexed
reads than an unknown one. Only a holder of the 128-bit code can observe that, so it is a timing difference, not an
oracle. Do not "optimise" the reads into a different answer.

### The routes

All under `{AlvoAccountsOptions.Prefix}` (default `/auth/v1`), `Cache-Control: no-store`, CORS like the other auth
routes. All five are charged the `Invitations` budget (`Alvo:Api:RateLimits:Invitations`, 20 per client per minute),
after CORS and no-store and before any other gate. While `tenancy.selfService.invitations` is off, every one answers 404
`feature-disabled` naming that key (or `tenancy.selfService`, when the whole block is off).

| Route | Who | Answers |
|---|---|---|
| `POST /tenants/{id}/invitations` `{roles, isOwner?, email?, maxUses?, expiresAt?}` | an owner | 201 with `code`, the only answer that ever carries it, and no `Location`; 422 at the offending pointer; 409 `conflict` on a personal tenant |
| `GET /tenants/{id}/invitations` | an owner | 200, newest first, never a `code`; `status` is `revoked` > `usedUp` > `expired` > `active` |
| `DELETE /tenants/{id}/invitations/{invitationId}` | any owner of the tenant | 204, also the second time (the first `revoked_at` is kept); 404 for another tenant's id |
| `POST /invitations/preview` `{code}` | anyone, no credential read | 200 `{tenantName, inviterName, roles, isOwner, expiresAt, emailBound}`; the dead 404 |
| `POST /invitations/accept` `{code}` | a person (an API key → 403; none → 401 `Bearer`) | 200 the person's membership, for `Joined` and `AlreadyMember` alike; the dead 404 |
| `POST /token` with `invitation_code` | the exchange | 200; 400 `invalid_grant` for a dead code; 400 `invalid_request` while invitations are off |

For the owner routes, a member who is not an owner gets 403, and a non-member or an unknown tenant gets the same 404.
**Create refuses rather than clamps.** `roles` is required, unique and within `assignableRoles` (`admin` never is). An
owner-granting invitation must be single-use and live at most 7 days, or it is refused at `/maxUses` or `/expiresAt`.
`expiresAt` is RFC 3339 with an offset, later than now and at most 30 days ahead. `email` is trimmed, at most 254
characters, and has exactly one `@`. No refusal sentence repeats what was sent.

**In the exchange, the order is the point.** The form is parsed first: `invitation_code` sent twice is the S2 malformed
form. Next the subject token is verified, so nobody can probe codes anonymously, and a refused ID token never consumes
one. A code sent while invitations are off is refused, not silently dropped. Only then does the sign-up decision run,
knowing a code was sent (`SignUpDecision.Decide(policy, identity, hasInvitation)`): `invitationOnly` with a code is
judged as `open` would be, `allowedEmailDomains` included; without one it refuses as in S2. The store receives only
the code's hash.

### Invariants, and the tests that hold them

| Invariant | Held by |
|---|---|
| Level 3 with invitations, and an `invitationOnly` app, end to end on both engines | `InvitationEngineFacts.Level_three_with_invitations_end_to_end`, `…Invitation_only_end_to_end` (`SqliteInvitationEngineTests`, `PostgresInvitationEngineTests`) |
| Two people accepting one single-use code at once make one member; two strangers signing up with it make one account (Review Focus 1) | `InvitationStoreFacts.Two_people_accepting_a_single_use_invitation_at_once_make_one_member`, `…Five_people_accepting_a_three_use_invitation_at_once_make_three_members`; `AccountStoreFacts.Two_new_identities_racing_for_one_single_use_invitation_create_one_account` (20 iterations, both engines); `InvitationEngineFacts.Two_people_accepting_…`, `…Two_strangers_signing_up_with_one_single_use_code_at_once_make_one_account` (over HTTP, 10 iterations, both engines) |
| An owner's invitations die with their ownership, in the same transaction, and a create racing the demotion never outlives it (Review Focus 2) | `InvitationStoreFacts.Demoting_an_owner_revokes_…`, `…Removing_an_owner_or_an_owner_leaving_revokes_their_invitations`, `…An_invitation_created_while_its_creator_is_demoted_never_outlives_the_demotion`, `…An_accept_racing_its_creator_s_demotion_never_joins_after_the_sweep`, `…An_accept_that_waited_on_a_revocation_finds_the_invitation_revoked`; `InvitationEngineFacts.A_demoted_owner_s_invitations_are_dead_on_the_next_request` |
| A disabled owner's invitations are dead and stay dead after enabling (D16) | `InvitationStoreFacts.Disabling_an_owner_revokes_their_invitations_and_enabling_revives_none`, `…Every_dead_invitation_previews_and_accepts_as_dead` (`creator disabled`), both engines |
| One person double-submitting accept: one membership, both 200, one use (Review Focus 3) | `InvitationStoreFacts.One_person_accepting_twice_at_once_joins_once` (`maxUses` 1 and 3), `…Accepting_a_single_use_invitation_again_is_already_a_member`; `InvitationAcceptTests.Accepting_again_is_200_with_the_roles_unchanged` |
| A dead code on a sign-up writes nothing — no account, identity, session, refresh token, personal tenant or membership (Review Focus 4) | `AccountStoreFacts.A_dead_invitation_creates_nothing` (six table counts); `InvitationSignUpTests.A_dead_code_refuses_and_creates_nothing` |
| Every dead reason, one answer: a byte-identical 404 on preview and accept, one `invalid_grant` on the exchange (Review Focus 5) | `InvitationStoreFacts.Every_dead_invitation_previews_and_accepts_as_dead`; `InvitationAcceptTests.Every_dead_reason_answers_one_byte_identical_404`; `InvitationSignUpTests.A_dead_code_refuses_and_creates_nothing` |
| A personal tenant is never invited into, also when a row says so | `InvitationStoreFacts.A_personal_tenant_takes_no_invitation`, `…An_invitation_into_a_personal_tenant_is_dead_even_when_its_row_exists`; `InvitationRoutesTests.A_personal_tenant_takes_no_invitation` |
| An email-bound invitation admits only that address, only when trusted, in any case | `InvitationStoreFacts.An_email_bound_invitation_admits_only_that_trusted_email`; `InvitationAcceptTests.An_email_bound_invitation_admits_its_trusted_address_in_any_case`; `InvitationSignUpTests.An_email_bound_invitation_signs_up_only_its_trusted_address` |
| An invitation grants only assignable roles, never `admin`; a role dropped from `assignableRoles` is gone on the next request (AC4) | `InvitationRoutesTests.A_role_outside_assignable_roles_is_refused_and_nothing_is_created`; `InvitationRequestTests`; `InvitationEngineFacts.An_invitation_grants_only_assignable_roles_and_never_admin` |
| An invited tenant role never reaches a global entity or management | `InvitationEngineFacts.An_invited_tenant_role_never_reaches_a_global_entity_or_management` |
| Only owners invite, list and revoke; a non-member's 404 equals an unknown tenant's; accept needs a person | `InvitationRoutesTests.A_member_who_is_not_an_owner_cannot_invite_list_or_revoke`, `…A_non_member_and_an_unknown_tenant_get_the_same_404`, `…An_api_key_is_403_and_no_credential_is_a_bearer_challenge`; `InvitationAcceptTests.Accept_needs_a_person` |
| `invitationOnly` creates an account only with a live code, verified after the subject token; domains still apply | `SignUpDecisionTests`; `InvitationSignUpTests.Without_a_code_a_new_person_is_refused_as_before`, `…A_code_from_a_refused_subject_token_is_never_consumed`, `…Allowed_email_domains_refuse_an_invited_outsider`, `…An_existing_person_with_a_dead_code_is_refused` |
| The code is 128 bits, stored as its hash, shown once, never in a URL, a log, a `ToString` or a refusal (AC6) | `InvitationCodeTests`; `InvitationRoutesTests.An_owner_invites_and_the_code_is_answered_once`; `AccountsOpenApiTests.No_invitation_route_takes_the_code_in_its_url`; `SecretsNeverLoggedTests.No_invitation_code_appears_in_any_log` (the code whole, its random part and its hash, at Trace), `…No_error_body_echoes_a_presented_token` |
| `feature-disabled` names `tenancy.selfService.invitations` on all five routes; a code on the exchange while off is `invalid_request`; all five are rate-limited | `InvitationRoutesTests.Without_invitations_every_route_is_404_naming_its_key`, `…Invitation_routes_answer_429_after_their_limit`; `InvitationAcceptTests.Without_invitations_preview_and_accept_are_404_naming_its_key`, `…Preview_and_accept_are_charged_the_invitations_budget`; `InvitationSignUpTests.A_code_while_invitations_are_off_is_invalid_request` |

### Recorded decisions and deviations

From the plan (`2026-11-13-accounts-s4-invitations.md`, D1–D15), one line each:

- **D1** — the routes live under the sign-in prefix (`/auth/v1/tenants/{id}/invitations`, `/auth/v1/invitations/…`),
  not §6.3's `{prefix}`, as in S3. No TeaPie path pin moves.
- **D2** — the code is `alvo_inv_` + 128 bits of base64url, stored as unsalted SHA-256. A salt adds nothing to a
  secret with 128 bits of entropy. Any presented string is hashed and looked up, so a malformed code is just not found.
- **D3** — one dead answer: the S3 `not-found` 404 on preview and accept, one `invalid_grant` with a fixed sentence on
  the exchange. No new problem slug.
- **D4** — one acceptance routine shared by the accept route and the sign-in transaction. The use count is decided by
  the conditional `UPDATE`. **Deviation from the plan's order:** the "already a member" check runs before the
  liveness read (steps 2–3 above), so a member presenting any code of their own tenant — dead or alive — is answered
  their membership and nothing is consumed. Without it, a double-submitted single-use accept answered 200 then 404,
  against Review Focus 3. A non-member still gets the one dead answer.
- **D5** — the exchange accepts `invitation_code` in any mode, in the order above. `allowedEmailDomains` now also
  applies under `invitationOnly`. §3.1 says who "may sign up" without naming a mode, and nobody was admitted under
  `invitationOnly` before, so nothing regresses.
- **D6** — an owner's invitations are revoked in the demotion's or removal's own transaction, and create and revoke go
  through S3's version bump, so a create racing its creator's demotion is serialised. The liveness read also requires the
  creator to still be an owner.
- **D7** — a dead code refuses the whole exchange, even for an account that would sign in without it — except an
  account already a member of the code's tenant, which signs in whatever the code's state.
- **D8** — preview is anonymous and says only `emailBound`. **Deviation from spec §6.4:** "email-bound to someone else
  → the same 404" holds for accept and the exchange, not for an anonymous preview, which has no "someone" to compare.
- **D9** — owner rules on create, refused never clamped: `maxUses` 1–100 (the spec gives only the default), `expiresAt`
  at most 30 days, and an owner-granting invitation single-use and at most 7 days.
- **D10** — who may call: owners create, list and revoke; any person accepts; anyone previews.
- **D11** — create answers 201 with the code and no `Location` (there is no single-invitation `GET`). **Deviation:** the
  201 also carries `revokedAt: null`, so a client parses one shape for created and listed invitations.
- **D12** — `created_at` is a column the spec's model does not list. It orders the list.
- **D13** — `Alvo:Api:RateLimits:Invitations`, 20 per client per minute, on all five routes. The exchange with a code
  stays on the `Token` budget.
- **D14** — `tenancy.selfService.invitations` is honoured. It left the unhonoured list, the dashboard's warned-slot
  mirror and the `alvo-descriptor-capabilities-and-limits` skill's generated region.
- **D15** — an accepted membership records `invitation_id`. No membership event is emitted (spec D9 defers them).
- **D16** — a disabled owner's invitations are dead. **Beyond spec §6.4**, which ties invitations to ownership only: a
  ban (spec §5.6) that left the banned owner's codes live would keep admitting people — under `invitationOnly`, creating
  accounts — for up to 30 days. Disabling an account revokes every live invitation it created, in every tenant, in the
  ban's transaction, so the consume's `revoked_at IS NULL` refuses an accept that read the code live before the ban
  committed; the liveness read also requires the creator's account not to be disabled (the second line, as in D6).
  Enabling the account revives none — the revocation is a fact, like D6's sweep, and the owner issues new codes.

And where the implementation answered differently from the plan's first wording, or a test had to look further:

- **An unknown member of a code body is refused at its own pointer** (`{"code":"…","y":1}` → 422 at `/y`), as on every
  other body.
- **Create, list and revoke read the caller's honoured membership before the store** (S3's precedent), so an unhonoured
  personal membership gets 404, not the store's 409.
- **Dropping the ownership sweep alone leaves preview and accept dead** — the liveness read's owner check is the second
  line — so the demotion fact also checks that the invitation is listed as `revoked`.

### Not in S4

- Personal and service tokens, and with them `X-Alvo-Api-Key` carrying a person — S5, below.
- Membership events — spec D9 defers them.
- Invitation emails. Alvo never sends one; the app shares the code however it likes.
- Deleting a tenant, and transferring a personal one.
- Recovering a tenant whose only owner is banned. A ban leaves memberships, and no operator route grants ownership, so
  nobody can manage it until the account is enabled again; and the lock order of an owner's removal racing their ban:
  [#543](https://github.com/Burgyn/MMLib.Alvo/issues/543).

## Tokens for integrators: personal and service tokens (S5)

### What you can build with it

**A script that works as you.** Ada dispatches for Acme on the level-3 backend above. She wants a nightly export, a CLI
or an automation step to read and create Acme's jobs as her, without a browser and without handing over her Google
account. She mints a **personal token** for Acme. The script sends it as `X-Alvo-Api-Key`, exactly as it would send an
API key, and every request it makes is Ada in Acme: `created_by` is Ada, and the token holds the tenant roles Ada holds
in Acme *at that moment*. Demote her and the script loses what she lost, on its next request. Remove her from Acme and
the token is dead for good; inviting her back does not revive it.

**A billing system that works as the team.** Acme's billing system creates jobs as Acme, not as whoever set it up. An
owner mints a **service token**. It acts in Acme with the tenant roles it was given — only roles its minter holds there
— and the rows it writes carry the token's own id in `created_by`. It keeps working while members come and go, until
its minter stops being an owner of Acme. Then it dies with that ownership, and a remaining owner mints the replacement:
a token that outlives the person who chose its power is exactly the case spec §7.1 rules out.

Both are switched on by one block in the level-3 descriptor's `auth`. The examples below also read `created_by`, which
only an audited entity has, so `jobs` gains `"audit": true` — leave it out and everything works, without that column:

```json
"auth": {
  "providers": ["google"],
  "roles": ["dispatcher", "technician", "owner"],
  "signUp": { "mode": "open" },
  "accessTokens": { "personal": true, "service": true, "maxLifetimeDays": 30 }
},
…
"jobs": { "tenancy": "scoped", "audit": true, … }
```

Every token expires, and `maxLifetimeDays` (1–365, default 90) is the furthest ahead its `expiresAt` may be. A service
token needs a shared tenant to act as, so **apply refuses `"service": true` without `tenancy.selfService.createTenants`**,
at `/auth/accessTokens/service` (D8). `personal` needs nothing else. At level 1 (`time-tracker` with
`"accessTokens": { "personal": true }`) a personal token minted without a tenant acts as its person in no tenant, and
`created_by == @user.id` keeps it to that person's rows, as it keeps their session.

Ada mints with her session. `$ADA` is her access token, and `3c…` is Acme's id from `GET /auth/v1/tenants`.
`expiresAt` must be later than now and at most `maxLifetimeDays` ahead, so the example computes one a week out:

```sh
EXPIRES=$(date -u -d '+7 days' +%Y-%m-%dT%H:%M:%SZ)   # GNU date; on macOS: date -u -v+7d +%Y-%m-%dT%H:%M:%SZ

curl -X POST https://api.example.com/auth/v1/tokens \
  -H "Authorization: Bearer $ADA" -H "Content-Type: application/json" \
  -d '{ "name": "nightly export", "kind": "personal", "tenantId": "3c…",
        "scopes": ["jobs:read", "jobs:write"], "expiresAt": "'"$EXPIRES"'" }'
# 201 { "keyId": "alvo_pat_q8…", "token": "alvo_pat_q8….Zp…", "header": "X-Alvo-Api-Key",
#       "kind": "personal", "name": "nightly export", "tenantId": "3c…", "roles": [],
#       "scopes": ["jobs:read", "jobs:write"], "expiresAt": "…", "createdAt": "…" }
```

`token` is in this answer and in no other, ever: store it in the script's secret store now — below it is `$TOKEN`.
`header` names the header to send it in (`Alvo:Auth:HeaderName`). The tenant is fixed at mint, so the script sends no
`X-Alvo-Tenant`; a scoped create still echoes its `tenant_id`, as every scoped write does:

```sh
curl https://api.example.com/api/jobs -H "X-Alvo-Api-Key: $TOKEN"
# 200, Acme's jobs

curl -X POST https://api.example.com/api/jobs \
  -H "X-Alvo-Api-Key: $TOKEN" -H "Content-Type: application/json" \
  -d '{ "title": "Boiler service", "tenant_id": "3c…" }'
# 201, created_by = Ada
```

An owner mints the billing system's token the same way, with `"kind": "service"` and the roles it acts with:

```sh
curl -X POST https://api.example.com/auth/v1/tokens \
  -H "Authorization: Bearer $ADA" -H "Content-Type: application/json" \
  -d '{ "name": "billing", "kind": "service", "tenantId": "3c…", "roles": ["dispatcher"],
        "scopes": ["jobs:read", "jobs:write"], "expiresAt": "'"$EXPIRES"'" }'
# 201 { "keyId": "alvo_svc_7f…", "token": "alvo_svc_7f….Lw…", …, "roles": ["dispatcher"], … }
```

The list shows what each token is, who answers for it and when it was last used — never its secret. Revoking takes the
key id, which is not secret:

```sh
curl https://api.example.com/auth/v1/tokens -H "Authorization: Bearer $ADA"
# 200 [ { "keyId": "alvo_svc_7f…", "kind": "service", "name": "billing", "tenantId": "3c…",
#         "roles": ["dispatcher"], "scopes": ["jobs:read", "jobs:write"],
#         "mintedBy": { "id": "…", "displayName": "Ada Lovelace" }, "createdAt": "…", "expiresAt": "…",
#         "lastUsedAt": "…", "revokedAt": null, "status": "active" }, … ]

curl -X DELETE https://api.example.com/auth/v1/tokens/alvo_svc_7f… -H "Authorization: Bearer $ADA"
# 204; the token's next request is 401
```

`AppTokenEngineFacts.Personal_token_end_to_end`, `…Service_token_end_to_end` and `…Level_one_personal_token` are this
section as tests, on SQLite and on PostgreSQL.

### Why it is shaped this way

- **A token is an API key with a name you can scan for.** It travels on the existing `X-Alvo-Api-Key: keyId.secret`
  channel, so a client, the scope gate and the rules handle it as they handle any API key, and Bearer stays a person's
  session. The key id starts `alvo_pat_` (personal) or `alvo_svc_` (service), GitHub's secret-scanning precedent: a
  leaked token is recognisable in a log, a paste or a commit. Both prefixes are reserved — a configured dev key may not
  use them — so "has the prefix" and "is a persisted app token" are the same statement everywhere (D3).
- **A personal token is its person, not a snapshot of them.** Its tenant is fixed at mint — one tenant, or none — and
  it stores no roles at all. Each request reads the person's current membership through S3's `MembershipRules`, the
  same honoured-membership predicate a session passes, so a demotion, a removal or a descriptor change counts on the
  next request (spec §14 AC3). A tenantless token reads no membership and never falls back to the personal tenant.
- **A service token is a member nobody signs in as.** It has its own `UserId`, so rows show that the integration wrote
  them, and fixed tenant roles: at mint a subset of what its minter holds in that tenant, on each request what is left
  of them in the current `assignableRoles`. They are tenant roles, never project roles, so they count only on the
  tenant's scoped rows (spec §6.2). Ownership is never a role and is never carried (spec §16 B4).
- **A token dies with its reason, in the same transaction.** Removing a member, or a member leaving, revokes their
  personal tokens for that tenant and the service tokens they minted for it. Demoting an owner revokes the service
  tokens they minted. Disabling an account revokes every token it minted. Each sweep runs inside the write that kills
  the reason, and minting is serialised against those writes with S3's account-row and tenant-version idiom, so a mint
  racing a removal either sees it or is swept by it. Death is a `revoked_at` stamp, not only a live check: a person
  invited back does not get their old tokens back, and enabling a banned account revives none.
- **The secret is shown once and stored as a hash.** 256 random bits; the registry stores only its SHA-256, unsalted —
  a salt adds nothing to a 256-bit random secret (S4 D2's reasoning). It is in the 201 and in no list, refusal, log,
  `ToString` or URL.
- **A token is an app credential, never a manager.** It cannot mint, list or revoke a token or call any self-service
  route — 403, those are a person's acts — and the Management API refuses it with 403 even when its roles would pass an
  `access` level (D9, which closes S1 D8).
- **The descriptor decides on every request** (D11). Remove `accessTokens` or switch one kind off, and the next request
  of every token of that kind is 401. Remove `selfService` — apply then needs `service` off too (D8) — and so is every
  service token's and every personal token's in a tenant, since no membership counts any more, while a tenantless
  personal token keeps working. Switch it back on and
  every unexpired, unrevoked token works again: a descriptor decision, as S3 made it for memberships.
- **Scopes are entity × read/write, and nothing wider.** At least one, each naming an entity the applied descriptor
  declares. `*` is refused, because a wildcard would quietly grant every entity added after the mint (the GitHub
  fine-grained token precedent); a person who wants everything uses their session.
- **Scopes bind where a route names an entity, and a token goes nowhere else.** The generated Data API (batch and
  embeds included) checks every operation against them, and a mapped command checks its own entity: a bound command
  needs `entity:read` and `entity:write`, and an unbound one needs `*:write`, which no token can hold, so a token never
  calls an unbound command. Once admitted, a command's handler acts with the command's own authority — its writes,
  to its entity or any other, are judged by the descriptor's rules as the token's caller, unscoped, exactly as a
  configured key's are (`commands.md`). A host's own `.WithAlvoCaller()` endpoint names no entity a scope could be
  checked against, so it **refuses** a personal or service token with 403 `forbidden` and a fixed sentence, after
  resolving it (an unusable token is still 401); configured keys, Bearer people and the anonymous caller reach it as
  before. Letting such an endpoint name the scope it needs is
  [#536](https://github.com/Burgyn/MMLib.Alvo/issues/536).

### The table

`{prefix}_api_keys`, named through `AlvoFrameworkTables.ApiKeysSuffix`, one DDL text for both engines (ids and instants
`TEXT`, roles and scopes JSON arrays), no foreign keys, indexed on `minted_by` and `tenant_id`. The SQL is in
`Internal/AppTokenTables.cs`; the store is `IAppTokenStore`, internal to Abstractions and implemented by
`EfCoreAppTokenStore`. Contract facts: `test/_shared/ef/AppTokenStoreFacts.cs`, both engines.

| Column | Holds |
|---|---|
| `key_id` | the primary key: the prefix + 22 base64url characters (128 random bits). Not secret: it is listed, and it is the revoke route's path segment |
| `kind`, `secret_hash` | `personal` or `service`; the base64 SHA-256 of the secret part, the form every `ApiKeyRecord.Sha256Hash` holds |
| `name`, `scopes` | what the person called it; its `entity:read` / `entity:write` scopes |
| `user_id`, `minted_by` | who it acts as — the person (personal) or a fresh id (service) — and the account that minted it |
| `tenant_id`, `roles` | the tenant it acts in, `NULL` for a tenantless personal token; a service token's tenant roles, `[]` for a personal one |
| `created_at`, `expires_at` | when it was minted, which orders the list newest first; when it stops working, always set |
| `last_used_at`, `revoked_at` | minute-grained (below); set by a revocation or a sweep, the first stamp kept |

A row reads back as an `ApiKeyRecord` with the two members S5 adds: `Kind` (`Configured` for every configured or
host-built key, so they keep their meaning) and `MintedBy`. A row whose stored scope no longer parses reads back as no
record at all, never as a token with fewer scopes.

### How a token becomes a caller

```
X-Alvo-Api-Key: alvo_pat_…|alvo_svc_….<secret>        X-Alvo-Tenant: optional, and it only confirms
   │  CallerResolution.Read — the one credential reading every API key goes through
   ▼
ApiKeyContextResolver (D5)
   1. split at the first '.' → key id, secret
   2. ApiKeyStores.FindAsync — the prefix routes: alvo_pat_/alvo_svc_ → the registry (one read); else → the dev keys
   3. the secret, compared in constant time (an unknown id still compares against a dummy hash)
   4. usable: not revoked, not expired
   5. TenantResolver: the token's own tenant; a header naming another refuses
   6. AppTokenPrincipals: its kind is on in the applied descriptor (a service token also needs selfService), then
        personal, no tenant → the person, in no tenant, no read
        personal, a tenant  → IMembershipResolution (S3's rules, one read): refused → REFUSE; else its roles, live
        service             → its own user id; stored roles ∩ the current assignableRoles
   7. last_used_at — written at most once a minute per token per replica, best effort
   ▼
AlvoPrincipal: User = the person | the token's own id, Roles = {authenticated}, Tenant, TenantRoles,
               ActorKind = User (personal) | ApiKey (service), Scopes and KeyId = the token's
```

The tenant is resolved before the kind is judged (`ApiKeyContextResolver.ResolveAppTokenAsync`, then
`AppTokenPrincipals.ResolveAsync`); both refuse with the same 401, so the order is not observable, only recorded.
Every refusal is the API key's ordinary 401, so a wrong secret, an expired token, a switched-off kind and an ended
membership look alike. The principal then meets its surface: the Data API and a command check its scopes (403
`out-of-scope`); the Management API and a host's `.WithAlvoCaller()` endpoint refuse it outright (403 `forbidden`).
A service token or a tenantless personal token costs one read; **a personal token in a tenant
costs two**, the token row and the membership. Spec §14 AC2's one round trip is about sessions — recorded here, not
hidden. A failed `last_used_at` write is logged as a warning naming the key id, never the secret, and the request goes
on.

**A personal token is a `User` actor, a service token an `ApiKey` one** (D6). An event a personal token causes reports
`authtype user` and the person as `authid`, so S3 D10's delivery narrowing applies to it unchanged: a queued delivery of
a person removed or banned since loses their tenant roles, whether they acted by session or by token. A service token's queued
deliveries keep the tenant roles recorded at the event, bounded by `assignableRoles`, as any API key's do.

### The routes

All under `{AlvoAccountsOptions.Prefix}` (default `/auth/v1`), Bearer only, `Cache-Control: no-store`, CORS like the
other auth routes. The gates run in this order: the `Create` budget (`POST` only); the person (any `X-Alvo-Api-Key`, a
token included → 403 `forbidden`; none → 401 `Bearer`; a key beside a Bearer → 401 ambiguous); `auth.accessTokens`
(404 `feature-disabled`); then, for `POST`, whether this host would ever resolve a token (the same 404, with a sentence
naming a replaced `IApiKeyStore`), the JSON body (415, then 422), the kind's own switch (404
`feature-disabled` naming `auth.accessTokens.personal` or `.service`), the person's membership of the named tenant, and
the registry.

| Route | Who | Answers |
|---|---|---|
| `POST /tokens` `{name, kind, tenantId?, roles?, scopes, expiresAt}` | personal: any person; service: an owner of a shared tenant | 201 with `token`, the only answer that carries it, and no `Location`; 404 `feature-disabled` on a host that replaced the `IApiKeyStore`; 403 for a member who is not an owner; 404 for a tenant the person is not in, byte for byte an unknown tenant's; 409 `conflict` for a personal tenant or past 100 live tokens; 422 at the offending pointer; 429 past `Alvo:Api:RateLimits:Create` |
| `GET /tokens` | any person | 200, newest first: their personal tokens and the service tokens of the shared tenants they own; `status` is `revoked` > `expired` > `active`; never `token`, never a hash |
| `DELETE /tokens/{keyId}` | a personal token: its minter; a service token: its minter or any owner of its tenant | 204, also the second time (the first `revokedAt` is kept); one identical 404 for an unknown id, another person's personal token and a service token of a tenant the caller does not own |

**The body is refused, never clamped.** `name` is 1–100 characters under the display name's rules (`SelfServiceName`).
`kind` is `personal` or `service`. `tenantId` is a UUID — optional for a personal token, required for a service one.
`roles` must be absent on a personal token (its roles are its person's); a service token names one or more, unique,
each in `assignableRoles` and each held by the minter in that tenant ("You can only give a token roles you hold in this
tenant", at `/roles`). `scopes` are one or more unique `entity:read` / `entity:write`, each entity declared — the
refusal lists the descriptor's entities and repeats nothing that was sent. `expiresAt` is RFC 3339 with an offset,
later than now and at most `maxLifetimeDays` ahead. An unknown member is refused at its own pointer.

**Minting is charged the `Create` budget** (`Alvo:Api:RateLimits:Create`, default 10 per client per minute) — the one
`POST /tenants` draws on, so the two share it (D15). Listing and revoking are not charged.

Three behaviours, recorded so they read as decisions:

- **A caller already at the live cap gets 409 before any tenant check.** The registry counts the minter's live tokens
  under the account lock first, so a person holding 100 who asks for a service token in a stranger's tenant is told
  409, not 404. The answer reveals only their own state.
- **A service token the descriptor no longer honours is invisible to the token routes.** While `selfService` is off no
  shared membership counts, so the list drops service tokens, and revoking one answers the unknown id's 404 without
  touching the registry. Both use one predicate (`TokenRoutes.Honoured`, over S3's `MembershipRules.Honours`). Turn
  `selfService` back on and the token is listed — and resolves — again.
- **The key id is matched by prefix, ordinally, and `key_id` is compared case-sensitively.** `AppTokenSecret.KindOf`
  reads the kind from the prefix with an ordinal comparison, and the revoke's `key_id = @key_id` is case-sensitive on
  SQLite and PostgreSQL, so `ALVO_SVC_…` names no row. A third engine whose collation compares `key_id`
  case-insensitively must keep this column case-sensitive: an upper-cased id is classified as a configured key, skips
  the honoured check, and would otherwise find the row.

### Invariants, and the tests that hold them

| Invariant | Held by |
|---|---|
| Personal and service tokens end to end, and a tenantless personal token at level 1, on both engines | `AppTokenEngineFacts.Personal_token_end_to_end`, `…Service_token_end_to_end`, `…Level_one_personal_token` (`SqliteAppTokenEngineTests`, `PostgresAppTokenEngineTests`) |
| A personal token's roles are its person's current ones: demoted → the lost role is gone on the next request; removed → 401; invited back → the old token is still 401 (Review Focus 1) | `AppTokenEngineFacts.Personal_token_end_to_end` (red on both engines without the removal sweep); `AppTokenResolutionTests.A_personal_token_acts_as_its_person_with_live_tenant_roles`, `…A_personal_token_whose_membership_ended_is_refused`; `AppTokenStoreFacts.A_rejoined_member_s_old_personal_token_stays_revoked` |
| A token dies with its reason, in that transaction: removal and leaving, an owner's demotion, a ban, a refresh-token reuse ending a session that was open when it was minted; a role change and a refused write revoke nothing; enabling revives nothing | `AppTokenStoreFacts.Removing_a_member_revokes_their_personal_tokens_for_that_tenant_only`, `…A_member_leaving_revokes_the_same`, `…Removing_an_owner_revokes_the_service_tokens_they_minted_and_no_one_else_s`, `…Demoting_an_owner_revokes_their_service_tokens_and_leaves_their_personal_tokens`, `…Changing_a_member_s_roles_revokes_nothing`, `…A_refused_demotion_revokes_nothing`, `…Disabling_an_account_revokes_every_token_it_minted_and_enabling_revives_none`; `AccountStoreFacts.Disabling_revokes_sessions_and_tokens_together`, `…A_reuse_revokes_the_tokens_and_invitations_created_since_its_session_started`; `AppTokenEngineFacts.Service_token_end_to_end`, `…A_banned_person_s_tokens_die_and_stay_dead` |
| A mint racing its minter's demotion, removal or ban — or a reuse of its session (stack review A-I1) — never outlives it (Review Focus 2) | `AppTokenStoreFacts.A_service_mint_racing_its_minter_s_demotion_never_outlives_it`, `…A_personal_mint_racing_the_person_s_removal_never_outlives_it`, `…A_mint_racing_a_ban_never_outlives_it` (20 iterations, both engines; red on PostgreSQL without the tenant bump); `AccountStoreFacts.A_mint_racing_a_reuse_of_its_session_never_outlives_it` |
| Never more than 100 live tokens per account, also in parallel | `AppTokenStoreFacts.The_live_cap_counts_only_unexpired_unrevoked_tokens`, `…Concurrent_mints_at_the_cap_never_exceed_it` (red on PostgreSQL without the account lock); `TokenRoutesTests.The_live_cap_is_409` |
| A token never mints, lists or revokes a token, never reaches Management, never acts in another tenant, never reads outside its scopes on the Data API (Review Focus 3) | `TokenRoutesTests.A_token_cannot_mint_a_token`, `…A_token_cannot_list_or_revoke`, `…A_person_mints_a_personal_token_and_sees_it_once` (`notes` is 403 `out-of-scope`); `AppTokenManagementTests.A_token_is_refused_by_every_management_route`, `…A_bearer_beside_an_app_token_on_management_is_judged_by_the_token`; `AppTokenEngineFacts.A_token_never_acts_outside_its_tenant`; `AppTokenResolutionTests.A_header_naming_another_tenant_refuses_either_kind` |
| A host's `.WithAlvoCaller()` endpoint refuses either kind with the fixed 403, after an unusable token's 401; a configured key, a Bearer person and the anonymous caller reach it as before; a bound command admits a token whose scopes cover its entity and refuses one whose scopes do not, and an unbound command refuses every token (final review I1) | `AppTokenCallerTests.A_token_on_a_custom_endpoint_is_refused_with_the_fixed_403_before_the_handler`, `…An_unusable_token_on_a_custom_endpoint_is_401_like_any_key`, `…A_configured_key_a_bearer_person_and_the_anonymous_caller_reach_a_custom_endpoint_as_before`, `…A_command_admits_a_token_whose_scopes_cover_its_entity`, `…A_command_refuses_a_token_outside_its_scopes_403_out_of_scope` |
| A service token's tenant role never writes a global entity, directly or through a relation term, and never reaches Management (Review Focus 4) | `AppTokenEngineFacts.A_service_token_s_tenant_role_never_reaches_global_data_or_management` (red when its roles go into `Roles`) |
| The descriptor moving under a live token counts on its next request: a role dropped from `assignableRoles`, a kind switched off and on, `selfService` removed (Review Focus 5) | `AppTokenEngineFacts.The_descriptor_moves_under_live_tokens`; `AppTokenResolutionTests.A_kind_the_descriptor_does_not_enable_is_refused`, `…Re_enabling_revives_an_unexpired_token`, `…A_service_token_acts_as_itself_with_its_roles_still_assignable` |
| A service token acts as itself: its own user id, only roles its minter holds, only for a shared tenant, minted only by an owner | `TokenRoutesTests.An_owner_mints_a_service_token_that_acts_as_the_tenant`, `…A_service_token_carries_only_roles_its_owner_holds`, `…A_personal_tenant_takes_no_service_token`, `…A_member_who_is_not_an_owner_cannot_mint_a_service_token`; `AppTokenStoreFacts.A_service_token_needs_an_owner_of_a_shared_tenant`, `…A_mint_that_would_act_as_someone_other_than_its_minter_is_refused` |
| A stranger learns nothing: a tenant they are not in and an unknown one are one 404; another person's token, a token the descriptor no longer honours and an unknown id are one 404 | `TokenRoutesTests.A_stranger_s_and_an_unknown_tenant_are_the_same_404`, `…Any_owner_revokes_a_service_token_and_nobody_else_s_personal_one`, `…A_plain_member_revoking_a_service_token_is_the_same_404_as_an_unknown_id`, `…A_service_token_the_descriptor_no_longer_honours_cannot_be_revoked`, `…A_member_s_list_after_self_service_is_switched_off_shows_only_personal_tokens` |
| An unusable token is any key's 401 — a wrong secret (constant time, dummy hash), expired, revoked — decided before any membership read | `AppTokenResolutionTests.A_wrong_secret_is_refused_like_an_unknown_id`, `…An_expired_or_revoked_token_is_refused_before_any_membership_read`; `AppTokenManagementTests.An_unusable_app_token_is_401_like_any_key`; `TokenRoutesTests.Revoking_kills_the_token_on_its_next_request` |
| The prefix means a persisted app token: dev keys may not use it; a prefixed id never reaches the dev keys, nor an unprefixed one the registry | `AlvoAuthOptionsValidatorTests.A_dev_key_with_a_reserved_prefix_is_refused`; `ApiKeyStoresTests.A_prefixed_id_never_reaches_the_dev_keys_and_an_unprefixed_one_never_the_registry`, `…Without_a_registry_an_app_token_is_unknown`; `AppTokenSecretTests.Kind_of_a_key_id` |
| The secret is 256 bits, stored as its hash, answered once, and never in a URL, a list, a refusal, a `ToString` or a log (spec §14 AC6) | `AppTokenSecretTests`; `AppTokenResponseTests`; `TokenRoutesTests.The_list_shows_my_tokens_and_my_tenants_service_tokens_never_a_secret`; `AccountsOpenApiTests.The_token_route_is_in_the_document_once_and_only_its_201_carries_the_credential`, `…The_token_list_and_revoke_are_in_the_document_once_and_take_no_secret`; `AppTokenRequestTests.No_violation_echoes_what_was_sent`; `SecretsNeverLoggedTests.No_app_token_secret_appears_in_any_log` (the credential whole, its secret and its hash, at Trace; red when the resolver logs the presented key), `…No_error_body_echoes_a_presented_token` |
| Default-deny: without `accessTokens` every token route is 404 naming its key, and a kind switched off is 404 naming its own; apply refuses `service` without `createTenants` | `TokenRoutesTests.Without_access_tokens_every_route_is_404_naming_its_key`, `…A_kind_switched_off_is_404_naming_its_key`, `…Without_access_tokens_list_and_revoke_are_404_naming_its_key`; `AccessTokensCheckTests.Service_tokens_without_self_service_are_refused`, `…Service_tokens_without_create_tenants_are_refused`; `AppTokenPolicyTests` |
| Every bound of the body is refused at its pointer: scopes without a wildcard and only declared entities, listed in the refusal; expiry at most `maxLifetimeDays` ahead | `AppTokenRequestTests.Each_bound_is_refused_at_its_pointer`, `…An_unknown_entity_is_refused_with_the_descriptor_s_entities`, `…Exactly_max_lifetime_is_accepted`; `TokenRoutesTests.An_invalid_body_is_422_and_stores_nothing` |
| A token is minted only where it would resolve: a host that replaced the `IApiKeyStore` gets the `feature-disabled` 404 naming `auth.accessTokens` and stores nothing; a host that decorates it keeps minting, and the token works | `AppTokenReachTests.A_host_that_replaced_the_api_key_store_is_refused_the_mint`, `…A_host_that_decorates_the_api_key_store_keeps_minting_and_its_tokens_resolve`; `ApiKeyStoresTests.The_reach_probe_is_answered_only_with_a_registry_and_is_never_a_key` |
| Minting shares the `Create` budget with tenant creation | `TokenRoutesTests.Minting_answers_429_after_the_create_budget_and_shares_it_with_tenant_creation` |
| `lastUsedAt` is minute-grained, and its write never fails a request | `ApiKeyStoresTests.App_token_touches_are_throttled_to_one_a_minute`; `TokenRoutesTests.Last_used_is_recorded_within_a_minute`; `AppTokenResolutionTests.A_failed_last_used_write_never_fails_a_request_whose_token_is_valid` |
| A configured API key behaves exactly as before (AC7) | `AppTokenManagementTests.A_configured_key_still_manages_as_before`; `ApiKeyStoresTests.Dev_keys_resolve_from_configuration_and_app_tokens_from_the_registry`; `ApiKeyContextResolverTests`, unchanged |

### Recorded decisions and deviations

From the plan (`2026-11-13-accounts-s5-tokens.md`, D1–D16), one line each:

- **D1** — the routes live under the sign-in prefix (`/auth/v1/tokens`), not §7.2's `{prefix}`, as in S3 and S4:
  `/api/tokens` would collide with an entity named `tokens`, and minting needs a person's session, which exists only
  where sign-in is mapped. No TeaPie path pin moves.
- **D2** — "app token" is the code's word for a personal or service token; no type is named `AccessToken*`
  (`AccessTokens` is already the JWT issuer); only the descriptor key keeps the spec's `auth.accessTokens`.
  **Deviation from the plan's names:** the route types are `AppTokenRequest`, `AppTokenResponse`, `AppTokenProblems`
  and `AppTokenListItem`, not `TokenRequest`/`TokenResponse`/`TokenProblems`/`TokenListItem`.
  `MMLib.Alvo.Accounts.Internal` already has the OAuth token endpoint's `TokenResponse` and `TokenProblems`, and a file
  naming both needed a `using` alias. `TokenRoutes`, the operation ids and the schema names are unchanged.
- **D3** — the credential is `alvo_pat_`/`alvo_svc_` + 128 random bits, a `.`, and a 256-bit secret, all base64url;
  stored as `ApiKeyHash.Compute(secret)`. Both prefixes are reserved at startup.
- **D4** — one `IApiKeyStore`, two homes: `ApiKeyStores` routes a prefixed id to the registry and every other id to the
  dev keys, registered with `TryAddSingleton`. A host that wants its own key store **decorates** Alvo's registration
  rather than replacing it, and tokens keep resolving; a host that replaces it owns key resolution entirely, and its
  `POST /tokens` answers 404 `feature-disabled` naming `auth.accessTokens` and the cause instead of minting a token
  nothing would accept (`host.md`). `last_used_at` is written at most once a minute per token per replica.
- **D5** — the resolution order above; a personal token in a tenant costs two reads. **Corrected from the plan's
  listing**, which judged the kind before `TenantResolver`: the code resolves the tenant first and judges the kind
  inside `AppTokenPrincipals`. Both refusals are the same 401, so nothing observable differs.
- **D6** — a personal token's principal is a `User` actor, a service token's an `ApiKey` actor. **Deviation from the
  master plan's description of `User`** ("a principal resolved from a person's session"): no route relied on it — the
  self-service routes admit by channel, and Management refuses tokens by D9, not by kind.
- **D7** — who mints: any person a personal token, an owner a service token, never a token.
- **D8** — service tokens are for shared tenants only, so a personal tenant's is 409, and apply refuses `service`
  without `createTenants`. **Deviation, stricter than spec §3.1's "requires `selfService`"**, which it implies: a
  personal tenant has one member forever, so a service token there outlives nothing a personal token would not.
- **D9** — the Management API refuses an app token with 403 `forbidden` and a fixed sentence, by its prefix and after it
  resolved, so an unusable token is any key's 401. `AlvoPrincipal` does not grow. S1 D8 is closed. A host's
  `.WithAlvoCaller()` endpoint now refuses the same way, with its own sentence — a deviation from D9, recorded below.
- **D10** — a token dies in the transaction that kills its reason, and every mint is serialised against those
  transactions. **Deviation beyond spec §7.1** for the service half: §7.1 lists removal and demotion; a ban also
  revokes the service tokens the account minted, because a banned owner who kept a secret is exactly spec §16 B4's case.
- **D11** — default-deny, decided per request; switching a kind back on revives unexpired, unrevoked tokens.
- **D12** — scopes are `entity:read|write`, at least one, unique, declared, no wildcard.
- **D13** — bounds: name 1–100, `expiresAt` required and refused past `maxLifetimeDays`, at most 100 live tokens per
  account. Lowering `maxLifetimeDays` bounds new mints only; an existing token keeps its expiry.
- **D14** — create answers 201 with the credential and `header`, once, and no `Location`; the list never carries the
  credential or a hash; revoke is 204 and idempotent; everything is `no-store`.
- **D15** — minting is charged `Alvo:Api:RateLimits:Create`, the budget `POST /tenants` already draws on (spec §7.3).
  One budget for both, recorded; listing and revoking are not charged.
- **D16** — public growth is exactly `ApiKeyKind`, `ApiKeyRecord.Kind`, `ApiKeyRecord.MintedBy`, `Auth.AccessTokens` and
  `AuthAccessTokens`. No new problem type.

And where the implementation answered differently from the plan's first wording, or a test had to look further:

- **Minting first asks whether the token would ever resolve** (Task 9 review): right after the `auth.accessTokens` gate,
  `AppTokenReach` asks the resolved `IApiKeyStore` for a reserved probe id (`alvo_pat_~reach`, outside base64url, so no
  minted id equals it), which `ApiKeyStores` answers itself with a dead record while it has a registry. A decorator
  passes it through; a replacement does not, and the mint is the `feature-disabled` 404. Not a boot refusal: the
  descriptor is unknown at boot, and replacing the store while never using tokens is legitimate.
- **`ApiKeyStores` takes no clock.** Its throttle judges the instant the resolver passes, so one request is judged on
  one instant (`ApiKeyStoresTests.The_throttle_reads_the_instant_it_is_given_not_the_clock_again`).
- **The registry refuses a mint whose actor contradicts its kind** — a personal token acting as anyone but its minter, a
  service token acting as its minter — with an `ArgumentException`. The routes never send either; it is the guard
  below them.
- **A refused `PATCH /api/regions/{id}` through `jobs.exists(…)` is 404, not the plan's 403**, for S3's recorded
  reason: an `update` rule filters the rows a caller may change.
- **The engine facts join members by invitation.** "A dispatcher creates Acme … the owner demotes them" needs two
  people, since nobody changes their own membership, so Carol joins as a dispatcher and Ada demotes her. Dropping
  `dispatcher` from `assignableRoles` drops it from `creatorRoles` too, which must stay within it.
- **`team-tracker`'s `jobs` gained `audit: true`**, so a fact can read `created_by`.
- **A host's `.WithAlvoCaller()` endpoint refuses app tokens** (final review I1). **Deviation from plan D9**, which let
  them reach such an endpoint: a custom endpoint names no entity, so `CallerGate` checks no scope there, and
  `AlvoCaller` exposes neither scopes nor key id — a token minted with only `jobs:read` would have acted there as its
  whole principal, while the text told its owner it was "limited to the scopes named". `AlvoCallerFilter` now resolves
  the token (an unusable one is still 401) and answers 403 `forbidden` with a fixed sentence
  (`ProblemResultFactory.AppTokenCustomEndpointForbidden`), by the same key-id test Management uses
  (`AppTokenSecret.IsAppToken`). Mapped commands are unchanged: `CommandEndpointFilter` already checks a token's
  scopes against the command's entity, so a token within them is admitted, and the handler then acts with the
  command's own authority. Public API unchanged. Scope opt-in for custom endpoints:
  [#536](https://github.com/Burgyn/MMLib.Alvo/issues/536).

### Not in S5

- Scopes finer than entity × read/write — a field, an operation, "only my own rows".
- App tokens on a host's own `.WithAlvoCaller()` endpoints: refused 403 until an endpoint can name the scope it needs
  ([#536](https://github.com/Burgyn/MMLib.Alvo/issues/536)). A command's writes beyond its own entity stay unscoped,
  judged by the rules.
- A per-token IP allow-list.
- OAuth client credentials (spec D3). A service token is an API key, not an OAuth client.
- A sweep of revoked and expired rows. Nothing deletes an `_api_keys` row yet, so the table grows by every mint —
  bounded per account by the live cap, not in total (spec D10; [#527](https://github.com/Burgyn/MMLib.Alvo/issues/527)
  grows by this table).
- Events and an audit trail for minting and revoking (spec D9).
- A settled `/auth/v1` contract before v0.1: one name per thing, one answer for the wrong kind of credential, the
  rate-limit keys (`RateLimits:Create` also charges `POST /tokens`, `RateLimits:Token` is the OAuth endpoint), the
  `auth.accessTokens` name, and `Idempotency-Key` on `POST /tenants`, `…/invitations` and `/tokens`:
  [#542](https://github.com/Burgyn/MMLib.Alvo/issues/542).
- The field-service demo, its TeaPie suite, the users' guides and the descriptor skill — S6, [below](#the-demo-s6).

## The demo (S6)

S6 changes no product behaviour. It takes everything S2–S5 built and runs it the way an app's users would: in the
published container, over PostgreSQL, through HTTP, with people who sign in rather than dev keys. The vehicle for it is
`examples/field-service`, the advanced demo, which was already a multi-tenant dispatch backend driven by five dev keys.
It became the spec's level-3 showcase (§12) **without losing a test**: the dev keys keep working beside sign-in, and
every suite that ran against the stack before still runs unchanged.

S6 also shipped what an app author reads — three guides, a generated reference page and a descriptor skill — and that
is covered at the end of this section. The plan is `docs/superpowers/plans/2026-11-13-accounts-s6-demo-docs.md`
(D1–D16); where the build answered differently, the deviation is recorded [below](#recorded-decisions-and-deviations-2).

### What it proves

A new TeaPie collection, `test/teapie-field-service/170-SelfService`, tells one story with three people: **Dana**, a
dispatcher who founds a company; **Theo**, the technician she invites; and **Olga**, an outsider who belongs to nothing.
Each is a brand-new Google account made for the run. The cases run in order, and each one leans on what the one before
it set up:

| Case | What happens, and what it pins |
|---|---|
| `001-sign-in` | The three people sign in. Each sign-in is an RFC 8693 token exchange that answers a 15-minute access token and a rotating refresh token, never cached. A token signed by a key the issuer never published is refused, a replayed ID token starts no second session, and a new person has an account and no membership. The admin key creates a region for the run. |
| `002-invite-and-accept` | Dana creates *Acme Repairs* and is its owner and a `dispatcher`. She invites Theo as a `technician`: the code is answered once, the anonymous preview shows only the tenant, the inviter and the roles, and accepting twice changes nothing. Acme then has exactly two members, and the invitation list never carries a code. |
| `003-assigned-work` | Dana creates a customer and two jobs, one assigned to Theo. A create without `tenant_id` in the body is refused (see D11 below). In Acme, Theo lists exactly his own job; naming no tenant, he is refused by the tenant guard. He cannot place a job or create a customer, and he walks his job through the state machine to `completed` with his tenant role. |
| `004-billing-token` | Dana mints a `billing` service token (`dispatcher`, `work_orders:read`). It reads exactly Acme's completed job and cannot write (`403 out-of-scope`), whatever its role would allow. Theo's personal token reads only his job. Dana's token list carries no secret. |
| `005-member-removed` | Dana removes Theo. His next request in Acme is `401 invalid_token`, and his personal token died with his membership. He stays signed in, and the billing token, which is Dana's, keeps reading. |
| `006-foreign-tenant` | Olga naming Acme is refused outright (`401`); naming no tenant she meets the tenant guard (`403`). She still reads global data — the run's region. |
| `007-roles-per-tenant` | Theo founds his own garage, where he is owner and dispatcher. A self-made dispatcher cannot write the global `regions`, in his tenant or in Dana's (the admin key's create is the control). Dana joins the garage as a technician, and the same body she sends is refused there and created in Acme. |
| `008-dead-invitations` | A revoked invitation, and one that expires 15 seconds after it is made; the suite polls until it has lapsed. |
| `009-dead-invitations-answer-alike` | Used-up, revoked, expired and unknown codes are one `404`, identical apart from the trace id, and none of them made Olga a member of anything. |
| `010-tokens-reach` | A token cannot mint a token. An app token sent as `Authorization: Bearer` is `401 invalid_token` (the same secret in `X-Alvo-Api-Key` is the control). Personal and service tokens are refused at Management with its fixed sentence; a Bearer session there is an anonymous caller and gets the same refusal, never a `200`. An operator key is the control. |
| `011-refresh-rotation` | A refresh rotates the token. Replaying the old one inside the 10-second grace window answers the same child. Replaying it after the window is `400 invalid_grant`; the suite polls until it is. |
| `012-reuse-ends-the-session` | That reuse ended Dana's session: every refresh token of the family is dead and her newest access token is `401`. Theo, whose session it was not, is still signed in. |

Every item of §12's list maps to a row: the story is `001`–`005`, and the adversarial half is `005` (a personal token
dying with its owner's membership), `006` (a header naming a tenant you are not in), `007` (a role from one tenant absent
in another and on `regions`, and a self-made dispatcher refused there), `009` (dead invitations answering alike) and
`010` (a token minting a token, and a personal token refused by Management). Every scoped create carries both
`X-Alvo-Tenant` and `tenant_id`, as a real client has to today.

The two waits — an invitation's expiry and the grace window — are polls (`## RETRY-UNTIL-TEST-PASS`, at most 30 × 1 s),
never sleeps, so a slow runner waits longer instead of failing. Measured: about 16 s and 11 s.

### How to run it

**The whole e2e:** `scripts/test-e2e`. It makes every secret fresh, starts all three stacks and runs every TeaPie
suite, `170-SelfService` last in the field-service one. It is the required **Build & test** check's e2e half; it is in
no ring.

**Just this collection, while you work on it.** `170` does not need a fresh database: it creates its own people,
region and tenants, so it runs alone and runs again on a live stack. The other field-service collections do not —
they own the database and need `down --volumes` first.

```bash
set -a; . examples/field-service/demo-identities.env; set +a
export ALVO_FS_DISPATCHER_NORTH_SECRET=$(openssl rand -hex 16)   # and the other four, as the example's README shows
export ALVO_FS_SECRETS_KEY=$(openssl rand -base64 32)
docker compose --env-file examples/field-service/demo-identities.env \
  -f docker-compose.field-service.yml up --build --detach --wait
# artifacts/teapie/env.json: scripts/test-e2e's shape (test/teapie-field-service/env.example.json shows it)
dotnet tool run teapie -- test test/teapie-field-service/170-SelfService \
  -e compose --env-file artifacts/teapie/env.json --no-cache-vars --no-logo
```

If something else holds port 8081, export `ALVO_FS_PORT` after sourcing the env file and use the same port in
`fsBaseUrl`. `scripts/test-e2e` cannot be moved that way: it reads `demo-identities.env` again itself.

### How the stack signs people in

Nobody can sign in to a CI container with a real Google account, so the stack points Alvo's **Google provider at a
test issuer** and the suite plays Google. Only configuration differs from a real deployment; the code path is the one
production runs.

- **The issuer is the `sink`** (D3). The WireMock that already plays the billing system shares Alvo's network
  namespace, so `http://localhost:9090/oidc` is it. Compose sets
  `Alvo__Accounts__Providers__Google__Authority` to that URL and `AllowHttpMetadata: "true"`. The discovery document is
  a committed mapping (`examples/field-service/sink/mappings/test-oidc-issuer.json`, named TEST-ONLY); it says
  `issuer: https://accounts.google.com`, because Google's preset finds its provider by that issuer, and names the key
  set at `http://localhost:9090/oidc/jwks.json`. The ID tokens are RS256, the preset's only algorithm.
- **The key is made per machine and registered per run** (D4, a recorded deviation). `_shared/TestIssuer.csx` creates
  an RSA-2048 key the first time a run needs one and keeps it under `$TMPDIR/alvo-field-service-e2e/`; its `kid` is the
  key's RFC 7638 thumbprint. The first request of `001-sign-in` publishes the key's public half to the sink through
  WireMock's admin API, before anyone signs in. No private key is in the repository. The key is kept between runs
  rather than made per run because Alvo refetches a provider's key set for an unknown `kid` at most once every five
  minutes (`OidcMetadata.UnknownKidInterval`); a new key per run would refuse the third run inside five minutes on a
  live stack.
- **The stack runs `ASPNETCORE_ENVIRONMENT=Development`** (D5), because `AllowHttpMetadata` is refused in every other
  environment (spec §11.2). `Alvo:Accounts:Issuer` stays unset, so Alvo names its tokens after the URL a request
  reached; the audience defaults to the descriptor's name. Only `MMLib.Alvo.Accounts` reads the host environment, the
  image ships no `appsettings.Development.json`, and Alvo's own exception handler masks the developer exception page,
  so nothing else changes.
- **A secrets key per run** (D6). Accounts refuses to start without `Alvo:Secrets:EncryptionKeyFile`.
  `scripts/test-e2e` exports `ALVO_FS_SECRETS_KEY` fresh, and compose hands it over as a Compose secret mounted at
  `/run/secrets/alvo_fs_secrets_key`; `${…:?}` refuses a start without it.
- **Wider rate budgets** (D7): `Token` 120, `Create` 30 and `Invitations` 120 a minute per IP, instead of 30, 10 and 20.
  One suite run comes from one address, and the limits themselves are pinned in process (`TokenRoutesTests`,
  `TenantRoutesTests`, `InvitationRoutesTests`), so a `429` in the e2e would be a flake, not a finding.

A consequence worth knowing: on a stack you start by hand, only the suite holds the signing key, so only a `170` run can
sign anyone in. `examples/field-service/README.md`, *Self-service*, says how to point the provider at real Google
instead.

### What else the stack change reached

- **The `090-Docs` path pin grew by exactly the sign-in surface** (D9): the 15 `/auth/v1` paths S5's Accounts OpenAPI
  snapshot holds, beside the 16 entity paths, and no others.
- **The load stack resets it all.** `test/load/docker-compose.load.yml` composes the demo's file, so it resets the
  Development environment, the three Google settings, the secrets-key file and the three rate budgets (`!reset null`)
  and the secret's mount (`secrets: !reset []`). The merged `alvo` service is identical to the one before S6, so load
  stays comparable to its baselines. `scripts/test-load` also exports `ALVO_FS_SECRETS_KEY`, as a safeguard.
- **`scripts/test-e2e`'s PostgreSQL partition check counts only the two seeded tenants.** It counted every distinct
  `tenant_id` in `work_orders`, and `170` adds Acme's jobs as a third. The claim is unchanged: storing everything under
  one discriminator would answer 1 or 0.
- **The embedded sample serves the same descriptor without sign-in** (D1). `samples/MMLib.Alvo.Samples.FieldService`
  registers no `AddAlvoAccounts`, so it logs the boot warning that the descriptor lists `google` and the host signs
  nobody in, and every request there behaves as before. Sign-in is the standalone stack's story.

### The guides, the reference and the skill

- **Three guides under *Secure it*** (D15): *Sign-in and accounts*, *Tenants, members and invitations* and *Tokens for
  integrators*. Each opens with what you will build, shows the smallest working thing, then explains the mechanics and
  the limits. Stale claims elsewhere on the site ("only local accounts and API keys", Google "tracked in #36", the
  generated routes taking "API keys only") were swept for the claim, not by file.
- **The guides' exchanges are hand-written, and held three ways** (D12): every descriptor a guide shows is a snippet
  `DocsSnippetTests` applies; every `METHOD /auth/v1/…` a guide names is checked against the generated reference; and
  each worked flow names the `170` case that runs it for real (on the fuller field-service descriptor, with the same
  answers).
- **The reference** (D13): `tools/MMLib.Alvo.DocsGen` boots a second in-process host over the field-service descriptor
  with Google configured (a placeholder client id, an `https` issuer, a key file in its temp directory — no network) and
  publishes only the `/auth/v1` paths, as `reference/accounts-api`.
- **A tenth descriptor skill, `alvo-descriptor-accounts`** (D14), the catalogue's last slot. `SkillAreas.Accounts`
  owns `/auth/**` except `/auth/roles`, and `/tenancy/selfService/**`. Its line in the always-on skill list did not fit
  the schema assistant's budget, so two sentences of the base prompt were cut (each repeated by a skill or by a
  refusal's own fix) and the prompt is now **v10**.

### Invariants, and the tests that hold them

| Invariant | Held by |
|---|---|
| People sign in through the real container, and every wall of §12 holds there: tenant walls, roles per tenant, the global `regions`, dead invitations, a token's reach, refresh reuse (AC5, over HTTP) | `test/teapie-field-service/170-SelfService/001`–`012`, inside the required e2e check |
| No private key is committed anywhere, and the sink serves only the public half of the run's key | `FieldServiceTestIssuerTests.No_private_key_is_committed` (every path `git ls-files` lists, staged ones included), `…The_committed_issuer_carries_no_key_material`; `170-SelfService/001`'s "The sink publishes the public half of the run's key, under its kid, and nothing private." (the live key set's members are exactly `alg`, `e`, `kid`, `kty`, `n`, `use`, and `n`/`e` are the run's key) |
| Only the demo stack trusts `http` metadata, beside Development; the compose authority, the discovery document and the suite's registration agree; the stack and the suite read one client id; the secrets key comes from the run | `FieldServiceTestIssuerTests.The_demo_stack_trusts_http_metadata_only_in_development`, `…The_discovery_document_names_googles_issuer_and_the_sinks_key_set`, `…The_suite_registers_its_key_set_where_the_discovery_document_points`, `…The_stack_and_the_suite_read_one_client_id`, `…The_secrets_key_comes_from_the_run_and_is_never_committed`; the product side is `AccountsOptionsTests.Allowing_http_metadata_is_refused_outside_development_naming_the_setting` |
| The load stack resets the demo's sign-in, so load measures the host its baselines describe | `FieldServiceTestIssuerTests.The_load_stack_resets_the_demos_sign_in` |
| Every existing suite stays green under the grown descriptor and the Development + accounts stack (AC7) | `scripts/test-e2e` whole (`test/teapie` 22, `test/teapie-field-service` 632, `test/teapie-field-service-host` 69, the PostgreSQL checks); `090-Docs` pins the path set by equality |
| Every route a guide names exists, and the reference holds exactly the sign-in routes | `AccountsReferenceTests.Every_route_a_guide_names_is_a_sign_in_operation`, `…The_reference_holds_exactly_the_sign_in_routes`, `…No_entity_route_reaches_the_sign_in_reference`, `…The_description_links_the_three_guides` |
| Every descriptor a guide shows applies | `DocsSnippetTests` (the `website/src/snippets/{sign-in,tenants,tokens}/*.alvo.json` files) |
| The skill's worked examples are real outcomes, it fits the caps, and the accounts paths load it | `SkillExampleOutcomeTests`, `SkillConformanceTests`, `SkillAreasTests.The_accounts_paths_belong_to_the_accounts_skill`, `AlvoAssistantTests.The_always_in_context_instructions_do_not_outgrow_the_v3_base_prompt` |

### Recorded decisions and deviations

From the plan (`2026-11-13-accounts-s6-demo-docs.md`, D1–D16), one line each:

- **D1** — sign-in lives on the standalone stack only; the embedded sample serves the same descriptor with no
  `AddAlvoAccounts`. **Correction to the plan's wording:** D1 said the sample's "`.WithAlvoCaller()` endpoints
  (dispatch, technician app)" would refuse app tokens. The sample maps no `.WithAlvoCaller()` endpoint: dispatch is a
  `MapAlvoCommand`, which takes a token within its entity's read and write scopes, and the technician app is a
  `MapAlvoWebhookReceiver`, which authenticates by its signing secret. The sample's README states the general rule
  (#536) and nothing about its own routes.
- **D2** — the descriptor gains exactly §12's keys, with `local` kept and no `personalTenant`; `regions` is untouched,
  so a self-made dispatcher's refusal there is pinned as a `403` with the admin's `201` as control.
- **D3** — the test issuer is Google's preset under another authority.
- **D4** — **Deviation from the plan and from spec §12**, which committed one test-only private key under `test/` and
  had the sink serve a static key set. No key is committed: it is made per machine and registered per run, as
  described above. The plan's reason for committing one ("a per-run key would leave a hand-started stack unable to
  sign anyone in") no longer applies — any `170` run publishes the key on a hand-started stack too — and a public
  repository then holds no private key for a secret scanner to flag. The cost: if the temp key file is deleted while a
  stack stays up, two fresh keys inside five minutes fail on the refetch throttle; restarting the stack clears it.
- **D5** — the stack runs Development, with `Issuer` unset.
- **D6** — a secrets key per run, as a Compose secret.
- **D7** — wider rate budgets. **Deviation from the plan's number:** `Invitations` is 120, not 60. All five
  invitation routes share one budget, and one `170` run spends about 31 calls on it (the expiry poll alone about 16
  previews), so two runs inside a minute on a live stack — how a contributor iterates — were refused `429` at 60.
- **D8** — the grace window keeps its default and is waited out by polling; so is a 15-second invitation expiry.
- **D9** — the `090-Docs` pin moves by exactly the 15 sign-in paths.
- **D10** — three people per run, minted fresh; `170` creates its own region and tenants.
- **D11** — the story runs in §12's order, and every scoped create carries `tenant_id` and `X-Alvo-Tenant`.
  **Deviation from the plan's expected answer:** a scoped create without `tenant_id` is `403 forbidden`, *"The write
  was rejected by policy."*, with no `violations` — not a `422` at `/tenant_id`. A candidate with no tenant fails the
  synthesized tenant scope's `WITH CHECK`. `170-SelfService/003` pins the real answer and the guides quote it, as a
  current limitation.
- **D12** — the guides' exchanges are hand-written and held three ways (above).
- **D13** — the reference publishes the sign-in routes from OpenAPI.
- **D14** — a tenth descriptor skill. **Beyond the plan:** the skill list's new line did not fit the always-on budget
  (`AlvoAssistantTests`, 22 758 bytes), so the base prompt lost two sentences rather than the budget growing, and its
  version moved to **v10**.
- **D15** — three guides under *Secure it*, each opening with what you will build.
- **D16** — no public API moves and no product behaviour changes; every `PublicApi.*.verified.txt` is unchanged.

And where the product answered differently from the plan's first wording (each pinned by the case named):

- **A technician placing a job is `422 unresolved-reference` at `/customer_id`, not `403`** (`003`). Reference
  resolution runs, as the caller, before the row-level `create` rule, and a technician reads no customer. A customer
  create, which has no reference, reaches the rule and is the `403`.
- **A token writing outside its scopes carries the `out-of-scope` type** (`004`).
- **Management answers an anonymous caller `403`, not `401`**, so a Bearer session there is pinned as "the same refusal
  as anonymous" (`010`).
- **An app token sent as `Authorization: Bearer` is the generic `401 invalid_token`** (`010`): *"The presented bearer
  token could not be used. …"*. It does not say the secret belongs in `X-Alvo-Api-Key`; the tokens guide says it.

### Not in S6, and owed

- **A scoped create's missing `tenant_id` names no field.** The `403` is correct but unhelpful to an agent: Alvo could
  fill the column from the tenant the request acts in, or answer a structured violation at `/tenant_id`. A DX
  follow-up: [#538](https://github.com/Burgyn/MMLib.Alvo/issues/538).
- **A Bearer carrying an `alvo_pat_` or `alvo_svc_` token gets no hint.** The `401` could name `X-Alvo-Api-Key` as its
  fix. A DX follow-up: [#539](https://github.com/Burgyn/MMLib.Alvo/issues/539).
- **An `eval-assistant` run on the v10 prompt and the tenth skill** is owed (it costs tokens and is in no ring). Two
  smaller gaps sit beside it: `SkillAreas.AreaOf("/tenancy", …)` does not look for a `selfService` inside an added
  value, and the base prompt's "You can change" list names neither `auth` nor `tenancy.selfService`. All three are
  [#540](https://github.com/Burgyn/MMLib.Alvo/issues/540).
- **No real browser signs in anywhere.** The Google console steps and `sign-in.js` follow Google's documented API;
  the demo stack signs people in through the test issuer only.
- App tokens on a host's own `.WithAlvoCaller()` endpoints ([#536](https://github.com/Burgyn/MMLib.Alvo/issues/536)),
  and everything else under *Not in S5* above.

## The stack review's fixes

The whole-stack review of S0–S6 (`.superpowers/sdd/2026-11-13-accounts-00-master/stack-review-{A,B,C}.md`) found
defects that no slice could see, because each sits where two slices meet. Each fix below is a fact on both engines.

- **A refresh-token reuse also kills what the thief minted** (A-I1, S2 × S4 × S5). Reuse is Alvo's one affirmative
  signal that a refresh token was stolen, and a stolen session could mint a personal token, a service token and an
  owner-granting invitation, none of which the session's revocation touched. The reuse's transaction now also revokes
  every live app token the account minted (`_api_keys.minted_by`) and every live invitation it created
  (`created_by`), in any tenant, whose `created_at` is at or after the reused session's own — invitations first, then
  tokens, the ban's lock order (`EfCoreAccountStore.RevokeMintedSinceAsync`). Nothing records the session a token was
  minted in, so the sweep is by time: a token the person minted from another of their sessions after the stolen one
  started dies too, and is minted again; one minted before it started survives.
  **The write in flight is closed too** (round 2 of the review). The thief can trigger the reuse at will — it holds the
  refresh token its own refresh spent — and the routes check the session when the request arrives, then read the body,
  then open the store's transaction, so a slow body held a gap of seconds in which a mint or an invitation committed
  after the sweep and lived. The routes now carry the caller's session (`PersonSessionKey`, read back from the person
  principal's `session:<sid>` key id) into `MintTokenRequest.Session` and `CreateInvitationRequest.Session`, and each
  store transaction re-reads it with `AuthTables.LockLiveSessionSql` — a no-op `UPDATE … WHERE revoked_at IS NULL AND
  expires_at > @now` that takes the session row's lock — before it writes. Either the write takes that lock first, and
  the reuse's revocation waits for it to commit and its sweep then revokes what it wrote, or the reuse revokes the row
  first, and the write finds no live session and writes nothing: `MintTokenStatus.SessionEnded` /
  `MemberChangeStatus.SessionEnded`, answered as the dead session's own `401 invalid_token`. SQLite's write gate
  serialises the two outright. **Lock order**: a mint takes the account row, then the session, then the tenant; an
  invitation's creation takes the session, then the tenant (before its version bump); a refresh takes the session, then
  the personal tenant it ensures; reuse and ban take the session, then invitations, then `_api_keys`. No pair takes two
  of these rows in opposite orders. Facts: `AccountStoreFacts.A_mint_or_an_invitation_naming_a_session_a_reuse_ended_writes_nothing`,
  `…A_mint_racing_a_reuse_of_its_session_never_outlives_it`, `…An_invitation_racing_a_reuse_of_its_session_never_outlives_it`
  (20 iterations; red on both engines without the re-read), and over HTTP `CredentialWritesMidReuseTests` (the store
  ends the session between the route's check and its own transaction; red when the route drops the session).
  **What it does not undo:** what the session already *used* rather than minted. An owner-granting invitation that
  the thief's accomplice accepted before the reuse is a membership by then, and a promotion or role change the session
  made is a member write; neither is a credential of the session, and the reuse leaves both. A host without Alvo's
  person resolver names no session (`PersonSessionKey.SessionOf` is `null`), so its writes are not tied to one.
- **A banned person's queued work loses their tenant roles** (B-M1, S2 ban × S3 D10). A ban revokes sessions,
  invitations and tokens but leaves membership rows, and the registry read an `EventActor` delivery narrows by
  (`RegistryMembershipResolution` → `ITenantStore.FindMembershipAsync`) did not look at the account, so a queued,
  retried or replayed delivery ran with the banned person's full tenant roles. That read now uses
  `TenantTables.HonouredMembershipSql`, which adds `NOT EXISTS` a disabled account. **Narrower than "every path using
  the same SQL"**, deliberately: the member writes read the *target* member with `MembershipSql`, and honouring the ban
  there would stop an owner from demoting or removing a banned member. The in-transaction actor reads (a mint, an
  accept, a member write) need no change: each first locks the actor's account row with `disabled_at IS NULL`, or runs
  for a person whose live session already proved it. Enabling the account honours the memberships again.
- **Apply warns when nobody could ever sign up** (C-I6, S2 × S4). `auth.signUp.mode: invitationOnly` with an end-user
  provider and without `tenancy.selfService.invitations: true` — no tenancy at all, or self-service without
  invitations — now draws a **warning**, not a refusal, at `/auth/signUp/mode` (`SignUpCheck.Warnings`), naming both
  ways out. A warning, following `GlobalWriteWarnings`' precedent rather than `SelfServiceCheck`'s refusals: those
  refuse descriptors that cannot work at all (invitations with no shared tenant to invite into), while this one is a
  legitimate "registration closed" state in which existing accounts keep signing in. `examples/complex-crm`, the
  schema showcase, had exactly this shape (S2 added `invitationOnly` before invitations existed), so it now declares
  `tenancy.selfService` with `invitations: true`. Its one tenant role is a role of its own, `member`, because every
  other role it declares is named in an `access` level, and a tenant role there is refused.

Still open after these fixes, each filed: the HTTP contract's coherence before v0.1
([#542](https://github.com/Burgyn/MMLib.Alvo/issues/542)); a banned sole owner's tenant and the owner-removal × ban
lock order ([#543](https://github.com/Burgyn/MMLib.Alvo/issues/543)) — the reuse sweep above is a third multi-row
sweep over the same invitations and `_api_keys` rows, in the same order as the ban's, and belongs in that issue's
race fact; signing-key rotation and `azp` ([#544](https://github.com/Burgyn/MMLib.Alvo/issues/544)).
