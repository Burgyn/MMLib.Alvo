# S6 — The field-service demo grows self-service, the guides and the skills Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> This file is committed on its own before Task 1, on `accounts/s6-demo-docs` (from `accounts/s5-tokens`, PR #537).

**Goal:** The field-service demo becomes the level-3 showcase — people sign in through a test OpenID issuer, create
*Acme Repairs*, invite a technician, mint a billing token, remove a member and lose a session to refresh-token reuse —
proven by a new TeaPie collection `170-SelfService` against the real container, with every existing suite still green;
readers get three guides (sign-in, tenants and invitations, tokens) that lead with what they can build; agents get a
descriptor skill for the new keys; and the contributor record closes the initiative.

**Architecture:** No product behaviour changes. The demo is configuration: `examples/field-service/field-service.alvo.json`
gains §12's keys; `docker-compose.field-service.yml` configures the Google provider against a WireMock-served test
issuer (discovery + static JWKS) in the Development environment, a per-run secrets key and wider rate budgets; the
TeaPie suite mints RS256 ID tokens in a shared `.csx` with the committed test-only private key. The only code outside
tests: `MMLib.Alvo.Ai`'s internal `SkillAreas` learns the new skill's area, and `tools/MMLib.Alvo.DocsGen` boots a
second in-process host with accounts on to publish the sign-in routes as a reference page.

**Tech Stack:** Docker Compose, WireMock 3.9.1, TeaPie 1.7 (`.http` + `.csx`, `System.Security.Cryptography.RSA`),
Astro + Starlight (`starlight-openapi`, links validator), .NET 10 (DocsGen, xUnit v3 on MTP, Shouldly).

