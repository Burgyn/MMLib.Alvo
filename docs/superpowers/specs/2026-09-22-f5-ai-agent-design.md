# F5 — the AI agent: a secret store it can keep a key in, and a tool set that cannot apply anything

> Issue **#29** *([24] AI agent in the dashboard)*. The prerequisite the admin-dashboard design
> named and deferred — `ISecretStore` — is answered here rather than waited for, because #29's own
> Definition of Done contains it: *"switching providers = just changing the connection, key in the
> secret store."*

## 0. Why this document exists, and what it is not

`2026-09-18-f5-admin-dashboard-design.md` §6.4 deferred this issue with a reason that reads as a
precondition: *"needs `ISecretStore` (§7.1), which does not exist… It returns gated on
`GET {m}/info` reporting an AI connection."* That sentence is the whole brief for this document. It
says what has to become true before the assistant may ship, and it names the gate that proves it.

This design is **not** a plan for `baas-analyza` §7.1 as written. §7.1 is a platform service with
rotation, versioning, per-tenant isolation and four provider adapters; most of it exists for JWT
signing keys and webhook HMAC secrets, and this build has neither. Building it whole now would be
designing against consumers that are absent. What lands is the narrowest store that is **complete
for its one consumer** and honest about the rest — with each omission named here, and each owed
issue listed in §7.

### 0.1 What the sources ask for

- **`baas-analyza` §2.8** (*Admin dashboard / studio*) — the agent is built on **Microsoft Agent
  Framework** + `Microsoft.Extensions.AI`; the connection is *configurable* (local Ollama / LM Studio /
  foundry-local through an OpenAI-compatible API, or cloud Azure OpenAI / OpenAI / Anthropic); the
  key goes through `ISecretStore`, **never into the descriptor**; the agent writes through **the same
  Management API** as the CLI and the dashboard; every intervention is audited and subject to RBAC;
  it **proposes**, a human **confirms a diff**, destructive operations need explicit approval; and
  the dashboard works with no model configured at all.
- **`baas-analyza` §2.8 acceptance criteria** — *"prepnutie providera (lokálny ↔ OpenAI ↔ Anthropic)
  je len zmena connection v UI, žiadny zásah do kódu; kľúč je v secret store, nie v logoch ani
  descriptore"* and *"každá AI-navrhnutá zmena schémy/rules sa zobrazí ako diff a aplikuje až po
  potvrdení; zápis je auditovaný."*
- **`baas-analyza` §7.1** (*Secrets management*) — the port, its four implementations, rotation with
  key overlap, versioning, access audit, the **bootstrap paradox** (credentials for the secret store
  come from the environment — managed or workload identity — never from another secret), and
  *"žiadny secret v telemetrii."*
- **`alvo-specifikacia.md` §351** — the same, in the spec's own words, plus the `IAiConnection` port
  and *"MCP je len voliteľný adaptér nad ním."*
- **Issue #29's Definition of Done** — *"an AI change is applied only after a confirmed diff;
  switching providers = just changing the connection, key in the secret store."*

### 0.2 Deliberate deviations from the sources

