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
exactly those five. **What that does and does not catch, precisely:** it filters this file's root keys
*through the table* before comparing, so adding a block here that is **also** added to
`UnhonouredSubsystems.All` fails the test until the expected set is updated — while a block added here and
left out of the table is invisible to it. That second state is where `branding` sits, and
`Branding_is_not_on_the_table_because_its_absence_is_merely_visible` is what pins the one case most
likely to be "tidied in" later.

**Neither `access` nor `branding` is on that list any more, for two different reasons.** `access`
left it when #146 landed: the three levels are compiled at apply, their role literals are
validated against `auth.roles` exactly as a rule's are, and a caller who matches no level is
refused — so a warning saying the block does nothing would now be false. `branding` was never on
it: an author who writes it and sees no logo has looked and found out, which earns an entry under
neither limb of the rule. An entry is earned two ways: limb one is "the absence is observable but easy
to misattribute" (a webhook that never fires looks like an endpoint that is down), and limb two is "the
block's *name* promises something it does not do, so the author holds a **false belief**" — which is the
limb the `webhooks` entry rests on, an unsigned delivery an author believes is signed.

### `access` is **role-based only**, and what this file lost to that is `developer`

Settled on #146 (option A). An `access` level is a CEL predicate over the closed context `@user` exposes —
`@user.id` and `@user.roles`, nothing else (`docs/architecture/cel.md`, deviation 1) — so in practice a
level tests role membership: `@user.id` is admitted by the profile, but Alvo's grammar has no `uuid`
literal and a level sees no row, so there is no `Uuid`-typed operand to compare it against.

The block this file was born with (commit `c84e1ab`, before `@user.role` was retired for `@user.roles`) was:

```json
"access": {
  "admin":     "@user.role == 'manager' && @user.email.endsWith('@firma.sk')",
  "developer": "@user.role == 'manager'",
  "viewer":    "@user.role in ['sales', 'manager', 'finance']"
}
```

**Only `admin` carried the domain gate, so only `admin` lost anything *to the narrowing*** — `developer`
left later, and for a different reason (enforcement, below). `developer` and `viewer` were
already rendered into the membership idiom the compiler mandates (`@user.role` is not a member `@user` has;
`CelParser` answers it with *"a caller holds a set of roles — test membership instead"*), and this change
leaves both untouched. That rendering is not quite an identity — membership *widens* the original equality
for a caller who holds several roles — but it is the only form that compiles, and it is applied repo-wide.

`admin` is the one that cannot be rendered at all: `@user.email` is not a member `@user` has, in this block
or any other, so the half that distinguished an admin from a developer — *"a manager, and one of ours"* — is
inexpressible. What is left of it is `'manager' in @user.roles`, which is exactly `developer`.

Rendered role-only, `admin` is exactly `'manager' in @user.roles` — which is exactly what `developer` was.
It is the precise cost of option A, visible in the one file whose job is to show the schema's whole
surface: without attribute claims, a two-level distinction that rested on an attribute collapses to one. The
alternative — inventing a role for `developer` so the two differ again — would assert an org shape this CRM
never had, which is the same error as the rewrite that put
`'manager' in @user.roles && 'finance' in @user.roles` on `admin` and **invented a requirement** the original
never carried (#146 point 3). That conjunction is gone; nothing replaces it.

**`developer` is now gone from this file, and that is the enforcement landing rather than the
finding changing.** While the block was inert, two identical predicates were an honest record of
what the narrowing cost. Once the levels are enforced, they resolve highest-match-wins — so a
`developer` predicate identical to `admin` is a level no caller can ever hold, and a showcase
whose job is the schema's shape would be teaching a shape that cannot work. What is left is the
two levels this CRM really has: managers administer it, and everyone in sales, management or
finance can look. The third key stays covered by the schema suite's own sample
(`MMLib.Alvo.Schema.Tests.AccessLevelsTests`), which is where a key's coverage belongs.

Attribute-based rules — typed claims, `@user.teams` — are #37's scope, and widening `@user` is additive, so
these expressions keep compiling on the day they land. On that day `admin` can be given back the
distinguishing half it originally had — the domain gate — and `developer` can return as the level it
always was, distinct from `admin` again because `admin` is narrower than it rather than equal to it.

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
