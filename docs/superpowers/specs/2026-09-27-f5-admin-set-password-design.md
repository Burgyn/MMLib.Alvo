# Item 30: a person the dashboard creates can set a password (design)

Status: design, executed on `f5/ai-agent` (PR #264). Rulings (controller, 27 Sep 2026) in §10. Security core (identity, sessions, auth endpoints): `alvo-security-core-review`
and `/security-review` apply to the implementation.

## 0. What the code does today (read, not assumed)

- `AlvoIdentityUserAdministration.CreateRowAsync` creates the row with no password.
  `MintCredentialTokenAsync` calls `GeneratePasswordResetTokenAsync` (the one registered provider is
  `DataProtectorTokenProvider` under `TokenOptions.DefaultProvider`) and states `ExpiresAt` as
  "now + 1 day" without reading the provider's options.
- `GuardedUserAdministration` refuses to issue for the bootstrap admin. Route:
  `POST {m}/projects/{p}/users/{id}/credential-reset` returns the raw token.
- The host maps two posts (`AlvoAdminSignIn`). Both are antiforgery-checked by hand and
  `AllowAnonymous`. `AlvoSignIn` is public and says it has "two methods and no more".
- **Three gaps this design has to close or record. They are not in item 30's text:**
  1. **There is no rate limiter anywhere.** Sign-in is protected only by Identity's per-account lockout
     (`lockoutOnFailure: true`). "The same rate limit as sign-in" does not exist yet.
  2. **Session revalidation ignores the security stamp.** `AlvoSessionValidation.StillStandsAsync`
     only checks that the account exists and is not disabled. A password reset rotates the stamp, but
     no open session notices. baas-analyza §2.2 *Pozor na* ("invalidácia sessions po zmene hesla")
     and its acceptance criterion ("password change → staré sessions mŕtve") are not met.
  3. Pre-existing, **out of scope**: `AlvoSignIn.PasswordSignInAsync` returns before hashing when the
     email is unknown, which is a timing oracle for enumeration on sign-in. File it as a new todo item.

## 1. The flow

1. An admin opens Access → person → **Issue a credential token**. The core decorator still refuses
   the bootstrap admin. The token panel now shows a **set-password link** with **Copy link**, plus
   "Hand this over out of band: this build sends no email. It works once, until …". If the person is
   disabled, the panel adds "They cannot use it until you let them back in."
2. The link is `{origin}{PathBase}/admin/set-password#email=<esc>&token=<esc>`. The dashboard builds
   it (internal `SetPasswordLink.For(NavigationManager, email, token)`, from `NavigationManager.BaseUri`
   so PathBase and forwarded origin are respected) with `Uri.EscapeDataString` on both values.
   The Management API response is unchanged: it returns the raw token, and a CLI or agent composes the
   link from the public constant `AlvoAdmin.SetPasswordPath`.
3. The person opens the link. The static-SSR page `/admin/set-password` runs a small script that reads
   the fragment, fills the hidden-by-default email and token inputs, and calls
   `history.replaceState` to remove the fragment from the address bar and history.
4. They type the new password twice and submit. The form posts to `/admin/set-password/submit`
   (the host's endpoint), which redeems the token.
5. On success: a 303 redirect to `/admin/sign-in?passwordSet=true`. The sign-in page shows "Your
   password is set. Sign in with it." They then **sign in normally**.

### Why the fragment and not the query string
The token is a bearer credential. Browsers never send a fragment: not to the server, not to a proxy's
access log, and not in `Referer` (fragments are always stripped from it). A query string ends up in
Kestrel's `Request starting …` line (hidden in the image only because `Microsoft.AspNetCore` is set to
`Warning`, which an embedded host may change), in reverse-proxy logs and in APM traces.
OWASP's Forgot Password examples use a query string. This is a **deliberate deviation**: it keeps the
token out of every log without depending on anyone's log configuration.
The cost is JS-off: the fragment then stays in history, and the email and token inputs show (a
`<noscript>` note plus visible fields) so the person pastes the token in by hand. That works without
JS, as the sign-in page does.

### Leakage mitigations on the page and the endpoint
- `Referrer-Policy: no-referrer` and `Cache-Control: no-store` on the page's response (set from the
  cascaded `HttpContext`) and on the submit's redirects. This is defence in depth: the page loads only
  same-origin assets today, and this header stops a future link or asset from leaking.
