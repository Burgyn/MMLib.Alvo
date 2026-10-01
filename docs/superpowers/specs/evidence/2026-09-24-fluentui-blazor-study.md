# Fluent UI Blazor for the Alvo admin dashboard: research notes

Researched 2026-09-24. Sources were: NuGet package metadata and the unpacked `.nupkg` files; a shallow clone of `github.com/microsoft/fluentui-blazor` at `dev` (commit `1b9be26`, 2026-09-23) and at `archive-v4`; Microsoft Learn; fluent2.microsoft.design; and a Playwright 1.56.1 probe I ran myself against the v5 RC5 JS bundle (section 4).

Legend: **[V]** means I checked it in a primary source or by experiment. **[U]** means I could not verify it. Where a claim is my own inference, it says so.

Clone paths below are relative to the `fluentui-blazor` repo root. The docs live in `examples/Demo/FluentUI.Demo.Client/Documentation/` (called `Docs/` below). The v5 demo site (https://v5.fluentui-blazor.net) is a WASM single-page app that WebFetch cannot render, so every docs citation points to the markdown source it is built from. Route names such as `/MigrationV5` come from that source's front matter.

---

## 0. Decision in one paragraph

Use **v5**: `Microsoft.FluentUI.AspNetCore.Components` **5.0.0-rc.5-26219.1**, published 2026-08-09, plus the matching `...Components.Icons` 5.0.0-rc.5-26219.1. Pin the exact version. Do not use v4, even though v4 has a stable 4.14.4 (2026-07-30). The `archive-v4` README now says: *"Version 4 is no longer supported and will receive security updates only. No new features or non-security bug fixes will be released for v4."* [V: `archive-v4/README.md` lines 34-36, commit 2026-09-18].

v4→v5 is a near-total API break: dialogs, lists, layout, nav, text fields and theming all change. Building on v4 now would mean doing the rebuild twice. v5 is still an RC, but the maintainers have called the API surface "stable" since RC3 [V: https://dvoituron.com/2026/05/19/fluentui-blazor-5-rc3/ ; https://dvoituron.com/2026/06/26/fluentui-blazor-5-rc4/]. No GA date is announced [U].

---

## 1. Version, license, install and render modes

### 1.1 Packages and versions [V: NuGet flat-container and registration APIs]

| Package | Latest stable | Latest pre-release | Notes |
|---|---|---|---|
| `Microsoft.FluentUI.AspNetCore.Components` | 4.14.4 (2026-07-30) | **5.0.0-rc.5-26219.1 (2026-08-09)** | v5 RCs: rc.1-26048.1, rc.2-26098.1, rc.3-26138.1, rc.4-26177.1 / 26180.1, rc.5-26219.1 |
| `Microsoft.FluentUI.AspNetCore.Components.Icons` | 4.14.4 | 5.0.0-rc.5-26219.1 | lib is `net9.0` only, so it runs on net10.0 |
| `...Components.DataGrid.EntityFrameworkAdapter` / `...ODataAdapter` | – | – | Optional. We should not need them, because Alvo pages through `IAlvoData`, not EF [V: `Docs/Components/DataGrid/FluentDataGrid.md`] |

- **Target frameworks** (v5 RC5 nuspec): `net8.0`, `net9.0`, `net10.0`. The net10.0 group depends on `Microsoft.AspNetCore.Components.Web` 10.0.10, plus `Microsoft.Extensions.{Configuration.Abstractions, Hosting.Abstractions, Http}` 10.0.10 [V: nuspec].
  - **Alvo impact:** `MMLib.Alvo.Admin.csproj` uses a `FrameworkReference Microsoft.AspNetCore.App` *instead of* the Components.Web package, to avoid NU1510. A transitive package dependency on `Components.Web` 10.0.10 arrives anyway. Whether it triggers NU1510/pruning warnings under `TreatWarningsAsErrors` was not tested [U]. Check with a `dotnet build` spike.
- **License:** MIT [V: nuspec `<license type="expression">MIT</license>`; `LICENSE.TXT`].
  - The JS bundle also embeds `@fluentui/web-components` ^3.1.3, `tabbable`, `imask` and `sortablejs` [V: `src/Core.Scripts/package.json`]. All four are MIT per their npm pages [U: not checked individually]. `THIRD-PARTY-NOTICES.TXT` in the repo covers the vendored parts.
- **Pre-release warning.** v5 Installation says: *"With the pre-release version, make sure you don't have the configuration `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in your csproj file."* [V: `Docs/GetStarted/Installation.md`]. Alvo sets `TreatWarningsAsErrors=true` in `Directory.Build.props:9`.
  - The v5 source carries ~20 `[Obsolete]` members kept as migration shims [V: grep of `src/Core`]. Referencing any of them becomes an error, which is the likely reason for the warning.
  - Plan for a spike build. Do not relax the global setting. Only suppress specific IDs in the Admin csproj if one fires.
- **NU5104.** A stable package that depends on a pre-release fails pack with NU5104 under warnings-as-errors [V: https://learn.microsoft.com/nuget/reference/errors-and-warnings/nu5104].
  - Alvo has no git tags yet, so MinVer versions are pre-release today and this is not a blocker.
  - **It becomes one the moment `MMLib.Alvo.Admin` wants a stable `v1.0.0` while Fluent v5 is still RC.** (Inference from `Directory.Build.props` + MinVer config.)

### 1.2 Breaking differences v4 → v5 that matter to us [V: `Docs/GetStarted/Migration/*.md`, route `/Migration`]

The migration index rates FluentDialog, FluentSelect and FluentList (Select/Combobox/Listbox) as **Critical**. Layout, Menu, MenuButton, Overflow, Popover, Splitter, Tabs, Badge and Combobox are rated **High**.

| v4 | v5 | Source file |
|---|---|---|
| `FluentDesignTheme`, `FluentDesignSystemProvider`, `DesignThemeModes`, `OfficeColor` | **Removed.** CSS custom properties + `ThemeService` | `MigrationFluentDesignTheme.md` |
| `FluentMainLayout`, `FluentHeader`, `FluentBodyContent`, `FluentFooter` | **Removed.** `FluentLayout` + `FluentLayoutItem Area=…` + `FluentLayoutHamburger` | `MigrationFluentLayout.md` |
| `FluentNavMenu`, `FluentNavGroup`, `FluentNavLink` | `FluentNav`, `FluentNavCategory`, `FluentNavItem`, `FluentNavSectionHeader`. **Only one nesting level.** | `MigrationFluentNavMenu.md`, `Components/Nav/FluentNav.md` |
| `FluentTextField`, `FluentSearch` | `FluentTextInput` (search = `StartTemplate` with a search icon) | `MigrationFluentTextField.md` |
| `FluentNumberField` | `FluentNumberInput` (generic `Min`/`Max`/`Step`, culture-aware) | `MigrationFluentNumberField.md` |
| `FluentProgressRing` / `FluentProgress` | `FluentSpinner` / `FluentProgressBar` | `MigrationIndex.md` |
| `FluentSplitter` | **Removed.** `FluentMultiSplitter` + `FluentMultiSplitterPane` | `MigrationFluentSplitter.md` |
| `DialogParameters`, `IDialogContentComponent<T>`, `FluentDialogHeader/Footer`, `DialogType.Panel` | `DialogOptions` (`Header`, `Footer`, `Alignment`), `FluentDialogInstance` base class, `FluentDialogBody`, `ShowDrawerAsync` | `MigrationFluentDialog.md` |
| `IToastService`, `CommunicationToast`/`ConfirmationToast`/`ProgressToast` | **`INotificationService`** (toasts *and* message bars), `ShowSuccessToastAsync`… | `MigrationFluentToast.md`, `Components/Toast/FluentToast.md`; RC4 renamed `IMessageBarService` → `INotificationService` (dvoituron RC4 post) |
| `FluentValidationMessage<T>`, `FluentEditForm` | `FluentField` (Label/Message/MessageCondition on every input) + plain `EditForm` | `MigrationGeneral.md`, `MigrationRemovedComponents.md` |
| `FluentToolbar`, `FluentProfileMenu`, `FluentBreadcrumb`, `FluentWizard` (v4 flavour), `FluentCollapsibleRegion`, `FluentAccessibility` | Removed (use `FluentStack`, `FluentPopover` + `FluentAvatar`, …) | `MigrationRemovedComponents.md`, `MigrationIndex.md` |
| Enum names `Color.Neutral`/`Accent` | `Default`/`Primary` | `MigrationColor.md` |
| Providers: `<FluentToastProvider/> <FluentDialogProvider/> <FluentTooltipProvider/> <FluentMessageBarProvider/> <FluentMenuProvider/>` (v4 README) | One `<FluentProviders />` | `MigrationGeneral.md`, `Installation.md` |
| Scoped-CSS isolation; `::deep` needed | Library ships with `ScopedCssEnabled=false`, so **`::deep` is useless** | `MigrationGeneral.md` |

There is also an MCP server with migration tools [V: `src/Tools/McpServer/Tools/MigrationTools.cs`]. It does not matter to us, because we are rebuilding rather than migrating.

### 1.3 Adding it to a Razor class library (our case)

Install steps [V: `Docs/GetStarted/Installation.md`; repo `README.md`]:

1. **Package.** `PackageReference` / `PackageVersion` for `Microsoft.FluentUI.AspNetCore.Components`, and `...Icons` if we use icons.
2. **`_Imports.razor`.** `@using Microsoft.FluentUI.AspNetCore.Components` and `@using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons`.
3. **CSS.** Put the following in the host document's `<head>`, which in our case is the RCL's root component: `<link href="_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.bundle.scp.css" rel="stylesheet" />`
4. **Services.** `builder.Services.AddFluentUIComponents(config => …)`.
   - The optional config lambda holds global `DefaultValues` (e.g. `config.DefaultValues.For<FluentButton>().Set(p => p.Appearance, …)`) and `config.Toast.*` / `config.Tooltip.*` [V: `Docs/General/DefaultValues.md`].
   - **RCL design point:** the host must call this. Alvo's own `AddAlvoAdmin()`-style extension (whatever the host already calls) should call `AddFluentUIComponents` *inside*, so embedded hosts do not need to know about Fluent. (Inference.)
5. **Providers.** `<FluentProviders />` at the end of the top-level layout. It hosts dialogs, tooltips, toasts and message bars [V: Installation.md §6].
   - A `FluentMessageBarProvider Section="…"` is needed wherever `INotificationService` message bars should appear. The docs say a message sent to a section with no provider is **not visible** [V: `Components/MessageBar/FluentMessageBar.md`].
6. **JS.** No `<script>` tag is needed. The package ships `Microsoft.FluentUI.AspNetCore.Components.lib.module.js`. Blazor discovers `{PackageId}.lib.module.js` in an RCL as a **JS initializer** and imports it automatically [V: https://learn.microsoft.com/aspnet/core/blazor/fundamentals/startup?view=aspnetcore-10.0#javascript-initializers].
   - Its `beforeWebStart` initialises the theme (to reduce FOUC) and defines all web components. `afterWebStarted` applies the default styles, wires the static-mode hamburger, registers custom events (dialog toggle, menu item, tabs, text input…) and re-applies styles on `enhancedload` [V: `src/Core.Scripts/src/Startup.ts`].
   - Per-component collocated modules (`FluentDataGrid.razor.js`, `FluentKeyCode.razor.js`, `FluentNav.razor.js`, …) are fingerprinted static-web-asset endpoints loaded on demand [V: `build/Microsoft.AspNetCore.StaticWebAssetEndpoints.props` in the nupkg].
7. **Default styles.** At start-up the library *fetches* `_content/.../css/default-fuib.css` and adopts it as a constructed stylesheet.
   - That stylesheet sets `body { height: 100dvh; overflow: hidden; font-family: var(--fontFamilyBase); color/background from tokens }`.
   - Opt out with a `no-fuib-style` attribute on `<body>`. Opt in to reboot with `use-reboot` [V: `staticwebassets/css/default-fuib.css` header; bundle strings `no-fuib-style`, `use-reboot`, `fetch(…default-fuib.css)`].
   - **Alvo must decide this deliberately:** `overflow:hidden` on body changes page scrolling.

### 1.4 Static SSR vs interactive server

- **Official stance.** "Fluent UI Blazor requires interactive rendering… especially if components like menus or dialogs are not appearing" [V: `Installation.md` §7]. Under static SSR, "most components will display correctly but will not offer complete, if any, functionality" [V: `Docs/HomeAside.md`].
- **What still works under static SSR** (our sign-in page):
  - The JS initializer runs in a Blazor Web App on static pages too (`beforeWebStart`/`afterWebStarted` are web-level, not circuit-level) [V: Learn startup page; `Startup.ts`]. Web components are therefore defined and styled.
  - `fluent-text-input` and `fluent-button` are **form-associated** custom elements (ElementInternals). A plain `<form method="post">` / `EditForm` with `FormName` submits their values, and pressing **Enter** in a text input triggers implicit submission via the form's submit button [V: bundle has `implicitSubmit(){… o=e.find(n=>n.getAttribute("type")==="submit"); if(o){o.click()…}`; my Playwright probe (section 4) confirmed that Enter submits the form].
  - For SSR form binding, **`Name` must be set manually**: *"⚠️ This value needs to be set manually for SSR scenarios to work correctly."* [V: `src/Core/Components/Base/FluentInputBase.cs` ~L221].
  - `FluentLayout`'s hamburger works in static mode. `OnBreakpointEnter` and `NavigationDeferredLoading` are interactive-only [V: `Components/Layout/FluentLayout.md`].
- **What breaks under static SSR:**
  - Anything driven by C# events or `OnAfterRenderAsync`: dialogs/drawers via `IDialogService`, toasts, `FluentMultiSplitter` ("requires an Interactive mode… does not function correctly in static rendering mode" [V: `Components/Splitter/MultiSplitter.md`]), `FluentKeyCode`, DataGrid sorting/paging.
  - **Accessibility gap:** `FluentField` pushes its label as `aria-label` into the text input's shadow `.control` **from `OnAfterRenderAsync`** [V: `src/Core/Components/Field/FluentField.razor.cs` L208-231]. `OnAfterRenderAsync` never runs in static SSR. On the static sign-in page, the inner `<input>` therefore has **no accessible name**.
    - My probe reproduced this: Chromium's AX tree reported `textbox:""` for a `<label for>` + `fluent-text-input`, and even for `aria-label` placed on the host.
    - **Recommendation:** keep the static sign-in form on native `<input>` elements styled with Fluent tokens, or accept the gap. (Inference.)
- **Prerender + interactive server.** The theme is applied in `beforeWebStart` from localStorage, so it runs before the circuit. The docs still warn about a dark-mode white flash and suggest an inline `<style>@media (prefers-color-scheme: dark){body{background:#292929}}</style>` [V: `Docs/General/Theme/Dark/ThemeLightDark.md`].
  - Alvo's existing pre-paint theme script covers this already.
  - That inline `<style>` would need a CSP nonce/hash if we ever ship a CSP (see §5).
- **Enhanced navigation.** The library re-applies styles and the layout on `enhancedload` [V: `Startup.ts`]. RC3/RC4 list "fixes for server-side rendering / Blazor SSR with enhanced navigation" [V: dvoituron RC3/RC4 posts].

---

## 2. Theming while keeping Alvo's identity

### 2.1 The v5 model [V: `Docs/General/Theme/Themes.md`, `ThemeLightDark.md`, `Designer/ThemeDesigner.md`, repo `docs/Theme-API.md`, `src/Core/Components/Theme/Services/IThemeService.cs`]

- Theming is **CSS-custom-property based**: Fluent 2 tokens such as `--colorBrandBackground`, `--colorNeutralBackground1`, `--fontFamilyBase`, `--borderRadiusMedium` and `--spacingHorizontalM`.
- `FluentDesignTheme` no longer exists.
- **Brand colour.** One brand colour generates a 16-step accessible ramp. The algorithm matches the Fluent Theme Designer's.
  - "Exact" mode forces the exact hex into `--colorBrandBackground` / `--colorCompoundBrandBackground`.
  - The docs warn that exact mode may break contrast for some steps.
- **Declarative inputs:** `<body data-theme="light|dark|system">` and `<body data-theme-color="#RRGGBB">`. Declarative values are not persisted.
- **Code API:** the `ThemeService` / `IThemeService` injectable. Members:
  - `SetThemeAsync(ThemeMode)`, `SetThemeAsync(string color, bool isExact)`, `SetThemeAsync(ThemeSettings)`, `SetThemeAsync(Theme)`
  - `CreateCustomThemeAsync(ThemeSettings)`, which returns a `Theme` you can modify (radius, font family, line height, colours…) before applying it
  - `SwitchThemeAsync()`, `IsDarkModeAsync()`, `IsSystemDarkAsync()`, `ClearStoredThemeSettingsAsync()`, `GetColorRampAsync()`
- **Persistence.** `SetThemeAsync(...)` persists to **`localStorage["fluentui-blazor:theme-settings"]`** as `{theme, base, dir, color, hue, vibrancy, exact}`. The exception is `SetThemeAsync(Theme)`, which is not cached.
- **Dark-mode attribute.** Dark mode is expressed as **`body[data-theme="dark"]`**. The attribute is *removed* for light. A `themeChanged` DOM event fires on `document.body`, and system changes are followed automatically.
- **How tokens land** [V: bundle]. `setTheme` writes the tokens as `html { --token: value; … }` into a **document-adopted constructed stylesheet**. Scoped themes use `@scope ([data-fluent-theme=…])` or `:host` inside shadow roots.
  - Adopted sheets cascade after document sheets, so an author override must win on specificity.
  - `:root { … }` (0,1,0) beats `html { … }` (0,0,1). That is why the migration guide's override example uses `:root` [V: `MigrationFluentDesignTheme.md`].
- **Density.** There is no global density token or switch [U: none found in docs or source]. Density is per component: `Size` enums (`TextInputSize`, `ButtonSize.Small`, …), `DataGridRowSize` and `RowSize`. Set them globally through `config.DefaultValues.ForAny<…>()` [V: `DefaultValues.md`, DataGrid doc "Row size"].

### 2.2 Mapping Alvo's identity onto it (recommendation, partly inference)

Alvo today [V: `src/MMLib.Alvo.Admin/wwwroot/alvo.css` L140-236, `wwwroot/alvo.js` L11-50]:
- `:root { color-scheme: light dark; --accent: light-dark(#0f7a48, #39e991); --accentText…; --bg; --panel; --text; --dim; --ok-*/--warn-*/--danger-* … }`
- Overrides via `:root[data-theme='light'|'dark']` on **`<html>`**.
- The toggle stores the choice in `localStorage['alvo.theme']`.

1. **One source of truth for mode.** Keep Alvo's toggle and `alvo.theme`. When it runs, also call `ThemeService.SetThemeAsync(ThemeMode.Light|Dark|System)`, or set `body[data-theme]` on the static page.
   - This leaves two stores (`alvo.theme` and `fluentui-blazor:theme-settings`), which can drift.
   - Simplest fix: let Alvo's pre-paint script write *both* `html[data-theme]` and `body[data-theme]` from `alvo.theme`, and call `ClearStoredThemeSettingsAsync()` once so Fluent's own store stays empty. [U: interplay not tested.]
   - Fluent reads `body[data-theme]`; Alvo uses `html[data-theme]`. These are different elements, so there is no collision.
2. **Brand.**
   - **Option A:** `SetThemeAsync("#0f7a48", isExact: true)` in light, and the dark-mode equivalent via `ThemeSettings`, so Fluent generates the full ramp.
   - **Option B:** keep Alvo's hand-tuned pair and override on `:root`: `--colorBrandBackground: var(--accent); --colorBrandBackgroundHover…; --colorNeutralForegroundOnBrand: var(--accentText); --colorBrandForeground1…; --colorStrokeFocus2…`.
   - Option B keeps the D6 AA decision (#0f7a48 at 5.39:1) exactly, but requires mapping ~20 brand tokens by hand. Option A guarantees ramp consistency but may shift the hover/pressed shades.
   - **Recommended:** A with `isExact: true`, then check the contrast of the generated ramp. [U: which exact tokens the ramp maps to, e.g. `--colorBrandBackground` vs `--colorCompoundBrandBackground` per mode, beyond what `Themes.md` says.]
3. **Neutral surfaces.** Map `--bg`→`--colorNeutralBackground2`, `--panel`→`--colorNeutralBackground1`, `--border`→`--colorNeutralStroke2`, `--text`→`--colorNeutralForeground1`, `--dim`→`--colorNeutralForeground2`.
   - Alternatively, invert it: define Alvo's own vars *in terms of* Fluent tokens, and delete most of `alvo.css`.
   - Inverting is the lower-maintenance direction, because Fluent's components read only Fluent tokens.
4. **Semantic colours.** Fluent has `--colorStatusSuccess*`, `--colorStatusWarning*` and `--colorStatusDanger*` [U: exact names not enumerated here; see `src/Core.Scripts/_ExtractCssVariables.ps1` output / `StylesVariables` C# class].
5. **Fonts.** Override `:root { --fontFamilyBase: 'Public Sans', …; --fontFamilyMonospace: 'IBM Plex Mono', …; }`. The defaults are the Segoe UI stack and Consolas [V: bundle strings `fontFamilyBase`, `fontFamilyMonospace`].
   - Keep Alvo's self-hosted `@font-face` rules. The library loads no web fonts itself: no `@import` or remote `url()` in its CSS [V: grep of shipped CSS].
   - Alternatively, set `fontFamilyBase` via `CreateCustomThemeAsync` → `Theme` → `SetThemeAsync(Theme)` [V: `ThemeDesigner.md` "Create and alter a Theme"].
6. **Radius and spacing.** `StylesVariables.Borders.Radius.*`, `Margin`/`Padding` helpers, and `--spacing*` tokens [V: `MigrationFluentDesignTheme.md`, `Docs/General/Spacing/Spacing.md`].

---

## 3. Components we need: v5 names, parameters, keyboard and a11y

All v5 type names below were checked against the RC5 `Microsoft.FluentUI.AspNetCore.Components.xml` [V]. Where the v4 name differs, it is in brackets.

### App shell
- **`FluentLayout` + `FluentLayoutItem Area="LayoutArea.Header|Navigation|Content|Aside|Footer"` + `FluentLayoutHamburger`** (v4 `FluentMainLayout`/`FluentHeader`/`FluentBodyContent`: removed).
  - It is a CSS-grid layout. The mobile breakpoint is `MobileBreakdownWidth` (768px default), evaluated with an `@container` query on the layout's own width.
  - Parameters: `Sticky` panels, `GlobalScrollbar`, and CSS vars `--layout-header-height` (44px) / `--layout-footer-height` (36px).
  - Known TODO in the docs: with `GlobalScrollbar="true"` "a problem persists with the fixed footer" [V: `Components/Layout/FluentLayout.md`].
- **`FluentNav` / `FluentNavCategory` / `FluentNavItem` / `FluentNavSectionHeader`** (v4 `FluentNavMenu`).
  - Supports one nesting level and **no icon-only (collapsed rail) layout**.
  - `UseSingleExpanded`; `FluentNavItem` takes `Href` or `OnClick`, plus `IconRest`/`IconActive` and `Match`.
  - Keyboard: ↑/↓/Home/End move between items; Enter/Space expands a category. It uses a **roving tabindex** and remembers the last focused item [V: `Components/Nav/FluentNav.md`].
  - For a collapsible icon rail, use **`FluentAppBar`/`FluentAppBarItem`**, which exists in v5 [V: XML]. Its keyboard behaviour is [U].

### Overlays
- **`IDialogService` + `FluentDialogInstance` (base class) + `FluentDialogBody`** (v4 `IDialogContentComponent<T>`, `DialogParameters`).
  - `ShowDialogAsync<TDialog>(Action<DialogOptions>)` and `ShowDrawerAsync<TDialog>(…)` both return `DialogResult` (`Cancelled`, data).
  - Message-box helpers: `ShowConfirmationAsync(message, title, primary, secondary)`, `ShowSuccess/Warning/Error/InfoAsync`, `ShowMessageBoxAsync(MessageBoxOptions)` [V: `src/Core/Components/Dialog/Services/IDialogService.cs`, `MessageBox/DialogService.cs`].
  - `DialogOptions`: `Header.Title`, `Header.CloseAction.Visible`, `Header.AddAction(...)`, `Footer.PrimaryAction`/`SecondaryAction` (`Label`, `Disabled`, `ShortCut`, `OnClickAsync`), `Alignment` (`Default` / `Start` / `End`), `Modal` (default true), `Size`, `Width`/`Height`, `PreventDismissOnEscape`, `OnStateChange` (`DialogState` Opening/Open/Closing/Closed), `Parameters.Add(nameof(X), value)`.
  - `FluentDialogBody FixedHeaderFooter="true"` pins the title and actions while the content scrolls, which is what long editor forms need [V: `Components/Dialog/FluentDialog.md`, `MigrationFluentDialog.md`].
- **Drawer / panel.** `ShowDrawerAsync` renders `<fluent-drawer>`. The default `Alignment` is `End` (right).
  - A modal drawer closes on an outside click (= Cancel). A non-modal drawer keeps the page interactive and closes only via its actions [V: `Components/Dialog/FluentDrawer.md`].
  - This is the side-panel editor surface we need. There is no persistent inline docked drawer: use a `FluentLayoutItem Area="Aside"` or `FluentMultiSplitter` for that. (Inference; Fluent 2 has "inline drawer", but no v5 Blazor inline-drawer API found [U].)
- **Focus and Escape** [V: source + bundle]:
  - Dialogs render a native `<dialog>` inside `fluent-dialog`'s shadow root [V: bundle `createElement("dialog")`], so the browser's modal focus containment and Esc-to-close apply.
  - `PreventDismissOnEscape` opts out.
  - **Default shortcuts:** Enter = primary, Esc = secondary. They fire only when focus is on the dialog surface itself or in the action/footer/close slots, **not while typing in a field**. So Enter in a text input inside a dialog does not submit the dialog [V: `Core.Scripts/src/Components/Dialog/FluentDialog.ts` `ShouldHandleShortcut` L100-118].
  - Custom `ActionTemplate` buttons disable the default shortcuts [V: `FluentDialog.md`].
  - `DialogOptionsFooterAction.ShortCut` accepts `"Ctrl+Enter"`, `"Escape;Enter"`, … [V: `DialogOptionsFooterAction.cs` L37-47]. The matcher builds only `Ctrl+`/`Alt+`/`Shift+` prefixes, so **Cmd (Meta) combos cannot be expressed** [V: `FluentDialog.razor.cs` L226].
  - Focus restore to the trigger on close relies on native `<dialog>` behaviour [U: not separately tested].
  - Docs "Don't": do not open a dialog from a dialog; do not use more than 3 footer buttons; do not open a dialog with no focusable elements. "Only one global overlay can be displayed at a time" (RC3) [V].
- **Toasts: `INotificationService` + `FluentToast`/`FluentToastProvider`** (v4 `IToastService`).
  - Helpers: `ShowSuccessToastAsync(title, …, lifetime)`, `ShowProgressToastAsync(...)` (returns `ToastResult` with `.Instance.CloseAsync()`), `ShowToastAsync<TComponent>(…)`.
  - `ToastOptions`: `Intent`, `Lifetime`, `ResultTiming` (`Queued` / `Visible` / `Closed`), quick actions.
  - Defaults: `MaxToastCount=4`, 7 s lifetime, `BottomEnd`, pause on hover and window blur, `AllowDismiss=true`. A toast with a quick action and no explicit lifetime **never auto-closes**.
  - A11y: `role=alert` plus a live region whose politeness follows the intent.
  - Toasts without actions **do not receive keyboard focus** [V: `Components/Toast/FluentToast.md`].
  - The doc contradicts itself on the default `ResultTiming` ("Queued" in one place, "Closed" in another) [V: same file]. Always pass `ResultTiming` explicitly.
- **`FluentMessageBar` + `FluentMessageBarProvider Section="…"`**, via `INotificationService.ShowSuccessBarAsync/ShowWarningBarAsync/ShowErrorBarAsync/ShowInfoBarAsync(section, title, message)` or `ShowMessageAsync(Action<MessageBarOptions>)`.
  - Parameters: `Intent`, `Layout` (`SingleLine`/`MultiLine`/`Notification`), `Shape`, `AriaLive`, `ActionsTemplate`, `TimeStamp`.
  - `Title` is a plain string; put markup in `ChildContent` [V: `Components/MessageBar/FluentMessageBar.md`, `MigrationFluentMessageBar.md`].
- **`FluentMenu` / `FluentMenuList` / `FluentMenuItem` / `FluentMenuButton` / `FluentSplitButton`**. RC5 added `RenderWhen` for deferred rendering [V: XML; dvoituron RC5 post].
  - Docs "Don't render focusable or clickable items inside menu items" [V: `Components/Menu/FluentMenu.md`].
  - Arrow-key/Esc behaviour follows the Fluent web-components v3 menu [U: not individually verified].
- **`FluentPopover`, `FluentTooltip`.** Tooltips go through the provider by default (`config.Tooltip.UseServiceProvider`) [V: `DefaultValues.md`].

### Data
- **`FluentDataGrid<TGridItem>`**, plus `PropertyColumn`/`TemplateColumn`/`SelectColumn`, `FluentPaginator` and `PaginationState`.
  - Parameters [V: `src/Core/Components/DataGrid/FluentDataGrid.razor.cs`]:
    - Data: `Items` (IQueryable) or `ItemsProvider` (`GridItemsProvider<T>`, which returns items + `totalItemCount` for remote paging); `Pagination`; `ItemKey`
    - Virtualization: `Virtualize`, `ItemSize`, `OverscanCount`, `InitialItemIndex`, `AnchorMode`
    - Row events: `OnRowClick`, `OnRowDoubleClick`, `OnRowFocus`, `OnCellClick`, `OnCellFocus`
    - Sorting: `SortMode` (Single/Multiple), `OnSortChanged`, `HeaderCellAsButtonWithMenu`
    - Columns: `ResizableColumns`, `ReorderableColumns`, `ColumnOrder`, `AutoFit`, `GridTemplateColumns`
    - States: `Loading`, `LoadingContent`, `EmptyContent`, `ErrorContent`, `HandleLoadingError`
    - Rows and layout: `RowClass`, `RowStyle`, `ShowHover`, `StripedRows`, `DisplayMode` (Grid/Table), `RowSize`, `MultiLine`, `RowDetails` (master/detail, RC5), `SaveStateInUrl`, `AutoFocus`
  - Keyboard [V: `Components/DataGrid/FluentDataGrid.md` "Accessibility"]:
    - Arrow keys move between cells. Tab reaches a header's sort and options buttons; Enter toggles sort; Shift+S clears sort.
    - Shift+Enter adds a column to a multi-sort. +/- resize a column; Shift+R resets widths.
    - Enter toggles row selection when there is a `SelectColumn` with `SelectFromEntireRow`. Esc closes the options popover.
  - With `Virtualize`, the docs "highly recommend" `DisplayMode="Table"`, because Grid mode "can exhibit odd scrolling behavior". `ItemSize` is mandatory, and `RowDetails` is incompatible with it [V: same file + `Pages/DataGridVirtualizePage.md`].
- **`FluentTabs` / `FluentTab`** (bind `ActiveTabId`/`ActiveTab`) [V: `Components/Tabs/FluentTabs.md`]. My probe showed the `tab` roles are exposed to Playwright (section 4).
- **`FluentBadge`, `FluentCounterBadge`, `FluentPresenceBadge`.** Badge migration impact is "High" [V: `MigrationIndex.md`].
- **`FluentCard`** [V: XML]. Its semantics are [U].
- **`FluentSkeleton`** (docs: add `aria-busy` to the loading container), **`FluentSpinner`** (v4 ProgressRing; needs a label for screen readers) and **`FluentProgressBar`** [V: `Components/Skeleton/FluentSkeleton.md`, `Components/Progress/FluentSpinner.md`].
- **`FluentMultiSplitter` / `FluentMultiSplitterPane`** (`Size`, `Min`, `Max`, `Resizable`, `Collapsible`, `OnResize`, `OnCollapse`/`OnExpand`).
  - It is interactive-only, and its **docs say "This component is not accessible yet."** [V: `Components/Splitter/MultiSplitter.md`]. That is a problem for a resizable editor pane, because Alvo's current `SplitHandle` presumably has keyboard resizing.
- **`FluentOverflow`.** RC5 made breaking changes: `MoreButtonTemplate`→`MoreTemplate`, and `FluentOverflowItem` was removed in favour of attributes on children [V: dvoituron RC5 post; `MigrationFluentOverflow.md`].
- **`FluentKeyCode`** (element-scoped `OnKeyDown` with full key info, `PreventMultipleKeyDown`), plus **`FluentKeyCodeProvider` + `IKeyCodeService`** for page-global shortcuts (`RegisterListener`; `PreventDefault` on the provider) [V: `Components/KeyCode/FluentKeyCode.md`, `MigrationFluentKeyCode.md`].
- **`FluentIcon`** with `Icons.Regular.Size20.X`, `.WithColor()`, `Focusable`, and custom SVG `Icon` subclasses. Icons render as inline SVG [V: `Components/Icon/FluentIcon.md`].

### Inputs (all implement `IFluentField`: `Label`, `LabelTemplate`, `Required`, `Message`, `MessageCondition`, `MessageState`)
- **`FluentTextInput`** (v4 TextField/Search): `TextInputType`, `Appearance` (`TextInputAppearance`), `Size`, `StartTemplate`/`EndTemplate`, `Immediate`/`ImmediateDelay`, **`ChangeAfterKeyPress`** (e.g. `[KeyPress.For(KeyCode.Enter)]`), masks, `Name` (needed for SSR).
- **`FluentTextArea`.** Same key API; e.g. `ChangeAfterKeyPress="@([KeyPress.For(KeyCode.Enter).AndCtrlKey()])"` + `OnChangeAfterKeyPress` [V: `Components/TextArea/FluentTextArea.md`].
  - `KeyPress` also has `AndMetaKey()`, so Cmd+Enter on macOS works for the chat input [V: `src/Core/Components/KeyCode/KeyPress.cs` L99].
  - Whether the triggering Enter is `preventDefault`-ed (i.e. no stray newline) is [U].
  - My probe: plain Enter inside `fluent-textarea` does **not** submit the enclosing form [V: probe].
- **`FluentNumberInput`**, **`FluentSelect`**, **`FluentCombobox`**, **`FluentListbox`**, **`FluentOption`**, **`FluentAutocomplete<TOption,TSelected>`**, **`FluentSwitch`**, **`FluentCheckbox`**, **`FluentRadioGroup`/`FluentRadio`**, **`FluentSlider`**, **`FluentDatePicker`/`FluentTimePicker`** [V: XML].
  - **`FluentAutocomplete` docs: "Accessibility requirements are not yet implemented for this component."** Keyboard: ↓/↑ open and navigate, Enter selects, Esc closes [V: `Components/List/Autocomplete/FluentAutocomplete.md`]. It is a poor fit for Alvo's `RefPicker` until that is fixed.
  - Combobox: the list stays open until dismissed by an outside click or Esc [V: `Components/List/Combobox/FluentCombobox.md`].
- **Empty states.** There is no dedicated component [V: absent from XML type list]. Compose `FluentStack` + `FluentIcon` + `FluentText` + `FluentButton`. `FluentDataGrid.EmptyContent` covers the grid case.
- **`FluentText`** (typography, replaces the v4 `FluentLabel` typo role), **`FluentStack`**, **`FluentDivider`**, **`FluentLink`/`FluentAnchorButton`**, **`FluentErrorBoundary`**, **`FluentWizard`**, **`FluentTreeView`**, **`FluentAccordion`** [V: XML].

---

## 4. Testing

### 4.1 Playwright: an experiment I ran [V]

Setup:
- Playwright **1.56.1** (Node) with Chromium 1194. This is the same line as Alvo's pinned `Microsoft.Playwright` 1.56.0 [V: `Directory.Packages.props:86`].
- A static page that imports the RC5 `lib.module.js` and calls `beforeWebStart({})`.
- Markup mirroring what `FluentTextInput`/`FluentField`/`FluentButton` render: `<fluent-field><label slot="label" for="name">…` + `<fluent-text-input slot="input" id="name">`, `<fluent-button type="submit">Save changes</fluent-button>`, `fluent-switch`, `fluent-checkbox`, `fluent-textarea`, `fluent-tablist`/`fluent-tab`.
- Scripts: `scratchpad/pwt/t.mjs`, `t2.mjs`.

| Probe | Result |
|---|---|
| `GetByRole("button", { name: "Save changes" })` | **0 matches** |
| `GetByRole("switch")`, `GetByRole("checkbox")` | **0 matches** |
| Chromium's own AX tree (CDP) for the same page | has `button:"Save changes"`, `switch`, `checkbox:"Pick"` |
| `GetByRole("tab", { name: "Rules" })` | 1 match (works) |
| `GetByRole("textbox")` | 3 (the shadow `<input>`s) |
| `GetByRole("textbox", { name: "Entity name" })` | **0**: inner input has no accessible name (see §1.4) |
| `GetByLabel("Entity name")` / `GetByLabel("Search records")` | 1 match each: resolves to the **host** element |
| `.FillAsync()` on host (`#name`) or on `GetByLabel(...)` | **throws** "Element is not an `<input>`, `<textarea>`, `<select>` or [contenteditable] and does not have a role allowing [aria-readonly]" |
| `Locator("#name input").FillAsync(...)` (CSS pierces open shadow DOM) | works; host `.value` updates |
| `Locator("#ta textarea").FillAsync(...)` | works |
| click host + `Keyboard.TypeAsync` | works |
| Enter in text input | submits form (implicit submit) |
| Enter in textarea | does not submit |
| `GetByText("Save changes")` | 1 match (works) |
| `ariaSnapshot()` | shows textboxes but **no button/switch/checkbox** roles |

Why: the Fluent web components v3 set roles and states through **`ElementInternals`** (`elementInternals.role="button"`, `"switch"`, `"checkbox"`, …, plus `ariaChecked`/`ariaExpanded`/`ariaDisabled`) [V: bundle grep]. Playwright's role engine reads DOM attributes and cannot see internals.
- Upstream report: https://github.com/microsoft/playwright/issues/37806, "getByRole() Does Not Acknowledge Form-Associated Web Components". Playwright 1.56.0, closed "not planned", P3-collecting-feedback [V].
- Playwright docs: locators pierce open shadow DOM except XPath and closed roots [V: https://playwright.dev/dotnet/docs/locators]. `FillAsync` accepts only input/textarea/select/contenteditable, or retargets through `<label>` [V: https://playwright.dev/dotnet/docs/api/class-locator].
- The Fluent UI Blazor repo's own Playwright integration tests use **CSS locators only** (`Locator("[slot='trigger']")`, `fluent-tab[aria-selected='true']`, ids) and no `GetByRole` [V: `tests/Integration/Components/**`].
- `FluentButton` adds `role="button"` as an attribute **only when `Title` is set** [V: `src/Core/Components/Button/FluentButton.razor`]. With a `Title`, role locators would work, but `Title` also renders a native tooltip.

**Consequence for `scripts/test-admin-e2e`:**
- Role/label-first locators for buttons, switches and checkboxes will break.
- Plan on stable `id`/`data-testid` attributes (or `Locator("fluent-button", new() { HasText = "Save" })`) plus `Locator("… input")` / `Locator("… textarea")` for fills.
- An alternative is a test helper that maps "role + name" onto the CDP accessibility tree [U: not built].
- This also defeats the "a11y-by-construction through role locators" argument, so an axe/AX-tree check is worth adding. (Inference.)

Other gotchas:
- On macOS Chromium, `fluent-text-input`'s `delegatesFocus: true` was reported to hide the caret and break click-to-place. The issue was closed "Can't Repro" [V: https://github.com/microsoft/fluentui/issues/35942]. It would matter for headed local demos, not headless CI [U].
- Earlier v4 reports showed the label wired to the host rather than the inner input (WCAG H44). v5 RC5 fixes this *interactively* via `FluentField` [V: https://github.com/microsoft/fluentui-blazor/issues/3259 ; https://dvoituron.com/2026/08/07/fluentui-blazor-5-rc5/ ; `FluentField.razor.cs`].

### 4.2 bUnit
- The library's own unit tests use **bUnit 2.9.0** [V: repo `Directory.Packages.props:28`, `tests/Core/Components.Tests.csproj`], with `JSInterop.Mode = JSRuntimeMode.Loose` and `Services.AddFluentUIComponents()` [V: `tests/Core/Components/Toast/NotificationServiceToastTests.cs`; `docs/unit-tests.md`]. They snapshot markup through their own `Verify` extension (`FluentUITestContext`).
- Alvo's stack is xUnit v3 + Verify.XunitV3 32.0.0 [V: Alvo `Directory.Packages.props:34`]. bUnit 2.x on xUnit v3 is [U]. Check bunit.dev before adding it; per `alvo-dotnet-conventions`, that is a new-library decision.
- bUnit renders only the Blazor markup (custom-element tags and attributes). Web-component behaviour (shadow DOM, focus, ElementInternals) is not exercised, so behavioural checks stay in Playwright.

---

## 5. Costs and risks

### 5.1 Asset size [V: measured from the RC5 nupkg; gzip -9 / brotli locally]

| Asset | raw | gzip | brotli |
|---|---|---|---|
| `…lib.module.js` (v5 RC5, includes web components v3) | 441,556 B | 105,186 B | 85,028 B |
| `…bundle.scp.css` (v5 RC5) | 170,444 B | 19,463 B | 13,253 B |
| same, v4.14.4, for comparison | 395 KB / 104 KB | 90 KB / 14 KB | 75 KB / 12 KB |
| on-demand modules, e.g. `FluentDataGrid.razor.js` | 43.8 KB | – | – |
| `default-fuib.css` (fetched at start-up) | 3.9 KB | – | – |

- The cost is ~100 KB of compressed JS and ~15-20 KB of compressed CSS on first load. Alvo's own `alvo.css` + `alvo.js` + `admin.js` is ~102 KB raw today [V].
- Server side: the core DLL is 1.6 MB.
- The **Icons package is ~23 MB of DLLs**: Regular 10.7 MB, Filled 9.0 MB, Color 3.6 MB [V: unpacked RC5 icons nupkg].
  - Under interactive server these DLLs never reach the browser; icons render as inline SVG.
  - They do grow the Docker image and every embedded host's publish output, unless it is trimmed [V: `FluentIcon.md` mentions `PublishTrimmed`].
  - Alternative: skip the Icons package and keep Alvo's own `Icons.cs` SVG subclasses (`FluentIcon` accepts custom `Icon` subclasses [V: `FluentIcon.md`]).

### 5.2 Render performance, FluentDataGrid at ~1000 rows
- Not measured [U]. Evidence from issues, all on v4:
  - https://github.com/microsoft/fluentui-blazor/issues/3233 (OnRowClick gets slower with more rows; closed "not planned").
  - https://github.com/microsoft/fluentui-blazor/issues/3253 (SelectColumn click handlers 155 ms on 36 rows × 4 columns, >600 ms on real data; v4.11.3; closed "not planned").
  - https://github.com/microsoft/fluentui-blazor/issues/1621 (high CPU from uncached `Expression.Compile()`).
- Every cell is a component and every row click re-renders under interactive server. The mitigation is to **always page, or virtualize, with `ItemsProvider`** so only one visible window is rendered.
  - With `Virtualize`, use `DisplayMode.Table` and a fixed `ItemSize` [V: DataGrid doc].
  - Alvo already pages server-side (keyset), so an `ItemsProvider` mapping onto `IAlvoData` paging is the natural fit (inference).
- Budget a measurement: 1000 rows, 10 columns, row click → drawer.

### 5.3 .NET 10 known issues
- RC5 targets net10.0 against ASP.NET Core 10.0.10 [V]. No open .NET-10-specific blocker was found [U: GitHub issue search was limited].
- v4.13.2 added .NET 10 template support [V: releases page].
- Watch the `Components.Web` transitive dependency against Alvo's FrameworkReference/NU1510 rule (§1.1).

### 5.4 SSR and prerender quirks
- Static-SSR inputs lack an accessible name (§1.4). SSR forms need `Name` (§1.4).
- The default body style sets `overflow:hidden` (§1.3).
- There are two theme stores: `alvo.theme` and `fluentui-blazor:theme-settings` (§2.2).
- Toasts, dialogs, splitter and KeyCode are interactive-only.
- `FluentLayout` hamburger `NavigationDeferredLoading` is interactive-only. Without it, the nav is rendered **twice** in the DOM [V: `FluentLayout.md`]. That also doubles any e2e locator match on nav items.

### 5.5 CSP
Alvo ships no CSP today [V: no `Content-Security-Policy` in `src/`]. If it adds one:
- `script-src 'self'` is fine: there is no `eval` or `new Function` in the bundle [V: grep = 0], and the JS initializer loads from `_content/` (same origin).
- `style-src` needs care, because Blazor components render inline `style="…"` attributes (`Style`/`StyleValue` on most components, e.g. `FluentDialog`, `FluentTextInput`) [V: `.razor` sources]. Those need `'unsafe-inline'` (or `'unsafe-hashes'` + hashes, which is impractical for dynamic values).
- Constructed/adopted stylesheets (the theme tokens, default styles) and CSSOM `style.setProperty` are **not** governed by `style-src` inline rules (inference from the CSSOM/CSP model [U: not tested under a real CSP]).
- The bundle also creates `<style>` elements in three places [V: bundle]:
  - FAST's fallback when `adoptedStyleSheets` is unsupported
  - the anchor-positioning style element for popovers/tooltips
  - `applyShadowStyle`, which injects `<style data-fluent-shadow-style>` into shadow roots

  All three need `'unsafe-inline'` or a nonce, and the library exposes no nonce hook [U: none found].
- `connect-src 'self'` is needed for the `fetch()` of `default-fuib.css`, plus the SignalR websocket.
- Our inline dark-flash `<style>` / pre-paint `<script>` would need a nonce.

---

## 6. Recommended interaction-pattern baseline (Fluent 2 guidance)

Each rule cites fluent2.microsoft.design (React usage pages; the guidance is platform-neutral) and, where it exists, the matching v5 Blazor doc.

1. **Where editing happens.**
   - **Record and entity editors go in an overlay drawer** at the end edge (`ShowDrawerAsync`, `Alignment=End`), sized medium or large, with `FluentDialogBody FixedHeaderFooter="true"` so Save and Cancel stay visible.
     - Fluent 2: overlay drawers draw attention and are modal by default. An *inline* drawer is for viewing main and drawer content side by side. "Full-width drawers obscure the content… Don't use them if it's helpful for people to reference the information on the main page". Multi-step flows in drawers: at most two or three steps [V: https://fluent2.microsoft.design/components/web/react/core/drawer/usage].
   - **Dialogs are for important interruptions only**: sign-in, confirming destructive or irreversible actions, deciding before continuing [V: https://fluent2.microsoft.design/components/web/react/core/dialog/usage ; Blazor `FluentDialog.md` "Modal dialogs should be used very sparingly"].
   - **Inline editing** covers single-value toggles and renames in place (switch, text input). Use it only where the value is atomic. (Inference; Fluent 2 has no explicit inline-edit page [U].)
   - **Never stack a dialog on a dialog** [V: Blazor `FluentDialog.md` "Don't"; RC3: one global overlay at a time].
2. **Destructive confirmations.**
   - Use an **alert-style modal**: modal, no light dismiss, dismissed "only by selecting one of the available buttons". It is recommended for "potential loss, like unsaved changes or confirming destructive actions".
   - Title is mandatory. Lead the body with the consequence and don't restate the title.
   - At most three buttons. Labels are specific verbs ("Delete", or "Delete entity" if ambiguous) [V: dialog usage page].
   - In Blazor: `ShowConfirmationAsync(...)`, or a `FluentDialogInstance` with `PreventDismissOnEscape` where the loss is severe. Keep Alvo's `ConfirmByName` (type-the-name) inside it for the highest-risk actions (rollback, delete entity).
   - Warn about unsaved data when a drawer with edits is closed [V: drawer usage page, "Alert users about potential data loss when closing drawers containing unsaved information"].
3. **Toasts vs message bars vs field errors.**
   - **Toast:** transient, non-critical feedback on an action just taken ("Saved", "Applied revision 12"). Timed dismissal (7 s, pauses on hover). One consistent corner (bottom-end default; Fluent 2 suggests top-right or bottom-right). At most four visible. No Close button unless the info can be found elsewhere, so Alvo's History page qualifies for "Applied/rolled back" toasts. Don't use toasts for necessary actions.
     - Toasts without actions get no keyboard focus, so never put the only copy of important information in a toast [V: https://fluent2.microsoft.design/components/web/react/core/toast/usage ; Blazor `FluentToast.md`].
   - **Message bar:** persistent state of a page, drawer, dialog, tab or form, e.g. "descriptor has unapplied changes" or "engine is read-only".
     - Stack by severity: Error → Warning → Success → Info.
     - Placement: page-level below the command bar; drawer-level below the header; form-level at the top of the form, **with focus moved there on submission errors**.
     - Error and warning bars **must** include a button or link to resolve the issue. They are closable, but reappear if unresolved [V: https://fluent2.microsoft.design/components/web/react/core/messagebar/usage]. This fits Alvo's structured errors with fix suggestions: render the fix as the bar's action.
   - **Field-level validation:** brief `FluentField` messages under the field, no trailing period. Helper text explains accepted formats. `required` shows an asterisk; if every field is required, say so once instead [V: https://fluent2.microsoft.design/components/web/react/core/field/usage].
   - **Dialog:** only when the user must act before continuing (toast page: "For critical messages, try a modal Dialog, Field error, or Message bar instead").
4. **Keyboard in forms** (Fluent 2 is silent on this [V: field usage page has no Enter guidance], so these are platform conventions implemented with v5 primitives):
   - **Enter submits single-line forms.** Wrap in `EditForm` with a `type="submit"` `FluentButton`. `fluent-text-input` performs implicit submit [V: bundle + probe].
   - **Enter inserts a newline in multi-line fields**, and **Ctrl+Enter / Cmd+Enter submits**: `FluentTextArea ChangeAfterKeyPress="[KeyPress.For(KeyCode.Enter).AndCtrlKey(), KeyPress.For(KeyCode.Enter).AndMetaKey()]"` [V: API; newline suppression U]. This covers the assistant chat input.
   - **Esc closes** the topmost overlay (dialog/drawer default), except alert-style confirmations.
   - Dialog footer shortcuts (Enter/Esc) only fire when focus is not in a field [V: `ShouldHandleShortcut`], so a drawer form never submits on a stray Enter. The form's own submit does that deliberately.
   - Cmd-based dialog shortcuts are not expressible via `ShortCut` [V], so use `FluentKeyCode` for those.
   - **Focus:** on open, focus goes to the first interactive element. Modal and alert dialogs trap focus. On close, focus returns to the trigger [V: dialog usage page]. Alvo's `FocusReturn.cs` stays as a belt-and-braces guarantee until focus return is verified.
5. **Empty, loading and error states.**
   - **Loading:** show nothing under 1 s. Use a spinner at 1-3 s, then switch to a progress bar or a reassuring status string ("Working on it…"). Keep the user in the same view [V: https://fluent2.microsoft.design/wait-ux].
     - Use **skeletons** for content that takes more than 1 s and whose structure is known (grid rows, cards). Don't use them for long processes. Set `aria-busy="true"` on the container and use a live region for large blocks [V: https://fluent2.microsoft.design/components/web/react/core/skeleton/usage ; Blazor `FluentSkeleton.md`].
     - For the AI assistant, show response indicators **immediately** rather than after the 1 s threshold [V: wait-ux page].
   - **Empty:** a short explanation of what the user can accomplish, plus one CTA whose label completes "I want to…". Active voice; non-judgmental tone ("Need help?" not "Stuck?") [V: https://fluent2.microsoft.design/onboarding]. For grids, use `FluentDataGrid.EmptyContent`.
   - **Error:** a message bar at the surface level with a resolving action (above). Grid load failures go to `ErrorContent`/`HandleLoadingError` [V: DataGrid params]. Unexpected render failures go to `FluentErrorBoundary` [V: XML].

---

## 7. Open items to spike before committing
1. `dotnet build` the Admin project with RC5 under Alvo's `TreatWarningsAsErrors` + NU1510 rules. Also do a Release `docker build`, per Alvo's "rings are Debug, CI is Release" lesson.
2. Theme wiring: `alvo.theme` → `body[data-theme]` + `html[data-theme]`; brand via `SetThemeAsync("#0f7a48", true)` vs manual `:root` mapping; AA check of the generated ramp.
3. `FluentDataGrid` with `ItemsProvider` over `IAlvoData` keyset paging, 1000 rows: measure row-click → drawer latency under interactive server.
4. The e2e locator strategy (§4.1): add `data-testid` conventions before porting `scripts/test-admin-e2e`.
5. Sign-in page: native inputs vs `FluentTextInput` under static SSR (a11y name, `Name`, antiforgery).
6. Replacing the resizable pane: `FluentMultiSplitter` is "not accessible yet", so keep Alvo's own `SplitHandle` or accept the regression.
7. Replacing `RefPicker`: `FluentAutocomplete` has "accessibility requirements not yet implemented", so `FluentCombobox` may be the better base.
