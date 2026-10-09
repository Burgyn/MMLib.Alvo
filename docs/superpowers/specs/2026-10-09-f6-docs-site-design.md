# F6 — README and the documentation site

**Status:** approved by the maintainer on 2026-10-09 in conversation. They chose Astro Starlight, GitHub Pages and English, then delegated the rest: "dalej ti doverujem, chcem vidiet az vysledok".
**Revised 2026-10-09** after two expert reviews of the design: a docs/IA review (7 Must, 15 Should, 10 Could) and a UX review (7 Must, 15 Should, 9 Could). The orchestrator ruled: adopt every Must of both, docs-review Should S1–S15, docs-review Could C6 and C10, and the trivial Coulds; skip the rest with a reason (§9). The plan (`docs/superpowers/plans/2026-10-09-f6-docs-site.md`) carries the per-item mapping.
**Milestone:** F6 — v0.1 (`docs/PLAN.md`: "documentation, logo, release").
**Sources:**
- spec §2, "Fáza 2 — README a dokumentácia použitia": README contents, the `docs/` skeleton, and `llms.txt` from day one;
- analysis §0–§2 for positioning;
- this repo's examples, samples and architecture notes.

## 1. Intent

Alvo is usable enough to show the world. The documentation has to do three things, in this order of importance:

1. **Explain what Alvo is and why it is good**, in the first screen of the README and the first screen of the site.
2. **Get a developer to a running backend fast.** There is one honest quick start that works today. After it comes a tutorial that ends with a real backend with rules and hooks, and a page that runs **the reader's own descriptor** with Docker only.
3. **Be a reference that cannot drift.** Every reference page that can be generated from a source of truth is generated. Every descriptor snippet in prose is validated in CI, and every HTTP exchange in prose is captured from a real host at build time.

**The docs are organised around what the reader does next, not around what Alvo has** (docs review). Sidebar groups are phrased as tasks, and every guide follows one template (§4.2).

**Design quality is a requirement, not a nicety.** The site looks like a product, not a default theme, and it works on a phone, in both themes and from the keyboard (§4.3).

