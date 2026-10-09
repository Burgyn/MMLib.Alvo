# F6 — README and the documentation site

**Status:** approved by the maintainer on 2026-10-09 in conversation. They chose Astro Starlight, GitHub Pages and English, then delegated the rest: "dalej ti doverujem, chcem vidiet az vysledok".
**Milestone:** F6 — v0.1 (`docs/PLAN.md`: "documentation, logo, release").
**Sources:**
- spec §2, "Fáza 2 — README a dokumentácia použitia": README contents, the `docs/` skeleton, and `llms.txt` from day one;
- analysis §0–§2 for positioning;
- this repo's examples, samples and architecture notes.

## 1. Intent

Alvo is usable enough to show the world. The documentation has to do three things, in this order of importance:

1. **Explain what Alvo is and why it is good**, in the first screen of the README and the first screen of the site.
2. **Get a developer to a running backend fast.** There is one honest quick start that works today. After it comes a tutorial that ends with a real backend with rules and hooks.
3. **Be a reference that cannot drift.** Every reference page that can be generated from a source of truth is generated. Every descriptor snippet in prose is validated in CI.

**Design quality is a requirement, not a nicety.** The site looks like a product, not a default theme.

**Success criteria:**
- **Sixty-second understanding.** A .NET developer who never heard of Alvo can say what it is and when to use it after 60 seconds on the README.
- **Ten-minute path.** They can reach a working API call (standalone) within 10 minutes, following only the docs.
- **No drift.** No reference fact (schema key, CEL function, problem type, public C# member) is hand-maintained.
- **CI-gated.** The docs build, link check and snippet validation run on every PR. `main` deploys to GitHub Pages.

## 2. Positioning (the message every entry page carries)

**One line:** *Describe your backend in one JSON file. Get a secure, production-shaped API — standalone in Docker or embedded in your ASP.NET Core app.*

Differentiators, each backed by a real feature and a link:
- **One descriptor, the whole backend.** Entities, fields, rules, hooks, computed fields, rollups, indexes, audit and webhooks live in `project.schema.json`-validated JSON. GitOps file or dashboard, same truth.
- **Security in the data layer.** Rules are CEL, compiled to parameterized SQL predicates (`USING`/`WITH CHECK` semantics). Default-deny. Hooks fail closed.
- **Agent-first.** There is a JSON Schema with a description on every key. Errors are structured with fix suggestions, and operations are idempotent. There is a management API and `llms.txt`. The built-in schema assistant uses the same skills the agents use.
- **.NET-native, two modes.** The same engine runs as a Docker image or as a NuGet package in your host, extended with C# (`AddCelFunction`, your own endpoints).
- **Admin dashboard** with a schema editor, rule/hook editors, data browser, history and rollback, plus an AI assistant.
- **Dynamic entities** (embedded): your end users define record types at runtime, in one shared partitioned store.

**Honest status:** pre-v0.1. No NuGet package or container image is published yet. Section 7 covers what the quick start does about it.

## 3. README (repo root)

The README replaces the current one. Its structure, in order:

1. **Header.**
   - The centered logo (`assets/alvo-logo.svg`, dark/light aware via `<picture>`).
   - The one-liner and a row of badges: CI, license, .NET 10, docs site, and NuGet only once published.
2. **Pitch.** Three sentences: what it is, who it is for (agentic developers; .NET teams embedding a configurable backend), and why it is different.
3. **"See it" block.**
   - A short descriptor excerpt (an entity with a rule and a before-hook) beside the HTTP exchange it produces: create, then list filtered by the rule.
   - The snippet is a real file under `examples/`, validated by the existing corpus tests.
4. **Quick start ≤ 10 lines.** The standalone path that works today (section 7), followed by "next: the 10-minute tutorial →".
5. **Feature grid.** Six to eight items, one line each, each linking to its guide.
6. **Admin dashboard screenshot.** Light, and dark if feasible, generated (section 6.4).
7. **Architecture diagram (Mermaid):** request → auth → policy (CEL→SQL) → data / before-hooks → transaction + outbox → after-hooks / webhooks / audit; the descriptor feeds the schema registry; admin and agents go through the management API.
8. **Packages table.** One sentence per shipped `MMLib.Alvo.*` package (taken from the csproj `Description`).
9. **Status and roadmap** (link to `docs/PLAN.md`), contributing, license (Apache-2.0) and a one-paragraph open-core statement per spec §2.

The README text is under about 250 lines. Depth goes to the site.

## 4. The site — `website/` (Astro + Starlight)

### 4.1 Toolchain

- **Stack:** Astro with `@astrojs/starlight`, latest stable at implementation time, versions pinned in `package.json` with a committed `package-lock.json`. Node LTS (22) in CI.
- **Additions:** `starlight-links-validator` for broken links. Mermaid rendered at build time via `rehype-mermaid` or an equivalent that needs no client JS. Expressive Code (Starlight's default) for code blocks, with titles, diffs and tabs.
- **Search:** Pagefind (Starlight default).
- **URL:** `site` = `https://burgyn.github.io`, `base` = `/MMLib.Alvo`.
- **`website/` is outside `MMLib.Alvo.slnx`.** `dotnet build` and the rings never touch Node.

### 4.2 Information architecture (Diátaxis)

- **Start here**
  - Why Alvo: positioning, when to use it, when not to, and a comparison with Supabase / PocketBase / ABP / hand-written ASP.NET Core.
  - Quick start (5 min).
  - Tutorial: your first backend (10 min). It builds a small descriptor step by step: entity → rule → before-hook with a built-in function → computed field → see it in the dashboard.
  - Embed Alvo in ASP.NET Core.
- **Guides** (task-oriented):
  - entities & fields;
  - access rules;
  - before-hooks (reject/mutate) and built-in functions;
  - custom CEL functions from C#;
  - computed fields & rollups;
  - indexes & uniqueness;
  - authentication & API keys;
  - multi-tenancy;
  - audit, history & rollback;
  - after-hooks, events & webhooks;
  - the admin dashboard;
  - the schema assistant (AI);
  - working with coding agents (schema, `llms.txt`, management API, idempotency);
  - querying the Data API (filters, sorting, paging);
  - running in production (Docker, PostgreSQL vs SQLite, configuration, secrets).
- **Concepts:**
  - the project descriptor;
  - CEL in Alvo (profiles: Rule, Computed, Condition, Mutate, Access; fail-closed);
  - the security model and trust boundary (analysis §10.3);
  - standalone vs embedded;
  - dynamic entities;
  - architecture (events, outbox, provider model).
- **Reference** (generated, section 5):
  - descriptor schema;
  - CEL functions;
  - Data API (OpenAPI);
  - Management API;
  - problem types;
  - C# API;
  - configuration keys.
- **Project:** roadmap, changelog (rendered from `CHANGELOG.md`), contributing, license.

**Content rules:**
- Every guide opens with what you will achieve, then shows the minimal descriptor, the request and the response.
- Every guide ends with "what can go wrong": the real problem types the reader can hit.
- Facts come from the code, the architecture notes (`docs/architecture/*.md`) and the specs. Anything not shipped is marked so; nothing is invented.
- Descriptor snippets live as files under `website/src/snippets/**.alvo.json` (or are pulled from `examples/`) and are imported, never pasted, so the CI can validate them (section 6.2).

### 4.3 Design

- **Brand source:** the existing identity.
  - `assets/alvo-logo.svg`, plus the admin's `alvo-mark.svg` and `alvo-wordmark.svg`;
  - the admin design tokens in `src/MMLib.Alvo.Admin/wwwroot/alvo.css`: palette, Public Sans for the UI, IBM Plex Mono for code.
  - The docs and the dashboard look like one product.
- **Theme:** a custom Starlight theme (CSS custom properties over Starlight's `--sl-*` tokens), designed for both light and dark. Accent and neutrals come from the admin palette. Typography is tuned for long reading (about 70ch measure).
- **Landing page:**
  - a custom `splash` template;
  - the hero has the one-liner, two CTAs (Quick start, GitHub) and a visual showing a descriptor turning into an API (a static side-by-side with a subtle CSS-only transition, no heavy JS);
  - then a "how it works" strip of three steps (describe → apply → call);
  - then a feature card grid;
  - then "two ways to run it" (standalone / embedded, with code for each);
  - then a dashboard screenshot and a closing CTA.
- **Accessibility and performance:**
  - Lighthouse (mobile) ≥ 95 on performance, accessibility and best practices for the landing page and one guide page;
  - contrast AA in both themes;
  - no layout shift from fonts (`font-display: swap` with metric fallbacks).

## 5. Generated reference

### 5.1 Generator: `tools/MMLib.Alvo.DocsGen`

- **Location:** a console project under `tools/`, in the solution so that it builds with the rest. It is not packed (`IsPackable=false`) and has no `MMLib.Alvo.*` package depending on it.
- **Interface:** `dotnet run --project tools/MMLib.Alvo.DocsGen -- --out website/src/content/docs/reference`. It writes Markdown/MDX with frontmatter.
- **Output policy:** generated files are gitignored, produced by the docs build (`npm run build` runs a `prebuild` step that calls the generator), and never committed. Drift is therefore impossible by construction.
- **Generated pages:**
  1. **Descriptor reference** from `schema/project.schema.json`: one page per top-level block, with every key's type, required-ness, enum values, default, the schema `description` text, and a nested anchor per path (`#entities.fields.maxLength`). The page also states conditional requirements (`decimal` → `precision`/`scale`, …).
  2. **CEL function catalog** from the engine's registered built-ins: name, overloads, parameter names and types, return type and summary. These are the same data `GET …/cel/functions` returns. The generator reads them in-process through whatever the engine exposes. If only an internal API exists, it uses an `InternalsVisibleTo` grant to the tools project, which must be justified in the PR.
  3. **Problem types:** every problem-type slug the API can emit, with title and meaning, from the code's single registry of them. If no single registry exists, this page is scoped to what can be enumerated without inventing one; the gap is noted and the page is not hand-written.
  4. **C# API:** the public surface of the shipped packages, from the XML doc files (`GenerateDocumentationFile` is already on) and reflection over the built assemblies. One page per package, grouped by namespace. Signatures come from the `PublicApi.*.verified.txt` baselines where convenient. The scope is limited to the types a host author touches: registration (`AddAlvo…`, `MapAlvo…`), options, `AddCelFunction`, the `IAlvoData` / `IAlvoManagement` ports, and the extension points in `docs/architecture/extensibility.md`.
  5. **Configuration keys:** the `Alvo:*` options and their defaults, from the options types.
  6. **Data API and Management API:** the OpenAPI document produced by booting the host over an example descriptor (`vehicle-registry`) with the generator capturing `/openapi/*.json`. It is rendered with Scalar as a static page or the `starlight-openapi` plugin, whichever builds without a running server.
- **`llms.txt` and `llms-full.txt`:** emitted into `website/public/`, following the llms.txt convention.
  - `llms.txt` is the index: title, summary, sections with links to every page.
  - `llms-full.txt` is the concatenated plain-text content of the guides, concepts and the descriptor reference.

### 5.2 Tests

- **Unit tests** (`test/MMLib.Alvo.DocsGen.Tests`, ring0): each renderer gets a small fixture input and produces exactly the expected Markdown, with no snapshot churn on the real schema. One additional test runs the real schema through and asserts structural facts: every top-level property has a page, and no key lacks a description.

## 6. CI, quality gates and assets

### 6.1 Workflow `.github/workflows/docs.yml`

- **Triggers:** PRs touching `website/**`, `tools/**`, `schema/**`, `src/**`, `README.md`, `CHANGELOG.md`, `examples/**`; and pushes to `main`.
- **Steps:**
  1. Set up .NET (global.json) and Node 22.
  2. `npm ci`.
  3. `npm run build`: generator, Astro build and link validation.
  4. Snippet validation (6.2).
  5. On `main` only, upload the Pages artifact and deploy (`actions/deploy-pages`).
- **Pages permissions:** scoped to the deploy job. Third-party actions are pinned by version like the rest of the repo.
- **Not required by the ruleset.** A docs failure does not block code merges; the maintainer can make it required later. This deviation is recorded here deliberately: the ruleset is the maintainer's call.

### 6.2 Snippet validation

Every `*.alvo.json` under `website/src/snippets/` is validated against `schema/project.schema.json`. The validation is done by extending the existing examples-corpus test (or an equivalent small test in ring0) to include that directory, so a snippet that would not validate fails a ring, not just the docs job. Snippets that are meant to apply are also applied in that test, the same way the corpus does for `examples/`.

### 6.3 Repo docs

`docs/` (specs, plans, architecture, product) stays as the internal engineering record and is not moved. The site links to architecture notes where they help, but does not publish the Slovak product docs.

### 6.4 Screenshots

`scripts/docs-screenshots` boots `scripts/demo-admin`-equivalent infrastructure (host over `bike-workshop`, seeded). It captures a fixed list of dashboard views in light and dark at 1440×900 with Playwright (the Node Playwright the site already has, or the .NET one the e2e suite uses) into `website/src/assets/screenshots/` and `assets/screenshots/` for the README. Screenshots are committed (they are images, not drift-prone facts) and regenerated on demand. The script is in no ring.

## 7. The honest quick start

- **Standalone, works today:**
  ```bash
  git clone https://github.com/Burgyn/MMLib.Alvo && cd MMLib.Alvo
  export ALVO_DEMO_KEY_SECRET="$(openssl rand -hex 16)"
  docker compose up --build --wait
  curl -s localhost:8080/api/owners -H "X-Alvo-Api-Key: demo.$ALVO_DEMO_KEY_SECRET"
  ```
  This is followed by the dashboard path: `scripts/demo-admin`.
- **Embedded:** the docs show the real `Program.cs` wiring, taken from the sample, with project references. A `dotnet add package MMLib.Alvo` variant sits in a tab marked *available from v0.1*.
- Commands in the quick start are exercised. The tutorial's descriptor is a validated snippet. The quick start's compose path is the one `scripts/test-e2e` already runs.

## 8. Out of scope

- Publishing NuGet or images, and the v0.1 release itself.
- Translations.
- Versioned docs (a single "latest" until v0.1 ships; Starlight versioning can be added then).
- A custom domain (`alvo.dev`); the `site`/`base` settings make the switch a one-line change later.
- Logo redesign.
- Docs for features this build refuses (automation rules, `function` actions, filtered rollups). These are mentioned only as "not yet", with the issue link.

## 9. Deviations

- **Spec §2 asked for a `docs/` skeleton.** The site lives in `website/` instead, because `docs/` already holds the internal engineering record, and mixing published and internal docs would publish Slovak specs and plans.
- **Generated reference is not committed**, unlike `scripts/gen-prototype-fixtures --check`. Here the consumer is a build, not a reviewer, so generating at build time removes the drift class instead of detecting it.
- **The docs workflow is not a required check** (6.1).
