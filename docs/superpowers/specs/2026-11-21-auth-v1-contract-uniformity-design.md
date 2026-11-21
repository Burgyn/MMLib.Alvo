# The `/auth/v1` contract before v0.1: names, credential answers, rate-limit keys, `auth.accessTokens`, idempotency — design

**Status:** proposed — item 4's name is the maintainer's call and is **not decided** here · **Date:** 2026-11-21 ·
**Branch:** `debt/542-auth-contract` · **Issue:** [#542](https://github.com/Burgyn/MMLib.Alvo/issues/542) (milestone F6 — v0.1)

Built on: the accounts spec `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md`
(§3.1, §6.3, §7, §14), the contributor page `docs/architecture/accounts.md` (S3 D7/D14, S4 D11/D13, S5 D2/D15/D16), the
Data API's idempotency design (`docs/architecture/data-api.md`, *`Idempotency-Key`: what is stored*), the command ledger
(`docs/architecture/commands.md`, *The idempotency ledger*) and #539, merged as `2d5b7441`, which already unified one
arm of item 2.

## 1. Intent

The whole-stack review of the accounts initiative (stack review C, findings I1, I2, I4, I5, I7) found five places
where the `/auth/v1` HTTP contract says one thing two ways. None is a security defect. All of them freeze at v0.1:
the descriptor schema (`schema/project.schema.json`) and the wire shapes are what agents and app authors will copy.
Alvo has no users yet (pre-v0.1), so every recommendation below may break the current shape. Each break is listed for
the CHANGELOG (§8), and no compatibility shim is proposed.

This note settles each item in four parts:

- the current state, with `file:line` from `origin/main` at `2d5b7441`;
- the options and the recommendation, with its reason and the prior art it follows;
- every deliberate deviation from that prior art;
- the wire before and after, and the blast radius.

§9 is the slice list.

**Out of scope:** the OAuth token endpoint's own error shapes (S2 D4, settled), the sign-in flow, and anything
Management answers. Management is a different surface with its own scheme (§3.4 explains why its 403 for app tokens
stays).

## 2. Item 1 — one shape for a person, one key per role

### 2.1 Current state

A person appears four times across `/auth/v1`, in three shapes and under three names for the display name:

| Where | Wire | Source |
|---|---|---|
| `GET/PATCH /user` | `{ "id", "name", "email", … }` — the person themself | `src/MMLib.Alvo.Accounts/Internal/UserResponse.cs:27` |
| `GET /tenants/{id}/members` | `[{ "userId", "name", "avatarUrl", "roles", "isOwner" }]` — the member is the person | `src/MMLib.Alvo/Tenants/SelfService/MemberResponse.cs:16-17` |
| invitation create 201 and list | `"createdBy": "5b1f…"` — a bare id | `src/MMLib.Alvo/Tenants/SelfService/InvitationResponse.cs:42` |
| invitation preview (anonymous) | `"inviterName": "Dana Novak"` — a bare name, no id | `src/MMLib.Alvo/Tenants/SelfService/InvitationPreviewResponse.cs:21` |
| token list | `"mintedBy": { "id", "displayName" }` — an object, with a third spelling of the name | `src/MMLib.Alvo/Tenants/SelfService/AppTokenListItem.cs:35`, `:80-81` (`AppTokenMinter`) |

One role appears under three keys: the person who made the resource is `createdBy` on an invitation, `mintedBy` on a
token and `inviter…` on a preview. A second, smaller inconsistency sits beside it. An invitation's 201 is its list
item plus `code`, which S4 D11 chose so that "a client parses one shape for created and listed invitations". A token's
201 (`AppTokenResponse.cs:33-43`) is **not** its list item plus `token`. It lacks `mintedBy`, `lastUsedAt`,
`revokedAt` and `status`.

The store already joins the display name for two of the three. `AppTokenTables.cs:44` selects `a.display_name` for the
list (`AppTokenView.MinterName`), and `InvitationTables.cs:146` does the same for the preview
(`InvitationPreview.InviterName`). The invitation list does not: `InvitationView` (`TenantInvitations.cs:42-44`) carries
`CreatedBy` only.

### 2.2 Prior art

- **The display name.** OpenID Connect Core 1.0 §5.1's standard claim is `name` ("End-User's full name in displayable
  form"). Alvo's account takes its display name from exactly that claim of the ID token (S2), and `PATCH /user`'s body
  is `{ "name" }` (`DisplayName.cs:8`). Microsoft Graph and the Google APIs spell it `displayName` instead (Graph's
  `identitySet.user.displayName`, Drive's `User.displayName`). GitHub's user object says `name`.
- **A reference to a person.** GitHub always embeds the same *simple user* object and names the key after the role on
  that resource: `inviter` on an organisation invitation, `creator` on a deployment, a milestone or a project, `user` on
  an issue. Stripe references every object by its id string and expands it on request (`expand[]`). It exposes no
  person objects in its core API. Microsoft Graph uses one key for one role on every resource: `createdBy` and
  `lastModifiedBy`, each an `identitySet`.

### 2.3 Options

| | Display name | Creator key | Creator shape |
|---|---|---|---|
| **A** (recommended) | `name` everywhere | `createdBy` on every resource | `{ "id", "name" }`; the anonymous preview `{ "name" }` |
| B | `name` everywhere | per resource, GitHub-style: `inviter`/`createdBy` (invitations), `minter`/`mintedBy` (tokens) | `{ "id", "name" }` |
| C | `displayName` everywhere | `createdBy` | `{ "id", "displayName" }` |
| D | as today | as today | document the differences |

**Recommendation: A.**

- **`name`, because three of the four sites already say it and the source is OIDC's `name`.** Moving to `displayName`
  (C) would rename `/user`, the member list, `PATCH /user`'s body and the tenant name's sibling field to follow one outlier.
- **One key, `createdBy`, because Alvo's resources share one role.** Every self-service resource has exactly one person
  behind it: the person whose session made it. `createdAt` already sits beside that person on both invitations and
  tokens, and the Data API's audit column for the same fact is `created_by`. GitHub's per-resource naming (B) earns
  its keep because GitHub's roles do differ per resource (an issue's author is not a deployment's creator); Alvo's do
  not. *Mint* stays the verb in the prose, the routes and the code. It is not a second wire key.
- **An object, not a bare id, because `GET /tokens` already ships one.** The list exists to answer "who answers for
  this?", which the spec says outright (§7.1: "The token list shows each token's minter so owners see what a removal
  will cut"). That takes a name, not a GUID the client must resolve through a member list it may not be allowed to
  read. Stripe's expansion (D's cousin) would be a second way to fetch the same field.
- **The members list stays flat** (`userId`, `name`, `avatarUrl`). A member *is* the person, not a reference to one.
  `userId` mirrors `tenantId` on a membership seen from the other side, and it is the path segment its routes take
  (`/members/{userId}`).

**The anonymous preview never exposes more than today.** Today it gives the inviter's display name and no id. Under A
it gives `createdBy: { "name": … }`, so the key and the path to the name are the same as on the authenticated list,
and `id` is **omitted**, not `null`. An account id is a stable identifier. Members see it, but a code holder who has
not signed in is not a member. A `null` would read as "this person has no id". The schema publishes two named
shapes, `PersonReference { id, name }` and `PersonName { name }`, so a generated client sees the difference rather than
an optional `id`. `name` stays nullable in both, as it is on `/user` today. A person with no display name answers
`"name": null`, never a missing `createdBy`.

**And the token's 201 becomes its list item plus `token` and `header`** (S4 D11's reasoning, applied to tokens). It then
carries `createdBy` (the caller themself), `lastUsedAt: null`, `revokedAt: null` and `status: "active"`. §6 needs this
too: a replayed mint answers that shape with the token's *current* state.

