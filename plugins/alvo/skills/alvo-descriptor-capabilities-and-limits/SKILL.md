---
name: alvo-descriptor-capabilities-and-limits
description: Use when asked whether Alvo can do something, or when a change needs a block or an action this build may not run — what is honoured, what is only warned, and what is refused.
---

# What this build of Alvo honours

The capability report is the authority, not memory and not other frameworks: quote it. It lists every block and
slot that is not honoured, each with the consequence and the fix in the framework's own words.

These top-level blocks are honoured:

<!-- gen:honoured -->
`entities` `auth` `tenancy` `access` `formats`
<!-- /gen:honoured -->

These are declared by the schema and accepted, and are not honoured, or only in part. The apply warns about all but
`entity.realtime`, and a descriptor that sets them looks as if it works:

<!-- gen:warned -->
`dynamicEntities` `automation` `templates` `webhooks` `functions` `auth.providers` `entity.storage` `entity.realtime`
<!-- /gen:warned -->

**After-hook `webhook` and `email`, the one exception.** An after-hook `webhook` or `email` action runs, and may be
proposed, but only against an endpoint under `webhooks.endpoints` or a template under `templates` that the descriptor
already declares — never a new `webhooks` or `templates` block, which is a warned block. The delivery is
**unsigned**: no `secretRef` is read, no HMAC header is sent and the payload is not projected per endpoint, so the
receiver cannot verify the sender. Say so when you offer it. Referenced only from `automation`, they do nothing.

These are refused at apply, each with its reason in the capability report (`field.default` is a `$cel` default; a
literal is honoured):

<!-- gen:refused -->
`field.validation` `field.default` `entity.softDelete` `rollup.where` `trigger.event` `JSONata` `email.data` `bodyFile` `function` `http.call` `entity.update`
<!-- /gen:refused -->

The after-hook action types in this list are refused as actions, not as blocks. The skills of each area say what to use instead.

## How to answer

- Never propose an unhonoured block or a refused action, and never describe one as working.
- Offer the lowest honoured rung that does the job: a rule, a before-hook, a rollup, a computed field, or the
  after-hook exception above. Say what it does not do.
- Never promise a date or a release for what is missing.
- Rows are not the descriptor: the dashboard assistant never reads or writes rows and never applies a change.

What the missing parts are, so they can be explained without being offered:

- `automation` — event-condition-action rules run after the commit, from the outbox.
- `webhooks` — delivery from automation, and signing in the Standard Webhooks format, which no delivery has yet.
- `functions` — custom C# script (csx) functions.

In the dashboard: call `get_capabilities` and quote it; `check_change` shows whether a change would be refused.
In this repo: `GET /management/projects/<project>/capabilities`, or `PUT /management/projects/<project>/descriptor?dryRun=true`.
