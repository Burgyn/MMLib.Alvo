---
name: alvo-descriptor-accounts
description: Use when an Alvo descriptor change lets people sign in or run their own tenants — auth.providers, auth.signUp, auth.accessTokens and tenancy.selfService.
---

# Sign-in, self-service tenants and app tokens in an Alvo descriptor

Listing `google` in `auth.providers` lets people sign in, and changes what `'authenticated' in @user.roles` means:
from a caller an operator provisioned to anyone Google vouches for. Shapes:
`schema/project.schema.json#/properties/auth` and `schema/project.schema.json#/properties/tenancy`.

| Level | Adds | Gives |
|---|---|---|
| 1 per-person rows | `auth.providers` with `google`, `auth.signUp` | rules such as `created_by == @user.id` on an `audit` entity |
| 2 a tenant per person | + `tenancy.enabled`, `selfService.personalTenant` | every scoped row walled to its person |
| 3 shared tenants | + `createTenants`, `invitations`, `assignableRoles`, `auth.accessTokens` | teams with roles per tenant, and tokens |

## Keys, defaults and refusals

- `auth.signUp` — `mode` `open` or `invitationOnly`, optional `allowedEmailDomains`. No default: once `providers`
  lists `google`, a missing one is refused at `/auth/signUp` ("auth.signUp says nothing about who may"). With
  tenancy, `open` warns about each global entity write rule that admits `authenticated`. `invitationOnly` without
  `tenancy.selfService.invitations: true` warns at `/auth/signUp/mode`: nobody can be invited, so nobody new signs up.
- `tenancy.selfService` — absent: tenants come only from API keys and operators. Needs `tenancy.enabled: true`, else
  refused ("self-service tenants are tenants").
  - `assignableRoles` — the tenant roles, the only roles a membership carries. Each is declared in `auth.roles` and is
    not built in, else refused.
  - `personalTenant.roles` (default `[]`) and `createTenants.creatorRoles` must be in `assignableRoles`, else refused
    ("a membership can only ever carry a tenant role"). `createTenants.maxPerAccount`: 1–100, default 5.
  - `invitations: true` needs `createTenants`, else refused ("no shared tenant can exist").
- `auth.accessTokens` — `personal` and `service`, default `false`; `maxLifetimeDays` 1–365, default 90. `service`
  without `createTenants` is refused at `/auth/accessTokens/service`: a service token acts as a shared tenant.

## Tenant roles are not project roles

**A tenant role counts only while every entity on the decision's path is scoped**: never in a rule of a
`tenancy: global` entity, never in `access`. Whoever creates a tenant holds its `creatorRoles`, so naming a tenant
role in an `access` level is refused ("is a tenant role"): give the tenant role a name no `access` level uses.
Owning a tenant is no role: owners are a membership flag, never in `@user.roles`. An app may declare a role named
`owner`, even as a tenant role, but holding it makes nobody an owner; to let rules name the people who run a tenant,
give them a role through `creatorRoles`.

## What the descriptor does not do

- Sign-in is host configuration: `Alvo:Accounts:Providers` on the standalone host, `AddAlvoAccounts` in an embedded
  one. A host without it only warns at boot that nobody can sign in with Google; the apply still succeeds.
- A scoped create needs no `tenant_id` in its body: it lands in the tenant `X-Alvo-Tenant` names, or the person's
  personal tenant without the header. A `tenant_id` naming any other tenant is 403, with the fix in `detail`.
- An app token (`alvo_pat_…` personal, `alvo_svc_…` service) is sent as `X-Alvo-Api-Key`; as `Authorization: Bearer`
  it is 401. The Management API refuses app tokens. The `/auth/v1` user, tenant and token routes take only a person's
  Bearer token: any API key there is 401.

<!-- example: google-sign-in -->
**Let workshop customers sign in with Google.**

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Lets anyone with a Google account sign in.",
 "operations": [{"op": "add", "path": "/auth/providers/-", "value": "google"},
                {"op": "add", "path": "/auth/signUp", "value": {"mode": "open"}}]}
```

```json
{"valid": true, "changedPaths": ["/auth/providers/1", "/auth/signUp"]}
```

Signing in decides who gets in, never which rows: every `list` and `get` rule admitting `authenticated` — six
entities in `bike-workshop` — now admits any Google account. Narrow each in the same change
(`alvo-descriptor-rules-and-cel`). Do not offer `invitationOnly` here: it needs invitations, which need shared tenants,
and `bike-workshop` has no tenancy, so nobody could ever sign up.

<!-- example: tenant-role-in-access -->
**Make manager a role people hold in their own workshop.**

```json
{"tool": "check_change", "baseRevision": 1,
 "operations": [{"op": "add", "path": "/tenancy",
                 "value": {"enabled": true, "selfService": {"assignableRoles": ["manager"]}}}]}
```

```json
{"valid": false, "changedPaths": ["/tenancy"],
 "violations": [{"pointer": "/access/developer", "severity": "error",
                 "message": "'manager' is a tenant role (tenancy.selfService.assignableRoles), and a tenant role can never manage the project."}]}
```

Stop and explain: `manager` is the `developer` level's role, and a tenant role never manages the project. Offer a
new tenant role, declared in `auth.roles`, instead.

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operations.
In this repo: dry-run the edited `*.alvo.json`: `PUT /management/projects/<project>/descriptor?dryRun=true` (Alvo repo: `scripts/test-ring0`).