| Deviation | Reason |
|---|---|
| `ISecretStore` ships **without** rotation, versioning, per-tenant isolation or access audit | Those exist in §7.1 for JWT signing keys, webhook HMAC and field-level encryption. None of the three is built: webhook delivery is unsigned and `secretRef` is unread (`UnhonouredSubsystems`), there is no field-level encryption, and identity signs nothing. A rotation protocol with no key to rotate is a design against an absent consumer. §7 owes the issue |
| Two provider implementations, not four | Configuration-backed covers env, user-secrets, K8s secrets **and** Azure Key Vault — all four reach `IConfiguration` through a provider the host adds, which is the .NET-idiomatic door and costs Alvo no adapter. The database-backed one exists for the single thing configuration cannot do: be written from a screen |
| **Anthropic does not ship as a connection kind** | There is no first-party `Microsoft.Extensions.AI` adapter for it. Shipping it means either a community package to license-check against `alvo-dotnet-conventions`, or an `IChatClient` over Anthropic's HTTP API that Alvo then owns. Both are decisions worth their own issue; the OpenAI-compatible kind already covers §2.8's whole *local* list, and `azure-openai` its enterprise one |
| The agent gets **no data tools** in this PR | §2.8 lists *"dotazy nad dátami"*. Every row read becomes model context, and a record somebody else wrote is untrusted input to a tool-calling loop. The descriptor tools alone satisfy the DoD; data tools are a separate trust argument and a separate issue (§7) |
| *"Every intervention audited"* is answered by **revision provenance**, not a transcript | The reads travel the operator's own credentials and take no privilege — which is exactly the decision §2.4 of the dashboard design already made for the data browser. The one thing that changes state is the apply, and it is attributable through `Author` and `Reason`, which `ManagementRevision` already documents as *"human- or agent-supplied"*. A transcript table means prompt text at rest, with retention and erasure obligations (§5 of the analysis) this build has no answer for |
| The dashboard writes the connection through `ISecretStore` rather than a new `IAlvoManagement` member | §3.4: every management member is an HTTP route by contract test, so a secret-write member widens the wire surface for a door F5 does not need |
| No `key_id` column held in reserve for a future rotation | `AlvoIdentitySchema` proved this repository can reconcile a column onto an existing table in the active provider's own DDL. The cheapest honest thing is to add the column when rotation is built, not to ship a field nothing writes |

---

## 1. Package boundary

| Piece | Home | Rule |
|---|---|---|
| `ISecretStore`, `SecretName`, `SecretShadowedException` | `MMLib.Alvo.Abstractions` | `package-boundary.md` names *a secret store* as its own example of rule (b) |
| `ConfigurationSecretStore` (read-only), `LayeredSecretStore` | `MMLib.Alvo` core | no foreign dependency: `IConfiguration` and nothing else. A package is earned, not assumed |
| `EfCoreSecretStore` (read/write, encrypted) | `MMLib.Alvo.Data.EntityFrameworkCore` | exactly `EfCoreOutboxStore`'s precedent; the table suffix joins `AlvoFrameworkTables` |
| `IAlvoAssistant`, `AssistantRequest`, `AssistantUpdate`, `AlvoAiConnection` | `MMLib.Alvo.Abstractions` | `MMLib.Alvo.Admin` holds no reference to the core, so a port it calls must live here |
| Microsoft Agent Framework + `Microsoft.Extensions.AI` implementation | **new `MMLib.Alvo.Ai`** | rule (a): an embedded host wanting a data API must not acquire an agent framework. Rule (b): the model provider is the swap point the DoD names |
| The assistant drawer | `MMLib.Alvo.Admin` | it is a screen |

**`MMLib.Alvo.Ai` references `MMLib.Alvo.Abstractions` only.** Its tools are `IAlvoManagement`
members, so *"the agent operates via the same Management API as the CLI"* (spec §0.5 contract 4) is
a **structural fact** rather than a promise — there is no core reference through which a second write
path could be reached. This is the same claim `EfDependencyBoundaryTests` already pins for
`MMLib.Alvo.Admin`, and it is pinned the same way, in both readings: the project file and the loaded
assembly.

`Microsoft.Agents.AI` is **MIT** and reached 1.0 GA in April 2026, so the analysis's named building
block is a licensable dependency rather than a preview bet — `alvo-dotnet-conventions`' licence rule
is satisfied without an exception.

---

## 2. The secret store

### 2.1 The port

```csharp
public interface ISecretStore
{
    ValueTask<string?> GetAsync(SecretName name, CancellationToken ct = default);
    ValueTask SetAsync(SecretName name, string value, CancellationToken ct = default);
    ValueTask<bool> DeleteAsync(SecretName name, CancellationToken ct = default);
    ValueTask<IReadOnlyList<SecretName>> ListNamesAsync(CancellationToken ct = default);
    bool CanWrite { get; }
}
```

