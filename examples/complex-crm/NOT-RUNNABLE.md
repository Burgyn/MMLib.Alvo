# `complex-crm` is a format showcase, not a runnable backend

Applying this descriptor **fails**, on purpose. It exists to exercise the *shape* of every key
`schema/project.schema.json` declares — including the ones this build does not yet honour — so the schema
corpus has one fixture that covers the whole surface.

Today it declares three such features, each refused at apply by
`DescriptorToSchemaMapper`/`DescriptorValidator` with the consequence and the fix named:

| Feature | Where | Why it is refused |
|---|---|---|
| `rollup.where` | `companies.open_deals` | the *filter* is not evaluated, so the aggregate is maintained over **every** child instead of the declared subset — a stored number that looks like data. `rollup` itself is honoured now (#21); only its `where` is not |
| `default` | `companies.owner_id`, `deals.stage`, `deals.owner_id` | no column default is emitted and the value is dropped, so the field is simply null |
| `hooks/before*` | `contacts.beforeCreate`, `deals.beforeUpdate` | the hooks never run, so a write the author believes is vetted or patched is neither. The three `after*` points **are** honoured now, and this example declares none of them |

**`computed` and `rollup` are no longer on that list — #21 honours both.**
`invoice_items.line_total` is a stored generated column the database maintains and refuses every write to,
`invoices.net_total` is a rollup Alvo recomputes inside the child write's own transaction, and
`invoices.gross_total` is a computed column *over* that rollup. What is left of the pair is
`companies.open_deals`' `where`, above.

## It also declares five blocks that are *warned about*, not refused

The distinction is the rule, not a per-case judgement: **a feature is refused when ignoring it silently
produces wrong data, and warned about when its absence is observable.** An ignored `default` stores NULL where
a value was expected and nobody can see it from outside; a webhook that never fires is a webhook that never
fires. So these five apply cleanly and earn one warning at apply naming each of them
(`Descriptor.Internal.UnhonouredSubsystems`):

| Block | Where | What does not happen |
|---|---|---|
| `dynamicEntities` | root | no runtime entity can be created; every governance limit here bounds nothing (F7) |
| `automation` | root | no rule is evaluated, so no declared action runs — which looks like a condition that never matched |
| `templates` | root | an after-hook `email` action renders a template; one referenced only from an automation rule does not, and a `bodyFile` is read on neither path |
| `webhooks` | root | an endpoint an after-hook posts to is delivered to; one referenced only from an automation rule receives nothing. No delivery is signed — `secretRef` is unread, no Standard Webhooks HMAC header is sent (7.1) — nor projected per endpoint (#152) |
| `functions` | root | no function is invoked, on any trigger or schedule it declares |

`UnhonouredSubsystemsTests` uses this file's descriptor as its fixture and asserts that the warning names
exactly those five, so adding a sixth such block here fails a test rather than going unnoticed.

**`entity.realtime` is unhonoured too and is deliberately *not* in that warning.** The schema declares it per
entity with a default of `true`, so it is unhonoured for every entity of every descriptor — warning only on an
explicit `realtime: true` would stay silent for the entities equally affected, and warning on all of them
would fire on every descriptor ever applied. `docs/architecture/data-api.md` records it instead.

**Start from `simple-tasks/` or `vehicle-registry/` instead** — both apply as they stand.

## When this file goes away

Delete it as soon as applying `crm.alvo.json` succeeds. That is not a suggestion a reader has to remember:
`DescriptorToSchemaMapperTests.Every_example_marked_not_runnable_really_is_refused` asserts every marked
example *is* refused, so the day the last of the three features lands, that fact fails until this file is
removed. The marker cannot outlive its reason.
