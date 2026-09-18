# F5 — the admin design system Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `MMLib.Alvo.Admin`'s design system — the token contract, the component stylesheet, and a static gallery that renders every component in both themes at desktop and 375 px — as the shipped source of truth the Blazor shell will consume.

**Architecture:** `MMLib.Alvo.Admin` is a Razor Class Library whose `wwwroot/alvo.css` carries the whole design system as CSS custom properties plus component classes. The gallery at `docs/design/gallery.html` references **that exact file**, so it is a proof rather than a copy — a token that regresses regresses in both at once. Two facts in `MMLib.Alvo.Admin.Tests` parse the stylesheet: one asserts every token is declared in both themes, one asserts WCAG AA contrast on every foreground/background pair, which is spec §6.3 criterion 5 made automatic.

**Tech Stack:** .NET 10 Razor Class Library, plain CSS (no preprocessor, no component library — spec deviation D1), xUnit v3 on Microsoft.Testing.Platform, Shouldly.

**Spec:** `docs/superpowers/specs/2026-09-18-f5-admin-dashboard-design.md` — §5 in full, §4.1 for the warned/refused distinction the `NotYet` components encode, §6.3 criteria 2 and 5.

**Issue:** #227, first half. The second half — the Blazor shell wired to identity and `access` — is blocked by #146 and is a separate plan.

## Global Constraints

- **Target framework `net10.0`**, SDK pinned in `global.json`. Never redeclare `TargetFramework`, `Nullable`, `ImplicitUsings` or `LangVersion` in the `.csproj` — they come from `Directory.Build.props` and the convention suite fails if they are repeated.
- **Central Package Management.** Any new dependency's version goes in `Directory.Packages.props`; `PackageReference` carries no `Version`.
- **No component library** (spec D1). No MudBlazor, Radzen, FluentUI, Bootstrap or Tailwind. Grid virtualisation later uses Blazor's in-box `Virtualize`.
- **Typeface:** Public Sans (UI) and IBM Plex Mono (code), as the reference drawing sets them.
- **Type scale, exactly seven steps:** `--text-2xs: 11px`, `--text-xs: 12px`, `--text-sm: 13px`, `--text-base: 14px`, `--text-lg: 16px`, `--text-xl: 20px`, `--text-2xl: 26px`. No half-pixel sizes.
- **Radius, exactly five steps:** `--radius-xs: 6px`, `--radius-sm: 10px`, `--radius-md: 12px`, `--radius-lg: 16px`, `--radius-pill: 999px`.
- **Weights:** 500, 600, 700 only. No 400 in UI chrome.
- **Spacing:** multiples of 4 from 4 to 32, as `--space-1` … `--space-8`.
- **Light accent is `#0f7a48`, not `#128a52`** (spec D6). `#128a52` on white measures 4.39 : 1 and fails WCAG AA for normal text; `#0f7a48` measures 5.39 : 1.
- **Dark theme is selected by `[data-theme="dark"]` and by `@media (prefers-color-scheme: dark)`**, and `[data-theme="light"]` must win over the media query.
- **`prefers-reduced-motion` disables animation, never shortens it.**
- **Short, single-purpose methods** — roughly a 25-line ceiling; extract aggressively.
- **Shouldly for assertions.** FluentAssertions is banned by licence.
- Public API additions land in `PublicApi.MMLib.Alvo.Admin.verified.txt`; prefer `internal` unless a symbol is genuinely the contract.

---

### Task 1: The project exists and builds