### 2.4 Deviations

- **From GitHub:** one key across resources instead of one per role. The reason is above.
- **From Graph and Google:** `name`, not `displayName`, following OIDC Core §5.1, which is where the value comes from.
- **The preview omits `id`.** This is a recorded narrowing, not an oversight. It keeps S4 D8's "a preview needs no
  account" from becoming "a preview discloses an account id".

### 2.5 Wire

```http
GET /auth/v1/tokens                                     (before)
200 [ { "keyId": "alvo_svc_7f…", …, "mintedBy": { "id": "5b1f…", "displayName": "Dana Novak" }, "createdAt": "…", … } ]

GET /auth/v1/tokens                                     (after)
200 [ { "keyId": "alvo_svc_7f…", …, "createdBy": { "id": "5b1f…", "name": "Dana Novak" }, "createdAt": "…", … } ]
```

```http
POST /auth/v1/tenants/3c…/invitations                    (before)
201 { "id": "b1…", "code": "alvo_inv_Qm9…", …, "createdBy": "5b1f…", "createdAt": "…", "status": "active", "revokedAt": null }

POST /auth/v1/tenants/3c…/invitations                    (after; the list item is the same minus "code")
201 { "id": "b1…", "code": "alvo_inv_Qm9…", …, "createdBy": { "id": "5b1f…", "name": "Dana Novak" }, "createdAt": "…", … }
```

```http
POST /auth/v1/invitations/preview                       (before)
200 { "tenantName": "Acme", "inviterName": "Dana Novak", "roles": ["technician"], "isOwner": false, "expiresAt": "…", "emailBound": false }

POST /auth/v1/invitations/preview                       (after)
200 { "tenantName": "Acme", "createdBy": { "name": "Dana Novak" }, "roles": ["technician"], "isOwner": false, "expiresAt": "…", "emailBound": false }
```

```http
POST /auth/v1/tokens                                    (after; before lacked the last four members)
201 { "keyId": "alvo_pat_q8…", "token": "alvo_pat_q8….Zp…", "header": "X-Alvo-Api-Key", "kind": "personal",
      "name": "nightly export", "tenantId": "3c…", "roles": [], "scopes": ["jobs:read", "jobs:write"],
      "createdBy": { "id": "5b1f…", "name": "Dana Novak" }, "createdAt": "…", "expiresAt": "…",
      "lastUsedAt": null, "revokedAt": null, "status": "active" }
```

`tenantName` stays. Tenants are referenced flat everywhere (`tenantId` on memberships and tokens), so there is no
tenant object to align it with. Introducing one here would create the very inconsistency this item removes.

### 2.6 Blast radius

- **Code.**
  - One internal `PersonReference(Guid Id, string? Name)` and `PersonName(string? Name)` in `Tenants/SelfService`. They
    replace `AppTokenMinter` (`AppTokenListItem.cs:79-81`).
  - The changed response types: `InvitationResponse.CreatedBy`, `InvitationPreviewResponse.InviterName` →
    `CreatedBy`, `AppTokenListItem.MintedBy` → `CreatedBy`, and `AppTokenResponse`, which gains the list members.
  - The store: `InvitationView` gains `CreatorName` (internal to Abstractions). The EF adapter's invitation list and
    create-returning reads `LEFT JOIN` the accounts table, as the preview's SQL already does (`InvitationTables.cs:146`).
    One SQL text serves both engines. `AppTokenView.MinterName` keeps its internal name.
- **OpenAPI.** `AccountsSchemas.Invitations.cs` and `AccountsSchemas.Tokens.cs` change, plus two shared schemas. The
  accounts snapshot `AccountsOpenApiTests.The_document_with_sign_in_is_stable.verified.txt` moves; the snapshot judge
  will be dispatched.
- **Tests.**
  - `TokenRoutesTests` (the list and mint shape facts, including
    `…The_list_shows_my_tokens_and_my_tenants_service_tokens_never_a_secret`) and `AppTokenResponseTests` (the 201
    shape; `PrintMembers` still masks `token`).
  - `InvitationRoutesTests.An_owner_invites_and_the_code_is_answered_once`, `InvitationAcceptTests` (the preview facts),
    and `InvitationStoreFacts` (the creator's name, on both engines).
  - New: the preview's key set never contains `id`, and a person with no display name answers `"name": null`.
- **TeaPie.** `test/teapie-field-service/170-SelfService/002-invite-and-accept-test.csx:50,56` (preview key set and
  `inviterName`) and `004-billing-token-test.csx:75-76` (`mintedBy.id` / `.displayName`).
- **Guides.** `website/src/content/docs/guides/tokens.mdx:197`, `tenants-and-invitations.mdx:241`.
- **Docs.** `docs/architecture/accounts.md:529,533,786-788,822`. The generated reference regenerates.
- **Skills, admin dashboard, assistant prompts:** none. No `plugins/alvo/skills` file quotes these bodies,
  `src/MMLib.Alvo.Admin` does not call `/auth/v1`, and the schema assistant never sees a route body.
- **Public API:** none. Every type involved is internal.

## 3. Item 2 — one answer for the wrong kind of credential

> **Implemented** (slice 1, accepted as recommended). One reading, `CallerResolution.PersonBearer`, serves
> `UserEndpoints` and `SelfServicePerson`; its refusal switch is private and names every channel. Held by
> `PersonBearerTests` (the reading, row by row), `TokenRoutesTests.An_api_key_of_any_kind_is_one_bare_bearer_challenge_on_every_person_only_route`
> (16 routes × 6 keys → one answer) and `TenantRoutesTests.A_bearer_a_host_resolves_to_no_person_is_403_naming_no_route`.

### 3.1 Current state, after #539

#539 moved the arms the person-only sites share into one helper, `CallerResolution.PersonOnlyRefusal`
(`src/MMLib.Alvo/Auth/Internal/CallerResolution.cs:271-281`). That helper handles a malformed or ambiguous Bearer, an
app token sent as a Bearer, and no credential at all. **The one arm it does not own is the API key, and that is where
the two sites disagree:**

| Presented on a person-only route | `/user`, `/logout` (`UserEndpoints.cs:133-138`) | tenants, members, invitations (owner routes and `accept`), tokens (`SelfServicePerson.cs:55-60`) |
|---|---|---|
| `X-Alvo-Api-Key` (a configured key, or a personal or service token) | falls to `PersonOnlyRefusal`'s default → **401**, `WWW-Authenticate: Bearer`, `BearerRequired()` (`ProblemResultFactory.cs:129`) | **403** `forbidden`, `PersonForbidden(NotAPerson)` (`SelfServicePerson.cs:58`) |
| a Bearer a host's own person resolver resolves to a non-person | n/a (only Alvo's `PersonSessions` reads it) | **403** `forbidden`, the same sentence (`SelfServicePerson.cs:71`) |
| an API key **and** an Alvo-owned Bearer | 401 `Bearer error="invalid_request"`, generic bearer detail (`CallerResolution.cs:276`) | the same |

Three things are still off:

1. **The status differs by route** for the same request shape.
2. **The 403 sentence is about tenants on every route**, the token routes and invitation accept included. It reads
   "Only a signed-in person manages tenants. …" (`SelfServicePerson.cs:38-40`), and TeaPie pins it on a *mint*
   (`010-tokens-reach-test.csx:9`). The OpenAPI document repeats it on `GET /tokens` and `DELETE /tokens/{keyId}`
   ("only a signed-in person manages tenants").
