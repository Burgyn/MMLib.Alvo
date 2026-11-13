---
name: alvo-descriptor-capabilities-and-limits
description: Use when asked whether Alvo can do something, or when a change acts after a write or on a schedule — after-hooks, automation rules, actions; what is honoured, warned or refused.
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
`trigger.event` is refused only as a wildcard (`entity.*.created`); an exact `entity.<entity>.<operation>` is honoured.

## After-hooks and automation

An after-hook is an entry of an entity's `afterCreate`, `afterUpdate` or `afterDelete` list: an optional `condition`
and one `action` (`schema/project.schema.json#/$defs/afterHookList`). An `automation` rule is project-level, keyed by
its name: a `trigger`, an optional `condition` and its `actions` (`schema/project.schema.json#/$defs/automationRule`;
each action is `schema/project.schema.json#/$defs/action`).

- A trigger is `{"event": "entity.<entity>.<created|updated|deleted>"}`, or `{"schedule": "0 2 * * *"}`: a five-field
  cron in UTC, run once per tenant when the project has tenancy (the host then needs an `IAlvoTenantCatalog`). A cron
  rule has no row: it takes no `condition`, and it cannot pick rows to change — a bulk change over rows is host code.
- A `condition` reads `new.` on create and update, `old.` on update and delete, like a before-hook's.
- `entity.update` writes `payload` into `entity` (each value a literal or `{"$cel": "…"}`; a string with `{{…}}` is
  refused): to the record `recordId` names (a UUID, or one `{{…}}` placeholder over the row), or to a new one without it.
- Editing a rule cancels the old rule's unfinished deliveries. A `timers` event is never a trigger: host code handles it.

Adding a rule depends on what exists: when `automation` exists, `add` at `/automation/<rule>`; `add` at `/automation`
with the whole map only when it does not, because an `add` there replaces every rule it holds.

<!-- example: rental-returned-webhook -->
**Tell the rental desk when a rental is returned** — `bike-workshop` has no `automation` yet.

```json
{"tool": "propose_change", "baseRevision": 1, "summary": "Posts each returned rental to the rental desk.",
 "operations": [{"op": "add", "path": "/automation",
                 "value": {"rental-returned": {"trigger": {"event": "entity.rentals.updated"},
                           "condition": "new.status == 'returned' && old.status != 'returned'",
                           "actions": [{"type": "webhook", "endpoint": "rental-desk"}]}}}]}
```

```json
{"valid": true, "changedPaths": ["/automation"]}
```

## How to answer

- Never propose an unhonoured block or a refused action, and never describe one as working.
- Offer the lowest honoured rung that does the job: a rule, a before-hook, a rollup, a computed field, or a
  declarative action above. Say what it does not do.
- Never promise a date or a release for what is missing.
- Rows are not the descriptor: the dashboard assistant never reads or writes rows and never applies a change.
- Beyond the descriptor is host C# (an event handler, a command, a schedule, a write hook), only when no construct
  fits. The host's developer writes it, with the `alvo-host-extensions` skill; never propose code.

What the missing parts are, so they can be explained without being offered:

- `webhooks` — signing in the Standard Webhooks format, which no delivery has yet.
- `functions` — custom C# script (csx) functions.

In the dashboard: call `get_capabilities` and quote it; `check_change` shows whether a change would be refused.
In this repo: `GET /management/projects/<project>/capabilities`, or `PUT /management/projects/<project>/descriptor?dryRun=true`.