- Inputs: `autocomplete="username"` (email), `autocomplete="new-password"` (both password fields),
  `autocomplete="off"` and `spellcheck="false"` on the token.
- Nothing logs the token, the password or the typed email. Success is logged at Information with the
  user id only. A refusal is logged at Information with no identifiers (the email is attacker input).
- The form is `data-enhance="false"`, as on sign-in: the answer is a redirect, not a DOM patch.

## 2. The unauthenticated page and endpoint

**Page** `Components/Shell/SetPassword.razor`: `@page "/admin/set-password"`, `SignInLayout`,
`[ExcludeFromInteractiveRouting]`, `[AllowAnonymous]` (so the cookie re-check skips it, as it skips
sign-in), native markup per D10, `<AntiforgeryToken />`. It has: email (read-only once prefilled),
token (hidden once prefilled), **New password** with `minlength=15 maxlength=128`, **Repeat it**, and
the policy stated statically under the field. The query-string states it renders are:

| query | text (never says which half was wrong) |
|---|---|
| `failed=true` | "This link does not work. It may have been used already, have expired, or been copied incompletely. Ask your administrator for a new one." |
| `problem=mismatch` | "The two passwords are not the same." |
| `problem=weak` | "Choose a password of at least 15 characters that is not your email address." |
| `throttled=true` | "Too many attempts from this network. Wait a minute, then open your link again." (amended, ruling 10.7: a throttled post comes back without the fragment, so the boxes are empty; sign-in keeps "Wait a minute and try again.") |

For `mismatch` and `weak`, the endpoint's redirect `Location` carries `#email=…&token=…` back, so the
person does not have to reopen the link. The browser keeps the fragment, and it never appears in a
request line. `failed` returns **no** fragment.

**Endpoint** `POST AlvoAdmin.SetPasswordEndpoint`, in `AlvoAdminSignIn.MapAlvoAdminSignIn`. It sits
behind the same `AlvoAdminOptions.Enabled` switch and is `AllowAnonymous().ExcludeFromDescription()`.
The steps, in order:
1. Validate antiforgery (`ValidAsync`). On failure: redirect to the page, write nothing.
2. Read the form and bound the input (email ≤ 256, token ≤ 2048, password ≤ 128). Anything over is
   treated as `failed` or `weak`. Trim the token, and turn spaces back into `+`: base64 never
   contains a space, so this safely repairs a token that went through `URLSearchParams` or was pasted
   with a `+`.
3. If the passwords differ → `problem=mismatch`. Nothing touches the store.
4. `await signIn.SetPasswordAsync(email, token, password)`, then map the outcome:
   `Set` → sign-in with `passwordSet=true`; `PasswordRejected` → `problem=weak`; `Refused` → `failed=true`.

**Rate limit, shared with sign-in (new; superseded in shape by the §8.7 amendment, ruling 10.6: two layers, charged by the endpoints after antiforgery, no middleware).** The host adds `AddRateLimiter` with **one** named policy,
`alvo-admin-credentials`: a fixed-window partitioner keyed on `Connection.RemoteIpAddress` (correct
behind a proxy only when `ForwardedHeaders.Enabled` is on, which `docs/architecture/host.md` must say),
`QueueLimit = 0`. Both `SignInEndpoint` and `SetPasswordEndpoint` call
`.RequireRateLimiting("alvo-admin-credentials")`, so one policy means one partitioned limiter and one
budget per client across both forms. The default is **20 attempts per minute**. It is read internally
from `Alvo:Admin:CredentialAttemptsPerMinute` (no public options property; validated > 0 at start) so
the e2e world can raise it, and the throttling scenario can lower it. `OnRejected` answers
`303` to the originating page with `throttled=true` and a `Retry-After`, never a bare 429 that a form
post would show as a blank error. `app.UseRateLimiter()` goes after `UseAuthentication`, before
`UseAntiforgery`, and the ordering comment block in `Compose` gains the reason.
Identity's per-account lockout still applies to sign-in and does **not** apply to token redemption,
which is correct: the token is a MAC'd data-protection payload and cannot be guessed. The limiter
exists there to bound PBKDF2 CPU cost on the success path and to stop spraying.

