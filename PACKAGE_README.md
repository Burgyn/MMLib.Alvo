# Alvo

**Describe your backend in one JSON file. Get a secure, production-shaped API — standalone in Docker or embedded in your ASP.NET Core app.**

Alvo is a .NET-native backend-as-a-service: one validated JSON descriptor — entities, access rules, hooks, computed
fields and rollups — becomes a REST API with its own OpenAPI document. It is built for developers who work with coding
agents, and for .NET teams that want a configurable backend inside their own app. Access rules are compiled into the
SQL that reads the rows, the whole backend is one file an agent can validate and dry-run, and every refusal is a
problem document, with a pointer and a fix suggestion wherever the request can be repaired.

![The admin dashboard's rule editor flagging an undeclared role before the rule is saved](https://raw.githubusercontent.com/Burgyn/MMLib.Alvo/main/assets/screenshots/rules-editor-light-2x.png)

## See it

The `tickets` entity of [`examples/help-desk`](https://github.com/Burgyn/MMLib.Alvo/blob/main/examples/help-desk/help-desk.alvo.json): anyone
authenticated may read, agents and admins may create, only an admin may delete, and two before-hooks trim the title and
refuse a high-priority ticket without a body.

[//]: # (excerpt: examples/help-desk/help-desk.alvo.json#/entities/tickets)
```json
{
  "audit": true,
  "fields": {
    "title": { "type": "string", "required": true, "maxLength": 120 },
    "body": { "type": "text" },
    "priority": { "type": "enum", "values": ["low", "normal", "high"], "default": "normal" },
    "status": { "type": "enum", "values": ["open", "closed"], "default": "open" },
    "estimate_hours": { "type": "decimal", "precision": 5, "scale": 1 },
    "hourly_rate": { "type": "decimal", "precision": 6, "scale": 2 },
    "estimate_cost": { "type": "decimal", "precision": 12, "scale": 2, "computed": "estimate_hours * hourly_rate" }
  },
  "rules": {
    "list": "'authenticated' in @user.roles",
    "get": "'authenticated' in @user.roles",
    "create": "'agent' in @user.roles || 'admin' in @user.roles",
    "update": "'agent' in @user.roles || 'admin' in @user.roles",
    "delete": "'admin' in @user.roles"
  },
  "hooks": {
    "beforeCreate": [
      { "action": { "mutate": { "title": { "$cel": "trim(new.title)" } } } },
      {
        "condition": "new.priority == 'high' && !has(new.body)",
        "action": { "reject": "A high-priority ticket needs a body." }
      }
    ]
  }
}
```

An agent creates a ticket, then a high-priority one without a body. Both exchanges were captured from a real host:

[//]: # (exchange: readme/see-it)
```http
POST /api/tickets HTTP/1.1
X-Alvo-Api-Key: agent.$ALVO_AGENT_KEY_SECRET
Content-Type: application/json

{
  "title": "  Printer on fire  ",
  "body": "Third floor."
}

HTTP/1.1 201 Created
Content-Type: application/json; charset=utf-8
ETag: "639272046278281070"
Location: /api/tickets/02ccad87-0d94-48b5-8ce2-e7c845bfb9d6

{
  "id": "02ccad87-0d94-48b5-8ce2-e7c845bfb9d6",
  "body": "Third floor.",
  "created_at": "2026-10-10T04:50:27.828107+00:00",
  "created_by": "5abcf0d4-de29-b506-10f9-aa428f1eedba",
  "estimate_cost": null,
  "estimate_hours": null,
  "hourly_rate": null,
  "priority": "normal",
  "status": "open",
  "title": "Printer on fire",
  "updated_at": "2026-10-10T04:50:27.828107+00:00",
  "updated_by": "5abcf0d4-de29-b506-10f9-aa428f1eedba"
}

POST /api/tickets HTTP/1.1
X-Alvo-Api-Key: agent.$ALVO_AGENT_KEY_SECRET
Content-Type: application/json

{
  "title": "Server room is flooding",
  "priority": "high"
}

HTTP/1.1 403 Forbidden
Content-Type: application/problem+json

{
  "type": "https://alvo.dev/errors/forbidden",
  "title": "Forbidden",
  "status": 403,
  "detail": "A high-priority ticket needs a body. (refused by the before-hook at '/entities/tickets/hooks/beforeCreate/1')"
}
```

## Quick start

Alvo is pre-v0.1: the descriptor format and the APIs may still change before the first tagged release. The quickest way
to see it is the standalone stack, built from a clone with Docker; the first build takes a few minutes.

```bash
git clone https://github.com/Burgyn/MMLib.Alvo && cd MMLib.Alvo
export ALVO_DEMO_KEY_SECRET="$(openssl rand -hex 16)"
docker compose up --build --wait --wait-timeout 60
curl -s localhost:8080/api/owners -H "X-Alvo-Api-Key: demo.$ALVO_DEMO_KEY_SECRET" \
  -H "Content-Type: application/json" -d '{"name":"Ada Lovelace"}'
curl -s localhost:8080/api/owners -H "X-Alvo-Api-Key: demo.$ALVO_DEMO_KEY_SECRET"
```

## Learn more

- Documentation: https://burgyn.github.io/MMLib.Alvo/
- Tutorial, your first backend in ten minutes: https://burgyn.github.io/MMLib.Alvo/start-here/tutorial/
- Embed Alvo in ASP.NET Core: https://burgyn.github.io/MMLib.Alvo/start-here/embed/
- For coding agents: https://burgyn.github.io/MMLib.Alvo/llms.txt
- Source and issues: https://github.com/Burgyn/MMLib.Alvo
- License: Apache-2.0, https://github.com/Burgyn/MMLib.Alvo/blob/main/LICENSE
