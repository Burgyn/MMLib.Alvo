# The design gallery

`gallery.html` renders the admin design system — tokens, every control, the two
classes of "not yet", and the live screens as full compositions — in both themes
and at 375 px.

**It is a proof, not a source.** It references
`src/MMLib.Alvo.Admin/wwwroot/alvo.css` and `alvo.js` by relative path and never
copies them, so a token that regresses regresses here at the same moment. It
declares no colour of its own; `GalleryTests` refuses one, for the same reason
`ComponentLayerTests` refuses one in the stylesheet — a colour this page invents
is a colour no contrast fact measures.

The usual failure mode for a design gallery is that it carries its own
stylesheet, stays beautiful while the product changes underneath it, and becomes
evidence for a claim that stopped being true. This one cannot do that.

## Opening it

```bash
open docs/design/gallery.html
```

The theme and density buttons in the top bar drive the same `window.alvo`
functions a host gets from `AlvoAdminAssets.Script`. Narrow the window past
720 px to see the grid become cards and the side navigation become the bottom
bar.

## What it is not

It is not the dashboard. The Blazor components, the routing and the binding to
`IAlvoManagement` are the second half of #227, behind #248 (identity) and #146
(`access` enforcement) — see
[the design](../superpowers/specs/2026-09-18-f5-admin-dashboard-design.md) §1.3.
The screens here are compositions of real components over illustrative data, so
what they prove is the system, not the wiring.
