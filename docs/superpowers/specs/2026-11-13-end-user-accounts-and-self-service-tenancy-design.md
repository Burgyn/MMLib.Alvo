# End-user accounts and self-service tenancy

Written 2026-11-13 on `feat/end-user-identity-spaces`, from a design conversation with the maintainer;
revised the same day after an adversarial builder review (§16 records every finding and its resolution).
Status: **approach A approved by the maintainer; design revised after review; build authorised.**

> **What this makes possible.** A person opens an app built on Alvo, signs in with Google, and has data of
> their own — and, when the app wants it, shares a space with their family or their company, invites
> people into it, and hands an integrator a token. No operator provisions anything. Today none of that
> is possible: the Data API accepts only API keys from configuration, and the only people Alvo knows are
> the dashboard's operators.

## 1. Why, and for whom

The maintainer builds self-service apps on Firebase today and wants to build them on Alvo instead. Four
concrete shapes drove this design; each is a real codebase or plan that was read for it:

| App | Unit that owns the data | Sharing | What it needs from Alvo |
|---|---|---|---|
| **TimeTracker** (`MMLib.TimeTracker`, Firebase) | one person (`users/{uid}/…`) | a public read-only link per client | Google sign-in, data only the owner sees |
| **Spolu** (`ReBeLeader`, Firebase) | a **family** (user → family via `members`) | families join events; invite codes | sign-in, a shared unit, membership, invites |
| **Kruho** (`MMLib.Family`, a plan) | a **household**, a hard wall; a person in several households | roles parent/teen/child; private items in a household; grants to linked households; guest links | all of the above, plus roles per household |
| **Invoicing at KROS / a GitHub-style organisation** | a **company** (`companyId`) | members with roles | the same unit, called a company |

The common core: **a person signs in, has data under some unit, may belong to several units with a role in
each, and shares across units only explicitly.** Alvo already has that unit — the *tenant* — and a wall
around it that the framework enforces on every query. What it lacks is everything that turns a person
into a member of one.

### Goals

1. A person can **sign in with Google** from a browser or mobile app and receive an Alvo session.
2. Sign-in **creates the account** on first use, when the app's sign-up policy allows it.
3. The app author chooses, in the descriptor, **one of three levels** (§3).
4. A person can **belong to several tenants**, with **roles per tenant**, and picks the tenant a request
   acts in. A role held in tenant A is never present in tenant B, never on shared (global) data, and
   never toward the Management API.
5. A tenant owner can **invite** people by link and **manage members** and their roles.
6. A person can **mint tokens for integrators** from the app.
7. Everything is **one model**, so a later provider (Microsoft, Apple, GitHub, generic OIDC) and
   **email + password** slot in without changing it (§5.7).

### Non-goals (this initiative)

- Email/password, magic links, passkeys, MFA, other providers — **designed for (§5.7), built later.**
- Sharing *across* tenants and public links — **sketched (§9), built later.**
- OAuth consent for third-party apps and an MCP connector (Alvo as an authorization server).
- Deleting an account or a tenant; auth and membership events in the event stream (§10).
- An admin dashboard screen for end users and tenants (a Management API operation to disable an account
  *is* in scope, §5.6).
- A JavaScript client SDK, realtime, file storage, push notifications.
- Federating a host's own identity into the generated routes (#210) — not built, not foreclosed (§5.8).

### Anti-goals — a failure even if it worked

