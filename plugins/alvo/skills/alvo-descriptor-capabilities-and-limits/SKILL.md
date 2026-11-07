---
name: alvo-descriptor-capabilities-and-limits
description: Use when asked whether Alvo can do something, or when a change needs a block or an action this build may not run — what is honoured, what is only warned, and what is refused.
---

# What this build of Alvo honours

The capability report is the authority, not memory and not other frameworks: quote it. It lists every block and
slot that is not honoured, each with the consequence and the fix in the framework's own words.

These top-level blocks are honoured:

<!-- gen:honoured -->
`entities` `auth` `tenancy` `access` `formats` `automation`
<!-- /gen:honoured -->

These are declared by the schema and accepted, and are not honoured, or only in part. The apply warns about all but
`entity.realtime`, and a descriptor that sets them looks as if it works:

<!-- gen:warned -->
`dynamicEntities` `templates` `webhooks` `functions` `auth.providers` `entity.storage` `entity.realtime`
<!-- /gen:warned -->

**Declarative actions.** `webhook`, `email`, `function` and `entity.update` run from after-hooks and `automation` rules,
each action as its own retried delivery; `entity.update` writes once per event, as the system in the event's tenant,
under the target's rules. A `webhook` or `email` names an endpoint under `webhooks.endpoints` or a template under
`templates` the descriptor already declares — never a new `webhooks` or `templates` block, which is a warned block. A
`function` names one the host registered (`get_extensions`). Deliveries stay **unsigned**: no `secretRef` is read, no
HMAC header is sent and the payload is not projected per endpoint, so the receiver cannot verify the sender. Say so
when you offer one. `http.call`, `delivery: batch` and `auth.*`/`storage.*` triggers are refused. An after-hook
`condition` or template may read `@tenant.id` and `@user.roles`: the writer's tenant and roles as the event recorded
them.

These are refused at apply, each with its reason in the capability report (`field.default` is a `$cel` default; a
literal is honoured):

<!-- gen:refused -->
`field.default` `entity.softDelete` `trigger.event` `batch` `trigger.source` `JSONata` `email.data` `bodyFile` `http.call`
<!-- /gen:refused -->

The after-hook action types in this list are refused as actions, not as blocks. The skills of each area say what to use instead.

## How to answer

- Never propose an unhonoured block or a refused action, and never describe one as working.
- Offer the lowest honoured rung that does the job: a rule, a before-hook, a rollup, a computed field, or a
  declarative action above. Say what it does not do.
- Never promise a date or a release for what is missing.
- Rows are not the descriptor: the dashboard assistant never reads or writes rows and never applies a change.

What the missing parts are, so they can be explained without being offered:

- `webhooks` — signing in the Standard Webhooks format, which no delivery has yet.
- `functions` — custom C# script (csx) functions.

In the dashboard: call `get_capabilities` and quote it; `check_change` shows whether a change would be refused.
In this repo: `GET /management/projects/<project>/capabilities`, or `PUT /management/projects/<project>/descriptor?dryRun=true`.