**`ListNamesAsync` returns names, never values**, and there is no `GetAllAsync`. A listing is what a
settings screen needs; a bulk read is what an exfiltration needs, and nothing in this build has a use
for one.

**`CanWrite` is on the port rather than discovered by catching an exception.** A deployment whose
secrets arrive from Key Vault through configuration has no writable store, and the screen has to say
*"this deployment's secrets come from configuration"* **before** an operator types a key into a box
that will refuse it.

`SecretName` is a validated value type — `^[a-z][a-z0-9._-]{0,63}$` — for the reason
`AlvoOptions.SchemaPrefix` is validated: the name is interpolated into a configuration key and used
as a primary key, so it must be a validated identifier and never caller-supplied data. Parsing is
`TryParse`-shaped and the type carries no implicit conversion from `string`.

### 2.2 Layering: configuration wins, the database is writable

`LayeredSecretStore` reads configuration first and the database second; it writes only to the
database. The precedence is the GitOps precedence the whole product already has — a file or an
environment a deployment controls beats a record a screen wrote — and it is the **one** rule to
learn, because §3 applies the same order to the AI connection itself.

**A write to a name configuration already shadows is refused by name**
(`SecretShadowedException`), never swallowed. It is the single failure mode this layering can
produce, and the silent version of it is the worst kind: the operator saves a key, the screen says
saved, and every request keeps using the old one. It fails closed and it fails visibly.

### 2.3 Encryption, and the bootstrap paradox