3. **An ambiguous request on a person-only route gets the generic "bearer token could not be used" detail.** The
   Data API answers the same request with `AmbiguousCredential`, whose detail names the actual mistake
   (`CallerResolution.cs:257`, `ProblemResultFactory.cs:143`).

The 403 also asserts something the server never checked. `SelfServicePerson` never resolves the API key: a revoked
key, a made-up one and a live service token all get the same "forbidden". RFC 9110 §15.5.4 reads a 403 on a request
with credentials as "the server considers them insufficient". Here they were never considered.

### 3.2 Prior art

- **RFC 9110 §15.5.2 (401):** the request "lacks valid authentication credentials for the target resource", and the
  server "MUST send a `WWW-Authenticate` header field containing at least one challenge applicable to the target
  resource".
- **RFC 9110 §15.5.4 (403):** the server "understood the request but refuses to fulfill it … If authentication
  credentials were provided in the request, the server considers them insufficient to grant access. The client SHOULD
  NOT automatically repeat the request with the same credentials."
- **RFC 6750 §3.1:** "If the request lacks any authentication information (e.g., the client was unaware that
  authentication is necessary **or attempted using an unsupported authentication method**), the resource server SHOULD
  NOT include an error code or other error information" — a bare `WWW-Authenticate: Bearer`. `insufficient_scope` →
  403 is for a token *of this scheme* that lacks privileges.
- **GitHub:** an installation (integration) token on a user-only endpoint such as `GET /user` is
  `403 Resource not accessible by integration`. **That precedent is about a credential of the route's own scheme.**
  GitHub sends every token kind in `Authorization`, so the route accepted the scheme and judged the identity
  insufficient.

### 3.3 Options

- **A — 401 for every non-Bearer credential, uniformly** (recommended). An API key of any kind on a person-only route
  answers `BearerRequired()`: 401 with a bare `WWW-Authenticate: Bearer` and a route-neutral detail. The one remaining
  403 is a *Bearer* that resolves to a non-person. It gets a route-neutral sentence.
- **B — 403 for an API key, uniformly.** `/user` and `/logout` join the self-service routes, and the sentence is made
  route-neutral.
- **C — keep both, document per route.**

**Recommendation: A**, because on these routes the API key is not an authentication scheme at all:

- **The routes declare one scheme.** Their OpenAPI security requirement is `alvoBearer` only. The `X-Alvo-Api-Key`
  header is read only to notice it, never resolved. By RFC 6750 §3.1 that is a request "attempted using an unsupported
  authentication method", which gets the bare challenge, and by RFC 9110 §15.5.2 it "lacks valid authentication
  credentials for the target resource". The challenge is the actionable part: it names the one scheme that works.
- **It is #539's mirror image.** #539 answers an app token in the Bearer channel with a 401 that names the API-key
  header. The opposite mistake, an API key on a Bearer-only route, then answers a 401 that names Bearer. One rule
  covers both directions: *a credential on a channel this route does not authenticate with is 401, and the challenge
  names the channel that works.*
- **403 stays exactly where RFC 9110 puts it.** It is for a credential the route *does* authenticate with that turned
  out insufficient:
  - a Bearer that resolves to a non-person (`SelfServicePerson.cs:71`), which is GitHub's situation exactly;
  - a member who is not an owner;
  - a person changing their own membership;
  - **Management refusing an app token**, which is unchanged. Management authenticates by API key, the token
    resolved, and it is insufficient (S5 D9).

  An agent can therefore read the status alone: 401 means change *how* you authenticate, and 403 means you
  authenticated and may not.
- **B would assert insufficiency for credentials never checked** (see the end of §3.1), and its fix ("present a
  Bearer") is the content of a challenge that 403 does not carry.

### 3.4 The sentences, route-neutral

- The 401 for an API key on a person-only route reuses `BearerRequired()`, with its detail widened so it no longer
  suggests only a configured key:
  *"This route answers a signed-in person only. Present the access token sign-in issued as 'Authorization: Bearer
  <token>'; an API key, a personal token or a service token does not reach it."* The sentence is identical for every
  kind of key, live or not, and echoes none of it, so it is no oracle.
- The 403 for a Bearer that resolves to a non-person: *"This route answers a signed-in person, and the bearer token
  names a caller that is not one."* It moves from `SelfServicePerson.NotAPerson` to `ProblemResultFactory`, beside
  the 401's.
- The ambiguous request on a person-only route keeps `WWW-Authenticate: Bearer error="invalid_request"`. Advertising
  the API-key challenge there would invite the wrong credential. Its detail becomes `AmbiguousCredential`'s
  sentence, with the fix narrowed: *"The request presents both X-Alvo-Api-Key and a bearer token. This route answers
  a signed-in person: present only the bearer token."* The header is the configured one.

**Code shape, one switch.** `PersonOnlyRefusal` gains the `ApiKey` arm explicitly; today `/user` reaches it only by the
default arm, which is how the two sites diverged. `UserEndpoints.Bearer` (`:133-138`) is hoisted next to it as
`CallerResolution.PersonBearer(request, options, personRegistered)`, returning `(token, refusal)`, and
`SelfServicePerson.AdmitAsync` uses it instead of its own switch (`:55-60`). The switch then exists once.

### 3.5 Deviations

- **RFC 6750 §3.1's 400 for `invalid_request` stays 401.** This is already recorded at `CallerResolution.cs` and
  `ProblemResultFactory.BearerUnauthenticated`, and the reason is unchanged: one status for "your credential cannot be
  used".
- **From GitHub's 403 for "the wrong kind of token".** The reason is in §3.3: GitHub's token kinds share the route's
  scheme, and Alvo's API key does not.

### 3.6 Wire

```http
POST /auth/v1/tokens            X-Alvo-Api-Key: alvo_svc_7f….Lw…                      (before)
403  { "type": "https://alvo.dev/errors/forbidden",
       "detail": "Only a signed-in person manages tenants. Present the access token sign-in issued as 'Authorization: Bearer <token>'; an API key or a token minted for an integration does not reach this route." }

POST /auth/v1/tokens            X-Alvo-Api-Key: alvo_svc_7f….Lw…                      (after; same on every person-only route)
401  WWW-Authenticate: Bearer
     { "type": "https://alvo.dev/errors/unauthenticated",
       "detail": "This route answers a signed-in person only. Present the access token sign-in issued as 'Authorization: Bearer <token>'; an API key, a personal token or a service token does not reach it." }
```

`GET /auth/v1/user` with an API key answers exactly this today, so `/user` and `/logout` change only the detail's last
clause.

### 3.7 Blast radius

- **Code.** `SelfServicePerson.cs` (the switch and `NotAPerson` go), `CallerResolution.cs` (one switch and
  `PersonBearer`), `UserEndpoints.cs:133-141`, and `ProblemResultFactory.cs:129` and `:538` (sentences).
  `AccountsDocumentation*.cs` changes in about 12 places: every self-service operation's 403 description loses "an API
  key or an integration's token was presented …", and its 401 description gains "(an API key does not reach this
  route)", as `/user`'s already says.
- **Snapshot.** The accounts OpenAPI snapshot changes descriptions only.
- **Tests** that pin 403 and flip to 401 with a bare `Bearer` challenge:
  - `TenantRoutesTests.An_api_key_is_403_and_no_credential_is_a_bearer_challenge`, renamed;
  - `InvitationRoutesTests.An_api_key_is_403_and_no_credential_is_a_bearer_challenge`;
  - `InvitationAcceptTests.Accept_needs_a_person`;
  - `TokenRoutesTests.A_token_cannot_mint_a_token` and `…A_token_cannot_list_or_revoke`;
  - `TenancyEngineFacts` (the person-only check);
  - `UserEndpointsTests.An_api_key_is_not_a_person_here`, detail only;
  - `…An_api_key_beside_a_bearer_is_an_invalid_request` and `TenantRoutesTests.A_key_beside_a_bearer_is_ambiguous_…`,
    detail only.

  New: one theory over every person-only operation id × {configured key, personal token, service token, made-up key}
  → the identical 401. A host resolver returning a non-person Bearer → 403 with the new sentence.
