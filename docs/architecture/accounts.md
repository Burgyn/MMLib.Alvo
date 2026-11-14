# End-user accounts: sign-in, sessions and the person resolver (S2)

> The contributor's view of `MMLib.Alvo.Accounts` and the parts of the core and the EF adapter it uses: the tables, the
> path a request takes, the invariants and the tests that hold them, and where Alvo deviates from the standards it
> adopts. The design is `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md`
> (§3–§5, §7.3, §14); the slice's decisions D1–D17 are in `docs/superpowers/plans/2026-11-13-accounts-s2-sign-in.md`.
> Users' guides arrive in S6.

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
- **Accounts are a package, and on per host** (D1, D9). A host that never calls `AddAlvoAccounts` is byte-for-byte the
  host it was:
  - no key file is required;
  - no `/auth` route is mapped;
  - the OpenAPI document gains no path or security scheme (only the shared problem-type enum lists the four new slugs);
  - `Authorization` is ignored.

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
   2. the tenant header → membership resolution (S2: none requested → no tenant; any requested → refuse the principal)
   3. PersonSessions.IsLiveAsync — the descriptor still lists the session's idp (no I/O), then ONE store read:
      session unrevoked, unexpired, account not disabled
   ▼
AlvoPrincipal: User = account, Roles = {authenticated}, TenantRoles = ∅, Tenant = null, ActorKind = User,
               scopes *:read/*:write (the rules decide), KeyId = "session:<sid>"
```

Every refusal is the same 401 `Bearer error="invalid_token"`, so a forged token and an expired session look alike. The
order is the cost model (D13): a refused tenant costs no read, and a valid request costs exactly one.
`PersonResolverTests.An_access_token_is_validated_without_reading_keys_again` counts the reads.

The principal's roles are **project roles**. S1's role provenance decides where `@user.roles` can also see *tenant
roles*: only while every entity on a decision's path is `tenancy: scoped`, and never in a Management `access` level
(spec §6.2; `relation-rules.md`, *Binding*; `management-api.md`, *Who the Management API believes the caller is*). In
S2 a person has no tenant, so `TenantRoles` is empty and level 1 uses only `authenticated` and `@user.id`. S3 fills
the tenant through the same membership step, folded into the same one read.

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
| A host without accounts is unchanged; `Alvo:Auth:HeaderName = Authorization` is refused beside accounts (Review Focus 4) | `HostAccountsTests`; `OpenApiDocumentTests.Without_a_person_resolver_there_is_no_bearer_scheme`; `AccountsOptionsTests.An_api_key_header_named_authorization_is_refused_beside_accounts` |
| An API key beside sign-in works exactly as before; a key and a Bearer together are 401 ambiguous | `AccountsEngineFacts.An_api_key_beside_sign_in_behaves_exactly_as_before`; `CredentialReadingTests` |
| Clock boundaries: `iat` 5 min in, 5 min + 1 s out; ID-token `exp` ± 2 min; access token 30 s past `exp` in, 31 s out; reuse at 10 s replays, at 11 s kills (Review Focus 5) | `OidcIdTokenVerifierTests.The_boundaries_are_accepted`, `…An_id_token_is_refused`; `AccessTokensTests.A_token_validates_within_30_seconds_past_exp_and_not_31`; `RefreshGrantTests.A_reuse_at_exactly_ten_seconds_answers_the_same_child` |
| An ID token starts at most one session; an account is found by `(provider, subject)`, never by email | `TokenExchangeTests.A_replayed_id_token_starts_no_second_session`; `AccountStoreFacts.A_second_sign_in_finds_the_account_by_provider_and_subject_never_by_email` |
| Two people never see each other's rows | `AccountsEngineFacts.Two_people_never_see_each_other_s_rows` |
| A disabled person is refused on the next request, refresh and exchange; enabling revokes every session the account still has, so none from before it comes back — not even one that reached the table after the ban's revocation | `AccountsEngineFacts.A_disabled_person_is_refused_on_the_next_request`; `AccountAdministrationTests`; `AccountStoreFacts.Enabling_an_account_revokes_a_session_the_ban_never_saw` (both engines); `PostgreSqlAccountStoreTests.A_sign_in_racing_a_ban_leaves_no_session_an_enable_revives`, staged with a held transaction |
| Every dead refresh token answers byte-identically | `RefreshGrantTests.Every_dead_refresh_token_answers_byte_identically` |
| A host with no applied descriptor identifies nobody | `PersonSessionsTests.Without_an_applied_descriptor_a_genuine_token_identifies_nobody` |
| No secret in a log, a problem body or a `ToString`; IdentityModel's PII switch stays off (spec §14 AC6) | `SecretsNeverLoggedTests` (logs captured at Trace with state values and scopes) |
| The JWKS publishes every verifying key and never `d`; a token validates against it with an independent handler | `DiscoveryTests.The_jwks_publishes_the_signing_key_and_no_private_part`, `…A_token_validates_against_the_published_jwks` |
| A Bearer token never reaches Management | `AccountAdministrationTests.A_bearer_token_never_reaches_the_route`; `TenantRoleManagementTests` |
| Accounts reaches no guarded core or Abstractions internal | `AccountsArchitectureTests` |

## Deviations from the standards adopted (spec §4)

The spec records D1–D9. The ones S2 builds on are listed here so that a reader can tell a decision from an oversight.
D1 (a tenant registry) and D8 (no tenant header means the personal tenant) are S3's.

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

- Tenants, memberships and every `selfService` refusal (S3).
- Invitations (S4).
- Personal and service tokens (S5).
- Key rotation (D12): one key per purpose, generation 1.
- Auth events and role-change audit (spec D9).
- §3.1's global-write warning (S3).
