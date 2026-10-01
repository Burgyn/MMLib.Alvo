# MudBlazor for the Alvo admin dashboard: research notes

Researched 2026-09-24. Same questions as `fluentui-research.md`, answered for MudBlazor.

Sources:
- NuGet metadata, and the unpacked `MudBlazor.9.10.0.nupkg`.
- A shallow clone of `github.com/MudBlazor/MudBlazor` at tag **`v9.10.0`** (commit `0147a6c`, 2026-09-11). I read the docs from their Razor source in `src/MudBlazor.Docs/Pages/**`, because mudblazor.com is a WASM app that WebFetch cannot render.
- The v9 migration guide (GitHub issue #12666).
- The Material 3 guidance text mirrored in `material-components-android/docs`.
- **A spike I built and ran myself.** It is an RCL plus a host, using Alvo's `Directory.Build.props` settings and Alvo's `.editorconfig`, with MudBlazor 9.10.0 on SDK 10.0.100. I drove it with Playwright 1.56.1 / Chromium 1194, the same line as Alvo's pinned `Microsoft.Playwright` 1.56.0. The scratch files are `scratchpad/mud/spike/**` (`App.razor`, `MainLayout.razor`, `Pages/{Home,Grid,SignIn,EditDialog}.razor`) and `scratchpad/mud/pw/probe*.mjs`.

Legend: **[V]** means I checked it in a primary source. **[S]** means I observed it in my own spike. **[U]** means unverified. **(inference)** marks my own reasoning.

Repo paths below are relative to the MudBlazor repo root at `v9.10.0`. Browse them at `https://github.com/MudBlazor/MudBlazor/blob/v9.10.0/<path>`.

---

## 0. Decision in one paragraph

Use **`MudBlazor` 9.10.0** (stable, 2026-09-13, MIT), pinned exactly.

Unlike Fluent UI Blazor v5, MudBlazor has a **stable** net10.0 build, plain-HTML components (no shadow DOM, no `ElementInternals`), and it **compiled with 0 warnings under Alvo's `TreatWarningsAsErrors` + `latest-recommended` + `EnforceCodeStyleInBuild`** in Debug and Release [S].

Playwright's `GetByRole`/`GetByLabel`/`FillAsync` work on every control I probed [S]. That removes the biggest Fluent blocker.

The costs are real and specific:
- **A global CSS reset.** It includes `*{margin:0;padding:0;border-width:0}`, `button:focus{outline:none}` and `a:focus-visible{outline:none}`, and it is *unlayered*, so it beats every rule in Alvo's `@layer`-ed `alvo.css`.
- **No visible keyboard focus indicator** on buttons or checkboxes [S; open issue #11735].
- **Several a11y gaps:** the drawer is not a dialog, `MudAlert` has no role, and the tooltip has no `role=tooltip`/`aria-describedby` [S].
- **Server-side theming.** The theme is emitted server-side for *one* palette. Without the workaround in §3.3 you get a dark-mode flash.
- **Static SSR is not supported** for anything interactive.

All of these can be worked around. §3, §4 and §8 give the workarounds.

---

## 1. Version, license, install

### 1.1 Package facts

| Item | Value | Source |
|---|---|---|
| Latest stable | **9.10.0**, published 2026-09-13 | [V] NuGet registration `mudblazor`; GitHub release `v9.10.0` |
| TFMs | `net8.0`, `net9.0`, `net10.0` (net10.0 since 9.0.0-preview.1, 2025-12-20) | [V] nuspec; NuGet registration |
| net10.0 deps | `Microsoft.AspNetCore.Components` / `.Components.Web` / `Microsoft.Extensions.Localization` **10.0.1** (`exclude="Build,Analyzers"`) | [V] `MudBlazor.nuspec` |
| License | **MIT**. `requireLicenseAcceptance=true` is only a NuGet prompt | [V] nuspec, `LICENSE` |
| Pre-release | none open. The 9.x line is GA since 9.0.0 (2026-02-19) | [V] NuGet |
| Ships analyzers | `analyzers/dotnet/cs/MudBlazor.Analyzers.dll` (MUD0001/0002, plus MUD0010-0012 added in v9) | [V] nupkg; https://github.com/MudBlazor/MudBlazor/issues/12666 "New Analyzers" |
| DLL size | `lib/net10.0/MudBlazor.dll` **10 MB** (Material icon SVG strings are compiled in) | [V] nupkg |

**Release cadence** [V: GitHub releases API, https://github.com/MudBlazor/MudBlazor/releases]:
- 9.0.0 on 02-19, 9.1 on 03-03, 9.2 on 03-18, 9.3 on 04-08, 9.4 on 04-22, 9.5 on 05-26, 9.6 on 06-27, 9.7 on 07-09, 9.8 on 08-05, 9.9 on 08-24, 9.10 on 09-13.
- That is a **minor every 2-5 weeks**. Majors have come roughly yearly: v8.0.0 in early 2025, v9.0.0 on 2026-02-19.
- `ROADMAP.md` says only that "pace depends on volunteer availability" [V]. No v10 date is announced [U].

### 1.2 Breaking changes to expect

We are not migrating, but these tell us how v9 differs from what most blog posts and older answers show. They also tell us what a future v10 will look like. All are [V] from the v9 migration guide, https://github.com/MudBlazor/MudBlazor/issues/12666.

- **`MudGlobal` theming defaults were removed** (`ButtonDefaults.Variant`, `InputDefaults.Variant`, `MudGlobal.Rounded`, …). There is no global "all buttons outlined / no ripple" switch.
  - The guide's own advice is to **write wrapper components** (`AppButton.razor`), or use CSS and theme tokens.
  - This matches Alvo's `DesignSystem/*` layer: keep thin Alvo wrappers (`AlvoButton` → `MudButton`), so defaults live in one place (inference).
- **`MudMenu.ActivatorContent`** is now `RenderFragment<MenuContext>`. You must call `context.ToggleAsync` yourself.
- **Popover `Modal` default changed `true` → `false`.** Flipping is global only, via `PopoverOptions` (default `FlipAlways`).
- **`DialogService.Show`/`ShowMessageBox` sync methods were removed.** Use `ShowAsync` / `ShowMessageBoxAsync`.
- **`DefaultFocus` moved** to `MudDialogProvider` / `DialogOptions`.
- **`MudDataGrid.ServerData`** now takes a `CancellationToken`: `Func<GridState<T>, CancellationToken, Task<GridData<T>>>`.
- **Converters were fully reworked** (`IConverter<,>`, `IReversibleConverter<,>`). This matters if Alvo's `FormValue` locale-safe round-tripping is exposed through Mud's `Converter` parameter.
- **`MudTextField.AutoGrow` → `Sizing="InputSizing.Auto"`.** `MudSelect.SelectedValues` is now `IReadOnlyCollection`, and `OnOpen`/`OnClose` became `@bind-Open`.
- **`MudChat` was removed** and moved to MudX (https://github.com/MudXtra/MudX). We would build the assistant thread ourselves anyway.
- **`MudThemeProvider.ObserveSystemThemeChange`** was renamed to `ObserveSystemDarkModeChange`. `PaletteLight`/`PaletteDark` are now typed `Palette`.
- **Snackbars with an action no longer auto-dismiss** ("following Material Design 3"). Use `RequireInteraction` to override.
- **`MudTabs`:** `PanelClass` → `TabPanelsClass`, `TabPanelClass` → `TabButtonsClass`.

**Risk:** most search and LLM answers are v6-v8 syntax (`Show<T>`, `IActivatable`, `MudGlobal.*`, `DisableRipple`, `AutoGrow`). A coding agent will produce stale code unless its context points at v9 (inference).

### 1.3 Adding it to our Razor class library

Install steps [V: `src/MudBlazor.Docs/Pages/Getting Started/Installation/Installation.razor` + `Examples/*`], adapted to Alvo:

1. **Package.** `PackageVersion Include="MudBlazor" Version="9.10.0"` in `Directory.Packages.props`, and a `PackageReference` in `MMLib.Alvo.Admin.csproj`.
   - It coexists with the existing `FrameworkReference Microsoft.AspNetCore.App`. The transitive `Components.Web` 10.0.1 raised **no NU1510** in the spike [S: 0 warnings, Debug and Release, with Alvo's `.editorconfig`].
2. **`_Imports.razor`:** `@using MudBlazor`.
3. **Services.** The docs say `builder.Services.AddMudServices()` (namespace `MudBlazor.Services`) [V: `InstallServicesNET6ManualCode.html`].
   - Call it **inside Alvo's own admin registration extension**, so embedded hosts never learn about Mud (inference; same rule as the Fluent study).
   - Global options go in the lambda: `config.SnackbarConfiguration.*`, `config.PopoverOptions.*` [V: #12666 "Popover Configuration Consolidated"].
4. **CSS.** `<link href="_content/MudBlazor/MudBlazor.min.css" rel="stylesheet" />` in `AdminApp.razor`'s `<head>`.
   - The docs use `@Assets["…"]` for fingerprinting, which needs `app.MapStaticAssets()` [V: Installation.razor].
   - Alvo already routes assets through `AlvoAdminAssets`, so add a `MudStyleSheet`/`MudScript` pair there.
   - **Do not copy the docs' `<link href="https://fonts.googleapis.com/css2?family=Roboto…">`** [V: `InstallationManualCssFontsExample.razor`].
   - The library CSS itself loads **no remote resources**. The only `url()` is one inline `data:` PNG, and there is no `@import` [V: grep of `MudBlazor.min.css`].
   - Fonts come from the theme's `FontFamily`, so Alvo's self-hosted Public Sans / IBM Plex Mono `@font-face` rules keep working.
5. **JS.** `<script src="_content/MudBlazor/MudBlazor.min.js"></script>` "next to the default Blazor script at the end" [V: `InstallScriptManualCode.html`].
   - MudBlazor ships **no** `*.lib.module.js` JS initializer [V: nupkg `staticwebassets/` has only `MudBlazor.min.{css,js,js.map}`], so the tag is mandatory.
   - `MudThemeProvider` logs a "missing script" error if it is absent [V: `MudThemeProvider.razor.cs` `WarnIfScriptMissingAsync`].
   - Alvo's `alvo.js` stays in `<head>` for the pre-paint theme.
6. **Providers.** Put them in the **interactive** layout (`AdminLayout.razor`), not in `AdminApp.razor`:
   ```razor
   <MudThemeProvider /> <MudPopoverProvider /> <MudDialogProvider /> <MudSnackbarProvider />
   ```
   [V: `InstallationManualComponentsExample.razor`]. The docs are explicit:
   > "Static rendering is not supported: these providers must render in the same interactive render mode as the components that use them … With global interactivity … keep them here in MainLayout.razor"

   [V: Installation.razor]. Alvo's `AdminApp` passes `@rendermode` on `<AdminRoutes>`, so `AdminLayout` is interactive and is the right home [S: the spike uses the same `AcceptsInteractiveRouting()` pattern].
   - `PopoverOptions.ThrowOnDuplicateProvider` exists [V: XML docs]. Mount exactly **one** `MudPopoverProvider`.
7. **Host.** Nothing Mud-specific.
   - The spike needed `RequiresAspNetWebAssets=true` on the host to serve `blazor.web.js`. Alvo's host already sets it [V: `src/MMLib.Alvo.Host/MMLib.Alvo.Host.csproj:22`; S].

### 1.4 Analyzers under `TreatWarningsAsErrors`

- **Clean build.** I compiled a representative page set with `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true` and Alvo's `.editorconfig`. The pages used Button, IconButton, CheckBox, Switch, Select, Autocomplete, NumericField, multi-line TextField + `OnKeyDown`, Tabs, Menu, Tooltip, Chip, Badge, Alert, a temporary end Drawer, Dialog + `IDialogService`, `ISnackbar`, and DataGrid with `ServerData` + virtualization. **Result: 0 warnings, 0 errors**, Debug and Release [S].
- **MUD0002 is a feature for us.** A typo'd parameter (`Varient=`) fails the build:
  > `error MUD0002: Illegal Attribute 'Varient' on 'MudButton' using pattern 'LowerCase'`

  [S]. The default pattern `LowerCase` still allows `data-testid`, `aria-label`, `name` and `id` [S; V: `src/MudBlazor.Docs/Pages/Features/Analyzers`].
  - Tuning knobs: `MudAllowedAttributePattern` (`LowerCase` / `HTMLAttributes` / `DataAndAria` / `None` / `Any`) and `MudAllowedAttributeList` [V: same page; `build/MudBlazor.targets` `CompilerVisibleProperty`].
- **MUD0010-0012** (ParameterState misuse) only matter if we author components that inherit Mud bases [V: #12666].
- **Not yet tested:** a Release `docker build` over the real Dockerfile, per Alvo's "rings are Debug, CI is Release" lesson [U]. The spike's `-c Release` build was clean, but it is not the Dockerfile.

---

## 2. Static SSR (the sign-in page)

### 2.1 What the maintainers say

- **SSR is not planned.** Maintainer on #7999: "SSR is not planned" [V: https://github.com/MudBlazor/MudBlazor/issues/7999, 2024-07-19]. Another maintainer: "The library is 100% C#, so if you use a rendermode like `static` it will prevent all the logic in the library and hence nothing will work except the first rendering."
  - Theming works: "MudTheming should work fine in SSR (without the dynamic dark mode functionality)" [V: same thread].
- For auth pages they point to the third-party **`Extensions.MudBlazor.StaticInput`** [V: same thread; https://github.com/0phois/MudBlazor.StaticInput].
  - Latest is 4.1.0 (2026-03-11), MIT, single maintainer, 110 stars, last push 2026-06-15.
  - Its net10.0 group depends on `MudBlazor >= 9.1.0` **and on `Microsoft.AspNetCore.Http.Abstractions` 2.3.9**, a legacy ASP.NET Core 2.x package [V: NuGet registration].
  - That dependency is a supply-chain and NU1510-style smell for Alvo. **Recommend not taking it** (inference).

### 2.2 What the spike showed on a static page [S]

The page was `[ExcludeFromInteractiveRouting]`, had a plain `<form method="post" data-enhance="false">`, and contained `MudTextField`, `MudCheckBox` and `MudButton ButtonType.Submit`.

**What works:**
- `MudTextField` renders a **native `<input>`**. Unmatched attributes (`name="email"`, `id="email"`) pass through to it, and the `<label for=…>` points at it.
- The **form post carried the values**: `email=a@b.c&password=pw&remember=on`.
- **Enter in the password field submitted** the form (native implicit submission).
- Role and label locators resolve: `GetByLabel("Email")`, `GetByRole("textbox",{name:"Email"})`, `GetByRole("button",{name:"Sign in (Mud)"})` and `GetByRole("checkbox",{name:"Remember me"})`.
- `MudThemeProvider` renders its `<style>` block statically too.

**What breaks:**
- **The floating label overlaps the typed value.** The screenshot shows "a@b.c" drawn over "Email". The label's "shrink" state is set from C#, which never runs statically.
- **The checkbox does not visually change** when checked. The native input *is* checked, but the icon is chosen in C#.
- Stray attributes `__internal_stopPropagation_onclick` are emitted onto `<label>`/`<button>` (harmless).
- The ripple and hover logic are inert.
- Buttons are UPPERCASE unless the theme on that static layout also sets `Button.TextTransform = "none"`.

### 2.3 Recommended pattern

Keep `SignIn.razor` on **native `<input>`/`<button>` markup**, as today. Style it with a small set of Alvo CSS classes built from Mud's CSS variables (`--mud-palette-*`, `--mud-typography-*`).
- The alternative, `Variant.Outlined` Mud fields with the label forced to stay shrunk, is fragile. It depends on internal class names like `mud-shrink` (inference).
- Render `<MudThemeProvider Theme=…/>` in `SignInLayout` (it works statically) so the palette variables exist.
- Do **not** mount `MudPopoverProvider`/`MudDialogProvider`/`MudSnackbarProvider` there. They are interactive-only [V: Installation.razor].
- Accessibility is fine with native inputs. This is unlike Fluent v5, whose shadow-DOM inputs had no accessible name statically.

---

## 3. Theming for Alvo's identity

### 3.1 The model [V: `src/MudBlazor/Themes/**`, `Components/ThemeProvider/MudThemeProvider.razor(.cs)`]

- **`MudTheme`** holds `PaletteLight`, `PaletteDark` (both `Palette`), `Shadows` (`Shadow.Elevation` is a `string[]`), `Typography` (`Default`, `H1`-`H6`, `Subtitle1/2`, `Body1/2`, `Button`, `Caption`, `Overline`, each a `BaseTypography` with FontFamily/Size/Weight/LineHeight/LetterSpacing/TextTransform), `LayoutProperties` (`DefaultBorderRadius`, `AppbarHeight`, `DrawerWidthLeft/Right`, `DrawerMiniWidthLeft/Right`), `ZIndex`, and **`PseudoCss.Scope`** (default `:root`).
- **`MudThemeProvider`** takes `Theme`, `IsDarkMode` (two-way bindable), `ObserveSystemDarkModeChange` (**default `true`**), `DefaultScrollbar`, and `CurrentPalette`. It exposes `GetSystemDarkModeAsync()` and `WatchSystemDarkModeAsync()`.
- **How it emits CSS.** The provider renders **`<style class='mud-theme-provider'>{Scope}{ --mud-palette-*: …; --mud-typography-*; --mud-elevation-*; --mud-default-borderradius … }</style>`**, built with a `StringBuilder` in `BuildTheme()` / `GenerateTheme()`. It emits **only the active palette**: `var palette = _isDarkModeState.Value ? theme.PaletteDark : theme.PaletteLight;` [V: `MudThemeProvider.razor.cs` ~L245].
  - It also emits two more `<style>` blocks: a scrollbar block (suppressed by `DefaultScrollbar="true"`) and an always-on chart `:hover` filter block [V: `MudThemeProvider.razor`; S: 3 `<style>` elements in the page].
- **Consequence.** A dark-mode switch is a *server re-render* of that `<style>`. During prerender the server does not know Alvo's `localStorage['alvo.theme']`, so a dark user gets the light palette until the circuit connects. That is a flash. `ObserveSystemDarkModeChange` would also fight Alvo's toggle.

### 3.2 Material-isms, and how to switch them off

| Material-ism | Control | Evidence |
|---|---|---|
| UPPERCASE buttons | `Typography.Button.TextTransform = "none"` | [S] computed `none` |
| UPPERCASE **tabs** | **not** covered by the button typography. `.mud-tab{…text-transform:uppercase}` is hard-coded, so override it in Alvo CSS | [V: `MudBlazor.min.css`; S: tab computed `uppercase` while buttons were `none`] |
| Elevation / shadows | `Elevation="0"` per component (AppBar, Drawer, Paper, Tabs, Alert, SplitPanel…), or globally by setting `Theme.Shadows.Elevation` to 26 × `"none"` (inference from `Shadow.cs`), and `DropShadow="false"` on buttons | [V: XML `MudBaseButton.DropShadow`; S: AppBar `box-shadow:none` at `Elevation=0`] |
| Ripple | `Ripple="false"` per button/icon button/menu item/tab. **No global switch** in v9 (MudGlobal was removed), so use wrappers or CSS `.mud-ripple::after{display:none}` | [V: XML `MudBaseButton.Ripple`; #12666] |
| Underlined "Text" inputs | Pick `Variant="Variant.Outlined"` in Alvo wrappers (the default is `Text`) | [V: #12666 "MudBaseInput … default to Variant.Text"] |
| Floating labels | Inherent to `Text`/`Filled`/`Outlined`. Use `ShrinkLabel="true"` for a static top label (inference; the parameter name is from #12666 `InputDefaults.ShrinkLabel`) | [V/U] |
| Roboto | Set `Typography.Default.FontFamily = ["Public Sans", "system-ui", "sans-serif"]` | [S] |
| Default radius 4px | `LayoutProperties.DefaultBorderRadius = "6px"` (Alvo value) | [S] |
| Motion | No global reduced-motion support: open issue "Support reduced motion" | [V: https://github.com/MudBlazor/MudBlazor/issues/12067] |

- **Fonts.** Alvo self-hosts **Public Sans 500/600/700 only, with no 400** [V: `src/MMLib.Alvo.Admin/wwwroot/fonts/`]. Mud's body typography defaults to weight 400, so set `Default/Body1/Body2.FontWeight = "500"`, or the browser will substitute (inference).
  - Put IBM Plex Mono on Alvo's own identifier class. Mud has no monospace token besides `Typography.*.FontFamily`.
- **Density.** There is no global density token. Density is per component: `Dense` on `MudDataGrid`, `MudNavMenu`, `MudAlert`, `MudMenu`, `MudAppBar`, `MudList` [V: XML]; `Margin.Dense` on inputs; `Size.Small` on buttons, chips and icons.
  - Alvo's `alvo.density` (`comfortable|compact`) maps to one boolean that the Alvo wrappers pass on (inference).

### 3.3 Driving dark mode from Alvo's own toggle without a flash

I verified this approach in the spike [S: `probe3.mjs`]:

1. **Render two providers in `AdminLayout`:**
   ```razor
   <MudThemeProvider Theme="_theme" IsDarkMode="false" ObserveSystemDarkModeChange="false" DefaultScrollbar="true" />
   <MudThemeProvider Theme="_themeDarkScoped" IsDarkMode="true" ObserveSystemDarkModeChange="false" DefaultScrollbar="true" />
   ```
   `_themeDarkScoped` is the same theme with `PseudoCss = new PseudoCss { Scope = ":root[data-theme=dark]" }`.
   - The `Scope` setter trims `:` and re-prefixes it, so the scope comes out as `:root[data-theme=dark]` [V: `Themes/Models/PseudoCss.cs`; S: emitted `:root[data-theme=dark]{`].
   - Specificity (0,2,0) beats the light block's `:root` (0,1,0).
2. **Keep `alvo.js` in `<head>` setting `html[data-theme]`** before paint.
   - **Result:** `--mud-palette-primary` was the dark value (`#39e991`) and `body` was dark on the **first paint of the prerendered HTML**. It stayed dark after the circuit connected. Flipping `data-theme` to `light` switched instantly, with no server round-trip [S].
3. **Needed change: `alvo.js` must always write a resolved `data-theme`.** Today it writes it only when a choice is stored [V: `wwwroot/alvo.js` `applyStored`], and relies on `color-scheme` for the system default.
   - Scoped Mud variables cannot follow `prefers-color-scheme` by themselves.
   - Resolve `matchMedia('(prefers-color-scheme: dark)')` when nothing is stored, and listen for changes (inference).
4. Alvo code never needs `IsDarkMode`, except for components that choose colours in C# (charts; not used) (inference).
5. **Alternative.** Generate a static `mud-dark.css` from the same `MudTheme` in a test with `--check` semantics, like `gen-prototype-fixtures`. It avoids the second provider, but it is one more generated artefact (inference).

### 3.4 Brand mapping

- `PaletteLight.Primary = "#0f7a48"`, `PaletteDark.Primary = "#39e991"` (Alvo's D6 AA pair) [S].
- Set `PrimaryContrastText` explicitly to Alvo's `--accentText`, because the default is white. Dark `#39e991` needs dark text for contrast (inference). Open issue #13121 reports that "Semantic Color Palette Text Colors Always Default to White" [V: https://github.com/MudBlazor/MudBlazor/issues/13121].
- Map the surfaces: `Background`→`--bg`, `Surface`→`--panel`, `AppbarBackground`/`DrawerBackground`, `TextPrimary`→`--text`, `TextSecondary`→`--dim`, `LinesDefault`/`Divider`→`--border`, and `Success`/`Warning`/`Error`/`Info` → Alvo's `--ok`/`--warn`/`--danger`.
- The lowest-maintenance direction is to **define Alvo's remaining CSS variables in terms of `--mud-palette-*`**, so there is one source of truth: the C# `MudTheme` (inference).

---

## 4. Component mapping (every screen in `ui-inventory.md`)

### 4.1 Building blocks: names, behaviour, a11y

Parameter names below are from `lib/net10.0/MudBlazor.xml` of 9.10.0 [V]. Behaviour marked [S] was observed in the spike.

**Shell**
- **`MudLayout` + `MudAppBar` (`Dense`, `Elevation`) + `MudDrawer` + `MudMainContent` + `MudContainer`.**
  - `MudDrawer`: `Variant` (Temporary/Persistent/Responsive/Mini), `Anchor` (Start/End/Left/Right), `Breakpoint`, `ClipMode`, `Open`/`OpenChanged`, `Overlay`, `OverlayAutoClose`, `Width`, `MiniWidth`, `OpenMiniOnHover`.
  - Responsive means persistent above the breakpoint and temporary below it [V: `Pages/Components/Drawer/DrawerPage.razor`].
  - A Temporary drawer "closes automatically on navigation" [V: same].
  - At 390 px the Responsive nav drawer started `mud-drawer--closed` [S].
- **`MudNavMenu` / `MudNavLink` / `MudNavGroup`** (`Dense`, `Bordered`, `Rounded`, `MultiExpansion`; `Match`).
  - Links are real `<a href>`. There is no roving-tabindex arrow-key nav in the menu [U: not probed].
- **Bottom nav for phone:** there is no component. Keep Alvo's `NavList` bottom bar as custom markup on Mud tokens (inference).

**Overlays**
- **`IDialogService.ShowAsync<TDialog>(title, parameters, DialogOptions)`** returns `IDialogReference`, and `await reference.Result` gives a `DialogResult` (`Canceled`, `Data`). The component uses `<MudDialog><TitleContent/><DialogContent/><DialogActions/></MudDialog>` plus `[CascadingParameter] IMudDialogInstance` (`Close(DialogResult)`, `Cancel()`, `SetTitleAsync`) [V: XML; S].
  - **`DialogOptions` / `MudDialogProvider`:** `CloseOnEscapeKey`, `BackdropClick` (false = no light dismiss), `CloseButton`, `FullWidth`, `MaxWidth`, **`FullScreen`**, `Position` (`DialogPosition.CenterRight` etc.), `NoHeader`, `DefaultFocus` (`None`/`Element`/`FirstChild`/`LastChild`), `CloseOnNavigation`, `BackgroundClass` [V: XML].
  - For phone, set `FullScreen = true` when a breakpoint service reports xs/sm (inference; `IBrowserViewportService` exists [U: not probed]).
  - `MudDialog.OnKeyDown` supports custom keys, e.g. Enter to accept [V: DialogPage.razor].
  - **Semantics [S]:** `role="dialog"`, `aria-modal="true"`, and `aria-labelledby` pointing at the title.
    - A **focus trap** held focus through 5 × Tab.
    - **Escape closed** the dialog with `Canceled`.
    - **Focus returned to the trigger** button on close.
  - **Gotcha [S]:** on open, focus landed on the focus-trap **sentinel `<div>`**, not on the field with `AutoFocus="true"`. One Tab reached the field. Set `<MudDialogProvider DefaultFocus="DefaultFocus.FirstChild">` and verify it (spike item 3).
  - The v9 guide moved `DefaultFocus` to the provider [V: #12666].
- **`IDialogService.ShowMessageBoxAsync(title, message, yesText, noText, cancelText, options)`** covers simple confirms [V: #12666]. For Alvo's `ConfirmByName`, use a custom `MudDialog` with a `MudTextField` that enables the danger button.
- **Side panel for editors.** There are two options:
  - **`MudDrawer Anchor="Anchor.End" Variant="DrawerVariant.Temporary"`** is visually right, but it is **not a dialog** [S]:
    - It renders a bare `<aside>` with no role, no `aria-modal` and no label [V: `Components/Drawer/MudDrawer.razor` L4; S].
    - Focus does **not** move into it.
    - **Escape does not close it**.
    - There is no focus trap or focus return.
    - You would have to add `MudFocusTrap` (which "restores focus to the original element when disposed" [V: `FocusTrapPage.razor`]), an Escape handler, `role="dialog" aria-modal aria-labelledby` via `UserAttributes`, and an unsaved-changes guard yourself.
  - **A `MudDialog` positioned right** (`Position = DialogPosition.CenterRight`, plus a CSS class via `BackgroundClass`/`Class` for full height and fixed width) inherits all the dialog semantics verified above. **Recommended for record, field and person editors** (inference; the full-height styling is [U]).
  - A **persistent** docked panel (History/Rules aside) is layout content, not an overlay. Use `MudSplitPanel` (below) or a grid column.
- **`ISnackbar.Add(message, Severity, configure)` + `MudSnackbarProvider`.**
  - Global config via `AddMudServices(c => c.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight …)`.
  - Options: `VisibleStateDuration` (ms), `RequireInteraction`, `ShowCloseIcon`, `CloseAfterNavigation`, `DuplicatesBehavior`, `Action` + `OnClick`, `SnackbarVariant` [V: XML; SnackbarPage.razor].
  - [S] Each snackbar renders `role="alert" aria-live="polite"` (`role=alert` implies assertive, so the combination is contradictory; inference). It is **not focusable**. Its default position is top-right.
  - It injects a per-snackbar `<style>@keyframes …</style>` plus an inline `style="animation…"`. That matters for CSP (§6).
- **`MudAlert`** (`Severity`, `Variant`, `Dense`, `ShowCloseIcon`, `CloseIconClicked`, `Icon`/`NoIcon`, `ContentAlignment`).
  - **It has no `role` at all** [S: rendered `<div class="mud-alert …">`; V: `MudAlert.razor` has only a close-button `aria-label`].
  - Alvo's `ErrorPanel` → `MudAlert` wrapper must add `role="alert"` (errors) or `role="status"` via `UserAttributes`.
- **`MudMenu` / `MudMenuItem`** (`Label`, `Icon`, `AriaLabel`, `Dense`, `AnchorOrigin`, `ActivationEvent`, `PositionAtCursor`, `Modal`).
  - [S] The activator gets `aria-haspopup="menu"`, `aria-expanded` and `aria-controls`. The list is `role="menu"` with `role="menuitem"` items.
  - **ArrowDown moves focus into the items. Escape closes** and resets `aria-expanded="false"`.
  - Focus-return-after-Escape was ambiguous in my run [U].
- **`MudPopover`** is the base of Select, Autocomplete, Menu and Tooltip. It is **rendered inside `MudPopoverProvider`, a direct child of `<body>`**, not next to the trigger [S: provider parent = BODY]. That matters for scoped CSS and for Playwright scoping (§5).
- **`MudTooltip`** (`Text`/`TooltipContent`, `Placement`, `Delay`, `Duration`, `ShowOnHover`/`ShowOnFocus`/`ShowOnClick`, `Arrow`).
  - [S] The tooltip is **not `role="tooltip"`** and has no `aria-describedby` link to the trigger. `GetByRole("tooltip")` found nothing even while it was visible.
  - A tooltip around a non-focusable `MudChip` (`tabindex=-1`) is unreachable by keyboard. Treat tooltips as decoration, and never as the only carrier of information.
- **`MudHotkey`** (`Key`, `KeyModifiers`, `OnHotkeyPressed`, `PreventEventPropagation`, `Disabled`) could replace part of Alvo's `alvo.js` key map, e.g. ⌘K [V: XML; `HotkeyPage.razor`].
  - Keep `alvo.js` for now. It already handles "not while typing" (`isTypingTarget`), and MudHotkey's behaviour there is [U].
- **`MudExitPrompt`** (`Title`, `Text`, `Disabled`, `UseNativePrompt`) "shows a confirmation when users leave a page" [V: `ExitPromptPage.razor`; `TScripts/mudExitPrompt.js`]. It guards **navigation**, not dialog close. The dialog's own Cancel/Escape path needs the Alvo guard (see §7).

**Data**
- **`MudDataGrid<T>`** [V: XML]:
  - Data: `ServerData` (`GridState<T>` + `CancellationToken` → `GridData<T>{Items, TotalItems}`), `ReloadServerData()`.
  - Virtualization: `Virtualize`, `VirtualizeServerData`, `ItemSize`, `OverscanCount`. "You must restrict the height for virtualization to work" [V: `DataGridPage.razor`].
  - Rows: `RowClick` (`DataGridRowClickEventArgs<T>`), `RowClassFunc`, `SelectedItem(s)`, `SelectOnRowClick`.
  - Sorting and columns: `SortMode`, `SortDefinitions`, `ColumnResizeMode`.
  - Layout and states: `Dense`, `Hover`, `FixedHeader`, `Height`, `Loading`, `LoadingContent`, `NoRecordsContent`, `PagerContent` + `MudDataGridPager`, `ChildRowContent`.
  - **Measured at 10 columns [S]:**

    | Setup | first rows | row click → state | rows in DOM |
    |---|---|---|---|
    | server paging, 50/page | ~111 ms | ~51 ms | 50 |
    | `Virtualize` + `Height=600px`, 1000 items | ~114 ms | ~49 ms | 30 |
    | one page of 1000 rows | ~304 ms to render | ~150 ms | 1000 |

    Sort by header was ~230 ms. The header gets **`aria-sort`**. Sort and column-options are real `<button>`s (`aria-label` "Sort", "Column options").
  - **Keyboard gaps [S; V: open issues]:**
    - Rows are **not focusable**: no `tabindex`, no row keyboard nav. Keep Alvo's `j`/`k`/Enter row cursor (`RowCursor.cs` + `admin.js focusSelected`) and put `tabindex`/`aria-selected` on rows via `RowClassFunc`/`RowStyleFunc`/`UserAttributes` [U: row attribute hook].
    - #13883 (2026-09-17): "DataGrid: sort and column-menu icons are never revealed by keyboard focus".
    - #13253: filter popover does not close on Escape.
    - #11182: no cell keyboard navigation in edit mode.
    - #13408: `MudTableSortLabel` not keyboard accessible (MudTable, not the DataGrid).
    
    Links: https://github.com/MudBlazor/MudBlazor/issues/13883, /13253, /11182, /13408 [V].
  - **Recommendation for Alvo's keyset paging:**
    - Use `ServerData` with a pager of 25-100 rows (no offset jumps, so replace the numeric pager with Prev/Next in `PagerContent`), **or** `VirtualizeServerData` if infinite scroll is wanted.
    - Never render 1000 rows at once.
- **`MudTabs` / `MudTabPanel`** (`ActivePanelIndex` two-way, `KeepPanelsAlive`, `Border`, `Elevation`, `TabPanelsClass`).
  - [S] `role=tablist`/`tab`/`tabpanel`, `aria-selected`, `aria-controls`, **roving tabindex** (`0`/`-1`).
  - ArrowRight moved focus to the next tab **without activating it** (manual activation; Enter/Space activates) [S: focus "Rules", selected "Fields"].
  - Tab labels are UPPERCASE by default (§3.2).
- **`MudSplitPanel`** (`FirstPanel`, `SecondPanel`, `FirstPanelInitialSize`, `MinPanelSize`, `Horizontal`, `ResetOnDoubleClick`, `UseAsOverlay`) is the resizable-pane answer, new in v9 [V: XML].
  - The divider is `tabindex="0" role="separator"` with a localized `aria-label` and `aria-orientation` [V: `Components/SplitPanel/MudSplitPanel.razor`].
  - JS handles **Arrow keys (10 px step), Home and End**, and updates the ARIA values [V: `TScripts/mudSplitPanel.js` L42, L229-247].
  - This beats Fluent's "not accessible yet" splitter, and can replace Alvo's `SplitHandle` (verify keyboard and persistence; spike item 7).
- **`MudTimeline` / `MudTimelineItem`** (`TimelineOrientation`, `TimelinePosition`, `TimelineAlign`, `Reverse`) is a candidate for the History revision list [V: XML]. Its semantics (list vs div) are [U].
- **`MudBreadcrumbs`**, **`MudSkeleton`** (`SkeletonType`, `Width`, `Height`, `Animation`), **`MudProgressLinear`/`MudProgressCircular`**, **`MudChip`/`MudChipSet`** (`SelectionMode`, `SelectedValue(s)`, `CheckMark`), **`MudBadge`** [V: XML].
  - [S] `MudBadge` renders `role="status" aria-live="polite"` on the count, so a changing pending-count is announced.
  - Open #12613: "MudChipSet: Interactive controls must not be nested (WCAG)" [V: https://github.com/MudBlazor/MudBlazor/issues/12613].
- **`MudVirtualize<T>`** wraps Blazor's `Virtualize` [V: XML]. **`IScrollManager`** has `ScrollToBottomAsync(selector, ScrollBehavior)`, `ScrollIntoViewAsync(selector, …)` and `ScrollToVirtualizedItemAsync(…)` [V: XML]. Both are relevant for the assistant thread.
- **Empty states:** there is no component. Compose `MudStack` + `MudText` + `MudButton`, and use `MudDataGrid.NoRecordsContent` for grids (inference).

**Inputs**
- **`MudTextField<T>`:** `Label`, `Variant`, `Margin`, `Lines`, `MaxLines`, `Sizing` (`InputSizing.Auto` auto-grows), `Immediate`, `DebounceInterval`, `OnKeyDown`/`OnKeyUp`, `KeyDownPreventDefault`, `AutoFocus`, `Clearable`, `Error`/`ErrorText` (two-way in v9), `HelperText`, `For`.
  - "By default, text fields update on Enter or blur using the OnChange event. To get input events instantly, set Immediate to true" [V: `TextFieldPage.razor`].
  - [S] `Lines="3"` renders a real `<textarea>`.
  - **Ctrl+Enter and Cmd(Meta)+Enter** via `OnKeyDown` (`e.Key=="Enter" && (e.CtrlKey||e.MetaKey)`) sent the message and **cleared the box**, both after `fill()` and after fast `keyboard.type`+`Ctrl+Enter` [S, with `Immediate="true"`].
    - Clearing works because `Value`/`ValueChanged` is component-driven. That also fixes Alvo defect #3 (textarea child-content not clearing).
    - Each keydown is a circuit message under interactive server (inference). Acceptable for a chat box.
- **`MudSelect<T>` / `MudSelectItem`.**
  - [S] The input is `role="combobox" aria-haspopup="listbox"`. The popover options are `role="option"`.
  - Enter opens, ArrowDown ×2 then Enter selects. Focus + ArrowDown opens, and Space selects.
  - **`aria-controls` and `aria-activedescendant` are absent**, so screen readers get no active-option announcement (inference from the attributes).
  - Open #13582 "MudSelect does not accept Enter key for selecting an item" [V] did not reproduce in my simple case [S].
  - `GetByLabel("Field type")` matched **2 elements** (the input plus a hidden sibling). Prefer `GetByRole("combobox", …)`.
- **`MudAutocomplete<T>`:** `SearchFunc(string, CancellationToken)`, `DebounceInterval`, `CoerceText`/`CoerceValue`, `Strict`, `MaxItems`, `OpenOnFocus`, `SelectValueOnTab`, `ResetValueOnEmptyText`, `ItemSelectedTemplate`.
  - [S] `role="combobox"`. `fill("bik")` then clicking `option "Bike Shop"` worked, as did `fill("c")`, ArrowDown, Enter.
  - `aria-controls` and `aria-activedescendant` are **null** [S].
  - Open issues: #12684 (clear icon not reachable by screen readers), #11517 ("Enhance Accessibility for Automation Testing"), #13087 (opens on focus by default) [V].
  - It is still a better base for Alvo's `RefPicker` than Fluent's "accessibility not implemented" Autocomplete.
- **`MudNumericField<T>`** [S]: `role="spinbutton"` with Increment/Decrement buttons; `fill("42")` worked. Keep Alvo's `FormValue` for decimal and locale handling; v9 converters are culture-aware [V: #12666].
- **`MudSwitch<T>`** [S]: `role="switch"`, `check()` works. **`MudCheckBox<T>`** [S]: native checkbox, `check()` works. **`MudRadioGroup`** [V: XML].
- **`MudForm`** (`Model`, `Validation`, `IsValid`/`IsValidChanged`, `Errors`, `OnEnterPressed`, `SuppressImplicitSubmission`, `ReadOnly`, `Disabled`, `ValidationDelay`) **or `EditForm`** + DataAnnotations with Mud inputs' `For="() => Model.X"` [V: XML].
  - Alvo's validation is mostly server refusals (structured errors with a fix). Map them onto `Error`/`ErrorText` per field plus a form-level `MudAlert`. Use `EditForm` for an Enter-submits single-line form (inference).

**Visible focus (cross-cutting) [S]:**
- A Tab-focused `MudButton` (`:focus-visible` true) showed **no outline, no shadow and no background change**. A focused checkbox likewise showed nothing.
- The library CSS contains `button:focus{outline:none}` and `a:focus-visible{outline:none}` [V: `MudBlazor.min.css`].
- Open: #11735 "Add visual focus indicator to MudButton for accessibility", #12803 "Differentiate `:focus-visible` and `:active` styles" [V].
- **Alvo must ship its own `:focus-visible` ring** for `.mud-button-root`, `.mud-icon-button`, `.mud-nav-link`, `.mud-tab`, `.mud-menu-item` and `.mud-checkbox` (WCAG 2.4.7). This is a hard requirement, not polish.

### 4.2 Per screen

| Screen (inventory §1) | MudBlazor composition | Notes / decisions |
|---|---|---|
| **Shell: `AdminLayout`** | `MudLayout` › `MudAppBar Dense Elevation=0` (search button, `ThemeToggle`, `SignedInAs`) + `MudDrawer Variant=Responsive ClipMode=Always Elevation=0` › `MudNavMenu Dense` + `MudMainContent`. The 4 providers live here. `ErrorBoundary` → Alvo `ErrorPanel` (`MudAlert` + `role=alert`) | The phone "more sections" becomes the temporary drawer below the breakpoint [S]. Keep the bottom nav custom |
| **`CommandPalette` (⌘K)** | `MudDialog` (Position Top, `NoHeader`, `MaxWidth.Small`) + `MudTextField` + `MudList`/`MudListItem` | This closes inventory defect #11: Escape, focus and scroll lock come from the dialog. Keep ⌘K in `alvo.js`, or use `MudHotkey`. Arrow-key list selection stays Alvo code [U: `MudList` keyboard] |
| **`PendingBar`** | Sticky `MudPaper Elevation=0` strip with `MudBadge`/text + `MudButton`s. Discard → confirm dialog | |
| **`ThemeToggle`** | `MudIconButton` + `aria-label`, calling `alvo.js` toggle (not `IsDarkMode`) | §3.3 |
| **`ProjectSwitcher`** | `MudText` / `MudChip` label only | |
| **`SignedInAs`** | `MudMenu` (avatar/email) › a sign-out item that posts a form | The form post must stay a real `<form>` inside the menu item content [U] |
| **`AssistantDrawer`** | Persistent end `MudDrawer Variant=Persistent Anchor=End` (desktop) / temporary (phone), or a `MudSplitPanel` second pane. Thread = list of turns; input = `MudTextField Lines=3 Sizing=Auto Immediate` + `OnKeyDown` Ctrl/Cmd+Enter [S] + send `MudButton` disabled while busy. After each render of a new turn, call `IScrollManager.ScrollToBottomAsync("#thread", ScrollBehavior.Smooth)` [V: API] | Fixes defects #1-#3. Add `aria-live="polite"` on the thread container (inference). Drawer a11y gap (§4.1): it is non-modal here, so no trap is needed, but give it `role="complementary"` + label |
| **`/admin` Overview** | `MudGrid`/`MudStack` of `MudPaper`/`MudCard` stat tiles, `MudSkeleton` ×3 while loading, `MudAlert Severity.Info` + link for "project is new" | |
| **`/admin/welcome`** | `MudList` of 4 steps with check icons + `MudButton Href=…`, or a `MudTimeline` | |
| **`/admin/schema` SchemaList** | `MudToggleGroup` (List/Map) + `MudButton "New entity"` → **dialog** (`MudDialog`, name field, Enter submits via `EditForm`). List = `MudList`/`MudSimpleTable` with `MudChip` badges; Map = existing SVG renderer | Replaces the "top strip" pattern (inventory §2a.4). Add a `MudTextField` filter (feature gap #2) |
| **`/admin/schema/{Entity}`** | `MudTabs` (Fields / Relationships / Rules / Hooks / Indexes / API). Field editor, rename, remove-field impact → **right-positioned `MudDialog`** (editor) / centred confirm (remove). Index/hook add → dialog, not a permanent inline form. Rule edit → `MudTextField Lines` + an explicit Save button + a dirty `MudChip` | Unifies inventory §2a patterns 1/3. Remove field gets a real confirm |
| **`/admin/transfer`** | `MudButton` download + Alvo `CodeBlock`; import = `MudTextField Lines=12` + Import button | |
| **`/admin/changes` Preview** | `DescriptorDiff` (Alvo) + `PlanSteps` as `MudList`/`MudTimeline`; reason `MudTextField`; Apply `MudButton` busy via `Disabled` + `MudProgressCircular` in the label; destructive → `ConfirmByName` dialog; success → `MudAlert Severity.Success` panel with a link (persistent) **plus** a snackbar | |
| **`/admin/rules`** | `MudSplitPanel` (entities / simulator) [V: keyboard]. Entity picker: `MudTabs` ≤6, else `MudAutocomplete` (not a plain select). Simulator: `MudSelect` caller/operation + verdict `MudAlert` (`role=status`) | The simulation error stays a scoped `MudAlert` |
| **`/admin/automations`, `/functions` NotYet** | `MudAlert Severity.Info` / empty-state composition + `MudSkeleton` | |
| **`/admin/data` DataList** | `MudList`/`MudSimpleTable` of entities + scoped `MudChip`; tenant warning `MudAlert Warning` | |
| **`/admin/data/{Entity}` EntityData** | `MudDataGrid ServerData` (keyset Prev/Next in `PagerContent`), `Dense Hover FixedHeader`, `RowClick` → record editor dialog (right). Search = `MudTextField` + `DebounceInterval`. Delete inside the editor → confirm dialog. Save/delete feedback → `ISnackbar` | Keep `j`/`k`/Enter/`/` in `alvo.js` + `RowCursor` [S: rows not focusable]. Use `NoRecordsContent` for the RLS explanation |
| **`/admin/access` Access** | People = `MudDataGrid` or `MudList`; "Change" → person editor dialog (roles `MudChipSet SelectionMode.MultiSelection`, tenant `MudSelect`/`MudAutocomplete`, issue token, Disable). Disable → confirm dialog. Token → shown **inside that dialog** with a copy button (not at the page top). Add person → dialog; Create button disabled while busy | Fixes defects #6, #8 and #10; replaces the inline expanding row |
| **`/admin/history`** | `MudSplitPanel`: revision list (`MudList` or `MudTimeline`) / selected revision with a "Compare to previous" `MudToggleGroup` (JSON vs `DescriptorDiff`); rollback plan inline + `ConfirmByName` dialog | Feature gap #1 |
| **`/admin/settings`** | `MudPaper` sections; AI form = `EditForm` + `MudSelect` kind + `MudTextField`s (key `InputType.Password`) + Save busy; success → snackbar "Saved"; refusal → form-level `MudAlert` | Replaces the never-dismissed "Saved." text |
| **`/admin/integrations`** | Read-only `MudExpansionPanels` per block + `CodeBlock` | |
| **`/admin/sign-in`** | **Native markup** on Mud CSS variables, `MudThemeProvider` only (§2.3) | Static SSR |
| **Alvo primitives** | `Sheet`→dialog, `Panel`→`MudPaper`, `ErrorPanel`→`MudAlert`+role, `Field`→Mud input `Label`/`HelperText`, `ChipGroup`→`MudChipSet`, `ListRow`→`MudListItem`, `PageHeader`→`MudText Typo.h4`+`MudBreadcrumbs`+`MudMenu` overflow, `Skeleton`→`MudSkeleton`, `EmptyState`→composition, `ConfirmByName`→custom dialog, `SplitHandle`→`MudSplitPanel`, `Icon`→`MudIcon` (custom SVG strings keep Alvo's icon set) | Keep them as **thin Alvo wrappers** that fix defaults (v9 has no global defaults) and patch a11y (roles, focus ring) |

---

## 5. Testing

### 5.1 Playwright with MudBlazor [S: `probe.mjs`, `probe2.mjs`, `probe4.mjs`]

MudBlazor renders plain DOM with native inputs and ARIA attributes. `ariaSnapshot()` of the page listed `button`, `checkbox`, `switch`, `combobox`, `spinbutton`, `textbox`, `tablist`/`tab`/`tabpanel`, `status` and `complementary` roles with names.

| Probe | Result |
|---|---|
| `GetByRole("button",{name})` for MudButton/MudIconButton with `aria-label` | works |
| `GetByRole("checkbox",{name:"Required"}).CheckAsync()` | works |
| `GetByRole("switch",{name:"Unique"}).CheckAsync()` | works (`role=switch`) |
| `GetByLabel("Entity name").FillAsync()` (MudTextField) | works; label `for` targets the `<input>` |
| `GetByLabel("Ask the assistant")` with `Lines=3` | a `<textarea>`; `FillAsync` + `PressAsync("Control+Enter")` / `"Meta+Enter"` work |
| MudSelect: `GetByRole("combobox",{name}).ClickAsync()` → `GetByRole("option",{name:"integer"}).ClickAsync()` | works. **`GetByLabel` resolves 2 elements** (strict-mode violation) |
| MudAutocomplete: `FillAsync("bik")` → `GetByRole("option",{name:"Bike Shop"}).ClickAsync()` | works; also ArrowDown+Enter |
| MudNumericField: `GetByLabel("Max length").FillAsync("42")` | works (`spinbutton`) |
| Tabs: `GetByRole("tab",{name:"Rules"})`, `GetByRole("tab",{selected:true})` | works; **`InnerText` returns "FIELDS"** because of CSS uppercase, so match names case-insensitively or remove the uppercase |
| Menu: `GetByRole("button",{name:"More actions"})` → `GetByRole("menuitem")` | works; closed items can linger in the DOM, so assert **visibility** rather than count |
| Dialog: `GetByRole("dialog")`; fill inside it; `Escape` | works (`aria-labelledby` present) |
| Snackbar | `Locator(".mud-snackbar")` / `GetByRole("alert")` (role=alert) |
| Tooltip | **no `role=tooltip`**; use `GetByText` |
| DataGrid | `GetByRole("cell",{name, exact:true})`, `GetByRole("columnheader",{name:/Name/})` + `aria-sort` work; rows are `tr` without a role override |

Gotchas:
1. **Popovers live in `MudPopoverProvider` under `<body>`.** `page.GetByRole("option")` works. `dialog.GetByRole("option")` from inside a dialog does **not**, because options are not DOM descendants [S: provider parent = BODY]. Scope with `page`, not with the component.
2. **The circuit must be connected before interacting.** Clicks before the circuit are lost. Alvo's e2e already waits (inference). I waited for `window.Blazor` plus a delay; a better wait is a `data-ready` attribute written in `OnAfterRenderAsync` (inference).
3. **Transitions.** Popovers and dialogs animate. `PopoverOptions.Duration`/`Delay` and `TransitionDefaults` can be set to 0 in a test host [V: XML `PopoverOptions.Duration`; #12666 "Retained … TransitionDefaults"]. Playwright auto-waits for visibility, so this is mostly for speed [U].
4. **Debounce.** `DebounceInterval` fields need `ToHaveValue`/`WaitFor` assertions, not immediate reads. `Immediate="false"` fields commit on **blur or Enter**, so `FillAsync` followed by a click elsewhere is fine, but `FillAsync` then reading bound state is not [V: TextFieldPage.razor].
5. **Generated ids** (`mudinput…`) are unstable. Use roles, labels, or `data-testid`, which MUD0002 allows by default [S].
6. **Static sign-in:** native markup, so today's locators keep working.

### 5.2 bUnit
- MudBlazor's own suite uses **bUnit 2.10.3** with **NUnit**. Its test context sets `JSInterop.Mode = JSRuntimeMode.Loose` and calls `Services.AddMudServices(...)` [V: `src/MudBlazor.UnitTests.Shared/MudBlazor.UnitTests.Shared.csproj`; `Extensions/TestContextExtensions.cs` L15-17].
- **bUnit latest:** 2.11.3 (2026-09-13), MIT, net10.0. Its package dependencies are framework-agnostic (AngleSharp + ASP.NET Core only) [V: NuGet registration]. That suggests it runs under Alvo's xUnit v3 + MTP [U: not built]. Adding it is a new-library decision under `alvo-dotnet-conventions`.
- **Popover and dialog content** only renders if the test also renders `MudPopoverProvider`/`MudDialogProvider` (inference from the provider model).
- Alvo's pure logic (`WorkingCopy*`, `RowCursor`, `FormValue`, …) needs no bUnit.

---

## 6. Costs and risks

### 6.1 Size [V: measured from the 9.10.0 nupkg; gzip -9 / brotli -q 11 locally]

| Asset | raw | gzip | brotli |
|---|---|---|---|
| `MudBlazor.min.css` | 624,586 B | 66,105 B | 42,506 B |
| `MudBlazor.min.js` | 70,141 B | 17,025 B | 15,104 B |
| `MudBlazor.dll` (server only) | 10 MB | – | – |

- **First load is ~60 KB brotli.** That is less JS than Fluent v5 (~85 KB brotli), but more CSS (~43 KB vs ~13 KB). The CSS is large because it ships ~4,500 un-prefixed utility classes (`.d-flex`, `.border`, `.absolute`, colour classes…) [V: CSS scan].
- There is no separate icons package. Material icons are compiled into the 10 MB DLL as SVG strings. No icon font, and nothing extra in the browser.
- The theme `<style>` adds ~200 custom properties per provider per page [S: 213 `--mud-` lines].

### 6.2 CSS coexistence during a staged migration

**This is the main risk.**
- MudBlazor's CSS is **unlayered** and contains a global reset [V: CSS scan]:
  - `*{box-sizing:border-box;margin:0;padding:0;border-width:0;border-style:solid}`
  - `body{color;font-family;font-size…}`, `a{color;text-decoration:none}`, `a:focus-visible{outline:none}`, `button{…border:0;outline:0…}`, `button:focus{outline:none}`, `input,button,select,textarea{font:inherit…}`, `label{display:inline-block}`, `iframe{…}`
  - `#blazor-error-ui{…}` and `#components-reconnect-modal{…}` styling
- Alvo's `alvo.css` declares **`@layer tokens, base, layout, components, utilities;`** and puts everything in layers [V: `wwwroot/alvo.css` L30].
- **In the cascade, any unlayered rule beats every layered rule** for normal declarations (CSS Cascade 5; inference from the spec). Mud's `*{margin:0;padding:0;border-width:0}` would therefore override Alvo's layered margins, paddings and borders on every not-yet-migrated screen, and remove Alvo's focus outlines.
- **Mitigation: import Mud into the lowest layer.**
  ```css
  @layer mud, tokens, base, layout, components, utilities;
  @import url("../MudBlazor/MudBlazor.min.css") layer(mud);
  ```
  - `@import` must come before other rules except `@layer` statements, so the layer order statement can precede it. Path relative to `_content/MMLib.Alvo.Admin/` (inference).
  - Then Alvo's layers win on old screens, and on new screens Alvo simply stops styling the element.
  - The theme provider's `<style>` only defines custom properties, and the snackbar keyframes are uniquely named, so leaving those unlayered is harmless.
  - Cost: `@import` serialises the download, and it bypasses `@Assets` fingerprinting of the Mud file [U: whether static-asset fingerprinting rewrites `@import` URLs].
- **Class-name collisions.** Alvo uses an `a-` prefix. Mud's un-prefixed utilities (`.border`, `.absolute`, `.d-flex`, colour names) do not collide with any `a-` class [V: scan found no `a-` selectors in Mud CSS].
- **Reconnect UI.** Mud styles `#components-reconnect-modal`. Check whether Alvo customises reconnect UI [U].
- **Verdict (inference).** Screen-by-screen coexistence is feasible **once Mud's CSS is layered below Alvo's**. Without that, it is not.

### 6.3 CSP
Alvo ships no CSP today (Fluent study, §5.5 [V]). If it adds one:
- **`script-src 'self'` is fine.** `MudBlazor.min.js` has no `eval(` or `new Function` [V: grep = 0], and it loads from `_content/`.
- **`style-src` needs `'unsafe-inline'`.** Mud renders **`<style>` elements**: the theme provider, the always-on chart block, the scrollbar block unless `DefaultScrollbar`, and a **per-snackbar `@keyframes` block with a random name** [S].
  - The random names make hashes impossible for the snackbar. There is no nonce parameter [U: none found in XML].
  - Components also render inline `style="…"` attributes (20 on the spike home page) [S].
- JS sets `element.style.*` via CSSOM [V: bundle grep]. CSP does not govern that (inference). `setAttribute('style')` via Blazor's DOM patching likely is governed (inference [U]).
- **Net:** a strict `style-src` without `'unsafe-inline'` is not achievable with MudBlazor. The same was true of Fluent.

### 6.4 .NET 10
- **Stable on net10.0 since 9.0.0**, with 10 minors since [V]. No .NET-10 blocker was seen in the spike [S].
- Deps are pinned to `Components.Web` 10.0.1 (floor). Alvo's framework reference supplies the real version [S: no NU1510].

### 6.5 Other risks
- **Stale-knowledge risk for agents** (v8 syntax everywhere online) (§1.2).
- **Volunteer-driven project** [V: ROADMAP.md]. Majors break a lot (#12666 is long); expect a v10 rewrite pass in roughly a year.
- **The a11y gaps a wrapper must patch:** focus ring, `MudAlert` role, drawer-as-dialog, tooltip semantics, select/autocomplete `aria-activedescendant`, DataGrid row keyboard. 29 open issues mention "accessibility" [V: GitHub search 2026-09-24].
- **Material look leaks** (underlines, floating labels, uppercase tabs, ripples) unless wrappers and CSS neutralise them.

---

## 7. Interaction-pattern baseline (Material 3 / MudBlazor, adapted for an admin tool)

M3 sources are the guidance texts mirrored in `material-components-android` (fetchable). m3.material.io itself is a JS app that WebFetch cannot render. Where I quote m3.material.io, the text came from search-engine excerpts, marked [V-excerpt].

1. **Where editing happens**
   - **Multi-field edit or create of one item** (field, record, person, index, hook, new entity): a **modal side sheet**, built as a right-positioned `MudDialog`. On phone use `FullScreen`.
     - M3: "Modal side sheets appear in front of app content, disabling all other app functionality … They're often used in compact breakpoints, like mobile" [V-excerpt: https://m3.material.io/components/side-sheets/guidelines].
     - Full-screen dialogs are "for compact breakpoints" and "containing actions that require a series of tasks to complete" [V: https://github.com/material-components/material-components-android/blob/master/docs/components/Dialog.md "Full-screen dialog"; V-excerpt m3 dialogs].
     - Why a dialog and not `MudDrawer`: the dialog carries focus trap, Escape and focus return [S]; the drawer does not [S].
   - **Secondary content viewed alongside the main content** (assistant, History detail, Rules simulator): a **standard (non-modal) side sheet or split pane**. M3: "Standard side sheets co-exist with the screen's main UI region and allow for simultaneously viewing and interacting with both regions" [V: `docs/components/SideSheet.md` "Standard side sheet"]. Use `MudSplitPanel` or a persistent end `MudDrawer`.
   - **Inline editing** only for one atomic value with an explicit commit (a switch, a rename-in-place with Enter/Escape). No save-on-blur (inventory defect #7) (inference).
   - **Dialogs for decisions only.** "Dialogs are purposefully interruptive, so they should be used sparingly"; "Common use cases … alerts, quick selection, and confirmation" [V: Dialog.md]. Never open a dialog over a dialog, except over a full-screen one: "full-screen dialogs are the only dialogs over which other dialogs can appear" [V: Dialog.md]. An editor's destructive confirm therefore replaces the editor's content, or the editor is full-screen (inference).
2. **Destructive confirmation**
   - A centred confirm dialog with `BackdropClick=false` (no light dismiss). Escape = cancel.
   - Title = the action. The body leads with the consequence. Buttons: specific verb ("Delete record") + Cancel.
   - Use `ShowMessageBoxAsync` or a custom `MudDialog`. For irreversible, high-blast-radius actions (apply destructive plan, rollback, delete entity), keep Alvo's **type-the-name** step inside it.
   - This covers delete record, disable person, remove field/index/hook and discard working copy (inventory §2c.3-4).
   - M3 basic dialogs exist for "confirmation" [V: Dialog.md].
   - M3/MudBlazor: a snackbar with an **Undo** action is the lighter alternative when the action is truly undoable [V: Snackbar.md "offer the ability to perform an action, such as undoing"]. Alvo's staged working copy makes removal of a field/index/hook undoable before apply, so **snackbar + Undo** fits there (inference).
3. **Snackbar vs alert vs field error**
   - **Snackbar** (`ISnackbar`): brief confirmation of an action just taken ("Saved", "Applied as revision 12", "Deleted Customer 3"), about 4-6 s, one position (bottom-right on desktop, bottom on phone), at most one or two visible.
     - M3: "They shouldn't interrupt the user experience, and they don't require user input to disappear" [V: https://github.com/material-components/material-components-android/blob/master/docs/components/Snackbar.md]. Snackbars "show short updates about app processes at the bottom of the screen" [V-excerpt m3 snackbar].
     - MudBlazor v9: snackbars with an action require interaction by default, following M3 [V: #12666].
     - Snackbars are not focusable [S], so never put the only copy of important info (e.g. a credential token) in one.
   - **Alert** (`MudAlert`, persistent, in place): page, form or dialog state and every error. That means Alvo's structured refusal with its fix suggestion, rendered as the alert's action. Also "working copy has unapplied changes" and "engine read-only".
     - MudBlazor docs: "To statically embed an important message in a page see Alert" [V: `SnackbarPage.razor`].
     - Add `role="alert"` for errors and `role="status"` for info [S: missing by default].
     - Move focus to the form-level alert on a failed submit (inference; the same rule is in the Fluent study).
   - **Field error:** `Error`/`ErrorText` under the input for field-specific refusals.
4. **Forms and keys**
   - **Enter submits single-line forms.** Use `EditForm` + a `ButtonType.Submit` `MudButton`. Native implicit submission works because Mud renders native `<input>`s [S: static page]. `MudForm` has `OnEnterPressed`/`SuppressImplicitSubmission` [V: XML].
   - **Multi-line:** Enter inserts a newline; **Ctrl/Cmd+Enter submits**, via `OnKeyDown` checking `CtrlKey || MetaKey` [S].
   - **Escape** closes the topmost dialog, since `CloseOnEscapeKey` is on [V; S]. For an editor with unsaved changes, intercept Cancel and Escape and ask "Discard changes?".
     - Options: `BackdropClick=false` + `CloseOnEscapeKey=false` + a custom `MudDialog.OnKeyDown`, or handle it in the Alvo editor wrapper using `RecordDraft`'s dirty state (inference; fixes defect #5).
     - Use `MudExitPrompt` for **navigation** away from a dirty page [V].
   - **Busy while saving:** disable the submit button (`Disabled="@_saving"`) and show an inline `MudProgressCircular Size.Small` in its label. Keep Cancel enabled. This closes the double-submit defect #6 everywhere (inference).
   - **Focus:** on dialog open, move focus to the first field (`DefaultFocus.FirstChild`, to verify: §4.1 gotcha). On close, focus returns to the trigger [S]. Keep Alvo's `FocusReturn.cs` for non-dialog cases.
5. **Empty, loading and error**
   - **Loading:** `MudSkeleton` for known-shape content (grid rows, cards). Show `MudProgressLinear Indeterminate` at the top of a panel for refreshes. Show the assistant's "thinking" indicator immediately. Keep content in place, with no spinner-only page (inference; MudBlazor gives no timing guidance [U]).
   - **Empty:** one sentence on what the operator can do, plus one primary CTA. Use `MudDataGrid.NoRecordsContent` for grids.
   - **Error:** an in-place `MudAlert` with the fix action. Page-level failures go to the `ErrorBoundary` → `ErrorPanel` wrapper. No toasts for errors, which matches Alvo's existing rule (inventory §2e).

---

## 8. Spikes to run first, then migration order

### 8.1 Spikes (in order; each is short)
1. **Real-repo build.** Add `MudBlazor` 9.10.0 to `MMLib.Alvo.Admin`. Run `dotnet build`, ring1 (architecture tests, public-API approval, since Mud types must not leak into Alvo's public surface) and a **Release `docker build`**. The scratch spike was clean [S]; the repo and Dockerfile are unverified [U].
2. **CSS layering.** `@import … layer(mud)` below Alvo's layers. Screenshot every current screen before and after, using the existing `scripts/test-admin-e2e` plus screenshots. Nothing may move, and focus outlines must survive.
3. **Dialog focus.** `DefaultFocus.FirstChild` / `AutoFocus` actually lands on the first field. Check a right-positioned, full-height `MudDialog` as the editor surface, and `FullScreen` below 600 px.
4. **Theme.** Wire the two-provider scoped dark palette (§3.3) to `alvo.js`, which must always write a resolved `data-theme`. Check system-preference changes, and run an AA contrast check of `PrimaryContrastText` in both modes.
5. **Wrapper layer.** Build `AlvoButton`, `AlvoTextField`, `AlvoAlert`, `AlvoDialog`/`AlvoEditor` and `ConfirmByName` over Mud, with the focus-visible ring, roles, `Ripple=false`, `Variant.Outlined`, no uppercase and `Dense` from `alvo.density`.
6. **DataGrid over `IAlvoData` keyset paging.** `ServerData` with Prev/Next, `j`/`k` row cursor integration, and row-click → editor latency (spike baseline: ~50 ms per click at 50 rows [S]).
7. **`MudSplitPanel` vs `SplitHandle`.** Keyboard resizing, persistence of size, and phone fallback.
8. **e2e locator port.** Rewrite `SignInScenarios` and the one flow `scripts/test-admin-e2e` covers against one migrated screen. This checks the §5.1 gotchas: popover scoping, and waiting for circuit readiness.

### 8.2 Recommended migration order
1. **Foundation, invisible to users:** package, layered CSS, providers in `AdminLayout`, theme and dark-mode wiring, wrapper components. Land the Alvo focus-ring CSS first.
2. **Shell:** `MudLayout`/`AppBar`/`Drawer`/`NavMenu`, `ThemeToggle`, `PendingBar`, `SignedInAs`. Put the `CommandPalette` on `MudDialog`. Every screen benefits, and it proves providers, theming and layering.
3. **The overlay primitive:** replace `Sheet` with `AlvoEditor` (right dialog) and `ConfirmDialog`, and add the unsaved-changes guard. This one change fixes defects #5, #8, #9 and #11 across screens.
4. **Data screens:** DataList, then EntityData (grid + record editor). Highest value, and it exercises the grid, autocomplete (`RefPicker`), forms and snackbars.
5. **Schema:** SchemaList (new-entity dialog), then Entity (tabs, field editor, rules with explicit save, index/hook dialogs), then Preview (apply + `ConfirmByName`), then Transfer.
6. **Access:** person editor dialog, token-in-dialog, confirm on disable.
7. **Split-pane screens:** History (plus diff toggle) and Rules, on `MudSplitPanel`.
8. **Assistant drawer:** Ctrl/Cmd+Enter, scroll-to-latest, clear-on-send.
9. **Read-only screens:** Overview, Welcome, Settings, Integrations, NotYet.
10. **Sign-in last:** it stays native markup, and only picks up Mud CSS variables.
11. **Remove** the replaced `DesignSystem/*` primitives and their `alvo.css` rules, then retire the `mud` layer trick if nothing old remains. Keep the layer if Alvo keeps any CSS of its own (inference).

### 8.3 Fluent UI Blazor v5 vs MudBlazor 9 on the questions that decided the Fluent study

| Question | Fluent v5 (RC5) | MudBlazor 9.10 |
|---|---|---|
| Stable for net10.0 | No (RC; `TreatWarningsAsErrors` discouraged) | **Yes**; 0 warnings under Alvo settings [S] |
| Playwright `GetByRole` on buttons/checkbox/switch | **Fails** (ElementInternals) | **Works** [S] |
| Static SSR inputs accessible | No (shadow input unnamed) | Yes, native inputs, but visuals break; use native markup |
| Resizable pane | `FluentMultiSplitter` "not accessible yet" | `MudSplitPanel` with separator role and keyboard [V] |
| Autocomplete a11y | "not yet implemented" | Partial (combobox role, no activedescendant) [S] |
| Visible focus indicator | Fluent-provided | **Missing** on buttons [S, #11735]; Alvo must add it |
| CSS coexistence | Scoped tokens, `default-fuib.css` body overrides | Unlayered global reset; **layer it below Alvo** |
| Dark mode without a flash | JS initializer reads localStorage | Two scoped providers + `alvo.js` [S] |
| First-load size (brotli) | ~85 KB JS + ~13 KB CSS | ~15 KB JS + ~43 KB CSS |