- **TeaPie.** `010-tokens-reach-req.http:3` (`[403]` → `[401]`) and `010-tokens-reach-test.csx:9-10` (type and detail).
  A new assertion checks the bare `Bearer` challenge.
- **Guides.** `tokens.mdx:266-267` ("Minting, listing and revoking tokens, and every tenant, member and invitation
  route: `403`" → `401`, naming the challenge) and `tenants-and-invitations.mdx:395`. `sign-in.mdx:354-357` is
  unchanged.
- **Docs.** `accounts.md:404-405, 439, 461, 641, 672, 856-857, 939-940, 987` and `website/src/data/problem-types.json`
  (`forbidden`'s list of causes).
- **Skills.** `plugins/alvo/skills/alvo-descriptor-accounts/SKILL.md:49-50` gains one clause ("…and on a sign-in or
  tenant route any API key is 401"). Its drift test and size cap apply.
- **Admin, assistant:** none.

## 4. Item 3 — rate-limit names

### 4.1 Current state

`AlvoRateLimitOptions` (public, bound from `Alvo:Api:RateLimits`) has three budgets:

- **`Token`** (`AlvoRateLimitOptions.cs:15`, default 30) limits `POST /auth/v1/token`, the OAuth endpoint
  (`AccountsEndpoints.cs:50`).
- **`Create`** (`:26`, default 10) limits `POST /tenants` **and** `POST /tokens` (`SelfServiceEndpoints.cs:116,129`).
  S3 D14 introduced it for tenants, and S5 D15 added minting to it.
- **`Invitations`** (`:33`, default 20) limits all five invitation routes.

So the budget named *Token* does not limit `/tokens`, and the one named *Create* does. The doc comment on `Create`
already says the names wait for #542. Validation is `AlvoApiOptionsValidator.cs:31-33`. Binding is
`AlvoHost.cs:424` (`GetSection("Alvo:Api").Bind(api)`), and an embedded host binds it however it likes.

### 4.2 Options

| | OAuth endpoint | Tenant creation | Token minting |
|---|---|---|---|
| A — rename only | `TokenEndpoint` | `Provisioning` (shared) | `Provisioning` (shared) |
| **B — rename and split** (recommended) | `TokenEndpoint` | `Tenants` | `AppTokens` / `ApiTokens` (item 4's noun) |
| C — document only | `Token` | `Create` | `Create` |

**Recommendation: B.**

- **Every budget is named after what it limits, as `Invitations` already is.** `TokenEndpoint` is RFC 6749 §3.2's own
  name for `POST /token`, so the word *token* never names two things in one options class.
- **Split, because the shared budget buys nothing and couples two unrelated acts.** Each act already has its own hard
  cap that the budget does not replace: `maxPerAccount` for tenants (S3 D9) and 100 live tokens per account (S5 D13).
  The rate budget exists to slow per-IP abuse of each route. Sharing it means a CI job minting tokens behind a
  company's egress IP spends the budget a colleague needs to create a tenant. Spec §7.3 names the two in one phrase
  ("tenant creation and token minting 10/min"), and the reading "10 each" is as natural as S5 D15's "10 together".
  The split reverts S5 D15, which is recorded below.
- **The minting budget takes item 4's noun**, so the three names the integrator reads match: the descriptor key
  (`auth.appTokens` or `auth.apiTokens`), the budget (`RateLimits:AppTokens` or `:ApiTokens`) and the guide's word.
  That makes this slice depend on item 4 (§9).

Defaults: `TokenEndpoint` 30, `Tenants` 10, `<noun>` 10, `Invitations` 20.

### 4.3 Configuration-binding impact

- **`ConfigurationBinder` ignores keys it does not know.** A host still setting `Alvo:Api:RateLimits:Token: 120` after
  the rename silently gets the default 30. Nothing fails, and the effect is a stricter limit.
- **Inside the repository the silent failure has one dangerous home: the load stack.**
  - `docker-compose.field-service.yml:135-137` widens the budgets for the e2e.
  - `test/load/docker-compose.load.yml:25-27` resets them with `!reset null` *by name*.
  - Renamed on one side only, the load stack would inherit the demo's widened budgets and stop measuring the host its
    baselines describe. `FieldServiceTestIssuerTests.The_load_stack_resets_the_demos_sign_in` is the guard. The slice
    renames both files and checks that the guard reads the new names, not a hard-coded list that still passes.
- **No shim** (pre-v0.1, no users). A startup refusal of the retired keys in `AlvoHost` was considered and rejected: it
  would be the only retired-key check in the host, and it would live forever for a rename nobody outside the
  repository has seen. The CHANGELOG entry names old → new.

### 4.4 Deviation

**From S5 D15** (one budget for creating and minting): two budgets, for the reason above.

### 4.5 Wire

No HTTP shape changes. The 429 `rate-limited` problem is the same; only the configuration key named in its OpenAPI
description changes.

```text
Alvo:Api:RateLimits:Token        →  Alvo:Api:RateLimits:TokenEndpoint
Alvo:Api:RateLimits:Create       →  Alvo:Api:RateLimits:Tenants     and   Alvo:Api:RateLimits:<AppTokens|ApiTokens>
Alvo:Api:RateLimits:Invitations     (unchanged)
```

### 4.6 Blast radius

- **Code.**
  - `AlvoRateLimitOptions.cs`: the public properties are renamed, so `PublicApi.MMLib.Alvo.verified.txt` changes (two
    removals, three additions). The turn-review gate will send this to `alvo-architecture-rules`: the class is already
    public, and the members are its contract.
  - `AlvoRateLimits.cs` (enum and dictionary), `AlvoApiOptionsValidator.cs:31-33`, `SelfServiceEndpoints.cs:116,129`
    and `AccountsEndpoints.cs:50`.
  - `AccountsDocumentation.cs:145`, `.Tenants.cs:223`, `.Tokens.cs:112` (the 429 descriptions).
- **Tests.**
  - `TokenRateLimitTests`.
  - `TenantRoutesTests.Tenant_creation_answers_429_after_its_limit`.
  - `TokenRoutesTests.Minting_answers_429_after_the_create_budget_and_shares_it_with_tenant_creation`, which is
    **inverted**: it now proves the budgets do *not* share, so minting at the limit leaves tenant creation open.
  - Validator tests and `FieldServiceTestIssuerTests`.
- **Compose and load.** `docker-compose.field-service.yml:135-137` and `test/load/docker-compose.load.yml:25-27`.
- **Guides and docs.**
  - Guides: `tokens.mdx:281-282`, `tenants-and-invitations.mdx:396-398`, `sign-in.mdx:319`, `production.mdx:191`,
    `concepts/security-model.md:239-241`.
  - `website/src/data/problem-types.json:244`.
  - `docs/architecture/host.md:155-163`, `data-api.md:1106`, and `accounts.md:413, 483, 631, 708, 948, 960, 999,
    1045, 1192`.
- **TeaPie, skills, admin, assistant:** none. TeaPie reads no budget names, and only compose sets them.

## 5. Item 4 — `auth.accessTokens` (the maintainer's call)

### 5.1 Current state

The descriptor key `auth.accessTokens` (`schema/project.schema.json:230`, public `Auth.AccessTokens` /
`AuthAccessTokens`, `DescriptorBlocks.cs:109,113`) switches on what everything else calls something different:

- the code calls them *app tokens* (S5 D2: "no type is named `AccessToken*` (`AccessTokens` is already the JWT
  issuer)");
- the guides say *personal and service tokens* and *tokens for integrators*;
- the OpenAPI tag says "App tokens";
- the `alvoBearer` scheme's description is written to separate "a personal or service token" from "an access token".

`AlvoAccountsOptions.AccessTokenMinutes` and the `access_token` member of the token response are the OAuth access
token, which really is one (RFC 9068). This is the confusion #539 had to fix at the wire. The schema key is the one
part of it that freezes when v0.1 is released.

### 5.2 Prior art

- **RFC 6749 §1.4:** "Access tokens are credentials used to access protected resources", issued by an authorization
  server to a client. RFC 9068 profiles them as JWTs. Alvo *is* that authorization server (S2), so in Alvo's own
  vocabulary "access token" already has one meaning. RFC 6749 does not define "API key".
- **Products that issue both OAuth tokens and user-minted credentials qualify the noun.** GitHub has "personal access
  tokens" beside "OAuth access tokens". GitLab has "personal / project / group access tokens". Supabase's platform has
  "access tokens" (`sbp_…`).
- **Products without that collision tend to say "API token".** Atlassian has "API tokens", Cloudflare "API Tokens" (scoped).
- **"App token" is, in GitHub's and Slack's vocabulary, an *application's* credential.** GitHub App installation tokens
  and Slack's app-level `xapp-` tokens both name a token that acts as the app, never as a person.

### 5.3 Candidates

| | `auth.apiTokens` | `auth.appTokens` | keep `auth.accessTokens` |
|---|---|---|---|
| Collides with OAuth access tokens | no | no | **yes** (#539) |
| Matches the channel the credential travels on | **yes**: `X-Alvo-Api-Key`, scheme `alvoApiKey`, stored as an `ApiKeyRecord` with `ApiKeyKind.Personal/Service` | no | no |
| Matches the code's existing word | no: 33 `src` files say `AppToken*` (all internal) | **yes** (S5 D2) | no |
| Reads correctly for a *personal* token | yes ("a personal API token") | weakly: "app" reads as the app's own credential (GitHub/Slack), and Alvo's guides call the app author's product "your app" 22 times | yes |
| Risk of being read as something else | could be read as "the configured API keys"; they are on the same channel, and the schema description says "self-service" | could be read as the client app's (frontend's) key | the OAuth token |

**My recommendation is `auth.apiTokens`; the decision is the maintainer's.** The reasons:

1. **It names what the credential is.** It is an API key with a scannable prefix (S5, *Why it is shaped this way*,
   first bullet). It travels in `X-Alvo-Api-Key`, under the `alvoApiKey` scheme. Its public type is already
   `ApiKeyKind`, so the public API would read `Auth.ApiTokens` beside `ApiKeyKind.Personal`.
2. **"App" is taken in Alvo's prose.** A guide's "your app" is the app author's product. An agent reading
   `auth.appTokens` beside it can reasonably conclude it configures the app's own key.
3. **The cost is internal.** The code's `AppToken*` vocabulary (S5 D2) is all internal, so it can be renamed
   mechanically in its own slice or left with a one-line note in `accounts.md`. Neither touches a contract.

`appTokens` is the stronger choice if the maintainer weighs "one word from descriptor to code, zero internal churn"
above the two readings in point 2. Keeping `accessTokens` is the weakest option: the collision it carries is the one
#539 shipped a fix for.

### 5.4 What the rename touches, exactly

This is the same list for either name. `<Noun>`/`<noun>` is `ApiTokens`/`apiTokens` or `AppTokens`/`appTokens`.

- **The frozen artifact.**
  - `schema/project.schema.json:230`: the key, and the description text that calls them "Self-service tokens for
    integrators".
  - The JSON pointers that name it: `/auth/accessTokens/service` in `AccessTokensCheck.cs:16`, and
    `examples/_negative/expectations.json:13`.
- **Public API (Abstractions).**
  - `Auth.AccessTokens` → `Auth.<Noun>` and `AuthAccessTokens` → `Auth<Noun>` (`DescriptorBlocks.cs:109-113`).
  - `PublicApi.MMLib.Alvo.Abstractions.verified.txt:681-688` gets a rename: one removal and one addition.
  - The turn-review gate will flag the addition. It is a rename of S5 D16's own growth, not new surface.
- **Descriptor checks and policy.**
  - `AccessTokensCheck.cs` → `<Noun>Check.cs`.
  - The `descriptorKey` constants `AppTokenProblems.cs:18-24` (`auth.accessTokens`, `.personal`, `.service`). These
    are **wire values** in the `feature-disabled` 404.
  - `AppTokenReach.cs:31` (refusal sentence), and the doc comments in `AppTokenPolicy.cs`, `AppTokenPrincipals.cs`,
    `PolicyCatalog.cs:126`, `TokenRoutes.cs:18-20` and `AppTokenRequest.cs:12`.
- **OpenAPI.** `AccountsDocumentation.Tokens.cs:62,72,89-90,102`, `AccountsSchemas.Tokens.cs:73`, the tag description
  "App tokens", and the accounts snapshot.
- **Descriptors.**
  - `examples/field-service/field-service.alvo.json:18` and `examples/field-service/README.md:194`.
  - `examples/_negative/service-tokens-without-shared-tenants.json:7`.
  - `test/_shared/accounts/descriptors/{team-tracker,time-tracker}.alvo.json`.
  - `test/MMLib.Alvo.Api.Tests/descriptors/{token-caller,token-desk}.alvo.json`.
  - `website/src/snippets/tokens/01-access-tokens.alvo.json` (renamed to `01-<noun>.alvo.json`; `tokens.mdx:9`
    imports it).
- **Tests.**
  - `AccessTokensSchemaTests` (renamed), `AccessTokensCheckTests`, `SelfServiceCheckTests`, `AppTokenPolicyTests`,
    `AppTokenResolutionTests`, `AppTokenReachTests` and `TokenRoutesTests` (the `descriptorKey` facts).
  - `SkillAreasTests` (`/auth/<noun>` belongs to the accounts skill) and `test/_shared/accounts/AccountsWorld.Tenants.cs`.
  - `AppTokenEngineFacts`.
- **TeaPie.** `170-SelfService/004-billing-token-init.csx` (a comment naming `maxLifetimeDays`'s key).
- **Guides.**
  - `tokens.mdx:52-56,295`: the anchors `/reference/descriptor/auth/#auth.accessTokens.*` are generated from the
    schema, so they move with it, and DocsGen's link validation catches a missed one.
  - `sign-in.mdx:368` and `advanced-demo.mdx:60`.
- **Skills.** `plugins/alvo/skills/alvo-descriptor-accounts/SKILL.md:3,16,31-32` (the description, the level table,
  the key bullet and the refusal pointer). Its generated regions and size cap are drift-tested.
- **Assistant.** No base-prompt text names the key. `SkillAreas` routes `/auth/**` by prefix, so it needs no change.
  The owed eval run (#540) should run after this rename, not before.
- **Admin dashboard.** `src/MMLib.Alvo.Admin` does not read `auth.accessTokens`. The F5 prototype's generated
  `docs/design/f5-admin/generated/descriptor.js:41` regenerates with `scripts/gen-prototype-fixtures`, and its
  `--check` would fail otherwise.
- **Docs.** `docs/architecture/accounts.md` (about 20 mentions), `host.md`, `docs/PLAN.md` and
  `website/src/data/problem-types.json:254`. The spec and plans stay as history.
- **Not touched:** `AccessTokenMinutes`, `AccessTokens.cs` (the JWT issuer) and `access_token`. These *are* OAuth
  access tokens.

A stored descriptor carrying `auth.accessTokens` is refused on its next apply by `additionalProperties: false`. With
no users there is no migration; the CHANGELOG states it.

## 6. Item 5 — `Idempotency-Key` on the three creating routes

### 6.1 Current state

Every Data API write honours `Idempotency-Key` (`docs/architecture/data-api.md`, *What is stored, and where it is
honoured*):

- the record is `(idempotency_key, scope, fingerprint, row_id, created_at)` in `{prefix}_idempotency`;
- the scope is `AlvoIdempotency.IdentityOf(caller)` = `tenant/user`;
- the record stores the row's **id, never a rendered response**, and a replay re-reads the row through the caller's
  current policy;
- a different fingerprint is `409 idempotency-conflict`.

Commands keep a ledger that stores the rendered answer, scoped per credential, with a 24-hour sweep. Management records
the revision it applied. CORS already allows the header on the auth routes (S2 plan, `AlvoCors`).

`POST /tenants`, `POST /tenants/{id}/invitations` and `POST /tokens` ignore the header. The consequences:

- **`POST /tenants`.** A retry after a lost 201 creates a second tenant and spends a **permanent** `maxPerAccount` slot,
  because tenants cannot be deleted (S3 D9).
- **`POST /tokens`.** A retry mints a second token. The first one's secret was never seen by the caller, yet it lives
  until its `expiresAt` (up to 365 days), counts toward the 100 live tokens, and is indistinguishable in the list from
  the one the caller holds. **A lost response is exactly the case in which the caller cannot know who else saw it**: a
  logging proxy, a crashed client that wrote it to disk.
- **`POST …/invitations`.** The same, for a code: a live bearer secret nobody is known to hold, up to 100 uses and 30
  days.

The other `/auth/v1` writes need no key. Rename and member `PATCH` assign absolute values. Every `DELETE` is already
idempotent (S4, S5). `accept` answers `AlreadyMember` for a repeat (S4 D4). `/logout` succeeds on a revoked session.
`/token` is OAuth, and the exchange's ID token is single-use while a refresh replay inside the grace window answers the
same child (S2 D6). None of this changes.

### 6.2 Prior art

- **`draft-ietf-httpapi-idempotency-key-header-08`** (22 October 2026):
  - §2.2: a key "MUST be unique and MUST NOT be reused with another request with a different request payload", and its
    uniqueness is "defined by the resource owner".
  - §2.3: the resource "MAY require time based idempotency keys" and "SHOULD define such expiration policy and publish
    it".
  - §2.4: a fingerprint is optional.
  - §2.6: on a retry after completion the resource "SHOULD respond with a response consistent with the request being
    completed". The draft does **not** require the stored original response, and Appendix A marks that text as still
    open (issue #48).
  - §2.7: 400 for a missing required key, **422** for a key reused with a different payload, **409** for a retry while
    the original is in flight.
  - The draft says nothing about responses that carry secrets.
- **Stripe (API v1):**
  - it "saves the status code and body of the first request … Retries with the same key return the saved response,
    including `500` errors";
  - keys are up to 255 characters, and may be pruned once "at least 24 hours old";
  - different parameters are an error;
  - every `POST` accepts a key.

  Storing the body is what lets Stripe replay a one-time value. It also means the value is at rest in the idempotency
  store.
- **Rolling a secret** is established prior art for "give me a working secret for the same credential": GitHub's
  *Regenerate token* for personal access tokens keeps the token's settings, issues a new value, and invalidates the old
  one. Stripe calls the same act *rolling* an API key.

### 6.3 Per-route decision

#### `POST /tenants` — support, with the Data API's semantics

- **What is recorded:** the tenant id, in `{prefix}_idempotency`, with scope `auth:tenants/<userId>`. The surface
  prefix follows the command ledger's `command:<name>/…`, so a Data API create and a tenant create holding the same
  key string never meet. A person on these routes has no tenant, so `IdentityOf` would produce `global/<user>`, which
  is exactly the Data API key space the prefix keeps apart.
- **Fingerprint:** SHA-256 over the operation id and the body bytes as sent (Management's "hashed as sent").
- **Same transaction.** `CreateTenantRequest` gains an optional idempotency triple. `ITenantStore.CreateSharedAsync`
  looks the record up **after** taking the account-row lock it already takes for `maxPerAccount` (S3 D3). It then
  inserts the tenant, the membership and the record in its one transaction. Two concurrent requests with one key are
  serialised by that lock, so the second becomes a replay; the primary key is the backstop.
- **New statuses:** `CreateTenantStatus` gains `Replayed` (with the tenant id) and `IdempotencyConflict`.
- **Replay:** `201` with the same `Location`, and the creator's membership **re-read** through
  `SelfServiceMemberships`, so a rename since then shows. If the creator is no longer a member, the replay answers
  `201` with `{ "tenantId": "3c…" }` alone. That is the Data API's id-only replay: the id is one the first 201
  already gave this caller, and a retry must not be worse than the create it replays.
- **Charged like the first request:** the replay spends a `Tenants` rate-budget unit, because the filter runs first.
  It spends **no** `maxPerAccount` slot, which is the point of the key.

#### `POST /tokens` — support; a replay **rolls the secret of the same token**

The secret is shown once and stored only as its SHA-256 (S5, spec §14 AC6), so a replay cannot repeat it. There are
four ways to answer a replay:

| | Replay answers | Cost |
|---|---|---|
| A — not supported (record the deviation) | nothing: a retry mints a second token | the orphaned unseen credential above, and a list the caller cannot reconcile |
| B — store the response encrypted (Stripe-identical) | the identical body | the secret at rest beside the hash: AC6's "stored as its hash" no longer holds for 24 h or more; a DB + key-file leak in that window yields live secrets |
| C — derive the secret (the refresh-token precedent, S2 D6: `alvo_rt_<id>_<HMAC(key, id)>`) | the identical body, re-derived | every token's secret becomes a function of one server key for its whole life (up to 365 days, not 10 s); a DB + key-file leak yields every live token |
| **D — roll** (recommended) | `201`, the same `keyId`, `createdAt` and settings, and a **new** secret; the old hash is replaced in the same transaction, so the lost secret is dead | the replayed `token` differs from the first response's |

**Recommendation: D.** It is the only option that stores no secret and leaves no live credential that nobody can
account for. It is also the Data API's own philosophy: Alvo replays *the state now*, never a stored rendering
(`AlvoIdempotency`'s remarks). It satisfies the draft's §2.6 "a response consistent with the request being completed".
Exactly one token exists for the key, and the caller holds a secret that works for it.

**The roll's rules:**

- **Same locks and the same session re-read as a mint.** The roll takes the account row, then the session, then the
  tenant: the A-I1 order (`accounts.md`, *The stack review's fixes*). A replay from a session a reuse has ended writes
  nothing (`SessionEnded` → 401).
- **Only a live token rolls.** If the token the key minted is revoked or expired, the replay answers `409 conflict`
  with *"The token this Idempotency-Key minted is no longer active, so its secret cannot be issued again. Mint a new
  token with a new key."* and a `keyId` member. The key id is not secret. This includes a token swept by a demotion,
  removal or ban: the reason it died still holds.
- **The roll is unconditional on `lastUsedAt`.** That stamp is minute-throttled and best-effort per replica (S5,
  *How a token becomes a caller*, step 7), so it cannot prove "never used". The replayed 201 carries the token's
  current `lastUsedAt`. A non-null value tells the caller that someone used the secret it never received, and the
  roll has just killed it.
- **The caller asserts by replaying that it never got the first answer.** A client that did get it and replays anyway
  kills its own first secret. The guide says so in one sentence. Only the same person, with the same key and an
  identical body, can trigger it, and that person could revoke the token anyway.
- **Scope:** `auth:tokens/<userId>`. The fingerprint is the operation id plus the body as sent. Its `expiresAt` is
  absolute, so a body re-sent days later with a recomputed expiry is a different request (`409 idempotency-conflict`).

#### `POST /tenants/{id}/invitations` — support; a replay rolls the code while it is unused

The same reasoning applies: a 128-bit code stored as its SHA-256 (S4 D2). The roll replaces `code_hash` on the same
invitation under the tenant version bump and session lock the create already takes (S4 D6, A-I1):

```sql
UPDATE … SET code_hash = @new WHERE id = @id AND created_by = @me AND uses = 0 AND revoked_at IS NULL AND expires_at > @now
```

- **`uses = 0` is exact for invitations** (the conditional `UPDATE` of S4's acceptance routine), unlike a token's
  `lastUsedAt`. So the rule is stricter: an invitation someone has already accepted, or that is dead, does not roll.
  The replay answers `409 conflict` (*"The invitation this Idempotency-Key created has already been used or is no
  longer active, so its code cannot be issued again. Create a new invitation with a new key."*) with an `invitationId`
  member.
- **Scope:** `auth:invitations/<userId>`. The fingerprint covers the path's tenant id, so the same key against
  another tenant is 409.

#### Common to all three

| Rule | Value | Precedent |
|---|---|---|
| Key bounds | ≤ 255 UTF-8 bytes, non-blank, one header line; otherwise 422 | `AlvoIdempotency.EnsureUsableKey`, `AlvoApiOptions.MaxIdempotencyKeyBytes` |
| Different fingerprint | `409 idempotency-conflict` | Data API, commands Ruling 7 |
| Concurrent duplicate | waits on the route's existing lock, then replays | Data API ("the loser retries into a replay") |
| Retention | that of the shared table, which has no sweep yet (#115); a record is honoured until #115 decides | the Data API's current behaviour |
| Replay status / `Location` | as the first answer (201; `Location` only on `POST /tenants`, where it already exists) | Data API create replay |
| Replay marker | none | the Data API sends none; Management's `replayed: true` is a body member of its own shape |
| OpenAPI | the header as an optional parameter on the three operations, each saying what a replay answers | the Data API's descriptions |

### 6.4 Deviations

- **From the draft's §2.7 422 for a reused key with a different payload:** 409 `idempotency-conflict`, which is
  already Alvo's one meaning across the Data API and commands (commands Ruling 7).
- **From the draft's §2.7 409 for a request in flight:** the duplicate waits on the account or tenant lock and is
  answered as a replay. The Data API does the same.
- **From Stripe's "return the saved response":**
  - `POST /tenants` re-reads, the Data API's recorded deviation;
  - `POST /tokens` and `POST …/invitations` return a **rolled** secret, not the first one, because Alvo never stores a
    shown-once secret (AC6).

  The draft's §2.6 permits both. Neither stored the response.
- **From the draft's §2.3 "SHOULD define [an] expiration policy and publish it":** inherited from the Data API's open
  #115. The guides say "kept until a retention policy lands".
- **New beyond the draft:** the replay of a creating request may answer `409 conflict` when the thing it created has
  since died. The draft has no case for "completed, then destroyed".

### 6.5 Wire

```http
POST /auth/v1/tenants   Idempotency-Key: 7c9e…   { "name": "Acme" }                (first; the 201 is lost)
201 Location: /auth/v1/tenants/3c…   { "tenantId": "3c…", "name": "Acme", "kind": "shared", "roles": ["dispatcher"], "isOwner": true }

POST /auth/v1/tenants   Idempotency-Key: 7c9e…   { "name": "Acme" }                (retry; before: a second tenant, slot spent)
201 Location: /auth/v1/tenants/3c…   { "tenantId": "3c…", "name": "Acme", … }       (after: the same tenant, no slot spent)

POST /auth/v1/tenants   Idempotency-Key: 7c9e…   { "name": "Acme Ltd" }
409 { "type": "https://alvo.dev/errors/idempotency-conflict", … }
```

```http
POST /auth/v1/tokens   Idempotency-Key: 51aa…   { "name": "billing", "kind": "service", … }   (first; the 201 is lost)
201 { "keyId": "alvo_svc_7f…", "token": "alvo_svc_7f….Lw…", … }

POST /auth/v1/tokens   Idempotency-Key: 51aa…   (identical body)                              (retry)
201 { "keyId": "alvo_svc_7f…", "token": "alvo_svc_7f….Q2…", …, "lastUsedAt": null, "status": "active" }
    — the same token; "….Lw…" is now refused 401 like any revoked secret

POST /auth/v1/tokens   Idempotency-Key: 51aa…   (identical body, after the token was revoked)
409 { "type": "https://alvo.dev/errors/conflict", "keyId": "alvo_svc_7f…",
      "detail": "The token this Idempotency-Key minted is no longer active, so its secret cannot be issued again. Mint a new token with a new key." }
```

### 6.6 Blast radius

- **Abstractions (internal ports).**
  - `CreateTenantRequest`, `MintTokenRequest` and `CreateInvitationRequest` each gain an optional
    `(Key, Scope, Fingerprint)`.
  - `CreateTenantStatus`, `MintTokenStatus` and `MemberChangeStatus` gain `Replayed`, `IdempotencyConflict` and
    `ReplayDead`.
  - The outcomes carry the replayed id.
- **No public API change.**
- **EF adapter.**
  - `EfCoreTenantStore` (create and invitations) and `EfCoreAppTokenStore` (mint) read and insert through
    `IdempotencyTable`'s SQL inside their transactions.
  - The roll statements go in `AppTokenTables.cs` and `InvitationTables.cs`, one text for both engines.
  - No new table, no `AlvoFrameworkTables` change.
- **Routes.** `TenantRoutes.CreateAsync` (`:43-66`), `TokenRoutes.CreateAsync`/`MintedAsync` (`:33`, `:122-139`) and
  `InvitationRoutes.CreateAsync`/`CreatedAsync` (`:37`, `:171-178`). They read the key with the Data API's bounds and
  fingerprint the body as read. `SelfServiceCall` keeps the raw body bytes it already reads.
- **OpenAPI.** The `Idempotency-Key` parameter and the 409 `idempotency-conflict` / `conflict` responses on three
  operations in `AccountsDocumentation.*.cs`, plus the snapshot.
- **Tests.**
  - Store facts, both engines:
    - `TenantStoreFacts.A_replayed_create_returns_the_same_tenant_and_spends_no_slot`, plus a 20-iteration concurrent
      variant at the cap;
    - `AppTokenStoreFacts.A_replayed_mint_rolls_the_same_token_s_secret`, `…A_replay_of_a_dead_token_writes_nothing`,
      `…A_replay_racing_a_reuse_of_its_session_never_outlives_it`;
    - `InvitationStoreFacts.A_replay_rolls_an_unused_code_and_never_a_used_one`.
  - Route tests: a mismatch is 409, a malformed key is 422, and the old secret or code is dead after a roll.
  - `SecretsNeverLoggedTests` extends to the rolled secret and the idempotency key, which is caller text and is never
    logged.
- **TeaPie.** One new case, `013-retries`, after `012`: Dana replays a tenant create and a mint, and shows that the
  first secret is 401 after the roll. The existing cases are unchanged.
- **Guides.**
  - `tenants-and-invitations.mdx` and `tokens.mdx` each gain a short "Retrying safely" paragraph, including the
    one-sentence warning in §6.3.
  - `sign-in.mdx` is unchanged: `/token` is not keyed.
- **Docs.** `accounts.md` (S3, S4 and S5 route tables and recorded deviations), `data-api.md` (the table's second
  family of writers), `concepts/security-model.md` (the shown-once secret now "or rolled on a keyed retry").
- **Skills, admin, assistant:** none.

## 7. How the items interact

- **Item 4 → item 3:** the minting budget's name is item 4's noun. Land item 4 first, or land item 3 with
  `TokenEndpoint` and `Tenants` and leave the minting budget's rename for the item-4 slice.
- **Item 1 → item 5:** a replayed mint answers the token's current state in the list shape. Item 1's "201 = list item
  + `token` + `header`" makes that one shape instead of two.
- **Item 2 is independent** and the smallest. It also touches the most test files, so it should land first, before the
  others move the same tests.

## 8. CHANGELOG (pre-v0.1 breaking, each in `[Unreleased]`)

- **Item 1.**
  - `GET /auth/v1/tokens`: `mintedBy` → `createdBy`, and `displayName` → `name`.
  - `POST`/`GET …/invitations`: `createdBy` is now `{ "id", "name" }`, not an id string.
  - `POST /auth/v1/invitations/preview`: `inviterName` → `createdBy: { "name" }`.
  - `POST /auth/v1/tokens` 201 adds `createdBy`, `lastUsedAt`, `revokedAt` and `status`.
- **Item 2.** An API key, a personal token or a service token on any person-only `/auth/v1` route
  (tenants, members, invitations, accept, tokens) is now `401` with `WWW-Authenticate: Bearer`, not `403 forbidden`.
  `/user` and `/logout` answered 401 already; only their detail's last clause changes. A Bearer a host's resolver
  resolves to a non-person stays `403`, with a new sentence.
- **Item 3.** `Alvo:Api:RateLimits:Token` → `:TokenEndpoint`; `:Create` → `:Tenants` and `:<AppTokens|ApiTokens>`,
  10 each. The two no longer share a budget. `AlvoRateLimitOptions.Token`/`.Create` are renamed in the public API.
- **Item 4.** `auth.accessTokens` → `auth.<noun>`, and `Auth.AccessTokens`/`AuthAccessTokens` →
  `Auth.<Noun>`/`Auth<Noun>`. `feature-disabled`'s `descriptorKey` values change with it, and a stored descriptor using
  the old key is refused on its next apply.
- **Item 5.** `Idempotency-Key` is honoured on `POST /auth/v1/tenants`, `POST /auth/v1/tenants/{id}/invitations` and
  `POST /auth/v1/tokens`. A keyed retry of the two secret-issuing routes answers the same token or invitation with a
  **new** secret and kills the first.

## 9. Slices — each independently mergeable, in order

| # | Slice | Tests it carries | Depends on | Estimate |
|---|---|---|---|---|
| 1 | **Item 2: 401 for a non-Bearer credential on every person-only route.** One switch (`PersonOnlyRefusal` with its `ApiKey` arm, `PersonBearer` hoisted), route-neutral sentences, OpenAPI descriptions, guides, skill line, TeaPie `010`. | The theory over every person-only operation × four key kinds → identical 401 with a bare `Bearer` challenge; the non-person Bearer → 403; the ambiguous detail; the flipped route tests; snapshot | — | 0.5 day |
| 2 | **Item 1: one person reference.** `PersonReference`/`PersonName`; `createdBy` everywhere; the creator's name joined in the invitation list (both engines); the token 201 as list item + `token` + `header`. | `InvitationStoreFacts` (creator name, both engines); route shape facts; the preview never carries `id`; a null name is `"name": null`; `AppTokenResponseTests` (masking still holds); TeaPie `002`, `004`; snapshot | — | 1 day |
| 3 | **Item 4: the descriptor key** — once the maintainer names it. Schema, Abstractions rename, checks, `descriptorKey` values, descriptors and snippets, skill, guides and anchors, prototype fixtures. | Schema tests renamed; `…CheckTests` at the new pointer; `descriptorKey` facts; `SkillAreasTests`; `DocsSnippetTests`; `gen-prototype-fixtures --check`; PublicApi baseline (rename) | the maintainer's decision | 0.5–1 day (mechanical, wide) |
| 3b | *(optional)* internal vocabulary: `AppToken*` → `ApiToken*`, only if item 4 picks `apiTokens`. No wire or public change. | Build plus the existing suites; no behaviour test moves | 3 | 0.5 day |
| 4 | **Item 3: rate-limit names and the split.** `TokenEndpoint`, `Tenants`, `<Noun>`; validator; both compose files; docs. | Minting at its limit leaves tenant creation open (the inverted D15 fact); each budget 429s alone; validator per key; `FieldServiceTestIssuerTests` reads the new names; PublicApi baseline | 3 (for the noun) | 0.5 day |
| 5 | **Item 5a: `Idempotency-Key` on `POST /tenants`.** Store triple, `Replayed`/`IdempotencyConflict`, re-read replay, id-only replay for a former member, OpenAPI. | `TenantStoreFacts` replay and 20× concurrent-at-the-cap (both engines); routes: 201 same `Location`, 409 conflict, 422 malformed, no slot spent | — | 1–1.5 days |
| 6 | **Item 5b: on `POST /tokens`, with the roll.** The A-I1 lock order and session re-read; the dead-token 409. | `AppTokenStoreFacts`: the roll, the dead-token replay writes nothing, a replay racing a reuse never outlives it (20×, both engines); route: the old secret is 401 after a roll; `SecretsNeverLoggedTests`; TeaPie `013` (part) | 2 (the 201 shape), 5 (shared plumbing) | 1.5 days |
| 7 | **Item 5c: on `POST …/invitations`, with the roll while `uses = 0`.** | `InvitationStoreFacts`: the roll, never after an accept, never on a dead invitation, racing an accept (20×, both engines); route: the old code previews dead; TeaPie `013` (rest) | 5 | 1 day |

**Total:** about 6–7 days, the item-4 decision aside. Slices 1 and 2 can start today. If v0.1 needs the contract frozen
before slices 6–7 are built, the fallback is to merge 5 and record option A's deviation for `/tokens` and
`…/invitations` in `accounts.md`. That records the decision and closes no door: the header is then documented as
"accepted and ignored" on those two routes, exactly as `POST {entity}/query` documents it, until 6–7 land.

Each slice runs ring1 when it is done and ring2 before its PR. Slices 1, 2, 3 and 6 move the accounts OpenAPI
snapshot, so the snapshot judge runs. Slices 3 and 4 move a PublicApi baseline, so the turn-review gate sends them to
`alvo-architecture-rules`. Slices 5–7 touch the person gate's write paths and the A-I1 lock order, so they get
`/security-review` and the `alvo-security-core-review` checklist.

## 10. Open questions for the maintainer

Item 4's name is asked in §5. Beyond it:

1. **Split or share the creating budget (item 3).** The recommendation is to split, which reverts S5 D15. If one
   budget is preferred, its name would be `Provisioning`.
2. **The roll on a keyed retry (item 5).** It is new behaviour beyond both the draft and Stripe. It needs an explicit
   yes, because it makes "shown once" read "shown once, or rolled on a keyed retry" (spec §14 AC6). The alternative
   is option A, a recorded deviation, for tokens and invitations.
3. **Retention of the three new record families.** The recommendation inherits #115 (none yet). Should these routes
   instead get the command ledger's 24-hour window now? That would mean a per-family sweep, because the shared table
   has none.
4. **The token 201 as the list item (item 1).** It is a small shape change outside the issue's literal wording, made so
   that a replay and a list share one shape. Is it in scope for #542, or a follow-up?