### Why no automatic sign-in after success
- OWASP Forgot Password CS: have the user log in through the usual mechanism, because auto sign-in
  adds session-handling complexity to the reset code.
- An intercepted link would otherwise be a *session* link, not just a password link.
- Signing in once proves the password was typed and stored correctly (the password manager saved it
  against `username`).
- Lockout, the rate limit and the sign-in audit stay on one path.
- This is also what ASP.NET Core Identity UI's `ResetPassword` does, so it is not a deviation.

## 3. Redemption in `MMLib.Alvo.Identity`

The new member is `AlvoSignIn.SetPasswordAsync(string email, string token, string newPassword) →
Task<AlvoPasswordSetOutcome>`. It runs inside `AlvoIdentityUnitOfWork.RunAsync` (one transaction; a
lost concurrency stamp becomes `Refused`). The order is a **security property**, which is why it lives
in the package rather than in each host:

1. **Policy first, independent of the account.** Run `UserManager.PasswordValidators` against a
   placeholder `new AlvoIdentityUser { UserName = email, Email = email }`, never the stored row, so the
   result is identical whether or not the address exists. On failure → `PasswordRejected`. A weak
   password therefore never reveals anything about the token or the account.
2. `FindByEmailAsync(email)`. If there is no row, **or** `AlvoIdentityLockout.IsDisabled`, **or**
   `IAlvoBootstrapAdmin.IsBootstrapAdmin` → `Refused`. None of these branches consumes anything.
   The bootstrap check is defence in depth: the only route that mints tokens already refuses the
   bootstrap admin, and the unguarded keyed implementation must not become a way around that.
