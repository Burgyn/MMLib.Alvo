---
title: Dynamic entities
description: "Understand the planned embedded mode where your application's own users define new record types at runtime, stored in one shared store instead of a table per type."
sidebar:
  order: 5
---

:::caution[Not in this build]
Dynamic entities are **planned** for a later phase. The `dynamicEntities` block of the descriptor is parsed and accepted,
and nothing runs: no runtime entity can be created, and every limit the block declares bounds nothing. An entity
declared with `storage: dynamic` is not created either. [Capabilities in this build](/MMLib.Alvo/reference/capabilities/#declared-but-not-run-in-this-build)
says so in the framework's own words. This page describes the design the rest of Alvo is already built to accommodate.
:::

## The problem

Some applications have to let **their own users** define what they record. An ERP built on Alvo has a customer who
says "I need a register of company vehicles, with a plate number, a VIN and an owner", and another who needs a register
of service contracts. Nobody should run a schema migration for that, and the developer should not have to ship a
release.

The obvious answer, a database table per user-defined type, breaks down quickly. A thousand customers with a dozen
registers each is twelve thousand tables: the database's catalog bloats, its planner and maintenance slow down, and
every new register is live schema change, with its locks and its failure modes, triggered by an end user, repeatedly.

## One shared store, never a table per entity

The planned design is the one Salesforce, Airtable and Microsoft Dataverse use: a **metadata-driven store**. A fixed,
small set of tables holds everything, whatever users create:

- the **definitions** of the record types and of their fields: names, types, which fields are required, references;
- the **records** of every dynamic type of every tenant, in **one shared table** partitioned by tenant, each record's
  values stored as JSON.

Creating a record type is then inserting a row of metadata: no schema change, no lock on existing tables, and ten
thousand new types do not add a single database object.

The cost is honest too. Aggregations and joins over JSON values are slower than over real columns, so a type that grows
large or reporting-heavy needs its hot fields indexed, or eventually its own table. Because the database enforces no
types or references inside the JSON, the API layer has to validate every value against the definitions before it is
written. And a dynamic type has no compiled C# type: it is read and written as JSON.

## One model, two drivers

The part that is already built is the reason this can be added without rewriting Alvo. Everything above the data
layer, the generated API, the rule engine and events, works from one abstract model of entities and fields that a
**schema registry** supplies. Today it has one driver, which reads the physical tables Alvo created from the
descriptor. The plan adds a second driver that reads the metadata tables and produces the same model.

So a dynamic entity is meant to be indistinguishable from a physical one to everything above the store: the same
routes, the same [CEL rules](/MMLib.Alvo/concepts/cel/) compiled to SQL, the same tenancy, the same events. The seam is
already in place where it matters most: SQL for a rule is generated through a dialect interface that renders each field
reference, so a field stored as a JSON path can be rendered there without touching the rest of the rule engine. The
acceptance bar for the feature is that the same adversarial and policy test suite passes, identically, over physical and
dynamic entities.

## How it is governed

Dynamic entities are an embedded-mode feature, for a host whose end users create the types. The host decides the policy
over that whole class of entities in the descriptor's [`dynamicEntities`](/MMLib.Alvo/reference/descriptor/dynamic-entities/)
block: whether it is enabled, a reserved name prefix so a user's type can never collide with one the descriptor
declares, the default rules every new type starts with, the field types users may choose, and limits on fields, records
and types per tenant. Today that block is validated against the schema
and then does nothing.

## Put it to work

- [Entities and fields](/MMLib.Alvo/guides/entities-and-fields/): physical entities, which work today.
- [Multi-tenancy](/MMLib.Alvo/guides/multi-tenancy/): the tenant isolation dynamic entities will share.
- [Roadmap and status](/MMLib.Alvo/project/roadmap/): where dynamic entities sit in the plan.
- [`dynamicEntities` reference](/MMLib.Alvo/reference/descriptor/dynamic-entities/): the block's keys.
