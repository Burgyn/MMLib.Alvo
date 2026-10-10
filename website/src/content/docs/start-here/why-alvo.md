---
title: Why Alvo
description: Decide whether Alvo fits your project, what makes it different, when not to use it, and how it compares with Supabase, PocketBase and hand-written ASP.NET Core.
sidebar:
  order: 1
---

## What it is

*Describe your backend in one JSON file. Get a secure, production-shaped API — standalone in Docker or embedded in your
ASP.NET Core app.*

That file, the project descriptor, declares your entities, their fields, who may read and write which rows, what
happens on every write, and who may change the project itself. Alvo turns it into database tables, a REST API with an
OpenAPI document, rules enforced in the database, and an admin dashboard. It is written in .NET, and the same engine
runs as a Docker container or as a library inside your own app.

## Why it is different

- **One descriptor, the whole backend.** Entities, rules, hooks, computed fields, rollups, indexes, audit and webhooks
  live in one schema-validated JSON file, whether it sits in your repository or is edited in the dashboard.
  [The project descriptor](/MMLib.Alvo/concepts/descriptor/)
- **Security in the data layer.** Rules are CEL expressions compiled to parameterized SQL predicates, everything is
  denied until a rule allows it, and hooks fail closed. [Security model](/MMLib.Alvo/concepts/security-model/)
- **Agent-first.** A JSON Schema with a description on every key, structured errors with fix suggestions, idempotent
  operations, a Management API and `llms.txt`, so a coding agent can do the work. [For coding agents](/MMLib.Alvo/start-here/coding-agents/)
- **.NET-native, two modes.** The Docker image and the embedded library are one codebase; embedded, you extend it in
  C# with your own functions and endpoints. [Standalone and embedded](/MMLib.Alvo/concepts/modes/)
- **An admin dashboard** with a schema editor, rule and hook editors, a data browser, history with rollback, and an AI
  assistant that uses the same skills your agents can. [The admin dashboard](/MMLib.Alvo/guides/admin-dashboard/)
- **Dynamic entities (planned).** Your end users define their own record types at runtime, in one shared store, in an
  embedded host. Not in this build. [Dynamic entities (planned)](/MMLib.Alvo/concepts/dynamic-entities/)

## When to use it

- You want a real backend (data, access rules, validation, audit) without writing the CRUD, the migrations and the
  authorization checks yourself.
- You work with a coding agent and want it to change the backend through a declarative file and an API that says what
  is wrong, rather than through generated controller code.
- Your team is on .NET, and you want to grow from a container into your own ASP.NET Core host without rewriting the
  backend: the descriptor moves with you.
- You need rules that hold for every request, enforced where the data is read, not in each endpoint.

## When not to use it

- **You need it in production today.** Alvo is pre-v0.1: no NuGet package or container image is published, and the
  format and APIs may still change. See [What works today](/MMLib.Alvo/start-here/what-works-today/).
- **You need realtime subscriptions, file storage or automation rules (the `automation` block).** None of them runs in
  this build; after-hooks that send e-mail and webhooks on a write do.
- **You need sign-in through Google, Microsoft or another identity provider.** Only local accounts and API keys exist
  in this build; embedded, your app can bring its own authentication.
- **Your logic does not fit declarative rules and hooks.** If most of your backend is custom code, the escape hatch is
  the embedded mode with your own endpoints, but then weigh how much Alvo still does for you.

## Compared with

How Alvo answers the same questions as the platforms closest to it:

| | Runtime | Where rules run | Backend defined as | Extending in .NET | Embedding in your app |
|---|---|---|---|---|---|
| **Alvo** | .NET: a Docker image, or a library in your host | CEL compiled to SQL predicates, inside the database query | one JSON descriptor | C# functions, endpoints and providers in the embedded mode | yes, in an ASP.NET Core app |
| **Supabase** | about seven services around PostgreSQL (gateway, auth, PostgREST, realtime, storage, functions, studio) | PostgreSQL row-level security policies, in the database | SQL: schema, policies and migrations | a community C# client; server-side logic in Deno edge functions | no |
| **PocketBase** | a single Go binary over SQLite | API rules per collection and operation, in PocketBase's filter syntax, applied as a filter on the record query (superusers bypass them) | collections with their API rules | no; extended in Go or with JavaScript hooks | yes, in a Go app |
| **Hand-written ASP.NET Core** | your own .NET app | a check in each endpoint, written by you | C# code | everything is .NET | it is your app |

## Status

Alvo is being built in the open, phase by phase. [Roadmap and status](/MMLib.Alvo/project/roadmap/) shows where it
is, what v0.1 brings, and what may change before then.