**Files:**
- Create: `src/MMLib.Alvo.Admin/MMLib.Alvo.Admin.csproj`
- Create: `test/MMLib.Alvo.Admin.Tests/MMLib.Alvo.Admin.Tests.csproj`
- Modify: `MMLib.Alvo.slnx`
- Modify: `docs/architecture/package-boundary.md` (§Current projects — the file's own instruction is *"Keep this list current."*)

**Interfaces:**
- Produces: the assembly `MMLib.Alvo.Admin`, packable, whose `wwwroot/` static web assets later tasks write into.

- [ ] **Step 1: Create the RCL**

```bash
cd /Users/martiniak/Developer/GitHub/Burgyn/MMLib.Alvo
dotnet new razorclasslib -o src/MMLib.Alvo.Admin -n MMLib.Alvo.Admin
rm -rf src/MMLib.Alvo.Admin/Component1.razor src/MMLib.Alvo.Admin/ExampleJsInterop.cs src/MMLib.Alvo.Admin/wwwroot/background.png src/MMLib.Alvo.Admin/wwwroot/exampleJsInterop.js src/MMLib.Alvo.Admin/wwwroot/styles.css
```

- [ ] **Step 2: Strip the inherited props from the csproj**

The generated file declares `TargetFramework`, `Nullable` and `ImplicitUsings`. All three come from `Directory.Build.props` and repeating them fails `MMLib.Alvo.Conventions.Tests`. The csproj should end up as only `<Project Sdk="Microsoft.NET.Sdk.Razor">` with `<AddRazorSupportForMvc>` absent and `<SupportedPlatform Include="browser" />` kept.

- [ ] **Step 3: Create the test project and reference the production project**

```bash
dotnet new xunit3 -o test/MMLib.Alvo.Admin.Tests -n MMLib.Alvo.Admin.Tests
dotnet add test/MMLib.Alvo.Admin.Tests reference src/MMLib.Alvo.Admin
```

The `ProjectReference` is not optional: the linked shared architecture rules `Assembly.Load` the sibling assembly and throw without it.

- [ ] **Step 4: Register both in the solution**

```bash
dotnet sln MMLib.Alvo.slnx add src/MMLib.Alvo.Admin/MMLib.Alvo.Admin.csproj --solution-folder src
dotnet sln MMLib.Alvo.slnx add test/MMLib.Alvo.Admin.Tests/MMLib.Alvo.Admin.Tests.csproj --solution-folder test
```

- [ ] **Step 5: Build and run the convention suite**

Run: `dotnet build && dotnet test --project test/MMLib.Alvo.Conventions.Tests`
Expected: PASS. If it names a redeclared property, remove it from the csproj rather than suppressing the test.

- [ ] **Step 6: Generate and commit the public-API baseline**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests` once; the approval gate writes `PublicApi.MMLib.Alvo.Admin.verified.txt` beside the test project. Review it — at this point it should carry only assembly metadata, because no public type exists yet. That is the correct starting baseline.

- [ ] **Step 7: Add the project to `package-boundary.md` §Current projects**

One bullet, in the style of the existing entries, stating the earning rule: **(a)** Blazor is a heavy dependency an embedded consumer of the Data API must not acquire.

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo.Admin test/MMLib.Alvo.Admin.Tests MMLib.Alvo.slnx docs/architecture/package-boundary.md
git commit -m "feat(admin): add MMLib.Alvo.Admin, the package Blazor earns"
```

---

### Task 2: The token contract, and the two facts that hold it

**Files:**
- Create: `src/MMLib.Alvo.Admin/wwwroot/alvo.css`
- Create: `test/MMLib.Alvo.Admin.Tests/DesignTokenTests.cs`
- Create: `test/MMLib.Alvo.Admin.Tests/Internal/StylesheetTokens.cs`

**Interfaces:**
- Produces: `StylesheetTokens.Read(string css)` → `IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>` keyed by theme (`"light"`, `"dark"`) then token name including the leading `--`.
- Produces: `StylesheetTokens.ContrastRatio(string hexA, string hexB)` → `double`, the WCAG 2.1 relative-luminance ratio.

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class DesignTokenTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Themes =
        StylesheetTokens.Read(File.ReadAllText(StylesheetTokens.AlvoCssPath));

    [Fact]
    public void Both_themes_declare_exactly_the_same_token_names()
    {
        Themes["dark"].Keys.OrderBy(k => k)
            .ShouldBe(Themes["light"].Keys.OrderBy(k => k));
    }

    [Theory]
    [InlineData("--accent", "--accentText")]
    [InlineData("--bg", "--text")]
    [InlineData("--panel", "--text")]
    [InlineData("--panel", "--dim")]
    public void Every_foreground_pair_meets_WCAG_AA(string background, string foreground)
    {
        foreach (var theme in Themes)
        {
            StylesheetTokens
                .ContrastRatio(theme.Value[background], theme.Value[foreground])
                .ShouldBeGreaterThanOrEqualTo(4.5, $"{theme.Key}: {foreground} on {background}");
        }
    }

    [Fact]
    public void The_light_accent_is_the_one_that_passes_AA()
        => Themes["light"]["--accent"].ShouldBe("#0f7a48");
}
```

The last fact looks redundant beside the contrast theory and is not: it pins the *specific* decision of spec deviation D6, so a later change that swaps the accent for another passing colour is a conscious act rather than a silent one.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: FAIL — `StylesheetTokens` does not exist.

- [ ] **Step 3: Write `StylesheetTokens`**

`Read` scans for `:root{…}` and `[data-theme="dark"]{…}` blocks and parses `--name: value;` pairs. `ContrastRatio` implements WCAG 2.1: channel → sRGB → linear (`c <= 0.03928 ? c/12.92 : ((c+0.055)/1.055)^2.4`), luminance `0.2126R + 0.7152G + 0.0722B`, ratio `(L1+0.05)/(L2+0.05)` with `L1` the lighter. `AlvoCssPath` resolves through the existing `RepositoryRoot` helper in `MMLib.Alvo.Testing`, not a relative path from the test binary.

Keep each of the three concerns — block extraction, pair parsing, colour maths — in its own short method.

- [ ] **Step 4: Write `alvo.css`'s token layer**

`:root` carries the light theme; `[data-theme="dark"]` and `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) }` carry dark. Token names follow the reference drawing exactly — `--bg`, `--panel`, `--panel2`, `--codeBg`, `--border`, `--border2`, `--text`, `--dim`, `--faint`, `--accent`, `--accentText`, `--accentSoft`, `--accentBorder`, and the semantic state triples — plus the type, radius, spacing and weight scales from Global Constraints.

Light: `--bg:#f7f8fa`, `--panel:#ffffff`, `--panel2:#f2f4f7`, `--codeBg:#f4f6f8`, `--border:#e8eaef`, `--border2:#d3d7e0`, `--text:#1c1e26`, `--dim:#5f6577`, `--faint:#8a90a0`, `--accent:#0f7a48`, `--accentText:#ffffff`.
Dark: `--bg:#1e2029`, `--panel:#262833`, `--panel2:#2e3040`, `--codeBg:#191b22`, `--border:#34364a`, `--border2:#3b3e52`, `--text:#e8eaf0`, `--dim:#9aa0b8`, `--faint:#7c8199`, `--accent:#39E991`, `--accentText:#12241a`.