`EfCoreSecretStore` stores AES-GCM ciphertext: a random 96-bit nonce per write, the ciphertext, and
the tag, in one `alvo_secrets` row keyed by name. The key-encryption key is read from a **file the
platform mounts** — `Alvo:Secrets:EncryptionKeyFile`, 32 bytes base64 — which is §7.1's own answer to
the bootstrap paradox: the credential for the secret store comes from the platform (a mounted K8s
secret, a workload identity's file, a Docker secret), never from another secret store.

**A key set in configuration itself — `Alvo:Secrets:EncryptionKey` — is refused at startup**, with
the message shape `Alvo__Admin__BootstrapPassword` already has. That refusal is this repository's own
precedent rather than a new opinion: a value in configuration is a value in an environment dump, a
process listing and a crash report, and §7.1's *"secrets never into logs/env dumps/git"* is exactly
the sentence it protects.

**With no key file, the writable store is not registered at all.** It does not fall back to
a derived key, a machine key, or plaintext. `CanWrite` is then false, the settings screen says the
connection must come from configuration in this deployment, and a default-deny build has refused
rather than invented a key an operator would believe protects them.

### 2.4 What this does not make true

`webhooks[].secretRef` and `httpCall.headersSecretRef` **stay unhonoured**. A store that *could*
resolve them is not a delivery path that signs them, and the entries in `UnhonouredSubsystems` and
`UnhonouredFeatures` are not touched by this work. The frozen schema's *"Secret name in
`ISecretStore`"* becomes a sentence with a type behind it, which is the only thing that changes.

---

## 3. The connection, and the gate the dashboard design wrote

### 3.1 The connection is infrastructure, never the descriptor

PLAN §4's invariant — *"Descriptor ≠ infra config"* — decides where the connection lives before any
convenience does. It resolves under §2.2's one rule:

| Source | Shape | Door it serves |
|---|---|---|
| configuration | `Alvo:Ai:Kind`, `:Endpoint`, `:Model`, `:ApiKeySecretRef` | GitOps, K8s, Azure: the connection is deployed, not clicked |
| the store | one secret, `alvo.ai.connection`, holding the same four fields as JSON | the dashboard: §2.8's *"just a change of connection in the UI"* |

Configuration wins. An operator whose deployment pins the connection sees it pinned and cannot
half-override it from a screen.

`AlvoAiConnection` carries `Kind` (`openai-compatible` | `azure-openai`), `Endpoint`, `Model` and the
resolved key. **Its `ToString` is the redacted form**, and the key is never a property anything logs;
`alvo-dotnet-conventions`' logging rules plus §7.1's *"no secret in telemetry"* are asserted by a
test, not by care.

### 3.2 `GET {m}/info` reports the connection — and not its endpoint

`ManagementInfo` gains `Ai`:

```json
{ "configured": true, "kind": "openai-compatible", "model": "qwen3:8b", "source": "store" }
```

**The endpoint is not reported.** `WebhookDelivery`'s own remark already records why — *"a secret
embedded in the URL is the only…"* — and an endpoint is exactly where a hosted-model key ends up when
somebody is in a hurry. Kind, model and source answer every question a settings screen has.

This is the gate the dashboard design named: *"It returns gated on `GET {m}/info` reporting an AI
connection."* Adding a fifth positional parameter to the `ManagementInfo` record is a public-API
change and will grow the approval baseline; nothing is released, and the alternative — a parallel
record — would be two shapes for one payload.

### 3.3 With no connection, the assistant is absent

Not badged *Not yet*. The dashboard design's §4.1 insists the two classes of "not yet" must not look
alike, and an unset connection is a third thing: nothing is missing from the build, something is
missing from the deployment. So the drawer and its launcher do not render, and **Settings** carries
the panel that fixes it. The dashboard keeps working without AI, which is §2.8's explicit
requirement.

### 3.4 The dashboard writes the connection through the port, not through a management route

`IAlvoManagement` gains **no** member. The dashboard injects `ISecretStore` directly — the shape it
already uses for `IAlvoUserAdministration`, which `ManagementGateway` takes as an optional
constructor dependency because only a deployment that has a store registers one.

The reason is blast radius. Every `IAlvoManagement` member has an HTTP route and a contract test that
holds it there, so a secret-write member would put secret writing on the surface the CLI, an MCP
adapter and any HTTP caller reach, in exchange for a door nothing in F5 needs: the CLI's answer to
*"set my model key"* is the deployment's own configuration, which already wins over the store (§3.1).
A port injected by the one screen that writes it keeps the write in-process and keeps
`GET {m}/info`'s `ai` block — which reports **whether** a connection exists and never its key — as
the only thing that crosses the wire.

### 3.5 The connection is resolved per turn, never cached in the circuit

The dashboard design's §10 records the sharpest lesson this repository has learned about Blazor
Server: *"a caller resolved once per circuit is a caller who cannot be revoked."* The same shape
applies here with a different consequence — a connection cached in a circuit makes *"switching
provider is a change in the UI"* false for exactly as long as an operator's tab stays open. The
factory resolves configuration and store on each turn.

---

## 4. The agent

### 4.1 The port

```csharp
public interface IAlvoAssistant
{
    IAsyncEnumerable<AssistantUpdate> AskAsync(AssistantRequest request, CancellationToken ct = default);
}
```

`AssistantRequest` carries the project, the operator's message and the prior turns. `AssistantUpdate`
is a closed hierarchy: a text delta, a tool-invoked notice, a **proposal** (candidate descriptor plus
the dry-run's plan and refusals), or a failure. Streaming, because §2.8 names latency and cost as the
thing to design for, and a server-interactive circuit can render tokens as they arrive.

### 4.2 Five tools, none of which can write

Every tool is an `IAlvoManagement` member:

| Tool | Member | Why the agent needs it |
|---|---|---|
| `get_schema` | `GetSchemaAsync` | what exists now, in the driver-agnostic model |
| `get_descriptor` | `GetDescriptorAsync` | the text to modify, and the revision an apply must echo |
| `get_capabilities` | `GetCapabilitiesAsync` | what this build honours — so it can refuse to author `automation` and say why |
| `get_revisions` | `ListRevisionsAsync` | what changed, and why, before proposing the next change |
| `validate_descriptor` | `ApplyDescriptorAsync(DryRun: true, AllowDestructive: false)` | the only way it learns whether its own proposal is even applicable |

**There is no apply tool.** Not *"the agent is instructed not to apply"* — the member is not in the
tool set, so the safety property is enforced by composition rather than by a prompt, and a prompt
injection that says *apply this now* has nothing to call. This is the difference between a guard and
a wish, and it is the same argument `GuardedUserAdministration` makes one layer down.

`validate_descriptor` runs with `AllowDestructive: false`, so a proposal that would drop a column
comes back as a **refusal the agent must show**, and enabling it is a decision an operator makes in
the existing confirm control. That is #29's *"destructive operations require approval"*, answered by
the guard that already exists rather than by a second one.

### 4.3 The proposal exits through the path every other change uses

The drawer writes the candidate into the existing per-operator `WorkingCopyStore` and routes to the
existing diff. The operator sees the same diff, clicks the same Apply, passes the same
`If-Match`/expected-revision optimistic lock. **No second write path exists**, which is how
*"everything clickable is exportable as code"* stays true for a change an agent composed.

### 4.4 Provenance

The apply carries `Author` = **the operator** — a machine cannot be accountable, and the person who
clicked is the one who decided — and `Reason` = a fixed `assistant: ` prefix plus the operator's own
request, truncated. `ManagementRevision.Reason` is already documented as *"the human- or
agent-supplied reason"*, so the frozen surface anticipated this.

**Configuration history does not parse that prefix.** It renders the reason verbatim, as it renders
every other one. A chip driven by a parsed prefix would be a second spelling of one truth — the
failure mode D2 of the dashboard design exists to avoid.

### 4.5 The prose the agent is not allowed to rewrite

`capabilities` consequences and refusal fixes are **served verbatim** (dashboard design §2.3). An LLM
paraphrasing them is the same defect as a Razor component doing it, in the one place an operator is
most likely to trust the sentence. The system prompt therefore instructs the agent to quote them, and
the drawer renders a refusal from the **tool result**, never from the model's retelling of it.

---

## 5. The trust boundary

**The agent is not a principal.** It calls `IAlvoManagement` in-process, and every member of that
interface *"gates itself"* against `IAlvoContextAccessor` — the operator's own context. The agent
therefore reaches exactly what the signed-in operator reaches, on reads as well as on the dry run,
and an operator below the required level is refused with the same `ManagementForbiddenException` a
click would produce. There is no service account, no bypass, and nothing new to audit.

**Everything the model reads is untrusted input.** The descriptor may have been written by somebody
else (this repository already treats it that way), and capability prose travels through the same
channel. Four properties contain it, and none of them is a prompt instruction:

1. no tool can write — §4.2;
2. the model's output is **data**: candidate JSON, parsed and validated by the existing validator,
   never executed;
3. the apply is behind a human click on the existing confirm control, with the existing
   optimistic lock;
4. the Data API is not in the tool set, so no record content enters the model's context at all.

**The system prompt is a fixed resource in the package and is not operator-editable.** An editable
system prompt is a privilege-escalation surface with no gate in front of it; making it configurable
is a decision that needs its own argument, and it does not have one yet.

**Secrets never reach the model, the log, or the wire beyond its own provider.** The key is read
through `ISecretStore` at the moment a client is built, is never a prompt field, never a log scope,
and never part of `ManagementInfo`.

---

## 6. Testing strategy and acceptance criteria

### 6.1 The contract suite comes first

`MMLib.Alvo.Testing` gains `SecretStoreContractTests` + `InMemorySecretStore`, following
`ISchemaMigrator`'s and `IDescriptorVersionStore`'s precedent. Every implementation runs it:
get-after-set, unknown name is `null` and not an exception, delete reports whether anything was
removed, `ListNamesAsync` returns names and no value, a write against a read-only store refuses, and
a redaction assertion that no member's `ToString` can print a value. **Interface-first** (§0 principle
1) means this suite exists before either implementation does.

### 6.2 What a test pins that agreement cannot

| Claim | How it is pinned |
|---|---|
| `MMLib.Alvo.Ai` cannot reach the core | the `EfDependencyBoundaryTests` shape, in both readings (project file, loaded assembly) |
| The tool set contains no write member | an assertion over the **registered** tools, not over the prompt |
| A proposal always passed through `validate_descriptor` | the scripted-client suite asserts the call order |
| Configuration shadows the store, and the write refuses | unit tests over `LayeredSecretStore` |
| No `ALVO_SECRET_KEY` ⇒ no writable store, no fallback | a registration test |
| A secret never appears in telemetry | a log-capturing test over a turn that resolves a key |
| A connection change takes effect without a restart | the factory test, plus §3.4's per-turn resolution |

### 6.3 No test ever reaches a model

`MMLib.Alvo.Ai.Tests` drives a **scripted `IChatClient`**: deterministic turns, deterministic tool
calls, no network, no key. The end-to-end suite registers a scripted `IAlvoAssistant` in the test host
and drives the drawer — ask, diff, confirm, and Configuration history showing the operator as author
with the `assistant: ` reason. What end-to-end must prove is the **flow**, not the model.

### 6.4 Acceptance criteria, made measurable

1. With `Alvo:Ai` unset and no stored connection, `GET {m}/info` reports `configured: false` and the
   dashboard renders no assistant affordance anywhere.
2. Switching kind and model from the Settings screen changes the provider used by the next turn,
   with no code change and no restart.
3. A descriptor change proposed by the agent is applied only after the operator confirms the diff;
   the appended revision names the operator and carries the `assistant: ` reason.
4. A proposal that would drop a column is reported as a refusal, not applied, until destructive
   changes are explicitly allowed in the existing control.
5. No test in the repository performs a network call to a model provider.
6. A secret's value appears in no log, no `ManagementInfo`, and no descriptor.

---

## 7. What this design requires of the milestone

Filed as issues the moment this design reaches `main` — the accountability pattern the dashboard
design's §7 established, for the same reason: a list nobody owns is how debt accumulates.

| Item | Why it is owed |
|---|---|
| **Secret rotation and versioning** (`ISecretStore`, §7.1) | required by JWT signing keys and webhook HMAC, neither of which exists yet; the port ships without them and the analysis's acceptance criterion *"rotation of a signing key without invalidating valid tokens"* is unmet by construction |
| **Per-tenant secret isolation** | §7.1 names it; this build has one secret namespace |
| **Secret access audit** | §7.1 points at §5; data-level audit is #42 and this belongs beside it |
| **An Anthropic connection kind** | §2.8 names it; no first-party MEAI adapter exists, so it needs a licence decision |
| **Data tools for the agent** | §2.8's *"dotazy nad dátami"*, deferred with §0.2's trust argument |
| **Webhook signing** (`secretRef`, honoured) | now unblocked by the store, and still not done — the store must not be read as having done it |

---

## 8. Documents this design changes

- `docs/architecture/package-boundary.md` — §Current projects gains `MMLib.Alvo.Ai`; the file's own
  instruction is *"Keep this list current."*
- `docs/architecture/management-api.md` — `GET {m}/info` gains the `ai` block.
- `docs/architecture/host.md` — `Alvo:Secrets:EncryptionKeyFile` and the `Alvo:Ai:*` configuration keys.
- `schema/project.schema.json` — **no change.** The connection is infrastructure; if this design
  touched the descriptor it would have got the invariant wrong.
- `docs/PLAN.md` — §3 when F5 closes.

## 9. Explicitly out of scope

Automation and function authoring (the agent refuses them, citing `capabilities` verbatim); a
transcript store; MCP (an adapter over the same Management API, and a separate issue); embeddings and
retrieval over the descriptor; cost accounting; and any agent affordance in the `alvo` CLI, which is
#213's surface, not this one.