- **A token that can do more than the person who made it** — at mint time or later.
- **A request that chooses its way into a tenant.** The tenant header stays a *confirmation* of a grant
  (#140's invariant).
- **Authority that leaks out of the tenant it was granted in** — into another tenant, onto global data,
  or into managing the project.
- **Roles that survive their reason.**
- **A second way to do the same thing.** Tenancy is the wall; rules are for what happens inside it.
- **A hand-rolled JWT or OAuth variant.** Alvo adopts the RFCs (§4).

## 2. Approaches considered

**A — Tenants are the spaces people own (chosen).** A person may have a personal tenant, create more (a
family, a company), invite people, and pick the active tenant per request; Alvo checks the membership.
Data is walled by the existing `tenant_id` machinery; relation rules stay for privacy *inside* a tenant
and, later, for grants *between* tenants. Prior art: GitHub (personal account + organisations), Clerk and
WorkOS organisations (an *active organisation* per session).

**B — Ownership in the data, no tenancy (Firebase/Supabase style).** Alvo adds only sign-in and an owner
field; families and members are ordinary entities and access lives in relation rules.

**Why A** (the maintainer chose it after weighing these): the wall is enforced by the framework on every
query while a rule is a convention every entity must repeat (`baas-analyza.md` §4: a tenant leak is *the
worst possible BaaS bug*); the tenant machinery already exists and only its source is missing; everything
beyond rows (billing, deletion, storage, quotas, realtime) needs a tenant key; `tenant_id = X` is an
indexed equality where B is an `EXISTS` per row; and Kruho and Invoicing need a wall anyway, so B now
means two models later. **What A costs, accepted:** more security-core work, #161 first, explicit grants
for things that cross tenants, one tenant per request. **A does not remove B** — level 1 is B.

## 3. The three levels, chosen by the app author

**Level 1 — per-person data, no tenancy.** TimeTracker if it never shares.

```json
{
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

`audit: true` already fills `created_by` from the caller. The only new thing level 1 needs is a person.

**Level 2 — a personal tenant per person.** The wall does the owner check; moving to level 3 later is a
descriptor change, because rows already carry `tenant_id`.

```json
{
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

**Level 3 — shared tenants, invitations, roles per tenant.** Kruho, Invoicing, Spolu's families, the
field-service demo.

```json
{
  "auth": {
    "providers": ["google"],
    "roles": ["dispatcher", "technician"],
    "signUp": { "mode": "open" },
    "accessTokens": { "personal": true, "service": true }
  },
  "tenancy": {
    "enabled": true,
    "selfService": {
      "createTenants": { "creatorRoles": ["dispatcher"], "maxPerAccount": 5 },
      "invitations": true,
      "assignableRoles": ["dispatcher", "technician"]
    }
  }
}
```

### 3.1 The schema, exactly

**`auth.signUp`** — required as soon as `auth.providers` lists a provider Alvo honours for end users
(today `google`); absent → apply refuses with both choices spelled out. Explicit because adding a
provider changes what `authenticated` means: from *a provisioned caller* to *anyone who can sign in*.

| Key | Type | Meaning |
|---|---|---|
| `mode` | `"open"` \| `"invitationOnly"` | `open`: any person the provider vouches for may create an account. `invitationOnly`: an account is created only while accepting a valid invitation (§6.4). |
| `allowedEmailDomains` | string[] | Optional. Only accounts whose **trusted** email (§5.3) is in one of these domains may sign up — a company app restricted to `kros.sk`. |

**`tenancy.selfService`** — optional; absent = today's behaviour (tenants come only from API keys and
operator grants). Requires `tenancy.enabled: true`.

| Key | Type | Meaning |
|---|---|---|
| `personalTenant` | object | Present = every person gets one tenant of their own at sign-up. `roles`: the tenant roles they hold in it (⊆ `assignableRoles`; default `[]`). |
| `createTenants` | object | Present = a person may create shared tenants. `creatorRoles`: the tenant roles the creator receives (⊆ `assignableRoles`); `maxPerAccount`: 1–100, default 5. |
| `invitations` | boolean | Owners may invite by link. Requires `createTenants`. |
| `assignableRoles` | identifier[] | The **tenant roles**: the only roles a membership can ever carry. Each must be declared in `auth.roles`. |

**`auth.accessTokens`** — optional; absent = no self-service tokens.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `personal` | boolean | `false` | A person may mint tokens that act as them. |
| `service` | boolean | `false` | A tenant owner may mint tokens that act as the tenant. Requires `selfService`. |
| `maxLifetimeDays` | integer 1–365 | `90` | The longest expiry a token may ask for. |

**Apply-time refusals** (structured, with a fix): `selfService` without tenancy; `invitations` without
`createTenants`; an `assignableRoles` entry not declared in `auth.roles` or naming a built-in role;
`personalTenant.roles`/`creatorRoles` not ⊆ `assignableRoles`; **an `assignableRoles` role named in the
`access` (management) block** — a tenant role must never be able to manage the project; `service` tokens
without `selfService`; an end-user provider without `auth.signUp`.

**Apply-time warning:** with `signUp.mode: open`, every rule on a `global` entity that admits
`authenticated` for a write is listed — those rows are now writable by anyone who signs up.

`auth.providers` keeps its enum. `google` stops being *unhonoured* (#36) in S2; the others keep the
warning until their slice; `local` keeps its meaning (the operator store) until §5.7 widens it. A host that
lists `google` but registers no end-user sign-in is warned **at boot** (S2 plan D11): the warning belongs to the
boot plan, so a runtime apply that adds `google` repeats it only at the next boot.

An entity's per-operation CEL lives under its `rules` key (the schema's name); `access` is the project's
top-level management block, which §3.1's refusals below refer to.

## 4. Prior art adopted, and where Alvo deviates

| Mechanism | Standard adopted |
|---|---|
| Token endpoint, error shape, refresh grant | OAuth 2.0 **RFC 6749** §4–§6; **RFC 9700** (OAuth security BCP) |
| Exchanging the provider's ID token for an Alvo session | OAuth 2.0 Token Exchange, **RFC 8693** |
| Verifying the ID token | **OpenID Connect Core 1.0 §3.1.3.7**; Google's *Verify the Google ID token on your server side* |
| Access token | JWT **RFC 7519**, JWS **RFC 7515** (ES256), JWK Set **RFC 7517**, JWT BCP **RFC 8725**, JWT access-token profile **RFC 9068** |
| Discovery of the issuer's keys | Authorization Server Metadata **RFC 8414** (`issuer`, `token_endpoint`, `jwks_uri`, grant types) |
| Refresh tokens for public clients | rotation with reuse detection (RFC 9700 §4.14) |
| Bearer use on the Data API | **RFC 6750** (`Authorization: Bearer`, `WWW-Authenticate`) |
| Personal access tokens | GitHub fine-grained PATs: owner + expiry + permissions + a recognisable prefix for secret scanning |
| Active tenant per request | Clerk/WorkOS *active organisation*; GitHub's personal namespace as the default |
| Invitations | GitHub/Slack invite links: secret code, expiry, use count, revocable |

**Deliberate deviations:**

- **D1 — A tenant registry now exists.** The F5 dashboard design §2.7 argued none was needed because
  nothing is listed. Self-service creates tenants, so a registry is earned. The Management API **still**
  does not enumerate tenants (F5's D4 stays); a person lists only their own memberships.
- **D2 — End users live apart from the operator store.** `MMLib.Alvo.Identity` stays the store of people
  who *administer* a project; end users are a different population (Supabase `auth.users` vs. platform
  accounts; Firebase Auth vs. IAM). Mixing them would put every app user in the dashboard's access list
  and make a sign-up one bug from an operator. `baas-analyza.md` §2.2 says the built-in `local` provider
  *builds on* ASP.NET Core Identity; when email + password lands (§5.7) it uses Identity's
  `PasswordHasher<T>`, lockout and token providers on the end-user tables.
- **D3 — Not OpenIddict, yet.** Phase 1 issues tokens to Alvo's own clients only; consent, client
  registration and third-party `authorization_code` are the OAuth/MCP slice, which is what OpenIddict
  buys. Phase 1 signs with `Microsoft.IdentityModel.JsonWebTokens` (MIT, first-party, which OpenIddict
  builds on), so a later move changes the issuer, not the tokens.
- **D4 — Token-endpoint errors are `application/problem+json` carrying RFC 6749's `error` and
  `error_description` members.** Alvo's errors are problem documents (§0 agent-first); an OAuth client
  reads `error` from any JSON body. Error codes follow the RFCs: an invalid subject token is
  `invalid_request` (RFC 8693 §2.2.2); an invalid or reused refresh token is `invalid_grant`.
- **D5 — Roles are not in the access token** (analysis §2.3 *Cache rolí v JWT*). A token says who you
  are; what you may do is read from the membership per request, so revocation takes effect on the next
  request.
- **D6 — A refresh grace window that never forks.** Strict reuse detection logs out a person whose two
  tabs refresh at once. A rotated refresh token presented again **within 10 seconds of its rotation**
  receives **the same child** it already produced — not a new branch, so a replay inside the window
  gains nothing a thief did not already have and the family keeps a single tip. Refresh tokens are
  therefore *derivable*: `alvo_rt_<id>_<HMAC-SHA256(key, id)>`, so the server re-derives the child's
  value instead of storing it. After the window, reuse revokes the whole session. Configurable to 0.
- **D7 — The refresh token is returned in the body, not an `httpOnly` cookie** (analysis §2.2 recommends
  cookies for the web). A refresh cookie makes the token endpoint cookie-authenticated — the CSRF surface
  #191 removed from the Data API. Phase 1 relies on rotation, reuse detection and a 15-minute access
  token; an opt-in same-site cookie mode is in §10.
- **D8 — No tenant header means the personal tenant.** For an API key the header only confirms; for a
  person it selects among memberships, and its absence selects the personal tenant if one exists (else
  none). The header can still only name a tenant the caller already holds a grant to.
- **D9 — Role-change audit and auth events are deferred** (analysis §2.2 *Audit auth eventov*, §2.3
  acceptance *Zmena role je auditovaná*). They need the event envelope to carry a tenant (#153) and the
  audit component (#42). This initiative records `created_by`/`joined_at`/`invitation_id` on the rows so
  the later audit has facts to start from; §14 does not claim those two criteria.
- **D10 — Sessions and refresh tokens are not swept yet** (recorded by the S2 final review). Nothing deletes a
  `{prefix}_auth_sessions` or `{prefix}_auth_refresh_tokens` row: every sign-in inserts a session, every refresh a token
  (about 96 rows a day for one session refreshed every 15 minutes), and with `signUp: open` anyone holding a Google
  account can add rows within the token budget. Session stores ordinarily expire such rows; Alvo does not in S2,
  because the sweep has a constraint of its own — it must keep a session's `subject_token_id` for at least the
  ID-token acceptance window (`iat` ≤ 5 min plus 2 min skew), or an ID token becomes replayable once its session row
  is gone — and it is a bounded background job, not a sign-in concern. Follow-up:
  [#527](https://github.com/Burgyn/MMLib.Alvo/issues/527) (rotated tokens and expired or revoked sessions past the
  grace window).

## 5. Accounts and sessions

### 5.1 Concepts and tables

- **Account** — an end user: `UserId`, email + `email_trusted`, display name, avatar URL, `created_at`,
  `last_sign_in_at`, `disabled_at`. Ids use the existing `UserId.New()`/`TenantId.New()` (uuid v4).
- **Identity** — one way an account signs in: `(provider, subject)` unique, the asserted email, whether
  the provider's email is trusted (§5.3). N identities per account; phase 1 creates one.
- **Session** — one sign-in on one device: id, account, `created_at`, `last_refreshed_at`,
  `expires_at` (absolute), `revoked_at`, `revoked_reason`, and the `jti` of the ID token that started it
  (**unique** — an ID token starts at most one session, §5.3).
- **Refresh token** — id, session, parent id, `created_at`, `rotated_at`. Its value is derived (D6); the
  row stores no secret.
- **Signing key** — `kid` (unique), the ES256 private key **encrypted with the deployment's secret
  cipher** (the one `ISecretStore` uses), `created_at`, `retires_at`. Its own table: `ISecretStore` is a
  replace-only name/value store whose names are listable through Management, and keys need
  insert-if-absent and overlap. The same table holds the HMAC key for refresh tokens.

Tables, spelled through `AlvoFrameworkTables`, prefix-aware, both engines: `{prefix}_auth_accounts`,
`{prefix}_auth_identities`, `{prefix}_auth_sessions`, `{prefix}_auth_refresh_tokens`,
`{prefix}_auth_keys`. **Startup refuses** an accounts-enabled host with no secrets encryption key
configured (the existing `Alvo:Secrets:EncryptionKeyFile`), naming the setting — rather than failing on
the first sign-in.

### 5.2 Signing in: the token exchange

```
POST {authPrefix}/token
Content-Type: application/x-www-form-urlencoded

grant_type=urn:ietf:params:oauth:grant-type:token-exchange
&subject_token=<the ID token the provider gave the app>
&subject_token_type=urn:ietf:params:oauth:token-type:id_token
```

`{authPrefix}` defaults to `/auth/v1` and is configurable.

1. **Find the provider by the token's `iss`** among those the applied descriptor lists *and* the host
   configured. None → `invalid_request`.
2. **Validate per OIDC Core §3.1.3.7**: signature against the provider's JWKS (discovery-cached,
   refreshed on an unknown `kid` at most once per 5 minutes); `iss` exact; `aud` contains a configured
   client id; `exp`/`nbf` with ≤ 2 minutes skew; `alg` is the provider's, never `none`.
3. **Freshness and single use**: `iat` no older than 5 minutes; the token's `jti` (or, absent one, the
   hash of the token) is stored on the session it starts and is unique — a replayed ID token starts no
   second session.
4. **Sign-up policy** (§3.1): a new `(provider, sub)` under `invitationOnly` without an invitation, or
   outside `allowedEmailDomains`, is refused (`access_denied`, with a problem `type` naming the policy).
5. **Find or create the account** by `(provider, sub)` — never by email (§5.7). First sign-in inserts the
   account, the identity and, at level 2, the personal tenant with its membership — **one store call,
   one transaction** (the EF adapter owns all of these tables). Later sign-ins refresh profile fields and
   stamp `last_sign_in_at`.
6. **Refuse a disabled account** with the same answer as an unknown one.
7. **Start a session** and answer RFC 6749/8693's response, with `Cache-Control: no-store`:

```json
{
  "access_token": "eyJhbGciOiJFUzI1NiIsInR5cCI6ImF0K2p3dCIsImtpZCI6Ii4uLiJ9…",
  "issued_token_type": "urn:ietf:params:oauth:token-type:access_token",
  "token_type": "Bearer",
  "expires_in": 900,
  "refresh_token": "alvo_rt_…"
}
```

### 5.3 Trusting a provider's email

A provider's `email_verified` is not equally trustworthy everywhere (the *nOAuth* class: a provider that
lets a user set an unverified address). Each provider preset declares when its email is **trusted**:
Google — `email_verified` is true **and** the address ends in `@gmail.com` or the token carries `hd`
(Google's own guidance). A generic OIDC provider — never, unless its configuration says
`TrustEmail: true`. Only a trusted email may satisfy `allowedEmailDomains` or an email-bound invitation,
and nothing ever links accounts by email (§5.7). An untrusted email is stored, shown, and used for
nothing else.

### 5.4 The access token

A JWT per RFC 9068, ES256, header `typ: at+jwt` and `kid`:

| Claim | Value |
|---|---|
| `iss` | `Alvo:Accounts:Issuer` — **required outside Development** (a request-origin default would make it Host-header controlled) |
| `aud` | `Alvo:Accounts:Audience`, default the descriptor `name` |
| `sub` | the account's `UserId` |
| `sid` | the session id |
| `iat`, `exp`, `jti` | 15 minutes by default, configurable 1–60 |
| `client_id` | `alvo` (RFC 9068 requires one; Alvo's own clients share it until client registration exists) |
| `idp` | the provider the session began with (`google`) |

**Nothing else** — no email, roles or tenant (D5). Metadata at
`GET {authPrefix}/.well-known/oauth-authorization-server` (RFC 8414) points at
`GET {authPrefix}/.well-known/jwks.json`.

### 5.5 Refresh, sign-out, the current person

- **Refresh** — `grant_type=refresh_token&refresh_token=…` (RFC 6749 §6). Verify the HMAC, load the row.
  Unrotated → rotate, mint the child. Rotated within the grace window → answer the **same** child again
  with a fresh access token (D6). Rotated after the window → revoke the session (`reuse`) in the same
  transaction, `invalid_grant`; every token of the family is dead before the response is sent. Idle
  limit 30 days, absolute session 180 days, both configurable. Revoked or expired session, or disabled
  account → `invalid_grant`.
- **Sign-out** — `POST {authPrefix}/logout` with the bearer token → revokes its session; idempotent.
- **The current person** — `GET {authPrefix}/user` → the account (id, email, trusted, name, avatar), its
  identities' providers, and its memberships (`tenantId`, `name`, `kind`, `roles`, `isOwner`).
  `PATCH {authPrefix}/user` changes the display name only.

### 5.6 Disabling an account (operators)

A new Management operation, `DisableAccount` / `EnableAccount`, at the `admin` level: stamps or clears
`disabled_at` and revokes every session of the account in the same transaction. Its next request and
next refresh are refused. Analysis §2.2 asks for ban and force-logout; this is both, and the minimum an
operator of an open-sign-up app needs on day one.

### 5.7 How a request becomes a caller

**One reading of credentials, shared by all three sites** that read one today — the Data API filter
(`AlvoContextFilter`), the command gate (`CallerGate`) and the Management filter
(`ManagementCallerFilter`) — through `CallerResolution`:

| Presented | Data API and commands | Management API |
|---|---|---|
| nothing | anonymous, as today | anonymous, as today |
| `X-Alvo-Api-Key` | the API-key resolver, as today | the API-key resolver, **but a personal or service token is refused** (they are app credentials, never project-management ones) |
| `Authorization: Bearer …`, **and a person resolver is registered** | the person resolver | **ignored** (management stays operators and dev keys) |
| `Authorization` with any other scheme, or Bearer with no person resolver registered | **ignored, exactly as today** — a host running its own JwtBearer or a Basic-auth proxy keeps working | ignored |
| `X-Alvo-Api-Key` **and** a Bearer token Alvo owns | **401** — ambiguous; never resolved by precedence | API key as above |

The person resolver, per Data API request:

1. Validate the JWT in memory: signature by `kid`, `iss`, `aud`, `exp` ± 30 s, `typ`.
2. **One store read** returns the account's status, the session's status and — when a tenant is
   requested or the person has a personal tenant — that membership. Disabled account or revoked/expired
   session → refuse the principal (401).
3. Tenant: `X-Alvo-Tenant` names one → it must be a membership, else **refuse the whole principal**
   (401; never a principal without a tenant, the bool trap `TenantResolver`'s remarks name). No header →
   the personal tenant if any, else **no tenant** — a valid caller for `global` entities and for level 1.
4. Mint the `AlvoContext` (§6.2).

The tenant step is a **core service** (`membership resolution`), not part of the bearer resolver, so a
personal token (§7) and a future host-federated identity (§5.8) reach tenants exactly the same way.

### 5.8 More providers and email + password — designed now, built later

Every way to sign in ends in the same function: *a verified identity `(provider, subject, email,
emailTrusted, name, picture)` → sign-up policy → find or create the account → start a session.* Providers
differ only in how they produce the identity:

| Family | Providers | How the identity reaches Alvo |
|---|---|---|
| **ID-token providers** | Google (S2), Microsoft/Entra, Apple, any OIDC issuer (Keycloak, Auth0) | the app signs in with the provider's SDK and sends the ID token to the exchange (§5.2) |
| **Redirect providers** | GitHub (OAuth 2.0, not OIDC); any OIDC provider for an app without an SDK | Alvo runs authorization code + PKCE: `GET {authPrefix}/authorize?provider=…` → provider → `{authPrefix}/callback` → a one-time code the app exchanges (`grant_type=authorization_code` + `code_verifier`) |
| **Credential providers** | email + password, magic link / OTP, passkeys | Alvo verifies the credential itself: sign-up, email verification, reset, then a session |

What phase 1 does to keep that additive:

- An **identity-verifier seam** (internal until a second implementation exists — *public is the
  contract*): `VerifyAsync(subject token) → VerifiedIdentity?`. Google is one preset of a **generic OIDC
  ID-token verifier** (authority + client ids + email-trust rule), so Microsoft, Apple and `oidc` are
  configuration plus a preset.
- The **identities table** holds N identities per account.
- **Email + password** becomes the `local` provider for end users on ASP.NET Core Identity's
  `PasswordHasher<T>`, lockout and token providers, with its own `{prefix}_auth_credentials` table so an
  OIDC-only account has no password row; mail goes through a pluggable sender with a console sender for
  development (analysis §2.2).
- **Linking:** an identity from a second provider is **never** linked by email. Linking is an explicit act
  of a signed-in person (`POST {authPrefix}/user/identities` with the second provider's token). Phase 1
  therefore enforces **no uniqueness on email**.

**#210 inherits** a smaller job: a host-federated resolver maps *subject → account*; the membership
service, roles per tenant and the scope gate are shared.

## 6. Tenants, memberships, roles, invitations

### 6.1 The model

- **Tenant** — `TenantId`, `kind` (`personal` | `shared`), `name`, `created_by`, `created_at`.
  `{prefix}_tenants`.
- **Membership** — `(tenant_id, user_id)` unique, `roles` (names), `is_owner`, `joined_at`,
  `invitation_id`. `{prefix}_tenant_members`.
- **Invitation** — `id`, `tenant_id`, `code_hash` (SHA-256 of a 128-bit code shown once), `roles`,
  `grants_owner`, `email` (optional, matched against a **trusted** email only), `max_uses` (default 1),
  `uses`, `expires_at` (default 7 days, max 30), `created_by`, `revoked_at`.
  `{prefix}_tenant_invitations`.

**A personal tenant has exactly one member, its owner,** forever: no invitations, no leaving, roles fixed
by `personalTenant.roles` (its owner cannot change them), not deletable on its own (§10).

### 6.2 Two kinds of role, and where each counts

This is the review's central correction. `AlvoContext.Roles` today is one set, and three different
authorities read it: entity rules, relation rules on other entities, and the Management API's `access`
levels. A role a person gives themselves by creating a tenant must reach only the first, and only inside
that tenant.

- **Project roles** — what `AlvoContext.Roles` holds today: `anon`/`authenticated`/`admin` plus declared
  roles from an API key or the operator store. Unchanged.
- **Tenant roles** — new: `AlvoContext.TenantRoles`, the membership's role names that the descriptor
  lists in `assignableRoles`, resolved for **the tenant the request acts in**. Empty for every existing
  caller, so nothing changes for API keys or operators.

Where they count:

| Evaluated where | `@user.roles` is |
|---|---|
| a rule of a **`tenancy: scoped`** entity, and every relation term reached from it **while every entity on the path is scoped** | project roles ∪ tenant roles |
| a rule of a **`tenancy: global`** entity, and **everything reached from it** — including a relation term into a scoped entity and that entity's own `list` rule bound into it | project roles only |
| a scoped entity's relation term into a global entity, and anything reached through it | project roles only |
| a Management `access` level | project roles only |

**The rule in one line: a tenant role counts only while every entity on the decision's path is scoped.** A
decision about a global entity must never depend on a tenant role, not even transitively: otherwise a global
`regions` with `update: "jobs.exists(j, true)"` and a scoped `jobs` whose `list` admits `'dispatcher' in
@user.roles` would let anyone who creates a tenant and holds `dispatcher` there update shared reference data.
(Found by the S1 task review; the first version of this table folded by the *related* entity alone.)

**What this rule governs, and what it does not.** It governs every *authorization decision*: entity rules, relation
terms, masks, lookup checks, state-machine transitions, conditions evaluated as the caller, and Management access.
It does **not** govern an automation action the app author declared to run **as `System`**: a scoped entity's
after-hook whose condition admits a tenant role may still fire a `System` action against a global entity, because
running as `System` is the author's explicit, reviewed escalation (spec §3.3 of the product spec), not the caller's
authority leaking. The guides say so next to the hook docs, so an author who writes such a hook knows a tenant
member can trigger it.

Rule authors still write `'dispatcher' in @user.roles` — one spelling. The policy cache key already
separates by entity, and role literals fold per caller at render, so the split is an evaluation-time
union, not a second compile. Belt and braces: apply refuses an `assignableRoles` role in the `access`
block (§3.1), and the Management filter refuses personal and service tokens (§5.7).

**Who manages a tenant is not a role.** *Owner* is the membership's `is_owner` flag, read only by the
self-service routes (members, invitations, service tokens, rename). It is never minted into
`@user.roles`, so no API key, dev key, operator role or descriptor role named `owner` can ever mean it —
and existing descriptors and tests that use `owner` as an ordinary application role keep working. An app
whose rules need "the people who run this tenant" gives them a tenant role (`creatorRoles`).

`admin` is never a tenant role (built-ins are refused in `assignableRoles`).

### 6.3 The self-service API

Mapped under `{prefix}` (default `/api/alvo`), in the core, behind the bearer channel. Every route
requires a **person** (`ActorKind.User` from a session); an API key, a personal token or a service token
is refused with 403 — managing who is in a tenant is a person's act, not an integration's. With the
feature off in the descriptor a route answers `404` with problem type `feature-disabled` naming the
descriptor key that enables it (agents discover it from the OpenAPI document; the routes are always
mapped).

| Route | Who | What |
|---|---|---|
| `GET /tenants` | a person | my memberships |
| `POST /tenants` `{name}` | a person, `createTenants`, under `maxPerAccount` | create a shared tenant; I become owner with `creatorRoles` |
| `PATCH /tenants/{id}` `{name}` | owner | rename |
| `GET /tenants/{id}/members` | member | user id, display name, avatar, roles, owner |
| `PATCH /tenants/{id}/members/{userId}` `{roles?, isOwner?}` | owner | roles ⊆ `assignableRoles` |
| `DELETE /tenants/{id}/members/{userId}` | owner, or the member themself | remove / leave |
| `POST /tenants/{id}/invitations` `{roles, isOwner?, email?, maxUses?, expiresAt?}` | owner, `invitations` | answers the code **once** |
| `GET /tenants/{id}/invitations` | owner | list, never the code |
| `DELETE /tenants/{id}/invitations/{invitationId}` | owner | revoke |
| `POST /invitations/preview` `{code}` | anyone holding the code | tenant name, inviter's display name, roles, expiry |
| `POST /invitations/accept` `{code}` | a person | join; idempotent for an existing member (roles unchanged) |

Codes travel in bodies, never in a URL, so they cannot land in an access log.

### 6.4 Invariants (each an adversarial test)

- The **last owner** of a shared tenant cannot leave, be removed, or lose `isOwner`.
- An invitation grants only `assignableRoles`. One that grants ownership is **single-use** and lives at
  most 7 days.
- **An owner's invitations die with their ownership**: removing or demoting an owner revokes the
  invitations they created, in the same transaction.
- Expired, revoked, used-up, or email-bound-to-someone-else invitations all answer **the same 404**.
- `uses < max_uses` is enforced by an atomic conditional update (`UPDATE … SET uses = uses + 1 WHERE id = …
  AND uses < max_uses AND revoked_at IS NULL AND expires_at > now`), never read-then-write.
- Under `signUp.mode: invitationOnly`, accepting is the only way an account is created: the exchange
  carries the code (`invitation_code` parameter, an RFC 8693 extension parameter) and account, identity
  and membership commit together.
- A person's tenant roles in A are never present while acting in B, never on a global entity, never in
  Management.
- Removing a membership refuses that person's **next** request in the tenant and every personal token
  they minted for it.
- `POST /tenants` and the invitation routes are rate-limited (§7.3).

### 6.5 Levels 2 and later switches

At level 2 the first sign-in creates the personal tenant in the account's transaction. A descriptor that
adds `personalTenant` after people signed up creates each missing one lazily, on that person's next
exchange or refresh.

### 6.6 What existing tenant sources keep doing

API keys keep their single tenant and the header-as-confirmation rule; the operator cookie keeps the
single tenant an administrator granted. Neither consults memberships. Tenants created by API-key
workflows (bare uuids today) are **not** in the registry and need not be: the registry is the source of
truth for *people*, not a precondition for a `tenant_id` value.

**`IAlvoTenantCatalog`:** a default over the registry is registered **only when `selfService` is on and
the host registered none** — so the existing "an `EachTenant` schedule without a catalog refuses boot"
check keeps firing everywhere else. Its documentation states plainly that it lists self-service tenants
only; a deployment mixing API-key tenants with self-service ones registers its own catalog.

Where the refusal fires moved, and that is a **recorded deviation** (S3 Task 9): an `EachTenant`
schedule on a host with a registry and no catalog of its own is now judged *per descriptor*, not in the
boot's catalog-free pass, because a `selfService` descriptor cures it — and every EF provider registers
the registry, so the registry's presence says nothing about intent. Concretely: (a) a relational host
with no descriptor at all now boots, the schedule unbound; (b) its first apply without `selfService`
only warns, as for any extension that was never bound; (c) the next restart then stands down not-ready
on that stored descriptor; (d) a host booting from a stored descriptor that cannot bind the schedule
stands down not-ready instead of exiting — the existing treatment of every per-descriptor extension
problem. A file (GitOps) boot still exits with the same locator and fix, an apply that drops
`selfService` while such a schedule is bound is refused as an unbinding, and a host with no registry
still refuses at the catalog-free pass. The reason: keeping the refusal in the catalog-free pass would
make a dashboard-first self-service host with such a schedule unbootable — its first boot has no
descriptor, and the descriptor that cures it can only arrive after the boot.

## 7. Tokens for integrators

### 7.1 Two kinds

| | Personal token | Service token |
|---|---|---|
| Acts as | the minting person, in one tenant (or none at level 1) | the tenant |
| `@user.id` | the person | the token's own `UserId` (a fresh one per token; it fills `created_by`) |
| Roles | **live**: the person's current tenant roles there, re-read per request (§5.7 membership resolution) | fixed at mint: ⊆ the minting owner's tenant roles then; at least one |
| Dies when | expiry, revocation, the membership ends, the account is disabled | expiry, revocation, **the minting owner is removed or demoted** |
| Who mints | a person, from a session | an owner, from a session |
| Scopes | entity × read/write (`ApiKeyScope`), at least one | same |
| Max lifetime | `maxLifetimeDays` | `maxLifetimeDays` |

**Why a service token dies with its minter's ownership.** A token that outlives the person who chose its
power is the anti-goal's case: a demoted owner who kept the secret keeps the access. Rotation is the
remaining owners' explicit act — re-mint, update the integration — which is also what makes them
re-decide its scopes. The token list shows each token's minter so owners see what a removal will cut.

Both are **API keys** on the existing `X-Alvo-Api-Key: keyId.secret` channel, over a **persisted**
`IApiKeyStore` (EF, both engines) with the in-memory dev-key store as the fallback for configured dev
keys. Key ids are prefixed `alvo_pat_…` / `alvo_svc_…` for secret scanning. `ApiKeyRecord` is widened
compatibly with its kind and minter; a personal token stores no roles, and its resolution goes through
membership resolution rather than `RoleNames`.

### 7.2 The self-service routes

| Route | Who |
|---|---|
| `POST {prefix}/tokens` `{name, kind, tenantId?, roles? (service), scopes, expiresAt}` | a person (`personal`) or an owner (`service`) — **never a token** |
| `GET {prefix}/tokens` | my personal tokens, plus service tokens of tenants I own: name, kind, minter, scopes, created, expiry, last used — never the secret |
| `DELETE {prefix}/tokens/{keyId}` | the minter; for service tokens any owner of the tenant |

`expiresAt` is required and ≤ `maxLifetimeDays`. The secret is answered once and stored as its SHA-256.
Scopes naming an entity the descriptor lacks are refused with the list of entities.

### 7.3 Abuse limits and CORS

**Rate limits** live in the core, on ASP.NET Core's rate limiter (in the shared framework), as named
policies Alvo registers from `Alvo:Api:RateLimits` and attaches to its own routes — so an embedded host
gets them too. Defaults per client IP: token endpoint 30/min, invitation routes 20/min, tenant creation
and token minting 10/min.

**CORS** lives in the core too: a named policy from `Alvo:Api:Cors:Origins` applied to the Data API,
the self-service routes and the auth routes; no origins = no CORS headers (today's behaviour).
Credentials are never allowed (bearer only), so a wildcard origin is refused at startup.

## 8. Security prerequisites and review

- **#161 closes first (S0).** With self-service anyone owns a tenant, so a scoped `ref` that may name
  another tenant's row becomes a cross-tenant write primitive anyone can use.
- **#198** stays open. Its oracle answers "does a row with this uuid exist", and self-service widens who
  might hold such a uuid (a former member knows their old tenant's ids) — but a former member already
  knew those rows existed; the oracle tells them nothing new about anyone else. Recorded here, not
  hidden.
- Every slice touching the resolver, tenancy, roles or tokens runs the `alvo-security-core-review`
  checklist; the stack ends with reviewer passes standing in for `/security-review` and
  `/code-review high` (user-only commands), labelled as substitutes.
- **Secrets never reach a log**: ID tokens, access and refresh tokens, invitation codes and token
  secrets are redacted wherever a record prints itself (`ApiKeyRecord.PrintMembers` precedent), and a
  test scans captured logs for each.

## 9. Sharing across tenants — sketched, not built

Spolu's events span families; Kruho's linked households share one album; TimeTracker shares a client by
link. These are **grants**:

- A grants table — *(entity, row id) → grantee tenant or person, operations, expiry* — read by a relation
  term, compiled to the same `EXISTS` relation rules render.
- The tenant guard becomes *"`tenant_id = @tenant.id` **or** a grant names me"* — a deliberate, audited
  punch-through in `PolicyEngine`, with its own adversarial suite. Nothing here weakens the guard.
- A public link is an anonymous principal bound to one row and an expiry — the token machinery with a row
  scope.

## 10. Later slices

Account and tenant deletion (GDPR; app stores require account deletion); auth and membership events
(D9); an admin dashboard view of end users and tenants; the opt-in refresh cookie (D7); email + password
and the other providers (§5.8); cross-tenant grants and public links (§9); OAuth consent + MCP (D3);
`@user.email` and typed claims (#37); `field.default: {"$cel": "@user.id"}` (#113).

## 11. Delivery — slices, each its own PR, stacked

Each store, port and table lands **in the slice that consumes it** — nothing is created ahead of its
caller.

| Slice | Content | Unlocks |
|---|---|---|
| **S0** | #161: composite `(tenant_id, id)` FK for scoped → scoped `ref`s; migration diff + destructive scan; two-tenant adversarial fact. Carries this design and the plan. | safe self-service |
| **S1** | Role provenance: `AlvoContext.TenantRoles`; the scoped/global/management split of §6.2 in `PolicyEngine`, relation binding and the management evaluator; one credential reading for the three sites (`CallerResolution`), including the "Bearer is ignored unless a person resolver is registered" rule and the management refusal of app tokens. No feature yet; every existing suite green. | the security groundwork |
| **S2** | `MMLib.Alvo.Accounts`: accounts, identities, sessions, refresh tokens, keys (stores + DDL); the exchange, refresh, logout, `/user`, JWKS, RFC 8414 metadata; the generic OIDC verifier + Google preset; `auth.signUp`; the person resolver with membership resolution returning *no tenant*; `DisableAccount`; CORS and rate limits in the core; `google` honoured. | **level 1** |
| **S3** | Tenants and memberships (stores + DDL); `tenancy.selfService` (schema, apply refusals and warning); membership resolution with tenants and tenant roles; `/tenants` and member routes; personal tenant at sign-up and lazily; the conditional default catalog. | **levels 2–3** minus invitations |
| **S4** | Invitations, including `invitationOnly` sign-up. | **level 3** |
| **S5** | `auth.accessTokens`; the persisted API-key store; personal and service tokens; `/tokens`. | integrators |
| **S6** | The field-service demo grows self-service (§12) with its TeaPie collection; the docs (§13); the `alvo-descriptor-*` skills learn the new keys. | — |

Route-adding slices (S2–S5) update the OpenAPI path pins the TeaPie suites hold by equality **in the same
slice**. Test discipline per step: the affected test project with a filter in the inner loop; ring0
through the pre-commit hook; ring1 once at the end of a slice; ring2 once before its PR; the TeaPie stacks
only for a slice that changes routes or the demo.

### 11.1 Where the code lives

- **`MMLib.Alvo.Abstractions`** — the ports each slice needs, and `AlvoContext.TenantRoles`.
- **`MMLib.Alvo` (core)** — descriptor keys and apply checks; role provenance; credential reading;
  membership resolution; the self-service, invitation and token routes; CORS and rate limits.
- **`MMLib.Alvo.Data.EntityFrameworkCore`** — the stores and DDL, beside every other framework table.
  Composite writes (sign-up with a personal tenant, accepting an invitation under `invitationOnly`) are
  single store calls so one transaction covers them.
- **`MMLib.Alvo.Accounts` (new package)** — sign-in: the token endpoint, sessions, keys, JWT, the OIDC
  verifier, JWKS and metadata, `/user`, the person resolver. **Earned by rule (a)** of
  `package-boundary.md` — it brings `Microsoft.IdentityModel.JsonWebTokens` and
  `Microsoft.IdentityModel.Protocols.OpenIdConnect`, which an embedded host wanting only the Data API
  must not acquire — **and rule (b)**: sign-in is a real swap point; a host with its own identity
  provider (#210) replaces exactly this package. It references Abstractions and the core; the standalone
  host references it; `AddAlvoAccounts()` / `MapAlvoAccounts()` wire it into an embedded host.

### 11.2 Configuration (standalone host)

```jsonc
"Alvo": {
  "Accounts": {
    "Issuer": "https://api.example.com/auth/v1",  // required outside Development
    "Audience": "field-service",                  // default: descriptor name
    "AccessTokenMinutes": 15,
    "RefreshIdleDays": 30,
    "SessionLifetimeDays": 180,
    "RefreshReuseGraceSeconds": 10,
    "Providers": {
      "Google": {
        "ClientIds": [ "1234-web.apps.googleusercontent.com", "1234-ios.apps.googleusercontent.com" ]
        // "Authority": "https://accounts.google.com" — overridable, for a test issuer
      }
    }
  },
  "Api": {
    "Cors": { "Origins": [ "https://app.example.com" ] },
    "RateLimits": { "Token": 30, "Invitations": 20, "Create": 10 }
  },
  "Secrets": { "EncryptionKeyFile": "/run/secrets/alvo-secrets-key" }   // required with Accounts
}
```

An `http://` authority is refused at startup unless that provider sets `AllowHttpMetadata: true`, which
logs a warning on every start — it exists for the end-to-end stack's test issuer only. **`AllowHttpMetadata` itself
is refused at startup outside the Development environment**, exactly as an unset `Issuer` is, with a message naming
the setting and why (S2 plan-guard ruling): a public switch that turns a security control off must not be reachable
from a production configuration by one stray key. Only the accounts package reads the host environment, so the
end-to-end stack (§12) runs Development without changing any other behaviour.

## 12. The demo: field-service gets self-service

`examples/field-service` is a multi-tenant dispatch backend whose tenants and people are today dev API
keys and constants in `demo-identities.env`. It becomes the level-3 showcase **without losing an
existing test**: API keys keep working beside sign-in, which is itself a property worth proving.

- The descriptor gains `providers: ["google"]`, `signUp: {mode: "open"}`, `selfService` with
  `createTenants: {creatorRoles: ["dispatcher"]}`, `invitations`, `assignableRoles: ["dispatcher",
  "technician"]`, and `accessTokens` with `personal` and `service`.
- The compose stack gains a **test OIDC issuer**: the WireMock `sink` serves a discovery document and a
  static JWKS; the TeaPie suite mints ID tokens in a `.csx` script with the matching test private key
  (committed under `test/`, labelled test-only). Alvo's Google provider points at it through `Authority`
  + `AllowHttpMetadata`, so the stack's Alvo runs in the Development environment (§11.2).
- A new TeaPie collection, **`170-SelfService`**: a dispatcher signs in → creates *Acme Repairs* →
  invites a technician → the technician accepts and sees only the work orders assigned to them → the
  dispatcher mints a service token for billing, which reads completed jobs → the dispatcher removes the
  technician, whose next request is refused → a refresh token reused after the grace window kills the
  session. And the adversarial half: a header naming a tenant you are not in; a role from tenant A absent
  in B and on the global `regions`; a self-made dispatcher cannot write `regions`; expired/revoked/used-up
  invitations answering the same 404; a personal token dying with its owner's membership; a token trying
  to mint a token; a personal token refused by the Management API.

## 13. Documentation

Written for a person reading top to bottom — each page opens with what you can build, shows the smallest
working thing, then explains why it behaves as it does:

- **Guide: "Sign-in and accounts"** — the Google set-up (client id, authorised origins), the exchange
  from a browser in ~20 lines of plain `fetch`, refresh, sign-out, where to keep the tokens and why (D7),
  sign-up policies, the three levels with their descriptors.
- **Guide: "Tenants, members and invitations"** — choosing a level, personal vs shared tenants, the tenant
  switcher (`GET /auth/v1/user`), invitation links end to end, tenant roles vs project roles and why a
  tenant role never reaches global data or Management, what an owner is.
- **Guide: "Tokens for integrators"** — personal vs service tokens, scopes, what happens when someone
  leaves.
- **Reference** — the descriptor keys (DocsGen from the schema) and the routes (from OpenAPI).
- **`docs/architecture/accounts.md`** — the contributor view: tables, the resolution pipeline, role
  provenance, the invariants and the tests that hold them, the deviations of §4.
- `examples/field-service/README.md` gains a *Self-service* section; `docs/PLAN.md` records the initiative.

## 14. Acceptance criteria

From `baas-analyza.md` §2.2, §2.3 and §4, made concrete (the two audit criteria are deferred, D9):

1. End to end: exchange → Data API → refresh → sign out → the old refresh token is dead; reuse after the
   grace window kills every token of the session before the response returns; reuse inside it returns
   the same child.
2. JWT validation does no I/O; one store round trip resolves account, session and membership.
3. Role and membership changes take effect on the next request.
4. Nobody changes their own role; an owner cannot grant outside `assignableRoles`; nobody grants `admin`;
   a tenant role never reaches a global entity or the Management API.
5. Two-tenant adversarial suite across the Data API, relation rules, rollups, hooks and the new routes;
   a scoped `ref` cannot name another tenant's row (S0).
6. No secret appears in logs.
7. Every existing suite — rings, both TeaPie stacks, the admin e2e — stays green; API keys, the operator
   cookie, and a host's own `Authorization` header behave exactly as before.

## 15. Left to the builder

Class and file names; EF mappings; problem-type URIs (following `ProblemResultFactory`); rate-limit
partitioning details; contract-test shapes (the `MMLib.Alvo.Testing` pattern); test names; how
`ApiKeyRecord` is widened compatibly; whether a port is public or internal-with-`InternalsVisibleTo`,
decided by the `alvo-architecture-rules` *public is the contract* rule. Anything that changes a decision
above comes back to this document first.

## 16. Review log (2026-11-13, adversarial builder check)

| Finding | Resolution |
|---|---|
| B1 tenant roles unlock Management (`access` CEL over `@user.roles`; same resolver; `owner` used as an admin level in tests) | §6.2 role provenance; management reads project roles only; apply refuses tenant roles in `access`; Management refuses app tokens (§5.7) |
| B2 tenant roles honoured on global entities | §6.2: global entities and relation terms into them see project roles only |
| B3 a built-in `owner` would be mintable by name and breaks fixtures | dropped; ownership is a membership flag read only by self-service routes (§6.2) |
| B4 service tokens outlive their minter's authority | die when the minter is removed or demoted; never carry ownership; cap ≤ 365 days (§7.1) |
| B5 grace window forks the family | derivable refresh tokens; the window re-issues the same child (D6) |
| B6 open sign-up unguarded; no quotas; no ban | `auth.signUp` required, `invitationOnly`, `allowedEmailDomains`; `maxPerAccount`; global-write warning; `DisableAccount` (§3.1, §5.6) |
| B7 default catalog would silence a boot check and skip API-key tenants | registered only with `selfService` and no host catalog; documented (§6.6) |
| B8 Bearer channel breaks hosts with their own `Authorization`; three read sites | Bearer is Alvo's only when a person resolver is registered; other schemes ignored; one reading for all three sites (§5.7) |
| B9 signing keys do not fit `ISecretStore` | own table, insert-if-absent, encrypted with the secret cipher, startup refusal without a key (§5.1) |
| B10 invitations: code in URL, outlive ownership, multi-use owner invites, races, email trust | POST bodies; revoked with ownership; owner invites single-use ≤ 7 days; atomic update; per-provider email trust (§5.3, §6.3–6.4) |
| B11 ID-token replay; wrong RFC 8693 error; Host-controlled `iss`; no metadata; `amr` | `iat` ≤ 5 min + unique `jti` per session; `invalid_request`; `Issuer` required outside Development; RFC 8414; `idp` claim (§5.2, §5.4) |
| B12 unrecorded deviations; uuid v7 claim | D9 records the deferred audit; #198 reasoning restated; ids are the existing v4 (§5.1, §8) |
| B13 slices not independently buildable | stores move into their consuming slices; S1 is the role-provenance groundwork; pins updated per slice (§11) |
| Builder Q1–Q8 | one store read (§5.7); no membership → no tenant unless a tenant was requested (§5.7); personal tenant roles fixed (§6.1); service-token `UserId` per token (§7.1); untrusted email stored, used for nothing (§5.3); composite writes are one store call (§11.1); rate limits and CORS in the core (§7.3); Management never takes Bearer (§5.7) |