3. `ResetPasswordAsync(row, token, newPassword)`. Identity verifies purpose `ResetPassword`, the user
   id inside the token (**another user's token fails here**), the security stamp inside the token, and
   `TokenLifespan`. It then re-runs the validators, hashes the password, rotates the stamp and saves.
   On failure → `Refused`.
4. On success: `ResetAccessFailedCountAsync`, and when `LockoutEnd` is a *temporary* lockout (not the
   disabled sentinel), `SetLockoutEndDateAsync(null)` → `Set`.

**Timing and enumeration.** No failure path hashes a password. The unknown-email path and the
wrong-token path each do one indexed lookup and at most one data-protection unprotect (microseconds).
So they cost the same without a dummy hash. Only the success path is slow, and success is not an
oracle, because the caller already holds a valid token. Every `Refused` produces a byte-identical
redirect.

**Lifetime and single use.**
- `AddIdentityCore` gains `services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan
  = TimeSpan.FromDays(1))`. This is the Identity default, now stated. A host that registers its own
  `Configure` afterwards overrides it.
- `MintCredentialTokenAsync` reads `IOptions<DataProtectionTokenProviderOptions>` and reports
  `ExpiresAt = issued + TokenLifespan` exactly. This replaces the "stated as the default" comment.
- Single use comes from the stamp: redeeming rotates it, so the used token **and every other
  outstanding token** for that person die. Two concurrent redemptions: the transaction or concurrency
  stamp lets exactly one succeed.
- Issuing a token does **not** rotate the stamp (see deviations).
- Tokens are protected with the host's data-protection key ring, so a container with an unpersisted
  ring loses outstanding tokens on restart, as it loses sessions. This fails closed. The link text
  says "ask for a new one", and `host.md` should note it.

**Password policy (NIST SP 800-63B-4 §3.1.1.2, password as the only factor).** It is configured once,
in `AddIdentityCore`:
- `RequiredLength = 15`, and no composition rules: `RequireDigit`, `RequireLowercase`,
  `RequireUppercase` and `RequireNonAlphanumeric` all `false`, `RequiredUniqueChars = 1`.
- A new internal `AlvoPasswordValidator : IPasswordValidator<AlvoIdentityUser>` refuses more than 128
  characters, and refuses a password equal to or containing the address or its local part (case-
  insensitive; the local part only from three characters, §8.12). That is the context-specific blocklist
  NIST asks for.
- Paste is allowed; there is no rotation and no hint.
- This is **one policy for everyone**, so it also governs the bootstrap seed. An already-seeded account
  is never rewritten, and a new deployment whose secret the policy refuses (too short, over 128, or
  containing the address) is refused at start. The check lives in `AlvoIdentityBootstrap.CreateAsync`,
  on the branch that creates the account, and gives a structured `AlvoIdentityConfiguration` sentence
  instead of Identity's error surfacing from the seed. **Not** in `AlvoIdentityOptionsValidation`
  (this design's first draft): options validation cannot tell a new seed from an already seeded
  account, so it would refuse the restart of a deployment ruling 10.2 says keeps working, and that
  class deliberately never reads the secret (§8.11).
  Every repository fixture is ≥ 15 (e2e 21, demo 21, identity tests 15).
- The Admin package cannot reference Identity, so it keeps its own `15` for `minlength` and the policy
  text, and a Host test pins the two together (the `TenantClaimType` precedent).

## 4. Security stamp and existing sessions

`AlvoSessionValidation.StillStandsAsync` gains a third condition. The principal's
`IdentityOptions.ClaimsIdentity.SecurityStampClaimType` claim must equal the stored stamp, which is
read as an untracked scalar by primary key from `AlvoIdentityDbContext` (the item-35 rule: never a
tracked `UserManager` read). A missing claim is a refusal (default-deny). Every cookie minted so far
carries one, because `UserClaimsPrincipalFactory` adds it whenever the EF store supports stamps.
Both transports share this method:
- **Cookie**: the next guarded request is rejected and the cookie is cleared.
- **Circuit**: the 30-second revalidation drops an open tab to sign-in.

This is the check of ASP.NET's `SecurityStampValidator`, without its 30-minute throttle, for the reason
the class already gives. Role and tenant writes do not rotate the stamp in `UserManager` (only
password, email, username, login and 2FA writes do). **A disable does rotate it** (ruling 10.5): the
administration calls `UpdateSecurityStampAsync` in the disable's own unit of work, so every cookie and
circuit the person holds ends, every outstanding credential token dies, and "let them back in" revives
neither; the person signs in again. So the check ends sessions on a credential change and on a disable,
never on a change to what a person may do. It adds one PK read per guarded HTTP request, and the
remarks' "two reads" becomes three. The class and the revalidating provider's remarks are updated.

## 5. Public API impact

| Symbol | Package | Why public |
|---|---|---|
| `AlvoSignIn.SetPasswordAsync` | Identity | `AlvoSignIn` exists because a host cannot name `UserManager<AlvoIdentityUser>`. An **embedded** host rendering its own screen needs the same redemption, so internal + IVT to the Host would serve only one of the two distributions. |
| `enum AlvoPasswordSetOutcome { Set, Refused, PasswordRejected }` | Identity | The host must tell a weak password (safe to say) from a refusal (generic). A `bool` would force a second call and move the policy-before-token order into every host. Three members and no more. `Refused` deliberately merges unknown, disabled, bootstrap, expired, reused and foreign. |
| `AlvoAdmin.SetPasswordPath`, `AlvoAdmin.SetPasswordEndpoint` | Admin | The same argument as `SignInPath`/`SignInEndpoint`: the host maps the endpoint, and a CLI or agent composes the link. |

Everything else is internal: `AlvoPasswordValidator`, `SetPasswordLink`, the stamp read, the rate-limit
policy, and the configuration key. `SetPassword.razor` is public only because the Razor SDK makes it so
(`Components.*` is not contract).
Abstractions do not change. Only the remarks on `IAlvoUserAdministration.IssueCredentialTokenAsync`
("nothing delivers the token") and `CreateAsync` are corrected to name the set-password page.
`AlvoSignIn`'s "two methods and no more" remark is rewritten to say why redemption is the third.
Expect the `PublicApi.MMLib.Alvo.Identity` and `…Admin` baselines to grow. The turn-review gate will
send the change to `alvo-architecture-rules`, and the justification is this table.

## 6. Files to touch

- `src/MMLib.Alvo.Identity/AlvoSignIn.cs`: `SetPasswordAsync`, remarks; the constructor also takes
  `IAlvoBootstrapAdmin` and `AlvoIdentityDbContext`, wired in the factory lambda.
- `src/MMLib.Alvo.Identity/AlvoPasswordSetOutcome.cs` (new).
- `src/MMLib.Alvo.Identity/AlvoIdentityAuthenticationExtensions.cs`: factory lambda, and the remarks
  on the stamp.
- `src/MMLib.Alvo.Identity/AlvoIdentityServiceCollectionExtensions.cs`: password options, validator,
  token lifespan.
- `src/MMLib.Alvo.Identity/Internal/AlvoPasswordValidator.cs` (new).
- `src/MMLib.Alvo.Identity/Internal/AlvoIdentityUserAdministration.cs`: `ExpiresAt` from options.
- `src/MMLib.Alvo.Identity/Internal/AlvoSessionValidation.cs` and
  `AlvoIdentityRevalidatingAuthenticationStateProvider.cs`: the stamp check and its remarks.
- `src/MMLib.Alvo.Identity/Internal/AlvoIdentityBootstrap.cs` (`CreateAsync`) and
  `AlvoIdentityConfiguration.cs`: the bootstrap seed's policy refusal (§3, §8.11); `StartingAsync` also
  makes the timing-parity hash (`AlvoTimingParity`, new, §8.14).
- `src/MMLib.Alvo.Abstractions/Identity/IAlvoUserAdministration.cs`: remarks only.
- `src/MMLib.Alvo.Admin/AlvoAdmin.cs`: the two constants.
- `src/MMLib.Alvo.Admin/Components/Shell/SetPassword.razor` (new); `SignIn.razor`: the
  `passwordSet` notice.
- `src/MMLib.Alvo.Admin/Internal/SetPasswordLink.cs` (new).
- `src/MMLib.Alvo.Admin/Components/Access/PersonEditor.razor` and `Access.razor`: link, Copy link, the
  disabled note, `EditorLeaveQuestion.UncopiedToken` wording.
- `src/MMLib.Alvo.Admin/wwwroot/admin.js`: fragment → inputs → `replaceState`, and the client-side
  mismatch hint.
- `src/MMLib.Alvo.Host/Internal/AlvoAdminSignIn.cs`: the endpoint and the rate-limit metadata on both
  posts. `src/MMLib.Alvo.Host/AlvoHost.cs`: `AddRateLimiter`, `UseRateLimiter`, ordering comment.
- Docs: `docs/architecture/host.md` (set-password, rate limit, forwarded headers, key ring, policy
  ≥ 15); `docs/todo-admin.md` item 30 ✅ (the sign-in timing oracle is fixed in T1, ruling 10.3, so it
  needs no new item).

## 7. Tests (adversarial cases in bold)

**Unit, `MMLib.Alvo.Identity.Tests`** (real SQLite and `UserManager`, as `AlvoIdentitySessionTests` does):
- valid → `Set`; the new password signs in; the lockout count is cleared;
- **reused** token → `Refused`;
- **expired** → `Refused`. Use `FakeTimeProvider` if the pinned `DataProtectorTokenProvider` reads a
  `TimeProvider`; otherwise use a registration with `TokenLifespan = 1 ms`;
- **wrong email** (a real colleague's address with Eva's token) → `Refused`, and neither password moves;
- **unknown email** → `Refused`;
- **another user's token** → `Refused`;
- **tampered or truncated token** → `Refused`;
- **disabled** → `Refused`; the disable rotated the stamp, so the old token stays `Refused` after "let
  back in" and a new one works (ruling 10.5);
- **bootstrap token minted through the keyed unguarded implementation** → `Refused`, and the file
  password still signs in;
- **weak** (14 chars; contains the email) → `PasswordRejected`, both for a real address and an unknown
  one, and the token is still valid afterwards;
- **two outstanding tokens**: redeeming one kills the other;
- **two parallel redemptions** → exactly one `Set`;
- **no hashing on any failure path**: a counting `IPasswordHasher` decorator sees 0 calls. This is
  the deterministic proxy for timing parity, because wall-clock assertions are flaky;
- `ExpiresAt` equals issue time + `TokenLifespan`, not "about";
- policy: 15 accepted, 14 refused, no composition required, 129 refused.

**Sessions** (`AlvoIdentitySessionTests` and the provider tests):
- **a cookie minted before a set-password** is rejected and cleared on its next guarded request;
- **a circuit state from before** fails `StillStandsAsync`;
- **a cookie with the stamp claim stripped** is refused;
- a role change does **not** end the session.

**Host integration, `MMLib.Alvo.Host.Tests`** over `AlvoHostWorld`:
- **a POST without an antiforgery token** changes nothing, and the token is still redeemable;
- **login-CSRF analogue**: a cross-site form carrying another antiforgery cookie is refused;
- a good POST → 303 to `sign-in?passwordSet=true`, then sign-in succeeds;
- **the `Location` header is byte-identical** for unknown email, wrong token, disabled, bootstrap and
  expired, and it carries no fragment;
- mismatch and weak redirect with the fragment carried back;
- **throttling**: with the limit set to 3, the 4th POST is throttled; **the budget is shared**, so three
  sign-in failures throttle the set-password POST;
- `Enabled=false` → page and endpoint both 404;
- the GET page sends `Cache-Control: no-store` and `Referrer-Policy: no-referrer`;
- **a captured log sink contains neither the token, the password nor the email**;
- the OpenAPI path set is unchanged (`ExcludeFromDescription`; TeaPie pins it by equality);
- the Admin `15` equals Identity's `RequiredLength`.

**Admin unit tests**: `SetPasswordLink` escapes `+ / = @ & #`, and a round trip through
`URLSearchParams` semantics gives the token back; the token panel renders the link, and Copy copies
the link.

**E2E, `MMLib.Alvo.Admin.Tests.EndToEnd`**:
- `A_person_the_dashboard_creates_can_set_a_password_and_sign_in`: create → issue → read the link →
  a fresh browser context opens it → **the address bar has no fragment after load** → set a password →
  the sign-in notice → sign in as them → the shell renders for their role;
- **the same link a second time** → the generic refusal;
- **with JavaScript disabled**, pasting the token by hand also works;
- an admin's open tab for that person, signed in before a password is set elsewhere, drops to
  sign-in within the revalidation interval.
Extend `A_second_person_can_be_created_and_gets_a_credential_token` to assert the link.

## 8. Deliberate deviations from OWASP and Identity defaults

1. **The link is handed over by an administrator, not emailed to the account's address.** There is no
   mail transport (spec: a mailer is a subsystem, not a screen's side effect). The panel says so. The
   trust assumption is the admin's out-of-band channel.
2. **The token is in the fragment, not the query string**: it stays out of server, proxy and APM logs
   and out of `Referer`. The cost is a small script and a history entry when JS is off (§1).
3. **The token lifetime is 24 h, the Identity default.** OWASP asks for "short" and gives no number.
   This link is an invitation handed over by a person, not a self-service reset, and a host can
   shorten it.
4. **Issuing does not revoke earlier outstanding tokens.** Rotating the stamp at issue would sign the
   person out merely because an admin clicked a button. All outstanding tokens die at the first
   redemption, at expiry, or at a key-ring change.
5. **Other sessions end automatically** rather than being offered as a choice (OWASP): this is
   stricter, and it is what baas-analyza's criterion demands.
6. **The password policy is NIST's, not Identity's**: 15 characters minimum, no composition rules,
   128 maximum. There is **no breached-password corpus check**, although NIST says SHALL: the image
   is offline, and no list ships. Only the context-specific (address) blocklist is applied. Follow-up.
7. **The rate limit is per client IP, a fixed window, with no CAPTCHA.** Identity's lockout does not
   cover redemption, by design (the token cannot be guessed).
   *Amended (ruling 10.6):* a single per-IP budget let one anonymous client lock everyone behind a shared
   address (a NAT, a proxy that forwards no client address) out of sign-in, the bootstrap admin included. So
   the limit has two layers, both fixed windows with no queue: the design's **20 a minute per (client,
   normalised address)** on sign-in and **per (client, hash of the token's first 64 characters)** on
   set-password, under a coarse **200 a minute per client** across both forms that bounds the hashing cost of a
   client cycling subjects (`Alvo:Admin:CredentialCeilingPerMinute`, internal, validated like the other). A
   client is its IPv4 address, or its IPv6 /64, with an IPv4-mapped address read as IPv4. Both are **charged
   by the endpoint after the antiforgery check**, not by `UseRateLimiter` middleware in front of it, so a
   cross-site page cannot spend a victim's budget; the pipeline has no rate-limiter middleware. Both counts are
   validated only when the dashboard is enabled. Both anonymous posts refuse a body over 16 KB before reading it. *Residual, accepted (ruling 10.8):* this
   makes a shared-address lockout ten times costlier, not impossible — a client cycling 200 distinct subjects a
   minute, each with a valid anonymous antiforgery pair, still spends the ceiling for everyone behind its address;
   requests with no remote address share one partition; and Identity's per-account lockout still lets anyone keep a
   known account locked (todo item 46). The ceiling is checked without spending it before a subject's partition is
   created, and every subject is a fixed-length hash (the sign-in address through the registered
   `ILookupNormalizer`, capped at 256 characters), so the limiter's memory is bounded by the ceiling.
8. **A temporary lockout is cleared on success.** Identity leaves it. The lockout protected the old
   password, and the holder just proved possession of an admin-issued token.
9. **Disabled accounts and the bootstrap admin are refused at redemption.** Identity's
   `ResetPasswordAsync` would reset both.
10. Unchanged and recorded: Identity v3 PBKDF2 hashing, where baas-analyza says Argon2id. The sign-in
    timing oracle (§0.3) is **fixed** by ruling 10.3 (see 13 and 14); a residue of about a millisecond
    remains (a wrong password for a live account also writes its failed-attempt count), bounded by the
    shared credential rate limit.
11. **The bootstrap seed's policy check is in `AlvoIdentityBootstrap.CreateAsync`, not in
    `AlvoIdentityOptionsValidation`** (§3): only the create branch knows the seed is new.
12. **The local part is a blocklist word only from three characters.** `ab@…` would otherwise refuse every
    password containing "ab", a policy nobody could satisfy by intent. The whole address is refused at any
    length.
13. **The dummy verification covers every sign-in refusal Identity makes without hashing**: an unknown
    address, an account with no password yet, and a locked-out or disabled one. They were the same
    enumeration oracle. A no-password account therefore no longer counts a failed attempt (it has no
    password to protect).
14. **The dummy hash is made by the registered `IPasswordHasher`, once, at start** (`StartingAsync`, lazily
    on first use as a fallback), never a fixed v3 blob (ruling 10.4): a raised iteration count or a
    replaced hasher (Argon2id) keeps the dummy at the stored hashes' cost.
15. **A disable rotates the security stamp** (ruling 10.5), which Identity does not do: OWASP ends
    sessions on a disable, and without it a cookie unused during the disable stood again after "let back
    in". The cost: a person let back in signs in again, and needs a new token if they had none set.

## 9. Task list (SDD)

- **T1: Identity redemption and sessions (security core).** Password options, `AlvoPasswordValidator`,
  token lifespan and the exact `ExpiresAt`, `SetPasswordAsync` and `AlvoPasswordSetOutcome` in a unit
  of work, the lockout clear, the bootstrap-length refusal, the stamp check in `AlvoSessionValidation`
  (cookie and circuit), and the remarks. Tests: every Identity unit and session case in §7. The
  PublicApi baseline grows by exactly two symbols. Run `alvo-security-core-review` and a security
  reviewer subagent.
- **T2: Host endpoint, rate limit and dashboard.** `AlvoAdmin` constants, `SetPassword.razor`, the
  `SignIn` notice, `SetPasswordLink`, the token panel's link and Copy link, the `admin.js` fragment
  handling, the endpoint in `AlvoAdminSignIn`, the shared `alvo-admin-credentials` limiter on both
  posts with its configuration key, and the headers. Tests: host integration and Admin unit cases in
  §7. Admin PublicApi grows by two constants.
- **T3: E2E and docs.** The four e2e scenarios, raising the e2e world's attempt limit, `host.md`, and
  todo item 30 ✅ (no timing-oracle item: fixed in T1). Then run the full `scripts/test-admin-e2e`,
  plan-guard, and the PR report.

## 10. Rulings (controller, 27 Sep 2026)

1. The design is adopted as written, including the two gaps of §0 (the shared credential rate limit and the
   security-stamp check on every session).
2. The NIST policy (15–128, no composition rules, no address in the password) is accepted before 1.0: every
   repository fixture already complies; an existing account is never rewritten; a *new* bootstrap seed under
   15 characters is refused at start with a structured `AlvoIdentityConfiguration` sentence.
3. §0.3's sign-in timing oracle is **fixed in T1**, not filed: T1 already rewrites `AlvoSignIn`, and the fix is
   one dummy verification (the hasher against a fixed hash) on the unknown-email path, bounded by the new
   limiter. The deviation list's item 10 changes accordingly.

Rulings on the T1 review (controller, 27 Sep 2026):

4. The dummy hash is generated once through the registered `IPasswordHasher` (at `StartingAsync`, lazy
   fallback), never hard-coded (§8.14). The three T1 deviations (§8.11–13) are accepted.
5. Disabling a person rotates their security stamp in the same unit of work, so every existing cookie and
   circuit ends and "let back in" never revives an old session (§4, §8.15).

Rulings on the T2 review (controller, 27 Sep 2026):

6. The credential limit is two-layered and charged after antiforgery (§8.7 amendment): per (client, address) on
   sign-in and per (client, token-prefix hash) on set-password at 20 a minute, under a 200-a-minute per-client
   ceiling; IPv6 by /64, IPv4-mapped as IPv4; a throttled sign-in keeps its `returnUrl`; a 16 KB body limit on both
   anonymous posts; the counts are validated only when the dashboard is on.
7. A throttled set-password page says "Too many attempts from this network. Wait a minute, then open your link
   again." (§2 table). The dashboard under a PathBase is todo item 45.

Rulings on the T2 re-review (controller, 27 Sep 2026):

8. The limiter's memory is bounded (ceiling checked before a subject partition exists, hashed subjects, a 256-character
   address cap, `ILookupNormalizer` for the sign-in key); a chunked body cut off past 16 KB answers the page; sign-out
   gets the same 16 KB bound. The per-client ceiling's residual (200 subjects a minute still throttle one address) and
   the shared partition for requests with no remote address are accepted and documented. Identity's per-account
   lockout stays — it is the only defence against a distributed guesser — and its denial-of-service trade-off (about
   one wrong post a minute keeps a known account, the bootstrap admin included, locked) is accepted before 1.0, to be
   stated in `host.md` (T3), with todo item 46 for a better scheme.
