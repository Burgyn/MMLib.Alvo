---
name: alvo-descriptor-project-access
description: Use when an Alvo descriptor change decides who may manage the project itself — the admin, developer and viewer levels of the access block, and who is allowed to change them.
---

# Project access in an Alvo descriptor

The top-level `access` block says who may manage this project — its descriptor, keys and dashboard — at three
levels: `admin`, `developer` and `viewer`. It is not about data rows: an entity's `rules` decide those
(`alvo-descriptor-rules-and-cel`).

The shape: `schema/project.schema.json#/properties/access`.

Each level is a CEL condition over the caller alone, `@user.roles` and `@user.id`, and in practice it tests role
membership. It sees no row and no tenant, so a field name or `@tenant` is refused:

<!-- gen:cel-access -->
- allowed: `'admin' in @user.roles` `'manager' in @user.roles || 'reception' in @user.roles` `!('reception' in @user.roles)`
- refused: `user_id == @user.id` `now()` `@tenant.id == @user.id`
<!-- /gen:cel-access -->

A role literal must be a built-in role or one declared in `auth.roles`; a typo is refused at apply, as in a rule.

**Only an administrator may change `access`.** For anyone else the change is refused with an `access` violation:
say that an administrator has to make it, and do not look for a way around it.

In the dashboard: read with `get_descriptor`, then `check_change` or `propose_change` the operations.
In this repo: edit the descriptor file and run the validator (`dotnet test`), or the Management API.
