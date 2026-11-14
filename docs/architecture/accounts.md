# End-user accounts: sign-in, sessions, the person resolver (S2) and self-service tenants (S3)

> The contributor's view of `MMLib.Alvo.Accounts` and the parts of the core and the EF adapter it uses: the tables, the
> path a request takes, the invariants and the tests that hold them, and where Alvo deviates from the standards it
> adopts. The design is `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md`
> (§3–§7, §14). Sign-in's decisions D1–D17 are in `docs/superpowers/plans/2026-11-13-accounts-s2-sign-in.md`; the
> tenants' D1–D15 in `docs/superpowers/plans/2026-11-13-accounts-s3-tenants.md`. The first half of this page is sign-in
> (level 1); [*Tenants a person creates (S3)*](#tenants-a-person-creates-s3) is levels 2 and 3. Users' guides arrive in
> S6.

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
  zero setting (strict) are both facts.
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
- Personal and service tokens (S5).
- Key rotation (D12): one key per purpose, generation 1.
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

Members arrive through invitations, which are S4; until then a shared tenant's only member is its creator, and the
tests seed a second one directly in the table (plan D15). `TenancyEngineFacts.Level_two_end_to_end` and
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
(event actors here, personal tokens in S5).

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
| An event actor keeps only assignable tenant roles, narrowed at delivery to the membership it still holds (D10) | `EventActorTests.A_recorded_tenant_role_no_longer_assignable_is_not_held`; `DeliveryRunnerActorTests` |
| The registry is the tenant list only under `selfService` and only when the host registers none (D13) | `TenantCatalogsTests` |

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
  the tenant, with project roles only (`events.md`).
- **D11** — `invitations` is accepted by the schema and warned as unhonoured until S4.
- **D12** — the global-write warning lists each `create`/`update`/`delete` rule of a global entity whose role literals
  include `authenticated`; a rule that admits without naming a role is not listed.
- **D13** — the default tenant catalog is resolved per descriptor, not registered. On a host with a registry and no
  catalog of its own, an `EachTenant` schedule is judged per descriptor rather than in the boot's catalog-free pass, so
  a relational host with no descriptor boots and a stored-descriptor boot stands down instead of exiting (spec §6.6).
- **D14** — `Alvo:Api:RateLimits:Create`, 10 per client per minute, charged on `POST /tenants` only.
- **D15** — members arrive only through invitations (S4); tests seed them.

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

- Invitations — the only way a second member joins a shared tenant (S4).
- Personal and service tokens, and with them `X-Alvo-Api-Key` carrying a person (S5).
- Deleting a tenant, and transferring a personal one.