`--faint` is deliberately **not** in the contrast theory: it carries non-essential meta text, and WCAG AA does not bind decorative text. Note that in a comment beside the token so the omission reads as a decision.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: PASS, including every theme/pair combination of the theory.

- [ ] **Step 6: Commit**

```bash
git add src/MMLib.Alvo.Admin/wwwroot/alvo.css test/MMLib.Alvo.Admin.Tests
git commit -m "feat(admin): the token contract, with AA contrast asserted rather than eyeballed"
```

---

### Task 3: The component layer

**Files:**
- Modify: `src/MMLib.Alvo.Admin/wwwroot/alvo.css`
- Create: `src/MMLib.Alvo.Admin/wwwroot/alvo.js`

**Interfaces:**
- Produces: the class names the gallery and the later Blazor components both use. Every class is prefixed `a-`.

- [ ] **Step 1: Write the chrome and layout classes**

`a-shell`, `a-sidebar`, `a-header`, `a-content`, `a-nav`, `a-nav-item`, `a-nav-item--active`, `a-nav-sep`, `a-bottomnav`, `a-sheet`, `a-project-switcher`, `a-panel`, `a-card`, `a-section-heading`, `a-modal`, `a-drawer`.

- [ ] **Step 2: Write the control classes**

`a-btn` with `--primary`/`--ghost`/`--danger` and `--sm`/`--lg`, `a-input`, `a-select`, `a-textarea`, `a-check`, `a-toggle`, `a-code` (IBM Plex Mono over `--codeBg`), `a-chip`, `a-badge` with `--won`/`--offer`/`--lead`/`--lost`.

