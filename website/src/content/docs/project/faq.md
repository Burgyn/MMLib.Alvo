---
title: "FAQ"
description: "Find short answers to the questions people ask first about Alvo: its status, packages, authentication, databases, scope, CEL, errors and licence."
---

## Is Alvo production-ready?

Not yet: Alvo is pre-v0.1, nothing is released, and the descriptor format's unfinished parts and the Management API
may still change. What it runs is tested on every change, and [Running in production](/MMLib.Alvo/guides/production/)
shows how to configure it for real use; [Roadmap and status](/MMLib.Alvo/project/roadmap/) lists what may change and
the known issues.

## Is there a NuGet package or a Docker image?

The image, yes; the packages, not yet. The standalone image is published as `ghcr.io/burgyn/alvo`, for `linux/amd64`
and `linux/arm64`, with an `edge` tag built from `main`; [Quick start](/MMLib.Alvo/start-here/quick-start/) runs it
with one downloaded compose file. Publishing the `MMLib.Alvo.*` packages, and the first versioned image tag, is the
v0.1 release; until then an embedded host references the projects or a local package feed
([Embed in ASP.NET Core](/MMLib.Alvo/start-here/embed/)).

## Can I use my own authentication?

In an embedded host, yes: your users sign in to your app, and your own endpoints pass Alvo their id, roles and tenant,
so the descriptor's rules decide what they may do ([Use your own authentication](/MMLib.Alvo/guides/own-authentication/)).
The generated API itself accepts only Alvo's API keys today, and publishing your signed-in user to it is planned
([#210](https://github.com/Burgyn/MMLib.Alvo/issues/210)).

## Which databases does it support?

PostgreSQL and SQLite, with the same rules, events and tenancy on both; PostgreSQL is the one the published performance
numbers were measured on. The SQL a database spells differently sits behind one dialect interface, so another engine can
be added without touching the rule engine, but no other engine is in this build
([Architecture](/MMLib.Alvo/concepts/architecture/#ports-and-the-provider-model)).

## Does it do realtime or file storage?

No. Both are planned components; an entity's `realtime` setting is accepted and sends nothing, as
[Capabilities in this build](/MMLib.Alvo/reference/capabilities/) states. [What works today](/MMLib.Alvo/start-here/what-works-today/)
lists what does run.

## Why CEL, and not C# lambdas?

A descriptor is data: a lambda cannot live in a JSON file, be checked when the descriptor is applied, be read by an
agent, or be rendered into the SQL that filters rows, and CEL can do all four while being unable to loop or reach the
network ([CEL in Alvo](/MMLib.Alvo/concepts/cel/)). When you do need C#, an embedded host registers it as a function the
descriptor's hooks can call ([Custom CEL functions](/MMLib.Alvo/guides/custom-cel-functions/)).

## Where are the errors documented?

Every refusal is an RFC 9457 problem document, and each `type` slug has its own section, with causes and fixes, in
[Problem types](/MMLib.Alvo/reference/problem-types/). [Handle errors](/MMLib.Alvo/guides/handle-errors/) shows how a
client should branch on them, including why an excluded row is a `404` and an excluded page is empty, not a `403`.

## Can an AI agent build and change my backend?

That is what Alvo is designed for: one schema-described JSON file, refusals that say how to fix them, and a Management
API with dry runs, revision checks and idempotent applies ([For coding agents](/MMLib.Alvo/start-here/coding-agents/)).
The dashboard's [schema assistant](/MMLib.Alvo/guides/schema-assistant/) proposes checked changes, and only a person
applies them.

## What does it cost?

Nothing: Alvo is open source under the Apache License 2.0, and the core stays under it. Only enterprise add-ons and
optional hosting may ever be commercial ([License](/MMLib.Alvo/project/license/)).