**Success criteria:**
- **Sixty-second understanding.** A .NET developer who never heard of Alvo can say what it is and when to use it after 60 seconds on the README.
- **Ten-minute path.** They can reach a working API call (standalone) within 10 minutes, following only the docs.
- **No drift.** No reference fact (schema key, CEL function, problem type, public C# member, limit, configuration key) is hand-maintained. No descriptor and no HTTP response in prose is hand-written.
- **CI-gated.** The docs build, link check and snippet validation run on every PR. `main` deploys to GitHub Pages.

## 2. Positioning (the message every entry page carries)

**One line:** *Describe your backend in one JSON file. Get a secure, production-shaped API — standalone in Docker or embedded in your ASP.NET Core app.*

On the landing hero the line is split: the first sentence is the `<h1>`, and the second follows the audience lead "For .NET teams and their coding agents:" (UX S1). Everywhere else, the meta description and `llms.txt` included, it stays verbatim.

Differentiators, each backed by a real feature and a link:
- **One descriptor, the whole backend.** Entities, fields, rules, hooks, computed fields, rollups, indexes, audit and webhooks live in `project.schema.json`-validated JSON. GitOps file or dashboard, same truth.
- **Security in the data layer.** Rules are CEL, compiled to parameterized SQL predicates (`USING`/`WITH CHECK` semantics). Default-deny. Hooks fail closed.
- **Agent-first.** There is a JSON Schema with a description on every key. Errors are structured with fix suggestions, and operations are idempotent. There is a management API and `llms.txt`. The built-in schema assistant uses the same skills the agents use.
- **.NET-native, two modes.** The same engine runs as a Docker image or as a NuGet package in your host, extended with C# (`AddCelFunction`, your own endpoints).
- **Admin dashboard** with a schema editor, rule/hook editors, data browser, history and rollback, plus an AI assistant.
- **Dynamic entities** (embedded, **planned**): your end users define record types at runtime, in one shared partitioned store. Not shipped in this build. It is never listed as a README feature or a landing feature card; it appears only as "planned", linking its concept page.

**Honest status:** pre-v0.1. No NuGet package or container image is published yet. Section 7 covers what the quick start does about it. Every site page carries a one-line pre-v0.1 banner linking *Roadmap and status*, which lists what may change before v0.1 and what is stable.

## 3. README (repo root)

The README replaces the current one. Its structure, in order:

1. **Header.**
   - The centered logo (`assets/alvo-logo.svg`; it is a self-contained dark tile that reads in both GitHub themes, see §9).
   - The one-liner and a row of badges: CI, license, .NET 10, docs site, and NuGet only once published.
2. **Pitch.** Three sentences: what it is, who it is for (agentic developers; .NET teams embedding a configurable backend), and why it is different.
3. **"See it" block.**
   - A short descriptor excerpt (an entity with a `create` rule, a `default` and a before-hook) beside the HTTP exchange it produces: an authenticated create whose response shows the hook and the default, then a request a rule refuses (403 `forbidden`).
   - The descriptor is a real file under `examples/`, validated by the existing corpus tests and pinned to the README by an excerpt test. The exchange is captured from a real host (§5.1 item 9) and pinned to the README by a test on its request lines and statuses.
4. **Quick start ≤ 10 lines.** The standalone path that works today (section 7), identical to the landing page's, followed by "next: the 10-minute tutorial →".
5. **Feature list.** Six to eight items, one line each, each linking its guide. "Planned", never a phase code such as "F7".
6. **Admin dashboard screenshot.** A cropped 2× capture of the rules editor in a `<picture>` with light and dark sources (section 6.4).
7. **Architecture diagram (Mermaid `flowchart LR`)** with two subgraphs. *Runtime:* request → auth → policy (CEL→SQL) → data / before-hooks → transaction + outbox → after-hooks / webhooks / audit. *Control:* descriptor → schema registry; admin dashboard and agents → management API → schema registry. No dangling edge.
8. **Packages table.** One sentence per shipped `MMLib.Alvo.*` package (taken from the csproj `Description`).
9. **Status and roadmap** (link to `docs/PLAN.md`), **Contributing**, license (Apache-2.0) and a one-paragraph open-core statement per spec §2.

The README text is under about 250 lines. Depth goes to the site.

**The NuGet package readme is a separate file.** `Directory.Build.props` packs the README as every package's `PackageReadmeFile`, and nuget.org renders neither Mermaid, nor `<picture>`, nor relative links and images. A `PACKAGE_README.md` holds the NuGet-safe subset (pitch, the "See it" descriptor and exchange, the quick start, links to the site), with absolute URLs only. `Directory.Build.props` points at it, and a ring0 test keeps it free of relative links, Mermaid and `<picture>`.

## 4. The site — `website/` (Astro + Starlight)

### 4.1 Toolchain

- **Stack:** Astro with `@astrojs/starlight`, latest stable at implementation time, versions pinned in `package.json` with a committed `package-lock.json`. Node LTS (22) in CI.
- **Additions:** `starlight-links-validator` for broken links. Mermaid rendered at build time via `rehype-mermaid` or an equivalent that needs no client JS. Expressive Code (Starlight's default) for code blocks, with **one theme pair** shared by the landing page and the docs.
- **Search:** Pagefind (Starlight default). The search button shows "Ctrl K", and "⌘K" on Apple platforms.
- **URL:** `site` = `https://burgyn.github.io`, `base` = `/MMLib.Alvo`.
- **`website/` is outside `MMLib.Alvo.slnx`.** `dotnet build` and the rings never touch Node.

### 4.2 Information architecture

The sidebar, with these exact labels and in this order (docs review, "Proposed sidebar"). Labels are set explicitly in the sidebar config; nothing relies on directory-name autogeneration for a group label.

- **Start here:** Why Alvo · Quick start · Tutorial: your first backend · Run your own descriptor · Embed in ASP.NET Core · For coding agents · What works today
- **Model your data:** Entities and fields · Computed fields and rollups · Indexes and uniqueness · Apply and evolve your descriptor
- **Secure it:** Authentication and API keys · Access rules · Multi-tenancy
- **Add behaviour:** Validate and transform writes (before-hooks) · After-hooks, events and webhooks · Audit row changes
- **Use the API:** Read data: filter, sort, page · Write data safely · Handle errors
- **Extend in C#:** Use your own authentication · Call Alvo from your endpoints · Custom CEL functions
- **Operate:** Running in production · The admin dashboard · The schema assistant
- **Examples** (one page, generated from `examples/`)
- **Concepts:** The project descriptor · CEL in Alvo · Security model · Standalone and embedded · Architecture · Dynamic entities (coming in F7) · Glossary
- **Reference:** Overview · Descriptor schema · CEL functions · Problem types · Data API conventions · Data API — example (vehicle-registry) · Management API · Configuration keys · Limits and budgets · Capabilities in this build · C# API
- **Project:** Roadmap and status · Changelog · Contributing · License · FAQ

What the new pages carry:
- **Run your own descriptor** needs Docker (and git) only. The compose stack reads the descriptor path from `ALVO_DESCRIPTOR`, defaulting to today's vehicle-registry path, so the default behaviour is unchanged. The page shows how to declare dev keys whose roles the reader's descriptor declares (a key carrying an undeclared role authenticates nothing), how a change is applied (restart, or the Management API), and a collapsed `dotnet run` variant. The tutorial and every guide link to it under "Before you start".
- **What works today:** status, honoured / warned / refused blocks (from the capabilities page), limits, measured performance, the security posture.
- **Apply and evolve your descriptor:** file + restart vs the Management API vs the dashboard, the startup mode, dry run, `destructive-change`, `renamedFrom`, revisions with `If-Match` (428/412), rollback. The landing page's "Apply" step links here.
- **Handle errors:** the four ways to get a 403, an empty page vs a 403, 404 as a policy result, 415 as a missing header, branch on the slug never on `detail`, and which slugs an embedded host answers only with `AddAlvoProblemDetails()` (`function-failed`, `internal`, `unreadable-request`).
- **Use your own authentication** and **Call Alvo from your endpoints** (embedded), built from the embedded sample's README and code: two surfaces one backend, the role catalog → `AlvoContext`, the one thing embedded cannot do yet, the CSRF must-not (a cookie `HeaderName` is refused at startup), `IAlvoData`, `MapAlvoDataApi().WithTags`, `RoutePrefix`.
- **Read data** and **Write data safely** split the old "querying" guide; **Audit row changes** keeps row audit, and descriptor revisions move to *Apply and evolve*.
- **Embed in ASP.NET Core** gains a *Local package feed (today)* tab (`dotnet pack -o ~/alvo-feed`, `dotnet add package --source … --prerelease`) beside the *available from v0.1* NuGet tab.
- **Glossary:** role / scope / access level / rule; warned vs refused; apply vs revision; the CEL profiles; `@user`, `@tenant`, `new`, `old`; standalone vs embedded.
- **Data API conventions** (hand-written reference): the URL grammar (`field=op.value`), paging, `select`, ETags, batch, `Idempotency-Key`, the JSON `Content-Type` rule, and that every descriptor generates its own OpenAPI document at `GET /openapi/v1.json`.
- **FAQ:** short answers that link the guides.

**The guide template** (docs review S12). Every guide:
1. **Title and lede.** The title is a noun people search for; the lede is phrased as a task.
2. **"What you will achieve"** callout, including the time it takes and the starting point.
3. **Before you start:** one to three bullets — the stack (link *Run your own descriptor*), the key and roles, any prior guide.
4. **Steps.** Per step: the descriptor change (a validated snippet, new lines marked), the apply step, then the request and the real response, both visible inline (never behind tabs).
5. **How it works** (optional): at most one paragraph, linking the concept page.
6. **Options and variations** (optional): a table linking reference anchors.
7. **Not in this build** (optional): a caution aside linking *Capabilities in this build* and the issue.
8. **What can go wrong:** per problem — status · slug (linked to its section) · when · fix · which mode returns it.
9. **Reference:** the keys, functions and problem types the page uses.
10. **Next:** one recommended page, plus prev/next.

Tutorials drop items 5 and 6 and end with a "What you built" recap. Concept pages drop items 3, 4 and 8 and end with "Put it to work" links.

**Content rules:**
- Facts come from the code, the architecture notes (`docs/architecture/*.md`) and the specs. Anything not shipped is marked so; nothing is invented.
- **Every code block has a checked source.** Descriptors are files under `website/src/snippets/**.alvo.json` or `examples/`, validated (and, unless marked not runnable, applied) in ring0. HTTP exchanges are captured by the generator from a real host at build time (§5.1 item 9); a response body is never hand-written. C# comes from compiled files through an anchored excerpt component. Shell commands live as files under `website/src/snippets/shell/` and are executed by a task's verification step.
- The design mockups (`Main.dc.html`, `Docs.dc.html`, `Readme.dc.html`, kept outside the repo in the SDD workspace) are **layout and visual authority only**. Facts in them (the `reject` shape, a mutate facet break's status, a bare `docker compose up --wait`, curls without the key or a JSON `Content-Type`, a hard-coded function count, dynamic entities as a feature) are not.

### 4.3 Design

- **Brand source:** the existing identity.
  - `assets/alvo-logo.svg`, plus the admin's `alvo-mark.svg` and `alvo-wordmark.svg`;
  - the admin design tokens in `src/MMLib.Alvo.Admin/wwwroot/alvo.css`: palette, Public Sans for the UI, IBM Plex Mono for code.
  - The docs and the dashboard look like one product.
- **Tokens are imported, not copied.** A build step extracts `alvo.css`'s `@layer tokens` block into a generated stylesheet. The Starlight theme maps `--sl-*` onto those tokens (`var(--accent)`, `light-dark()` values, `color-mix()` where Starlight needs a tint no token provides). The only literal colours in the site are the two control-border values that give the search field 3:1 contrast, because the admin palette has no such token.
- **Theme:** both light and dark, one theme control (Starlight's Auto/Light/Dark select, 44px) shared by landing and docs. Code blocks use the admin's `--codeBg` in both themes (not a dark block in the light theme). One status-colour map everywhere: 2xx `--ok-fg`, 3xx `--neutral-fg`, 4xx `--warn-fg`, 5xx `--danger-fg`. The four Starlight asides use the ok / warn / danger / neutral tokens.
- **Reading:** prose measure 68ch; code and tables may extend to 760px. Tables scroll inside their own wrapper with a 560px minimum width.
- **Interaction:** a visible `:focus-visible` ring (2px accent outline, 2px offset) on every control; the search field's focus adds an accent border and a soft ring; prose links are underlined, cards and buttons are not. Touch targets are at least 44px.
- **Layout:** one shared header (56px tall) with the primary nav at ≥1024px; below 768px it shows logo · search (44×44) · menu (44×44), and the menu sheet carries the navigation, GitHub and the theme control. The docs sidebar is sticky on desktop and a drawer on mobile, with an "On this page" disclosure under the H1. No horizontal page scroll at 390px.
- **Landing page** (`Main.dc.html` is the layout authority):
  - eyebrow ".NET-native backend-as-a-service · pre-v0.1"; the split one-liner (§2); two CTAs (Quick start, GitHub) and a copyable `git clone …` with a "3 more steps →" link identical to the README's quick start;
  - at ≥1200px a split hero: copy in a `minmax(0, 520px)` column, and on the right a descriptor → API card showing the hero descriptor (an entity with a `create` rule, a `default` and a before-hook) and two captured exchanges (an authenticated create, a request a rule refuses). Code lines are ≤ 56 characters, no hero code block scrolls sideways at 1024/1280/1440, and the transition is CSS-only;
  - "How it works" as a numbered rail: describe → apply (linking *Apply and evolve*) → call;
  - four differentiator cards, each with a two-line proof from a real artifact instead of an icon: rules → SQL, hooks that fail closed, audit / history / rollback, dynamic entities marked "planned";
  - "Two ways to run it" (standalone / embedded, with code for each);
  - a cropped 2× rules-editor screenshot and a closing CTA.
  - Gutters `clamp(16px, 3vw, 32px)`; a type ramp of 18/20/26/40/64; radii on the 6/10/12/16 scale.
- **Accessibility and performance:**
  - Lighthouse (mobile) ≥ 95 on performance, accessibility and best practices for the landing page and one guide page;
  - contrast AA in both themes (axe), and 3:1 for control boundaries;
  - a UX check script (Playwright) over phone, tablet and desktop widths in both themes: no horizontal scroll, header height, 44px targets, focus visibility, hero code width, the search hint;
  - no layout shift from fonts (`font-display: swap` with metric fallbacks).

## 5. Generated reference

### 5.1 Generator: `tools/MMLib.Alvo.DocsGen`

- **Location:** a console project under `tools/`, in the solution so that it builds with the rest. It is not packed (`IsPackable=false`) and has no `MMLib.Alvo.*` package depending on it.
- **Interface:** `dotnet run --project tools/MMLib.Alvo.DocsGen -- --out website/src/content/docs/reference`. It writes Markdown/MDX with frontmatter.
- **Output policy:** generated files are gitignored, produced by the docs build (`npm run build` runs a `prebuild` step that calls the generator), and never committed. Drift is therefore impossible by construction.
- **Generated pages:**
  1. **Descriptor reference** from `schema/project.schema.json`: one page per top-level block, with every key's type, required-ness, enum values, default, the schema `description` text, and a nested anchor per path (`#entities.fields.maxLength`). The page also states conditional requirements (`decimal` → `precision`/`scale`, …). **The `entities` block is split** into Entities / Fields / Computed & rollups / Rules / Hooks / Indexes pages; anchors are unchanged. Each page caps its table of contents at level 2, and a ring0 test caps the key sections per page. Each page opens with a link to the guide that teaches it, from a block → guide map that a test keeps complete (a block with no guide is marked not shipped).
  2. **CEL function catalog** from the engine's registered built-ins: name, overloads, parameter names and types, return type and summary. These are the same data `GET …/cel/functions` returns. The generator reads them in-process through whatever the engine exposes. If only an internal API exists, it uses an `InternalsVisibleTo` grant to the tools project, which must be justified in the PR.
  3. **Problem types:** every problem-type slug the API can emit, from the code's single registry (`AlvoProblemTypes.All`). **One `##` section per slug, whose anchor is the slug**: status, meaning, causes, fix, its `violations[].code` values, the guides that cover it, and which hosts answer with it (`function-failed`, `internal` and `unreadable-request` reach an embedded caller only with `AddAlvoProblemDetails()`). Status and meaning come from the code; causes, fix, codes and guide links come from a hand-written notes file that a test keeps complete against `AlvoProblemTypes.All`, with every code proven to exist in the source and every guide link proven to resolve. The `type` URIs (`https://alvo.dev/errors/<slug>`) do not resolve today; the page and `llms.txt` state the mapping (`…/errors/<slug>` → this page's `#<slug>`).
  4. **C# API:** the public surface of the shipped packages, from the XML doc files (`GenerateDocumentationFile` is already on) and reflection over the built assemblies. One page per package, grouped by namespace. Signatures come from the `PublicApi.*.verified.txt` baselines where convenient. The scope is limited to the types a host author touches: registration (`AddAlvo…`, `MapAlvo…`), options, `AddCelFunction`, the `IAlvoData` / `IAlvoManagement` ports, and the extension points in `docs/architecture/extensibility.md`.
  5. **Configuration keys:** the `Alvo:*` options and their defaults, from the options types, **plus the keys read outside an options type** (`ConnectionStrings:Alvo`, `Alvo:Admin:Credential*PerMinute`, `Alvo:Admin:SessionRevalidationSeconds`, `Alvo:Events:WebhookAllowedNetworks`) from a tested list: a test fails when the source reads an `Alvo:*` key that neither an options type nor the list covers.
  6. **Data API and Management API:** the OpenAPI document produced by booting the host over an example descriptor (`vehicle-registry`) with the generator capturing `/openapi/*.json`. It is rendered with Scalar as a static page or the `starlight-openapi` plugin, whichever builds without a running server. The document's overview says it is the vehicle-registry example and that every descriptor generates its own at `GET /openapi/v1.json`.
  7. **Limits and budgets:** page size, body size, payload depth and keys, batch rows, filter depth / terms / `in` candidates, `Idempotency-Key` length, the dev-key secret floor and CEL nesting depth, each read from its named member (an options default or a constant) with that member's XML summary, and its configuration key when it has one.
  8. **Examples:** one entry per directory under `examples/` (except `_negative`): what it shows (from `examples/README.md`), whether it applies, the roles a key needs, and how to run it through *Run your own descriptor*.
  9. **Captured exchanges:** each `website/src/exchanges/**.exchange.json` names a descriptor, the dev keys and a sequence of requests with their expected statuses. The generator boots the real host in-process over that descriptor, sends the requests (with the API key and a JSON `Content-Type`), fails the build when a status differs from the expectation, and writes the request (as `curl` and as an HTTP message, the secret replaced by an environment-variable reference) and the real response for the pages to render.
  10. **The descriptor schema, served:** `schema/project.schema.json` is copied to `website/public/schema/v1/project.json`, so `https://burgyn.github.io/MMLib.Alvo/schema/v1/project.json` serves it. The docs show that URL and a VS Code `json.schemas` mapping. They do not claim that a `$schema` value alone gives editor checking until that is verified, and the schema's `$id` (`https://alvo.dev/schema/v1/project.json`) is unchanged (§10).
- **`llms.txt` and `llms-full.txt`:** emitted into `website/public/`, following the llms.txt convention.
  - `llms.txt` is the index: title, summary, sections with links to every page. The descriptor schema (served URL and raw GitHub URL) and the raw `alvo-descriptor-*` skills are listed in the main body, not under `## Optional`, and the problem-type URI mapping is stated.
  - `llms-full.txt` is the concatenated plain-text content of the start-here pages, the guides, the concepts, the descriptor reference, the CEL functions, the problem types, the capabilities and the configuration keys, with captured exchanges inlined.

### 5.2 Tests

- **Unit tests** (`test/MMLib.Alvo.DocsGen.Tests`, ring0): each renderer gets a small fixture input and produces exactly the expected Markdown, with no snapshot churn on the real schema. Structural tests over the real sources: every top-level property has a page, no key lacks a description, no descriptor page exceeds the key-section cap, every problem type has notes, every configuration key read in the source is documented, every exchange spec parses and names an existing descriptor, and one exchange spec runs against a real host. The full exchange set runs in the docs build.

## 6. CI, quality gates and assets

### 6.1 Workflow `.github/workflows/docs.yml`

- **Triggers:** PRs touching `website/**`, `tools/**`, `schema/**`, `src/**`, `README.md`, `PACKAGE_README.md`, `CHANGELOG.md`, `examples/**`, `docker-compose.yml`; and pushes to `main`.
- **Steps:**
  1. Set up .NET (global.json) and Node 22.
  2. `npm ci`.
  3. `npm run build`: token sync, generator (incl. captured exchanges), Astro build and link validation.
  4. Snippet validation (6.2).
  5. On `main` only, upload the Pages artifact and deploy (`actions/deploy-pages`).
- **Pages permissions:** scoped to the deploy job. Third-party actions are pinned by version like the rest of the repo.
- **Not required by the ruleset.** A docs failure does not block code merges; the maintainer can make it required later. This deviation is recorded here deliberately: the ruleset is the maintainer's call.

### 6.2 Snippet validation

Every `*.alvo.json` under `website/src/snippets/` is validated against `schema/project.schema.json`. The validation is done by extending the existing examples-corpus test (or an equivalent small test in ring0) to include that directory, so a snippet that would not validate fails a ring, not just the docs job. Snippets that are meant to apply are also applied in that test, the same way the corpus does for `examples/`.

### 6.3 Repo docs

`docs/` (specs, plans, architecture, product) stays as the internal engineering record and is not moved. The site links to architecture notes where they help, but does not publish the Slovak product docs.

### 6.4 Screenshots

`scripts/docs-screenshots` boots `scripts/demo-admin`-equivalent infrastructure (host over `bike-workshop`, seeded). It captures a fixed list of dashboard views in light and dark at 1440×900 with Playwright into `website/src/assets/screenshots/`, plus **cropped 2× captures** (device scale factor 2, clipped to the dashboard's `main` region so the app chrome is not doubled): the rules editor at 960×600 and a 390×520 phone crop, the data browser and history at 960×600. The README uses the rules-editor crop from `assets/screenshots/`. Screenshots are committed (they are images, not drift-prone facts) and regenerated on demand. The script is in no ring.

## 7. The honest quick start

- **Standalone, works today:**
  ```bash
  git clone https://github.com/Burgyn/MMLib.Alvo && cd MMLib.Alvo
  export ALVO_DEMO_KEY_SECRET="$(openssl rand -hex 16)"
  docker compose up --build --wait
  curl -s localhost:8080/api/owners -H "X-Alvo-Api-Key: demo.$ALVO_DEMO_KEY_SECRET"
  ```
  The quick start page states how long the first image build takes (measured), and creates a record before listing, so the list shows data; that POST and GET are captured exchanges over the same descriptor and key. This is followed by the dashboard path: `scripts/demo-admin`.
- **Your own descriptor:** `ALVO_DESCRIPTOR=./path/to/your.alvo.json docker compose up --build --wait`, with keys declared in a `docker-compose.override.yml` (gitignored) — *Run your own descriptor*.
- **Embedded:** the docs show the real `Program.cs` wiring, taken from the sample, with project references, a local package feed, and a `dotnet add package MMLib.Alvo` variant in a tab marked *available from v0.1*.
- Commands in the quick start are exercised. The tutorial's descriptor is a validated snippet. The quick start's compose path is the one `scripts/test-e2e` already runs; the `ALVO_DESCRIPTOR` default keeps that path identical.

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
- **The landing hero splits the verbatim one-liner** into an `<h1>` and a subhead with an audience lead (UX S1). The verbatim line stays the meta description, the README's line and `llms.txt`'s summary.
- **README logo stays a single file** (no `<picture>` pair, UX S15 partly): `assets/alvo-logo.svg` is a self-contained dark tile that reads in both GitHub themes, and a light variant would be logo work (§8). The screenshot does use `<picture>`.
- **Two literal colours** (the search field's control border, light and dark) exist outside the admin token layer, because the palette has no token with 3:1 contrast against the page background (UX S12).
- **The 44px touch-target minimum applies to sidebar and table-of-contents links only on coarse pointers or narrow viewports**; header, footer, pagination and button controls get it everywhere. A 44px desktop sidebar row would make the reference groups unscannable.
- **Hand-written reference page outside `reference/`:** *Data API conventions* lives at `data-api/conventions` because the generator owns and clears `reference/`.
- **Problem-type causes and fixes are hand-written** in a notes file, beside generated status and meaning; completeness, codes and links are tested, the prose is reviewed. There is no source of truth to generate causes from.
- **Skipped review items** (each with its reason) are listed in the plan's "Deviations and rulings".

## 10. Open items for the maintainer

- **`AlvoProblemTypes.BaseUri` (`https://alvo.dev/errors/`) does not resolve.** Changing it is a contract change; until then the docs explain the mapping. Options: own `alvo.dev` and redirect `/errors/*` to the problem-types page, or move the base URI to the Pages site before v0.1.
- **The schema's `$id` and `$schema` values (`https://alvo.dev/schema/v1/project.json`) do not resolve.** The Pages site serves a copy at `/MMLib.Alvo/schema/v1/project.json`; a redirect from `alvo.dev` (or a different `$id`) is the maintainer's call.
- **GitHub Pages source** must be set to "GitHub Actions" once, by hand.
- **Whether `docs.yml` becomes a required check.**
