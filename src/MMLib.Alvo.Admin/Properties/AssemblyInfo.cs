using System.Runtime.CompilerServices;

// The surface a host is meant to use is seven things: AlvoAdmin, AlvoAdminAssets,
// AlvoAdminOptions, AlvoAdminClaims, the one port a host fills (IAlvoAdminCallerResolver), and the
// two extension methods that register and map the dashboard. Everything a screen is built *from* —
// the navigation table, the management and data gateways, the working copy — is internal, because
// none of it is a thing a consumer calls.
//
// The screens themselves are a different case, and the approval baseline states it plainly rather
// than letting the sentence above imply otherwise: the Razor SDK emits every component class as
// public, and there is no per-component accessibility to set — a `.razor` file cannot declare one,
// and a partial declaration that tried would conflict with the generated one. So some sixty
// screen, shell and design-system component types appear in PublicApi.MMLib.Alvo.Admin.verified.txt, and
// they are public because of how Blazor compiles, not because anybody decided they were contract.
// That is the same position every shipped Razor Class Library is in, Microsoft's own included.
//
// What the repository's "public is the contract" rule buys here is therefore the baseline itself:
// a component type appearing or disappearing shows up in the diff, so the question *is* asked on
// every PR even though the answer for a screen is always "Blazor did that". What must never appear
// in that file is a type this package chose to publish — a gateway, an option bag, a helper — and
// that is what a reviewer is looking for when the file grows.
//
// The policy this earns (docs/architecture/admin-dashboard-review.md, F-10): the seven types above
// are the contract, and `Components.*` is implementation that may change in any minor version —
// renamed, split, or removed without that being a breaking change. `_Imports.razor` carries
// `[EditorBrowsable(Never)]` for the whole namespace so a consumer's IntelliSense reflects that,
// and it is stated here (there is no package README to carry it instead) and in `AlvoAdmin`'s own
// remarks, so it is findable from either the assembly-level or the type-level side.
//
// The folders are features, not layers (§0.9; review F-25): `Components/<Feature>/` holds one nav
// section's screens together with the internal helpers that feature owns — `Schema` owns the
// working copy even though the pending bar reads it, `Data` the grid and the record form — and a
// feature's namespace follows its folder. `Components/Shell` is the frame around every screen,
// `Components/DesignSystem` the primitives, and `Internal/` keeps only what no one feature owns: the
// gateways over the Abstractions ports, the session, the paths, the descriptor lens, the problem
// classifier and the interop. A deliberate deviation from
// docs/architecture/vertical-slice.md's `<Feature>/Internal/`: that split exists to keep a feature's
// internals apart from its public contract, and here there is no such contract to keep them apart
// from — every component is public only because Blazor compiles it so, and every helper is already
// `internal` — so a nested `Internal` per feature would be a second folder with nothing to guard.
//
// Its own suite is the only assembly that can see the internals. The same forgeability caveat as
// every other InternalsVisibleTo in the family applies: the assemblies are unsigned, so this grants
// access by name alone.
[assembly: InternalsVisibleTo("MMLib.Alvo.Admin.Tests")]
