# Admin dashboard — open items

What the dashboard still owes, kept here rather than in a chat scrollback. Each item says what
is missing, why it matters, and what the answer probably is — so whoever picks it up starts from
the decision rather than from the symptom.

Raised by the maintainer while driving the real dashboard from a phone, 23 Sep 2026.

## 1. Indexes cannot be created or managed

`field.indexed` and `unique` are honoured by the apply, and the Fields tab *shows* them as badges,
but there is no control that adds or removes one, and nothing at all for a composite index — which
the descriptor carries as an entity-level `indexes` block. So the one thing an operator reaches for
after watching a list get slow is the one thing they have to leave the dashboard for.

Probably: a section on the entity's Fields tab beside "Declared fields", listing the declared
indexes with their fields and uniqueness, and an editor that writes the `indexes` block into the
working copy like every other edit — no new write path, the same preview and apply.

## 2. The AI panel never appears

Reported from the phone: Settings shows the connection panel, but no assistant launcher renders
anywhere. Expected when no connection resolves (§3.3 asks for exactly that), so the first question
is whether the connection actually saved — `GET {m}/info` reports `ai.configured` and `ai.source`,
and the panel now writes through `PUT {m}/ai/connection`.

Needs a reproduction on a host with a mounted `Alvo:Secrets:EncryptionKeyFile` and a saved
connection, checking `info` before blaming the drawer. If `configured` is true and the launcher is
still absent, the fault is in `AdminLayout`'s one-shot `IsConfiguredAsync` — it runs once per
circuit, so a connection saved *during* a session does not light the launcher until a reload.
That last part is almost certainly the real answer, and it is a bug: saving a connection should
make the assistant appear without a reload.

## 3. `on write` hooks are not editable from the dashboard

The entity page has an "On write" tab that renders the hook points and says which this build
refuses. Reading them is not editing them. Whether they *should* be editable here depends on what
the build honours — several hook points are in `UnhonouredFeatures`, and an editor for a facet the
apply refuses is the control this dashboard already argues against elsewhere.

So: first the audit in item 5, then an editor for exactly the hook points that are honoured, with
the refused ones staying as the read-only explanation they are now.

## 4. No link to the OpenAPI documentation

The host serves a generated OpenAPI document and a docs UI, and the dashboard never points at it.
An operator who has just changed a schema is one click from the contract that changed, and that
click does not exist.

Probably: a link on the entity page's API tab and one in Settings, pointing at the host's docs
route — with the caveat that an embedded host may mount it elsewhere or not at all, so the link
appears only when the route is actually mapped.

## 5. Audit: what Alvo can do that the dashboard cannot

The dashboard was built screen by screen against the F5 design, not against the product's full
surface, so the gaps are wherever nobody looked. Items 1, 3 and 4 were all found by one person
using it for an hour, which suggests there are more.

The audit is mechanical: walk `IAlvoManagement`, the descriptor schema's top-level blocks and
`UnhonouredFeatures`, and for each ask — can an operator reach this from the dashboard, is it
read-only when it should be editable, and is its absence explained or silent? Output is a table in
this file, not prose.

## 6. Crowded toolbars on a phone

With more than two controls a header row becomes a wall of equal-weight buttons. Wanted: the
ordinary toolbar behaviour — keep the primary action and at most one other, and move the rest
behind an overflow `…` menu.

This is a design-system item rather than a screen one: `PageHeader`'s `Actions` slot should take
a primary action and a collection of secondary ones, and decide at the breakpoint which are shown
and which fold into the menu. The sections sheet already has the sheet pattern the overflow menu
would reuse.
