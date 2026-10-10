---
title: Roadmap and status
description: "See where Alvo is before v0.1, what may still change before the release and what you can rely on, what v0.1 means, and what is planned after it."
---

Alvo is **pre-v0.1**. Everything this site documents runs today, from a clone of the repository, and is tested on
every change. Nothing is published yet: no NuGet package and no container image. This page says what that means for
you. The live plan is [`docs/PLAN.md`](https://github.com/Burgyn/MMLib.Alvo/blob/main/docs/PLAN.md) on GitHub; where the
two disagree, it wins.

## Where we are

The plan moves in phases, each a GitHub milestone, one at a time:

| Phase | What it delivered or delivers | Status |
|---|---|---|
| Skeleton | something to build on | done |
| Quality before code | every test and review gate, set up on empty projects | done |
| Schema foundation | the descriptor's JSON Schema, and one entity model with room for a second, dynamic driver | done |
| Vertical slice | descriptor to tables to a generated REST API, with validation | done |
| Demo from the start | runnable examples and the end-to-end suites that drive them | done |
| Admin mode | the dashboard, the rule and hook editors, the schema assistant | **in progress** |
| v0.1 | the documentation you are reading, the logo, and the release | next |
| Further components | the rest of the backend-as-a-service surface, by value, including dynamic entities | planned |

The release phase also carries the debt on what already ships: known defects, gaps between engines, and the health of
the test gates. A release does not go out with a known hole.

## What may change before v0.1

- **The parts of the descriptor format that are warned or refused.** A block this build parses and does not run, such
  as `automation`, `functions` or `dynamicEntities`, will start doing what it declares when its feature lands; a refused
  key stops being refused. The list is [Capabilities in this build](/MMLib.Alvo/reference/capabilities/). Today's
  honoured behaviour is not what this item is about.
- **The Management API's routes and bodies.** It has no OpenAPI document of its own yet; its reference here is generated
  from the running route table. Expect it to be described, and possibly reshaped, before it is frozen.
- **The base URI of the problem types.** A refusal's `type` is `https://alvo.dev/errors/<slug>`, and that address does
  not resolve today. The base may move; the slug after it does not
  ([Problem types](/MMLib.Alvo/reference/problem-types/) explains the mapping).
- **The schema's URL.** A descriptor's `$schema` and the schema's own `$id`, `https://alvo.dev/schema/v1/project.json`,
  do not resolve either. This site serves a copy at `https://burgyn.github.io/MMLib.Alvo/schema/v1/project.json`; the
  canonical address may still change.
- **The environment-variable names** of the container, which become a breaking change once an image is published
  ([#233](https://github.com/Burgyn/MMLib.Alvo/issues/233)).

## What is stable

- **The descriptor format version, `apiVersion: alvo.dev/v1`.** The schema's own rule: within v1 the format only grows;
  a breaking change becomes `alvo.dev/v2`.
- **The problem-type slugs**, the fifteen in [Problem types](/MMLib.Alvo/reference/problem-types/). They are the
  contract a client branches on, and `detail` is not.

The Data API's URL grammar follows PostgREST on purpose, and its known deviations are documented
([Data API conventions](/MMLib.Alvo/data-api/conventions/)), but no source declares it frozen before v0.1.

## What v0.1 means

v0.1 is the first release: publishing the `MMLib.Alvo.*` packages on NuGet and the standalone container image, with
this documentation. Until then, [Quick start](/MMLib.Alvo/start-here/quick-start/) runs the image from the clone and
[Embed in ASP.NET Core](/MMLib.Alvo/start-here/embed/) references the projects or a local package feed. Install commands
for the published packages appear only in tabs marked *available from v0.1*.

## Known issues

Open defects the documentation pages point to. A fix is in progress for #353.

| Issue | What happens |
|---|---|
| [#103](https://github.com/Burgyn/MMLib.Alvo/issues/103) | An entity added through the Management API or the dashboard gets no Data API route until the host restarts. |
| [#340](https://github.com/Burgyn/MMLib.Alvo/issues/340) | The standalone host exits with code 139 on an invalid descriptor, instead of the clean refusal and exit code 78. |
| [#343](https://github.com/Burgyn/MMLib.Alvo/issues/343) | A dry run of a destructive change is refused like a real apply. |
| [#344](https://github.com/Burgyn/MMLib.Alvo/issues/344) | Small inconsistencies: an unverified `author` on Management API revisions, different statuses for writes to derived fields. |
| [#345](https://github.com/Burgyn/MMLib.Alvo/issues/345) | A before-hook may `mutate` a rollup or computed field, which the apply should refuse. |
| [#350](https://github.com/Burgyn/MMLib.Alvo/issues/350) | Filter negation is spelled `not.field=op.value`, not PostgREST's `field=not.op.value`. |
| [#351](https://github.com/Burgyn/MMLib.Alvo/issues/351) | A write to a child row recomputes the parent's rollups without advancing the parent's `ETag`, so an `If-Match` or `If-None-Match` taken before it still matches. |
| [#353](https://github.com/Burgyn/MMLib.Alvo/issues/353) | A field added through the Management API or the dashboard is refused as `unknown-field` until the host restarts. |
| [#354](https://github.com/Burgyn/MMLib.Alvo/issues/354) | A request that races a runtime apply can be judged by the old policy and read with the new schema, so a field the apply just made `hidden` can be returned once. |
| [#355](https://github.com/Burgyn/MMLib.Alvo/issues/355) | A write that reaches a database `NOT NULL` constraint the API did not check first answers 500 instead of a structured refusal. |

## What comes after

After v0.1, components are added one at a time, ordered by value, each with its contract tests first. The plan names
these candidates; none has a date:

- **Automation**: rules that react to events, with signed webhook deliveries and redelivery.
- **Custom functions**: scripts and a runtime to execute them, beyond the C# functions an embedded host registers today.
- **Full authentication**: sign-in providers beyond local passwords, and issuing and revoking API keys
  ([#36](https://github.com/Burgyn/MMLib.Alvo/issues/36)).
- **Teams and permissions** on top of roles, and richer caller context in rules.
- **Realtime** change notifications and **file storage**.
- **[Dynamic entities](/MMLib.Alvo/concepts/dynamic-entities/)**: record types your own users define at runtime, in one
  shared store.
- **An audit log of data changes**, beside today's history of configuration changes.

Not every candidate becomes its own package: the core stays one package unless a feature brings a heavy dependency, is a
real swap point, or ships differently.
