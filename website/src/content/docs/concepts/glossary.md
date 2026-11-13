---
title: "Glossary"
description: "Look up the terms Alvo's docs use, from access level and apply to working copy and write hook, each with a link to where it is taught."
---

The words these docs use with a precise meaning, in alphabetical order. Each links to the page that teaches it.

<dl class="alvo-glossary">

<dt id="access-level">Access level</dt>
<dd>

One of three levels on the Management API and the dashboard: `viewer` reads, `developer` also applies and rolls back,
`admin` also changes the `access` block, the people and the AI connection. The descriptor's `access` block maps roles to
levels, and the highest match wins. [For coding agents](/start-here/coding-agents/#access-levels)

</dd>

<dt id="profile-access">Access profile</dt>
<dd>

The CEL profile of the `access` block's three levels: a boolean over `@user` alone, with no row and no tenant.
[CEL in Alvo](/concepts/cel/#the-five-profiles)

</dd>

<dt id="after-hook">After-hook</dt>
<dd>

An action that runs after a write has committed, delivered from the outbox: a webhook, an e-mail, a host
[function](#function) or an `entity.update`. Each action is its own [delivery](#delivery). It may reach the network and
is retried, so its receiver must tolerate a repeat.
[After-hooks, events and webhooks](/guides/after-hooks-and-webhooks/)

</dd>

<dt id="apply">Apply</dt>
<dd>

Making a descriptor the running one: validate it, plan the migration from the schema applied last, run it, and record a
new revision. It happens on boot, through the Management API, or from the dashboard; a dry run does everything but the
last two steps. [Apply and evolve your descriptor](/guides/apply-and-evolve/)

</dd>

<dt id="before-hook">Before-hook</dt>
<dd>

A `reject` or `mutate` action that runs inside a write's transaction, before the row is stored. Nothing a descriptor
can express in it reaches the network (a function a host registers in C# is host code and the exception), and if it
refuses or fails, nothing is written.
[Validate and transform writes (before-hooks)](/guides/before-hooks/)

</dd>

<dt id="bootstrap-administrator">Bootstrap administrator</dt>
<dd>

The first dashboard account, created once from configuration rather than from the descriptor. It always holds the
`admin` level, so a project can never lock everyone out.
[Authentication and API keys](/guides/authentication/#people-sign-in-to-the-dashboard)

</dd>

<dt id="causation-depth">Causation depth</dt>
<dd>

How many hops a write or an event sits from the request that started it: a write a hook or a handler makes is one
hop deeper than what ran it, and the envelope carries it as `chaindepth`. Past `Alvo:Extensions:MaxDepth` (8) it is
refused with `508 chain-depth-exceeded`, so a loop stops. [Extending in code](/extending/#the-chain-depth-limit)

</dd>

<dt id="cel-profile">CEL profile</dt>
<dd>

The set of CEL constructs an expression may use, decided by where it stands in the descriptor. There are five: Rule,
Computed, Condition, Mutate and Access. [CEL in Alvo](/concepts/cel/#the-five-profiles)

</dd>

<dt id="command">Command</dt>
<dd>

A named action of your host's — `confirm-stay` rather than a raw `PATCH` — with an access rule, an idempotency key and
a completion event, mapped over HTTP with `MapAlvoCommand` or invoked in process.
[Commands and inbound webhooks](/extending/commands/)

</dd>

<dt id="computed-field">Computed field</dt>
<dd>

A field whose value the database derives from the same row, written in the **Computed** profile; callers cannot write
it. [Computed fields and rollups](/guides/computed-and-rollups/)

</dd>

<dt id="profile-computed">Computed profile</dt>
<dd>

The CEL profile of a `computed` field: a value the database computes from the same row, with no caller context, and the
only profile with the `? :` conditional. [CEL in Alvo](/concepts/cel/#the-five-profiles)

</dd>

<dt id="profile-condition">Condition profile</dt>
<dd>

The CEL profile of a hook's `condition`: a boolean that sees the row before and after the write (`old.`, `new.`) and
may call `changed(field)`. [CEL in Alvo](/concepts/cel/#the-five-profiles)

</dd>

<dt id="custom-cel-function">Custom CEL function</dt>
<dd>

A C# function an embedded host adds to CEL with `AddCelFunction`, such as `normalizeVin(new.vin)`: pure, synchronous,
inside the write's transaction, no network. Not a [host function](#host-function).
[Custom CEL functions](/guides/custom-cel-functions/)

</dd>

<dt id="delivery">Delivery, dead delivery</dt>
<dd>

One run of one [subscriber](#subscriber) for one event — an after-hook action, an automation action or a host
[event handler](#event-handler) — recorded with its attempts, retried on its own and kept in order per record. After its
last attempt it is **dead**: it stays listed, and holds that record's later deliveries for the same subscriber back,
until an operator redelivers, replays or cancels it. A schedule's run and a data migration are deliveries too.
[Operate deliveries](/extending/operate-deliveries/)

</dd>

<dt id="descriptor">Descriptor</dt>
<dd>

The one JSON document that defines a backend: entities, fields, rules, hooks, derived values and management access,
never infrastructure or credentials. [The project descriptor](/concepts/descriptor/)

</dd>

<dt id="dry-run">Dry run</dt>
<dd>

An apply that stops before changing anything (`?dryRun=true` on the Management API): it answers with the plan, or with
the refusal a real apply would give. [Apply and evolve your descriptor](/guides/apply-and-evolve/)

</dd>

<dt id="embedded">Embedded</dt>
<dd>

Running Alvo as NuGet packages inside your own ASP.NET Core app, beside your own endpoints and users. It is the mode
in which your own C# joins Alvo's pipeline: [Extending in code](/extending/).
[Standalone and embedded](/concepts/modes/)

</dd>

<dt id="entity">Entity</dt>
<dd>

One kind of record the descriptor declares, such as `tickets`: a table in the database and its own routes in the Data
API. [Entities and fields](/guides/entities-and-fields/)

</dd>

<dt id="event-envelope">Event envelope</dt>
<dd>

What every event carries, as a CloudEvents 1.0 document: its type, the row before and after, who acted, in which tenant
and flow, and its [causation depth](#causation-depth). After-hooks, automation and event handlers read the same one.
[The event envelope](/extending/event-envelope/)

</dd>

<dt id="event-handler">Event handler</dt>
<dd>

Host C# that runs after a write commits, once per matching event: at least once, retried, in order per record, each
run its own [delivery](#delivery). It can never undo the write. [React to changes](/extending/event-handlers/)

</dd>

<dt id="function">Function</dt>
<dd>

Three different things in these docs. A [custom CEL function](#custom-cel-function) (`AddCelFunction`) is called from
an expression, inside the transaction. A [host function](#host-function) (`AddFunction`) is called by a descriptor's
`function` action, after the commit. The descriptor's `functions` block names csx scripts, which this build does not
run, so the dashboard's *Functions* screen is marked *Not yet*.

</dd>

<dt id="honoured">Honoured</dt>
<dd>

A descriptor block this build runs as documented. The others are warned or refused.
[Capabilities in this build](/reference/capabilities/)

</dd>

<dt id="host-function">Host function</dt>
<dd>

C# an embedded host registers with `AddFunction<T>` and a descriptor calls by name with a `{"type": "function"}`
after-hook or automation action. It runs after the commit, as a [delivery](#delivery), with its input checked at apply.
Not a [custom CEL function](#custom-cel-function). [Functions in the descriptor](/extending/functions/)

</dd>

<dt id="lifecycle-tab">Lifecycle tab</dt>
<dd>

The tab of an entity on the dashboard's *Schema* screen that lists, read-only, the code around its writes and reads:
the descriptor's after-hooks and, in an embedded host, the write hooks, event handlers, commands and read extensions
registered in C#. The descriptor's before-hooks stay on the *On write* tab.
[See your extensions in the dashboard](/extending/extension-catalog/#in-the-dashboard)

</dd>

<dt id="profile-mutate">Mutate profile</dt>
<dd>

The CEL profile of a before-hook's `mutate` values: a value a field can hold, computed from the row, functions and
arithmetic. [CEL in Alvo](/concepts/cel/#the-five-profiles)

</dd>

<dt id="new-old">new, old</dt>
<dd>

In a hook, the row as it will be stored (`new.status`) and as it was before the write (`old.status`). Only the Condition
and Mutate profiles can read them. [Validate and transform writes (before-hooks)](/guides/before-hooks/)

</dd>

<dt id="outbox">Outbox</dt>
<dd>

The table every write appends its event to, in the same transaction as the row, so there is no change without an event
and no event without a change. Every [delivery](#delivery) — an after-hook action, an automation action, a host
[event handler](#event-handler) — is fanned out from it. [Architecture](/concepts/architecture/#events-and-the-outbox)

</dd>

<dt id="problem-type">Problem type</dt>
<dd>

The slug at the end of a refusal's `type` (`https://alvo.dev/errors/forbidden`) that names its kind. Clients branch on
it, never on the prose in `detail`. [Problem types](/reference/problem-types/)

</dd>

<dt id="refused">Refused</dt>
<dd>

A descriptor feature the schema allows but this build rejects at apply, with a reason and a fix, because accepting it
would silently do something other than what it says. [Capabilities in this build](/reference/capabilities/#refused-at-apply)

</dd>

<dt id="revision">Revision</dt>
<dd>

One entry in a project's append-only descriptor history: the descriptor, its number, who applied it, when and why.
Every apply adds one, a rollback included, and none is rewritten. Do not confuse it with `apiVersion`, the format's
version. [Apply and evolve your descriptor](/guides/apply-and-evolve/)

</dd>

<dt id="role">Role</dt>
<dd>

A name a caller holds, such as `agent`, that rules test with `'agent' in @user.roles`. The descriptor declares its roles
in `auth.roles`; `anon`, `authenticated` and `admin` are built in. A key carrying a role the descriptor does not declare
authenticates nothing. [Authentication and API keys](/guides/authentication/)

</dd>

<dt id="rollup">Rollup</dt>
<dd>

A field that aggregates related rows, such as the sum of an order's lines, kept up to date inside the same transaction
as the change. [Computed fields and rollups](/guides/computed-and-rollups/)

</dd>

<dt id="rule">Rule</dt>
<dd>

A CEL expression per entity operation (`list`, `get`, `create`, `update`, `delete`) that decides which rows a caller may
reach: rendered into the SQL that reads rows, and checked against the row a write would store. An operation
without one is refused.
[Access rules](/guides/access-rules/)

</dd>

<dt id="profile-rule">Rule profile</dt>
<dd>

The CEL profile of rules and of `hidden` and `readOnly` flags: a boolean over the current row, `@user`, `@tenant` and
`now()`, with no arithmetic but timestamp and duration arithmetic. [CEL in Alvo](/concepts/cel/#the-five-profiles)

</dd>

<dt id="savepoint">Savepoint</dt>
<dd>

A point inside a transaction that a failed statement can roll back to without ending the transaction. Each call in a
[transaction unit](#transaction-unit) runs under its own, so one refused write undoes that call alone and the unit
goes on. [Transactions in your own code](/extending/transactions/#a-refused-write-does-not-end-the-unit)

</dd>

<dt id="scope">Scope</dt>
<dd>

A limit written on an API key, `<entity|*>:<read|write>`, that narrows what the key reaches on the Data API whatever the
rules allow. Scopes do not limit the Management API, where a key reaches what its roles reach.
[Authentication and API keys](/guides/authentication/#3-narrow-a-key-with-scopes)

</dd>

<dt id="standalone">Standalone</dt>
<dd>

Running Alvo as a container image driven by a mounted descriptor, with the dashboard and an API browser included.
[Standalone and embedded](/concepts/modes/)

</dd>

<dt id="subscriber">Subscriber</dt>
<dd>

Whatever receives an event's [deliveries](#delivery): a host event handler, named by the id it was registered under,
or a descriptor after-hook or automation action, named by its JSON pointer and a hash, such as
`/entities/tickets/hooks/afterCreate#1f0c9a2e`. Pausing, filtering and replaying go by it.
[Operate deliveries](/extending/operate-deliveries/)

</dd>

<dt id="tenant">Tenant, @tenant</dt>
<dd>

The customer or organisation a caller acts for, at most one per caller. On a tenant-scoped entity every row belongs to
one tenant, and `@tenant.id` is the caller's tenant in a rule. [Multi-tenancy](/guides/multi-tenancy/)

</dd>

<dt id="transaction-unit">Transaction unit</dt>
<dd>

Several writes, batches and events your host code makes as one database transaction, opened with
`IAlvoTransactions.TransactAsync` (or a command's `ctx.TransactAsync`): returning commits, throwing rolls back. Every
call runs under the caller's rules, and under its own [savepoint](#savepoint).
[Transactions in your own code](/extending/transactions/)

</dd>

<dt id="user">@user</dt>
<dd>

The caller in an expression: `@user.id` and `@user.roles`, and nothing else. An expression that reads `@user.id` refuses
a caller with no identity. [Access rules](/guides/access-rules/)

</dd>

<dt id="warned">Warned</dt>
<dd>

A descriptor block this build parses and accepts but does not run. The apply succeeds, and the dashboard and the
capabilities answer say what does not happen. [Capabilities in this build](/reference/capabilities/#declared-but-not-run-in-this-build)

</dd>

<dt id="working-copy">Working copy</dt>
<dd>

The dashboard's one unapplied draft of the descriptor, which every screen and the schema assistant edit. Nothing changes
until you preview and apply it. [The admin dashboard](/guides/admin-dashboard/#3-change-the-schema)

</dd>

<dt id="write-hook">Write hook</dt>
<dd>

Host C# inside a write. A **validate** hook runs before the transaction, may call the network, and can only refuse; a
**before-write** or **after-write** hook (and the delete pair) runs inside the transaction and can refuse, set fields
or write other rows. Contrast the descriptor's [before-hook](#before-hook).
[Validate and guard writes](/extending/write-hooks/)

</dd>

</dl>