**Spec:** `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md` — read §3 (the three
levels, §3.1's keys and refusals), §5.2–§5.5 (exchange, access token, refresh, logout, `/user`), §5.7 (the credential
table), §6.2 (role provenance, and the paragraph on `System`-run automation that "the guides say so next to the hook
docs"), §6.3–§6.4, §7.1–§7.3, §11 (S6 row), §11.2 (`Authority` + `AllowHttpMetadata`, Development only), **§12** (the
demo), **§13** (the docs), **§14** (AC7: every existing suite stays green). Master plan
`docs/superpowers/plans/2026-11-13-accounts-00-master.md` (S6 row; Global Constraints; test discipline). S3 plan D1, S4
plan D1, S5 plan D1 (routes under `/auth/v1`); S5 plan D9 + its recorded deviation (`.WithAlvoCaller()` refuses app
tokens, #536). Contributor view: `docs/architecture/accounts.md` (what S2–S5 shipped — the guides must match it).
Existing paths read for this plan: `examples/field-service/**`, `docker-compose.field-service*.yml`, `scripts/test-e2e`,
`test/teapie-field-service/**` (README's "Four TeaPie behaviours", `_shared/Rows.csx`, `090-Docs`, `160-Automation`'s
`RETRY-UNTIL-TEST-PASS`), `test/teapie-field-service-host/030-Mobile/001-job-completed-init.csx` (signing in an init
script), `src/MMLib.Alvo.Accounts/Internal/{GooglePreset,OidcProviders,OidcMetadata,OidcIdTokenVerifier,
AlvoAccountsOptionsValidation}.cs`, `src/MMLib.Alvo/Tenants/SelfService/SelfServiceEndpoints.cs`,
`test/_shared/accounts/TestOidcIssuer.cs`, `plugins/alvo/skills/**`, `test/MMLib.Alvo.Ai.Tests/SkillConformanceTests.cs`
(10 skills max, 6 144 bytes, 200 lines, worked examples), `test/MMLib.Alvo.Host.Tests/SkillExampleOutcomeTests.cs`
(examples run against `bike-workshop`), `src/MMLib.Alvo.Ai/Internal/SkillAreas.cs`, `eval/MMLib.Alvo.Ai.Eval/SkillsRead.cs`,
`website/astro.config.mjs`, `tools/MMLib.Alvo.DocsGen/Host/{HostBoot,HostPagesGenerator}.cs`,
`test/_shared/docs/DocsSnippets.cs`. Prior art read: Google Identity Services *Sign in with Google for Web* (the
`credential` a page receives is the ID token) and *Verify the Google ID token on your server side*; OIDC Discovery 1.0
§3 (the metadata members a verifier needs); RFC 7517 (an RSA JWK carries `n`/`e`, never `d`/`p`/`q` in a public set);
RFC 6749 §6 + RFC 9700 §4.14 (rotation and reuse); OWASP *HTML5 Security* / *JWT for Java* cheat sheets on where a SPA
keeps tokens.

## Decisions (read before Task 1)

- **D1 — Sign-in lives on the standalone stack only.** `docker-compose.field-service.yml` (+ `test/teapie-field-service`)
  gets the issuer, the accounts configuration and `170-SelfService`. The embedded sample
  (`docker-compose.field-service-host.yml`, `samples/MMLib.Alvo.Samples.FieldService`) serves the **same** descriptor
  unchanged in code: it registers no `AddAlvoAccounts`, so it logs the existing boot warning (`auth.providers lists
  google, but this host registered no end-user sign-in…`) and every request there behaves exactly as today. Its
  `.WithAlvoCaller()` endpoints (dispatch, technician app) would refuse app tokens anyway (S5 D9, #536), so the sample's
  README says sign-in is the standalone stack's story and names #536. Nothing in the demo or the guides promises that a
  host's own endpoints take personal or service tokens.
  **Recorded deviation (Task 3):** the sample maps no `.WithAlvoCaller()` endpoint, so the parenthetical above is wrong.
  Dispatch is a `MapAlvoCommand`, which takes a token within its entity's read and write scopes, and the technician app
  is a `MapAlvoWebhookReceiver`, which authenticates by its signing secret. The sample's README states the general rule
  (#536) and nothing about its own routes; no guide or skill repeats the parenthetical.
- **D2 — The descriptor gains exactly §12's keys.** `auth.providers: ["local", "google"]` (`local` stays: it is the
  operator store), `auth.signUp: {"mode": "open"}`, `auth.accessTokens: {"personal": true, "service": true}` (lifetime
  default 90), `tenancy.selfService: {createTenants: {creatorRoles: ["dispatcher"]}, invitations: true,
  assignableRoles: ["dispatcher", "technician"]}`. **No `personalTenant`** (§12 omits it): a field-service person works
  in a company, so a person with no header acts in no tenant and the demo shows the tenant switcher. **`regions` is not
  touched**: `070-Authorization` pins its "no update, no delete" shape and `010-Seed` its admin-only create. So "a
  self-made dispatcher cannot write `regions`" is pinned in its regression form (403, with the admin key's 201 as the
  control); the sharper contrast — the *same* role name admitted as a project role and refused as a tenant role — stays
  proven in process on both engines (`TenancyEngineFacts.A_self_made_dispatcher_cannot_write_global_regions`), and the
  guides cite it.
- **D3 — The test issuer is Google's preset under another authority.** `Alvo__Accounts__Providers__Google__Authority:
  http://localhost:9090/oidc` + `AllowHttpMetadata: "true"` + `ClientIds__0: ${ALVO_FS_GOOGLE_CLIENT_ID}`. The sink
  shares Alvo's network namespace, so `localhost:9090` is it. `GooglePreset` matches a token by its fixed issuer set,
  so the ID tokens carry `iss: https://accounts.google.com`; the sink's discovery document says the same `issuer` and
  names `jwks_uri: http://localhost:9090/oidc/jwks.json` and `id_token_signing_alg_values_supported: ["RS256"]`. Tokens
  are RS256 (the preset's only algorithm) with `kid` `field-service-e2e-1`. A real deployment's Google provider fetches
  Google's own keys and refuses every token this key signs — the label in D4 says so.
- **D4 — The private key is committed once, test-only, and held out of everything shippable.** One RSA-2048 key, PKCS#8
  PEM, inside `test/teapie-field-service/_shared/TestIssuer.csx` under a `TEST-ONLY` banner (what it signs, why it is
  public, that a Development-only `AllowHttpMetadata` authority is the only thing that trusts it). The public half is
  `examples/field-service/sink/mappings/test-oidc-issuer.json` (a WireMock mapping, `"name"` labelled test-only; the
  Dockerfile copies only descriptors, so it never reaches the image). Ring0 facts (Task 2) hold that the JWKS is the
  key's public half, carries no private member, and that no `PRIVATE KEY` block exists under `src/`, `samples/`,
  `examples/`, `schema/`, `plugins/`, `tools/`, `docs/` or `website/src/`. Not generated per run: §12 asks for a committed
  key, and a per-run key would leave a hand-started stack (README) unable to sign anyone in.
  **Recorded deviation (Task 2, controller ruling):** no private key is committed. `_shared/TestIssuer.csx` makes an
  RSA-2048 key on the machine that runs the suite (kept under `$TMPDIR/alvo-field-service-e2e/`, `kid` = its RFC 7638
  thumbprint) and registers the public half in the sink through WireMock's admin API at the start of every run; the
  committed mapping is the discovery document only. Kept per machine, not made per run, because Alvo refetches the key
  set for an unknown `kid` at most once per five minutes (`OidcMetadata.UnknownKidInterval`). The hand-started-stack
  objection no longer holds — any `170` run publishes the key there too. The guards became
  `FieldServiceTestIssuerTests.No_private_key_is_committed` (every `git ls-files` path, staged included),
  `…The_committed_issuer_carries_no_key_material`, and `170/001`'s check of the sink's live key set.
- **D5 — The demo stack runs `ASPNETCORE_ENVIRONMENT=Development`**, as §11.2 foresaw: `AllowHttpMetadata` is refused
  anywhere else, and `Alvo:Accounts:Issuer` stays unset so tokens are issued from the URL a request reached
  (`http://localhost:8081/auth/v1`); `Audience` defaults to `field-service`. Only `MMLib.Alvo.Accounts` reads the host
  environment (`grep -rn IsDevelopment src` finds nothing else). ASP.NET Core's own Development effects, recorded: DI
  scope validation (already exercised — `HostAccountsTests` boots Development), `appsettings.Development.json` (the
  image ships none), the developer exception page (masked — `AlvoHost` maps `UseExceptionHandler` first).
- **D6 — A per-run secrets key.** Accounts refuses to start without `Alvo:Secrets:EncryptionKeyFile`. `scripts/test-e2e`
  exports `ALVO_FS_SECRETS_KEY="$(openssl rand -base64 32)"`; the compose file passes it as a Compose secret
  (`secrets: alvo_fs_secrets_key: environment: ALVO_FS_SECRETS_KEY`, the quickstart's precedent) mounted at
  `/run/secrets/alvo_fs_secrets_key`, and `Alvo__Secrets__EncryptionKeyFile` points there. Never committed; `${…:?}`
  refuses a start without it.
- **D7 — Wider rate budgets in the demo stack.** One TeaPie run comes from one IP and spends ~20 invitation calls
  (defaults: Token 30, Invitations 20, Create 10 per minute), so the stack sets `Alvo__Api__RateLimits__Token: 120`,
  `__Invitations: 60`, `__Create: 30`, with a comment: the limits themselves are pinned in process
  (`TokenRoutesTests`, `InvitationRoutesTests`, `TenantRoutesTests`); a 429 in the e2e would be a flake, not a finding.
  **Recorded deviation (Task 4 fix round):** `__Invitations` is **120**, not 60. All five invitation routes share one
  budget and one `170` run spends ~31 calls on it (the expiry poll alone ~16 previews), so a second run inside a minute
  on a live stack was refused 429 at 60. The load stack resets all three budgets (`test/load/docker-compose.load.yml`).
- **D8 — The grace window keeps its default (10 s) and is waited out by polling, never by sleeping.** The reuse case
  replays the rotated token at once (same child, the D6 property), then polls it with `## RETRY-UNTIL-TEST-PASS`
  (≤ 30 × 1 s, `Constant` backoff) until it answers `400 invalid_grant`. A replay inside the window is idempotent, so
  the poll cannot fork the family. The same idiom waits out a 15-second invitation expiry (polling the anonymous,
  side-effect-free `preview`).
- **D9 — The `090-Docs` path pin moves, by exactly the sign-in surface.** With accounts configured, `/openapi/v1.json`
  gains 15 paths: `/auth/v1/token`, `/auth/v1/user`, `/auth/v1/logout`, `/auth/v1/tenants`, `/auth/v1/tenants/{tenantId}`,
  `/auth/v1/tenants/{tenantId}/members`, `/auth/v1/tenants/{tenantId}/members/{userId}`,
  `/auth/v1/tenants/{tenantId}/invitations`, `/auth/v1/tenants/{tenantId}/invitations/{invitationId}`,
  `/auth/v1/invitations/preview`, `/auth/v1/invitations/accept`, `/auth/v1/tokens`, `/auth/v1/tokens/{keyId}`,
  `/auth/v1/.well-known/oauth-authorization-server`, `/auth/v1/.well-known/jwks.json` (the S5 Accounts OpenAPI snapshot's
  set). The case's name says "the descriptor's entities plus the sign-in surface, and no others". The host-stack suite
  holds no OpenAPI pin and its host maps no `/auth` route — nothing there moves.
- **D10 — Three people per run, minted fresh.** `001-sign-in` mints ID tokens in its init script for **Dana** (the
  dispatcher who founds Acme), **Theo** (the technician) and **Olga** (an outsider), each `sub` = a role word plus a fresh
  GUID, a `@gmail.com` address with `email_verified: true`, and a display name. Nothing about them is committed;
  `demo-identities.env` gains only the non-secret `ALVO_FS_GOOGLE_CLIENT_ID=field-service-demo.apps.googleusercontent.com`
  (compose and TeaPie read the same value, the file's own rule). `170-SelfService` creates its own region (admin key,
  run-unique code) and its own tenants, so it runs alone and re-runs on a live stack — unlike `010`–`160`.
- **D11 — The level-3 story is §12, in this order:** sign in → Dana creates *Acme Repairs* → invites Theo as
  `technician` → Theo previews and accepts → Dana creates a customer and two jobs, one assigned to Theo → Theo sees only
  his, starts and completes it (a tenant role through the state machine) → Dana mints a `billing` service token
  (`roles: ["dispatcher"]`, `scopes: ["work_orders:read"]`) that reads completed jobs and cannot write → Theo's personal
  token reads his job → Dana removes Theo: his next request in Acme and his personal token are 401 → the adversarial half
  → refresh reuse, last (it ends Dana's session). **Every scoped create carries `tenant_id` in its body and
  `X-Alvo-Tenant` in its headers** — the known DX gap (S3 ledger: a create must echo the tenant; Alvo checks it, never
  fills it in) is shown honestly in the demo and the guides, not papered over.
  **Recorded deviation (Task 3):** the answer to a scoped create without `tenant_id` is `403 forbidden`, "The write was
  rejected by policy.", with no `violations` — not Task 3 Step 2's `422` at `/tenant_id`. A candidate with no tenant
  fails the synthesized tenant scope's `WITH CHECK`. `170-SelfService/003` pins the 403 and the guides quote it as a
  current limitation.
- **D12 — The guides' exchanges are hand-written, and held three ways.** DocsGen's captured exchanges boot a host with
  dev keys and no ID-token issuer, so a captured sign-in would need a test issuer inside a shipped tool. Instead:
  (a) every descriptor a guide shows is a `website/src/snippets/**/*.alvo.json` that `DocsSnippetTests` applies;
  (b) a fact asserts every `METHOD /auth/v1/…` a guide names is an operation of the generated sign-in reference (Task 7);
  (c) each worked flow mirrors a `170-SelfService` case, named in the guide's prose, which runs it for real.
- **D13 — The reference publishes the sign-in routes from OpenAPI** (§13). DocsGen boots a second in-process host over
  `examples/field-service/field-service.alvo.json` with Google configured (placeholder client id, an `https` issuer, a
  key file in its temp directory — the document needs no network) and writes `openapi/accounts-api.json` holding only
  the paths under `/auth/v1` (components untouched). Astro renders it at `reference/accounts-api` beside the vehicle-
  registry Data API.
- **D14 — A tenth descriptor skill, `alvo-descriptor-accounts`.** `auth` has no skill home today and the three levels do
  not fit the 1.5 KB left in `traits-and-tenancy`. This takes the catalogue's last slot (`MaximumSkills = 10`).
  `SkillAreas.Accounts = "accounts"`: `/auth/**` except `/auth/roles` (which stays unmapped, as today) and
  `/tenancy/selfService/**` map to it; `/tenancy` itself stays `traits-and-tenancy`. `traits-and-tenancy` and
  `project-access` each gain one pointer sentence; `capabilities-and-limits` says `google` is honoured (the other
  providers still warn). The developer-only `alvo-host-extensions` gains one sentence: `.WithAlvoCaller()` refuses
  personal and service tokens (#536).
- **D15 — Three guides under "Secure it", each opening with what you can build.** `guides/sign-in.mdx` ("Sign-in and
  accounts"), `guides/tenants-and-invitations.mdx` ("Tenants, members and invitations"), `guides/tokens.mdx` ("Tokens for
  integrators"), after *Authentication and API keys*. Each starts with a `:::tip[What you will build]`, then the
  smallest working thing (an Acme Repairs worked example), then the mechanics and the why. Stale claims elsewhere on the
  site ("only local accounts and API keys exist", Google "tracked in #36", sign-in providers unhonoured) are swept for
  the claim, not by file.
- **D16 — No public API moves, and no product behaviour changes.** Every `PublicApi.*.verified.txt` stays as it is;
  `SkillAreas` is internal; DocsGen is `IsPackable=false`. Moved baselines are limited to: `090-Docs`' pin (not a Verify
  file), the F5 prototype fixtures if `gen-prototype-fixtures` regenerates them, and any DocsGen/skill snapshot the new
  page or skill legitimately grows — each judged when the Stop hook asks.

## Global Constraints

- English in everything committed. CRLF + BOM in every new `.cs` (copy a neighbour's bytes; re-normalise after any
  Bash/Python edit). `.csx`, `.http`, `.json`, `.yml`, `.md(x)` follow their neighbours' line endings.
- **Test discipline (the maintainer's instruction):** inner loop = one project filtered to the class
  (`dotnet test --project <proj> -- --filter-class '*<Class>*'`) or one TeaPie collection against one stack; **ring0 only
  through the pre-commit hook**; ring1 and ring2 once, in Task 9. The TeaPie stacks (`scripts/test-e2e`) only in the tasks that change the demo — 1, 2, 3, 4 — and the gate;
  the admin e2e once at the gate; `npm run build` (in `website/`) once in Task 7 and at the gate; a whole-solution Release
  build and `scripts/gen-prototype-fixtures --check` at the gate.
- **One stack in the inner loop:** `set -a; . examples/field-service/demo-identities.env; set +a`, export the five
  `ALVO_FS_*_SECRET`s and `ALVO_FS_SECRETS_KEY` (as `scripts/test-e2e` does), `docker compose --env-file
  examples/field-service/demo-identities.env -f docker-compose.field-service.yml up --build --detach --wait`, write
  `artifacts/teapie/env.json` with `scripts/test-e2e`'s shape (Task 2 adds `fsGoogleClientId`), then
  `dotnet tool run teapie -- test test/teapie-field-service/170-SelfService -e compose --env-file artifacts/teapie/env.json --no-cache-vars --no-logo`.
  `down --volumes` before each fresh run of the whole suite (it owns its database; `170` alone does not need it).
- TeaPie rules from `test/teapie-field-service/README.md` bind every case: assert parsed shapes (`_shared/Rows.csx`),
  name exact rows, carry a control wherever the wrong behaviour could pass; a `Capture:` test that asserts nothing hands
  ids on; GUID-shaped values through `Constant(name)`; a `###` before every request; a polled request is its file's
  last and its test reads `tp.Response`; **never spell the load directive inside a comment of a shared `.csx`**.
- No secret in a commit, a log line the suite prints, or a URL: refresh tokens, invitation codes and token secrets
  travel in bodies and headers only (spec §6.3, §8).
- Never push to `main`; never merge. The controller opens the PR against `accounts/s5-tokens`.
- A moved baseline is accepted only in the task that moves it.

## Review Focus

1. **A reader copies a guide's request and it fails** — a scoped create without `tenant_id`, a person with no
   `X-Alvo-Tenant` and no personal tenant (403 — the tenant guard), an app token sent as `Authorization`. Expected: every guide request is one that works, and each pitfall is shown with its
   answer. Pinned by Task 3/4 (each pitfall is a TeaPie request with its real status), Task 7 (route fact, applied
   snippets).
2. **`170-SelfService` run alone, or twice on a live stack** (how a contributor iterates). Expected: green both times —
   no dependency on `010-Seed`, fresh subjects, own region, own tenants. Pinned by Task 3 Step 4 (run alone, then again
   without `down`).
3. **A slow CI runner** — an ID token older than 5 minutes at exchange, a fixed sleep shorter than the grace window or
   an expiry. Expected: ID tokens are minted in the init of the case that exchanges them, and every wait is a poll with a
   30 s ceiling. Pinned by Task 2 (mint-in-init) and Task 4 (both waits are `RETRY-UNTIL-TEST-PASS`).
4. **The test key escaping, or the demo's trust in `http` metadata outliving Development.** Expected: the key exists
   only under `test/`, the published set is its public half with no private member, and the product refuses
   `AllowHttpMetadata` outside Development (already `AccountsOptionsTests`). Pinned by Task 2's ring0 facts.
5. **Everything that already ran against field-service, under the grown descriptor and stack** (AC7): the five dev keys,
   `070`'s three shapes, the sample host booting with `google` listed and no sign-in, the dashboard and its e2e over the
   descriptor, the prototype fixtures. Expected: unchanged behaviour. Pinned by Task 1 (every consumer suite), Task 2
   (`scripts/test-e2e` whole), Task 9 (admin e2e, ring1/ring2).

---

### Task 1: The field-service descriptor grows self-service, and every consumer stays green

**Files:**
- Modify: `examples/field-service/field-service.alvo.json` (`auth`, `tenancy`, `description`)
- Regenerate if stale: `docs/design/f5-admin/generated/*` via `scripts/gen-prototype-fixtures`
- Test (run, edit only if a fact pins the old `auth`/`tenancy` text): `test/MMLib.Alvo.Samples.FieldService.Tests`,
  `test/MMLib.Alvo.Admin.Tests`, `test/MMLib.Alvo.Analyzers.Tests` (`*AlvoEntityGeneratorSnapshotTests*`),
  `test/MMLib.Alvo.DocsGen.Tests` (`*ExampleCatalogTests*`), `test/MMLib.Alvo.Host.Tests` (`*QuickStartImageTests*`),
  `test/MMLib.Alvo.Schema.Tests`, `test/MMLib.Alvo.Tests` (`*DescriptorToSchemaMapperTests*`, `*DocsSnippetTests*`)

**Interfaces:**
- Produces the descriptor of D2. The top-level `description` gains one sentence: *"People sign in with Google, create
  their own companies, invite technicians and mint tokens for integrations beside the five dev keys — see README.md,
  Self-service."*

- [ ] **Step 1: Edit the descriptor** per D2 (keys in the order `providers`, `roles`, `signUp`, `accessTokens`;
  `tenancy.enabled` first).
- [ ] **Step 2: Validate it** — `dotnet test --project test/MMLib.Alvo.Schema.Tests` and
  `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*DescriptorToSchemaMapperTests*'`. Expected: PASS, and
  the apply raises **no** global-write warning (`regions`' rules admit no `authenticated` write — if one appears, stop:
  D2 is wrong).
- [ ] **Step 3: Run every consumer** — `dotnet test --project test/MMLib.Alvo.Samples.FieldService.Tests`,
  `dotnet test --project test/MMLib.Alvo.Admin.Tests`, and the filtered classes in *Files*. Expected: PASS. If the
  sample's in-process host refuses `tenancy.selfService` (no tenant registry under its provider), stop and report — the
  cure is a design question, never a weaker descriptor.
- [ ] **Step 4: Fixtures** — `scripts/gen-prototype-fixtures --check`; if stale, run `scripts/gen-prototype-fixtures`,
  then `scripts/test-prototype` once (the drawing reads them). Expected: `--check` exit 0 after regeneration;
  `test-prototype` green.
- [ ] **Step 5: The TeaPie stacks** — `scripts/test-e2e` once. Expected: `[e2e] OK` with **no** suite edited — the
  descriptor alone moves nothing (no host in either stack signs anyone in yet, so no route and no pin moves); the host
  stack logs D1's boot warning.
- [ ] **Step 6: Commit** — `feat(examples): the field-service descriptor lets people sign in, create tenants, invite and mint tokens`

### Task 2: A test OpenID issuer in the field-service stack, and people sign in end to end

**Files:**
- Create: `test/teapie-field-service/_shared/TestIssuer.csx` (D4's key and the minting helpers)
- Create: `examples/field-service/sink/mappings/test-oidc-issuer.json` (discovery + JWKS, D3)
- Modify: `docker-compose.field-service.yml` (Development, accounts, secret, rate budgets — D3, D5, D6, D7; the header
  comment's run lines gain `ALVO_FS_SECRETS_KEY`), `examples/field-service/demo-identities.env`
  (`ALVO_FS_GOOGLE_CLIENT_ID`), `scripts/test-e2e` (export `ALVO_FS_SECRETS_KEY`; env `fsGoogleClientId`; its header's
  field-service paragraph names sign-in), `test/teapie-field-service/env.example.json` (`fsGoogleClientId`)
- Modify: `test/teapie-field-service/090-Docs/001-openapi-document-test.csx` (D9)
- Create: `test/teapie-field-service/170-SelfService/001-sign-in-init.csx`, `001-sign-in-req.http`, `001-sign-in-test.csx`
- Create: `test/MMLib.Alvo.Host.Tests/FieldServiceTestIssuerTests.cs`

**Interfaces:**
- `TestIssuer.csx` (top-level functions; no directive spelled in its comments):
  - `const string TestIssuerKid = "field-service-e2e-1";` `const string GoogleIssuer = "https://accounts.google.com";`
  - `string IdToken(string subject, string email, string name)` — header `{"alg":"RS256","kid":TestIssuerKid,"typ":"JWT"}`;
    payload `iss` = `GoogleIssuer`, `aud` = `tp.GetVariable<string>("fsGoogleClientId")`, `sub`, `email`,
    `email_verified: true`, `name`, `iat` = now, `exp` = now + 1 h, `jti` = a fresh GUID (`N`); base64url without
    padding; signature `RSA.SignData(…, SHA256, Pkcs1)` with the PEM key (`RSA.ImportFromPem`).
  - `string IdTokenSignedByAnotherKey(string subject, string email, string name)` — the same claims and `kid`, signed by
    a fresh `RSA.Create(2048)`.
- `test-oidc-issuer.json`: `{"mappings": [ … ]}` with two `GET` stubs, each `"name": "TEST-ONLY OpenID issuer for the
  field-service e2e (see test/teapie-field-service/_shared/TestIssuer.csx)"`:
  `urlPath /oidc/.well-known/openid-configuration` → `jsonBody {issuer: GoogleIssuer, jwks_uri:
  "http://localhost:9090/oidc/jwks.json", id_token_signing_alg_values_supported: ["RS256"], response_types_supported:
  ["id_token"], subject_types_supported: ["public"]}`; `urlPath /oidc/jwks.json` → `{"keys": [{kty: "RSA", use: "sig",
  alg: "RS256", kid, n, e}]}`. `accept-everything.json` matches `POST` only, so nothing collides.
- Compose (`alvo` service) gains `ASPNETCORE_ENVIRONMENT: Development`; `Alvo__Accounts__Providers__Google__ClientIds__0:
  ${ALVO_FS_GOOGLE_CLIENT_ID:?…}`, `…__Authority: http://localhost:9090/oidc`, `…__AllowHttpMetadata: "true"`;
  `Alvo__Secrets__EncryptionKeyFile: /run/secrets/alvo_fs_secrets_key`; the three rate budgets; a top-level `secrets:`
  block. Each gains a comment in the file's existing voice (why Development, why http, why the key is per run).
- TeaPie env (`compose`): `"fsGoogleClientId": "$ALVO_FS_GOOGLE_CLIENT_ID"`.
- `001-sign-in` produces (via `Capture:`): `DanaAccess`, `DanaRefresh`, `DanaId`, `TheoAccess`, `TheoId`, `OlgaAccess`,
  `OlgaId`, `RegionId` (Task 3/4 consume them).

- [ ] **Step 1: Make the key pair** once: `openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048` → the PEM into
  `TestIssuer.csx` under the banner; `n`/`e` (base64url of the modulus and exponent) into the mapping. The banner states:
  *TEST-ONLY. Public by being committed. It signs ID tokens that only an Alvo in the Development environment, whose
  Google provider's Authority is this suite's WireMock sink with AllowHttpMetadata, will accept. Never configure it
  anywhere else.*
- [ ] **Step 2: Write the ring0 facts** in `FieldServiceTestIssuerTests` (parse the PEM between the `BEGIN/END
  PRIVATE KEY` lines of `TestIssuer.csx`; read the mapping as JSON):
  `The_sink_publishes_the_public_half_of_the_suites_key` (`n`, `e` equal the PEM's `ExportParameters(false)` in base64url;
  `kid` equals the `.csx` constant); `The_published_key_set_carries_no_private_member` (no `d`, `p`, `q`, `dp`, `dq`, `qi`
  on any key); `The_discovery_document_names_googles_issuer_and_the_sinks_key_set` (`issuer`, `jwks_uri`, `RS256`);
  `No_private_key_lives_outside_the_e2e_suite` (no file under D4's roots — skipping `bin`, `obj`, `node_modules` —
  contains `PRIVATE KEY-----`); `The_demo_stack_trusts_http_metadata_only_in_development` (in
  `docker-compose.field-service.yml`, `AllowHttpMetadata: "true"` appears only beside `ASPNETCORE_ENVIRONMENT:
  Development`, and in no other compose file).
- [ ] **Step 3: Run them, and prove they can fail** — `dotnet test --project test/MMLib.Alvo.Host.Tests -- --filter-class '*FieldServiceTestIssuerTests*'`;
  Expected: PASS. Then flip one character of `n` (red), add a `"d"` member (red), paste a `PRIVATE KEY-----` line into a
  scratch file under `examples/` (red); restore each and record the three red runs in the commit body.
- [ ] **Step 4: Stack and env** — edit compose, `demo-identities.env`, `scripts/test-e2e`, `env.example.json`.
- [ ] **Step 5: Write `001-sign-in`.** Init: `tp.SetVariable` an ID token for each of Dana, Theo, Olga (D10) and one
  `IdTokenSignedByAnotherKey` for Olga; generate the run's region code (`"SS" + 8 hex`). Requests (form bodies,
  `Content-Type: application/x-www-form-urlencoded`, `grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Atoken-exchange&subject_token=…&subject_token_type=urn%3Aietf%3Aparams%3Aoauth%3Atoken-type%3Aid_token`):
  `Metadata` (`GET /auth/v1/.well-known/oauth-authorization-server` → 200); `ForgedSignIn` (Olga's foreign-key token →
  400); `DanaSignIn`, `TheoSignIn`, `OlgaSignIn` (200); `DanaReplaysIdToken` (Dana's token again → 400);
  `DanaUser` (`GET /auth/v1/user`, Bearer → 200); `AdminCreatesRegion` (`POST /api/regions`, `keyAdmin` → 201).
  Tests: `Capture:`; *the metadata names this host's token endpoint and key set*; *a sign-in answers RFC 8693's shape*
  (`token_type` `Bearer`, `expires_in` 900, `issued_token_type` `urn:ietf:params:oauth:token-type:access_token`,
  `refresh_token` starts `alvo_rt_`, `Cache-Control: no-store`); *a token signed by a key the issuer never published is
  refused* (400, `error` `invalid_request`) with Dana's 200 as the control; *an ID token starts at most one session*
  (replay 400 `invalid_request`); *a new person has an account and no membership* (`DanaUser`: `email` Dana's,
  `emailTrusted` true, `providers` `["google"]`, `memberships` `[]`).
- [ ] **Step 6: Move the `090-Docs` pin** per D9 (one ordinal-sorted array; rename the test).
- [ ] **Step 7: Run one stack** (Global Constraints) and `teapie test test/teapie-field-service/170-SelfService` then
  `…/090-Docs`. Expected: all green. Then `scripts/test-e2e` once (all three stacks, every suite). Expected: `[e2e] OK`,
  and the host stack's log shows the D1 boot warning and nothing new.
- [ ] **Step 8: Commit** — `test(e2e): a test OpenID issuer in the field-service stack, and people sign in end to end`

### Task 3: `170-SelfService` — Acme Repairs, from a new tenant to a removed technician

**Files:**
- Create in `test/teapie-field-service/170-SelfService/`: `002-invite-and-accept-{req.http,test.csx}`,
  `003-assigned-work-{init.csx,req.http,test.csx}`, `004-billing-token-{init.csx,req.http,test.csx}`,
  `005-member-removed-{init.csx,req.http,test.csx}`
- Modify: `test/teapie-field-service/README.md` (the groups table gains `170-SelfService`; the run section names the
  issuer), `examples/field-service/README.md` (a **Self-service** section after *The dev keys*), `examples/README.md`
  (field-service's line mentions sign-in), `samples/MMLib.Alvo.Samples.FieldService/README.md` (D1's paragraph)

**Interfaces:**
- Consumes Task 2's captured variables. Produces `AcmeId`, `TheoInviteCode` (Task 4's "used up" probe),
  `BillingToken`, `TheoToken`, `TheoJobId`.
- Init scripts compute RFC 3339 instants (`DateTimeOffset.UtcNow.AddDays(n).ToString("O")`) and run-unique `reference`s
  (`WO-` + 6 digits from the run's ticks) — never literals.

- [ ] **Step 1: `002-invite-and-accept`.** `DanaCreatesAcme` (`POST /auth/v1/tenants` `{"name": "Acme Repairs"}` → 201:
  `kind` `shared`, `roles` `["dispatcher"]`, `isOwner` true); `DanaInvitesTheo` (`POST /auth/v1/tenants/{AcmeId}/invitations`
  `{"roles": ["technician"]}` → 201, `code` starts `alvo_inv_`, `maxUses` 1); `AnyonePreviews` (`POST
  /auth/v1/invitations/preview` `{"code"}`, **no credential** → 200: tenant name *Acme Repairs*, inviter Dana's display
  name, `roles`); `TheoAccepts` (`POST /auth/v1/invitations/accept`, Theo's Bearer → 200); `TheoAcceptsAgain` (→ 200,
  roles unchanged — idempotent); `DanaListsMembers` (200: exactly Dana `["dispatcher"]` owner and Theo `["technician"]`
  not owner); `DanaListsInvitations` (200: no member named `code`). Tests assert each claim on parsed bodies; the
  "no code in the list" claim checks every item's keys.
- [ ] **Step 2: `003-assigned-work`.** `DanaCreatesCustomer` (`POST /api/customers`, Bearer + `X-Alvo-Tenant: {AcmeId}`,
  body **with** `"tenant_id": "{AcmeId}"` → 201); `DanaCreatesWithoutTenantId` (same body minus `tenant_id` → 422
  `https://alvo.dev/errors/validation`, one violation, code `required`, pointer `/tenant_id` — the managed column is
  required on a scoped create; the guide quotes this answer); `DanaCreatesTheosJob` and `DanaCreatesOtherJob` (`POST /api/work_orders`, `status` `scheduled`,
  `assigned_to` = `TheoId` on the first only, `access_code`, `customer_id`, `region_id` = `RegionId` → 201 each);
  `TheoListsInAcme` (`GET /api/work_orders`, `X-Alvo-Tenant` → exactly his reference); `TheoListsWithoutTenant` (no
  header → 403 `https://alvo.dev/errors/forbidden` — the tenant guard; no row of any tenant); `TheoCreatesAJob` (403 —
  `technician` holds no `create`); `TheoStarts` (`PATCH …/{TheoJobId}` `{"status": "in_progress"}` → 200);
  `TheoCompletes` (`{"status": "completed", "completed_on": "<today>"}` → 200); `DanaListsInAcme` (both references — the
  control that the narrowing is the rule, not the data).
- [ ] **Step 3: `004-billing-token`.** `DanaMintsBilling` (`POST /auth/v1/tokens` `{"name": "billing", "kind": "service",
  "tenantId": AcmeId, "roles": ["dispatcher"], "scopes": ["work_orders:read"], "expiresAt": now + 30 d}` → 201: `token`
  starts `alvo_svc_`, `header` `X-Alvo-Api-Key`, `Cache-Control: no-store`); `BillingReadsCompleted` (`GET
  /api/work_orders?status=eq.completed`, `X-Alvo-Api-Key: {BillingToken}`, no tenant header → exactly Theo's reference);
  `BillingTriesToWrite` (`POST /api/work_orders` → 403, scope); `TheoMintsPersonal` (`kind` `personal`, `tenantId`
  AcmeId, `scopes` `["work_orders:read"]`, `expiresAt` now + 7 d → 201, `alvo_pat_`); `TheosTokenReads` (`GET
  /api/work_orders` → exactly his reference — his live `technician` role); `DanaListsTokens` (`GET /auth/v1/tokens` →
  `billing` with `mintedBy.displayName` Dana's, `status` `active`, no `token` member on any item).
- [ ] **Step 4: `005-member-removed`.** `DanaRemovesTheo` (`DELETE /auth/v1/tenants/{AcmeId}/members/{TheoId}` → 204);
  `TheoInAcmeAfter` (Bearer + `X-Alvo-Tenant` → 401, `WWW-Authenticate` names `invalid_token`); `TheosTokenAfter` (→ 401);
  `TheoUserAfter` (`GET /auth/v1/user` → 200, `memberships` `[]` — his session lives, only the membership died);
  `BillingStillReads` (→ 200 — a service token dies with its *minter's* power, not a member's).
- [ ] **Step 5: Run** `170-SelfService` alone on one stack, then **again without `down`** (Review Focus 2). Expected:
  green both times. Then `scripts/test-e2e` once. Expected: `[e2e] OK`.
- [ ] **Step 6: READMEs.** `examples/field-service/README.md` *Self-service*: what a person can do here (the D11 story in
  five sentences), the descriptor keys, how the stack signs people in (D3, D5) and **how to point it at real Google**
  (a Web client id in `ALVO_FS_GOOGLE_CLIENT_ID`, drop `Authority` and `AllowHttpMetadata`, run `Production` with
  `Alvo__Accounts__Issuer` set to the public `https://…/auth/v1`), that the dev keys keep working beside sign-in, and
  that scoped creates echo `tenant_id`. The sample's README: D1 in three sentences, naming #536.
- [ ] **Step 7: Commit** — `test(e2e): 170-SelfService — Acme Repairs from a new tenant to a removed technician`

### Task 4: `170-SelfService` — the walls a self-service tenant holds, and refresh reuse last

**Files:**
- Create in `test/teapie-field-service/170-SelfService/`: `006-foreign-tenant-{req.http,test.csx}`,
  `007-roles-per-tenant-{req.http,test.csx}`, `008-dead-invitations-{init.csx,req.http,test.csx}`,
  `009-dead-invitations-answer-alike-{init.csx,req.http,test.csx}`, `010-tokens-reach-{init.csx,req.http,test.csx}`,
  `011-refresh-rotation-{req.http,test.csx}`, `012-reuse-ends-the-session-{req.http,test.csx}`
- Modify: `test/teapie-field-service/README.md` (the 170 row's case count and claims)

**Interfaces:** consumes Tasks 2–3's variables; produces nothing after `012`.

- [ ] **Step 1: `006-foreign-tenant`.** `OlgaNamesAcme` (`GET /api/work_orders`, Olga's Bearer, `X-Alvo-Tenant: {AcmeId}`
  → 401 — the whole principal is refused, never a tenantless one); `OlgaNamesNothing` (no header → 403, the tenantless
  answer); `DanaNamesAcme` (200, the control); `OlgaReadsRegions` (`GET /api/regions`, no header → 200 — a tenantless
  person still reads global data).
- [ ] **Step 2: `007-roles-per-tenant`.** `TheoCreatesGarage` (`POST /auth/v1/tenants` `{"name": "Theo's Garage"}` → 201,
  `dispatcher`); `TheoWritesRegions` (`POST /api/regions`, `X-Alvo-Tenant: {GarageId}` → 403); `DanaWritesRegions`
  (Acme header → 403 — a self-made dispatcher, D2); `TheoInvitesDana` (`roles ["technician"]` → 201) and `DanaAccepts`
  (200); `DanaCreatesInGarage` (`POST /api/customers` with Garage header + `tenant_id` → 403 — technician there);
  `DanaCreatesInAcme` (same body for Acme → 201 — dispatcher there: the same person, two tenants, two role sets);
  `AdminCreatesRegion2` (`keyAdmin` → 201, the control that `regions.create` is writable by the one role it names).
- [ ] **Step 3: `008-dead-invitations`** (Dana, owner of Acme). Init: `ExpiresSoon` = now + 15 s (RFC 3339; generous, so a slow runner never sends an instant already past).
  `RevokedInvite` (create → 201) then `RevokeIt` (`DELETE …/invitations/{id}` → 204); `ExpiringInvite` (`{"roles":
  ["technician"], "expiresAt": ExpiresSoon}` → 201); last, `ExpiredPreview` polls the expiring code's preview —
  `## RETRY-UNTIL-TEST-PASS: The expiring invitation has expired.`, `## RETRY-MAX-ATTEMPTS: 30`,
  `## RETRY-BACKOFF-TYPE: Constant`, `## RETRY-MAX-DELAY: 00:00:01`, test on `tp.Response` → 404.
- [ ] **Step 4: `009-dead-invitations-answer-alike`.** Init: an unknown, well-formed code (`alvo_inv_` + 22 base64url
  characters). Olga accepts each of: `UsedUp` (`TheoInviteCode`), `Revoked`, `Expired`, `Unknown` → all 404 with **one**
  `RefusalFingerprint` (spec §6.4: one answer for every dead reason); `OlgaUserAfter` → `memberships` `[]`.
- [ ] **Step 5: `010-tokens-reach`.** Init: `expiresAt` now + 1 d. `BillingMintsAToken` (`POST /auth/v1/tokens` with
  `X-Alvo-Api-Key: {BillingToken}` → 403); `DanaMintsPersonal` (`kind personal`, Acme, `work_orders:read` → 201);
  `PersonalAtManagement` (`GET /management/projects/field-service/descriptor`, that token → 403, `detail` exactly
  *"Personal and service tokens are app credentials and never manage a project. Use an operator session or a configured
  API key."*); `ServiceAtManagement` (billing → 403, same detail); `AnonymousAtManagement` (no credential);
  `BearerAtManagement` (Dana's access token as `Authorization: Bearer` → the **same status as `AnonymousAtManagement`**,
  and not 200 — Management ignores Bearer, §5.7; `AccountAdministrationTests.A_bearer_token_never_reaches_the_route`'s
  shape); `AdminAtManagement` (`keyAdmin` → 200, the control).
- [ ] **Step 6: `011-refresh-rotation`.** `DanaRefreshes` (`grant_type=refresh_token&refresh_token={DanaRefresh}` → 200,
  a new `refresh_token`); `DanaReplaysInsideWindow` (the **old** token again → 200 whose `refresh_token` equals
  `DanaRefreshes`' — D6's same child); last, `ReuseAfterWindow` polls the old token with D8's retry directives until
  `400` and `error` `invalid_grant`.
- [ ] **Step 7: `012-reuse-ends-the-session`.** `ChildAfterReuse` (the child from `DanaRefreshes` → 400 `invalid_grant`
  — every token of the family is dead); `AccessAfterReuse` (`GET /auth/v1/user` with the latest access token → 401);
  `TheoStillSignedIn` (Theo's Bearer `GET /auth/v1/user` → 200 — reuse ended Dana's session only).
- [ ] **Step 8: Run** `170-SelfService` alone (Expected: green; the two polls finish well inside 30 s), then
  `scripts/test-e2e` once. Expected: `[e2e] OK`.
- [ ] **Step 9: Commit** — `test(e2e): 170-SelfService — the walls a self-service tenant holds, and refresh reuse last`

### Task 5: The descriptor skills learn sign-in, self-service tenants and app tokens

**Files:**
- Create: `plugins/alvo/skills/alvo-descriptor-accounts/SKILL.md`
- Modify: `plugins/alvo/skills/alvo-descriptor-traits-and-tenancy/SKILL.md`, `…-project-access/SKILL.md`,
  `…-capabilities-and-limits/SKILL.md`, `plugins/alvo/skills/alvo-host-extensions/SKILL.md` (D14)
- Modify: `src/MMLib.Alvo.Ai/Internal/SkillAreas.cs` (`Accounts`), `eval/MMLib.Alvo.Ai.Eval/SkillsRead.cs` (`Areas`)
- Test: `test/MMLib.Alvo.Ai.Tests/SkillConformanceTests.cs` (`_areas` gains `"accounts"`),
  `test/MMLib.Alvo.Ai.Tests/SkillAreasTests.cs`, `test/MMLib.Alvo.Ai.Eval.Tests/SkillsReadTests.cs`,
  `test/MMLib.Alvo.Host.Tests` (`*SkillExampleOutcomeTests*`, `*SkillCoreClaimsTests*`), `test/MMLib.Alvo.DocsGen.Tests`
  (`*HostSkillTests*`, `*AgentSkillsInstallerTests*`)

**Interfaces:**
- `internal const string Accounts = "accounts";` in `SkillAreas`; `AreaOf`: `"auth"` → `Accounts` unless the second
  token is `"roles"` (then `null`, as today); `"tenancy"` → `Accounts` when the second token is `"selfService"`, else
  `TraitsAndTenancy`. `SkillsRead.Areas` lists it.
- The skill: frontmatter `name: alvo-descriptor-accounts`; `description: Use when an Alvo descriptor change lets people
  sign in or run their own tenants — auth.providers, auth.signUp, auth.accessTokens and tenancy.selfService.` (≤ 200
  characters, no quotes). Body, in this order: the three levels as a table (keys each adds); each key with its default
  and its apply refusal (§3.1, wording from the validator); tenant roles vs project roles (§6.2's one-line rule; never
  name an `assignableRoles` role in `access` — refused; `owner` is not a role); a scoped create still echoes
  `tenant_id`; sign-in itself is host configuration (`Alvo:Accounts:Providers`) — listing `google` on a host without it
  only warns at boot; at least one schema citation that exists and slices under 16 KB; two worked examples, each a
  `propose_change`/`check_change` and its **real** result over `bike-workshop`: *"Let workshop customers sign in with
  Google and see only their own bookings"* (add `google`, `auth.signUp` open) and *"Make manager a role people hold in
  their own workshop"* (add tenancy with `selfService.assignableRoles: ["manager"]` → refused at `/access/developer`,
  *"is a tenant role"*); the closing `In the dashboard:` / `In this repo:` lines.

- [ ] **Step 1: Write the failing facts** — `SkillAreasTests`: theory `The_accounts_paths_belong_to_the_accounts_skill`
  (`/auth/signUp`, `/auth/providers/1`, `/auth/accessTokens/service`, `/tenancy/selfService/invitations` → `accounts`;
  `/auth/roles` → `null`; `/tenancy`, `/tenancy/enabled` → `traits-and-tenancy`); `A_violation_at_sign_up_points_at_the_accounts_skill`
  (`ForViolation("/auth/signUp")`). `SkillsReadTests`: two `InlineData` rows (`/auth/signUp` → `accounts`,
  `/tenancy/selfService` → `accounts`). `SkillConformanceTests._areas` gains `"accounts"`.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.Ai.Tests -- --filter-class '*SkillAreasTests*' --filter-class '*SkillConformanceTests*'`
  and `dotnet test --project test/MMLib.Alvo.Ai.Eval.Tests -- --filter-class '*SkillsReadTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement** `SkillAreas`, `SkillsRead`, the new skill and the four one-line edits.
- [ ] **Step 4: Capture the examples' real outcomes** — run
  `dotnet test --project test/MMLib.Alvo.Host.Tests -- --filter-class '*SkillExampleOutcomeTests*'`, copy each real
  result (warnings included) into the second fence, rerun. Expected: PASS.
- [ ] **Step 5: Run** the Step 2 commands, `dotnet test --project test/MMLib.Alvo.Ai.Tests` (whole: region, embedded and
  instruction facts), `--filter-class '*SkillCoreClaimsTests*'` in Host.Tests, and
  `dotnet test --project test/MMLib.Alvo.DocsGen.Tests -- --filter-class '*HostSkillTests*' --filter-class '*AgentSkillsInstallerTests*'`.
  Expected: PASS, sizes under the caps (6 144 bytes / 8 192 for the host skill).
- [ ] **Step 6: Commit** — `feat(ai): the descriptor skills teach sign-in, self-service tenants and app tokens`

### Task 6: The reference publishes the sign-in, tenant and token routes

**Files:**
- Modify: `tools/MMLib.Alvo.DocsGen/Host/HostBoot.cs` (an accounts-on boot), `Host/HostPagesGenerator.cs` or a new
  `Host/AccountsReferenceGenerator.cs` registered where `DocsGenRun` lists generators
- Create: `tools/MMLib.Alvo.DocsGen/Host/AccountsApiDocument.cs`
- Modify: `website/astro.config.mjs` (a second `starlightOpenAPI` entry, its sidebar group, the links-validator exclude)
- Test: `test/MMLib.Alvo.DocsGen.Tests/Host/AccountsReferenceTests.cs`

**Interfaces:**
- `HostBoot.StartAsync(string descriptorPath, IReadOnlyDictionary<string, ExchangeKey> keys, CancellationToken ct,
  bool withAccounts = false)` — with accounts: `Alvo:Accounts:Issuer = https://alvo.example/auth/v1`,
  `Alvo:Accounts:Providers:Google:ClientIds:0 = docs.apps.googleusercontent.com`, `Alvo:Secrets:EncryptionKeyFile` = a
  file in the work directory holding 32 random bytes in base64.
- `internal static class AccountsApiDocument { internal const string DocumentPath = "openapi/accounts-api.json";
  internal static JsonNode Filter(JsonNode openApi, string prefix); }` — keeps `paths` whose key starts with `prefix`,
  sets `info.title` *"Alvo sign-in, tenants and tokens — example (field-service)"* and an `info.description` linking the
  three guides by `SiteLinks`, leaves `components` as they are.
- Astro: `{ base: 'reference/accounts-api', schema: './src/generated/openapi/accounts-api.json', sidebar: { label:
  'Sign-in, tenants and tokens API — example (field-service)' } }`, and `${base}reference/accounts-api/` excluded from the
  links validator like the Data API's.

- [ ] **Step 1: Write the failing facts** — `AccountsReferenceTests`: `The_reference_holds_exactly_the_sign_in_routes`
  (the generated document's path set equals D9's 15 paths); `No_entity_route_reaches_the_sign_in_reference`;
  `Every_operation_keeps_its_operation_id` (`tokens.create`, `tokens.list`, `tokens.revoke` and every tenant, member and
  invitation operation id of `SelfServiceEndpoints` appears exactly once). The generator runs
  `HostPagesGenerator.RequireLinkTargets` over its description, so until Task 7 the description links only
  `/guides/authentication/`; Task 7 points it at the three guides.
- [ ] **Step 2: Run to see them fail** — `dotnet test --project test/MMLib.Alvo.DocsGen.Tests -- --filter-class '*AccountsReferenceTests*'`; Expected: FAIL.
- [ ] **Step 3: Implement**; then `cd website && npm run gen` once to see `src/generated/openapi/accounts-api.json`
  (generated, gitignored — never committed).
- [ ] **Step 4: Run** the Step 2 command and `dotnet test --project test/MMLib.Alvo.DocsGen.Tests` (whole). Expected:
  PASS.
- [ ] **Step 5: Commit** — `feat(docsgen): the reference publishes the sign-in, tenant and token routes`

### Task 7: The guides — sign-in, tenants and invitations, tokens — and the site's stale claims

**Files:**
- Create: `website/src/content/docs/guides/sign-in.mdx`, `guides/tenants-and-invitations.mdx`, `guides/tokens.mdx`
- Create: `website/src/snippets/sign-in/01-level-one.alvo.json`, `website/src/snippets/tenants/01-level-two.alvo.json`,
  `tenants/02-level-three.alvo.json`, `website/src/snippets/tokens/01-access-tokens.alvo.json`,
  `website/src/snippets/sign-in/sign-in.js` (the browser flow), `website/src/snippets/shell/sign-in.compose.override.yml`
- Modify: `website/astro.config.mjs` (three pages under *Secure it*, after *Authentication and API keys*)
- Modify (the sweep): `start-here/why-alvo.md`, `start-here/what-works-today.mdx`, `project/roadmap.md`,
  `guides/authentication.mdx`, `guides/multi-tenancy.mdx`, `guides/production.mdx`, `concepts/security-model.md`,
  `concepts/glossary.md`, `start-here/embed.mdx`, `guides/advanced-demo.mdx`, `guides/after-hooks-and-webhooks.mdx`,
  `extending/automation.mdx`, `extending/http-conventions.mdx` — all under `website/src/content/docs/`
- Modify: `tools/MMLib.Alvo.DocsGen/Host/AccountsApiDocument.cs` (its description links the three guides)
- Test: `test/MMLib.Alvo.DocsGen.Tests/Host/AccountsReferenceTests.cs` (the route fact; the description's links),
  `test/MMLib.Alvo.Tests` (`*DocsSnippetTests*`), `test/MMLib.Alvo.Schema.Tests`

**Interfaces:**
- Route fact `Every_route_a_guide_names_is_a_sign_in_operation`: every match of
  `` \b(GET|POST|PATCH|DELETE) (/auth/v1/[^\s`'")?]+) `` in the three guides and `advanced-demo.mdx` is a (method, path)
  of the generated reference. Guides therefore write **templates** (`/auth/v1/tenants/{tenantId}/members/{userId}`),
  never concrete ids, in prose and code alike.
- Each guide: `title`, `description`, `sidebar.order`; opens with `:::tip[What you will build]` (outcome + ~time +
  starting point); then numbered steps (`<Steps>`) that build the worked example; then *How it behaves* (mechanics + why);
  then *Limits and what is not here yet*; closes with *See also*. Every worked flow names the `170-SelfService` case that
  runs it (D12c). Plain fences for HTTP (`http`), JSON responses abridged with `…`.

- [ ] **Step 1: Write the failing facts** — the route fact, and `The_description_links_the_three_guides`; point the
  description at them. Expected: FAIL (pages missing).
- [ ] **Step 2: `sign-in.mdx` — "Sign-in and accounts".** Tip: a browser app where each person signs in with Google and
  sees only their own time entries (level 1). Steps: (1) decide what signing up means — `auth.providers`, `auth.signUp`
  (`open` / `invitationOnly` / `allowedEmailDomains`, and why the key is required) with `01-level-one.alvo.json`;
  (2) create a Google *Web* OAuth client (authorised JavaScript origins = your app's origin) and configure the host —
  `Alvo__Accounts__Providers__Google__ClientIds__0`, `Alvo__Accounts__Issuer` (required outside Development),
  `Alvo__Secrets__EncryptionKeyFile`, `Alvo__Api__Cors__Origins__0` — the compose override, and the embedded two lines
  (`AddAlvoAccounts`/`MapAlvoAccounts`, `accounts.md`'s sample); (3) sign in from the page — `sign-in.js`, ~20 lines of
  plain `fetch`: Google Identity Services' `credential` → `POST /auth/v1/token` (RFC 8693 form) → keep the pair → call
  `POST /api/time_entries` with `Authorization: Bearer`; (4) keep the session — refresh before `expires_in` (rotation;
  two tabs inside 10 s get the same child; a reuse after it ends the session), `POST /auth/v1/logout`, `GET`/`PATCH
  /auth/v1/user`. *Where to keep the tokens, and why* (access token in memory; refresh token in `localStorage` or
  memory + re-sign-in, the XSS trade-off and CSP; why Alvo does not set a cookie — D7 and the CSRF surface). *How it
  behaves*: the access token says who, never what (claims list); roles read per request so a ban or removal lands on the
  next request; the four problem slugs; which emails Alvo trusts (§5.3) and that accounts are never linked by email;
  an operator's ban (`PUT …/accounts/{account}/disabled`). *Choosing a level*: the three-row table with links to the next
  two guides. *Not here yet*: other providers, email + password, account deletion (spec §10).
- [ ] **Step 3: `tenants-and-invitations.mdx` — "Tenants, members and invitations".** Tip: Acme Repairs — a dispatcher
  creates the company, invites a technician, and the technician sees only the jobs assigned to them. Steps: (1) choose
  level 2 or 3 (`01-level-two`, `02-level-three`; personal tenant vs shared; a person with no personal tenant and no
  header acts in no tenant — the 403 quoted from `003`); (2) create a tenant (`POST /auth/v1/tenants`, `creatorRoles`,
  `maxPerAccount`); (3) act in it — the tenant switcher (`GET /auth/v1/user` → `memberships`, then `X-Alvo-Tenant`) and,
  **said plainly**, a scoped create also carries `"tenant_id"` in its body (the refusal from `003` quoted); (4) invite
  (`POST …/invitations` → the code once; put it in your join link's **fragment**, `https://app.example/join#code=…`, so
  it never reaches a server log; `preview` without signing in; `accept`); (5) manage members (`GET`/`PATCH`/`DELETE
  …/members/{userId}`; leave; the last-owner rule; an owner's invitations die with their ownership). *Tenant roles and
  project roles*: §6.2's table and one-line rule, why a tenant role never reaches a global entity or Management (the
  `regions` example; `TenancyEngineFacts.A_self_made_dispatcher_cannot_write_global_regions`), apply refusing a tenant
  role in `access`, *owner* is a flag not a role. *Automation that runs as `System`* (§6.2's paragraph: a tenant member
  can trigger such a hook). *Invitation-only sign-up* (`invitation_code` on the exchange). *How it behaves*: every dead
  invitation is one 404, codes never in URLs, rate limits, the registry as the default tenant list under `selfService`.
- [ ] **Step 4: `tokens.mdx` — "Tokens for integrators".** Tip: give a billing system read access to Acme's completed
  jobs, and a script that acts as you. Steps: (1) turn them on (`01-access-tokens.alvo.json`; `service` needs
  `createTenants`); (2) mint (`POST /auth/v1/tokens` — personal vs service bodies, shown once, `header`); (3) use it
  (`X-Alvo-Api-Key`, scopes `<entity>:read|write`, no wildcard); (4) list and revoke (`GET /auth/v1/tokens`, `DELETE
  /auth/v1/tokens/{keyId}`). *Personal or service* (§7.1's table). *When someone leaves* — removal, demotion, a ban,
  expiry, revocation, each with what dies (D10 of S5; a rejoined person's old token stays dead). *Where a token is
  refused*: self-service routes and minting (403), the Management API (403, its fixed sentence), **a host's own
  `.WithAlvoCaller()` endpoints (403 until #536)**. *Limits*: `maxLifetimeDays`, 100 live per account, minting on the
  10/min budget.
- [ ] **Step 5: The sweep — for the claim, not the file.** `grep -rn -i "only local\|local accounts and API keys\|sign-in providers\|google\|tracked in\|#36\|personal token\|service token\|invitation" website/src/content/docs`
  and fix every claim S2–S5 made false: `why-alvo.md` (Google sign-in exists), `what-works-today.mdx` (`google`
  honoured; others warned), `roadmap.md` (the initiative shipped; what is next per spec §10), `authentication.mdx` (a
  *People sign in to your app* section pointing at the three guides; the dashboard paragraph unchanged),
  `multi-tenancy.mdx` (*Tenants people create* cross-link), `production.mdx` (the accounts keys: `Issuer`, key file,
  CORS origins, rate budgets), `security-model.md` (tenant vs project roles, one paragraph), `glossary.md` (*account*,
  *tenant role*, *personal token*, *service token*, *invitation*), `embed.mdx` (`MMLib.Alvo.Accounts` in the package
  list), `advanced-demo.mdx` (a *Self-service* row and section: D11's story, the test issuer, and that "Nothing is
  test-only" holds — the issuer stands in for Google, the product code is unchanged), `after-hooks-and-webhooks.mdx` +
  `automation.mdx` (§6.2's `System` note, one `:::caution` each), `http-conventions.mdx` (S5's deferred minor: move the
  app-token paragraph after the second `SourceExcerpt`, so the colon introduces the sample it belongs to).
- [ ] **Step 6: Run** `dotnet test --project test/MMLib.Alvo.DocsGen.Tests -- --filter-class '*AccountsReferenceTests*'`,
  `dotnet test --project test/MMLib.Alvo.Tests -- --filter-class '*DocsSnippetTests*'`,
  `dotnet test --project test/MMLib.Alvo.Schema.Tests`, then `cd website && npm ci && npm run build` once. Expected:
  PASS, and the build's link validation reports no broken link.
- [ ] **Step 7: Commit** — `docs(website): guides for sign-in, tenants and invitations, and tokens for integrators`

### Task 8: The contributor record closes S6

**Files:**
- Modify: `docs/architecture/accounts.md` (title and intro: the guides exist — link them; a new `## The demo (S6)`
  section: the stack's issuer (D3–D6), what `170-SelfService` proves case by case, the invariants table's new rows naming
  their cases; *Not in …* sections that still say "S6" or "guides arrive")
- Modify: `docs/superpowers/specs/2026-11-13-end-user-accounts-and-self-service-tenancy-design.md` (§12: a *Recorded
  decisions (S6 plan)* paragraph — D1, D2's `regions`, D4, D5, D7; §13: D12 and D13)
- Modify: `docs/PLAN.md` (S6 done; the initiative's line ends "S0–S6 complete; a whole-stack review precedes the merge";
  the F5 marker unchanged), `CHANGELOG.md` `[Unreleased]` (the demo, the guides, the reference page, the skill),
  `README.md` and `PACKAGE_README.md` (one feature bullet each: sign-in with Google, self-service tenants, integrator
  tokens — if they list features)

- [ ] **Step 1: Sweep for the claim** — `grep -rn -i "arrive in S6\|guides arrive\|S6 —\|until S6\|(S6)" docs src website/src plugins examples samples test/teapie-field-service --include='*.md' --include='*.mdx' --include='*.cs' --include='*.csx'`
  and `grep -rn "Users' guides" docs`; every hit is updated or is this plan.
- [ ] **Step 2: Write** the files above; `accounts.md`'s new rows: *people sign in through a real container and the
  wall holds* (`170-SelfService/001`–`012`), *the test key is the published set's private half and lives only under
  `test/`* (`FieldServiceTestIssuerTests`), *every route a guide names exists* (`AccountsReferenceTests`).
- [ ] **Step 3: Check** — `scripts/check-brief-freshness` (Expected: OK — the product spec and analysis are untouched).
- [ ] **Step 4: Commit** — `docs(accounts): the contributor record, the plan and the changelog close S6`

### Task 9: Slice gate

**Files:** none new (fixes only, each its own commit, if a gate finds something).

- [ ] **Step 1: Release build** — `dotnet build MMLib.Alvo.slnx -c Release`; Expected: 0 warnings, 0 errors (repeat in
  `mcr.microsoft.com/dotnet/sdk:10.0` if the local SDK differs from CI's — *Rings are Debug, CI is Release*).
- [ ] **Step 2: Fixtures** — `scripts/gen-prototype-fixtures --check`; Expected: exit 0.
- [ ] **Step 3: ring1** — `scripts/test-ring1`; Expected `[ring1] OK`. No `PublicApi.*.verified.txt` moved (D16).
- [ ] **Step 4: ring2** — `scripts/test-ring2` (Docker up); Expected: exit 0, `[lint-api] OK`.
- [ ] **Step 5: The TeaPie stacks** — `scripts/test-e2e`; Expected `[e2e] OK` (three stacks, every suite, the three
  PostgreSQL assertions).
- [ ] **Step 6: The admin e2e** — `scripts/test-admin-e2e`; Expected: green over the grown descriptor.
- [ ] **Step 7: The site** — `cd website && npm run build`; Expected: green, links valid.
- [ ] **Step 8: Security note for the PR report** (not the security-core checklist — no core code moved): the test key
  is test-only and held there by ring0 facts; `AllowHttpMetadata` lives only in the Development demo stack and the
  product refuses it elsewhere; the stack still binds loopback only; no secret in a commit (secrets key, dev keys, token
  secrets and refresh tokens are per run); the guides send codes in bodies and fragments, never query strings.
- [ ] **Step 9: Hand back** to the controller: plan-guard, the PR report, the PR against `accounts/s5-tokens`; then the
  whole-stack review the maintainer asked for before the stack merges. Flag: the eval (`scripts/eval-assistant`) is owed
  a run with the tenth skill, outside the rings.

## Self-review against the spec

| Spec requirement | Task |
|---|---|
| §12 descriptor gains `google`, `signUp open`, `selfService` (`createTenants`/`creatorRoles`, `invitations`, `assignableRoles`), `accessTokens` personal + service | 1 |
| §12 the sink serves discovery + a static JWKS; ID tokens minted in a `.csx` with a committed, labelled test-only key; `Authority` + `AllowHttpMetadata`; Development | 2 |
| §12 `170-SelfService` journey: sign in → *Acme Repairs* → invite → accept → technician sees only assigned → billing service token reads completed → removal refuses next request → refresh reuse after the window kills the session | 2, 3, 4 |
| §12 adversarial: foreign tenant header; tenant-A role absent in B and on `regions`; self-made dispatcher cannot write `regions`; expired/revoked/used-up one 404; personal token dies with membership; a token minting a token; a personal token at Management | 3, 4 |
| §12 API keys keep working beside sign-in; §14 AC7 every suite green (rings, both TeaPie stacks, admin e2e) | 1, 2, 9 |
| §13 Guide "Sign-in and accounts" (Google set-up, ~20-line `fetch`, refresh, sign-out, where to keep tokens + D7, sign-up policies, three levels) | 7 |
| §13 Guide "Tenants, members and invitations" (levels, personal vs shared, switcher via `/auth/v1/user`, invitations end to end, tenant vs project roles, owner) | 7 |
| §13 Guide "Tokens for integrators" (personal vs service, scopes, leaving) | 7 |
| §13 Reference: descriptor keys (DocsGen from schema — already) and routes from OpenAPI | 6 |
| §13 `accounts.md` contributor view; field-service README *Self-service*; PLAN records the initiative | 3, 8 |
| §11 S6: the `alvo-descriptor-*` skills learn the new keys | 5 |
| §6.2 "the guides say so next to the hook docs" (`System`-run automation) | 7 |
| S5 ledger: scoped create echoes `tenant_id` shown honestly; `.WithAlvoCaller()` refuses app tokens (#536) not promised; http-conventions wording | 3, 7 |