The focus ring is defined once, on `:focus-visible`, and is never removed anywhere in the sheet — spec §5.5.

- [ ] **Step 3: Write the data classes**

`a-grid`, `a-grid-head`, `a-grid-row`, `a-grid-cell`, `a-grid-cell--num` carrying `font-variant-numeric: tabular-nums`, `a-bulkbar`, `a-tabs`, `a-tab`, `a-row-card` (the 375 px substitute for a grid row).

- [ ] **Step 4: Write the feedback classes, including the two "not yet" classes**

`a-toast`, `a-skeleton` (shimmer), `a-empty`, `a-diff` with `a-diff-add`/`a-diff-del`, `a-confirm`.

Then the pair that encodes spec §4.1:

- `a-notyet` — the badge and empty state for a **warned** subsystem. Muted, informative, the section remains navigable.
- `a-refused` — the disabled state for a **refused** feature. Visibly inert, carrying the refusal text.

They must not look alike. A warned section is somewhere you can stand; a refused control is one you must not be able to use.

- [ ] **Step 5: Write `alvo.js`**

Three concerns only: the theme toggle writing `data-theme` and persisting it, `prefers-reduced-motion` respected via a `data-motion` attribute, and the keyboard map of spec §5.5 (`j`/`k`, `/`, `Enter`, `Esc`, `g`+letter, `⌘K`). No framework.

- [ ] **Step 6: Verify the token facts still pass**

Run: `dotnet test --project test/MMLib.Alvo.Admin.Tests`
Expected: PASS. The component layer must introduce no literal colour — every colour is a token, which is what keeps the contrast fact meaningful.

- [ ] **Step 7: Add the fact that forbids literal colours**

```csharp
[Fact]
public void The_component_layer_uses_tokens_rather_than_literal_colours()
{
    var css = File.ReadAllText(StylesheetTokens.AlvoCssPath);
    StylesheetTokens.LiteralColoursOutsideTokenBlocks(css).ShouldBeEmpty();
}
```

Run it, watch it fail if any literal slipped in, fix the stylesheet rather than the test.

- [ ] **Step 8: Commit**

```bash
git add src/MMLib.Alvo.Admin/wwwroot test/MMLib.Alvo.Admin.Tests
git commit -m "feat(admin): the component layer, with warned and refused as two different states"
```

---

### Task 4: The gallery

**Files:**
- Create: `docs/design/gallery.html`
- Create: `docs/design/README.md`

**Interfaces:**
- Consumes: `src/MMLib.Alvo.Admin/wwwroot/alvo.css` by relative path — **never a copy.**

- [ ] **Step 1: Write the gallery**

One page, referencing `../../src/MMLib.Alvo.Admin/wwwroot/alvo.css`. It renders, in order: the token plates (colour, type, radius, spacing), every control in every state, the data grid with its bulk bar, and then the six live screens plus the two `Not yet` screens as full compositions inside the shell. A theme switch and a 375 px frame sit at the top.

- [ ] **Step 2: Write `docs/design/README.md`**

Two paragraphs: that the gallery is a proof and not a source, that it must never carry its own colours, and how to open it.

- [ ] **Step 3: Add the fact that the gallery cannot drift**

```csharp
[Fact]
public void The_gallery_references_the_shipped_stylesheet_and_declares_no_style_of_its_own()
{
    var gallery = File.ReadAllText(StylesheetTokens.GalleryPath);
    gallery.ShouldContain("src/MMLib.Alvo.Admin/wwwroot/alvo.css");
    StylesheetTokens.LiteralColoursOutsideTokenBlocks(gallery).ShouldBeEmpty();
}
```

- [ ] **Step 4: Run the suite**

Run: `scripts/test-ring1`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add docs/design test/MMLib.Alvo.Admin.Tests
git commit -m "docs(admin): the design gallery, proving the stylesheet rather than copying it"
```

---

## What this plan deliberately leaves to the next one

The Razor components themselves, the shell's routing, the `IAlvoManagement` binding, and the Playwright harness. All four need a host to run in, and the host needs identity (#248) and `access` (#146) before an admin surface may be reachable at all — spec §1.3. Building Razor components against no data would be designing against imagined shapes, which is the failure spec §0 rejects approach B for.
